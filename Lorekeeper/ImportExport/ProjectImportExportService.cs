using System.Security.Cryptography;
using System.Text.Json;
using Lorekeeper.Context;
using Lorekeeper.Ingest;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence;
using Lorekeeper.Persistence.Repositories;
using Lorekeeper.Publish;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.ImportExport;

public sealed class ProjectImportExportService(
    IAppDatabaseOperationFactory database, IEntityTypeService entityTypeService, IProjectImportJobQueue importQueue, IProjectImportJobNotifier notifier) : IProjectImportExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static readonly HashSet<string> NonStructuralExcludedNodeTypes =
    [
        EntityTypeService.ProjectNodeType,
        EntityTypeService.ActNodeType,
        EntityTypeService.ChapterNodeType,
        EntityTypeService.EventNodeType,
    ];

    public async Task<ProjectExportFile> ExportProjectAsync(
        Guid projectId,
        ProjectExportKind kind,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var acts = databaseOperation.Repositories.Acts;
        var chapters = databaseOperation.Repositories.Chapters;
        var nodes = databaseOperation.Repositories.GraphNodes;
        var edges = databaseOperation.Repositories.GraphEdges;
        var entityTypes = databaseOperation.Repositories.GraphEntityTypes;
        var projects = databaseOperation.Repositories.Projects;
        var project = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");
        await entityTypeService.EnsureDefaultsAsync(projectId, cancellationToken);
        var exportedImageContextIds = await ListExportedImageContextIdsAsync(projectId, cancellationToken);

        var canonSourceIds = await db.BookBriefCanonSources
            .AsNoTracking()
            .Where(selection => selection.BookBrief.ProjectId == projectId)
            .Select(selection => selection.IngestSourceId)
            .ToListAsync(cancellationToken);
        var allProjectSourceIds = await db.IngestSources.AsNoTracking()
            .Where(source => source.ProjectId == projectId)
            .Select(source => source.Id)
            .ToListAsync(cancellationToken);
        var omittedSourceIds = (kind == ProjectExportKind.NonStructural
                ? allProjectSourceIds
                : allProjectSourceIds.Except(canonSourceIds))
            .ToHashSet();
        var exportedProvenanceIds = canonSourceIds
            .Concat(await db.IngestSourceChunks.AsNoTracking()
                .Where(chunk => canonSourceIds.Contains(chunk.SourceId))
                .Select(chunk => chunk.Id)
                .ToListAsync(cancellationToken))
            .Concat(await db.IngestSourceBlocks.AsNoTracking()
                .Where(block => canonSourceIds.Contains(block.SourceId))
                .Select(block => block.Id)
                .ToListAsync(cancellationToken))
            .Select(id => id.ToString("N"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allNodes = await nodes.ListByProjectAsync(projectId, cancellationToken);
        var nodeById = allNodes.ToDictionary(node => node.Id);
        var includedNodeKeys = allNodes
            .Where(node => ShouldExportNode(kind, node, exportedProvenanceIds))
            .Select(NodeStableKey)
            .ToHashSet(StringComparer.Ordinal);
        var warnings = new List<string>();
        var outgoingReferences = await db.ProjectReferences
            .AsNoTracking()
            .Include(reference => reference.ReferencedProject)
            .Where(reference => reference.ReferencingProjectId == projectId)
            .OrderBy(reference => reference.ReferencedProject.Name)
            .ToListAsync(cancellationToken);
        if (outgoingReferences.Count > 0)
        {
            var names = string.Join(", ", outgoingReferences.Select(reference => reference.ReferencedProject.Name));
            warnings.Add(ProjectExportWarningText.OutgoingReferencesOmitted(outgoingReferences.Count, names));
        }
        var exportedIngestSources = kind == ProjectExportKind.Full
            ? (await db.IngestSources
                .AsNoTracking()
                .Include(source => source.SourceChunks)
                .Include(source => source.SourcePages)
                .Include(source => source.SourceBlocks)
                .Where(source => source.ProjectId == projectId && canonSourceIds.Contains(source.Id))
                .OrderBy(source => source.Title)
                .ToListAsync(cancellationToken))
                .Select(ProjectIngestSource)
                .ToList()
            : [];
        if (kind == ProjectExportKind.NonStructural && allProjectSourceIds.Count > 0)
            warnings.Add($"Omitted {allProjectSourceIds.Count} ingested source body/bodies, all source evidence, and {canonSourceIds.Count} Book Brief canonical selection(s) from this non-structural export.");
        var visualExamples = await db.EntityVisualExamples
            .AsNoTracking()
            .Include(example => example.GraphNode)
            .Include(example => example.SourceVisualCandidate)
            .Where(example => example.ProjectId == projectId)
            .OrderBy(example => example.SortOrder)
            .ToListAsync(cancellationToken);
        var exportedVisualExamples = visualExamples
            .Where(example => includedNodeKeys.Contains(NodeStableKey(example.GraphNode)))
            .Select(example => new ProjectExportEntityVisualExample(
                new ProjectExportNodeRef(example.GraphNode.NodeType, example.GraphNode.Key),
                example.ImageId,
                example.Label,
                example.SortOrder,
                example.Origin,
                example.SourceVisualCandidate?.SourceUrl ?? string.Empty,
                example.SourceVisualCandidate?.Locator ?? string.Empty))
            .ToList();
        var referencedVisualImageIds = exportedVisualExamples.Select(example => example.ImageId).ToHashSet();

        var exportedEdges = new List<ProjectExportEdge>();
        foreach (var edge in await edges.ListByProjectAsync(projectId, cancellationToken))
        {
            if (!nodeById.TryGetValue(edge.FromNodeId, out var from) || !nodeById.TryGetValue(edge.ToNodeId, out var to))
                continue;

            var fromKey = NodeStableKey(from);
            var toKey = NodeStableKey(to);
            if (includedNodeKeys.Contains(fromKey) && includedNodeKeys.Contains(toKey))
            {
                exportedEdges.Add(ProjectEdge(edge, from, to, omittedSourceIds));
                continue;
            }

            if (kind == ProjectExportKind.NonStructural
                && !string.Equals(edge.EdgeType, EntityService.HasChildEdgeType, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"Skipped {edge.EdgeType} edge from {from.NodeType}/{from.Label ?? from.Key} to {to.NodeType}/{to.Label ?? to.Key} because one endpoint is structural.");
            }
        }

        var document = new ProjectExportDocument
        {
            ExportKind = kind,
            Project = new ProjectExportProject(
                project.Id,
                project.Name,
                project.Slug,
                project.ProjectGuidance,
                project.IncludeCurrentChapterInContext,
                project.AiChangeApprovalEnabled),
            PageSetup = await db.ProjectPageSetups.AsNoTracking()
                .Where(item => item.ProjectId == projectId)
                .Select(item => new ProjectExportPageSetup(
                    item.PageWidthInches,
                    item.PageHeightInches,
                    item.PageMarginInches,
                    item.BodyFontSizePoints,
                    item.BodyLineHeight))
                .SingleOrDefaultAsync(cancellationToken),
            BookBrief = await db.BookBriefs
                .AsNoTracking()
                .Where(brief => brief.ProjectId == projectId)
                .Select(brief => new ProjectExportBookBrief(
                    brief.BookKind,
                    brief.Premise,
                    brief.Genre,
                    brief.PrimaryThemes,
                    brief.Purpose,
                    brief.CreativeConstraints,
                    brief.TargetAudience,
                    brief.MinimumReaderAge,
                    brief.MaximumReaderAge,
                    brief.ReadingLevelGuidance,
                    brief.TargetWordCount,
                    brief.PointOfView,
                    brief.Tense,
                    brief.VoiceAndTone,
                    brief.LanguageLocale,
                    brief.HouseStyle,
                    brief.ReadAloudPriority,
                    brief.AccessibilityGoals,
                    brief.VisualDirection))
                .SingleOrDefaultAsync(cancellationToken),
            IngestSources = exportedIngestSources,
            BookBriefCanonSourceIds = kind == ProjectExportKind.Full ? canonSourceIds : [],
            EntityTypes = (await entityTypes.ListByProjectAsync(projectId, cancellationToken))
                .Where(type => ShouldExportType(kind, type))
                .Select(ProjectEntityType)
                .ToList(),
            Images = await db.PublishAssets
                    .AsNoTracking()
                    .Where(asset => asset.ProjectId == projectId
                        && (kind == ProjectExportKind.Full || referencedVisualImageIds.Contains(asset.Id)))
                    .OrderBy(asset => asset.CreatedAt)
                    .Select(asset => ProjectImage(asset))
                    .ToListAsync(cancellationToken),
            EntityVisualExamples = exportedVisualExamples,
            PublicationBook = kind == ProjectExportKind.Full
                ? ProjectPublicationBook(await db.PublicationBooks.AsNoTracking()
                    .Include(item => item.OutlineItems).Include(item => item.CoverDesign)
                    .Include(item => item.PdfPresentation)
                    .SingleOrDefaultAsync(item => item.ProjectId == projectId, cancellationToken))
                : null,
            PublicationEditions = kind == ProjectExportKind.Full
                ? (await db.PublicationEditions
                    .AsNoTracking()
                    .Include(edition => edition.OutlineItems)
                    .Include(edition => edition.ChapterOverrides)
                    .Include(edition => edition.CoverDesign)
                    .Where(profile => profile.ProjectId == projectId)
                    .OrderBy(profile => profile.CreatedAt)
                    .ToListAsync(cancellationToken))
                    .Select(ProjectPublicationEdition)
                    .ToList()
                : [],
            PageCompositions = kind == ProjectExportKind.Full
                ? (await db.PageCompositions
                    .AsNoTracking()
                    .Include(composition => composition.Variants.Where(variant => variant.DetachedAt == null))
                    .Where(composition => composition.ProjectId == projectId && composition.DetachedAt == null)
                    .OrderBy(composition => composition.ChapterId)
                    .ThenBy(composition => composition.CreatedAt)
                    .ToListAsync(cancellationToken))
                    .Select(composition => new ProjectExportPageComposition(
                        composition.Id,
                        composition.ChapterId,
                        composition.Name,
                        composition.SemanticManuscriptJson,
                        composition.Revision,
                        composition.Variants
                            .OrderBy(variant => variant.GeometryKey, StringComparer.Ordinal)
                            .Select(variant => new ProjectExportPageCompositionVariant(
                                variant.Id,
                                variant.GeometryKey,
                                variant.SceneJson,
                                variant.Revision))
                            .ToList(),
                        composition.ActiveAuthoringVariantId)
                    {
                        EditionId = composition.EditionId,
                        SourceCompositionId = composition.SourceCompositionId,
                        PublicationSectionId = composition.PublicationSectionId,
                    })
                    .ToList()
                : [],
            PublicationSections = kind == ProjectExportKind.Full
                ? await db.PublicationSections.AsNoTracking()
                    .Where(section => section.ProjectId == projectId)
                    .OrderBy(section => section.EditionId)
                    .ThenBy(section => section.Anchor)
                    .ThenBy(section => section.LocalOrder)
                    .Select(section => new ProjectExportPublicationSection(
                        section.Id,
                        section.EditionId,
                        section.CoreSectionId,
                        section.Title,
                        section.Kind,
                        section.SystemRole,
                        section.Anchor,
                        section.TargetKind,
                        section.TargetId,
                        section.InclusionMode,
                        section.StartSide,
                        section.IsExcluded,
                        section.LocalOrder,
                        section.ManuscriptJson,
                        section.Revision,
                        section.CreatedAt,
                        section.UpdatedAt))
                    .ToListAsync(cancellationToken)
                : [],
            ManuscriptStyles = kind == ProjectExportKind.Full
                ? (await db.ManuscriptStyleDefinitions
                    .AsNoTracking()
                    .Where(style => style.ProjectId == projectId)
                    .OrderBy(style => style.Kind)
                    .ThenBy(style => style.Name)
                    .ToListAsync(cancellationToken))
                    .Select(style => new ProjectExportManuscriptStyle(
                        style.Id,
                        style.Name,
                        style.Kind,
                        style.SemanticRole,
                        ManuscriptStyleService.NormalizeDefinition(
                            JsonSerializer.Deserialize<ManuscriptStyleProperties>(
                                style.DefinitionJson,
                                ManuscriptCodec.JsonOptions) ?? new ManuscriptStyleProperties()),
                        style.Revision))
                    .ToList()
                : [],
            FontFamilies = kind == ProjectExportKind.Full
                ? (await db.ProjectFontFamilies
                    .AsNoTracking()
                    .Include(family => family.Faces)
                    .Where(family => family.ProjectId == projectId)
                    .OrderBy(family => family.Name)
                    .ToListAsync(cancellationToken))
                    .Select(family => new ProjectExportFontFamily(
                        family.Id,
                        family.Name,
                        family.Faces
                            .OrderBy(face => face.Weight)
                            .ThenBy(face => face.Italic)
                            .Select(face => new ProjectExportFontFace(
                                face.Id,
                                face.SubfamilyName,
                                face.FileName,
                                face.ContentType,
                                face.Weight,
                                face.Italic,
                                face.Data,
                                Convert.ToHexStringLower(SHA256.HashData(face.Data))))
                            .ToList(),
                        family.EmbeddingRightsConfirmed,
                        family.RightsDeclaration))
                    .ToList()
                : [],
            Acts = kind == ProjectExportKind.Full
                ? (await acts.ListByProjectAsync(projectId, cancellationToken)).Select(ProjectAct).ToList()
                : [],
            Chapters = kind == ProjectExportKind.Full
                ? (await chapters.ListByProjectAsync(projectId, cancellationToken))
                    .Select(chapter => ProjectChapter(chapter, exportedImageContextIds))
                    .ToList()
                : [],
            ManuscriptAnnotations = await db.ManuscriptAnnotations.AsNoTracking()
                .Where(annotation => annotation.ProjectId == projectId)
                .OrderBy(annotation => annotation.ChapterId)
                .ThenBy(annotation => annotation.CreatedAt)
                .Select(annotation => new ProjectExportManuscriptAnnotation(
                    annotation.Id,
                    annotation.ChapterId,
                    annotation.Chapter.Title,
                    annotation.EditionId,
                    annotation.Edition == null ? null : annotation.Edition.Name,
                    annotation.Kind,
                    annotation.NoteText,
                    annotation.Revision,
                    annotation.AnchorManuscriptRevision,
                    annotation.AnchorState,
                    annotation.StartBlockId,
                    annotation.StartOffset,
                    annotation.EndBlockId,
                    annotation.EndOffset,
                    annotation.OriginalQuote,
                    annotation.ContextBefore,
                    annotation.ContextAfter,
                    annotation.CreatedAt,
                    annotation.UpdatedAt))
                .ToListAsync(cancellationToken),
            Nodes = allNodes
                .Where(node => includedNodeKeys.Contains(NodeStableKey(node)))
                .Select(node => ProjectNode(node, omittedSourceIds))
                .ToList(),
            Edges = exportedEdges,
            Warnings = warnings,
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        return new ProjectExportFile(
            FileName: $"{SafeFileName(project.Slug)}-{kind.ToString().ToLowerInvariant()}-graph.lorekeeper.json",
            ContentType: "application/json; charset=utf-8",
            Content: bytes,
            Warnings: warnings);
    }

    public async Task<ProjectImportJobListItem> CreateImportJobAsync(
        Guid projectId,
        string fileName,
        string contentJson,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var projects = databaseOperation.Repositories.Projects;
        var imports = databaseOperation.Repositories.ProjectImports;
        _ = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");
        if (string.IsNullOrWhiteSpace(contentJson))
            throw new ArgumentException("Import file is empty.", nameof(contentJson));

        var (formatId, formatVersion, exportKind) = ReadEnvelope(contentJson);
        var job = new ProjectImportJob
        {
            ProjectId = projectId,
            FileName = string.IsNullOrWhiteSpace(fileName) ? "import.lorekeeper.json" : fileName.Trim(),
            ContentJson = contentJson,
            FormatId = formatId,
            FormatVersion = formatVersion,
            ExportKind = exportKind,
            Status = ProjectImportJobStatus.Queued,
            TotalSteps = 9,
            CurrentMessage = "Queued for import.",
        };

        await imports.AddJobAsync(job, cancellationToken);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        importQueue.Enqueue(job.Id);
        Notify(job.ProjectId, job.Id, ProjectImportJobUpdateKind.Created);
        Notify(job.ProjectId, job.Id, ProjectImportJobUpdateKind.Queued);

        return (await imports.ListJobSummariesByProjectAsync(projectId, cancellationToken))
            .First(item => item.Id == job.Id);
    }

    public async Task<IReadOnlyList<ProjectImportJobListItem>> ListImportJobsAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var imports = databaseOperation.Repositories.ProjectImports;
        return await imports.ListJobSummariesByProjectAsync(projectId, cancellationToken);
    }
    public async Task<ProjectImportJobDetailView?> GetImportJobDetailAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var imports = databaseOperation.Repositories.ProjectImports;
        return await imports.GetJobDetailViewAsync(jobId, cancellationToken);
    }
    public async Task DeleteImportJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var imports = databaseOperation.Repositories.ProjectImports;
        var job = await imports.GetJobAsync(jobId, cancellationToken);
        if (job is null) return;
        if (job.Status == ProjectImportJobStatus.Running)
            throw new InvalidOperationException("Running imports cannot be deleted.");

        var projectId = job.ProjectId;
        imports.RemoveJob(job);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        Notify(projectId, jobId, ProjectImportJobUpdateKind.Deleted);
    }

    private static (string FormatId, int FormatVersion, string ExportKind) ReadEnvelope(string contentJson)
    {
        try
        {
            using var document = JsonDocument.Parse(contentJson);
            var root = document.RootElement;
            var formatId = root.TryGetProperty("formatId", out var formatIdElement) ? formatIdElement.GetString() ?? string.Empty : string.Empty;
            var version = root.TryGetProperty("formatVersion", out var versionElement) && versionElement.TryGetInt32(out var parsedVersion) ? parsedVersion : 0;
            var exportKind = root.TryGetProperty("exportKind", out var kindElement) ? kindElement.GetString() ?? string.Empty : string.Empty;
            return (formatId, version, exportKind);
        }
        catch (JsonException)
        {
            return (string.Empty, 0, string.Empty);
        }
    }

    private static bool ShouldExportNode(
        ProjectExportKind kind,
        GraphNode node,
        IReadOnlySet<string> exportedProvenanceIds)
    {
        if (kind == ProjectExportKind.NonStructural)
            return !NonStructuralExcludedNodeTypes.Contains(node.NodeType);
        if (node.NodeType is EntityTypeService.SourceNodeType
            or EntityTypeService.SourceChunkNodeType
            or EntityTypeService.SourceBlockNodeType)
        {
            return exportedProvenanceIds.Contains(node.Key);
        }
        return true;
    }

    private static bool ShouldExportType(ProjectExportKind kind, GraphEntityType type) =>
        kind == ProjectExportKind.Full || !NonStructuralExcludedNodeTypes.Contains(type.Type);

    private static ProjectExportNode ProjectNode(GraphNode node, IReadOnlySet<Guid> omittedSourceIds)
    {
        var properties = new Dictionary<string, object?>(node.Properties);
        foreach (var sourceId in omittedSourceIds)
        {
            IngestSourceAssertions.RemoveEntitySource(properties, sourceId);
            IngestWikiSheet.RemoveSourceEvidence(properties, sourceId);
        }
        return new(node.NodeType, node.Key, node.Label, properties, node.CreatedAt, node.UpdatedAt);
    }

    private static ProjectExportIngestSource ProjectIngestSource(IngestSource source) => new(
        source.Id,
        source.Title,
        source.SourceKind,
        source.Description,
        source.Synopsis,
        source.UserInstructions,
        source.SourceText,
        source.SourceHash,
        source.SourceUrl,
        source.FinalUrl,
        source.CanonicalUrl,
        source.FetchedAt,
        source.ContentType,
        source.SourceMetadataJson,
        source.CreatedAt,
        source.UpdatedAt,
        source.SourceChunks.OrderBy(chunk => chunk.Index).Select(chunk => new ProjectExportIngestSourceChunk(
            chunk.Id, chunk.Index, chunk.Title, chunk.HeadingPath, chunk.StartChar, chunk.EndChar,
            chunk.EstimatedTokenCount, chunk.TokenCountMethod, chunk.TokenEncodingName, chunk.TokenCountIsExact,
            chunk.Summary, chunk.AgentNotes, chunk.StructureStatus, chunk.CreatedAt, chunk.UpdatedAt)).ToList(),
        source.SourcePages.OrderBy(page => page.PageNumber).Select(page => new ProjectExportIngestSourcePage(
            page.Id, page.PageNumber, page.Text, page.StartChar, page.EndChar, page.ExtractionMethod,
            page.Width, page.Height, page.ImageHash, page.RenderSettingsJson, page.VisionProviderId,
            page.VisionModelName, page.Diagnostics, page.CreatedAt)).ToList(),
        source.SourceBlocks.OrderBy(block => block.Index).Select(block => new ProjectExportIngestSourceBlock(
            block.Id, block.SourcePageId, block.Index, block.Kind, block.Title, block.Locator,
            block.PageNumber, block.StartChar, block.EndChar, block.MetadataJson, block.CreatedAt)).ToList());

    private static ProjectExportEdge ProjectEdge(
        GraphEdge edge,
        GraphNode from,
        GraphNode to,
        IReadOnlySet<Guid> omittedSourceIds)
    {
        var properties = new Dictionary<string, object?>(edge.Properties);
        foreach (var sourceId in omittedSourceIds)
        {
            IngestSourceAssertions.RemoveRelationshipSource(properties, sourceId);
            IngestWikiSheet.RemoveSourceEvidence(properties, sourceId);
        }
        return new(
            new ProjectExportNodeRef(from.NodeType, from.Key),
            new ProjectExportNodeRef(to.NodeType, to.Key),
            edge.EdgeType,
            new Dictionary<string, object?>(edge.Properties),
            edge.SortOrder,
            edge.CreatedAt,
            edge.UpdatedAt);
    }

    private static ProjectExportEntityType ProjectEntityType(GraphEntityType type) =>
        new(
            type.Type,
            type.SingularLabel,
            type.PluralLabel,
            type.Color,
            type.Icon,
            type.IsStructural,
            type.IsChapterScoped,
            type.SortOrder,
            new Dictionary<string, object?>(type.DefaultProperties));

    private static ProjectExportAct ProjectAct(Act act) =>
        new(act.Id, act.Title, act.Synopsis, act.Order);

    private static ProjectExportImage ProjectImage(PublishAsset asset) =>
        new(
            asset.Id,
            asset.FileName,
            asset.ContentType,
            asset.Data,
            asset.AltText,
            asset.Source,
            asset.Prompt,
            asset.GenerationModel,
            asset.SourceMetadataJson,
            asset.DerivedFromImageId,
            asset.CropXPercent,
            asset.CropYPercent,
            asset.CropWidthPercent,
            asset.CropHeightPercent,
            asset.CreatedAt,
            asset.UpdatedAt);

    private static ProjectExportPublicationEdition ProjectPublicationEdition(PublicationEdition profile) =>
        new ProjectExportPublicationEdition(
            profile.Id,
            profile.Name,
            profile.Format,
            profile.Vendor,
            profile.VendorProfileVersion,
            profile.Status,
            false,
            profile.Revision,
            profile.TitleOverride,
            profile.Subtitle,
            profile.Author,
            profile.Language,
            profile.Publisher,
            profile.Copyright,
            profile.Isbn,
            profile.Description,
            profile.IncludeTableOfContents,
            profile.IncludeVisibleTableOfContents,
            profile.IncludeActSynopses,
            profile.IncludeChapterSynopses,
            profile.IncludeActHeadings,
            profile.IncludeChapterHeadings,
            profile.NumberActs,
            profile.NumberChapters,
            profile.TitlePageMode,
            profile.PageWidthInches,
            profile.PageHeightInches,
            profile.PageMarginInches,
            profile.SelectedCoverImageId,
            profile.Format is PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover ? LegacyPublicationBinding.PerfectBound : LegacyPublicationBinding.Digital,
            profile.PrintProductKey.Contains("cream", StringComparison.Ordinal) ? LegacyPublicationPaper.Cream : profile.Format is PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover ? LegacyPublicationPaper.White : LegacyPublicationPaper.Digital,
            profile.PrintProductKey.Contains("color", StringComparison.Ordinal) ? LegacyPublicationInk.Color : profile.Format is PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover ? LegacyPublicationInk.BlackAndWhite : LegacyPublicationInk.Digital,
            profile.Bleed,
            profile.AllowDesignedPageOverrides,
            profile.OutlineItems
                .OrderBy(item => item.SortOrder)
                .Select(item => new ProjectExportEditionOutlineItem(
                    item.Id,
                    item.TargetKind,
                    item.TargetId,
                    item.IsIncluded,
                    item.SortOrder))
                .ToList(),
            profile.CoverDesign is null ? null : new ProjectExportCoverDesign(
                profile.CoverDesign.Title,
                profile.CoverDesign.Subtitle,
                profile.CoverDesign.Author,
                profile.CoverDesign.SpineText,
                profile.CoverDesign.BackCopy,
                profile.CoverDesign.BackgroundColor,
                profile.CoverDesign.BarcodeMode,
                profile.CoverDesign.ImageCropXPercent,
                profile.CoverDesign.ImageCropYPercent,
                profile.CoverDesign.CompositionSceneJson,
                profile.CoverDesign.Revision)
            {
                SurfaceScenesJson = profile.CoverDesign.SurfaceScenesJson,
            })
        {
            PrintRegistryVersion = profile.PrintRegistryVersion,
            PrintProductKey = profile.PrintProductKey,
            PrintFinish = profile.PrintFinish,
            PrintCoverMode = profile.PrintCoverMode,
            GenericPrintTemplateJson = profile.GenericPrintTemplateJson,
            OverrideFields = ParseOverrideFields(profile.OverrideFieldsJson),
            InheritsCoreCover = profile.InheritsCoreCover,
            EditionSpecificContentEnabled = profile.EditionSpecificContentEnabled,
            PublicationSectionOrder = PublicationSectionOrderCodec.Deserialize(profile.PublicationSectionOrderJson),
            ChapterOverrides = profile.ChapterOverrides
                .OrderBy(item => item.ChapterId)
                .Select(item => new ProjectExportEditionChapterOverride(
                    item.Id,
                    item.ChapterId,
                    item.ManuscriptJson,
                    item.Revision,
                    item.BaseCoreRevision,
                    item.BaseCoreHash,
                    item.CreatedAt,
                    item.UpdatedAt))
                .ToList(),
        };

    private static ProjectExportPublicationBook? ProjectPublicationBook(PublicationBook? book) => book is null ? null : new(
        book.Revision, book.Title, book.Subtitle, book.Author, book.Language, book.Publisher, book.Copyright,
        book.Description, book.IncludeTableOfContents, book.IncludeVisibleTableOfContents, book.IncludeActSynopses,
        book.IncludeChapterSynopses, book.IncludeActHeadings, book.IncludeChapterHeadings, book.NumberActs,
        book.NumberChapters, book.TitlePageMode,
        book.OutlineItems.OrderBy(item => item.SortOrder).Select(item => new ProjectExportEditionOutlineItem(
            item.Id, item.TargetKind, item.TargetId, item.IsIncluded, item.SortOrder)).ToList(),
        book.CoverDesign is null ? null : new ProjectExportCoverDesign(
            book.Title, book.Subtitle, book.Author, string.Empty, string.Empty, book.CoverDesign.BackgroundColor,
            PublicationBarcodeMode.None, 50, 50, book.CoverDesign.CompositionSceneJson, book.CoverDesign.Revision))
    {
        AllowDesignedPageOverrides = book.PdfPresentation?.AllowDesignedPageOverrides ?? false,
    };

    private static List<PublicationEditionOverrideField> ParseOverrideFields(string json)
    {
        try { return JsonSerializer.Deserialize<List<PublicationEditionOverrideField>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    private static ProjectExportChapter ProjectChapter(
        Chapter chapter,
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> exportedImageContextIds) =>
        new()
        {
            Id = chapter.Id,
            ActId = chapter.ActId,
            Title = chapter.Title,
            ManuscriptJson = chapter.ManuscriptJson,
            ManuscriptRevision = chapter.ManuscriptRevision,
            Synopsis = chapter.Synopsis,
            Order = chapter.Order,
            ExplicitImageContextImageIds = exportedImageContextIds.TryGetValue(chapter.Id, out var imageIds)
                ? imageIds.ToList()
                : [],
        };

    private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> ListExportedImageContextIdsAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var preferences = await db.EditorContextPreferences
            .AsNoTracking()
            .Where(preference =>
                preference.ProjectId == projectId
                && preference.IsIncluded
                && preference.Kind == ContextItemKind.ProjectImage.ToString())
            .ToListAsync(cancellationToken);

        return preferences
            .Select(preference => new
            {
                preference.ChapterId,
                ImageId = EditorContextKeys.TryParseProjectImage(preference.Key, out var imageId)
                    ? imageId
                    : (Guid?)null,
            })
            .Where(item => item.ImageId is not null)
            .GroupBy(item => item.ChapterId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<Guid>)group.Select(item => item.ImageId!.Value).Distinct().ToList());
    }

    private static string NodeStableKey(GraphNode node) =>
        $"{node.NodeType}/{node.Key}";

    private static string SafeFileName(string input)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var chars = input.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray();
        var result = new string(chars).Trim('-', ' ', '.');
        return string.IsNullOrWhiteSpace(result) ? "project" : result;
    }

    private void Notify(Guid projectId, Guid jobId, ProjectImportJobUpdateKind kind) =>
        notifier.Notify(new ProjectImportJobUpdate(projectId, jobId, kind, DateTime.UtcNow));
}
