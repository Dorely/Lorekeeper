using Lorekeeper.Images;
using Lorekeeper.Persistence;
using Lorekeeper.Projects;
using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.EntityVisuals;

/// <summary>
/// Read-only access to canonical visual examples in a project's direct references.
/// The active project is deliberately never treated as a reference by this service.
/// </summary>
public interface IReferenceVisualService
{
    Task<IReadOnlyList<ReferenceVisualSummary>> ListAsync(
        Guid activeProjectId,
        CancellationToken cancellationToken = default);

    Task<ReferenceVisualReadResult?> ReadAsync(
        Guid activeProjectId,
        Guid originProjectId,
        Guid imageId,
        bool includeData,
        CancellationToken cancellationToken = default);
}

public sealed record ReferenceVisualSummary(
    Guid OriginProjectId,
    string OriginProjectName,
    string OriginProjectSlug,
    Guid EntityId,
    string EntityType,
    string EntityName,
    Guid CanonicalReferenceId,
    string Label,
    int SortOrder,
    Guid ImageId,
    string FileName,
    string ContentType,
    string PreviewUrl,
    string AltText,
    string Prompt,
    PublishAssetSource ImageSource,
    bool IsReferenced = true);

public sealed record ReferenceVisualReadResult(
    Guid OriginProjectId,
    string OriginProjectName,
    string OriginProjectSlug,
    Guid EntityId,
    string EntityType,
    string EntityName,
    Guid CanonicalReferenceId,
    string Label,
    int SortOrder,
    Guid ImageId,
    string FileName,
    string ContentType,
    string PreviewUrl,
    string AltText,
    string Prompt,
    PublishAssetSource ImageSource,
    bool IsReferenced,
    bool DataDelivered,
    byte[]? Data);

public sealed class ReferenceVisualService(
    IAppDatabaseOperationFactory database,
    IProjectReferenceService projectReferences) : IReferenceVisualService
{
    public const int MaximumListResults = 256;
    private const int MaxVisionEdge = 1_024;

    public async Task<IReadOnlyList<ReferenceVisualSummary>> ListAsync(
        Guid activeProjectId,
        CancellationToken cancellationToken = default)
    {
        var referenced = await DirectReferencesAsync(activeProjectId, cancellationToken);
        if (referenced.Count == 0) return [];

        await using var operation = await database.OpenReadAsync(cancellationToken);
        var rows = await operation.Db.EntityVisualExamples
            .AsNoTracking()
            .Include(example => example.GraphNode)
            .Include(example => example.Image)
            .Include(example => example.Project)
            .Where(example => referenced.Contains(example.ProjectId))
            .OrderBy(example => example.Project.Name)
            .ThenBy(example => example.GraphNode.NodeType)
            .ThenBy(example => example.GraphNode.Label)
            .ThenBy(example => example.SortOrder)
            .ThenBy(example => example.CreatedAt)
            .Take(MaximumListResults)
            .ToListAsync(cancellationToken);

        return rows
            .Where(IsEligibleExample)
            .Select(ToSummary)
            .ToList();
    }

    public async Task<ReferenceVisualReadResult?> ReadAsync(
        Guid activeProjectId,
        Guid originProjectId,
        Guid imageId,
        bool includeData,
        CancellationToken cancellationToken = default)
    {
        var referenced = await DirectReferencesAsync(activeProjectId, cancellationToken);
        if (!referenced.Contains(originProjectId)) return null;

        await using var operation = await database.OpenReadAsync(cancellationToken);
        var example = await operation.Db.EntityVisualExamples
            .AsNoTracking()
            .Include(item => item.GraphNode)
            .Include(item => item.Image)
            .Include(item => item.Project)
            .FirstOrDefaultAsync(item => item.ProjectId == originProjectId && item.ImageId == imageId, cancellationToken);
        if (example is null || !IsEligibleExample(example)) return null;

        var data = includeData
            ? ProjectImageResize.Resize(example.Image.Data, example.Image.ContentType, MaxVisionEdge)
            : null;
        return new ReferenceVisualReadResult(
            example.ProjectId,
            example.Project.Name,
            example.Project.Slug,
            Guid.ParseExact(example.GraphNode.Key, "N"),
            example.GraphNode.NodeType,
            example.GraphNode.Label ?? example.GraphNode.Key,
            example.Id,
            example.Label,
            example.SortOrder,
            example.Image.Id,
            example.Image.FileName,
            example.Image.ContentType,
            $"/projects/{example.ProjectId:N}/images/{example.Image.Id:N}/content?maxEdge=640",
            example.Image.AltText,
            example.Image.Prompt,
            example.Image.Source,
            true,
            data is not null,
            data);
    }

    private async Task<HashSet<Guid>> DirectReferencesAsync(Guid activeProjectId, CancellationToken cancellationToken)
    {
        var scopes = await projectReferences.ListReadableScopesAsync(activeProjectId, cancellationToken);
        return scopes
            .Where(scope => scope.IsReferenced && scope.ProjectId != activeProjectId)
            .Select(scope => scope.ProjectId)
            .ToHashSet();
    }

    private static bool IsEligibleExample(Models.EntityVisualExample example) =>
        Guid.TryParseExact(example.GraphNode.Key, "N", out _)
        && !string.Equals(example.GraphNode.NodeType, "Project", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(example.GraphNode.NodeType, "Act", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(example.GraphNode.NodeType, "Chapter", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(example.GraphNode.NodeType, "ProjectFact", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(example.GraphNode.NodeType, "Source", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(example.GraphNode.NodeType, "SourceChunk", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(example.GraphNode.NodeType, "SourceBlock", StringComparison.OrdinalIgnoreCase);

    private static ReferenceVisualSummary ToSummary(Models.EntityVisualExample example) => new(
        example.ProjectId,
        example.Project.Name,
        example.Project.Slug,
        Guid.ParseExact(example.GraphNode.Key, "N"),
        example.GraphNode.NodeType,
        example.GraphNode.Label ?? example.GraphNode.Key,
        example.Id,
        example.Label,
        example.SortOrder,
        example.Image.Id,
        example.Image.FileName,
        example.Image.ContentType,
        $"/projects/{example.ProjectId:N}/images/{example.Image.Id:N}/content?maxEdge=640",
        example.Image.AltText,
        example.Image.Prompt,
        example.Image.Source);
}
