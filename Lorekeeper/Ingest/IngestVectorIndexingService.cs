using Lorekeeper.Knowledge;
using Lorekeeper.Llm;
using Lorekeeper.Models;
using Lorekeeper.Persistence.Repositories;

namespace Lorekeeper.Ingest;

public sealed class IngestVectorIndexingService(
    IIngestRepository ingest,
    IVectorStore vectors,
    IEmbeddingService embeddings,
    ITextChunker chunker) : IIngestVectorIndexingService
{
    public async Task EnsureVectorFragmentsAsync(IngestSource source, bool force = false, CancellationToken cancellationToken = default)
    {
        var existingFragments = await ingest.ListVectorFragmentsAsync(source.Id, cancellationToken);
        if (!force && source.VectorIndexState == VectorIndexState.UpToDate && existingFragments.Count > 0)
            return;

        if (!await embeddings.IsAvailableAsync(cancellationToken))
        {
            await DeleteExistingVectorRowsAsync(source, existingFragments, cancellationToken);
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
            await DeleteExistingVectorRowsAsync(source, existingFragments, cancellationToken);

            var chunks = chunker.Chunk(source.SourceText);
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
                    var metadata = $"Source {source.Title} - Vector fragment {index + 1}/{chunks.Count}";
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
        await vectors.DeleteBySourceAsync("ingest_source", source.VectorSourceId, Project.ScopeKey(source.ProjectId), cancellationToken);
        foreach (var fragment in existingFragments)
            ingest.RemoveVectorFragment(fragment);
        await ingest.SaveChangesAsync(cancellationToken);
    }

    private static int FindFragmentStart(string sourceText, string fragmentText, int cursor)
    {
        var searchStart = Math.Max(0, cursor - 500);
        var found = sourceText.IndexOf(fragmentText, searchStart, StringComparison.Ordinal);
        return found < 0 ? Math.Clamp(cursor, 0, sourceText.Length) : found;
    }
}

