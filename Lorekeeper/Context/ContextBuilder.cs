using System.Text;
using Lorekeeper.ChapterVisuals;
using Lorekeeper.Chapters;
using Lorekeeper.EntityVisuals;
using Lorekeeper.Images;
using Lorekeeper.Ingest;
using Lorekeeper.Llm;
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
    IEntityService entities,
    IIngestRepository ingest,
    IProjectImageService images,
    IEntityVisualExampleService entityVisualExamples,
    IChapterVisualService chapterVisuals) : IEditorContextService
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
                Key: EditorContextKeys.AssistantWorkflow,
                Kind: ContextItemKind.AssistantWorkflow,
                Label: "Assistant Workflow",
                Body: AssistantWorkflowInstructions.EditorChat,
                IsEnabled: true,
                IsRemovable: false,
                Badge: "App"),
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
                Body: BuildCurrentChapterBlock(currentChapter),
                IsEnabled: IsIncluded(preferenceMap, ContextItemKind.CurrentChapter, EditorContextKeys.CurrentChapter, defaultIncluded: true),
                IsRemovable: true));

            var visualItem = await BuildChapterVisualLayoutItemAsync(project.Id, currentChapter.Id, preferenceMap, cancellationToken);
            if (visualItem is not null)
                items.Add(visualItem);

            var structuralReferenceKeysToSkip = new HashSet<string>(StringComparer.Ordinal);
            var previousChapterItem = await BuildPreviousChapterReferenceItemAsync(project.Id, currentChapter.Id, preferenceMap, cancellationToken);
            if (previousChapterItem is not null)
            {
                items.Add(previousChapterItem);
                structuralReferenceKeysToSkip.Add(previousChapterItem.Key);
            }

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

            foreach (var item in await ListStructuralReferenceItemsAsync(project.Id, currentChapter.Id, preferenceMap, structuralReferenceKeysToSkip, cancellationToken))
            {
                items.Add(item);
            }

            var contextEntities = await ListContextEntitiesAsync(project.Id, currentChapter.Id, preferenceMap, cancellationToken);
            var visualsByEntity = await entityVisualExamples.ListForEntitiesAsync(project.Id, contextEntities.Select(entity => entity.Id).ToList(), cancellationToken);
            foreach (var entity in contextEntities)
            {
                var key = EditorContextKeys.Entity(entity.Id);
                var examples = visualsByEntity.GetValueOrDefault(entity.Id) ?? [];
                items.Add(new ContextItem(
                    Key: key,
                    Kind: ContextItemKind.Entity,
                    Label: $"{entity.Type} — {entity.Name}",
                    Body: await BuildEntityBlockAsync(project.Id, entity, examples, cancellationToken),
                    IsEnabled: true,
                    IsRemovable: true,
                    Badge: entity.Type,
                    Reason: entity.ParentId == currentChapter.Id ? "Chapter beat" : "Related to current chapter context",
                    Visuals: examples.Select(EntityVisualContextService.ToReference).ToList()));
            }
        }

        return new ContextAssembly(items);
    }

    public async Task<ContextAssembly> BuildProjectAsync(
        Project project,
        string assistantWorkflow,
        CancellationToken cancellationToken = default)
    {
        var items = new List<ContextItem>
        {
            new(
                Key: EditorContextKeys.SystemPrompt,
                Kind: ContextItemKind.SystemPrompt,
                Label: "Project Guidance",
                Body: project.SystemPrompt,
                IsEnabled: true,
                IsRemovable: false),
            new(
                Key: EditorContextKeys.ProjectOutline,
                Kind: ContextItemKind.ProjectOutline,
                Label: "Outline Structure and Synopses",
                Body: await BuildOutlineBlockAsync(project.Id, Guid.Empty, cancellationToken),
                IsEnabled: true,
                IsRemovable: false),
            new(
                Key: EditorContextKeys.ProjectFacts,
                Kind: ContextItemKind.ProjectFacts,
                Label: "Project Facts",
                Body: await BuildProjectFactsBlockAsync(project.Id, cancellationToken),
                IsEnabled: true,
                IsRemovable: false),
            new(
                Key: EditorContextKeys.AssistantWorkflow,
                Kind: ContextItemKind.AssistantWorkflow,
                Label: "Assistant Workflow",
                Body: assistantWorkflow,
                IsEnabled: true,
                IsRemovable: false),
        };

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

    public async Task<IReadOnlyCollection<string>> ListIncludedContextKeysAsync(
        Guid projectId,
        Guid chapterId,
        CancellationToken cancellationToken = default)
    {
        var preferenceMap = (await preferences.ListForChapterAsync(projectId, chapterId, cancellationToken))
            .ToDictionary(preference => PreferenceKey(preference.Kind, preference.Key), StringComparer.Ordinal);
        var keys = preferenceMap.Values
            .Where(preference => preference.IsIncluded)
            .Select(preference => preference.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entity in await ListContextEntitiesAsync(projectId, chapterId, preferenceMap, cancellationToken))
            keys.Add(EditorContextKeys.Entity(entity.Id));

        keys.Add(EditorContextKeys.CurrentChapter);
        keys.Add(EditorContextKeys.ProjectOutline);
        keys.Add(EditorContextKeys.ProjectFacts);
        var previousChapter = await FindPreviousChapterAsync(projectId, chapterId, cancellationToken);
        if (previousChapter is not null)
        {
            var previousChapterKey = EditorContextKeys.ChapterReference(previousChapter.Id);
            if (IsIncluded(preferenceMap, ContextItemKind.ChapterReference, previousChapterKey, defaultIncluded: true))
                keys.Add(previousChapterKey);
        }

        return keys;
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
            if (link.IsAutoLink) continue;
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

    private async Task<IReadOnlyList<ContextItem>> ListStructuralReferenceItemsAsync(
        Guid projectId,
        Guid currentChapterId,
        IReadOnlyDictionary<string, EditorContextPreference> preferenceMap,
        IReadOnlySet<string> keysToSkip,
        CancellationToken cancellationToken)
    {
        var items = new List<ContextItem>();
        foreach (var preference in preferenceMap.Values.Where(preference => preference.IsIncluded))
        {
            if (keysToSkip.Contains(preference.Key)) continue;

            if (string.Equals(preference.Kind, ContextItemKind.ChapterReference.ToString(), StringComparison.Ordinal)
                && EditorContextKeys.TryParseChapterReference(preference.Key, out var chapterId))
            {
                var item = await BuildChapterReferenceItemAsync(projectId, currentChapterId, chapterId, cancellationToken);
                if (item is not null) items.Add(item);
            }
            else if (string.Equals(preference.Kind, ContextItemKind.ActReference.ToString(), StringComparison.Ordinal)
                && EditorContextKeys.TryParseActReference(preference.Key, out var actId))
            {
                var item = await BuildActReferenceItemAsync(projectId, actId, cancellationToken);
                if (item is not null) items.Add(item);
            }
            else if (string.Equals(preference.Kind, ContextItemKind.IngestSourceReference.ToString(), StringComparison.Ordinal)
                && EditorContextKeys.TryParseIngestSourceReference(preference.Key, out var sourceId))
            {
                var item = await BuildIngestSourceReferenceItemAsync(projectId, sourceId, cancellationToken);
                if (item is not null) items.Add(item);
            }
            else if (string.Equals(preference.Kind, ContextItemKind.IngestSourceChunkReference.ToString(), StringComparison.Ordinal)
                && EditorContextKeys.TryParseIngestSourceChunkReference(preference.Key, out var sourceChunkId))
            {
                var item = await BuildIngestSourceChunkReferenceItemAsync(projectId, sourceChunkId, cancellationToken);
                if (item is not null) items.Add(item);
            }
            else if (string.Equals(preference.Kind, ContextItemKind.ProjectImage.ToString(), StringComparison.Ordinal)
                && EditorContextKeys.TryParseProjectImage(preference.Key, out var imageId))
            {
                var item = await BuildProjectImageItemAsync(projectId, imageId, cancellationToken);
                if (item is not null) items.Add(item);
            }
        }

        return items;
    }

    private static string BuildCurrentChapterBlock(Chapter chapter)
    {
        var body = new StringBuilder();
        body.Append("Chapter id: ").AppendLine(chapter.Id.ToString());
        body.Append("Title: ").AppendLine(chapter.Title);
        body.Append("Active visual mode: ").AppendLine(chapter.VisualMode.ToString());
        body.Append("Editing contract: ").AppendLine(chapter.VisualMode switch
        {
            ChapterVisualMode.Prose => "This is a prose chapter. Edit body text with edit_chapter. It has no active visual layout; do not generate Picture Page art or change its mode unless the user explicitly requests that.",
            ChapterVisualMode.IllustratedProse => "Edit body text with edit_chapter. Images, when requested, use anchored Illustrated Prose layout tools.",
            ChapterVisualMode.PicturePage => "Text is managed through Picture Page text boxes and visual layout tools, not edit_chapter.",
            _ => "Respect the active visual mode before choosing editing tools.",
        });
        body.AppendLine("Body (line-numbered):");
        body.Append(string.IsNullOrWhiteSpace(chapter.Body)
            ? "(empty)"
            : ChapterFormatting.WithLineNumbers(chapter.Body));
        return body.ToString();
    }

    private async Task<ContextItem?> BuildChapterVisualLayoutItemAsync(
        Guid projectId,
        Guid chapterId,
        IReadOnlyDictionary<string, EditorContextPreference> preferenceMap,
        CancellationToken cancellationToken)
    {
        var state = await chapterVisuals.GetAsync(chapterId, cancellationToken);
        if (state is null) return null;

        var hasVisuals = state.VisualMode == ChapterVisualMode.PicturePage
            || state.VisualMode == ChapterVisualMode.IllustratedProse
                && state.IllustrationLayout.Images.Count > 0;
        if (!hasVisuals) return null;

        var imageNames = (await images.ListAsync(projectId, cancellationToken))
            .ToDictionary(image => image.Id, image => image.FileName);
        var key = EditorContextKeys.ChapterVisualLayout(chapterId);
        return new ContextItem(
            Key: key,
            Kind: ContextItemKind.ChapterVisualLayout,
            Label: "Current Chapter Visual Layout",
            Body: chapterVisuals.BuildManifest(state, imageNames),
            IsEnabled: IsIncluded(preferenceMap, ContextItemKind.ChapterVisualLayout, key, defaultIncluded: true),
            IsRemovable: true,
            Badge: "Visual");
    }

    private async Task<ContextItem?> BuildProjectImageItemAsync(Guid projectId, Guid imageId, CancellationToken cancellationToken)
    {
        var image = await images.GetAsync(projectId, imageId, cancellationToken);
        if (image is null) return null;

        var body = new StringBuilder();
        body.Append("Filename: ").AppendLine(image.FileName);
        body.Append("Content type: ").AppendLine(image.ContentType);
        body.Append("Source: ").AppendLine(image.Source.ToString());
        body.Append("Size: ").Append(image.SizeBytes).AppendLine(" bytes");
        AppendOptionalIndented(body, "Alt text", image.AltText, 0);
        AppendOptionalIndented(body, "Prompt", image.Prompt, 0);
        AppendOptionalIndented(body, "Generation model", image.GenerationModel, 0);
        AppendOptionalIndented(body, "Source metadata JSON", image.SourceMetadataJson, 0);

        return new ContextItem(
            Key: EditorContextKeys.ProjectImage(image.Id),
            Kind: ContextItemKind.ProjectImage,
            Label: $"Image - {image.FileName}",
            Body: body.ToString().TrimEnd(),
            IsEnabled: true,
            IsRemovable: true,
            Badge: "Image",
            Reason: "Selected project image",
            Visuals:
            [
                new EntityVisualContextReference(
                    image.Id,
                    EntityId: null,
                    EntityType: string.Empty,
                    EntityName: string.Empty,
                    Label: string.IsNullOrWhiteSpace(image.AltText) ? image.FileName : image.AltText,
                    SortOrder: 0,
                    image.FileName,
                    image.AltText,
                    image.Prompt,
                    IsExplicitImage: true),
            ]);
    }

    private async Task<ContextItem?> BuildPreviousChapterReferenceItemAsync(
        Guid projectId,
        Guid currentChapterId,
        IReadOnlyDictionary<string, EditorContextPreference> preferenceMap,
        CancellationToken cancellationToken)
    {
        var previousChapter = await FindPreviousChapterAsync(projectId, currentChapterId, cancellationToken);
        if (previousChapter is null) return null;

        var key = EditorContextKeys.ChapterReference(previousChapter.Id);
        if (!IsIncluded(preferenceMap, ContextItemKind.ChapterReference, key, defaultIncluded: true))
            return null;

        var item = await BuildChapterReferenceItemAsync(projectId, currentChapterId, previousChapter.Id, cancellationToken);
        return item is null
            ? null
            : item with
            {
                Label = $"Previous Chapter - {previousChapter.Title}",
                Reason = "Previous chapter",
            };
    }

    private async Task<Chapter?> FindPreviousChapterAsync(
        Guid projectId,
        Guid currentChapterId,
        CancellationToken cancellationToken)
    {
        var actList = await acts.ListAsync(projectId, cancellationToken);
        var allChapters = await chapters.ListAsync(projectId, cancellationToken);
        var orderedChapters = new List<Chapter>();

        foreach (var act in actList.OrderBy(act => act.Order))
        {
            orderedChapters.AddRange(allChapters
                .Where(chapter => chapter.ActId == act.Id)
                .OrderBy(chapter => chapter.Order));
        }

        orderedChapters.AddRange(allChapters
            .Where(chapter => chapter.ActId is null)
            .OrderBy(chapter => chapter.Order));

        for (var i = 1; i < orderedChapters.Count; i++)
        {
            if (orderedChapters[i].Id == currentChapterId)
                return orderedChapters[i - 1];
        }

        return null;
    }

    private async Task<ContextItem?> BuildChapterReferenceItemAsync(
        Guid projectId,
        Guid currentChapterId,
        Guid chapterId,
        CancellationToken cancellationToken)
    {
        if (chapterId == currentChapterId) return null;

        var chapter = await chapters.GetAsync(chapterId, cancellationToken);
        if (chapter is null || chapter.ProjectId != projectId) return null;

        var body = new StringBuilder();
        body.Append("Title: ").AppendLine(chapter.Title);
        AppendOptionalIndented(body, "Synopsis", chapter.Synopsis, 0);
        body.Append("Visual mode: ").AppendLine(chapter.VisualMode.ToString());
        if (chapter.VisualMode != ChapterVisualMode.Prose)
            body.Append("Page layout: ").AppendLine(chapter.PageLayoutKind.ToString());
        body.AppendLine("Body (line-numbered):");
        body.AppendLine(string.IsNullOrWhiteSpace(chapter.Body) ? "(empty)" : ChapterFormatting.WithLineNumbers(chapter.Body));

        return new ContextItem(
            Key: EditorContextKeys.ChapterReference(chapter.Id),
            Kind: ContextItemKind.ChapterReference,
            Label: $"Chapter Reference — {chapter.Title}",
            Body: body.ToString().TrimEnd(),
            IsEnabled: true,
            IsRemovable: true,
            Badge: "Chapter",
            Reason: "Selected chapter reference");
    }

    private async Task<ContextItem?> BuildActReferenceItemAsync(Guid projectId, Guid actId, CancellationToken cancellationToken)
    {
        var act = await acts.GetAsync(actId, cancellationToken);
        if (act is null || act.ProjectId != projectId) return null;

        var actChapters = (await chapters.ListAsync(projectId, cancellationToken))
            .Where(chapter => chapter.ActId == act.Id)
            .OrderBy(chapter => chapter.Order)
            .ToList();

        var body = new StringBuilder();
        body.Append("Title: ").AppendLine(act.Title);
        AppendOptionalIndented(body, "Synopsis", act.Synopsis, 0);
        if (actChapters.Count > 0)
        {
            body.AppendLine("Chapters:");
            foreach (var chapter in actChapters)
            {
                body.Append("- Chapter ").Append(chapter.Order + 1).Append(": ").AppendLine(chapter.Title);
                AppendOptionalIndented(body, "Synopsis", chapter.Synopsis, 2);
            }
        }

        return new ContextItem(
            Key: EditorContextKeys.ActReference(act.Id),
            Kind: ContextItemKind.ActReference,
            Label: $"Act Reference — {act.Title}",
            Body: body.ToString().TrimEnd(),
            IsEnabled: true,
            IsRemovable: true,
            Badge: "Act",
            Reason: "Selected act reference");
    }

    private async Task<ContextItem?> BuildIngestSourceReferenceItemAsync(Guid projectId, Guid sourceId, CancellationToken cancellationToken)
    {
        var source = await ingest.GetSourceAsync(sourceId, cancellationToken);
        if (source is null || source.ProjectId != projectId) return null;

        var sourceChunks = await ingest.ListSourceChunksAsync(source.Id, cancellationToken);
        var body = new StringBuilder();
        body.Append("Title: ").AppendLine(source.Title);
        AppendOptionalIndented(body, "Kind", source.SourceKind, 0);
        AppendOptionalIndented(body, "Description", source.Description, 0);
        AppendOptionalIndented(body, "Synopsis", source.Synopsis, 0);
        if (sourceChunks.Count > 0)
        {
            body.AppendLine("Source chunks:");
            foreach (var chunk in sourceChunks)
            {
                body.Append("- Part ").Append(chunk.Index + 1).Append(": ").AppendLine(chunk.Title);
                AppendOptionalIndented(body, "Heading", chunk.HeadingPath, 2);
                AppendOptionalIndented(body, "Summary", chunk.Summary, 2);
                AppendOptionalIndented(body, "Notes", chunk.AgentNotes, 2);
            }
        }

        return new ContextItem(
            Key: EditorContextKeys.IngestSourceReference(source.Id),
            Kind: ContextItemKind.IngestSourceReference,
            Label: $"Source Reference — {source.Title}",
            Body: body.ToString().TrimEnd(),
            IsEnabled: true,
            IsRemovable: true,
            Badge: "Source",
            Reason: "Selected source reference");
    }

    private async Task<ContextItem?> BuildIngestSourceChunkReferenceItemAsync(Guid projectId, Guid sourceChunkId, CancellationToken cancellationToken)
    {
        var sourceChunk = await ingest.GetSourceChunkAsync(sourceChunkId, cancellationToken);
        if (sourceChunk is null) return null;

        var source = await ingest.GetSourceAsync(sourceChunk.SourceId, cancellationToken);
        if (source is null || source.ProjectId != projectId) return null;

        var excerpt = await ingest.GetSourceChunkExcerptAsync(sourceChunk.Id, maxChars: 8_000, cancellationToken);
        var body = new StringBuilder();
        body.Append("Source: ").AppendLine(source.Title);
        body.Append("Part: ").Append(sourceChunk.Index + 1).AppendLine();
        AppendOptionalIndented(body, "Title", sourceChunk.Title, 0);
        AppendOptionalIndented(body, "Heading", sourceChunk.HeadingPath, 0);
        AppendOptionalIndented(body, "Summary", sourceChunk.Summary, 0);
        AppendOptionalIndented(body, "Notes", sourceChunk.AgentNotes, 0);
        if (excerpt is not null)
        {
            body.Append(excerpt.IsTruncated ? "Excerpt (truncated):" : "Excerpt:").AppendLine();
            body.AppendLine(excerpt.Text);
        }

        return new ContextItem(
            Key: EditorContextKeys.IngestSourceChunkReference(sourceChunk.Id),
            Kind: ContextItemKind.IngestSourceChunkReference,
            Label: $"Source Chunk Reference — {source.Title} Part {sourceChunk.Index + 1}",
            Body: body.ToString().TrimEnd(),
            IsEnabled: true,
            IsRemovable: true,
            Badge: "Source chunk",
            Reason: "Selected source chunk reference");
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
            sb.Append("Act ").Append(act.Order + 1).Append(": ").Append(act.Title)
              .Append(" [id: ").Append(act.Id).AppendLine("]");
            AppendOptionalIndented(sb, "Synopsis", act.Synopsis, 2);
            await AppendChaptersAsync(sb, byAct.TryGetValue(act.Id, out var chaptersInAct) ? chaptersInAct : [], currentChapterId, cancellationToken);
            sb.AppendLine();
        }

        if (unassigned.Count > 0)
        {
            sb.AppendLine("Unassigned Chapters [actId: unassigned]");
            await AppendChaptersAsync(sb, unassigned, currentChapterId, cancellationToken);
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

    private async Task<string> BuildEntityBlockAsync(
        Guid projectId,
        StoryEntity entity,
        IReadOnlyList<EntityVisualExampleView> visualExamples,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.Append("Id: ").AppendLine(entity.Id.ToString("N"));
        sb.Append("Type: ").AppendLine(entity.Type);
        sb.Append("Name: ").AppendLine(entity.Name);
        AppendOptionalIndented(sb, "Summary", entity.Summary, 0);

        if (entity.Properties.Count > 0)
        {
            sb.AppendLine("Properties:");
            foreach (var property in entity.Properties.OrderBy(property => property.Key, StringComparer.OrdinalIgnoreCase))
                sb.Append("- ").Append(property.Key).Append(": ").AppendLine(property.Value ?? string.Empty);
        }

        if (entity.Aliases.Count > 0)
        {
            sb.AppendLine("Aliases:");
            sb.Append("- ").AppendLine(string.Join(", ", entity.Aliases));
        }

        if (entity.WikiSections.Count > 0)
        {
            sb.AppendLine("Wiki sheet:");
            foreach (var section in entity.WikiSections.Take(8))
            {
                sb.Append("- ").Append(section.Title).Append(": ").AppendLine(section.Body);
                foreach (var citation in section.Citations.Take(2))
                {
                    sb.Append("  Source: ").Append(citation.SourceTitle).Append(" chunk ").Append(citation.SourceChunkIndex + 1).AppendLine();
                }
            }
        }

        if (entity.CanonSources.Count > 0)
        {
            sb.AppendLine("Canon sources:");
            foreach (var source in entity.CanonSources.Take(6))
            {
                sb.Append("# ").Append(source.SourceTitle).AppendLine();
                sb.AppendLine(source.Markdown);
            }
        }

        if (visualExamples.Count > 0)
        {
            sb.AppendLine("Visual examples:");
            foreach (var example in visualExamples.OrderBy(example => example.SortOrder))
            {
                sb.Append("- ").Append(example.Label)
                  .Append(" [imageId: ").Append(example.Image.Id.ToString("N"))
                  .Append("; file: ").Append(example.Image.FileName).AppendLine("]");
                AppendOptionalIndented(sb, "Alt text", example.Image.AltText, 2);
                AppendOptionalIndented(sb, "Generation prompt", example.Image.Prompt, 2);
            }
        }

        var links = await entities.ListLinksAsync(projectId, entity.Id, cancellationToken);
        var visibleLinks = links
            .Where(link => IsContextEntityType(link.OtherEntityType) || string.Equals(link.OtherEntityType, EntityTypeService.ChapterNodeType, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (visibleLinks.Count > 0)
        {
            var manualLinks = visibleLinks.Where(link => !link.IsAutoLink).ToList();
            var autoLinks = visibleLinks.Where(link => link.IsAutoLink).ToList();
            if (manualLinks.Count > 0)
            {
                sb.AppendLine("Links:");
                foreach (var link in manualLinks)
                    AppendLinkLine(sb, link);
            }
            if (autoLinks.Count > 0)
            {
                sb.AppendLine("Auto mention links (weak discovery hints):");
                foreach (var link in autoLinks.Take(12))
                    AppendLinkLine(sb, link);
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendLinkLine(StringBuilder sb, EntityLink link)
    {
        var direction = link.Direction == EntityLinkDirection.Outgoing ? "->" : "<-";
        sb.Append("- ").Append(direction).Append(' ').Append(link.EdgeType).Append(' ')
          .Append(link.OtherEntityName).Append(" (").Append(link.OtherEntityType).AppendLine(")");
    }

    private async Task AppendChaptersAsync(
        StringBuilder sb,
        IReadOnlyList<Chapter> chapterList,
        Guid currentChapterId,
        CancellationToken cancellationToken)
    {
        foreach (var chapter in chapterList.OrderBy(chapter => chapter.Order))
        {
            var beats = await entities.ListAsync(chapter.ProjectId, EntityTypeService.EventNodeType, chapter.Id, cancellationToken);
            sb.Append("  Chapter ").Append(chapter.Order + 1).Append(": ").Append(chapter.Title);
            sb.Append(" [id: ").Append(chapter.Id).Append(']');
            sb.Append(" [visual: ").Append(chapter.VisualMode);
            if (chapter.VisualMode != ChapterVisualMode.Prose)
                sb.Append("; pageLayout: ").Append(chapter.PageLayoutKind);
            sb.Append(']');
            if (chapter.Id == currentChapterId)
                sb.Append(" (current)");
            sb.AppendLine();
            AppendOptionalIndented(sb, "Synopsis", chapter.Synopsis, 4);
            AppendBeats(sb, beats);
        }
    }

    private static void AppendBeats(StringBuilder sb, IReadOnlyList<StoryEntity> beats)
    {
        if (beats.Count == 0) return;

        sb.Append(' ', 4).AppendLine("Beats:");
        for (var i = 0; i < beats.Count; i++)
        {
            var beat = beats[i];
            sb.Append(' ', 6).Append("Beat ").Append(i + 1).Append(": ").Append(beat.Name)
              .Append(" [id: ").Append(beat.Id).AppendLine("]");
            if (beat.Properties.TryGetValue("summary", out var summary))
                AppendOptionalIndented(sb, "Summary", summary ?? string.Empty, 8);
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
        && !string.Equals(type, EntityTypeService.SourceChunkNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.SourceBlockNodeType, StringComparison.OrdinalIgnoreCase);
}
