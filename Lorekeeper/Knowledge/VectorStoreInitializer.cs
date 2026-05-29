using Microsoft.Data.Sqlite;
using Lorekeeper.Persistence;
using Lorekeeper.Search;

namespace Lorekeeper.Knowledge;

/// <summary>
/// Loads the sqlite-vec extension and ensures the <c>knowledge_chunks</c> metadata table
/// exists. Creates <c>vec_knowledge</c> only when embedding dimensions are configured.
/// Run once at startup before any vector reads or writes.
/// </summary>
public static class VectorStoreInitializer
{
    public static void Initialize(IConfiguration configuration, ILogger logger, int? dimensions)
    {
        var connectionString = SqliteConnectionSettings.BuildConnectionString(configuration);

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        SqliteConnectionSettings.ConfigureDatabase(connection);
        connection.LoadVector();

        using (var createMeta = connection.CreateCommand())
        {
            createMeta.CommandText =
                """
                CREATE TABLE IF NOT EXISTS knowledge_chunks (
                    id INTEGER PRIMARY KEY,
                    content TEXT NOT NULL,
                    source_type TEXT NOT NULL,
                    source_id TEXT,
                    scope_key TEXT NOT NULL DEFAULT 'default',
                    metadata TEXT,
                    chunk_index INTEGER,
                    created_at TEXT NOT NULL
                );
                """;
            createMeta.ExecuteNonQuery();
        }

        using (var createIndex = connection.CreateCommand())
        {
            createIndex.CommandText =
                "CREATE INDEX IF NOT EXISTS ix_knowledge_chunks_scope_key ON knowledge_chunks(scope_key);";
            createIndex.ExecuteNonQuery();
        }

        SqliteFtsProjectSearchIndex.Initialize(connection);

        if (dimensions is int vectorDimensions)
        {
            using var createVec = connection.CreateCommand();
            createVec.CommandText =
                $"CREATE VIRTUAL TABLE IF NOT EXISTS vec_knowledge USING vec0(embedding float[{vectorDimensions}])";
            createVec.ExecuteNonQuery();
            logger.LogInformation("Vector store ready (dimensions={Dimensions})", vectorDimensions);
            return;
        }

        logger.LogInformation("Vector metadata store ready; vector table not created because no embedding model is configured.");
    }
}
