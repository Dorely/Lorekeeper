using System.Text.Json;
using Lorekeeper.Context;
using Lorekeeper.Chapters;
using Lorekeeper.Models;
using Lorekeeper.Persistence.Repositories;

namespace Lorekeeper.Outline;

public sealed class OutlineToolStagingContext(
    Guid projectId,
    Guid conversationId,
    IAiChangeRepository changes,
    IProjectRepository projects,
    IActService acts,
    IChapterService chapters,
    IEntityService entities,
    IEntityTypeService entityTypes,
    IEntityRelationContextService entityRelations)
{
    private const string _eventNodeType = "Event";

    private static readonly EntityRelationContextOptions EntityRelationOptions = new()
    {
        Depth = 2,
        MaxDirectLinks = 8,
        MaxTraversalPaths = 10,
        MaxLinksPerNode = 8,
    };

    private readonly Dictionary<Guid, ActState> _acts = [];
    private readonly Dictionary<Guid, ChapterState> _chapters = [];
    private readonly Dictionary<Guid, EntityState> _entities = [];
    private readonly Dictionary<string, EntityTypeDefinition> _entityTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Guid> _createdResourceProducers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AiChange> _newChanges = [];

    private AiChangeBatch? _batch;
    private bool _loaded;
    private int _nextOrder;
    private Guid? _currentAssistantMessageId;
    private string _currentToolCallId = string.Empty;
    private string _currentToolName = string.Empty;
    private string _currentArgumentsJson = "{}";

    public Guid ProjectId { get; } = projectId;
    public Guid ConversationId { get; } = conversationId;

    public void BeginToolCall(Guid assistantMessageId, string toolCallId, string toolName, string argumentsJson)
    {
        _currentAssistantMessageId = assistantMessageId;
        _currentToolCallId = toolCallId;
        _currentToolName = toolName;
        _currentArgumentsJson = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson;
    }

    public IReadOnlyList<AiChange> DrainNewChanges()
    {
        var result = _newChanges.ToList();
        _newChanges.Clear();
        return result;
    }

    public async Task<string> ListOutlineAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);

        object ProjectChapter(ChapterState chapter) => new
        {
            id = chapter.Id,
            order = chapter.Order,
            title = chapter.Title,
            synopsis = chapter.Synopsis,
            beatCount = _entities.Values.Count(entity =>
                !entity.Deleted
                && string.Equals(entity.Type, _eventNodeType, StringComparison.OrdinalIgnoreCase)
                && entity.ParentId == chapter.Id),
        };

        var activeChapters = _chapters.Values.Where(chapter => !chapter.Deleted).ToList();
        var byAct = activeChapters
            .Where(chapter => chapter.ActId is not null)
            .GroupBy(chapter => chapter.ActId!.Value)
            .ToDictionary(group => group.Key, group => group.OrderBy(chapter => chapter.Order).ToList());
        var unassigned = activeChapters.Where(chapter => chapter.ActId is null).OrderBy(chapter => chapter.Order).ToList();

        var payload = new
        {
            projectFacts = _entities.Values
                .Where(entity =>
                    !entity.Deleted
                    && string.Equals(entity.Type, EntityTypeService.ProjectFactNodeType, StringComparison.OrdinalIgnoreCase))
                .OrderBy(entity => ReadProperty(entity.Properties, "key")?.StartsWith("outline.", StringComparison.OrdinalIgnoreCase) == true ? 0 : 1)
                .ThenBy(entity => ReadProperty(entity.Properties, "key") ?? entity.Name, StringComparer.OrdinalIgnoreCase)
                .Select(entity => new
                {
                    id = entity.Id,
                    key = ReadProperty(entity.Properties, "key") ?? entity.Name,
                    name = entity.Name,
                    value = ReadProperty(entity.Properties, "value") ?? string.Empty,
                    linkedEntities = Array.Empty<object>(),
                }),
            acts = _acts.Values
                .Where(act => !act.Deleted)
                .OrderBy(act => act.Order)
                .Select(act => new
                {
                    id = act.Id,
                    order = act.Order,
                    title = act.Title,
                    synopsis = act.Synopsis,
                    chapters = (byAct.TryGetValue(act.Id, out var list) ? list : []).Select(ProjectChapter),
                }),
            unassigned = unassigned.Select(ProjectChapter),
        };
        return Serialize(payload);
    }

    public async Task<string> ListEntityTypesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        return Serialize(_entityTypes.Values
            .OrderBy(type => type.SortOrder)
            .ThenBy(type => type.Type, StringComparer.OrdinalIgnoreCase)
            .Select(type => new
            {
                type = type.Type,
                singular = type.SingularLabel,
                plural = type.PluralLabel,
                isStructural = type.IsStructural,
                isChapterScoped = type.IsChapterScoped,
                defaultProperties = type.DefaultProperties,
            }));
    }

    public async Task<string> ListEntitiesAsync(string type, Guid? parentId, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        var query = _entities.Values
            .Where(entity => !entity.Deleted && string.Equals(entity.Type, type, StringComparison.OrdinalIgnoreCase) && entity.ParentId == parentId);
        query = parentId is null
            ? query.OrderBy(entity => entity.Name, StringComparer.OrdinalIgnoreCase)
            : query.OrderBy(entity => entity.Order ?? int.MaxValue).ThenBy(entity => entity.Name, StringComparer.OrdinalIgnoreCase);

        var payload = new List<object>();
        foreach (var entity in query)
            payload.Add(await EntityPayloadAsync(entity, cancellationToken));

        return Serialize(payload);
    }

    public async Task<string> CreateActAsync(string title, string? synopsis, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(title)) return "Error: title is required.";

        var nextOrder = _acts.Values.Where(act => !act.Deleted).Select(act => act.Order).DefaultIfEmpty(-1).Max() + 1;
        var act = new ActState(Guid.NewGuid(), nextOrder, title.Trim(), synopsis?.Trim() ?? string.Empty, Deleted: false);
        _acts[act.Id] = act;

        var after = act.ToChange();
        var result = Serialize(new { id = act.Id, order = act.Order, title = act.Title, synopsis = act.Synopsis });
        await StageChangeAsync(
            summary: $"Create act '{act.Title}'",
            before: null,
            after: after,
            resultJson: result,
            resourceKind: "Act",
            resourceId: Resource("Act", act.Id),
            createdResources: [Resource("Act", act.Id)],
            referencedResources: [],
            cancellationToken);
        return result;
    }

    public async Task<string> UpdateActAsync(Guid actId, string? title, string? synopsis, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        if (!TryGetAct(actId, out var act)) return $"Error: act {actId} not found in this project.";

        var before = act.ToChange();
        if (title is not null) act.Title = title.Trim();
        if (synopsis is not null) act.Synopsis = synopsis.Trim();
        var after = act.ToChange();
        var result = Serialize(new { id = act.Id, title = act.Title, synopsis = act.Synopsis });
        await StageChangeAsync(
            summary: $"Update act '{act.Title}'",
            before: before,
            after: after,
            resultJson: result,
            resourceKind: "Act",
            resourceId: Resource("Act", act.Id),
            createdResources: [],
            referencedResources: [Resource("Act", act.Id)],
            cancellationToken);
        return result;
    }

    public async Task<string> DeleteActAsync(Guid actId, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        if (!TryGetAct(actId, out var act)) return $"Error: act {actId} not found in this project.";

        var before = act.ToChange();
        act.Deleted = true;
        foreach (var chapter in _chapters.Values.Where(chapter => !chapter.Deleted && chapter.ActId == actId))
        {
            chapter.ActId = null;
            chapter.Order = NextChapterOrder(null);
        }

        var result = $"Deleted act {actId}. Owned chapters were moved to the unassigned bucket.";
        await StageChangeAsync(
            summary: $"Delete act '{before.Title}'",
            before: before,
            after: null,
            resultJson: result,
            resourceKind: "Act",
            resourceId: Resource("Act", act.Id),
            createdResources: [],
            referencedResources: [Resource("Act", act.Id)],
            cancellationToken);
        return result;
    }

    public async Task<string> CreateChapterAsync(Guid? actId, string title, string? synopsis, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(title)) return "Error: title is required.";
        if (actId is not null && !TryGetAct(actId.Value, out _)) return $"Error: act {actId} not found in this project.";

        var chapter = new ChapterState(Guid.NewGuid(), actId, NextChapterOrder(actId), title.Trim(), synopsis?.Trim() ?? string.Empty, Deleted: false);
        _chapters[chapter.Id] = chapter;

        var after = chapter.ToChange();
        var result = Serialize(new { id = chapter.Id, order = chapter.Order, actId = chapter.ActId, title = chapter.Title, synopsis = chapter.Synopsis });
        await StageChangeAsync(
            summary: $"Create chapter '{chapter.Title}'",
            before: null,
            after: after,
            resultJson: result,
            resourceKind: "Chapter",
            resourceId: Resource("Chapter", chapter.Id),
            createdResources: [Resource("Chapter", chapter.Id)],
            referencedResources: actId is null ? [] : [Resource("Act", actId.Value)],
            cancellationToken);
        return result;
    }

    public async Task<string> UpdateChapterAsync(Guid chapterId, string? title, string? synopsis, Guid? newActId, bool moveChapter, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        if (!TryGetChapter(chapterId, out var chapter)) return $"Error: chapter {chapterId} not found in this project.";
        if (moveChapter && newActId is not null && !TryGetAct(newActId.Value, out _)) return $"Error: act {newActId} not found in this project.";

        var before = chapter.ToChange();
        if (title is not null) chapter.Title = title.Trim();
        if (synopsis is not null) chapter.Synopsis = synopsis.Trim();
        if (moveChapter && chapter.ActId != newActId)
        {
            chapter.ActId = newActId;
            chapter.Order = NextChapterOrder(newActId);
        }

        var after = chapter.ToChange();
        var references = new List<string> { Resource("Chapter", chapter.Id) };
        if (newActId is not null) references.Add(Resource("Act", newActId.Value));
        var result = Serialize(new { id = chapter.Id, actId = chapter.ActId, order = chapter.Order, title = chapter.Title, synopsis = chapter.Synopsis });
        await StageChangeAsync(
            summary: $"Update chapter '{chapter.Title}'",
            before: before,
            after: after,
            resultJson: result,
            resourceKind: "Chapter",
            resourceId: Resource("Chapter", chapter.Id),
            createdResources: [],
            referencedResources: references,
            cancellationToken);
        return result;
    }

    public async Task<string> DeleteChapterAsync(Guid chapterId, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        if (!TryGetChapter(chapterId, out var chapter)) return $"Error: chapter {chapterId} not found in this project.";

        var before = chapter.ToChange();
        chapter.Deleted = true;
        foreach (var entity in _entities.Values.Where(entity => entity.ParentId == chapterId))
            entity.Deleted = true;

        var result = $"Deleted chapter {chapterId}.";
        await StageChangeAsync(
            summary: $"Delete chapter '{before.Title}'",
            before: before,
            after: null,
            resultJson: result,
            resourceKind: "Chapter",
            resourceId: Resource("Chapter", chapter.Id),
            createdResources: [],
            referencedResources: [Resource("Chapter", chapter.Id)],
            cancellationToken);
        return result;
    }

    public async Task<string> ReorderActsAsync(IReadOnlyList<Guid> orderedIds, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        if (orderedIds.Count == 0) return "Error: orderedIds is required.";

        var activeActs = _acts.Values.Where(act => !act.Deleted).OrderBy(act => act.Order).ToList();
        var activeIds = activeActs.Select(act => act.Id).ToHashSet();
        var unknown = orderedIds.Where(orderedId => !activeIds.Contains(orderedId)).ToList();
        if (unknown.Count > 0) return $"Error: unknown act ids: {string.Join(", ", unknown)}";

        var before = new OutlineReorderChange(null, activeActs.Select(act => act.Id).ToList());
        var final = orderedIds.ToList();
        foreach (var act in activeActs)
            if (!final.Contains(act.Id)) final.Add(act.Id);
        for (var order = 0; order < final.Count; order++)
            _acts[final[order]].Order = order;

        var after = new OutlineReorderChange(null, final);
        var result = $"Reordered {final.Count} acts.";
        await StageChangeAsync(
            summary: "Reorder acts",
            before: before,
            after: after,
            resultJson: result,
            resourceKind: "ActOrder",
            resourceId: "ActOrder",
            createdResources: [],
            referencedResources: final.Select(actId => Resource("Act", actId)).ToList(),
            cancellationToken);
        return result;
    }

    public async Task<string> ReorderChaptersAsync(Guid? actId, IReadOnlyList<Guid> orderedIds, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        if (orderedIds.Count == 0) return "Error: orderedIds is required.";
        if (actId is not null && !TryGetAct(actId.Value, out _)) return $"Error: act {actId} not found in this project.";

        var bucketChapters = _chapters.Values.Where(chapter => !chapter.Deleted && chapter.ActId == actId).OrderBy(chapter => chapter.Order).ToList();
        var bucketIds = bucketChapters.Select(chapter => chapter.Id).ToHashSet();
        var unknown = orderedIds.Where(orderedId => !bucketIds.Contains(orderedId)).ToList();
        if (unknown.Count > 0) return $"Error: chapter ids not in target bucket: {string.Join(", ", unknown)}";

        var before = new OutlineReorderChange(actId, bucketChapters.Select(chapter => chapter.Id).ToList());
        var final = orderedIds.ToList();
        foreach (var chapter in bucketChapters)
            if (!final.Contains(chapter.Id)) final.Add(chapter.Id);
        for (var order = 0; order < final.Count; order++)
            _chapters[final[order]].Order = order;

        var after = new OutlineReorderChange(actId, final);
        var result = $"Reordered {final.Count} chapters in bucket {(actId is null ? "unassigned" : actId.ToString())}.";
        var references = final.Select(chapterId => Resource("Chapter", chapterId)).ToList();
        if (actId is not null) references.Add(Resource("Act", actId.Value));
        await StageChangeAsync(
            summary: "Reorder chapters",
            before: before,
            after: after,
            resultJson: result,
            resourceKind: "ChapterOrder",
            resourceId: actId is null ? "ChapterOrder:unassigned" : Resource("ChapterOrder", actId.Value),
            createdResources: [],
            referencedResources: references,
            cancellationToken);
        return result;
    }

    public async Task<string> CreateEntityAsync(string type, string name, Dictionary<string, string?>? properties, Guid? parentId, int? order, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(type)) return "Error: type is required.";
        if (string.IsNullOrWhiteSpace(name)) return "Error: name is required.";

        var trimmedType = type.Trim();
        if (string.Equals(trimmedType, EntityTypeService.ProjectFactNodeType, StringComparison.OrdinalIgnoreCase))
        {
            parentId ??= ProjectId;
            properties ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            properties.TryAdd("key", name.Trim());
            properties.TryAdd("value", string.Empty);
        }
        if (parentId is not null && !CanResolveEntityOrChapter(parentId.Value)) return $"Error: parent entity {parentId} not found in this project.";

        var duplicate = FindDuplicateForCreate(trimmedType, name, properties, parentId);
        if (duplicate is not null)
            return await DuplicateEntityResultAsync(trimmedType, duplicate, cancellationToken);

        EnsureType(trimmedType, parentId is not null);
        var resolvedOrder = parentId is null ? order : order ?? NextEntityOrder(trimmedType, parentId.Value);
        var entity = new EntityState(
            Guid.NewGuid(),
            trimmedType,
            name.Trim(),
            resolvedOrder,
            parentId,
            new Dictionary<string, string?>(properties ?? [], StringComparer.OrdinalIgnoreCase),
            Deleted: false);
        _entities[entity.Id] = entity;

        var after = entity.ToChange();
        var result = Serialize(await EntityPayloadAsync(entity, cancellationToken));
        var references = parentId is null ? [] : new List<string> { ResourceForExisting(parentId.Value) };
        await StageChangeAsync(
            summary: $"Create {entity.Type} '{entity.Name}'",
            before: null,
            after: after,
            resultJson: result,
            resourceKind: entity.Type,
            resourceId: Resource("Entity", entity.Id),
            createdResources: [Resource("Entity", entity.Id)],
            referencedResources: references,
            cancellationToken);
        return result;
    }

    public async Task<string> UpdateEntityAsync(Guid entityId, string? name, Dictionary<string, string?>? propertiesToSet, string[]? propertiesToRemove, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        if (!TryGetEntity(entityId, out var entity)) return $"Error: entity {entityId} not found in this project.";

        var before = entity.ToChange();
        if (!string.IsNullOrWhiteSpace(name)) entity.Name = name.Trim();
        if (propertiesToSet is not null)
        {
            foreach (var property in propertiesToSet)
                entity.Properties[property.Key] = property.Value;
        }
        if (propertiesToRemove is not null)
        {
            foreach (var propertyName in propertiesToRemove)
                entity.Properties.Remove(propertyName);
        }

        var after = entity.ToChange();
        var result = Serialize(await EntityPayloadAsync(entity, cancellationToken));
        await StageChangeAsync(
            summary: $"Update {entity.Type} '{entity.Name}'",
            before: before,
            after: after,
            resultJson: result,
            resourceKind: entity.Type,
            resourceId: Resource("Entity", entity.Id),
            createdResources: [],
            referencedResources: [Resource("Entity", entity.Id)],
            cancellationToken);
        return result;
    }

    public async Task<string> DeleteEntityAsync(Guid entityId, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        if (!TryGetEntity(entityId, out var entity)) return $"Error: entity {entityId} not found in this project.";

        var before = entity.ToChange();
        entity.Deleted = true;
        var result = $"Deleted entity {entityId}.";
        await StageChangeAsync(
            summary: $"Delete {entity.Type} '{entity.Name}'",
            before: before,
            after: null,
            resultJson: result,
            resourceKind: entity.Type,
            resourceId: Resource("Entity", entity.Id),
            createdResources: [],
            referencedResources: [Resource("Entity", entity.Id)],
            cancellationToken);
        return result;
    }

    public async Task<string> ReorderEntitiesAsync(string type, Guid parentId, IReadOnlyList<Guid> orderedIds, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(type)) return "Error: type is required.";
        if (!CanResolveEntityOrChapter(parentId)) return $"Error: parent entity {parentId} not found in this project.";
        if (orderedIds.Count == 0) return "Error: orderedIdsJson is required.";

        var trimmedType = type.Trim();
        var children = _entities.Values
            .Where(entity => !entity.Deleted && entity.ParentId == parentId && string.Equals(entity.Type, trimmedType, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entity => entity.Order ?? int.MaxValue)
            .ToList();
        var childIds = children.Select(entity => entity.Id).ToHashSet();
        if (!orderedIds.ToHashSet().SetEquals(childIds))
            return "Error: reorder list must contain exactly the parent's children of the given type.";

        var before = new OutlineEntityReorderChange(trimmedType, parentId, children.Select(entity => entity.Id).ToList());
        for (var orderIndex = 0; orderIndex < orderedIds.Count; orderIndex++)
            _entities[orderedIds[orderIndex]].Order = orderIndex;

        var after = new OutlineEntityReorderChange(trimmedType, parentId, orderedIds.ToList());
        var result = $"Reordered {orderedIds.Count} {trimmedType} entities under parent {parentId}.";
        var references = orderedIds.Select(entityId => Resource("Entity", entityId)).ToList();
        references.Add(ResourceForExisting(parentId));
        await StageChangeAsync(
            summary: $"Reorder {trimmedType} entities",
            before: before,
            after: after,
            resultJson: result,
            resourceKind: trimmedType,
            resourceId: Resource("EntityOrder", parentId),
            createdResources: [],
            referencedResources: references,
            cancellationToken);
        return result;
    }

    public async Task<string> LinkEntitiesAsync(Guid fromId, Guid toId, string edgeType, Dictionary<string, string?>? properties, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        if (fromId == toId) return "Error: cannot link an entity to itself.";
        if (string.IsNullOrWhiteSpace(edgeType)) return "Error: edgeType is required.";
        if (!CanResolveEntityOrChapter(fromId)) return $"Error: source entity {fromId} not found in this project.";
        if (!CanResolveEntityOrChapter(toId)) return $"Error: target entity {toId} not found in this project.";

        var link = new OutlineEntityLinkChange(
            fromId,
            toId,
            edgeType.Trim(),
            new Dictionary<string, string?>(properties ?? [], StringComparer.OrdinalIgnoreCase));
        var result = $"Linked {fromId} -[{edgeType.Trim()}]-> {toId}.";
        await StageChangeAsync(
            summary: $"Link {fromId} to {toId} as {edgeType.Trim()}",
            before: null,
            after: link,
            resultJson: result,
            resourceKind: "EntityLink",
            resourceId: $"EntityLink:{fromId:N}:{edgeType.Trim()}:{toId:N}",
            createdResources: [],
            referencedResources: [ResourceForExisting(fromId), ResourceForExisting(toId)],
            cancellationToken);
        return result;
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded) return;

        _ = await projects.GetByIdAsync(ProjectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {ProjectId} not found.");

        foreach (var act in await acts.ListAsync(ProjectId, cancellationToken))
            _acts[act.Id] = new ActState(act.Id, act.Order, act.Title, act.Synopsis, Deleted: false);

        foreach (var chapter in await chapters.ListAsync(ProjectId, cancellationToken))
            _chapters[chapter.Id] = new ChapterState(chapter.Id, chapter.ActId, chapter.Order, chapter.Title, chapter.Synopsis, Deleted: false);

        var typeList = await entityTypes.ListAsync(ProjectId, includeStructural: true, cancellationToken);
        foreach (var typeDefinition in typeList)
            _entityTypes[typeDefinition.Type] = typeDefinition;

        foreach (var typeDefinition in typeList.Where(typeDefinition => typeDefinition.Type is not "Project" and not "Act" and not "Chapter"))
        {
            if (typeDefinition.IsChapterScoped)
            {
                foreach (var chapter in _chapters.Values)
                    await LoadEntitiesAsync(typeDefinition.Type, chapter.Id, cancellationToken);
            }
            else
            {
                await LoadEntitiesAsync(typeDefinition.Type, parentId: null, cancellationToken);
            }
        }

        _loaded = true;
    }

    private async Task LoadEntitiesAsync(string type, Guid? parentId, CancellationToken cancellationToken)
    {
        var list = await entities.ListAsync(ProjectId, type, parentId, cancellationToken);
        foreach (var entity in list)
        {
            _entities[entity.Id] = new EntityState(
                entity.Id,
                entity.Type,
                entity.Name,
                entity.Order,
                entity.ParentId,
                new Dictionary<string, string?>(entity.Properties, StringComparer.OrdinalIgnoreCase),
                Deleted: false);
        }
    }

    private async Task StageChangeAsync(
        string summary,
        object? before,
        object? after,
        string resultJson,
        string resourceKind,
        string resourceId,
        IReadOnlyCollection<string> createdResources,
        IReadOnlyCollection<string> referencedResources,
        CancellationToken cancellationToken)
    {
        var batch = await EnsureBatchAsync(cancellationToken);
        var dependencies = referencedResources
            .Where(_createdResourceProducers.ContainsKey)
            .Select(resource => _createdResourceProducers[resource])
            .Distinct()
            .ToList();

        var change = new AiChange
        {
            BatchId = batch.Id,
            Order = _nextOrder++,
            ToolCallId = _currentToolCallId,
            ToolName = _currentToolName,
            ArgumentsJson = _currentArgumentsJson,
            Summary = summary,
            BeforeJson = Serialize(before),
            AfterJson = Serialize(after),
            ResultJson = resultJson,
            ResourceKind = resourceKind,
            ResourceId = resourceId,
            CreatedResourceIdsJson = Serialize(createdResources),
            ReferencedResourceIdsJson = Serialize(referencedResources),
            DependsOnChangeIdsJson = Serialize(dependencies),
        };

        await changes.AddChangeAsync(change, cancellationToken);
        await changes.SaveChangesAsync(cancellationToken);
        _newChanges.Add(change);

        foreach (var resource in createdResources)
            _createdResourceProducers[resource] = change.Id;
    }

    private async Task<AiChangeBatch> EnsureBatchAsync(CancellationToken cancellationToken)
    {
        if (_batch is not null) return _batch;

        _batch = new AiChangeBatch
        {
            ProjectId = ProjectId,
            ConversationKind = AiChangeConversationKind.Outline,
            ConversationId = ConversationId,
            AssistantMessageId = _currentAssistantMessageId,
        };
        await changes.AddBatchAsync(_batch, cancellationToken);
        await changes.SaveChangesAsync(cancellationToken);
        _nextOrder = 0;
        return _batch;
    }

    private bool TryGetAct(Guid id, out ActState act)
    {
        var found = _acts.TryGetValue(id, out var value) && !value.Deleted;
        act = value!;
        return found;
    }

    private bool TryGetChapter(Guid id, out ChapterState chapter)
    {
        var found = _chapters.TryGetValue(id, out var value) && !value.Deleted;
        chapter = value!;
        return found;
    }

    private bool TryGetEntity(Guid id, out EntityState entity)
    {
        var found = _entities.TryGetValue(id, out var value) && !value.Deleted;
        entity = value!;
        return found;
    }

    private bool CanResolveEntityOrChapter(Guid id) =>
        id == ProjectId
        ||
        (_entities.TryGetValue(id, out var entity) && !entity.Deleted)
        || (_chapters.TryGetValue(id, out var chapter) && !chapter.Deleted);

    private string ResourceForExisting(Guid id)
    {
        if (id == ProjectId) return Resource("Project", id);
        if (_entities.ContainsKey(id)) return Resource("Entity", id);
        if (_chapters.ContainsKey(id)) return Resource("Chapter", id);
        if (_acts.ContainsKey(id)) return Resource("Act", id);
        return Resource("Entity", id);
    }

    private int NextChapterOrder(Guid? actId) =>
        _chapters.Values
            .Where(chapter => !chapter.Deleted && chapter.ActId == actId)
            .Select(chapter => chapter.Order)
            .DefaultIfEmpty(-1)
            .Max() + 1;

    private int NextEntityOrder(string type, Guid parentId) =>
        _entities.Values
            .Where(entity => !entity.Deleted && entity.ParentId == parentId && string.Equals(entity.Type, type, StringComparison.OrdinalIgnoreCase))
            .Select(entity => entity.Order ?? -1)
            .DefaultIfEmpty(-1)
            .Max() + 1;

    private EntityState? FindDuplicateForCreate(string type, string name, IReadOnlyDictionary<string, string?>? properties, Guid? parentId)
    {
        var candidates = _entities.Values.Where(entity => !entity.Deleted && string.Equals(entity.Type, type, StringComparison.OrdinalIgnoreCase));
        if (parentId is not null && !string.Equals(type, EntityTypeService.ProjectFactNodeType, StringComparison.OrdinalIgnoreCase))
            candidates = candidates.Where(entity => entity.ParentId == parentId);

        if (string.Equals(type, EntityTypeService.ProjectFactNodeType, StringComparison.OrdinalIgnoreCase))
        {
            var requestedKey = NormalizeForComparison(ReadProperty(properties, "key") ?? name);
            return candidates.FirstOrDefault(entity =>
                NormalizeForComparison(ReadProperty(entity.Properties, "key") ?? entity.Name) == requestedKey);
        }

        var requestedName = NormalizeForComparison(name);
        return candidates.FirstOrDefault(entity => NormalizeForComparison(entity.Name) == requestedName);
    }

    private async Task<string> DuplicateEntityResultAsync(string requestedType, EntityState duplicate, CancellationToken cancellationToken) =>
        Serialize(new
        {
            status = "existing_match",
            message = $"No new {requestedType} was created because an existing {duplicate.Type} with the same name or key already exists. Use update_entity or link_entities for the existing entity, or create a more distinctly named entity if this is a separate story subject.",
            existing = await EntityPayloadAsync(duplicate, cancellationToken),
        });

    private async Task<object> EntityPayloadAsync(EntityState entity, CancellationToken cancellationToken)
    {
        var relationContext = await entityRelations.BuildForEntityAsync(ProjectId, entity.Id, EntityRelationOptions, cancellationToken);
        return new
        {
            id = entity.Id,
            type = entity.Type,
            name = entity.Name,
            order = entity.Order,
            parentId = entity.ParentId,
            properties = entity.Properties,
            relationContext,
        };
    }

    private void EnsureType(string type, bool isChapterScoped)
    {
        if (_entityTypes.ContainsKey(type)) return;
        _entityTypes[type] = new EntityTypeDefinition(
            type,
            type,
            type.EndsWith('s') ? type : type + "s",
            IsStructural: false,
            isChapterScoped,
            SortOrder: _entityTypes.Values.Select(entityType => entityType.SortOrder).DefaultIfEmpty(0).Max() + 100,
            DefaultProperties: new Dictionary<string, string?>());
    }

    private static string Resource(string kind, Guid id) => $"{kind}:{id:N}";

    private static string? ReadProperty(Dictionary<string, string?> properties, string key) =>
        properties.TryGetValue(key, out var value) ? value : null;

    private static string? ReadProperty(IReadOnlyDictionary<string, string?>? properties, string key) =>
        properties is not null && properties.TryGetValue(key, out var value) ? value : null;

    private static string NormalizeForComparison(string value) =>
        string.Join(' ', value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    private static string Serialize(object? value) => JsonSerializer.Serialize(value, JsonSerializerOptions.Default);

    private sealed record ActState(Guid Id, int Order, string Title, string Synopsis, bool Deleted)
    {
        public int Order { get; set; } = Order;
        public string Title { get; set; } = Title;
        public string Synopsis { get; set; } = Synopsis;
        public bool Deleted { get; set; } = Deleted;

        public OutlineActChange ToChange() => new(Id, Order, Title, Synopsis);
    }

    private sealed record ChapterState(Guid Id, Guid? ActId, int Order, string Title, string Synopsis, bool Deleted)
    {
        public Guid? ActId { get; set; } = ActId;
        public int Order { get; set; } = Order;
        public string Title { get; set; } = Title;
        public string Synopsis { get; set; } = Synopsis;
        public bool Deleted { get; set; } = Deleted;

        public OutlineChapterChange ToChange() => new(Id, ActId, Order, Title, Synopsis);
    }

    private sealed record EntityState(
        Guid Id,
        string Type,
        string Name,
        int? Order,
        Guid? ParentId,
        Dictionary<string, string?> Properties,
        bool Deleted)
    {
        public string Name { get; set; } = Name;
        public int? Order { get; set; } = Order;
        public Dictionary<string, string?> Properties { get; } = Properties;
        public bool Deleted { get; set; } = Deleted;

        public OutlineEntityChange ToChange() => new(Id, Type, Name, Order, ParentId, new Dictionary<string, string?>(Properties, StringComparer.OrdinalIgnoreCase));
    }
}
