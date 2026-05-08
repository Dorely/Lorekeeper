using System.Text;
using Lorekeeper.Chapters;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence.Repositories;
using Lorekeeper.Writing;

namespace Lorekeeper.Context;

public sealed class ContextBuilder(
    IEditorContextPreferenceRepository preferences,
    IActService acts,
    IChapterService chapters,
    IProjectFactService projectFacts,
    IWritingSampleService writingSamples,
    IEntityService entities) : IEditorContextService
{
    public async Task<ContextAssembly> BuildAsync(
        Project project,
        Chapter? currentChapter,
        CancellationToken cancellationToken = default)
    {
        var preferenceMap = currentChapter is null
            ? new Dictionary<string, EditorContextPreference>(StringComparer.Ordinal)
            : (await preferences.ListForChapterAsync(project.Id, currentChapter.Id, cancellationToken))
                .ToDictionary(preference => PreferenceKey(preference.Kind, preference.Key), StringComparer.Ordinal);

        var items = new List<ContextItem>
        {
            new(
                Key: EditorContextKeys.SystemPrompt,
                Kind: ContextItemKind.SystemPrompt,
                Label: "Project Guidance",
                Body: project.SystemPrompt,
                IsEnabled: true,
                IsRemovable: false),
        };

        if (currentChapter is not null)
        {
            items.Add(new ContextItem(
                Key: EditorContextKeys.CurrentChapter,
                Kind: ContextItemKind.CurrentChapter,
                Label: $"Current Chapter — {currentChapter.Title} (line-numbered)",
                Body: ChapterFormatting.WithLineNumbers(currentChapter.Body),
                IsEnabled: IsIncluded(preferenceMap, ContextItemKind.CurrentChapter, EditorContextKeys.CurrentChapter, defaultIncluded: true),
                IsRemovable: true));

            items.Add(new ContextItem(
                Key: EditorContextKeys.ProjectOutline,
                Kind: ContextItemKind.ProjectOutline,
                Label: "Outline Structure and Synopses",
                Body: await BuildOutlineBlockAsync(project.Id, currentChapter.Id, cancellationToken),
                IsEnabled: IsIncluded(preferenceMap, ContextItemKind.ProjectOutline, EditorContextKeys.ProjectOutline, defaultIncluded: true),
                IsRemovable: true));

            items.Add(new ContextItem(
                Key: EditorContextKeys.ProjectFacts,
                Kind: ContextItemKind.ProjectFacts,
                Label: "Project Facts",
                Body: await BuildProjectFactsBlockAsync(project.Id, cancellationToken),
                IsEnabled: IsIncluded(preferenceMap, ContextItemKind.ProjectFacts, EditorContextKeys.ProjectFacts, defaultIncluded: true),
                IsRemovable: true));

            foreach (var sample in await writingSamples.ListAsync(project.Id, cancellationToken))
            {
                var key = EditorContextKeys.WritingSample(sample.Id);
                items.Add(new ContextItem(
                    Key: key,
                    Kind: ContextItemKind.WritingSample,
                    Label: $"Writing Sample — {sample.Title}",
                    Body: BuildWritingSampleBlock(sample),
                    IsEnabled: IsIncluded(preferenceMap, ContextItemKind.WritingSample, key, defaultIncluded: true),
                    IsRemovable: true,
                    Badge: "Style"));
            }

            foreach (var entity in await ListContextEntitiesAsync(project.Id, currentChapter.Id, preferenceMap, cancellationToken))
            {
                var key = EditorContextKeys.Entity(entity.Id);
                items.Add(new ContextItem(
                    Key: key,
                    Kind: ContextItemKind.Entity,
                    Label: $"{entity.Type} — {entity.Name}",
                    Body: await BuildEntityBlockAsync(project.Id, entity, cancellationToken),
                    IsEnabled: true,
                    IsRemovable: true,
                    Badge: entity.Type,
                    Reason: entity.ParentId == currentChapter.Id ? "Chapter beat" : "Related to current chapter context"));
            }
        }

        return new ContextAssembly(items);
    }

    public async Task SetItemIncludedAsync(
        Guid projectId,
        Guid chapterId,
        ContextItemKind kind,
        string key,
        bool isIncluded,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Context item key is required.", nameof(key));

        var chapter = await chapters.GetAsync(chapterId, cancellationToken)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found.");
        if (chapter.ProjectId != projectId)
            throw new InvalidOperationException($"Chapter {chapterId} does not belong to project {projectId}.");

        var kindText = kind.ToString();
        var preference = await preferences.FindAsync(projectId, chapterId, kindText, key, cancellationToken);
        if (preference is null)
        {
            preference = new EditorContextPreference
            {
                ProjectId = projectId,
                ChapterId = chapterId,
                Kind = kindText,
                Key = key,
                IsIncluded = isIncluded,
            };
            await preferences.AddAsync(preference, cancellationToken);
        }
        else
        {
            preference.IsIncluded = isIncluded;
            preference.UpdatedAt = DateTime.UtcNow;
            preferences.Update(preference);
        }

        await preferences.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<StoryEntity>> ListAutoRelatedEntitiesAsync(
        Guid projectId,
        Guid chapterId,
        CancellationToken cancellationToken = default)
    {
        var byId = new Dictionary<Guid, StoryEntity>();
        var beats = await entities.ListAsync(projectId, EntityTypeService.EventNodeType, chapterId, cancellationToken);
        foreach (var beat in beats)
            AddContextEntity(byId, beat);

        await AddLinkedEntitiesAsync(projectId, chapterId, byId, cancellationToken);
        foreach (var beat in beats)
            await AddLinkedEntitiesAsync(projectId, beat.Id, byId, cancellationToken);

        return byId.Values
            .OrderBy(entity => entity.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entity => entity.Order ?? int.MaxValue)
            .ThenBy(entity => entity.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyCollection<Guid>> ListIncludedEntityIdsAsync(
        Guid projectId,
        Guid chapterId,
        CancellationToken cancellationToken = default)
    {
        var preferenceMap = (await preferences.ListForChapterAsync(projectId, chapterId, cancellationToken))
            .ToDictionary(preference => PreferenceKey(preference.Kind, preference.Key), StringComparer.Ordinal);
        var entitiesForContext = await ListContextEntitiesAsync(projectId, chapterId, preferenceMap, cancellationToken);
        return entitiesForContext.Select(entity => entity.Id).ToHashSet();
    }

    private async Task<IReadOnlyList<StoryEntity>> ListContextEntitiesAsync(
        Guid projectId,
        Guid chapterId,
        IReadOnlyDictionary<string, EditorContextPreference> preferenceMap,
        CancellationToken cancellationToken)
    {
        var byId = (await ListAutoRelatedEntitiesAsync(projectId, chapterId, cancellationToken))
            .Where(entity => IsIncluded(preferenceMap, ContextItemKind.Entity, EditorContextKeys.Entity(entity.Id), defaultIncluded: true))
            .ToDictionary(entity => entity.Id);

        foreach (var preference in preferenceMap.Values.Where(preference =>
            preference.IsIncluded
            && string.Equals(preference.Kind, ContextItemKind.Entity.ToString(), StringComparison.Ordinal)
            && EditorContextKeys.TryParseEntity(preference.Key, out _)))
        {
            if (!EditorContextKeys.TryParseEntity(preference.Key, out var entityId) || byId.ContainsKey(entityId))
                continue;

            var entity = await entities.GetAsync(projectId, entityId, cancellationToken);
            if (entity is not null && IsContextEntityType(entity.Type))
                byId[entity.Id] = entity;
        }

        return byId.Values
            .OrderBy(entity => entity.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entity => entity.Order ?? int.MaxValue)
            .ThenBy(entity => entity.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task AddLinkedEntitiesAsync(
        Guid projectId,
        Guid entityId,
        IDictionary<Guid, StoryEntity> byId,
        CancellationToken cancellationToken)
    {
        var links = await entities.ListLinksAsync(projectId, entityId, cancellationToken);
        foreach (var link in links)
        {
            if (!IsContextEntityType(link.OtherEntityType)) continue;
            var entity = await entities.GetAsync(projectId, link.OtherEntityId, cancellationToken);
            if (entity is not null)
                AddContextEntity(byId, entity);
        }
    }

    private static void AddContextEntity(IDictionary<Guid, StoryEntity> byId, StoryEntity entity)
    {
        if (!IsContextEntityType(entity.Type)) return;
        byId.TryAdd(entity.Id, entity);
    }

    private async Task<string> BuildOutlineBlockAsync(Guid projectId, Guid currentChapterId, CancellationToken cancellationToken)
    {
        var actList = await acts.ListAsync(projectId, cancellationToken);
        var allChapters = await chapters.ListAsync(projectId, cancellationToken);
        var byAct = allChapters
            .Where(chapter => chapter.ActId is not null)
            .GroupBy(chapter => chapter.ActId!.Value)
            .ToDictionary(group => group.Key, group => group.OrderBy(chapter => chapter.Order).ToList());
        var unassigned = allChapters.Where(chapter => chapter.ActId is null).OrderBy(chapter => chapter.Order).ToList();

        var sb = new StringBuilder();
        foreach (var act in actList.OrderBy(act => act.Order))
        {
            sb.Append("Act ").Append(act.Order + 1).Append(": ").AppendLine(act.Title);
            AppendOptionalIndented(sb, "Synopsis", act.Synopsis, 2);
            await AppendChaptersAsync(sb, byAct.TryGetValue(act.Id, out var chaptersInAct) ? chaptersInAct : [], currentChapterId);
            sb.AppendLine();
        }

        if (unassigned.Count > 0)
        {
            sb.AppendLine("Unassigned Chapters");
            await AppendChaptersAsync(sb, unassigned, currentChapterId);
        }

        return sb.Length == 0 ? "(no outline yet)" : sb.ToString().TrimEnd();
    }

    private async Task<string> BuildProjectFactsBlockAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var facts = await projectFacts.ListAsync(projectId, cancellationToken);
        if (facts.Count == 0) return "(no project facts yet)";

        var sb = new StringBuilder();
        foreach (var fact in facts)
        {
            sb.Append("- ").Append(fact.Key).Append(": ").AppendLine(fact.Value);
            foreach (var link in fact.LinkedEntities)
            {
                sb.Append("  linked ").Append(link.EdgeType).Append(' ')
                  .Append(link.EntityName).Append(" (").Append(link.EntityType).AppendLine(")");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildWritingSampleBlock(WritingSample sample)
    {
        var body = string.IsNullOrWhiteSpace(sample.Body) ? "(empty)" : sample.Body.Trim();
        return $"# {sample.Title}\n\n{body}";
    }

    private async Task<string> BuildEntityBlockAsync(Guid projectId, StoryEntity entity, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.Append("Type: ").AppendLine(entity.Type);
        sb.Append("Name: ").AppendLine(entity.Name);

        if (entity.Properties.Count > 0)
        {
            sb.AppendLine("Properties:");
            foreach (var property in entity.Properties.OrderBy(property => property.Key, StringComparer.OrdinalIgnoreCase))
                sb.Append("- ").Append(property.Key).Append(": ").AppendLine(property.Value ?? string.Empty);
        }

        if (entity.IngestObservations.Count > 0)
        {
            sb.AppendLine("Ingest observations:");
            foreach (var observation in entity.IngestObservations.Take(5))
            {
                var text = !string.IsNullOrWhiteSpace(observation.Summary)
                    ? observation.Summary
                    : !string.IsNullOrWhiteSpace(observation.Evidence)
                        ? observation.Evidence
                        : observation.Notes;
                sb.Append("- ").Append(observation.SourceTitle).Append(": ").AppendLine(text);
            }
        }

        var links = await entities.ListLinksAsync(projectId, entity.Id, cancellationToken);
        var visibleLinks = links
            .Where(link => IsContextEntityType(link.OtherEntityType) || string.Equals(link.OtherEntityType, EntityTypeService.ChapterNodeType, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (visibleLinks.Count > 0)
        {
            sb.AppendLine("Links:");
            foreach (var link in visibleLinks)
            {
                var direction = link.Direction == EntityLinkDirection.Outgoing ? "->" : "<-";
                sb.Append("- ").Append(direction).Append(' ').Append(link.EdgeType).Append(' ')
                  .Append(link.OtherEntityName).Append(" (").Append(link.OtherEntityType).AppendLine(")");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private async Task AppendChaptersAsync(StringBuilder sb, IReadOnlyList<Chapter> chapterList, Guid currentChapterId)
    {
        foreach (var chapter in chapterList.OrderBy(chapter => chapter.Order))
        {
            var beatCount = await entities.CountChildrenAsync(chapter.ProjectId, chapter.Id, EntityTypeService.EventNodeType);
            sb.Append("  Chapter ").Append(chapter.Order + 1).Append(": ").Append(chapter.Title);
            if (chapter.Id == currentChapterId)
                sb.Append(" (current)");
            if (beatCount > 0)
                sb.Append(" - ").Append(beatCount).Append(" beat").Append(beatCount == 1 ? string.Empty : "s");
            sb.AppendLine();
            AppendOptionalIndented(sb, "Synopsis", chapter.Synopsis, 4);
        }
    }

    private static void AppendOptionalIndented(StringBuilder sb, string label, string value, int spaces)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        sb.Append(' ', spaces).Append(label).Append(": ").AppendLine(value.Trim());
    }

    private static bool IsIncluded(
        IReadOnlyDictionary<string, EditorContextPreference> preferenceMap,
        ContextItemKind kind,
        string key,
        bool defaultIncluded) =>
        preferenceMap.TryGetValue(PreferenceKey(kind.ToString(), key), out var preference)
            ? preference.IsIncluded
            : defaultIncluded;

    private static string PreferenceKey(string kind, string key) => kind + "|" + key;

    private static bool IsContextEntityType(string type) =>
        !string.Equals(type, EntityTypeService.ProjectNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.ActNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.ChapterNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.ProjectFactNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.SourceNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.SourceChunkNodeType, StringComparison.OrdinalIgnoreCase);
}
