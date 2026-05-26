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
    public const string SourceBlockNodeType = EntityTypeService.SourceBlockNodeType;
    public const string ExtractedFromEdgeType = "ExtractedFrom";

    public async Task EnsureSourceAsync(
        IngestSource source,
        IReadOnlyList<IngestSourceChunk> sourceChunks,
        IReadOnlyList<IngestSourceBlock>? sourceBlocks = null,
        CancellationToken cancellationToken = default)
    {
        var orderedChunks = sourceChunks.OrderBy(chunk => chunk.Index).ToList();
        var orderedBlocks = (sourceBlocks ?? []).OrderBy(block => block.Index).ToList();
        var isSingleChunkSource = orderedChunks.Count == 1;
        var sourceNode = await graph.UpsertNodeAsync(
            source.ProjectId,
            SourceNodeType,
            source.Id.ToString("N"),
            source.Title,
            BuildSourceProperties(source, isSingleChunkSource ? orderedChunks[0] : null),
            cancellationToken);

        foreach (var sourceChunk in orderedChunks)
        {
            var existingChunkNode = await nodes.FindAsync(
                source.ProjectId,
                SourceChunkNodeType,
                sourceChunk.Id.ToString("N"),
                cancellationToken);

            if (isSingleChunkSource && existingChunkNode is null)
                continue;

            var chunkNode = await graph.UpsertNodeAsync(
                source.ProjectId,
                SourceChunkNodeType,
                sourceChunk.Id.ToString("N"),
                existingChunkNode is null ? BuildChunkGraphLabel(source, sourceChunk) : existingChunkNode.Label,
                BuildChunkProperties(source, sourceChunk),
                cancellationToken);

            await graph.UpsertEdgeAsync(
                sourceNode.Id,
                chunkNode.Id,
                EntityService.HasChildEdgeType,
                properties: null,
                sortOrder: sourceChunk.Index,
                cancellationToken: cancellationToken);
        }

        foreach (var sourceBlock in orderedBlocks)
        {
            var parentNode = FindParentChunkNode(orderedChunks, sourceBlock) is IngestSourceChunk parentChunk
                ? await nodes.FindAsync(source.ProjectId, SourceChunkNodeType, parentChunk.Id.ToString("N"), cancellationToken)
                : sourceNode;
            parentNode ??= sourceNode;

            var blockNode = await graph.UpsertNodeAsync(
                source.ProjectId,
                SourceBlockNodeType,
                sourceBlock.Id.ToString("N"),
                BuildBlockGraphLabel(source, sourceBlock),
                BuildBlockProperties(source, sourceBlock),
                cancellationToken);

            await graph.UpsertEdgeAsync(
                parentNode.Id,
                blockNode.Id,
                EntityService.HasChildEdgeType,
                properties: null,
                sortOrder: sourceBlock.Index,
                cancellationToken: cancellationToken);
        }
    }

    public async Task RemoveSourceAsync(Guid projectId, Guid sourceId, CancellationToken cancellationToken = default)
    {
        var sourceKey = sourceId.ToString("N");
        var blockNodes = await nodes.ListByTypeAsync(projectId, SourceBlockNodeType, cancellationToken);
        foreach (var blockNode in blockNodes.Where(node => HasSourceId(node, sourceKey)).ToList())
        {
            await graph.RemoveNodeAsync(blockNode.Id, cancellationToken);
        }

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

    private static Dictionary<string, object?> BuildSourceProperties(IngestSource source, IngestSourceChunk? singleChunk)
    {
        var properties = new Dictionary<string, object?>
        {
            [IngestSourceAssertions.GraphOriginProperty] = IngestSourceAssertions.GraphOriginIngestValue,
            ["sourceType"] = "ingest_source",
            ["sourceId"] = source.Id.ToString("N"),
            ["kind"] = source.SourceKind,
            ["description"] = source.Description,
            ["synopsis"] = source.Synopsis,
            ["sourceUrl"] = source.SourceUrl,
            ["finalUrl"] = source.FinalUrl,
            ["canonicalUrl"] = source.CanonicalUrl,
            ["fetchedAt"] = source.FetchedAt?.ToString("o"),
            ["contentType"] = source.ContentType,
            ["sourceMetadataJson"] = source.SourceMetadataJson,
            ["structural"] = true,
            ["singleChunkSource"] = singleChunk is not null,
            ["vectorIndexState"] = source.VectorIndexState.ToString(),
            ["vectorIndexedAt"] = source.VectorIndexedAt?.ToString("o"),
            ["vectorIndexError"] = source.VectorIndexError,
        };

        if (singleChunk is not null)
            AddChunkProperties(properties, source, singleChunk, sourceType: "ingest_source");

        return properties;
    }

    private static Dictionary<string, object?> BuildChunkProperties(IngestSource source, IngestSourceChunk sourceChunk)
    {
        var properties = new Dictionary<string, object?>
        {
            [IngestSourceAssertions.GraphOriginProperty] = IngestSourceAssertions.GraphOriginIngestValue,
            ["sourceType"] = "ingest_source_chunk",
            ["structural"] = true,
        };

        AddChunkProperties(properties, source, sourceChunk, sourceType: "ingest_source_chunk");
        return properties;
    }

    private static Dictionary<string, object?> BuildBlockProperties(IngestSource source, IngestSourceBlock sourceBlock) =>
        new()
        {
            [IngestSourceAssertions.GraphOriginProperty] = IngestSourceAssertions.GraphOriginIngestValue,
            ["sourceType"] = "ingest_source_block",
            ["sourceId"] = source.Id.ToString("N"),
            ["sourceBlockId"] = sourceBlock.Id.ToString("N"),
            ["sourcePageId"] = sourceBlock.SourcePageId?.ToString("N"),
            ["index"] = sourceBlock.Index,
            ["kind"] = sourceBlock.Kind,
            ["title"] = sourceBlock.Title,
            ["locator"] = sourceBlock.Locator,
            ["pageNumber"] = sourceBlock.PageNumber,
            ["startChar"] = sourceBlock.StartChar,
            ["endChar"] = sourceBlock.EndChar,
            ["metadataJson"] = sourceBlock.MetadataJson,
            ["structural"] = true,
        };

    private static void AddChunkProperties(
        Dictionary<string, object?> properties,
        IngestSource source,
        IngestSourceChunk sourceChunk,
        string sourceType)
    {
        properties["sourceType"] = sourceType;
        properties["sourceId"] = source.Id.ToString("N");
        properties["sourceChunkId"] = sourceChunk.Id.ToString("N");
        properties["sourceChunkIndex"] = sourceChunk.Index;
        properties["index"] = sourceChunk.Index;
        properties["headingPath"] = sourceChunk.HeadingPath;
        properties["startChar"] = sourceChunk.StartChar;
        properties["endChar"] = sourceChunk.EndChar;
        properties["estimatedTokenCount"] = sourceChunk.EstimatedTokenCount;
        properties["tokenCountMethod"] = sourceChunk.TokenCountMethod;
        properties["tokenEncodingName"] = sourceChunk.TokenEncodingName;
        properties["tokenCountIsExact"] = sourceChunk.TokenCountIsExact;
        properties["summary"] = sourceChunk.Summary;
        properties["notes"] = sourceChunk.AgentNotes;
    }

    private static string BuildChunkGraphLabel(IngestSource source, IngestSourceChunk sourceChunk)
    {
        var prefix = $"{source.Title.Trim()} Part {sourceChunk.Index + 1}".Trim();
        var title = sourceChunk.Title.Trim();
        return string.IsNullOrWhiteSpace(title) || IsGeneratedPartTitle(title)
            ? prefix
            : $"{prefix} / {title}";
    }

    private static IngestSourceChunk? FindParentChunkNode(
        IReadOnlyList<IngestSourceChunk> sourceChunks,
        IngestSourceBlock sourceBlock) =>
        sourceChunks.FirstOrDefault(chunk =>
            sourceBlock.StartChar >= chunk.StartChar
            && sourceBlock.StartChar < chunk.EndChar)
        ?? sourceChunks.FirstOrDefault(chunk =>
            sourceBlock.EndChar > chunk.StartChar
            && sourceBlock.StartChar < chunk.EndChar);

    private static string BuildBlockGraphLabel(IngestSource source, IngestSourceBlock sourceBlock)
    {
        var title = string.IsNullOrWhiteSpace(sourceBlock.Title)
            ? sourceBlock.Locator
            : sourceBlock.Title;
        if (string.IsNullOrWhiteSpace(title))
            title = $"Block {sourceBlock.Index + 1}";
        return $"{source.Title.Trim()} / {title}".Trim();
    }

    private static bool IsGeneratedPartTitle(string title)
    {
        var normalized = title.Trim();
        if (!normalized.StartsWith("Part ", StringComparison.OrdinalIgnoreCase))
            return false;

        var remainder = normalized["Part ".Length..].Trim();
        var numberLength = 0;
        while (numberLength < remainder.Length && char.IsDigit(remainder[numberLength]))
            numberLength++;

        if (numberLength == 0)
            return false;

        remainder = remainder[numberLength..].Trim();
        if (remainder.Length == 0)
            return true;

        if (!remainder.StartsWith("+", StringComparison.Ordinal))
            return false;

        var parts = remainder[1..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2
            && int.TryParse(parts[0], out _)
            && (string.Equals(parts[1], "section", StringComparison.OrdinalIgnoreCase)
                || string.Equals(parts[1], "sections", StringComparison.OrdinalIgnoreCase));
    }
}
