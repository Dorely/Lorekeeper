using System.Text.RegularExpressions;
using Lorekeeper.Persistence;
using Microsoft.Data.Sqlite;

namespace Lorekeeper.Search;

public sealed partial class SqliteFtsProjectSearchIndex(
    IConfiguration configuration,
    IAppDatabaseWriteCoordinator writes,
    ILogger<SqliteFtsProjectSearchIndex> logger) : IProjectSearchIndex
{
    private const string TableName = "project_search_fts";

    private string ConnectionString =>
        SqliteConnectionSettings.BuildConnectionString(configuration);

    public async Task StoreAsync(ProjectSearchIndexChunk chunk, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(chunk.Content)) return;

        await using var writeLease = await writes.AcquireAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            $"""
            INSERT INTO {TableName}
                (title, content, metadata, scope_key, source_type, source_id, container_source_id, chunk_index, created_at)
            VALUES
                (@title, @content, @metadata, @scopeKey, @sourceType, @sourceId, @containerSourceId, @chunkIndex, @createdAt)
            """;
        cmd.Parameters.AddWithValue("@title", chunk.Title ?? string.Empty);
        cmd.Parameters.AddWithValue("@content", chunk.Content);
        cmd.Parameters.AddWithValue("@metadata", chunk.Metadata ?? string.Empty);
        cmd.Parameters.AddWithValue("@scopeKey", chunk.ScopeKey);
        cmd.Parameters.AddWithValue("@sourceType", chunk.SourceType);
        cmd.Parameters.AddWithValue("@sourceId", (object?)chunk.SourceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@containerSourceId", (object?)chunk.ContainerSourceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@chunkIndex", (object?)chunk.ChunkIndex ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@createdAt", DateTime.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task StoreManyAsync(IEnumerable<ProjectSearchIndexChunk> chunks, CancellationToken cancellationToken = default)
    {
        var list = chunks.Where(chunk => !string.IsNullOrWhiteSpace(chunk.Content)).ToList();
        if (list.Count == 0) return;

        await using var writeLease = await writes.AcquireAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var chunk in list)
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = (SqliteTransaction)transaction;
            cmd.CommandText =
                $"""
                INSERT INTO {TableName}
                    (title, content, metadata, scope_key, source_type, source_id, container_source_id, chunk_index, created_at)
                VALUES
                    (@title, @content, @metadata, @scopeKey, @sourceType, @sourceId, @containerSourceId, @chunkIndex, @createdAt)
                """;
            cmd.Parameters.AddWithValue("@title", chunk.Title ?? string.Empty);
            cmd.Parameters.AddWithValue("@content", chunk.Content);
            cmd.Parameters.AddWithValue("@metadata", chunk.Metadata ?? string.Empty);
            cmd.Parameters.AddWithValue("@scopeKey", chunk.ScopeKey);
            cmd.Parameters.AddWithValue("@sourceType", chunk.SourceType);
            cmd.Parameters.AddWithValue("@sourceId", (object?)chunk.SourceId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@containerSourceId", (object?)chunk.ContainerSourceId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@chunkIndex", (object?)chunk.ChunkIndex ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@createdAt", DateTime.UtcNow.ToString("o"));
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ProjectLexicalSearchResult>> SearchAsync(
        ProjectLexicalSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Query)) return [];

        var matchQuery = BuildMatchQuery(request.Query);
        if (string.IsNullOrWhiteSpace(matchQuery)) return [];

        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var cmd = connection.CreateCommand();
            var filters = new List<string> { $"{TableName} MATCH @query", "scope_key = @scopeKey" };
            cmd.Parameters.AddWithValue("@query", matchQuery);
            cmd.Parameters.AddWithValue("@scopeKey", request.ScopeKey);
            cmd.Parameters.AddWithValue("@topK", Math.Clamp(request.TopK, 1, 100));

            AddStringListFilter(cmd, filters, "source_type", "type", request.SourceTypes);
            AddStringListFilter(cmd, filters, "source_id", "source", request.SourceIds);
            if (!string.IsNullOrWhiteSpace(request.ContainerSourceId))
            {
                filters.Add("container_source_id = @containerSourceId");
                cmd.Parameters.AddWithValue("@containerSourceId", request.ContainerSourceId);
            }

            cmd.CommandText =
                $"""
                SELECT rowid,
                       source_type,
                       source_id,
                       container_source_id,
                       title,
                       content,
                       snippet({TableName}, 1, '[', ']', '...', 32) AS snippet,
                       metadata,
                       chunk_index,
                       bm25({TableName}) AS rank
                FROM {TableName}
                WHERE {string.Join(" AND ", filters)}
                ORDER BY rank, rowid
                LIMIT @topK
                """;

            var results = new List<ProjectLexicalSearchResult>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                results.Add(new ProjectLexicalSearchResult(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetInt32(8),
                    reader.GetDouble(9)));
            }

            return results;
        }
        catch (SqliteException ex)
        {
            logger.LogWarning(ex, "FTS project search failed for query {Query}", request.Query);
            return [];
        }
    }

    public async Task DeleteBySourceAsync(
        string sourceType,
        string sourceId,
        string scopeKey,
        CancellationToken cancellationToken = default)
    {
        await using var writeLease = await writes.AcquireAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            $"""
            DELETE FROM {TableName}
            WHERE scope_key = @scopeKey
              AND source_type = @sourceType
              AND source_id = @sourceId
            """;
        cmd.Parameters.AddWithValue("@scopeKey", scopeKey);
        cmd.Parameters.AddWithValue("@sourceType", sourceType);
        cmd.Parameters.AddWithValue("@sourceId", sourceId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteByScopeAsync(string scopeKey, CancellationToken cancellationToken = default)
    {
        await using var writeLease = await writes.AcquireAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"DELETE FROM {TableName} WHERE scope_key = @scopeKey";
        cmd.Parameters.AddWithValue("@scopeKey", scopeKey);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public static void Initialize(SqliteConnection connection)
    {
        using var create = connection.CreateCommand();
        create.CommandText =
            $"""
            CREATE VIRTUAL TABLE IF NOT EXISTS {TableName} USING fts5(
                title,
                content,
                metadata,
                scope_key UNINDEXED,
                source_type UNINDEXED,
                source_id UNINDEXED,
                container_source_id UNINDEXED,
                chunk_index UNINDEXED,
                created_at UNINDEXED,
                tokenize = 'unicode61'
            );
            """;
        create.ExecuteNonQuery();
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        SqliteConnectionSettings.ConfigureDatabase(connection);
        return connection;
    }

    private static void AddStringListFilter(
        SqliteCommand cmd,
        ICollection<string> filters,
        string column,
        string parameterPrefix,
        IReadOnlyCollection<string>? values)
    {
        var normalized = values?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalized is not { Count: > 0 }) return;

        var names = new List<string>();
        for (var i = 0; i < normalized.Count; i++)
        {
            var name = $"@{parameterPrefix}{i}";
            names.Add(name);
            cmd.Parameters.AddWithValue(name, normalized[i]);
        }

        filters.Add($"{column} IN ({string.Join(", ", names)})");
    }

    private static string BuildMatchQuery(string query)
    {
        var terms = SearchTermRegex().Matches(query)
            .Select(match => match.Value.Trim())
            .Where(term => term.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();

        if (terms.Count == 0) return string.Empty;
        if (terms.Count == 1) return Quote(terms[0]);

        var phrase = Quote(string.Join(' ', terms));
        var andTerms = string.Join(" AND ", terms.Select(Quote));
        return $"{phrase} OR ({andTerms})";
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}'_-]*", RegexOptions.CultureInvariant)]
    private static partial Regex SearchTermRegex();
}
