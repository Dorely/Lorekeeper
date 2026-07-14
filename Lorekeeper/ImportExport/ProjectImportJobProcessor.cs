using System.Text.Json;
using System.Text.Json.Nodes;
using Lorekeeper.ChapterVisuals;
using Lorekeeper.Chapters;
using Lorekeeper.Context;
using Lorekeeper.EntityVisuals;
using Lorekeeper.Ingest;
using Lorekeeper.Knowledge;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence;
using Lorekeeper.Persistence.Repositories;
using Lorekeeper.Projects;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.ImportExport;

public sealed class ProjectImportJobProcessor(
    IProjectImportRepository imports,
    AppDbContext db,
    IProjectRepository projects,
    IGraphNodeRepository nodes,
    IGraphEdgeRepository edges,
    IGraphEntityTypeRepository entityTypes,
    IGraphStore graph,
    IActService acts,
    IChapterService chapters,
    IChapterRepository chapterRepo,
    IProjectFactService projectFacts,
    IEntityTypeService entityTypeService,
    IOutlineGraphSync outlineGraphSync,
    IContextIndexingService contextIndexing,
    IEntityVisualExampleService entityVisualExamples,
    IBookBriefService bookBriefs,
    IProjectImportJobNotifier notifier,
    ILogger<ProjectImportJobProcessor> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly HashSet<string> AppendStructuralNodeTypes =
    [
        EntityTypeService.ActNodeType,
        EntityTypeService.ChapterNodeType,
        EntityTypeService.EventNodeType,
    ];

    private static readonly HashSet<string> NonStructuralMergeExcludedTypes =
    [
        EntityTypeService.ProjectNodeType,
        EntityTypeService.ActNodeType,
        EntityTypeService.ChapterNodeType,
        EntityTypeService.EventNodeType,
        EntityTypeService.SourceNodeType,
        EntityTypeService.SourceChunkNodeType,
        EntityTypeService.SourceBlockNodeType,
    ];

    private sealed class ImportState
    {
        public Dictionary<string, GraphNode> NodeMap { get; } = new(StringComparer.Ordinal);
        public Dictionary<Guid, Guid> ImageMap { get; } = [];
        public Dictionary<Guid, Guid> ChapterMap { get; } = [];
        public List<Guid> CreatedActIds { get; } = [];
        public List<Guid> CreatedChapterIds { get; } = [];
        public List<Guid> ContextEntityIdsToReindex { get; } = [];
    }

    public async Task RunAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await imports.GetJobAsync(jobId, cancellationToken);
        if (job is null || job.Status != ProjectImportJobStatus.Queued) return;

        try
        {
            await MarkRunningAsync(job, cancellationToken);
            var document = await ReadAndValidateAsync(job, cancellationToken);
            var project = await projects.GetByIdAsync(job.ProjectId, cancellationToken)
                ?? throw new InvalidOperationException($"Project {job.ProjectId} not found.");

            await outlineGraphSync.RepairProjectAsync(project.Id, cancellationToken);
            await entityTypeService.EnsureDefaultsAsync(project.Id, cancellationToken);
            await ImportProjectDirectionAsync(project, document, cancellationToken);
            await StepAsync(job, "Prepared current project graph.", cancellationToken);

            var state = new ImportState();
            await SeedProjectNodeMapAsync(project, document, state, cancellationToken);
            await ImportEntityTypesAsync(job, document, cancellationToken);
            await StepAsync(job, "Imported entity type definitions.", cancellationToken);

            await ImportProjectImagesAsync(job, document, state, cancellationToken);
            await StepAsync(job, "Imported project images.", cancellationToken);

            if (document.ExportKind == ProjectExportKind.Full)
            {
                await AppendStructuralItemsAsync(job, document, state, cancellationToken);
                if (document.PublishProfiles.FirstOrDefault() is { } importedProfile)
                {
                    await ImportPublishProfileSettingsAsync(
                        job.ProjectId,
                        importedProfile,
                        state.ChapterMap,
                        cancellationToken);
                }
                await StepAsync(job, "Appended exported outline structure and imported publish page settings.", cancellationToken);
            }
            else
            {
                await StepAsync(job, "Skipped structural outline data for non-structural import.", cancellationToken);
            }

            await ImportGraphNodesAsync(job, document, state, cancellationToken);
            await StepAsync(job, "Merged graph entities and provenance nodes.", cancellationToken);

            await ImportGraphEdgesAsync(job, document, state, cancellationToken);
            await StepAsync(job, "Merged graph relationships.", cancellationToken);

            await ImportEntityVisualExamplesAsync(job, document, state, cancellationToken);

            await outlineGraphSync.RepairProjectAsync(project.Id, cancellationToken);
            await StepAsync(job, "Repaired graph outline links.", cancellationToken);

            await ReindexBestEffortAsync(job, state, cancellationToken);
            await StepAsync(job, "Refreshed import indexes where available.", cancellationToken);

            job.Status = ProjectImportJobStatus.Completed;
            job.CurrentMessage = "Import completed.";
            job.CompletedAt = DateTime.UtcNow;
            job.UpdatedAt = DateTime.UtcNow;
            imports.UpdateJob(job);
            await imports.SaveChangesAsync(cancellationToken);
            Notify(job.ProjectId, job.Id, ProjectImportJobUpdateKind.Completed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await MarkFailedAsync(job, ex, cancellationToken);
        }
    }

    private async Task ImportProjectDirectionAsync(
        Project project,
        ProjectExportDocument document,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(project.ProjectGuidance)
            && !string.IsNullOrWhiteSpace(document.Project.EffectiveProjectGuidance))
        {
            project.ProjectGuidance = document.Project.EffectiveProjectGuidance.Trim();
            project.UpdatedAt = DateTime.UtcNow;
            projects.Update(project);
            await projects.SaveChangesAsync(cancellationToken);
        }

        if (document.BookBrief is not { } imported)
            return;

        var current = await bookBriefs.GetOrCreateAsync(project.Id, cancellationToken);
        await bookBriefs.UpdateAsync(project.Id, new BookBriefPatch
        {
            BookKind = current.BookKind == BookKind.Unspecified && imported.BookKind != BookKind.Unspecified
                ? imported.BookKind
                : null,
            Premise = Missing(current.Premise, imported.Premise),
            Genre = Missing(current.Genre, imported.Genre),
            PrimaryThemes = Missing(current.PrimaryThemes, imported.PrimaryThemes),
            Purpose = Missing(current.Purpose, imported.Purpose),
            CreativeConstraints = Missing(current.CreativeConstraints, imported.CreativeConstraints),
            TargetAudience = Missing(current.TargetAudience, imported.TargetAudience),
            MinimumReaderAge = current.MinimumReaderAge is null ? imported.MinimumReaderAge : null,
            MaximumReaderAge = current.MaximumReaderAge is null ? imported.MaximumReaderAge : null,
            ReadingLevelGuidance = Missing(current.ReadingLevelGuidance, imported.ReadingLevelGuidance),
            TargetWordCount = current.TargetWordCount is null ? imported.TargetWordCount : null,
            PointOfView = Missing(current.PointOfView, imported.PointOfView),
            Tense = Missing(current.Tense, imported.Tense),
            VoiceAndTone = Missing(current.VoiceAndTone, imported.VoiceAndTone),
            LanguageLocale = Missing(current.LanguageLocale, imported.LanguageLocale),
            HouseStyle = Missing(current.HouseStyle, imported.HouseStyle),
            ReadAloudPriority = current.ReadAloudPriority is null ? imported.ReadAloudPriority : null,
            AccessibilityGoals = Missing(current.AccessibilityGoals, imported.AccessibilityGoals),
            VisualDirection = Missing(current.VisualDirection, imported.VisualDirection),
        }, cancellationToken);
    }

    private static string? Missing(string current, string imported) =>
        string.IsNullOrWhiteSpace(current) && !string.IsNullOrWhiteSpace(imported)
            ? imported
            : null;

    private async Task<ProjectExportDocument> ReadAndValidateAsync(ProjectImportJob job, CancellationToken cancellationToken)
    {
        ProjectExportDocument document;
        try
        {
            document = JsonSerializer.Deserialize<ProjectExportDocument>(job.ContentJson, JsonOptions)
                ?? throw new InvalidOperationException("Import file did not contain a project export document.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Import file is not valid Lorekeeper JSON: {ex.Message}", ex);
        }

        if (!string.Equals(document.FormatId, ProjectExportDocument.CurrentFormatId, StringComparison.Ordinal))
            throw new InvalidOperationException($"Unsupported import format '{document.FormatId}'.");
        if (document.FormatVersion < 1 || document.FormatVersion > ProjectExportDocument.CurrentFormatVersion)
            throw new InvalidOperationException($"Unsupported import format version {document.FormatVersion}.");

        var duplicateNode = document.Nodes
            .GroupBy(node => StableKey(node.NodeType, node.Key), StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateNode is not null)
            throw new InvalidOperationException($"Import file contains duplicate graph node '{duplicateNode.Key}'.");

        var nodeKeys = document.Nodes
            .Select(node => StableKey(node.NodeType, node.Key))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var edge in document.Edges)
        {
            if (!nodeKeys.Contains(edge.From.StableKey))
                throw new InvalidOperationException($"Import edge references missing source node '{edge.From.StableKey}'.");
            if (!nodeKeys.Contains(edge.To.StableKey))
                throw new InvalidOperationException($"Import edge references missing target node '{edge.To.StableKey}'.");
        }

        job.FormatId = document.FormatId;
        job.FormatVersion = document.FormatVersion;
        job.ExportKind = document.ExportKind.ToString();
        imports.UpdateJob(job);
        await imports.SaveChangesAsync(cancellationToken);

        await AddReportAsync(
            job,
            ProjectImportReportItemKind.Validation,
            "Validated import file",
            $"{document.ExportKind} export from {document.Project.Name}",
            resourceType: "ProjectExport",
            resourceKey: document.Project.Id.ToString("N"),
            cancellationToken: cancellationToken);
        await StepAsync(job, "Validated import file.", cancellationToken);
        return document;
    }

    private async Task SeedProjectNodeMapAsync(
        Project project,
        ProjectExportDocument document,
        ImportState state,
        CancellationToken cancellationToken)
    {
        var projectNode = await nodes.FindAsync(
            project.Id,
            EntityTypeService.ProjectNodeType,
            project.Id.ToString("N"),
            cancellationToken) ?? throw new InvalidOperationException("Current project graph node was not found.");

        foreach (var exportedProjectNode in document.Nodes.Where(node => string.Equals(node.NodeType, EntityTypeService.ProjectNodeType, StringComparison.Ordinal)))
            state.NodeMap[StableKey(exportedProjectNode.NodeType, exportedProjectNode.Key)] = projectNode;
    }

    private async Task ImportEntityTypesAsync(
        ProjectImportJob job,
        ProjectExportDocument document,
        CancellationToken cancellationToken)
    {
        foreach (var importedType in document.EntityTypes)
        {
            if (string.Equals(importedType.Type, EntityTypeService.ProjectNodeType, StringComparison.Ordinal)
                || string.Equals(importedType.Type, EntityTypeService.ActNodeType, StringComparison.Ordinal)
                || string.Equals(importedType.Type, EntityTypeService.ChapterNodeType, StringComparison.Ordinal)
                || string.Equals(importedType.Type, EntityTypeService.EventNodeType, StringComparison.Ordinal))
            {
                continue;
            }

            var existing = await entityTypes.FindAsync(job.ProjectId, importedType.Type, cancellationToken);
            if (existing is null)
            {
                await entityTypes.AddAsync(new GraphEntityType
                {
                    ProjectId = job.ProjectId,
                    Type = importedType.Type,
                    SingularLabel = importedType.SingularLabel,
                    PluralLabel = importedType.PluralLabel,
                    Color = importedType.Color,
                    Icon = importedType.Icon,
                    IsStructural = importedType.IsStructural,
                    IsChapterScoped = importedType.IsChapterScoped,
                    SortOrder = importedType.SortOrder,
                    DefaultProperties = NormalizeProperties(importedType.DefaultProperties),
                }, cancellationToken);
                await entityTypes.SaveChangesAsync(cancellationToken);
                await AddReportAsync(job, ProjectImportReportItemKind.EntityType, $"Created type {importedType.Type}", importedType.PluralLabel, "GraphEntityType", importedType.Type, cancellationToken: cancellationToken);
                continue;
            }

            var changed = false;
            if (string.IsNullOrWhiteSpace(existing.Color) && !string.IsNullOrWhiteSpace(importedType.Color))
            {
                existing.Color = importedType.Color;
                changed = true;
            }
            if (string.IsNullOrWhiteSpace(existing.Icon) && !string.IsNullOrWhiteSpace(importedType.Icon))
            {
                existing.Icon = importedType.Icon;
                changed = true;
            }
            changed |= MergeProperties(existing.DefaultProperties, NormalizeProperties(importedType.DefaultProperties));
            if (changed)
            {
                existing.UpdatedAt = DateTime.UtcNow;
                entityTypes.Update(existing);
                await entityTypes.SaveChangesAsync(cancellationToken);
            }
            await AddReportAsync(job, ProjectImportReportItemKind.EntityType, $"Merged type {importedType.Type}", importedType.PluralLabel, "GraphEntityType", importedType.Type, cancellationToken: cancellationToken);
        }
    }

    private async Task AppendStructuralItemsAsync(
        ProjectImportJob job,
        ProjectExportDocument document,
        ImportState state,
        CancellationToken cancellationToken)
    {
        var actMap = new Dictionary<Guid, Guid>();
        foreach (var importedAct in document.Acts.OrderBy(act => act.Order))
        {
            var created = await acts.CreateAsync(job.ProjectId, importedAct.Title, importedAct.Synopsis, cancellationToken: cancellationToken);
            actMap[importedAct.Id] = created.Id;
            job.CreatedActCount++;
            state.CreatedActIds.Add(created.Id);
            var node = await nodes.FindAsync(job.ProjectId, EntityTypeService.ActNodeType, created.Id.ToString("N"), cancellationToken);
            if (node is not null)
                state.NodeMap[StableKey(EntityTypeService.ActNodeType, importedAct.Id.ToString("N"))] = node;
            await AddReportAsync(job, ProjectImportReportItemKind.Structural, $"Appended act {created.Title}", "Imported as a new act at the end of the current outline.", "Act", created.Id.ToString("N"), cancellationToken: cancellationToken);
        }

        foreach (var importedChapter in OrderedImportedChapters(document))
        {
            var targetActId = importedChapter.ActId is Guid exportedActId && actMap.TryGetValue(exportedActId, out var localActId)
                ? localActId
                : (Guid?)null;
            var created = await chapters.CreateAsync(job.ProjectId, targetActId, importedChapter.Title, importedChapter.Synopsis, cancellationToken: cancellationToken);
            var tracked = await chapterRepo.GetByIdAsync(created.Id, cancellationToken)
                ?? throw new InvalidOperationException($"Created chapter {created.Id} could not be reloaded.");
            tracked.Body = importedChapter.Body;
            tracked.VisualMode = importedChapter.VisualMode;
            tracked.PageLayoutKind = importedChapter.PageLayoutKind;
            tracked.PageLayoutJson = RewritePageLayoutJson(importedChapter.PageLayoutJson, state.ImageMap);
            tracked.IllustrationLayoutJson = RewriteIllustrationLayoutJson(importedChapter.IllustrationLayoutJson, state.ImageMap);
            ChapterTextLayoutSynchronizer.SynchronizeFromBody(
                tracked,
                tracked.Body,
                ensureLayout: tracked.VisualMode == ChapterVisualMode.PicturePage);
            tracked.VectorIndexState = string.IsNullOrWhiteSpace(importedChapter.Body) ? VectorIndexState.UpToDate : VectorIndexState.Stale;
            tracked.VectorIndexedAt = null;
            tracked.VectorIndexError = null;
            tracked.UpdatedAt = DateTime.UtcNow;
            chapterRepo.Update(tracked);
            await chapterRepo.SaveChangesAsync(cancellationToken);
            await AddImageContextPreferencesAsync(
                job.ProjectId,
                tracked.Id,
                importedChapter.ExplicitImageContextImageIds,
                state.ImageMap,
                cancellationToken);
            await outlineGraphSync.EnsureChapterAsync(tracked, cancellationToken);

            job.CreatedChapterCount++;
            state.ChapterMap[importedChapter.Id] = tracked.Id;
            state.CreatedChapterIds.Add(tracked.Id);
            var node = await nodes.FindAsync(job.ProjectId, EntityTypeService.ChapterNodeType, tracked.Id.ToString("N"), cancellationToken);
            if (node is not null)
                state.NodeMap[StableKey(EntityTypeService.ChapterNodeType, importedChapter.Id.ToString("N"))] = node;
            await AddReportAsync(job, ProjectImportReportItemKind.Structural, $"Appended chapter {tracked.Title}", "Imported as a new chapter; duplicate titles are left for outline cleanup.", "Chapter", tracked.Id.ToString("N"), cancellationToken: cancellationToken);
        }

        var importedEvents = document.Nodes
            .Where(node => string.Equals(node.NodeType, EntityTypeService.EventNodeType, StringComparison.Ordinal))
            .ToList();
        foreach (var importedEvent in importedEvents)
        {
            var parentEdge = document.Edges.FirstOrDefault(edge =>
                string.Equals(edge.EdgeType, EntityService.HasChildEdgeType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(edge.To.NodeType, importedEvent.NodeType, StringComparison.Ordinal)
                && string.Equals(edge.To.Key, importedEvent.Key, StringComparison.Ordinal)
                && string.Equals(edge.From.NodeType, EntityTypeService.ChapterNodeType, StringComparison.Ordinal));
            if (parentEdge is null || !state.NodeMap.TryGetValue(parentEdge.From.StableKey, out var localChapterNode))
            {
                await AddWarningAsync(job, $"Skipped beat {importedEvent.Label ?? importedEvent.Key}", "The exported beat did not have an imported chapter parent.", cancellationToken);
                continue;
            }

            var localKey = Guid.NewGuid().ToString("N");
            var created = await graph.UpsertNodeAsync(
                job.ProjectId,
                EntityTypeService.EventNodeType,
                localKey,
                importedEvent.Label,
                NormalizeProperties(importedEvent.Properties),
                cancellationToken);
            await graph.UpsertEdgeAsync(
                localChapterNode.Id,
                created.Id,
                EntityService.HasChildEdgeType,
                sortOrder: parentEdge.SortOrder,
                cancellationToken: cancellationToken);

            state.NodeMap[StableKey(importedEvent.NodeType, importedEvent.Key)] = created;
            if (Guid.TryParseExact(localKey, "N", out var eventId))
                state.ContextEntityIdsToReindex.Add(eventId);
            job.CreatedBeatCount++;
            await AddReportAsync(job, ProjectImportReportItemKind.Structural, $"Appended beat {created.Label ?? created.Key}", "Imported as a new beat under an imported chapter.", "Event", created.Key, entityId: Guid.ParseExact(localKey, "N"), graphNodeId: created.Id, cancellationToken: cancellationToken);
        }
    }

    private async Task ImportProjectImagesAsync(
        ProjectImportJob job,
        ProjectExportDocument document,
        ImportState state,
        CancellationToken cancellationToken)
    {
        var importedAssets = new Dictionary<Guid, PublishAsset>();
        foreach (var importedImage in document.Images)
        {
            if (importedImage.Data.Length == 0 || !importedImage.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                await AddWarningAsync(job, $"Skipped image {importedImage.FileName}", "The exported image was empty or did not have an image content type.", cancellationToken);
                continue;
            }

            var localId = Guid.NewGuid();
            state.ImageMap[importedImage.Id] = localId;
            var asset = new PublishAsset
            {
                Id = localId,
                ProjectId = job.ProjectId,
                Source = importedImage.Source,
                FileName = string.IsNullOrWhiteSpace(importedImage.FileName) ? $"imported-image-{localId:N}.png" : importedImage.FileName.Trim(),
                ContentType = importedImage.ContentType.Trim(),
                Data = importedImage.Data,
                AltText = importedImage.AltText,
                Prompt = importedImage.Prompt,
                GenerationModel = importedImage.GenerationModel,
                SourceMetadataJson = importedImage.SourceMetadataJson,
                CropXPercent = importedImage.CropXPercent,
                CropYPercent = importedImage.CropYPercent,
                CropWidthPercent = importedImage.CropWidthPercent,
                CropHeightPercent = importedImage.CropHeightPercent,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            importedAssets[importedImage.Id] = asset;
            await db.PublishAssets.AddAsync(asset, cancellationToken);
        }

        foreach (var importedImage in document.Images)
        {
            if (importedImage.DerivedFromImageId is not Guid exportedSourceId
                || !importedAssets.TryGetValue(importedImage.Id, out var localAsset)
                || !state.ImageMap.TryGetValue(exportedSourceId, out var localSourceId))
            {
                continue;
            }

            localAsset.DerivedFromImageId = localSourceId;
        }

        if (state.ImageMap.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            await AddReportAsync(job, ProjectImportReportItemKind.Structural, $"Imported {state.ImageMap.Count} project image(s)", "Images were added to the project image library.", "ProjectImage", job.ProjectId.ToString("N"), cancellationToken: cancellationToken);
        }

    }

    private async Task ImportEntityVisualExamplesAsync(
        ProjectImportJob job,
        ProjectExportDocument document,
        ImportState state,
        CancellationToken cancellationToken)
    {
        foreach (var group in document.EntityVisualExamples
            .GroupBy(example => example.Entity.StableKey, StringComparer.Ordinal))
        {
            if (!state.NodeMap.TryGetValue(group.Key, out var localNode)
                || !Guid.TryParseExact(localNode.Key, "N", out var entityId))
            {
                await AddWarningAsync(job, $"Skipped {group.Count()} visual association(s)", $"Entity '{group.Key}' was not imported.", cancellationToken);
                continue;
            }

            var attachedIds = new List<Guid>();
            foreach (var imported in group.OrderBy(example => example.SortOrder))
            {
                if (!state.ImageMap.TryGetValue(imported.ImageId, out var imageId))
                {
                    await AddWarningAsync(job, "Skipped entity visual", $"Image {imported.ImageId:N} was not imported.", cancellationToken);
                    continue;
                }
                var attached = await entityVisualExamples.AttachAsync(
                    job.ProjectId, entityId, imageId, imported.Label, EntityVisualExampleOrigin.Import,
                    cancellationToken: cancellationToken);
                attachedIds.Add(attached.Id);
            }
            if (attachedIds.Count > 1)
            {
                var all = await entityVisualExamples.ListForEntityAsync(job.ProjectId, entityId, cancellationToken);
                var importedSet = attachedIds.ToHashSet();
                var order = all.Where(example => importedSet.Contains(example.Id)).OrderBy(example => attachedIds.IndexOf(example.Id))
                    .Concat(all.Where(example => !importedSet.Contains(example.Id)))
                    .Select(example => example.Id)
                    .ToList();
                await entityVisualExamples.ReorderAsync(job.ProjectId, entityId, order, markManual: false, cancellationToken: cancellationToken);
            }
        }
    }

    private async Task ImportPublishProfileSettingsAsync(
        Guid projectId,
        ProjectExportPublishProfile importedProfile,
        IReadOnlyDictionary<Guid, Guid> chapterMap,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(importedProfile.TitlePageMode)
            || !Enum.IsDefined(importedProfile.PrintPicturePageSpreadMode)
            || !Enum.IsDefined(importedProfile.EpubPicturePageSpreadMode))
        {
            throw new InvalidOperationException("The imported publish profile contains an unsupported page presentation mode.");
        }

        var profile = await db.PublishProfiles.FirstOrDefaultAsync(candidate => candidate.ProjectId == projectId, cancellationToken);
        if (profile is null)
        {
            profile = new PublishProfile { ProjectId = projectId };
            await db.PublishProfiles.AddAsync(profile, cancellationToken);
        }

        profile.PageWidthInches = importedProfile.PageWidthInches;
        profile.PageHeightInches = importedProfile.PageHeightInches;
        profile.PageMarginInches = importedProfile.PageMarginInches;
        profile.BodyFontSizePoints = importedProfile.BodyFontSizePoints;
        profile.BodyLineHeight = importedProfile.BodyLineHeight;
        profile.TitlePageMode = importedProfile.TitlePageMode;
        profile.PrintPicturePageSpreadMode = importedProfile.PrintPicturePageSpreadMode;
        profile.EpubPicturePageSpreadMode = importedProfile.EpubPicturePageSpreadMode;
        profile.SelectedCoverChapterId = null;
        if (importedProfile.SelectedCoverChapterId is Guid exportedCoverChapterId
            && chapterMap.TryGetValue(exportedCoverChapterId, out var localCoverChapterId)
            && await db.Chapters.AsNoTracking().AnyAsync(
                chapter => chapter.Id == localCoverChapterId
                    && chapter.ProjectId == projectId
                    && chapter.VisualMode == ChapterVisualMode.PicturePage,
                cancellationToken))
        {
            profile.SelectedCoverChapterId = localCoverChapterId;
        }
        profile.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task ImportGraphNodesAsync(
        ProjectImportJob job,
        ProjectExportDocument document,
        ImportState state,
        CancellationToken cancellationToken)
    {
        foreach (var importedNode in document.Nodes)
        {
            var importedStableKey = StableKey(importedNode.NodeType, importedNode.Key);
            if (state.NodeMap.ContainsKey(importedStableKey)) continue;
            if (string.Equals(importedNode.NodeType, EntityTypeService.ProjectNodeType, StringComparison.Ordinal)) continue;
            if (document.ExportKind == ProjectExportKind.Full && AppendStructuralNodeTypes.Contains(importedNode.NodeType)) continue;

            if (string.Equals(importedNode.NodeType, EntityTypeService.ProjectFactNodeType, StringComparison.Ordinal))
            {
                await ImportProjectFactAsync(job, importedNode, state, cancellationToken);
                continue;
            }

            var importedProperties = NormalizeProperties(importedNode.Properties);
            var existing = await FindExistingNodeAsync(job.ProjectId, importedNode, cancellationToken);
            if (existing is null)
            {
                var created = await graph.UpsertNodeAsync(job.ProjectId, importedNode.NodeType, importedNode.Key, importedNode.Label, importedProperties, cancellationToken);
                state.NodeMap[importedStableKey] = created;
                job.CreatedNodeCount++;
                AddContextEntityId(created, state.ContextEntityIdsToReindex);
                await AddReportAsync(job, ProjectImportReportItemKind.Entity, $"Created {created.NodeType} {created.Label ?? created.Key}", string.Empty, created.NodeType, created.Key, graphNodeId: created.Id, cancellationToken: cancellationToken);
                continue;
            }

            var changed = false;
            if (string.IsNullOrWhiteSpace(existing.Label) && !string.IsNullOrWhiteSpace(importedNode.Label))
            {
                existing.Label = importedNode.Label;
                changed = true;
            }
            changed |= MergeProperties(existing.Properties, importedProperties);
            if (changed)
            {
                existing.UpdatedAt = DateTime.UtcNow;
                nodes.Update(existing);
                await nodes.SaveChangesAsync(cancellationToken);
            }

            state.NodeMap[importedStableKey] = existing;
            job.MergedNodeCount++;
            AddContextEntityId(existing, state.ContextEntityIdsToReindex);
            await AddReportAsync(job, ProjectImportReportItemKind.Entity, $"Merged {existing.NodeType} {existing.Label ?? existing.Key}", "Existing canonical values were preserved; missing fields and provenance were added.", existing.NodeType, existing.Key, graphNodeId: existing.Id, cancellationToken: cancellationToken);
        }
    }

    private async Task ImportProjectFactAsync(
        ProjectImportJob job,
        ProjectExportNode importedNode,
        ImportState state,
        CancellationToken cancellationToken)
    {
        var importedProperties = NormalizeProperties(importedNode.Properties);
        var key = ReadString(importedProperties, "key") ?? importedNode.Label ?? importedNode.Key;
        var value = ReadString(importedProperties, "value") ?? string.Empty;
        var existing = await projectFacts.GetByKeyAsync(job.ProjectId, key, cancellationToken);
        var requestedId = existing is null && Guid.TryParseExact(importedNode.Key, "N", out var importedId)
            ? importedId
            : (Guid?)null;
        var fact = await projectFacts.UpsertAsync(job.ProjectId, key, value, requestedId, cancellationToken);
        var node = await nodes.FindByKeyAsync(job.ProjectId, fact.Id.ToString("N"), cancellationToken)
            ?? throw new InvalidOperationException($"Imported project fact {fact.Id} could not be resolved.");
        var changed = MergeProperties(node.Properties, importedProperties);
        if (changed)
        {
            node.UpdatedAt = DateTime.UtcNow;
            nodes.Update(node);
            await nodes.SaveChangesAsync(cancellationToken);
        }

        state.NodeMap[StableKey(importedNode.NodeType, importedNode.Key)] = node;
        if (existing is null) job.CreatedNodeCount++; else job.MergedNodeCount++;
        await AddReportAsync(job, ProjectImportReportItemKind.Entity, $"{(existing is null ? "Created" : "Merged")} project fact {fact.Key}", fact.Value, EntityTypeService.ProjectFactNodeType, fact.Id.ToString("N"), entityId: fact.Id, graphNodeId: node.Id, cancellationToken: cancellationToken);
    }

    private async Task ImportGraphEdgesAsync(
        ProjectImportJob job,
        ProjectExportDocument document,
        ImportState state,
        CancellationToken cancellationToken)
    {
        foreach (var importedEdge in document.Edges)
        {
            if (!state.NodeMap.TryGetValue(importedEdge.From.StableKey, out var fromNode)
                || !state.NodeMap.TryGetValue(importedEdge.To.StableKey, out var toNode))
            {
                await AddWarningAsync(job, $"Skipped {importedEdge.EdgeType} relationship", "One or both relationship endpoints were not imported.", cancellationToken);
                continue;
            }

            if (IsManagedStructuralEdge(importedEdge)) continue;

            var importedProperties = NormalizeProperties(importedEdge.Properties);
            var existing = await edges.FindAsync(fromNode.Id, toNode.Id, importedEdge.EdgeType, cancellationToken);
            if (existing is null)
            {
                var created = await graph.UpsertEdgeAsync(fromNode.Id, toNode.Id, importedEdge.EdgeType, importedProperties, importedEdge.SortOrder, cancellationToken);
                job.CreatedEdgeCount++;
                AddContextEndpointIds(fromNode, toNode, state.ContextEntityIdsToReindex);
                await AddReportAsync(job, ProjectImportReportItemKind.Relationship, $"Created {created.EdgeType} relationship", $"{fromNode.Label ?? fromNode.Key} -> {toNode.Label ?? toNode.Key}", created.EdgeType, created.Id.ToString(), graphEdgeId: created.Id, cancellationToken: cancellationToken);
                continue;
            }

            var changed = MergeProperties(existing.Properties, importedProperties);
            if (existing.SortOrder is null && importedEdge.SortOrder is not null)
            {
                existing.SortOrder = importedEdge.SortOrder;
                changed = true;
            }
            if (changed)
            {
                existing.UpdatedAt = DateTime.UtcNow;
                edges.Update(existing);
                await edges.SaveChangesAsync(cancellationToken);
            }

            job.MergedEdgeCount++;
            AddContextEndpointIds(fromNode, toNode, state.ContextEntityIdsToReindex);
            await AddReportAsync(job, ProjectImportReportItemKind.Relationship, $"Merged {existing.EdgeType} relationship", $"{fromNode.Label ?? fromNode.Key} -> {toNode.Label ?? toNode.Key}", existing.EdgeType, existing.Id.ToString(), graphEdgeId: existing.Id, cancellationToken: cancellationToken);
        }
    }

    private async Task ReindexBestEffortAsync(ProjectImportJob job, ImportState state, CancellationToken cancellationToken)
    {
        foreach (var actId in state.CreatedActIds.Distinct())
            await contextIndexing.ReindexActAsync(actId, cancellationToken);

        foreach (var chapterId in state.CreatedChapterIds.Distinct())
        {
            await contextIndexing.ReindexChapterAsync(chapterId, cancellationToken);
            try
            {
                await chapters.ReindexAsync(chapterId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Failed to rebuild chapter vectors during import for {ChapterId}", chapterId);
                await AddWarningAsync(job, "Chapter body index refresh failed", ex.Message, cancellationToken);
            }
        }

        foreach (var entityId in state.ContextEntityIdsToReindex.Distinct())
            await contextIndexing.ReindexEntityAsync(job.ProjectId, entityId, cancellationToken);
    }

    private async Task<GraphNode?> FindExistingNodeAsync(
        Guid projectId,
        ProjectExportNode importedNode,
        CancellationToken cancellationToken)
    {
        var byStableKey = await nodes.FindAsync(projectId, importedNode.NodeType, importedNode.Key, cancellationToken);
        if (byStableKey is not null) return byStableKey;

        if (NonStructuralMergeExcludedTypes.Contains(importedNode.NodeType)) return null;
        var importedName = NormalizeComparable(importedNode.Label ?? importedNode.Key);
        if (importedName.Length == 0) return null;

        var candidates = await nodes.ListByTypeAsync(projectId, importedNode.NodeType, cancellationToken);
        return candidates.FirstOrDefault(candidate => NormalizeComparable(candidate.Label ?? candidate.Key) == importedName);
    }

    private static IReadOnlyList<ProjectExportChapter> OrderedImportedChapters(ProjectExportDocument document)
    {
        var actOrder = document.Acts.ToDictionary(act => act.Id, act => act.Order);
        return document.Chapters
            .OrderBy(chapter => chapter.ActId is Guid actId && actOrder.TryGetValue(actId, out var order) ? order : int.MaxValue)
            .ThenBy(chapter => chapter.ActId is null ? 1 : 0)
            .ThenBy(chapter => chapter.Order)
            .ToList();
    }

    private async Task AddImageContextPreferencesAsync(
        Guid projectId,
        Guid chapterId,
        IReadOnlyList<Guid> exportedImageIds,
        IReadOnlyDictionary<Guid, Guid> imageMap,
        CancellationToken cancellationToken)
    {
        foreach (var exportedImageId in exportedImageIds.Distinct())
        {
            if (!imageMap.TryGetValue(exportedImageId, out var localImageId))
                continue;

            await db.EditorContextPreferences.AddAsync(new EditorContextPreference
            {
                ProjectId = projectId,
                ChapterId = chapterId,
                Kind = ContextItemKind.ProjectImage.ToString(),
                Key = EditorContextKeys.ProjectImage(localImageId),
                IsIncluded = true,
            }, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static string RewriteIllustrationLayoutJson(
        string layoutJson,
        IReadOnlyDictionary<Guid, Guid> imageMap)
    {
        if (string.IsNullOrWhiteSpace(layoutJson) || imageMap.Count == 0)
            return layoutJson;

        try
        {
            var layout = JsonSerializer.Deserialize<IllustratedProseLayout>(layoutJson, JsonOptions)
                ?? new IllustratedProseLayout([]);
            var images = layout.Images
                .Select(image => imageMap.TryGetValue(image.ImageId, out var localImageId)
                    ? image with { ImageId = localImageId }
                    : image)
                .ToList();
            return JsonSerializer.Serialize(layout with { Images = images }, JsonOptions);
        }
        catch (JsonException)
        {
            return layoutJson;
        }
    }

    private static string RewritePageLayoutJson(
        string layoutJson,
        IReadOnlyDictionary<Guid, Guid> imageMap)
    {
        if (string.IsNullOrWhiteSpace(layoutJson) || imageMap.Count == 0)
            return layoutJson;

        try
        {
            var layout = JsonSerializer.Deserialize<PicturePageLayout>(layoutJson, JsonOptions)
                ?? new PicturePageLayout([], []);
            var images = layout.Images
                .Select(image => imageMap.TryGetValue(image.ImageId, out var localImageId)
                    ? image with { ImageId = localImageId }
                    : image)
                .ToList();
            return JsonSerializer.Serialize(layout with { Images = images }, JsonOptions);
        }
        catch (JsonException)
        {
            return layoutJson;
        }
    }

    private async Task MarkRunningAsync(ProjectImportJob job, CancellationToken cancellationToken)
    {
        job.Status = ProjectImportJobStatus.Running;
        job.StartedAt = DateTime.UtcNow;
        job.UpdatedAt = DateTime.UtcNow;
        job.CurrentMessage = "Starting import.";
        imports.UpdateJob(job);
        await imports.SaveChangesAsync(cancellationToken);
        Notify(job.ProjectId, job.Id, ProjectImportJobUpdateKind.Progress);
    }

    private async Task StepAsync(ProjectImportJob job, string message, CancellationToken cancellationToken)
    {
        job.CompletedSteps = Math.Min(job.TotalSteps, job.CompletedSteps + 1);
        job.CurrentMessage = message;
        job.UpdatedAt = DateTime.UtcNow;
        imports.UpdateJob(job);
        await imports.SaveChangesAsync(cancellationToken);
        Notify(job.ProjectId, job.Id, ProjectImportJobUpdateKind.Progress);
    }

    private async Task MarkFailedAsync(ProjectImportJob job, Exception exception, CancellationToken cancellationToken)
    {
        job.Status = ProjectImportJobStatus.Failed;
        job.CurrentMessage = "Import failed.";
        job.ErrorMessage = exception.Message;
        job.CompletedAt = DateTime.UtcNow;
        job.UpdatedAt = DateTime.UtcNow;
        imports.UpdateJob(job);
        await imports.SaveChangesAsync(cancellationToken);
        await AddReportAsync(job, ProjectImportReportItemKind.Validation, "Import failed", exception.Message, status: ProjectImportReportItemStatus.Failed, errorMessage: exception.Message, cancellationToken: cancellationToken);
        Notify(job.ProjectId, job.Id, ProjectImportJobUpdateKind.Failed);
    }

    private async Task AddWarningAsync(ProjectImportJob job, string title, string summary, CancellationToken cancellationToken)
    {
        job.WarningCount++;
        imports.UpdateJob(job);
        await imports.SaveChangesAsync(cancellationToken);
        await AddReportAsync(job, ProjectImportReportItemKind.Warning, title, summary, cancellationToken: cancellationToken);
    }

    private async Task AddReportAsync(
        ProjectImportJob job,
        ProjectImportReportItemKind kind,
        string title,
        string summary,
        string resourceType = "",
        string resourceKey = "",
        Guid? entityId = null,
        long? graphNodeId = null,
        long? graphEdgeId = null,
        string payloadJson = "{}",
        ProjectImportReportItemStatus status = ProjectImportReportItemStatus.Active,
        string errorMessage = "",
        CancellationToken cancellationToken = default)
    {
        await imports.AddReportItemAsync(new ProjectImportReportItem
        {
            JobId = job.Id,
            Kind = kind,
            Status = status,
            Title = title,
            Summary = summary,
            ResourceType = resourceType,
            ResourceKey = resourceKey,
            EntityId = entityId,
            GraphNodeId = graphNodeId,
            GraphEdgeId = graphEdgeId,
            PayloadJson = payloadJson,
            ErrorMessage = errorMessage,
        }, cancellationToken);
        await imports.SaveChangesAsync(cancellationToken);
        Notify(job.ProjectId, job.Id, ProjectImportJobUpdateKind.Report);
    }

    private static bool IsManagedStructuralEdge(ProjectExportEdge edge)
    {
        if (!string.Equals(edge.EdgeType, EntityService.HasChildEdgeType, StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.Equals(edge.From.NodeType, EntityTypeService.ProjectNodeType, StringComparison.Ordinal)
            && (string.Equals(edge.To.NodeType, EntityTypeService.ActNodeType, StringComparison.Ordinal)
                || string.Equals(edge.To.NodeType, EntityTypeService.ChapterNodeType, StringComparison.Ordinal)
                || string.Equals(edge.To.NodeType, EntityTypeService.ProjectFactNodeType, StringComparison.Ordinal)))
        {
            return true;
        }

        return (string.Equals(edge.From.NodeType, EntityTypeService.ActNodeType, StringComparison.Ordinal)
                && string.Equals(edge.To.NodeType, EntityTypeService.ChapterNodeType, StringComparison.Ordinal))
            || (string.Equals(edge.From.NodeType, EntityTypeService.ChapterNodeType, StringComparison.Ordinal)
                && string.Equals(edge.To.NodeType, EntityTypeService.EventNodeType, StringComparison.Ordinal));
    }

    private static Dictionary<string, object?> NormalizeProperties(IDictionary<string, object?> source)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in source)
        {
            if (string.IsNullOrWhiteSpace(kv.Key)) continue;
            result[kv.Key] = NormalizeJsonValue(kv.Value);
        }

        return result;
    }

    private static object? NormalizeJsonValue(object? value)
    {
        if (value is not JsonElement element) return value;
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt32(out var intValue) => intValue,
            JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when element.TryGetDouble(out var doubleValue) => doubleValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            _ => element.GetRawText(),
        };
    }

    private static bool MergeProperties(IDictionary<string, object?> target, IReadOnlyDictionary<string, object?> source)
    {
        var changed = false;
        foreach (var kv in source)
        {
            if (string.IsNullOrWhiteSpace(kv.Key)) continue;
            if (IsAssertionProperty(kv.Key) && target.TryGetValue(kv.Key, out var existingAssertion))
            {
                var merged = MergeJsonObjectStrings(existingAssertion?.ToString(), kv.Value?.ToString());
                if (merged is not null && !string.Equals(existingAssertion?.ToString(), merged, StringComparison.Ordinal))
                {
                    target[kv.Key] = merged;
                    changed = true;
                }
                continue;
            }

            if (!target.TryGetValue(kv.Key, out var existing) || IsEmptyValue(existing))
            {
                target[kv.Key] = kv.Value;
                changed = true;
            }
        }

        return changed;
    }

    private static string? MergeJsonObjectStrings(string? existingJson, string? importedJson)
    {
        if (string.IsNullOrWhiteSpace(importedJson)) return existingJson;
        if (string.IsNullOrWhiteSpace(existingJson)) return importedJson;

        try
        {
            var existing = JsonNode.Parse(existingJson) as JsonObject;
            var imported = JsonNode.Parse(importedJson) as JsonObject;
            if (existing is null || imported is null) return existingJson;
            DeepMerge(existing, imported);
            return existing.ToJsonString(JsonOptions);
        }
        catch (JsonException)
        {
            return existingJson;
        }
    }

    private static void DeepMerge(JsonObject target, JsonObject source)
    {
        foreach (var kv in source.ToList())
        {
            if (!target.TryGetPropertyValue(kv.Key, out var existing) || existing is null)
            {
                target[kv.Key] = kv.Value?.DeepClone();
                continue;
            }

            if (existing is JsonObject existingObject && kv.Value is JsonObject importedObject)
            {
                DeepMerge(existingObject, importedObject);
                continue;
            }

            if (existing is JsonArray existingArray && kv.Value is JsonArray importedArray)
            {
                var existingValues = existingArray.Select(item => item?.ToJsonString()).ToHashSet(StringComparer.Ordinal);
                foreach (var item in importedArray)
                {
                    var key = item?.ToJsonString();
                    if (key is not null && existingValues.Add(key))
                        existingArray.Add(item?.DeepClone());
                }
            }
        }
    }

    private static bool IsAssertionProperty(string key) =>
        string.Equals(key, IngestSourceAssertions.EntityAssertionsProperty, StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, IngestSourceAssertions.RelationshipAssertionsProperty, StringComparison.OrdinalIgnoreCase);

    private static bool IsEmptyValue(object? value) =>
        value is null || string.IsNullOrWhiteSpace(value.ToString());

    private static string? ReadString(IReadOnlyDictionary<string, object?> properties, string key) =>
        properties.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static string NormalizeComparable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        Span<char> buffer = stackalloc char[value.Length];
        var index = 0;
        foreach (var ch in value.Trim())
        {
            if (char.IsLetterOrDigit(ch))
                buffer[index++] = char.ToLowerInvariant(ch);
        }
        return new string(buffer[..index]);
    }

    private static string StableKey(string nodeType, string key) =>
        $"{nodeType}/{key}";

    private static void AddContextEndpointIds(GraphNode from, GraphNode to, ICollection<Guid> target)
    {
        AddContextEntityId(from, target);
        AddContextEntityId(to, target);
    }

    private static void AddContextEntityId(GraphNode node, ICollection<Guid> target)
    {
        if (!Guid.TryParseExact(node.Key, "N", out var entityId)) return;
        if (string.Equals(node.NodeType, EntityTypeService.ProjectNodeType, StringComparison.OrdinalIgnoreCase)
            || string.Equals(node.NodeType, EntityTypeService.ActNodeType, StringComparison.OrdinalIgnoreCase)
            || string.Equals(node.NodeType, EntityTypeService.ChapterNodeType, StringComparison.OrdinalIgnoreCase)
            || string.Equals(node.NodeType, EntityTypeService.ProjectFactNodeType, StringComparison.OrdinalIgnoreCase)
            || string.Equals(node.NodeType, EntityTypeService.SourceNodeType, StringComparison.OrdinalIgnoreCase)
            || string.Equals(node.NodeType, EntityTypeService.SourceChunkNodeType, StringComparison.OrdinalIgnoreCase)
            || string.Equals(node.NodeType, EntityTypeService.SourceBlockNodeType, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        target.Add(entityId);
    }

    private void Notify(Guid projectId, Guid jobId, ProjectImportJobUpdateKind kind) =>
        notifier.Notify(new ProjectImportJobUpdate(projectId, jobId, kind, DateTime.UtcNow));
}
