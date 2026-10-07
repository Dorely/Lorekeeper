using System.Text.Json;
using Lorekeeper.Context;
using Lorekeeper.Ingest;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence;
using Lorekeeper.Persistence.Repositories;
using Lorekeeper.Publish;
using Lorekeeper.ProjectArchive;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.ImportExport;

public sealed class ProjectImportExportService(
    IAppDatabaseOperationFactory database,
    IProjectImportJobQueue importQueue,
    IProjectImportJobNotifier notifier,
    IProjectImportFileStore? fileStore = null,
    ILogger<ProjectImportExportService>? logger = null,
    ProjectArchiveLimits? archiveLimits = null) : IProjectImportExportService
{
    private readonly IProjectImportFileStore _fileStore = fileStore ?? new ProjectImportFileStore();
    private readonly ILogger<ProjectImportExportService> _logger = logger ?? NullLogger<ProjectImportExportService>.Instance;
    private readonly ProjectArchiveLimits _archiveLimits = archiveLimits ?? ProjectArchiveLimits.Default;
    private static readonly HashSet<string> NonStructuralExcludedNodeTypes =
    [
        EntityTypeService.ProjectNodeType,
        EntityTypeService.ActNodeType,
        EntityTypeService.ChapterNodeType,
        EntityTypeService.EventNodeType,
        EntityTypeService.SourceNodeType,
        EntityTypeService.SourceChunkNodeType,
        EntityTypeService.SourceBlockNodeType,
    ];

    public async Task<ProjectArchiveDocumentCapture> CaptureArchiveDocumentAsync(
        Guid projectId,
        ProjectExportKind kind,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var acts = databaseOperation.Repositories.Acts;
        var chapters = databaseOperation.Repositories.Chapters;
        var nodes = databaseOperation.Repositories.GraphNodes;
        var edges = databaseOperation.Repositories.GraphEdges;
        var entityTypes = databaseOperation.Repositories.GraphEntityTypes;
        var projects = databaseOperation.Repositories.Projects;
        var project = await projects.GetSnapshotByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");
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
                : Enumerable.Empty<Guid>())
            .ToHashSet();
        var exportedProvenanceIds = allProjectSourceIds
            .Concat(await db.IngestSourceChunks.AsNoTracking()
                .Where(chunk => allProjectSourceIds.Contains(chunk.SourceId))
                .Select(chunk => chunk.Id)
                .ToListAsync(cancellationToken))
            .Concat(await db.IngestSourceBlocks.AsNoTracking()
                .Where(block => allProjectSourceIds.Contains(block.SourceId))
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
            .Where(reference => reference.ReferencingProjectId == projectId)
            .OrderBy(reference => reference.ReferencedProjectName)
            .ToListAsync(cancellationToken);
        if (outgoingReferences.Count > 0)
        {
            var names = string.Join(", ", outgoingReferences.Select(reference =>
                reference.ResolvedProjectId is null
                    ? $"{reference.ReferencedProjectName} (unresolved)"
                    : reference.ReferencedProjectName));
            warnings.Add(ProjectExportWarningText.OutgoingReferencesOmitted(outgoingReferences.Count, names));
        }
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
        HashSet<Guid>? exportedImageIds = null;
        if (kind == ProjectExportKind.NonStructural)
        {
            exportedImageIds = referencedVisualImageIds;
            var imageParents = await db.PublishAssets
                .AsNoTracking()
                .Where(asset => asset.ProjectId == projectId)
                .Select(asset => new { asset.Id, asset.DerivedFromImageId })
                .ToListAsync(cancellationToken);
            var parentByImageId = imageParents.ToDictionary(item => item.Id, item => item.DerivedFromImageId);
            var pending = new Queue<Guid>(exportedImageIds);
            while (pending.TryDequeue(out var imageId))
            {
                if (parentByImageId.GetValueOrDefault(imageId) is not Guid parentId
                    || !exportedImageIds.Add(parentId))
                {
                    continue;
                }

                pending.Enqueue(parentId);
            }
        }

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
                project.ReviewEditsEnabled),
            PageSetup = await db.ProjectPageSetups.AsNoTracking()
                .Where(item => item.ProjectId == projectId)
                .Select(item => new ProjectExportPageSetup(
                    item.PageWidthInches,
                    item.PageHeightInches,
                    item.PageMarginInches,
                    item.BodyFontSizePoints,
                    item.BodyLineHeight))
                .SingleOrDefaultAsync(cancellationToken),
            WorldBrief = await db.WorldBriefs.AsNoTracking().Where(brief => brief.ProjectId == projectId).Select(brief => brief.Content).SingleOrDefaultAsync(cancellationToken) ?? "",
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
            IngestSources = [],
            BookBriefCanonSourceIds = kind == ProjectExportKind.Full ? canonSourceIds : [],
            EntityTypes = (await entityTypes.ListByProjectAsync(projectId, cancellationToken))
                .Where(type => ShouldExportType(kind, type))
                .Select(ProjectEntityType)
                .ToList(),
            Images = await ListExportImagesAsync(
                db, projectId, kind, exportedImageIds, cancellationToken),
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
            // v31 writes only project-owned Designed Pages. v1-v30
            // PageComposition payloads remain read-only import adapters.
            LegacyPageCompositions = null,
            DesignedPages = kind == ProjectExportKind.Full
                ? (await db.DesignedPages
                    .AsNoTracking()
                    .Include(page => page.Contents)
                    .ThenInclude(content => content.Variants)
                    .Where(page => page.ProjectId == projectId)
                    .OrderBy(page => page.ScopeEditionId)
                    .ThenBy(page => page.CreatedAt)
                    .ToListAsync(cancellationToken))
                    .Select(page => new ProjectExportDesignedPage(
                        page.Id,
                        page.Name,
                        page.ScopeEditionId,
                        page.Contents
                            .OrderBy(content => content.EditionId)
                            .ThenBy(content => content.Id)
                            .Select(content => new ProjectExportDesignedPageContent(
                                content.Id,
                                content.DesignedPageId,
                                content.EditionId,
                                content.SemanticManuscriptJson,
                                content.AccessibilityDescription,
                                content.Revision,
                                content.Variants
                                    .OrderBy(variant => variant.GeometryKey, StringComparer.Ordinal)
                                    .Select(variant => new ProjectExportDesignedPageVariant(
                                        variant.Id,
                                        variant.ContentId,
                                        variant.GeometryKey,
                                        variant.SceneJson,
                                        variant.Revision))
                                    .ToList(),
                                content.ActiveVariantId))
                            .ToList()))
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
                ? await ListExportFontFamiliesAsync(db, projectId, cancellationToken)
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

        return new ProjectArchiveDocumentCapture(document, project.Slug);
    }

    public async Task<ProjectImportJobListItem> CreateImportJobAsync(
        Guid projectId,
        string fileName,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var normalizedFileName = NormalizeImportFileName(fileName);
        var inputKind = InferInputKind(normalizedFileName);
        var maximumBytes = inputKind == ProjectImportInputKind.LegacyJson
            ? _archiveLimits.MaximumLegacyJsonBytes : _archiveLimits.MaximumCompressedBytes;
        var staged = await _fileStore.StageAsync(content, normalizedFileName, maximumBytes, cancellationToken);
        try
        {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var projects = databaseOperation.Repositories.Projects;
        var imports = databaseOperation.Repositories.ProjectImports;
        _ = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");
        var job = new ProjectImportJob
        {
            ProjectId = projectId,
            FileName = normalizedFileName,
            StagedFileKey = staged.Key.Value,
            StagedLength = staged.Length,
            StagedSha256 = staged.Sha256,
            InputKind = inputKind,
            Status = ProjectImportJobStatus.Staged,
            TotalSteps = 9,
            CurrentMessage = "Import staged for validation.",
        };

        var terminalJobs = await databaseOperation.Db.ProjectImportJobs
            .Where(item => item.ProjectId == projectId
                && (item.Status == ProjectImportJobStatus.Completed
                    || item.Status == ProjectImportJobStatus.CompletedWithWarnings
                    || item.Status == ProjectImportJobStatus.Failed
                    || item.Status == ProjectImportJobStatus.Cancelled))
            .ToListAsync(cancellationToken);
        var terminalFileKeys = terminalJobs
            .Select(item => new ProjectImportJobFileKey(item.StagedFileKey))
            .ToArray();
        databaseOperation.Db.ProjectImportJobs.RemoveRange(terminalJobs);
        await imports.AddJobAsync(job, cancellationToken);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        foreach (var terminalFileKey in terminalFileKeys)
            TryDeleteStagedFile(terminalFileKey);
        importQueue.Enqueue(job.Id);
        Notify(job.ProjectId, job.Id, ProjectImportJobUpdateKind.Created);
        Notify(job.ProjectId, job.Id, ProjectImportJobUpdateKind.Queued);

        return (await imports.ListJobSummariesByProjectAsync(projectId, cancellationToken))
            .First(item => item.Id == job.Id);
        }
        catch
        {
            _fileStore.Delete(staged.Key);
            throw;
        }
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
        if (job.Status is ProjectImportJobStatus.Applying or ProjectImportJobStatus.Committed or ProjectImportJobStatus.Indexing)
            throw new InvalidOperationException("An applying or committed import cannot be deleted.");

        var projectId = job.ProjectId;
        var key = new ProjectImportJobFileKey(job.StagedFileKey);
        imports.RemoveJob(job);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        TryDeleteStagedFile(key);
        Notify(projectId, jobId, ProjectImportJobUpdateKind.Deleted);
    }

    private static string NormalizeImportFileName(string fileName)
    {
        var value = string.IsNullOrWhiteSpace(fileName) ? "import.lorekeeper" : Path.GetFileName(fileName.Trim());
        if (value.Length > 240 || value.IndexOf('\0') >= 0)
            throw new ArgumentException("Import file name is invalid.", nameof(fileName));
        return value;
    }

    private static ProjectImportInputKind InferInputKind(string fileName) =>
        fileName.EndsWith(".lorekeeper", StringComparison.OrdinalIgnoreCase)
            ? ProjectImportInputKind.LorekeeperArchive
            : fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? ProjectImportInputKind.LegacyJson
                : throw new InvalidDataException("Imports must be .lorekeeper archives or legacy .json project files.");

    private void TryDeleteStagedFile(ProjectImportJobFileKey key)
    {
        try
        {
            _fileStore.Delete(key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remove staged project import file {FileKey}", key.Value);
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
            properties,
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

    private static async Task<List<ProjectExportImage>> ListExportImagesAsync(
        AppDbContext db,
        Guid projectId,
        ProjectExportKind kind,
        IReadOnlySet<Guid>? selectedImageIds,
        CancellationToken cancellationToken)
    {
        var query = db.PublishAssets.AsNoTracking()
            .Where(asset => asset.ProjectId == projectId
                && (kind == ProjectExportKind.Full || selectedImageIds!.Contains(asset.Id)))
            .OrderBy(asset => asset.CreatedAt);
        var metadata = await query.Select(asset => new ImageMetadata(
            asset.Id, asset.FileName, asset.ContentType, asset.AltText, asset.Source, asset.Prompt,
            asset.GenerationModel, asset.SourceMetadataJson, asset.DerivedFromImageId, asset.CropXPercent,
            asset.CropYPercent, asset.CropWidthPercent, asset.CropHeightPercent, asset.CreatedAt,
            asset.UpdatedAt)).ToListAsync(cancellationToken);
        return metadata.Select(asset => new ProjectExportImage(
            asset.Id, asset.FileName, asset.ContentType, [], asset.AltText, asset.Source, asset.Prompt,
            asset.GenerationModel, asset.SourceMetadataJson, asset.DerivedFromImageId, asset.CropXPercent,
            asset.CropYPercent, asset.CropWidthPercent, asset.CropHeightPercent, asset.CreatedAt,
            asset.UpdatedAt)).ToList();
    }

    private static async Task<List<ProjectExportFontFamily>> ListExportFontFamiliesAsync(
        AppDbContext db,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var families = await db.ProjectFontFamilies.AsNoTracking()
            .Where(family => family.ProjectId == projectId)
            .OrderBy(family => family.Name)
            .Select(family => new FontFamilyMetadata(family.Id, family.Name, family.EmbeddingRightsConfirmed,
                family.RightsDeclaration))
            .ToListAsync(cancellationToken);
        var familyIds = families.Select(family => family.Id).ToArray();
        var faces = await db.ProjectFontFaces.AsNoTracking()
            .Where(face => familyIds.Contains(face.FamilyId))
            .OrderBy(face => face.Weight)
            .ThenBy(face => face.Italic)
            .Select(face => new FontFaceMetadata(face.Id, face.FamilyId, face.SubfamilyName, face.FileName,
                face.ContentType, face.Weight, face.Italic))
            .ToListAsync(cancellationToken);
        return families.Select(family => new ProjectExportFontFamily(
            family.Id,
            family.Name,
            faces.Where(face => face.FamilyId == family.Id)
                .Select(face => new ProjectExportFontFace(face.Id, face.SubfamilyName, face.FileName,
                    face.ContentType, face.Weight, face.Italic, [], string.Empty))
                .ToList(),
            family.EmbeddingRightsConfirmed,
            family.RightsDeclaration)).ToList();
    }

    private sealed record ImageMetadata(Guid Id, string FileName, string ContentType, string AltText,
        PublishAssetSource Source, string Prompt, string GenerationModel, string SourceMetadataJson,
        Guid? DerivedFromImageId, double? CropXPercent, double? CropYPercent, double? CropWidthPercent,
        double? CropHeightPercent, DateTime CreatedAt, DateTime UpdatedAt);
    private sealed record FontFamilyMetadata(Guid Id, string Name, bool EmbeddingRightsConfirmed, string RightsDeclaration);
    private sealed record FontFaceMetadata(Guid Id, Guid FamilyId, string SubfamilyName, string FileName,
        string ContentType, int Weight, bool Italic);

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
            profile.PrintArtifactProfileKey.Contains("cream", StringComparison.Ordinal) ? LegacyPublicationPaper.Cream : profile.Format is PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover ? LegacyPublicationPaper.White : LegacyPublicationPaper.Digital,
            profile.PrintArtifactProfileKey.Contains("color", StringComparison.Ordinal) ? LegacyPublicationInk.Color : profile.Format is PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover ? LegacyPublicationInk.BlackAndWhite : LegacyPublicationInk.Digital,
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
                profile.CoverDesign.BackgroundColor,
                profile.CoverDesign.BarcodeMode,
                profile.CoverDesign.ImageCropXPercent,
                profile.CoverDesign.ImageCropYPercent,
                profile.CoverDesign.CompositionSceneJson,
                profile.CoverDesign.Revision)
            {
                SurfaceScenesJson = profile.CoverDesign.SurfaceScenesJson,
                SpineReadingDirection = profile.CoverDesign.SpineReadingDirection,
            })
        {
            PrintArtifactRegistryVersion = profile.PrintArtifactRegistryVersion,
            PrintArtifactProfileKey = profile.PrintArtifactProfileKey,
            PrintCoverMode = profile.PrintCoverMode,
            PrintProjectUse = profile.PrintProjectUse,
            PrintIdentifierMode = profile.PrintIdentifierMode,
            PrintCoverSubmissionMode = profile.PrintCoverSubmissionMode,
            PrinterDimensions = PrinterDimensions.From(profile),
            RectoChapterStarts = profile.RectoChapterStarts,
            CitationStyle = profile.CitationStyle,
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
            book.Title, book.Subtitle, book.Author, string.Empty, book.CoverDesign.BackgroundColor,
            PublicationBarcodeMode.None, 50, 50, book.CoverDesign.CompositionSceneJson, book.CoverDesign.Revision))
    {
        AllowDesignedPageOverrides = book.PdfPresentation?.AllowDesignedPageOverrides ?? false,
        RectoChapterStarts = book.RectoChapterStarts,
        CitationStyle = book.CitationStyle,
    };

    private static List<PublicationEditionOverrideField> ParseOverrideFields(string json)
    {
        try
        {
            return (JsonSerializer.Deserialize<List<PublicationEditionOverrideField>>(json) ?? [])
                .Where(Enum.IsDefined)
                .Distinct()
                .Order()
                .ToList();
        }
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
            .OrderBy(preference => preference.ChapterId)
            .ThenBy(preference => preference.SortOrder)
            .ThenBy(preference => preference.Key)
            .ThenBy(preference => preference.Id)
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
                group => (IReadOnlyList<Guid>)group
                    .Select(item => item.ImageId!.Value)
                    .Distinct()
                    .OrderBy(id => id)
                    .ToList());
    }

    private static string NodeStableKey(GraphNode node) =>
        $"{node.NodeType}/{node.Key}";

    private void Notify(Guid projectId, Guid jobId, ProjectImportJobUpdateKind kind) =>
        notifier.Notify(new ProjectImportJobUpdate(projectId, jobId, kind, DateTime.UtcNow));
}
