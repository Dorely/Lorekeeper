using System.Text.Json;
using System.Text.Json.Nodes;
using Lorekeeper.Chapters;
using Lorekeeper.ChatTurns;
using Lorekeeper.Context;
using Lorekeeper.EntityVisuals;
using Lorekeeper.Ingest;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Persistence.Repositories;

namespace Lorekeeper.Outline;

public sealed class OutlineToolStagingContext(
IAppDatabaseOperationFactory database, Guid projectId, Guid conversationId, AiChangeConversationKind conversationKind, IActService acts, IChapterService chapters, IEntityService entities, IEntityTypeService entityTypes, IEntityVisualExampleService entityVisualExamples, Action? onDirectMutationApplied = null)
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
    private readonly HashSet<string> _directlyCreatedResources = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LinkState> _links = [];
    private readonly List<AiChange> _newChanges = [];

    private AiChangeBatch? _batch;
    private bool _loaded;
    private int _nextOrder;
    private long _nextSyntheticLinkId = -1;
    private Guid? _currentAssistantMessageId;
    private string _currentToolCallId = string.Empty;
    private string _currentToolName = string.Empty;
    private string _currentArgumentsJson = "{}";

    public Guid ProjectId { get; } = projectId;
    public Guid ConversationId { get; } = conversationId;
    public AiChangeConversationKind ConversationKind { get; } = conversationKind;

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

        var chapterDetails = new Dictionary<Guid, object>();
        foreach (var chapter in _chapters.Values.Where(chapter => !chapter.Deleted))
        {
            var chapterLinks = await ListEntityLinksCoreAsync(chapter.Id, cancellationToken);
            var beatPayloads = new List<object>();
            foreach (var beat in _entities.Values.Where(entity =>
                !entity.Deleted
                && string.Equals(entity.Type, _eventNodeType, StringComparison.OrdinalIgnoreCase)
                && entity.ParentId == chapter.Id).OrderBy(entity => entity.Order))
            {
                var beatLinks = await ListEntityLinksCoreAsync(beat.Id, cancellationToken);
                beatPayloads.Add(new
                {
                    id = beat.Id,
                    order = beat.Order,
                    name = beat.Name,
                    summary = beat.Summary,
                    attachedEntities = beatLinks
                        .Where(link => !string.Equals(link.EdgeType, EntityService.HasChildEdgeType, StringComparison.OrdinalIgnoreCase))
                        .Select(LinkPayload),
                });
            }
            chapterDetails[chapter.Id] = new
            {
                relevantEntities = chapterLinks
                    .Where(link => string.Equals(link.EdgeType, EntityService.RelevantToEdgeType, StringComparison.OrdinalIgnoreCase))
                    .Select(link => new { id = link.OtherEntityId, type = link.OtherEntityType, name = link.OtherEntityName }),
                beats = beatPayloads,
            };
        }

        object ProjectChapter(ChapterState chapter) => new
        {
            id = chapter.Id,
            order = chapter.Order,
            title = chapter.Title,
            synopsis = chapter.Synopsis,
            detail = chapterDetails[chapter.Id],
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

    public async Task<string> ListEntitiesAsync(string? type, int page, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        const int pageSize = 20;
        page = Math.Max(1, page);
        var typeNames = SearchableTypeNames(type);
        var ordered = _entities.Values
            .Where(entity => !entity.Deleted && typeNames.Contains(entity.Type))
            .OrderBy(entity => entity.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entity => entity.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var items = ordered.Skip((page - 1) * pageSize).Take(pageSize).Select(entity => new
        {
            id = entity.Id,
            type = entity.Type,
            name = entity.Name,
            origin = entity.IsIngestCreated ? "source-derived" : "project-owned",
            entity.IsIngestCreated,
            sourceEvidenceCount = entity.SourceEvidence.Count,
            state = "staged",
            detailReadTool = "read_entity",
            detailReadArguments = new { entityId = entity.Id, pageNumber = 1 },
        }).ToList();
        return Serialize(new
        {
            page,
            pageSize,
            total = ordered.Count,
            returned = items.Count,
            isComplete = page * pageSize >= ordered.Count,
            items,
            nextPageArguments = page * pageSize < ordered.Count ? new { type, page = page + 1 } : null,
        });
    }

    public async Task<string> SearchEntitiesAsync(string query, int topK, string? type, Guid? parentId, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(query)) return "Error: query is required.";
        topK = Math.Clamp(topK, 1, 20);

        var searchTerms = SearchTerms(query);
        var typeNames = SearchableTypeNames(type);
        var matches = _entities.Values
            .Where(entity =>
                !entity.Deleted
                && typeNames.Contains(entity.Type)
                && (parentId is null || entity.ParentId == parentId))
            .Select(entity => (Entity: entity, Score: SearchScore(entity, query, searchTerms)))
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Entity.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(match => match.Entity.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var selected = matches
            .Take(topK)
            .Select(match => CompactEntitySearchPayload(match.Entity, match.Score));

        return AgentPayloadPaginator.SerializeCompactDiscovery(query.Trim(), topK, matches.Count, selected, "read_entity");
    }

    public async Task<string> StageExternalChangeAsync(
        string summary,
        object? before,
        object? after,
        object result,
        string resourceKind,
        string resourceId,
        IReadOnlyCollection<string>? referencedResources = null,
        CancellationToken cancellationToken = default)
    {
        var resultJson = Serialize(result);
        await StageChangeAsync(
            summary, before, after, resultJson, resourceKind, resourceId,
            createdResources: [], referencedResources ?? [], cancellationToken);
        return resultJson;
    }

    public async Task<string?> GetEntityTypeAsync(Guid entityId, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        return TryGetEntity(entityId, out var entity) ? entity.Type : null;
    }

    public async Task<string> ReadEntityAsync(
        Guid entityId,
        bool addedToContextFeed,
        EntityRelationContextOptions? relationOptions = null,
        int? pageNumber = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        if (!TryGetEntity(entityId, out var entity))
            return $"Error: entity {entityId} not found in this project.";

        var links = await ListEntityLinksCoreAsync(entityId, cancellationToken);
        var resolvedRelationOptions = relationOptions ?? EntityRelationOptions;
        var relationContext = await BuildRelationContextAsync(entityId, resolvedRelationOptions, cancellationToken);
        var visualExamples = await entityVisualExamples.ListForEntityAsync(ProjectId, entityId, cancellationToken);

        var detail = JsonSerializer.SerializeToNode(new
        {
            origin = entity.IsIngestCreated ? "source-derived" : "project-owned",
            entity.IsIngestCreated,
            properties = entity.Properties,
            summary = entity.Summary,
            aliases = entity.Aliases,
            wikiSections = entity.WikiSections,
            sourceEvidence = entity.SourceEvidence,
            canonicalVisualReferences = visualExamples.Select(example => new
            {
                example.Id,
                example.EntityId,
                example.Label,
                example.SortOrder,
                example.Origin,
                image = new
                {
                    example.Image.Id,
                    example.Image.FileName,
                    example.Image.AltText,
                    example.Image.Prompt,
                },
            }),
            links = links.Select(link => new
            {
                link.EdgeId,
                link.EdgeType,
                direction = link.Direction.ToString(),
                link.OtherEntityId,
                link.OtherEntityName,
                link.OtherEntityType,
                link.SortOrder,
                link.Properties,
            }),
            relationContextPreview = RelationContextPreview(entity.Id, resolvedRelationOptions, relationContext),
        });
        return AgentPayloadPaginator.SerializePage(
            AgentPayloadPaginator.EntityIdentity(
                entity.Id,
                entity.Type,
                entity.Name,
                entity.Order,
                entity.ParentId,
                ("addedToContextFeed", JsonValue.Create(addedToContextFeed)),
                ("state", JsonValue.Create("staged"))),
            detail,
            "read_entity",
            new JsonObject { ["entityId"] = entity.Id },
            pageNumber);
    }

    private static object RelationContextPreview(Guid entityId, EntityRelationContextOptions options, object relationContext) => new
    {
        isComplete = false,
        note = "This traversal is a bounded orientation preview. All adjacent links are included separately; use list_entity_links and read_entity to continue traversal.",
        bounds = new { options.Depth, options.MaxDirectLinks, options.MaxTraversalPaths, options.MaxLinksPerNode },
        detailReadTool = "list_entity_links",
        detailReadArguments = new { entityId, pageNumber = 1 },
        value = relationContext,
    };

    public async Task<string> ListEntityLinksAsync(
        Guid entityId,
        int? pageNumber = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        if (!CanResolveEntityOrChapter(entityId))
            return $"Error: entity {entityId} not found in this project.";

        var links = await ListEntityLinksCoreAsync(entityId, cancellationToken);
        var endpoint = ResolveEndpoint(entityId, fallbackType: "Entity", fallbackName: entityId.ToString("N"));
        return AgentPayloadPaginator.SerializePage(
            AgentPayloadPaginator.EntityIdentity(
                endpoint.Id,
                endpoint.Type,
                endpoint.Name,
                null,
                null,
                ("state", JsonValue.Create("staged"))),
            JsonSerializer.SerializeToNode(new { links = links.Select(LinkPayload) }),
            "list_entity_links",
            new JsonObject { ["entityId"] = entityId },
            pageNumber);
    }

    public async Task<string> CreateActAsync(string title, string? synopsis, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(title)) return "Error: title is required.";

        var created = await acts.CreateAsync(ProjectId, title.Trim(), synopsis?.Trim(), cancellationToken: cancellationToken);
        var act = new ActState(created.Id, created.Order, created.Title, created.Synopsis, Deleted: false);
        _acts[act.Id] = act;
        MarkDirectlyCreated(Resource("Act", act.Id));
        onDirectMutationApplied?.Invoke();

        var result = Serialize(new { id = act.Id, order = act.Order, title = act.Title, synopsis = act.Synopsis });
        return result;
    }

    public async Task<string> UpdateActAsync(Guid actId, string? title, string? synopsis, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        if (!TryGetAct(actId, out var act)) return $"Error: act {actId} not found in this project.";

        if (IsDirectlyCreated(Resource("Act", actId)))
        {
            var updated = await acts.UpdateAsync(actId, title?.Trim(), synopsis?.Trim(), cancellationToken);
            _acts[updated.Id] = new ActState(updated.Id, updated.Order, updated.Title, updated.Synopsis, Deleted: false);
            onDirectMutationApplied?.Invoke();
            return Serialize(new { id = updated.Id, title = updated.Title, synopsis = updated.Synopsis });
        }

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

        if (IsDirectlyCreated(Resource("Act", actId)))
        {
            await acts.DeleteAsync(actId, cancellationToken);
            act.Deleted = true;
            foreach (var chapter in _chapters.Values.Where(chapter => !chapter.Deleted && chapter.ActId == actId))
            {
                chapter.ActId = null;
                chapter.Order = NextChapterOrder(null);
            }
            onDirectMutationApplied?.Invoke();
            return $"Deleted act {actId}. Owned chapters were moved to the unassigned bucket.";
        }

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

    public async Task<string> CreateChapterAsync(
        Guid? actId,
        string title,
        string? synopsis,
        CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(title)) return "Error: title is required.";
        if (actId is not null && !TryGetAct(actId.Value, out _)) return $"Error: act {actId} not found in this project.";

        var created = await chapters.CreateAsync(ProjectId, actId, title.Trim(), synopsis?.Trim(), cancellationToken: cancellationToken);
        var chapter = ChapterState.From(created);
        _chapters[chapter.Id] = chapter;
        MarkDirectlyCreated(Resource("Chapter", chapter.Id));
        onDirectMutationApplied?.Invoke();

        var result = Serialize(ChapterPayload(chapter));
        return result;
    }

    public async Task<string> UpdateChapterAsync(
        Guid chapterId,
        string? title,
        string? synopsis,
        Guid? newActId,
        bool moveChapter,
        CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        if (!TryGetChapter(chapterId, out var chapter)) return $"Error: chapter {chapterId} not found in this project.";
        if (moveChapter && newActId is not null && !TryGetAct(newActId.Value, out _)) return $"Error: act {newActId} not found in this project.";

        if (IsDirectlyCreated(Resource("Chapter", chapterId)))
        {
            var assignment = moveChapter ? new ChapterActAssignment(newActId) : (ChapterActAssignment?)null;
            var updated = await chapters.UpdateAsync(chapterId, title?.Trim(), synopsis?.Trim(), assignment, cancellationToken);
            _chapters[updated.Id] = ChapterState.From(updated);
            onDirectMutationApplied?.Invoke();
            return Serialize(ChapterPayload(_chapters[updated.Id]));
        }

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
        var result = Serialize(ChapterPayload(chapter));
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

        if (IsDirectlyCreated(Resource("Chapter", chapterId)))
        {
            await chapters.DeleteAsync(chapterId, cancellationToken);
            chapter.Deleted = true;
            foreach (var entity in _entities.Values.Where(entity => entity.ParentId == chapterId))
                entity.Deleted = true;
            onDirectMutationApplied?.Invoke();
            return $"Deleted chapter {chapterId}.";
        }

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
        var created = await entities.CreateAsync(ProjectId, trimmedType, name.Trim(), properties, parentId, resolvedOrder, cancellationToken: cancellationToken);
        var entity = new EntityState(
            created.Id,
            created.Type,
            created.Name,
            resolvedOrder,
            created.ParentId,
            new Dictionary<string, string?>(created.Properties, StringComparer.OrdinalIgnoreCase),
            created.Summary,
            created.Aliases,
            created.WikiSections,
            created.SourceEvidence,
            created.IsIngestCreated,
            Deleted: false);
        _entities[entity.Id] = entity;
        MarkDirectlyCreated(Resource("Entity", entity.Id));
        onDirectMutationApplied?.Invoke();

        var result = Serialize(EntityMutationPayload(entity));
        return result;
    }

    public async Task<string> UpdateEntityAsync(Guid entityId, string? name, Dictionary<string, string?>? propertiesToSet, string[]? propertiesToRemove, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        if (!TryGetEntity(entityId, out var entity)) return $"Error: entity {entityId} not found in this project.";

        if (IsDirectlyCreated(Resource("Entity", entityId)))
        {
            var updated = await entities.UpdateAsync(ProjectId, entityId, name?.Trim(), propertiesToSet, propertiesToRemove, cancellationToken);
            _entities[updated.Id] = new EntityState(
                updated.Id,
                updated.Type,
                updated.Name,
                entity.Order,
                entity.ParentId,
                new Dictionary<string, string?>(updated.Properties, StringComparer.OrdinalIgnoreCase),
                updated.Summary,
                updated.Aliases,
                updated.WikiSections,
                updated.SourceEvidence,
                updated.IsIngestCreated,
                Deleted: false);
            onDirectMutationApplied?.Invoke();
            return Serialize(EntityMutationPayload(_entities[updated.Id]));
        }

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
        var result = Serialize(EntityMutationPayload(entity));
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

        if (IsDirectlyCreated(Resource("Entity", entityId)))
        {
            var directlyDeleted = EntityMutationPayload(entity);
            await entities.DeleteAsync(ProjectId, entityId, cancellationToken);
            entity.Deleted = true;
            onDirectMutationApplied?.Invoke();
            return Serialize(new
            {
                status = "deleted",
                deleted = directlyDeleted,
            });
        }

        var before = entity.ToChange();
        var deleted = EntityMutationPayload(entity);
        entity.Deleted = true;
        var result = Serialize(new
        {
            status = "deleted",
            deleted,
        });
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
        var orderedEntities = new List<object>();
        foreach (var orderedId in orderedIds)
            orderedEntities.Add(EntityMutationPayload(_entities[orderedId]));
        var result = Serialize(new
        {
            status = "reordered",
            type = trimmedType,
            parentId,
            orderedIds,
            entities = orderedEntities,
        });
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

        var normalizedEdgeType = edgeType.Trim();
        if (string.Equals(normalizedEdgeType, "AppearsIn", StringComparison.OrdinalIgnoreCase)
            && TryGetEndpoint(toId, out var target)
            && string.Equals(target.Type, EntityTypeService.ChapterNodeType, StringComparison.OrdinalIgnoreCase))
        {
            normalizedEdgeType = EntityService.RelevantToEdgeType;
        }
        var link = new OutlineEntityLinkChange(
            fromId,
            toId,
            normalizedEdgeType,
            new Dictionary<string, string?>(properties ?? [], StringComparer.OrdinalIgnoreCase));
        var linkState = new LinkState(
            _nextSyntheticLinkId--,
            link.FromId,
            link.ToId,
            link.EdgeType,
            new Dictionary<string, string?>(link.Properties, StringComparer.OrdinalIgnoreCase));
        _links.Add(linkState);
        var result = Serialize(new
        {
            status = "linked",
            link = new
            {
                fromId,
                toId,
                edgeType = link.EdgeType,
                properties = link.Properties,
            },
            from = EndpointMutationPayload(fromId),
            to = EndpointMutationPayload(toId),
        });
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
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var projects = databaseOperation.Repositories.Projects;
        if (_loaded) return;

        _ = await projects.GetByIdAsync(ProjectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {ProjectId} not found.");

        foreach (var act in await acts.ListAsync(ProjectId, cancellationToken))
            _acts[act.Id] = new ActState(act.Id, act.Order, act.Title, act.Synopsis, Deleted: false);

        foreach (var chapter in await chapters.ListAsync(ProjectId, cancellationToken))
            _chapters[chapter.Id] = ChapterState.From(chapter);

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
                entity.Summary,
                entity.Aliases,
                entity.WikiSections,
                entity.SourceEvidence,
                entity.IsIngestCreated,
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
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var changes = databaseOperation.Repositories.AiChanges;
        var beforeJson = Serialize(before);
        var afterJson = Serialize(after);
        if (string.Equals(beforeJson, afterJson, StringComparison.Ordinal))
            return;

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
            ArgumentsJson = ChatToolCallManifest.CompactPersistedArguments(_currentToolName, _currentArgumentsJson),
            Summary = summary,
            BeforeJson = beforeJson,
            AfterJson = afterJson,
            ResultJson = resultJson,
            ResourceKind = resourceKind,
            ResourceId = resourceId,
            CreatedResourceIdsJson = Serialize(createdResources),
            ReferencedResourceIdsJson = Serialize(referencedResources),
            DependsOnChangeIdsJson = Serialize(dependencies),
        };

        await changes.AddChangeAsync(change, cancellationToken);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        _newChanges.Add(change);

        foreach (var resource in createdResources)
            _createdResourceProducers[resource] = change.Id;
    }

    private async Task<AiChangeBatch> EnsureBatchAsync(CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var changes = databaseOperation.Repositories.AiChanges;
        if (_batch is not null) return _batch;

        _batch = new AiChangeBatch
        {
            ProjectId = ProjectId,
            ConversationKind = ConversationKind,
            ConversationId = ConversationId,
            AssistantMessageId = _currentAssistantMessageId,
        };
        await changes.AddBatchAsync(_batch, cancellationToken);
        await databaseOperation.SaveChangesAsync(cancellationToken);
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

    private Task<string> DuplicateEntityResultAsync(string requestedType, EntityState duplicate, CancellationToken cancellationToken) =>
        Task.FromResult(Serialize(new
        {
            status = "existing_match",
            message = $"No new {requestedType} was created because an existing {duplicate.Type} with the same name or key already exists. Use update_entity or link_entities for the existing entity, or create a more distinctly named entity if this is a separate story subject.",
            existing = EntityMutationPayload(duplicate),
        }));

    private static object EntityMutationPayload(EntityState entity) => new
    {
        id = entity.Id,
        type = entity.Type,
        name = entity.Name,
        order = entity.Order,
        parentId = entity.ParentId,
        properties = entity.Properties,
        origin = entity.IsIngestCreated ? "source-derived" : "project-owned",
        entity.IsIngestCreated,
        sourceEvidence = entity.SourceEvidence,
    };

    private object EndpointMutationPayload(Guid entityId)
    {
        if (TryGetEntity(entityId, out var entity))
            return OutlineMutationPayloads.Endpoint(entity.Id, entity.Type, entity.Name);

        if (TryGetEndpoint(entityId, out var endpoint))
            return OutlineMutationPayloads.Endpoint(endpoint.Id, endpoint.Type, endpoint.Name);

        return new
        {
            id = entityId,
            status = "not_found",
        };
    }

    private async Task<EntityRelationContext> BuildRelationContextAsync(
        Guid entityId,
        EntityRelationContextOptions options,
        CancellationToken cancellationToken)
    {
        var resolvedOptions = NormalizeRelationOptions(options);
        if (!TryGetEndpoint(entityId, out var root))
            return EntityRelationContext.Empty;

        var links = await ListEntityLinksCoreAsync(entityId, cancellationToken);
        var directLinks = links
            .Where(link => !link.IsAutoLink && CanTraverse(link))
            .Take(resolvedOptions.MaxDirectLinks)
            .Select(link => ProjectDirectLink(root, link))
            .ToList();
        var autoLinks = links
            .Where(link => link.IsAutoLink)
            .Take(resolvedOptions.MaxDirectLinks)
            .Select(link => ProjectDirectLink(root, link))
            .ToList();
        var traversalMap = await BuildTraversalMapAsync(entityId, resolvedOptions, cancellationToken);

        return new EntityRelationContext(directLinks, traversalMap, autoLinks);
    }

    private async Task<IReadOnlyList<EntityTraversalPathContext>> BuildTraversalMapAsync(
        Guid rootEntityId,
        EntityRelationContextOptions options,
        CancellationToken cancellationToken)
    {
        if (options.Depth <= 0 || options.MaxTraversalPaths <= 0)
            return [];

        if (!TryGetEndpoint(rootEntityId, out var root))
            return [];

        var paths = new List<EntityTraversalPathContext>();
        var visited = new HashSet<Guid> { root.Id };
        var queue = new Queue<TraversalCursor>();
        queue.Enqueue(new TraversalCursor(root.Id, Depth: 0, FormatEndpoint(root)));

        while (queue.Count > 0 && paths.Count < options.MaxTraversalPaths)
        {
            var current = queue.Dequeue();
            if (current.Depth >= options.Depth) continue;

            var links = await ListEntityLinksCoreAsync(current.EntityId, cancellationToken);
            foreach (var link in links.Take(options.MaxLinksPerNode))
            {
                if (!CanTraverse(link) || link.OtherEntityId == Guid.Empty || !visited.Add(link.OtherEntityId))
                    continue;

                var other = ResolveEndpoint(link.OtherEntityId, link.OtherEntityType, link.OtherEntityName);
                var nextDepth = current.Depth + 1;
                var nextPath = AppendHop(current.Path, link, other);
                paths.Add(new EntityTraversalPathContext(nextDepth, other.Id, other.Type, other.Name, nextPath));
                if (paths.Count >= options.MaxTraversalPaths) break;

                queue.Enqueue(new TraversalCursor(other.Id, nextDepth, nextPath));
            }
        }

        return paths;
    }

    private async Task<List<StagedEntityLink>> ListEntityLinksCoreAsync(Guid entityId, CancellationToken cancellationToken)
    {
        var overlayLinks = BuildOverlayLinks(entityId);
        var overlayKeys = overlayLinks.Select(link => LinkKey(entityId, link)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<StagedEntityLink>();

        if (!IsCreatedEntity(entityId))
        {
            foreach (var link in await entities.ListLinksAsync(ProjectId, entityId, cancellationToken))
            {
                if (link.OtherEntityId == Guid.Empty || IsDeletedEndpoint(link.OtherEntityId))
                    continue;

                var fromId = link.Direction == EntityLinkDirection.Outgoing ? entityId : link.OtherEntityId;
                var toId = link.Direction == EntityLinkDirection.Outgoing ? link.OtherEntityId : entityId;
                if (overlayKeys.Contains(LinkKey(fromId, toId, link.EdgeType)))
                    continue;

                var other = ResolveEndpoint(link.OtherEntityId, link.OtherEntityType, link.OtherEntityName);
                result.Add(new StagedEntityLink(
                    link.EdgeId,
                    link.EdgeType,
                    link.Direction,
                    other.Id,
                    other.Name,
                    other.Type,
                    link.SortOrder,
                    link.Properties,
                    link.IsAutoLink));
            }
        }

        result.AddRange(overlayLinks);
        return result;
    }

    private List<StagedEntityLink> BuildOverlayLinks(Guid rootId)
    {
        var links = new List<StagedEntityLink>();

        foreach (var child in _entities.Values.Where(entity => !entity.Deleted && entity.ParentId is not null))
        {
            var parentId = child.ParentId!.Value;
            if (parentId != rootId && child.Id != rootId)
                continue;
            if (!TryGetEndpoint(parentId, out var parent))
                continue;

            var link = new LinkState(
                SyntheticStructuralLinkId(parentId, child.Id),
                parentId,
                child.Id,
                EntityService.HasChildEdgeType,
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
                child.Order);
            links.Add(ProjectLink(link, rootId));
        }

        links.AddRange(_links
            .Where(link => link.FromId == rootId || link.ToId == rootId)
            .Where(link => !IsDeletedEndpoint(link.FromId) && !IsDeletedEndpoint(link.ToId))
            .Select(link => ProjectLink(link, rootId)));

        return links;
    }

    private StagedEntityLink ProjectLink(LinkState link, Guid rootId)
    {
        var isOutgoing = link.FromId == rootId;
        var otherId = isOutgoing ? link.ToId : link.FromId;
        var other = ResolveEndpoint(otherId, fallbackType: "Entity", fallbackName: otherId.ToString("N"));

        return new StagedEntityLink(
            link.EdgeId,
            link.EdgeType,
            isOutgoing ? EntityLinkDirection.Outgoing : EntityLinkDirection.Incoming,
            other.Id,
            other.Name,
            other.Type,
            link.SortOrder,
            link.Properties,
            false);
    }

    private static object LinkPayload(StagedEntityLink link) => new
    {
        edgeId = link.EdgeId,
        edgeType = link.EdgeType,
        direction = link.Direction.ToString(),
        otherEntityId = link.OtherEntityId,
        otherEntityName = link.OtherEntityName,
        otherEntityType = link.OtherEntityType,
        sortOrder = link.SortOrder,
        properties = link.Properties,
        isAutoLink = link.IsAutoLink,
    };

    private static EntityDirectLinkContext ProjectDirectLink(EntityEndpoint root, StagedEntityLink link) => new(
        link.EdgeId,
        link.EdgeType,
        link.Direction.ToString(),
        link.OtherEntityId,
        link.OtherEntityName,
        link.OtherEntityType,
        link.SortOrder,
        link.Properties,
        AppendHop(FormatEndpoint(root), link),
        link.IsAutoLink);

    private static string AppendHop(string path, StagedEntityLink link, EntityEndpoint other) =>
        AppendHop(path, link, FormatEndpoint(other));

    private static string AppendHop(string path, StagedEntityLink link) =>
        AppendHop(path, link, FormatEndpoint(link.OtherEntityType, link.OtherEntityName, link.OtherEntityId));

    private static string AppendHop(string path, StagedEntityLink link, string other)
    {
        var relation = string.IsNullOrWhiteSpace(link.EdgeType) ? "related" : link.EdgeType;
        return link.Direction == EntityLinkDirection.Outgoing
            ? $"{path} -[{relation}]-> {other}"
            : $"{path} <-[{relation}]- {other}";
    }

    private EntityEndpoint ResolveEndpoint(Guid id, string fallbackType, string fallbackName) =>
        TryGetEndpoint(id, out var endpoint)
            ? endpoint
            : new EntityEndpoint(id, fallbackType, fallbackName);

    private bool TryGetEndpoint(Guid id, out EntityEndpoint endpoint)
    {
        if (id == ProjectId)
        {
            endpoint = new EntityEndpoint(ProjectId, EntityTypeService.ProjectNodeType, "Project");
            return true;
        }

        if (_entities.TryGetValue(id, out var entity) && !entity.Deleted)
        {
            endpoint = new EntityEndpoint(entity.Id, entity.Type, entity.Name);
            return true;
        }

        if (_chapters.TryGetValue(id, out var chapter) && !chapter.Deleted)
        {
            endpoint = new EntityEndpoint(chapter.Id, EntityTypeService.ChapterNodeType, chapter.Title);
            return true;
        }

        endpoint = null!;
        return false;
    }

    private bool IsCreatedEntity(Guid entityId) =>
        _createdResourceProducers.ContainsKey(Resource("Entity", entityId))
        || IsDirectlyCreated(Resource("Entity", entityId));

    private bool IsDirectlyCreated(string resource) =>
        _directlyCreatedResources.Contains(resource);

    private void MarkDirectlyCreated(string resource) =>
        _directlyCreatedResources.Add(resource);

    private bool IsDeletedEndpoint(Guid id) =>
        (_entities.TryGetValue(id, out var entity) && entity.Deleted)
        || (_chapters.TryGetValue(id, out var chapter) && chapter.Deleted);

    private static bool CanTraverse(StagedEntityLink link) =>
        !link.IsAutoLink
        && !string.Equals(link.OtherEntityType, EntityTypeService.SourceNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(link.OtherEntityType, EntityTypeService.SourceChunkNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(link.OtherEntityType, EntityTypeService.SourceBlockNodeType, StringComparison.OrdinalIgnoreCase);

    private static EntityRelationContextOptions NormalizeRelationOptions(EntityRelationContextOptions options) => new()
    {
        Depth = Math.Clamp(options.Depth, 0, 3),
        MaxDirectLinks = Math.Clamp(options.MaxDirectLinks, 0, 30),
        MaxTraversalPaths = Math.Clamp(options.MaxTraversalPaths, 0, 80),
        MaxLinksPerNode = Math.Clamp(options.MaxLinksPerNode, 1, 30),
    };

    private static string FormatEndpoint(EntityEndpoint endpoint) => FormatEndpoint(endpoint.Type, endpoint.Name, endpoint.Id);

    private static string FormatEndpoint(string type, string name, Guid id)
    {
        var label = string.IsNullOrWhiteSpace(name) ? id.ToString("N") : name.Trim();
        return $"{type} \"{label}\" (id={id:N})";
    }

    private static string LinkKey(Guid rootId, StagedEntityLink link)
    {
        var fromId = link.Direction == EntityLinkDirection.Outgoing ? rootId : link.OtherEntityId;
        var toId = link.Direction == EntityLinkDirection.Outgoing ? link.OtherEntityId : rootId;
        return LinkKey(fromId, toId, link.EdgeType);
    }

    private static string LinkKey(Guid fromId, Guid toId, string edgeType) =>
        $"{edgeType.ToUpperInvariant()}:{fromId:N}:{toId:N}";

    private static long SyntheticStructuralLinkId(Guid parentId, Guid childId)
    {
        var bytes = parentId.ToByteArray().Concat(childId.ToByteArray()).ToArray();
        var hash = BitConverter.ToInt64(bytes, 0) ^ BitConverter.ToInt64(bytes, 8) ^ BitConverter.ToInt64(bytes, 16) ^ BitConverter.ToInt64(bytes, 24);
        return hash is 0 or long.MinValue ? -1 : -Math.Abs(hash);
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

    private IReadOnlySet<string> SearchableTypeNames(string? type)
    {
        if (!string.IsNullOrWhiteSpace(type))
            return new HashSet<string>([type.Trim()], StringComparer.OrdinalIgnoreCase);

        return _entityTypes.Values
            .Select(entityType => entityType.Type)
            .Where(IsSearchableEntityType)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string[] SearchTerms(string query) =>
        query.Split([' ', '\t', '\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(term => term.Trim('"', '\'', '`', '(', ')', '[', ']', '{', '}', '.', ':'))
            .Where(term => term.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static int SearchScore(EntityState entity, string query, IReadOnlyList<string> searchTerms)
    {
        var score = TextMatchScore(entity.Name, query, titleWeight: 80, detailWeight: 30);
        score += TextMatchScore(entity.Type, query, titleWeight: 12, detailWeight: 8);
        score += TextMatchScore(entity.Summary, query, titleWeight: 20, detailWeight: 12);
        foreach (var alias in entity.Aliases)
            score += TextMatchScore(alias, query, titleWeight: 30, detailWeight: 16);
        foreach (var section in entity.WikiSections)
        {
            score += TextMatchScore(section.Title, query, titleWeight: 12, detailWeight: 6);
            score += TextMatchScore(section.Body, query, titleWeight: 12, detailWeight: 8);
        }
        foreach (var sourceEvidence in entity.SourceEvidence)
        {
            score += TextMatchScore(sourceEvidence.SourceTitle, query, titleWeight: 12, detailWeight: 6);
            score += TextMatchScore(sourceEvidence.Markdown, query, titleWeight: 12, detailWeight: 8);
        }
        foreach (var property in entity.Properties)
        {
            score += TextMatchScore(property.Key, query, titleWeight: 8, detailWeight: 4);
            score += TextMatchScore(property.Value, query, titleWeight: 8, detailWeight: 4);
        }

        foreach (var term in searchTerms)
        {
            score += TextMatchScore(entity.Name, term, titleWeight: 180, detailWeight: 60);
            score += TextMatchScore(entity.Type, term, titleWeight: 16, detailWeight: 8);
            score += TextMatchScore(entity.Summary, term, titleWeight: 28, detailWeight: 14);
            foreach (var alias in entity.Aliases)
                score += TextMatchScore(alias, term, titleWeight: 70, detailWeight: 24);
            foreach (var section in entity.WikiSections)
            {
                score += TextMatchScore(section.Title, term, titleWeight: 18, detailWeight: 8);
                score += TextMatchScore(section.Body, term, titleWeight: 18, detailWeight: 10);
            }
            foreach (var sourceEvidence in entity.SourceEvidence)
            {
                score += TextMatchScore(sourceEvidence.SourceTitle, term, titleWeight: 18, detailWeight: 8);
                score += TextMatchScore(sourceEvidence.Markdown, term, titleWeight: 18, detailWeight: 10);
            }
            foreach (var property in entity.Properties)
            {
                score += TextMatchScore(property.Key, term, titleWeight: 10, detailWeight: 5);
                score += TextMatchScore(property.Value, term, titleWeight: 10, detailWeight: 5);
            }
        }

        return score;
    }

    private static int TextMatchScore(string? value, string query, int titleWeight, int detailWeight)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(query)) return 0;
        if (value.Equals(query, StringComparison.OrdinalIgnoreCase)) return titleWeight * 4;
        if (value.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return titleWeight * 2;
        return value.Contains(query, StringComparison.OrdinalIgnoreCase) ? detailWeight : 0;
    }

    private static object CompactEntitySearchPayload(EntityState entity, int score) => new
    {
        id = entity.Id,
        type = entity.Type,
        name = entity.Name,
        order = entity.Order,
        parentId = entity.ParentId,
        origin = entity.IsIngestCreated ? "source-derived" : "project-owned",
        entity.IsIngestCreated,
        matchScore = score,
        previewIsComplete = false,
        previewCounts = new
        {
            summaryCharacters = entity.Summary?.Length ?? 0,
            aliases = entity.Aliases.Count,
            wikiSections = entity.WikiSections.Count,
            sourceEvidence = entity.SourceEvidence.Count,
            properties = entity.Properties.Count,
        },
        preview = new
        {
            summaryText = TruncatePropertyValue(entity.Summary),
            aliases = entity.Aliases.Take(4).ToArray(),
            properties = CompactProperties(entity.Properties),
        },
        detailReadTool = "read_entity",
        detailReadArguments = new { entityId = entity.Id, pageNumber = 1 },
    };

    private static Dictionary<string, string?> CompactProperties(IReadOnlyDictionary<string, string?> properties)
    {
        var compact = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in properties
            .Where(property => !string.Equals(property.Key, "order", StringComparison.OrdinalIgnoreCase))
            .OrderBy(property => property.Key, StringComparer.OrdinalIgnoreCase)
            .Take(4))
        {
            compact[property.Key] = TruncatePropertyValue(property.Value);
        }

        return compact;
    }

    private static string? TruncatePropertyValue(string? value) =>
        string.IsNullOrEmpty(value) || value.Length <= 240 ? value : value[..240] + "...";

    private static bool IsSearchableEntityType(string type) =>
        !string.Equals(type, EntityTypeService.ProjectNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.ActNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.ChapterNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.ProjectFactNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.SourceNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.SourceChunkNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.SourceBlockNodeType, StringComparison.OrdinalIgnoreCase);

    private static object ChapterPayload(ChapterState chapter) => new
    {
        id = chapter.Id,
        actId = chapter.ActId,
        order = chapter.Order,
        title = chapter.Title,
        synopsis = chapter.Synopsis,
    };

    private static string Serialize(object? value) => JsonSerializer.Serialize(value, JsonSerializerOptions.Default);

    private sealed record ActState(Guid Id, int Order, string Title, string Synopsis, bool Deleted)
    {
        public int Order { get; set; } = Order;
        public string Title { get; set; } = Title;
        public string Synopsis { get; set; } = Synopsis;
        public bool Deleted { get; set; } = Deleted;

        public OutlineActChange ToChange() => new(Id, Order, Title, Synopsis);
    }

    private sealed record ChapterState(
        Guid Id,
        Guid? ActId,
        int Order,
        string Title,
        string Synopsis,
        bool Deleted)
    {
        public Guid? ActId { get; set; } = ActId;
        public int Order { get; set; } = Order;
        public string Title { get; set; } = Title;
        public string Synopsis { get; set; } = Synopsis;
        public bool Deleted { get; set; } = Deleted;

        public OutlineChapterChange ToChange() => new(Id, ActId, Order, Title, Synopsis);

        public static ChapterState From(Chapter chapter)
        {
            return new ChapterState(
                chapter.Id,
                chapter.ActId,
                chapter.Order,
                chapter.Title,
                chapter.Synopsis,
                Deleted: false);
        }
    }

    private sealed record EntityState(
        Guid Id,
        string Type,
        string Name,
        int? Order,
        Guid? ParentId,
        Dictionary<string, string?> Properties,
        string Summary,
        IReadOnlyList<string> Aliases,
        IReadOnlyList<IngestWikiSection> WikiSections,
        IReadOnlyList<IngestSourceEvidence> SourceEvidence,
        bool IsIngestCreated,
        bool Deleted)
    {
        public string Name { get; set; } = Name;
        public int? Order { get; set; } = Order;
        public Dictionary<string, string?> Properties { get; } = Properties;
        public bool Deleted { get; set; } = Deleted;

        public OutlineEntityChange ToChange() => new(Id, Type, Name, Order, ParentId, new Dictionary<string, string?>(Properties, StringComparer.OrdinalIgnoreCase));
    }

    private sealed record LinkState(
        long EdgeId,
        Guid FromId,
        Guid ToId,
        string EdgeType,
        IReadOnlyDictionary<string, string?> Properties,
        int? SortOrder = null);

    private sealed record StagedEntityLink(
        long EdgeId,
        string EdgeType,
        EntityLinkDirection Direction,
        Guid OtherEntityId,
        string OtherEntityName,
        string OtherEntityType,
        int? SortOrder,
        IReadOnlyDictionary<string, string?> Properties,
        bool IsAutoLink);

    private sealed record EntityEndpoint(Guid Id, string Type, string Name);

    private sealed record TraversalCursor(Guid EntityId, int Depth, string Path);
}
