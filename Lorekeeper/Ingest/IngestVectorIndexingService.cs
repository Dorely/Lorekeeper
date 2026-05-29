using Lorekeeper.Knowledge;
using Lorekeeper.Llm;
using Lorekeeper.Models;
using Lorekeeper.Graph;
using Lorekeeper.Persistence.Repositories;
using Lorekeeper.Search;

namespace Lorekeeper.Ingest;

public sealed class IngestVectorIndexingService(
    IIngestRepository ingest,
    IVectorStore vectors,
    IEmbeddingService embeddings,
    ITextChunker chunker,
    IProjectSearchIndex projectSearch,
    IGraphAutoLinkService autoLinks) : IIngestVectorIndexingService
{
    public async Task EnsureVectorFragmentsAsync(IngestSource source, bool force = false, CancellationToken cancellationToken = default)
    {
        var existingFragments = await ingest.ListVectorFragmentsAsync(source.Id, cancellationToken);
        if (!force && source.VectorIndexState == VectorIndexState.UpToDate && existingFragments.Count > 0)
            return;

        await DeleteExistingVectorRowsAsync(source, existingFragments, cancellationToken);
        await StoreLexicalFragmentsAsync(source, cancellationToken);
        await autoLinks.RefreshSourceAsync(source.ProjectId, ProjectSearchSourceTypes.RawIngestSource, source.Id, cancellationToken);

        if (!await embeddings.IsAvailableAsync(cancellationToken))
        {
            source.VectorIndexState = VectorIndexState.Disabled;
            source.VectorIndexedAt = null;
            source.VectorIndexError = null;
            source.UpdatedAt = DateTime.UtcNow;
            ingest.UpdateSource(source);
            await ingest.SaveChangesAsync(cancellationToken);
            return;
        }

        try
        {
            var chunks = chunker.Chunk(source.SourceText);
            var sourceBlocks = await ingest.ListSourceBlocksAsync(source.Id, cancellationToken);
            if (chunks.Count > 0)
            {
                var contents = chunks.Select(item => item.Content).ToList();
                var embeddingVectors = await embeddings.GenerateEmbeddingsAsync(contents, cancellationToken);
                var cursor = 0;
                for (var index = 0; index < chunks.Count; index++)
                {
                    var text = chunks[index].Content;
                    var start = FindFragmentStart(source.SourceText, text, cursor);
                    var end = Math.Min(source.SourceText.Length, start + text.Length);
                    cursor = Math.Min(source.SourceText.Length, Math.Max(start + 1, end - 200));
                    var metadata = BuildFragmentMetadata(source, sourceBlocks, index, chunks.Count, start, end);
                    var rowId = await vectors.StoreAsync(
                        content: text,
                        embedding: embeddingVectors[index],
                        sourceType: "ingest_source",
                        scopeKey: Project.ScopeKey(source.ProjectId),
                        sourceId: source.VectorSourceId,
                        metadata: metadata,
                        chunkIndex: index,
                        cancellationToken: cancellationToken);

                    await ingest.AddVectorFragmentAsync(new IngestVectorFragment
                    {
                        SourceId = source.Id,
                        Index = index,
                        VectorRowId = rowId,
                        StartChar = start,
                        EndChar = end,
                        Metadata = metadata,
                    }, cancellationToken);
                }
            }

            source.VectorIndexState = VectorIndexState.UpToDate;
            source.VectorIndexedAt = DateTime.UtcNow;
            source.VectorIndexError = null;
            source.UpdatedAt = DateTime.UtcNow;
            ingest.UpdateSource(source);
            await ingest.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            source.VectorIndexState = VectorIndexState.Failed;
            source.VectorIndexError = ex.Message;
            source.UpdatedAt = DateTime.UtcNow;
            ingest.UpdateSource(source);
            await ingest.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task DeleteExistingVectorRowsAsync(
        IngestSource source,
        IReadOnlyList<IngestVectorFragment> existingFragments,
        CancellationToken cancellationToken)
    {
        var scopeKey = Project.ScopeKey(source.ProjectId);
        await vectors.DeleteBySourceAsync("ingest_source", source.VectorSourceId, scopeKey, cancellationToken);
        await projectSearch.DeleteBySourceAsync(ProjectSearchSourceTypes.RawIngestSource, source.VectorSourceId, scopeKey, cancellationToken);
        foreach (var fragment in existingFragments)
            ingest.RemoveVectorFragment(fragment);
        await ingest.SaveChangesAsync(cancellationToken);
    }

    private async Task StoreLexicalFragmentsAsync(IngestSource source, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.SourceText)) return;

        var chunks = chunker.Chunk(source.SourceText);
        if (chunks.Count == 0) return;

        await projectSearch.StoreManyAsync(chunks.Select(chunk => new ProjectSearchIndexChunk(
            chunk.Content,
            ProjectSearchSourceTypes.RawIngestSource,
            Project.ScopeKey(source.ProjectId),
            source.VectorSourceId,
            source.Id.ToString("N"),
            source.Title,
            $"Raw source {source.Title} - Part {chunk.Index + 1}/{chunks.Count}",
            chunk.Index)), cancellationToken);
    }

    private static int FindFragmentStart(string sourceText, string fragmentText, int cursor)
    {
        var searchStart = Math.Max(0, cursor - 500);
        var found = sourceText.IndexOf(fragmentText, searchStart, StringComparison.Ordinal);
        return found < 0 ? Math.Clamp(cursor, 0, sourceText.Length) : found;
    }

    private static string BuildFragmentMetadata(
        IngestSource source,
        IReadOnlyList<IngestSourceBlock> sourceBlocks,
        int index,
        int count,
        int startChar,
        int endChar)
    {
        var blocks = sourceBlocks
            .Where(block => block.EndChar > startChar && block.StartChar < endChar)
            .OrderBy(block => block.Index)
            .Take(8)
            .Select(block => new
            {
                id = block.Id.ToString("N"),
                block.Kind,
                block.Title,
                block.Locator,
                block.PageNumber,
                block.StartChar,
                block.EndChar,
            })
            .ToList();

        return System.Text.Json.JsonSerializer.Serialize(new
        {
            label = $"Source {source.Title} - Vector fragment {index + 1}/{count}",
            sourceId = source.Id.ToString("N"),
            startChar,
            endChar,
            blocks,
            linkedEntityIds = Array.Empty<string>(),
        });
    }
}
