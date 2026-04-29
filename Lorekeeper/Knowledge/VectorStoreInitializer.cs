using Microsoft.Data.Sqlite;

namespace Lorekeeper.Knowledge;

/// <summary>
/// Loads the sqlite-vec extension and ensures the <c>knowledge_chunks</c> metadata table
/// + <c>vec_knowledge</c> virtual table exist. Run once at startup before any vector
/// reads or writes.
/// </summary>
public static class VectorStoreInitializer
{
    public static void Initialize(IConfiguration configuration, ILogger logger)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? "Data Source=lorekeeper.db";
        var dimensions = configuration.GetValue("Embeddings:Dimensions", 768);

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
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

        using (var createVec = connection.CreateCommand())
        {
            createVec.CommandText =
                $"CREATE VIRTUAL TABLE IF NOT EXISTS vec_knowledge USING vec0(embedding float[{dimensions}])";
            createVec.ExecuteNonQuery();
        }

        logger.LogInformation("Vector store ready (dimensions={Dimensions})", dimensions);
    }
}
