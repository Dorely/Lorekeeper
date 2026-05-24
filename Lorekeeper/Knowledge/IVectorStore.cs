namespace Lorekeeper.Knowledge;

public record KnowledgeResult(
    string Content,
    double Distance,
    string SourceType,
    string? SourceId,
    long RowId,
    string? Metadata,
    int? ChunkIndex);

/// <summary>
/// Backend-agnostic vector storage. Today implemented over sqlite-vec; a Postgres impl
/// (pgvector) or other backends should be addable without touching callers.
/// <para>
/// <c>scopeKey</c> is an opaque caller-defined string (e.g. <c>"world:42"</c>) used for
/// row partitioning during reads/deletes. The store itself does not interpret it.
/// </para>
/// </summary>
public interface IVectorStore
{
    Task<long> StoreAsync(
        string content,
        float[] embedding,
        string sourceType,
        string scopeKey,
        string? sourceId = null,
        string? metadata = null,
        int? chunkIndex = null,
        CancellationToken cancellationToken = default);

    Task<List<KnowledgeResult>> SearchAsync(
        float[] queryEmbedding,
        string scopeKey,
        int topK = 5,
        string? sourceTypeFilter = null,
        CancellationToken cancellationToken = default);

    Task<List<KnowledgeResult>> SearchMultiScopeAsync(
        float[] queryEmbedding,
        IEnumerable<string> scopeKeys,
        int topK = 5,
        string? sourceTypeFilter = null,
        CancellationToken cancellationToken = default);

    Task DeleteBySourceAsync(
        string sourceType,
        string sourceId,
        string scopeKey,
        CancellationToken cancellationToken = default);

    Task DeleteByScopeAsync(string scopeKey, CancellationToken cancellationToken = default);
}

public interface IVectorStoreMaintenance
{
    void Initialize(int? dimensions);
    Task RecreateAsync(int dimensions, CancellationToken cancellationToken = default);
}
