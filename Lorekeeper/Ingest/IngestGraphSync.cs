using Lorekeeper.Knowledge;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence.Repositories;

namespace Lorekeeper.Ingest;

public sealed class IngestGraphSync(
    IGraphStore graph,
    IGraphNodeRepository nodes,
    ILogger<IngestGraphSync> logger) : IIngestGraphSync
{
    public const string SourceNodeType = EntityTypeService.SourceNodeType;
    public const string SourceChunkNodeType = EntityTypeService.SourceChunkNodeType;
    public const string ExtractedFromEdgeType = "ExtractedFrom";

    public async Task EnsureSourceAsync(IngestSource source, IReadOnlyList<IngestSourceChunk> sourceChunks, CancellationToken cancellationToken = default)
    {
        var sourceNode = await graph.UpsertNodeAsync(
            source.ProjectId,
            SourceNodeType,
            source.Id.ToString("N"),
            source.Title,
            new Dictionary<string, object?>
            {
                ["sourceType"] = "ingest_source",
                ["sourceId"] = source.Id.ToString("N"),
                ["kind"] = source.SourceKind,
                ["description"] = source.Description,
                ["structural"] = true,
                ["vectorIndexState"] = source.VectorIndexState.ToString(),
                ["vectorIndexedAt"] = source.VectorIndexedAt?.ToString("o"),
                ["vectorIndexError"] = source.VectorIndexError,
            },
            cancellationToken);

        foreach (var sourceChunk in sourceChunks.OrderBy(chunk => chunk.Index))
        {
            var chunkNode = await graph.UpsertNodeAsync(
                source.ProjectId,
                SourceChunkNodeType,
                sourceChunk.Id.ToString("N"),
                sourceChunk.Title,
                new Dictionary<string, object?>
                {
                    ["sourceType"] = "ingest_source_chunk",
                    ["sourceId"] = source.Id.ToString("N"),
                    ["sourceChunkId"] = sourceChunk.Id.ToString("N"),
                    ["index"] = sourceChunk.Index,
                    ["headingPath"] = sourceChunk.HeadingPath,
                    ["startChar"] = sourceChunk.StartChar,
                    ["endChar"] = sourceChunk.EndChar,
                    ["estimatedTokenCount"] = sourceChunk.EstimatedTokenCount,
                    ["tokenCountMethod"] = sourceChunk.TokenCountMethod,
                    ["summary"] = sourceChunk.Summary,
                    ["notes"] = sourceChunk.AgentNotes,
                    ["structural"] = true,
                },
                cancellationToken);

            await graph.UpsertEdgeAsync(
                sourceNode.Id,
                chunkNode.Id,
                EntityService.HasChildEdgeType,
                properties: null,
                sortOrder: sourceChunk.Index,
                cancellationToken: cancellationToken);
        }
    }

    public async Task RemoveSourceAsync(Guid projectId, Guid sourceId, CancellationToken cancellationToken = default)
    {
        var sourceKey = sourceId.ToString("N");
        var chunkNodes = await nodes.ListByTypeAsync(projectId, SourceChunkNodeType, cancellationToken);
        foreach (var chunkNode in chunkNodes.Where(node => HasSourceId(node, sourceKey)).ToList())
        {
            await graph.RemoveNodeAsync(chunkNode.Id, cancellationToken);
        }

        var node = await nodes.FindAsync(projectId, SourceNodeType, sourceId.ToString("N"), cancellationToken);
        if (node is null) return;

        try
        {
            await graph.RemoveNodeAsync(node.Id, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to remove ingest source graph node {SourceId}", sourceId);
            throw;
        }
    }

    private static bool HasSourceId(GraphNode node, string sourceKey) =>
        node.Properties.TryGetValue("sourceId", out var value)
        && string.Equals(value?.ToString(), sourceKey, StringComparison.OrdinalIgnoreCase);
}