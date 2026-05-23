using System.Text.Json;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence.Repositories;

namespace Lorekeeper.ImportExport;

public sealed class ProjectImportExportService(
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

        var allNodes = await nodes.ListByProjectAsync(projectId, cancellationToken);
        var nodeById = allNodes.ToDictionary(node => node.Id);
        var includedNodeKeys = allNodes
            .Where(node => ShouldExportNode(kind, node))
            .Select(NodeStableKey)
            .ToHashSet(StringComparer.Ordinal);
        var warnings = new List<string>();

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
            Acts = kind == ProjectExportKind.Full
                ? (await acts.ListByProjectAsync(projectId, cancellationToken)).Select(ProjectAct).ToList()
                : [],
            Chapters = kind == ProjectExportKind.Full
                ? (await chapters.ListByProjectAsync(projectId, cancellationToken)).Select(ProjectChapter).ToList()
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
            TotalSteps = 8,
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

    private static ProjectExportChapter ProjectChapter(Chapter chapter) =>
        new(chapter.Id, chapter.ActId, chapter.Title, chapter.Body, chapter.Synopsis, chapter.Order);

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
