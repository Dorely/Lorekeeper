using System.Text.Json;
using Lorekeeper.Context;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence;
using Lorekeeper.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.ImportExport;

public sealed class ProjectImportExportService(
    AppDbContext db,
    IProjectRepository projects,
    IActRepository acts,
    IChapterRepository chapters,
    IGraphNodeRepository nodes,
    IGraphEdgeRepository edges,
    IGraphEntityTypeRepository entityTypes,
    IEntityTypeService entityTypeService,
    IProjectImportRepository imports,
    IProjectImportJobQueue importQueue,
    IProjectImportJobNotifier notifier) : IProjectImportExportService
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
        var project = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");
        await entityTypeService.EnsureDefaultsAsync(projectId, cancellationToken);
        var exportedImageContextIds = await ListExportedImageContextIdsAsync(projectId, cancellationToken);

        var allNodes = await nodes.ListByProjectAsync(projectId, cancellationToken);
        var nodeById = allNodes.ToDictionary(node => node.Id);
        var includedNodeKeys = allNodes
            .Where(node => ShouldExportNode(kind, node))
            .Select(NodeStableKey)
            .ToHashSet(StringComparer.Ordinal);
        var warnings = new List<string>();
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
                exportedEdges.Add(ProjectEdge(edge, from, to));
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
                project.SystemPrompt,
                project.IncludeCurrentChapterInContext,
                project.AiChangeApprovalEnabled),
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
            PublishProfiles = kind == ProjectExportKind.Full
                ? await db.PublishProfiles
                    .AsNoTracking()
                    .Where(profile => profile.ProjectId == projectId)
                    .OrderBy(profile => profile.CreatedAt)
                    .Select(profile => ProjectPublishProfile(profile))
                    .ToListAsync(cancellationToken)
                : [],
            Acts = kind == ProjectExportKind.Full
                ? (await acts.ListByProjectAsync(projectId, cancellationToken)).Select(ProjectAct).ToList()
                : [],
            Chapters = kind == ProjectExportKind.Full
                ? (await chapters.ListByProjectAsync(projectId, cancellationToken))
                    .Select(chapter => ProjectChapter(chapter, exportedImageContextIds))
                    .ToList()
                : [],
            Nodes = allNodes
                .Where(node => includedNodeKeys.Contains(NodeStableKey(node)))
                .Select(ProjectNode)
                .ToList(),
            Edges = exportedEdges,
            Warnings = warnings,
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        return new ProjectExportFile(
            FileName: $"{SafeFileName(project.Slug)}-{kind.ToString().ToLowerInvariant()}-graph.lorekeeper.json",
            ContentType: "application/json; charset=utf-8",
            Content: bytes);
    }

    public async Task<ProjectImportJobListItem> CreateImportJobAsync(
        Guid projectId,
        string fileName,
        string contentJson,
        CancellationToken cancellationToken = default)
    {
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
        await imports.SaveChangesAsync(cancellationToken);
        importQueue.Enqueue(job.Id);
        Notify(job.ProjectId, job.Id, ProjectImportJobUpdateKind.Created);
        Notify(job.ProjectId, job.Id, ProjectImportJobUpdateKind.Queued);

        return (await imports.ListJobSummariesByProjectAsync(projectId, cancellationToken))
            .First(item => item.Id == job.Id);
    }

    public async Task<IReadOnlyList<ProjectImportJobListItem>> ListImportJobsAsync(
        Guid projectId,
        CancellationToken cancellationToken = default) =>
        await imports.ListJobSummariesByProjectAsync(projectId, cancellationToken);

    public Task<ProjectImportJobDetailView?> GetImportJobDetailAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        imports.GetJobDetailViewAsync(jobId, cancellationToken);

    public async Task DeleteImportJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await imports.GetJobAsync(jobId, cancellationToken);
        if (job is null) return;
        if (job.Status == ProjectImportJobStatus.Running)
            throw new InvalidOperationException("Running imports cannot be deleted.");

        var projectId = job.ProjectId;
        imports.RemoveJob(job);
        await imports.SaveChangesAsync(cancellationToken);
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

    private static bool ShouldExportNode(ProjectExportKind kind, GraphNode node) =>
        kind == ProjectExportKind.Full || !NonStructuralExcludedNodeTypes.Contains(node.NodeType);

    private static bool ShouldExportType(ProjectExportKind kind, GraphEntityType type) =>
        kind == ProjectExportKind.Full || !NonStructuralExcludedNodeTypes.Contains(type.Type);

    private static ProjectExportNode ProjectNode(GraphNode node) =>
        new(node.NodeType, node.Key, node.Label, new Dictionary<string, object?>(node.Properties), node.CreatedAt, node.UpdatedAt);

    private static ProjectExportEdge ProjectEdge(GraphEdge edge, GraphNode from, GraphNode to) =>
        new(
            new ProjectExportNodeRef(from.NodeType, from.Key),
            new ProjectExportNodeRef(to.NodeType, to.Key),
            edge.EdgeType,
            new Dictionary<string, object?>(edge.Properties),
            edge.SortOrder,
            edge.CreatedAt,
            edge.UpdatedAt);

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

    private static ProjectExportPublishProfile ProjectPublishProfile(PublishProfile profile) =>
        new(
            profile.Id,
            profile.TitleOverride,
            profile.Subtitle,
            profile.Author,
            profile.Language,
            profile.Publisher,
            profile.Copyright,
            profile.Isbn,
            profile.Description,
            profile.Dedication,
            profile.Acknowledgments,
            profile.References,
            profile.IncludeTableOfContents,
            profile.IncludeVisibleTableOfContents,
            profile.IncludeActSynopses,
            profile.IncludeChapterSynopses,
            profile.IncludeActHeadings,
            profile.IncludeChapterHeadings,
            profile.NumberActs,
            profile.NumberChapters,
            profile.TitlePageMode,
            profile.PrintPicturePageSpreadMode,
            profile.EpubPicturePageSpreadMode,
            profile.PageWidthInches,
            profile.PageHeightInches,
            profile.PageMarginInches,
            profile.BodyFontSizePoints,
            profile.BodyLineHeight,
            profile.SelectedCoverChapterId);

    private static ProjectExportChapter ProjectChapter(
        Chapter chapter,
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> exportedImageContextIds) =>
        new()
        {
            Id = chapter.Id,
            ActId = chapter.ActId,
            Title = chapter.Title,
            Body = chapter.Body,
            Synopsis = chapter.Synopsis,
            Order = chapter.Order,
            VisualMode = chapter.VisualMode,
            PageLayoutKind = chapter.PageLayoutKind,
            PageLayoutJson = chapter.PageLayoutJson,
            IllustrationLayoutJson = chapter.IllustrationLayoutJson,
            ExplicitImageContextImageIds = exportedImageContextIds.TryGetValue(chapter.Id, out var imageIds)
                ? imageIds.ToList()
                : [],
        };

    private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> ListExportedImageContextIdsAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
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
