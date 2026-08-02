using System.Text;
using Lorekeeper.Graph;
using Lorekeeper.Ingest;
using Lorekeeper.Knowledge;
using Lorekeeper.Llm;
using Lorekeeper.Models;
using Lorekeeper.Manuscripts;
using Lorekeeper.Outline;
using Lorekeeper.Persistence;
using Lorekeeper.Persistence.Repositories;
using Lorekeeper.Search;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Context;

public sealed class ContextIndexingService(
    IVectorStore vectors,
    IEmbeddingService embeddings,
    ITextChunker chunker,
    IProjectSearchIndex projectSearch,
    IGraphAutoLinkService autoLinks,
    IGraphNodeRepository nodes,
    IGraphEdgeRepository edges,
    IActRepository acts,
    IChapterRepository chapters,
    IIngestRepository ingest,
    AppDbContext db,
    IChapterSemanticProjectionService semanticProjection,
    IVectorIndexWorkCoordinator indexWork,
    ILogger<ContextIndexingService> logger) : IContextIndexingService
{
    private const int MaxEntityLinks = 30;
    private const int MaxSourceChunkExcerptChars = 6_000;

    public Task ReindexEntityAsync(Guid projectId, Guid entityId, CancellationToken cancellationToken = default) =>
        indexWork.QueueOrRunAsync(
            VectorIndexWorkKind.ContextEntity,
            $"{projectId:N}:{entityId:N}",
            async ct =>
            {
                try
                {
                    var node = await nodes.FindByKeyAsync(projectId, entityId.ToString("N"), ct);
                    if (node is null || !IsContextEntityNode(node))
                    {
                        await DeleteEntityAsync(projectId, entityId, ct);
                        return;
                    }

                    await ReindexEntityNodeAsync(node, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Failed to reindex context entity {EntityId}", entityId);
                }
            },
            cancellationToken);

    public Task DeleteEntityAsync(Guid projectId, Guid entityId, CancellationToken cancellationToken = default) =>
        DeleteBySourceAsync(projectId, ContextVectorSourceTypes.Entity, entityId, cancellationToken);

    public Task ReindexChapterAsync(Guid chapterId, CancellationToken cancellationToken = default) =>
        indexWork.QueueOrRunAsync(
            VectorIndexWorkKind.ContextChapter,
            chapterId.ToString("N"),
            async ct =>
            {
                try
                {
                    var chapter = await chapters.GetByIdAsync(chapterId, ct);
                    if (chapter is not null)
                        await ReindexChapterCoreAsync(chapter, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Failed to reindex context chapter {ChapterId}", chapterId);
                }
            },
            cancellationToken);

    public Task DeleteChapterAsync(Guid projectId, Guid chapterId, CancellationToken cancellationToken = default) =>
        DeleteBySourceAsync(projectId, ContextVectorSourceTypes.Chapter, chapterId, cancellationToken);

    public Task ReindexActAsync(Guid actId, CancellationToken cancellationToken = default) =>
        indexWork.QueueOrRunAsync(
            VectorIndexWorkKind.ContextAct,
            actId.ToString("N"),
            async ct =>
            {
                try
                {
                    var act = await acts.GetByIdAsync(actId, ct);
                    if (act is not null)
                        await ReindexActCoreAsync(act, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Failed to reindex context act {ActId}", actId);
                }
            },
            cancellationToken);

    public Task DeleteActAsync(Guid projectId, Guid actId, CancellationToken cancellationToken = default) =>
        DeleteBySourceAsync(projectId, ContextVectorSourceTypes.Act, actId, cancellationToken);

    public async Task ReindexIngestSourceAsync(Guid sourceId, CancellationToken cancellationToken = default)
    {
        try
        {
            var source = await ingest.GetSourceAsync(sourceId, cancellationToken);
            if (source is not null)
                await ReindexIngestSourceCoreAsync(source, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to reindex context ingest source {SourceId}", sourceId);
        }
    }

    public async Task DeleteIngestSourceAsync(Guid projectId, Guid sourceId, CancellationToken cancellationToken = default)
    {
        try
        {
            foreach (var sourceChunk in await ingest.ListSourceChunksAsync(sourceId, cancellationToken))
                await DeleteIngestSourceChunkAsync(projectId, sourceChunk.Id, cancellationToken);

            await DeleteBySourceAsync(projectId, ContextVectorSourceTypes.IngestSource, sourceId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to delete context ingest source vectors for {SourceId}", sourceId);
        }
    }

    public async Task ReindexIngestSourceChunkAsync(Guid sourceChunkId, CancellationToken cancellationToken = default)
    {
        try
        {
            var sourceChunk = await ingest.GetSourceChunkAsync(sourceChunkId, cancellationToken);
            if (sourceChunk is null) return;

            await ReindexIngestSourceChunkCoreAsync(sourceChunk, cancellationToken);
            await ReindexIngestSourceAsync(sourceChunk.SourceId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to reindex context ingest source chunk {SourceChunkId}", sourceChunkId);
        }
    }

    public Task DeleteIngestSourceChunkAsync(Guid projectId, Guid sourceChunkId, CancellationToken cancellationToken = default) =>
        DeleteBySourceAsync(projectId, ContextVectorSourceTypes.IngestSourceChunk, sourceChunkId, cancellationToken);

    private async Task ReindexEntityNodeAsync(GraphNode node, CancellationToken cancellationToken)
    {
        try
        {
            var text = await BuildEntityTextAsync(node, cancellationToken);
            await StoreChunksAsync(
                node.ProjectId,
                ContextVectorSourceTypes.Entity,
                Guid.ParseExact(node.Key, "N"),
                $"{node.NodeType} {node.Label ?? node.Key}",
                text,
                null,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to reindex context entity node {NodeType}/{NodeKey}", node.NodeType, node.Key);
        }
    }

    private async Task ReindexChapterCoreAsync(Chapter chapter, CancellationToken cancellationToken)
    {
        var text = await BuildChapterTextAsync(chapter, cancellationToken);
        await StoreChunksAsync(
            chapter.ProjectId,
            ContextVectorSourceTypes.Chapter,
            chapter.Id,
            $"Chapter {chapter.Order + 1} {chapter.Title}",
            text,
            null,
            cancellationToken);
    }

    private async Task ReindexActCoreAsync(Act act, CancellationToken cancellationToken)
    {
        var text = await BuildActTextAsync(act, cancellationToken);
        await StoreChunksAsync(
            act.ProjectId,
            ContextVectorSourceTypes.Act,
            act.Id,
            $"Act {act.Order + 1} {act.Title}",
            text,
            null,
            cancellationToken);
    }

    private async Task ReindexIngestSourceCoreAsync(IngestSource source, CancellationToken cancellationToken)
    {
        var text = await BuildIngestSourceTextAsync(source, cancellationToken);
        await StoreChunksAsync(
            source.ProjectId,
            ContextVectorSourceTypes.IngestSource,
            source.Id,
            $"Source {source.Title}",
            text,
            source.Id,
            cancellationToken);

        foreach (var sourceChunk in await ingest.ListSourceChunksAsync(source.Id, cancellationToken))
            await ReindexIngestSourceChunkCoreAsync(sourceChunk, cancellationToken);
    }

    private async Task ReindexIngestSourceChunkCoreAsync(IngestSourceChunk sourceChunk, CancellationToken cancellationToken)
    {
        var source = await ingest.GetSourceAsync(sourceChunk.SourceId, cancellationToken);
        if (source is null) return;

        var text = await BuildIngestSourceChunkTextAsync(source, sourceChunk, cancellationToken);
        await StoreChunksAsync(
            source.ProjectId,
            ContextVectorSourceTypes.IngestSourceChunk,
            sourceChunk.Id,
            $"Source chunk {source.Title} part {sourceChunk.Index + 1}",
            text,
            source.Id,
            cancellationToken);
    }

    private async Task StoreChunksAsync(
        Guid projectId,
        string sourceType,
        Guid sourceId,
        string metadata,
        string content,
        Guid? containerSourceId,
        CancellationToken cancellationToken)
    {
        var scopeKey = Project.ScopeKey(projectId);
        var sourceKey = sourceId.ToString("N");
        await vectors.DeleteBySourceAsync(sourceType, sourceKey, scopeKey, cancellationToken);
        await projectSearch.DeleteBySourceAsync(sourceType, sourceKey, scopeKey, cancellationToken);

        if (string.IsNullOrWhiteSpace(content))
        {
            await autoLinks.RefreshSourceAsync(projectId, sourceType, sourceId, cancellationToken);
            return;
        }

        var chunks = chunker.Chunk(content);
        if (chunks.Count == 0)
        {
            await autoLinks.RefreshSourceAsync(projectId, sourceType, sourceId, cancellationToken);
            return;
        }

        await projectSearch.StoreManyAsync(chunks.Select(chunk => new ProjectSearchIndexChunk(
            chunk.Content,
            sourceType,
            scopeKey,
            sourceKey,
            containerSourceId?.ToString("N"),
            metadata,
            chunks.Count == 1 ? metadata : $"{metadata} - Part {chunk.Index + 1}/{chunks.Count}",
            chunk.Index)), cancellationToken);

        await autoLinks.RefreshSourceAsync(projectId, sourceType, sourceId, cancellationToken);

        if (!await embeddings.IsAvailableAsync(cancellationToken))
            return;

        var contents = chunks.Select(chunk => chunk.Content).ToList();
        var embeddingVectors = await embeddings.GenerateEmbeddingsAsync(contents, cancellationToken);
        for (var index = 0; index < chunks.Count; index++)
        {
            await vectors.StoreAsync(
                chunks[index].Content,
                embeddingVectors[index],
                sourceType,
                scopeKey,
                sourceKey,
                chunks.Count == 1 ? metadata : $"{metadata} - Part {index + 1}/{chunks.Count}",
                index,
                cancellationToken);
        }
    }

    private async Task DeleteBySourceAsync(Guid projectId, string sourceType, Guid sourceId, CancellationToken cancellationToken)
    {
        try
        {
            var sourceKey = sourceId.ToString("N");
            var scopeKey = Project.ScopeKey(projectId);
            await vectors.DeleteBySourceAsync(sourceType, sourceKey, scopeKey, cancellationToken);
            await projectSearch.DeleteBySourceAsync(sourceType, sourceKey, scopeKey, cancellationToken);
            await autoLinks.RefreshSourceAsync(projectId, sourceType, sourceId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to delete context vectors for {SourceType}/{SourceId}", sourceType, sourceId);
        }
    }

    private async Task<string> BuildEntityTextAsync(GraphNode node, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.Append("Type: ").AppendLine(node.NodeType);
        sb.Append("Name: ").AppendLine(node.Label ?? node.Key);
        AppendOptional(sb, "Summary", IngestWikiSheet.ReadSummary(node.Properties));

        AppendProperties(sb, node.Properties);

        var aliases = IngestWikiSheet.ReadAliases(node.Properties);
        if (aliases.Count > 0)
        {
            sb.AppendLine("Aliases:");
            sb.Append("- ").AppendLine(string.Join(", ", aliases));
        }

        var visualExamples = await db.EntityVisualExamples
            .AsNoTracking()
            .Include(example => example.Image)
            .Where(example => example.GraphNodeId == node.Id)
            .OrderBy(example => example.SortOrder)
            .ToListAsync(cancellationToken);
        if (visualExamples.Count > 0)
        {
            sb.AppendLine("Canonical visual references:");
            foreach (var example in visualExamples)
            {
                sb.Append("- ").Append(example.Label).Append(" [imageId: ").Append(example.ImageId.ToString("N")).AppendLine("]");
                AppendOptional(sb, "  Alt text", example.Image.AltText);
                AppendOptional(sb, "  Prompt", example.Image.Prompt);
            }
        }

        var sections = IngestWikiSheet.ReadSections(node.Properties);
        if (sections.Count > 0)
        {
            sb.AppendLine("Wiki sheet:");
            foreach (var section in sections)
            {
                sb.Append("- ").Append(section.Title).Append(": ").AppendLine(section.Body);
                foreach (var citation in section.Citations.Take(3))
                {
                    sb.Append("  Source: ").Append(citation.SourceTitle).Append(" chunk ").Append(citation.SourceChunkIndex + 1).AppendLine();
                }
            }
        }

        var adjacent = (await edges.GetAdjacentAsync(node.Id, EdgeDirection.Both, edgeTypes: null, MaxEntityLinks, cancellationToken))
            .Where(edge => !GraphAutoLinkService.IsAutoMentionEdge(edge))
            .ToList();
        if (adjacent.Count > 0)
        {
            var otherIds = adjacent.Select(edge => edge.FromNodeId == node.Id ? edge.ToNodeId : edge.FromNodeId).Distinct();
            var otherNodes = (await nodes.GetByIdsAsync(otherIds, cancellationToken)).ToDictionary(other => other.Id);
            sb.AppendLine("Links:");
            foreach (var edge in adjacent)
            {
                var otherNodeId = edge.FromNodeId == node.Id ? edge.ToNodeId : edge.FromNodeId;
                if (!otherNodes.TryGetValue(otherNodeId, out var other)) continue;
                var direction = edge.FromNodeId == node.Id ? "->" : "<-";
                sb.Append("- ").Append(direction).Append(' ').Append(edge.EdgeType).Append(' ')
                    .Append(other.Label ?? other.Key).Append(" (").Append(other.NodeType).AppendLine(")");
                AppendProperties(sb, edge.Properties, "  ");
                AppendOptional(sb, "  Summary", ReadProperty(edge.Properties, IngestWikiSheet.SummaryProperty));
            }
        }

        return sb.ToString().TrimEnd();
    }

    private async Task<string> BuildChapterTextAsync(Chapter chapter, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.Append("Type: Chapter\n");
        sb.Append("Title: ").AppendLine(chapter.Title);
        AppendOptional(sb, "Synopsis", chapter.Synopsis);
        AppendOptional(sb, "Body", await semanticProjection.ExpandPlainTextAsync(chapter, cancellationToken));
        return sb.ToString().TrimEnd();
    }

    private async Task<string> BuildActTextAsync(Act act, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.Append("Type: Act\n");
        sb.Append("Title: ").AppendLine(act.Title);
        AppendOptional(sb, "Synopsis", act.Synopsis);

        var actChapters = (await chapters.ListByProjectAsync(act.ProjectId, cancellationToken))
            .Where(chapter => chapter.ActId == act.Id)
            .OrderBy(chapter => chapter.Order)
            .ToList();
        if (actChapters.Count > 0)
        {
            sb.AppendLine("Chapters:");
            foreach (var chapter in actChapters)
            {
                sb.Append("- Chapter ").Append(chapter.Order + 1).Append(": ").AppendLine(chapter.Title);
                AppendOptional(sb, "  Synopsis", chapter.Synopsis);
            }
        }

        return sb.ToString().TrimEnd();
    }

    private async Task<string> BuildIngestSourceTextAsync(IngestSource source, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.Append("Type: Ingest source\n");
        sb.Append("Title: ").AppendLine(source.Title);
        AppendOptional(sb, "Kind", source.SourceKind);
        AppendOptional(sb, "Description", source.Description);
        AppendOptional(sb, "Synopsis", source.Synopsis);
        AppendOptional(sb, "Instructions", source.UserInstructions);

        var sourceChunks = await ingest.ListSourceChunksAsync(source.Id, cancellationToken);
        if (sourceChunks.Count > 0)
        {
            sb.AppendLine("Source chunks:");
            foreach (var sourceChunk in sourceChunks)
            {
                sb.Append("- Part ").Append(sourceChunk.Index + 1).Append(": ").AppendLine(sourceChunk.Title);
                AppendOptional(sb, "  Heading", sourceChunk.HeadingPath);
                AppendOptional(sb, "  Summary", sourceChunk.Summary);
                AppendOptional(sb, "  Notes", sourceChunk.AgentNotes);
            }
        }

        return sb.ToString().TrimEnd();
    }

    private async Task<string> BuildIngestSourceChunkTextAsync(IngestSource source, IngestSourceChunk sourceChunk, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.Append("Type: Ingest source chunk\n");
        sb.Append("Source: ").AppendLine(source.Title);
        AppendOptional(sb, "Source kind", source.SourceKind);
        sb.Append("Part: ").Append(sourceChunk.Index + 1).AppendLine();
        AppendOptional(sb, "Title", sourceChunk.Title);
        AppendOptional(sb, "Heading path", sourceChunk.HeadingPath);
        AppendOptional(sb, "Summary", sourceChunk.Summary);
        AppendOptional(sb, "Notes", sourceChunk.AgentNotes);

        var excerpt = await ingest.GetSourceChunkExcerptAsync(sourceChunk.Id, MaxSourceChunkExcerptChars, cancellationToken);
        if (excerpt is not null)
            AppendOptional(sb, excerpt.IsTruncated ? "Excerpt (truncated)" : "Excerpt", excerpt.Text);

        return sb.ToString().TrimEnd();
    }

    private static void AppendProperties(StringBuilder sb, IReadOnlyDictionary<string, object?> properties, string prefix = "")
    {
        var visible = properties
            .Where(property => !IngestSourceAssertions.IsProtectedProperty(property.Key)
                && !IngestSourceAssertions.IsLegacyIngestProperty(property.Key)
                && !IngestWikiSheet.IsWikiStorageProperty(property.Key)
                && !string.IsNullOrWhiteSpace(property.Value?.ToString()))
            .OrderBy(property => property.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (visible.Count == 0) return;

        sb.Append(prefix).AppendLine("Properties:");
        foreach (var property in visible)
            sb.Append(prefix).Append("- ").Append(property.Key).Append(": ").AppendLine(property.Value?.ToString());
    }

    private static void AppendObservedProperties(StringBuilder sb, IReadOnlyDictionary<string, string?> properties, string prefix)
    {
        foreach (var property in properties
            .Where(property => !string.IsNullOrWhiteSpace(property.Value))
            .OrderBy(property => property.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(prefix).Append(property.Key).Append(": ").AppendLine(property.Value);
        }
    }

    private static void AppendOptional(StringBuilder sb, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        sb.Append(label).Append(": ").AppendLine(value.Trim());
    }

    private static string ReadProperty(IReadOnlyDictionary<string, object?> properties, string key) =>
        properties.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty;

    private static bool IsContextEntityNode(GraphNode node) =>
        Guid.TryParseExact(node.Key, "N", out _)
        && IsContextEntityType(node.NodeType);

    private static bool IsContextEntityType(string type) =>
        !string.Equals(type, EntityTypeService.ProjectNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.ActNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.ChapterNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.ProjectFactNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.SourceNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.SourceChunkNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.SourceBlockNodeType, StringComparison.OrdinalIgnoreCase);
}
