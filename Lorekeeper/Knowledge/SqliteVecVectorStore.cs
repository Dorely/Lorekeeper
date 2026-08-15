using System.Runtime.InteropServices;
using Lorekeeper.Persistence;
using Microsoft.Data.Sqlite;

namespace Lorekeeper.Knowledge;

/// <summary>
/// sqlite-vec backed implementation of <see cref="IVectorStore"/>. The vector tables
/// (<c>knowledge_chunks</c> + <c>vec_knowledge</c>) are managed by
/// <see cref="VectorStoreInitializer"/>, not by EF Core migrations, so the abstraction
/// stays portable to non-SQLite backends.
/// </summary>
public class SqliteVecVectorStore(
    IConfiguration configuration,
    IAppDatabaseWriteCoordinator writes,
    ILogger<SqliteVecVectorStore> logger) : IVectorStore, IVectorStoreMaintenance
{
    private string ConnectionString =>
        SqliteConnectionSettings.BuildConnectionString(configuration);

    public async Task<long> StoreAsync(string content, float[] embedding, string sourceType,
        string scopeKey, string? sourceId = null, string? metadata = null, int? chunkIndex = null,
        CancellationToken cancellationToken = default)
    {
        await using var writeLease = await writes.AcquireAsync(cancellationToken);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        connection.LoadVector();

        if (!await VectorTableExistsAsync(connection, cancellationToken))
            throw new InvalidOperationException("Vector store is not initialized because no embedding model is configured.");

        await using var metaCmd = connection.CreateCommand();
        metaCmd.CommandText =
            """
            INSERT INTO knowledge_chunks (content, source_type, source_id, scope_key, metadata, chunk_index, created_at)
            VALUES (@content, @sourceType, @sourceId, @scopeKey, @metadata, @chunkIndex, @createdAt);
            SELECT last_insert_rowid();
            """;
        metaCmd.Parameters.AddWithValue("@content", content);
        metaCmd.Parameters.AddWithValue("@sourceType", sourceType);
        metaCmd.Parameters.AddWithValue("@sourceId", (object?)sourceId ?? DBNull.Value);
        metaCmd.Parameters.AddWithValue("@scopeKey", scopeKey);
        metaCmd.Parameters.AddWithValue("@metadata", (object?)metadata ?? DBNull.Value);
        metaCmd.Parameters.AddWithValue("@chunkIndex", (object?)chunkIndex ?? DBNull.Value);
        metaCmd.Parameters.AddWithValue("@createdAt", DateTime.UtcNow.ToString("o"));

        var rowId = (long)(await metaCmd.ExecuteScalarAsync(cancellationToken))!;

        // Defensive: clear any orphaned vec entry sharing this rowid before insert.
        await using var delCmd = connection.CreateCommand();
        delCmd.CommandText = "DELETE FROM vec_knowledge WHERE rowid = @rowid";
        delCmd.Parameters.AddWithValue("@rowid", rowId);
        await delCmd.ExecuteNonQueryAsync(cancellationToken);

        await using var vecCmd = connection.CreateCommand();
        vecCmd.CommandText = "INSERT INTO vec_knowledge (rowid, embedding) VALUES (@rowid, @embedding)";
        vecCmd.Parameters.AddWithValue("@rowid", rowId);
        vecCmd.Parameters.AddWithValue("@embedding", SerializeEmbedding(embedding));
        await vecCmd.ExecuteNonQueryAsync(cancellationToken);

        logger.LogDebug("Stored chunk {RowId} ({SourceType}/{SourceId})", rowId, sourceType, sourceId);
        return rowId;
    }

    public async Task<List<KnowledgeResult>> SearchAsync(float[] queryEmbedding, string scopeKey,
        int topK = 5, string? sourceTypeFilter = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        connection.LoadVector();

        if (!await VectorTableExistsAsync(connection, cancellationToken))
            return [];

        await using var cmd = connection.CreateCommand();
        var typeFilter = sourceTypeFilter is not null ? " AND k.source_type = @sourceType" : "";
        cmd.CommandText =
            $"""
            SELECT v.rowid, v.distance, k.content, k.source_type, k.source_id, k.metadata, k.chunk_index
            FROM (
                SELECT rowid, distance
                FROM vec_knowledge
                WHERE embedding MATCH @query
                ORDER BY distance
                LIMIT @fetchLimit
            ) v
            INNER JOIN knowledge_chunks k ON k.id = v.rowid
            WHERE k.scope_key = @scopeKey{typeFilter}
            LIMIT @topK
            """;
        cmd.Parameters.AddWithValue("@query", SerializeEmbedding(queryEmbedding));
        cmd.Parameters.AddWithValue("@scopeKey", scopeKey);
        cmd.Parameters.AddWithValue("@topK", topK);
        cmd.Parameters.AddWithValue("@fetchLimit", topK * 10);
        if (sourceTypeFilter is not null)
            cmd.Parameters.AddWithValue("@sourceType", sourceTypeFilter);

        return await ReadResultsAsync(cmd, cancellationToken);
    }

    public async Task<List<KnowledgeResult>> SearchMultiScopeAsync(float[] queryEmbedding,
        IEnumerable<string> scopeKeys, int topK = 5, string? sourceTypeFilter = null,
        CancellationToken cancellationToken = default)
    {
        var scopes = scopeKeys.ToList();
        if (scopes.Count == 0) return [];

        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        connection.LoadVector();

        if (!await VectorTableExistsAsync(connection, cancellationToken))
            return [];

        await using var cmd = connection.CreateCommand();
        var scopeParams = new List<string>();
        for (var i = 0; i < scopes.Count; i++)
        {
            var paramName = $"@scope{i}";
            scopeParams.Add(paramName);
            cmd.Parameters.AddWithValue(paramName, scopes[i]);
        }
        var inClause = string.Join(", ", scopeParams);
        var typeFilter = sourceTypeFilter is not null ? " AND k.source_type = @sourceType" : "";

        cmd.CommandText =
            $"""
            SELECT v.rowid, v.distance, k.content, k.source_type, k.source_id, k.metadata, k.chunk_index
            FROM (
                SELECT rowid, distance
                FROM vec_knowledge
                WHERE embedding MATCH @query
                ORDER BY distance
                LIMIT @fetchLimit
            ) v
            INNER JOIN knowledge_chunks k ON k.id = v.rowid
            WHERE k.scope_key IN ({inClause}){typeFilter}
            LIMIT @topK
            """;
        cmd.Parameters.AddWithValue("@query", SerializeEmbedding(queryEmbedding));
        cmd.Parameters.AddWithValue("@topK", topK);
        cmd.Parameters.AddWithValue("@fetchLimit", topK * 10);
        if (sourceTypeFilter is not null)
            cmd.Parameters.AddWithValue("@sourceType", sourceTypeFilter);

        return await ReadResultsAsync(cmd, cancellationToken);
    }

    public async Task DeleteBySourceAsync(string sourceType, string sourceId, string scopeKey,
        CancellationToken cancellationToken = default)
    {
        await using var writeLease = await writes.AcquireAsync(cancellationToken);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        connection.LoadVector();

        var ids = await SelectIdsAsync(connection,
            "SELECT id FROM knowledge_chunks WHERE source_type = @sourceType AND source_id = @sourceId AND scope_key = @scopeKey",
            cancellationToken,
            ("@sourceType", sourceType), ("@sourceId", sourceId), ("@scopeKey", scopeKey));

        if (ids.Count == 0) return;

        if (await VectorTableExistsAsync(connection, cancellationToken))
            await DeleteVecRowsAsync(connection, ids, cancellationToken);

        await using var metaCmd = connection.CreateCommand();
        metaCmd.CommandText =
            "DELETE FROM knowledge_chunks WHERE source_type = @sourceType AND source_id = @sourceId AND scope_key = @scopeKey";
        metaCmd.Parameters.AddWithValue("@sourceType", sourceType);
        metaCmd.Parameters.AddWithValue("@sourceId", sourceId);
        metaCmd.Parameters.AddWithValue("@scopeKey", scopeKey);
        await metaCmd.ExecuteNonQueryAsync(cancellationToken);

        logger.LogDebug("Deleted {Count} chunks for {SourceType}/{SourceId}", ids.Count, sourceType, sourceId);
    }

    public async Task DeleteByScopeAsync(string scopeKey, CancellationToken cancellationToken = default)
    {
        await using var writeLease = await writes.AcquireAsync(cancellationToken);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        connection.LoadVector();

        var ids = await SelectIdsAsync(connection,
            "SELECT id FROM knowledge_chunks WHERE scope_key = @scopeKey",
            cancellationToken,
            ("@scopeKey", scopeKey));

        if (ids.Count == 0) return;

        if (await VectorTableExistsAsync(connection, cancellationToken))
            await DeleteVecRowsAsync(connection, ids, cancellationToken);

        await using var metaCmd = connection.CreateCommand();
        metaCmd.CommandText = "DELETE FROM knowledge_chunks WHERE scope_key = @scopeKey";
        metaCmd.Parameters.AddWithValue("@scopeKey", scopeKey);
        await metaCmd.ExecuteNonQueryAsync(cancellationToken);

        logger.LogDebug("Deleted {Count} chunks for scope {ScopeKey}", ids.Count, scopeKey);
    }

    public void Initialize(int? dimensions)
    {
        using var writeLease = writes.Acquire();
        VectorStoreInitializer.Initialize(configuration, logger, dimensions);
    }

    public async Task RecreateAsync(int dimensions, CancellationToken cancellationToken = default)
    {
        if (dimensions <= 0)
            throw new ArgumentOutOfRangeException(nameof(dimensions), "Embedding dimensions must be greater than zero.");

        await using var writeLease = await writes.AcquireAsync(cancellationToken);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        SqliteConnectionSettings.ConfigureDatabase(connection);
        connection.LoadVector();

        await using (var dropVec = connection.CreateCommand())
        {
            dropVec.CommandText = "DROP TABLE IF EXISTS vec_knowledge;";
            await dropVec.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteMeta = connection.CreateCommand())
        {
            deleteMeta.CommandText = "DELETE FROM knowledge_chunks;";
            await deleteMeta.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var createVec = connection.CreateCommand())
        {
            createVec.CommandText = $"CREATE VIRTUAL TABLE vec_knowledge USING vec0(embedding float[{dimensions}])";
            await createVec.ExecuteNonQueryAsync(cancellationToken);
        }

        logger.LogInformation("Recreated vector store (dimensions={Dimensions})", dimensions);
    }

    private static async Task<List<KnowledgeResult>> ReadResultsAsync(SqliteCommand cmd, CancellationToken cancellationToken)
    {
        var results = new List<KnowledgeResult>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new KnowledgeResult(
                Content: reader.GetString(2),
                Distance: reader.GetDouble(1),
                SourceType: reader.GetString(3),
                SourceId: reader.IsDBNull(4) ? null : reader.GetString(4),
                RowId: reader.GetInt64(0),
                Metadata: reader.IsDBNull(5) ? null : reader.GetString(5),
                ChunkIndex: reader.IsDBNull(6) ? null : reader.GetInt32(6)
            ));
        }
        return results;
    }

    private static async Task<List<long>> SelectIdsAsync(
        SqliteConnection connection, string sql, CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);

        var ids = new List<long>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            ids.Add(reader.GetInt64(0));
        return ids;
    }

    private static async Task<bool> VectorTableExistsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type IN ('table', 'virtual table') AND name = 'vec_knowledge' LIMIT 1";
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is not null;
    }

    private static async Task DeleteVecRowsAsync(SqliteConnection connection, List<long> ids, CancellationToken cancellationToken)
    {
        foreach (var id in ids)
        {
            await using var vecCmd = connection.CreateCommand();
            vecCmd.CommandText = "DELETE FROM vec_knowledge WHERE rowid = @rowid";
            vecCmd.Parameters.AddWithValue("@rowid", id);
            await vecCmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static byte[] SerializeEmbedding(float[] embedding)
    {
        var bytes = new byte[embedding.Length * sizeof(float)];
        MemoryMarshal.AsBytes(embedding.AsSpan()).CopyTo(bytes);
        return bytes;
    }
}
