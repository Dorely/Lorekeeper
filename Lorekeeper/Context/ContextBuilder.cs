using System.Text;
using Lorekeeper.Chapters;
using Lorekeeper.Composition;
using Lorekeeper.EntityVisuals;
using Lorekeeper.Images;
using Lorekeeper.Ingest;
using Lorekeeper.Llm;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence;
using Lorekeeper.Persistence.Repositories;
using Lorekeeper.Projects;
using Lorekeeper.Search;
using Lorekeeper.Tokens;
using Lorekeeper.Writing;

namespace Lorekeeper.Context;

public sealed class ContextBuilder(
IAppDatabaseOperationFactory database, IActService acts, IChapterService chapters, IProjectFactService projectFacts, IWritingSampleService writingSamples, IEntityService entities, IProjectImageService images, IEntityVisualExampleService entityVisualExamples, IManuscriptService manuscripts, IChapterSemanticProjectionService semanticProjection, IManuscriptStyleService manuscriptStyles, ICompositionService compositions, IProjectPageSetupService pageSetups, IEmbeddingService embeddings, IBookBriefService bookBriefs, ISystemPromptComposer systemPrompts, IProjectSearchService projectSearch, ITokenCounter tokenCounter) : IEditorContextService
{
    public async Task<ContextAssembly> BuildAsync(
        ContextBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var preferences = databaseOperation.Repositories.EditorContextPreferences;
        ArgumentNullException.ThrowIfNull(request);
        var project = request.Project;
        var currentChapter = request.ActiveChapter;
        var preferenceMap = currentChapter is null
            ? new Dictionary<string, EditorContextPreference>(StringComparer.Ordinal)
            : (await preferences.ListForChapterAsync(project.Id, currentChapter.Id, cancellationToken))
                .ToDictionary(preference => PreferenceKey(preference.Kind, preference.Key), StringComparer.Ordinal);

        var assistantWorkflow = request.OperatingRules
            ?? await ResolveOperatingRulesAsync(request.Purpose, project, cancellationToken);
        var brief = await bookBriefs.GetOrCreateAsync(project.Id, cancellationToken);
        var composition = systemPrompts.Compose(new(
            project,
            brief,
            RoleFor(request.Purpose),
            assistantWorkflow,
            currentChapter));
        var items = composition.Sections.Select(ToContextItem).ToList();

        if (request.Purpose is ContextBuildPurpose.Images or ContextBuildPurpose.Research or ContextBuildPurpose.Publish)
        {
            items.Add(new ContextItem(
                Key: EditorContextKeys.ProjectOutline,
                Kind: ContextItemKind.ProjectOutline,
                Label: "Outline Structure and Synopses",
                Body: await BuildOutlineBlockAsync(project.Id, Guid.Empty, cancellationToken),
                IsEnabled: true,
                IsRemovable: false,
                IsProtected: true));
            items.Add(new ContextItem(
                Key: EditorContextKeys.ProjectFacts,
                Kind: ContextItemKind.ProjectFacts,
                Label: "Project Facts",
                Body: await BuildProjectFactsBlockAsync(project.Id, cancellationToken),
                IsEnabled: true,
                IsRemovable: false,
                IsProtected: true));
        }

        if (currentChapter is not null
            && request.Purpose is ContextBuildPurpose.Editor or ContextBuildPurpose.EditorRevision)
        {
            var manuscriptSnapshot = await manuscripts.GetManuscriptAsync(request.ContentTarget, currentChapter.Id, cancellationToken);
            var pageSetup = await pageSetups.GetOrCreateAsync(project.Id, cancellationToken);
            items.Add(new ContextItem(
                Key: EditorContextKeys.ProjectPageSetup,
                Kind: ContextItemKind.ProjectPageSetup,
                Label: "Project Page Setup",
                Body: $"Revision: {pageSetup.Revision}\nPage: {pageSetup.PageWidthInches:0.####} × {pageSetup.PageHeightInches:0.####} in\nAspect: {pageSetup.PageWidthInches / pageSetup.PageHeightInches:0.######}\nMargin: {pageSetup.PageMarginInches:0.####} in\nBody type: {pageSetup.BodyFontSizePoints:0.##} pt at {pageSetup.BodyLineHeight:0.###} line height",
                IsEnabled: true,
                IsRemovable: false,
                Badge: "Geometry",
                Reason: "Authoring geometry for Figures and Designed Pages",
                IsProtected: true));
            items.Add(await BuildManuscriptStylesItemAsync(
                project.Id,
                manuscriptSnapshot,
                cancellationToken));
            items.Add(new ContextItem(
                Key: EditorContextKeys.CurrentChapter,
                Kind: ContextItemKind.CurrentChapter,
                Label: $"Current Chapter — {currentChapter.Title} (editable manuscript snapshot)",
                Body: ContextManuscriptFormatter.SerializeCurrentChapter(currentChapter, manuscriptSnapshot),
                IsEnabled: IsIncluded(preferenceMap, ContextItemKind.CurrentChapter, EditorContextKeys.CurrentChapter, defaultIncluded: true),
                IsRemovable: true,
                IsProtected: true));

            var visualItem = await BuildChapterVisualLayoutItemAsync(
                project.Id,
                currentChapter.Id,
                manuscriptSnapshot,
                preferenceMap,
                cancellationToken);
            if (visualItem is not null)
                items.Add(visualItem with { IsProtected = true });

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

            var projectWritingSamples = await writingSamples.ListAsync(project.Id, cancellationToken);
            var relevantWritingSampleIds = RelevantWritingSampleIds(projectWritingSamples, request.UserMessage);
            foreach (var sample in projectWritingSamples)
            {
                var key = EditorContextKeys.WritingSample(sample.Id);
                items.Add(new ContextItem(
                    Key: key,
                    Kind: ContextItemKind.WritingSample,
                    Label: $"Writing Sample — {sample.Title}",
                    Body: BuildWritingSampleBlock(sample),
                    IsEnabled: IsIncluded(
                        preferenceMap,
                        ContextItemKind.WritingSample,
                        key,
                        defaultIncluded: relevantWritingSampleIds.Contains(sample.Id)),
                    IsRemovable: true,
                    Badge: "Style",
                    Reason: relevantWritingSampleIds.Contains(sample.Id)
                        ? "Writing style matched to this turn"
                        : "Available writing sample",
                    Origin: ContextItemOrigin.WritingSample,
                    IsProtected: IsExplicitlyIncluded(preferenceMap, ContextItemKind.WritingSample, key)));
            }

            foreach (var item in await ListStructuralReferenceItemsAsync(project.Id, currentChapter.Id, preferenceMap, structuralReferenceKeysToSkip, cancellationToken))
            {
                items.Add(item with
                {
                    Origin = ContextItemOrigin.DirectLink,
                    IsProtected = true,
                });
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
                    Visuals: examples.Select(EntityVisualContextService.ToReference).ToList(),
                    Origin: ContextItemOrigin.DirectLink,
                    IsProtected: entity.ParentId == currentChapter.Id
                        || IsExplicitlyIncluded(preferenceMap, ContextItemKind.Entity, key)));
            }

            items.AddRange(await BuildTurnRetrievalItemsAsync(
                project.Id,
                currentChapter.Id,
                request.UserMessage,
                preferenceMap,
                items,
                cancellationToken));
        }

        return new ContextAssembly(items.Select(AddTokenEstimate).ToList());
    }

    private async Task<string> ResolveOperatingRulesAsync(
        ContextBuildPurpose purpose,
        Project project,
        CancellationToken cancellationToken)
    {
        return purpose switch
        {
            ContextBuildPurpose.Editor when project.ContestModeEnabled =>
                AssistantWorkflowInstructions.EditorContestPreparationWorkflow,
            ContextBuildPurpose.Editor =>
                AssistantWorkflowInstructions.EditorChatFor(await embeddings.IsAvailableAsync(cancellationToken)),
            ContextBuildPurpose.EditorRevision => AssistantWorkflowInstructions.EditorRevisionWorker,
            ContextBuildPurpose.Images => AssistantWorkflowInstructions.VisualCreationWorkflow,
            ContextBuildPurpose.Research =>
                "Use the supplied research tools to gather, attribute, compare, and synthesize evidence. Distinguish sourced facts from editorial inference and never fabricate a source.",
            ContextBuildPurpose.Publish =>
                "Use the supplied publication tools to inspect and prepare publication artifacts. Report validation results accurately and never claim vendor acceptance.",
            _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
        };
    }

    private static SystemPromptAgentRole RoleFor(ContextBuildPurpose purpose) => purpose switch
    {
        ContextBuildPurpose.Editor => SystemPromptAgentRole.Editor,
        ContextBuildPurpose.EditorRevision => SystemPromptAgentRole.RevisionWorker,
        ContextBuildPurpose.Images => SystemPromptAgentRole.Images,
        ContextBuildPurpose.Research => SystemPromptAgentRole.Research,
        ContextBuildPurpose.Publish => SystemPromptAgentRole.Publish,
        _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
    };

    private static ContextItem ToContextItem(SystemPromptSection section)
    {
        var kind = section.Kind switch
        {
            SystemPromptSectionKind.ProfessionalIdentity => ContextItemKind.SystemInstructions,
            SystemPromptSectionKind.OperatingRules => ContextItemKind.AssistantWorkflow,
            SystemPromptSectionKind.DynamicGuidance => ContextItemKind.DynamicGuidance,
            SystemPromptSectionKind.ProjectGuidance => ContextItemKind.ProjectGuidance,
            SystemPromptSectionKind.BookBrief => ContextItemKind.BookBrief,
            _ => throw new ArgumentOutOfRangeException(nameof(section)),
        };
        var key = section.Kind switch
        {
            SystemPromptSectionKind.ProfessionalIdentity => EditorContextKeys.SystemInstructions,
            SystemPromptSectionKind.OperatingRules => EditorContextKeys.AssistantWorkflow,
            SystemPromptSectionKind.DynamicGuidance => EditorContextKeys.DynamicGuidance,
            SystemPromptSectionKind.ProjectGuidance => EditorContextKeys.ProjectGuidance,
            SystemPromptSectionKind.BookBrief => EditorContextKeys.BookBrief,
            _ => section.Key,
        };

        return new ContextItem(
            Key: key,
            Kind: kind,
            Label: section.Label,
            Body: section.Body,
            IsEnabled: true,
            IsRemovable: false,
            Badge: section.IsUserOwned ? "Author" : "App",
            Origin: section.IsUserOwned ? ContextItemOrigin.User : ContextItemOrigin.Application,
            IsProtected: true);
    }

    private ContextItem AddTokenEstimate(ContextItem item) =>
        item.EstimatedTokens > 0
            ? item
            : item with { EstimatedTokens = Math.Max(1, tokenCounter.Count(item.Body).TokenCount) };

    public async Task SetItemIncludedAsync(
        Guid projectId,
        Guid chapterId,
        ContextItemKind kind,
        string key,
        bool isIncluded,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var preferences = databaseOperation.Repositories.EditorContextPreferences;
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

        await databaseOperation.SaveChangesAsync(cancellationToken);
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
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var preferences = databaseOperation.Repositories.EditorContextPreferences;
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
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var preferences = databaseOperation.Repositories.EditorContextPreferences;
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

    private async Task<ContextItem> BuildManuscriptStylesItemAsync(
        Guid projectId,
        ManuscriptSnapshot? manuscriptSnapshot,
        CancellationToken cancellationToken)
    {
        var styles = await manuscriptStyles.ListAsync(projectId, cancellationToken);
        return new ContextItem(
            Key: EditorContextKeys.ManuscriptStyles,
            Kind: ContextItemKind.ManuscriptStyles,
            Label: "Book Text Styles and Active Formatting",
            Body: ContextManuscriptFormatter.SerializeStyles(styles, manuscriptSnapshot?.Document),
            IsEnabled: true,
            IsRemovable: false,
            Badge: "Style",
            Reason: "Named styles and active manuscript formatting",
            IsProtected: true);
    }

    private async Task<ContextItem?> BuildChapterVisualLayoutItemAsync(
        Guid projectId,
        Guid chapterId,
        ManuscriptSnapshot? snapshot,
        IReadOnlyDictionary<string, EditorContextPreference> preferenceMap,
        CancellationToken cancellationToken)
    {
        if (snapshot is null) return null;
        var visualBlocks = snapshot.Document.Content
            .Where(block => block.Type is ManuscriptBlockType.Figure or ManuscriptBlockType.DesignedPage)
            .ToList();
        if (visualBlocks.Count == 0) return null;
        var manifest = new StringBuilder();
        manifest.Append("Manuscript revision: ").AppendLine(snapshot.Revision.ToString());
        manifest.Append("Figures: ").AppendLine(visualBlocks.Count(block => block.Type == ManuscriptBlockType.Figure).ToString());
        manifest.Append("Designed Pages: ").AppendLine(visualBlocks.Count(block => block.Type == ManuscriptBlockType.DesignedPage).ToString());
        foreach (var block in visualBlocks.Take(24))
        {
            manifest.Append("- ").Append(block.Type).Append(" block ").Append(block.Id);
            if (block.ImageId is { } imageId) manifest.Append(" image=").Append(imageId);
            if (block.PageCompositionId is { } compositionId)
            {
                var composition = await compositions.GetAsync(projectId, compositionId, cancellationToken);
                manifest.Append(" composition=").Append(compositionId)
                    .Append(" variants=").Append(composition?.Variants.Count ?? 0);
                if (composition is not null)
                {
                    manifest.Append(" compositionRevision=").Append(composition.Revision)
                        .Append(" activeAuthoringVariant=").Append(composition.ActiveAuthoringVariantId?.ToString() ?? "none");
                    if (composition.ActiveAuthoringVariantId is Guid activeVariantId)
                    {
                        var activeVariant = await compositions.ReadVariantAsync(projectId, activeVariantId, cancellationToken);
                        var scene = System.Text.Json.JsonSerializer.Deserialize<CompositionScene>(
                            activeVariant.SceneJson,
                            ManuscriptCodec.JsonOptions);
                        if (scene is not null)
                        {
                            manifest.AppendLine()
                                .Append("  active geometry: variant=").Append(activeVariant.Id)
                                .Append(" revision=").Append(activeVariant.Revision)
                                .Append(" mode=").Append(scene.Surface.Kind)
                                .Append(" surface=").Append((scene.Surface.WidthPoints / 72).ToString("0.####"))
                                .Append("×").Append((scene.Surface.HeightPoints / 72).ToString("0.####"))
                                .Append(" in aspect=").Append((scene.Surface.WidthPoints / scene.Surface.HeightPoints).ToString("0.######"))
                                .Append(" geometryKey=").Append(activeVariant.GeometryKey);
                        }
                    }
                    var semanticText = ManuscriptCodec.ProjectPlainText(
                        ManuscriptCodec.Deserialize(composition.SemanticManuscriptJson, composition.Id, composition.Revision));
                    if (!string.IsNullOrWhiteSpace(semanticText))
                        manifest.AppendLine().Append("  semantic reading order: ")
                            .Append(semanticText.Length <= 2_000 ? semanticText : semanticText[..2_000] + "…");
                }
            }
            if (block.Type == ManuscriptBlockType.Figure)
                manifest.Append(" placement=").Append(block.FigurePresentation?.Placement.ToString() ?? "Centered")
                    .Append(" accessibility=").Append(block.Decorative ? "decorative" : string.IsNullOrWhiteSpace(block.AltText) ? "missing-alt-decision" : "described");
            manifest.AppendLine();
        }
        var key = EditorContextKeys.ChapterVisualLayout(chapterId);
        return new ContextItem(
            Key: key,
            Kind: ContextItemKind.ChapterVisualLayout,
            Label: "Current Manuscript Visuals",
            Body: manifest.ToString(),
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
                    IsExplicitImage: true,
                    ImageSource: image.Source),
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
                Origin = ContextItemOrigin.PreviousChapter,
                IsProtected = true,
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

        var plainText = await semanticProjection.ExpandPlainTextAsync(chapter, cancellationToken);
        var body = new StringBuilder();
        body.Append("Title: ").AppendLine(chapter.Title);
        AppendOptionalIndented(body, "Synopsis", chapter.Synopsis, 0);
        body.AppendLine("Body (line-numbered):");
        body.AppendLine(string.IsNullOrWhiteSpace(plainText) ? "(empty)" : ChapterFormatting.WithLineNumbers(plainText));

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
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var ingest = databaseOperation.Repositories.Ingest;
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
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var ingest = databaseOperation.Repositories.Ingest;
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
        var links = await entities.ListLinksAsync(projectId, entity.Id, cancellationToken);
        return ContextEntityPayloadFormatter.Serialize(entity, visualExamples, links);
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

    private async Task<IReadOnlyList<ContextItem>> BuildTurnRetrievalItemsAsync(
        Guid projectId,
        Guid currentChapterId,
        string userMessage,
        IReadOnlyDictionary<string, EditorContextPreference> preferenceMap,
        IReadOnlyCollection<ContextItem> existingItems,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return [];

        ProjectSearchResponse response;
        try
        {
            response = await projectSearch.SearchAsync(
                new ProjectSearchRequest(projectId, userMessage.Trim(), TopK: 24),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Context search is opportunistic. Direct graph, outline, and lexical feed
            // construction above remain usable when a search index is unavailable.
            return [];
        }

        var existingKeys = existingItems.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        var results = new List<ContextItem>();
        foreach (var result in response.Results)
        {
            if (results.Count >= 8 || result.SourceId is not { } sourceId)
                break;
            if (!TryMapTurnResult(result.SourceType, sourceId, out var kind, out var key))
                continue;
            if (kind == ContextItemKind.ChapterReference && sourceId == currentChapterId)
                continue;
            if (existingKeys.Contains(key) || IsExplicitlyExcluded(preferenceMap, kind, key))
                continue;

            var reasons = result.Reasons.Count == 0
                ? "project search"
                : string.Join(" + ", result.Reasons);
            var origin = result.Reasons.Contains("semantic", StringComparer.OrdinalIgnoreCase)
                && !result.Reasons.Contains("keyword", StringComparer.OrdinalIgnoreCase)
                    ? ContextItemOrigin.VectorRetrieval
                    : ContextItemOrigin.LexicalRetrieval;
            var exactExcerpt = string.IsNullOrWhiteSpace(result.Content)
                ? result.Snippet
                : result.Content;
            var body = $$"""
                Source type: {{result.SourceType}}
                Source id: {{sourceId}}
                Retrieval reason: {{reasons}}

                Exact retrieved excerpt:
                {{exactExcerpt}}
                """;

            results.Add(new ContextItem(
                Key: key,
                Kind: kind,
                Label: $"This turn — {result.Title}",
                Body: body,
                IsEnabled: true,
                IsRemovable: true,
                Badge: "This turn",
                Reason: $"Matched this request through {reasons}",
                Origin: origin,
                IsTransient: true,
                IsProtected: IsExplicitlyIncluded(preferenceMap, kind, key)));
            existingKeys.Add(key);
        }

        return results;
    }

    private static bool TryMapTurnResult(
        string sourceType,
        Guid sourceId,
        out ContextItemKind kind,
        out string key)
    {
        var normalized = ProjectSearchSourceTypes.Normalize(sourceType);
        switch (normalized)
        {
            case ProjectSearchSourceTypes.Entity:
                kind = ContextItemKind.Entity;
                key = EditorContextKeys.Entity(sourceId);
                return true;
            case ProjectSearchSourceTypes.Chapter:
            case ProjectSearchSourceTypes.ContextChapter:
                kind = ContextItemKind.ChapterReference;
                key = EditorContextKeys.ChapterReference(sourceId);
                return true;
            case ProjectSearchSourceTypes.Act:
                kind = ContextItemKind.ActReference;
                key = EditorContextKeys.ActReference(sourceId);
                return true;
            case ProjectSearchSourceTypes.IngestSource:
            case ProjectSearchSourceTypes.RawIngestSource:
                kind = ContextItemKind.IngestSourceReference;
                key = EditorContextKeys.IngestSourceReference(sourceId);
                return true;
            case ProjectSearchSourceTypes.IngestSourceChunk:
                kind = ContextItemKind.IngestSourceChunkReference;
                key = EditorContextKeys.IngestSourceChunkReference(sourceId);
                return true;
            default:
                kind = default;
                key = string.Empty;
                return false;
        }
    }

    private static HashSet<Guid> RelevantWritingSampleIds(
        IReadOnlyList<WritingSample> samples,
        string userMessage)
    {
        if (samples.Count == 0 || string.IsNullOrWhiteSpace(userMessage))
            return [];

        var terms = userMessage
            .Split([' ', '\t', '\r', '\n', ',', '.', ';', ':', '!', '?'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(term => term.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var explicitlyStyleFocused = terms.Any(term => term.Equals("style", StringComparison.OrdinalIgnoreCase)
            || term.Equals("voice", StringComparison.OrdinalIgnoreCase)
            || term.Equals("tone", StringComparison.OrdinalIgnoreCase));

        var ranked = samples
            .Select(sample => new
            {
                sample.Id,
                Score = terms.Sum(term =>
                    (sample.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ? 8 : 0)
                    + (sample.Body.Contains(term, StringComparison.OrdinalIgnoreCase) ? 1 : 0)),
                sample.UpdatedAt,
            })
            .Where(item => item.Score > 0 || explicitlyStyleFocused)
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.UpdatedAt)
            .Take(explicitlyStyleFocused ? 2 : 3)
            .Select(item => item.Id)
            .ToHashSet();
        return ranked;
    }

    private static bool IsExplicitlyIncluded(
        IReadOnlyDictionary<string, EditorContextPreference> preferenceMap,
        ContextItemKind kind,
        string key) =>
        preferenceMap.TryGetValue(PreferenceKey(kind.ToString(), key), out var preference)
        && preference.IsIncluded;

    private static bool IsExplicitlyExcluded(
        IReadOnlyDictionary<string, EditorContextPreference> preferenceMap,
        ContextItemKind kind,
        string key) =>
        preferenceMap.TryGetValue(PreferenceKey(kind.ToString(), key), out var preference)
        && !preference.IsIncluded;

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
