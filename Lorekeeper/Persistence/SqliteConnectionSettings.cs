using Microsoft.Data.Sqlite;

namespace Lorekeeper.Persistence;

public static class SqliteConnectionSettings
{
    public const int BusyTimeoutSeconds = 60;
    public const int BusyTimeoutMilliseconds = BusyTimeoutSeconds * 1000;

    public static string BuildConnectionString(
        IConfiguration configuration,
        bool usePerUserDataDirectory = false)
    {
        var configured = configuration.GetConnectionString("DefaultConnection")
            ?? "Data Source=lorekeeper.db";
        var builder = new SqliteConnectionStringBuilder(configured);

        if (usePerUserDataDirectory && IsRelativeFilePath(builder.DataSource))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
                throw new InvalidOperationException("The per-user local application data directory is unavailable.");

            var dataDirectory = Path.Combine(localAppData, "Lorekeeper", "Data");
            var databasePath = Path.GetFullPath(Path.Combine(dataDirectory, builder.DataSource));
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
            builder.DataSource = databasePath;
        }

        if (builder.DefaultTimeout < BusyTimeoutSeconds)
            builder.DefaultTimeout = BusyTimeoutSeconds;
        return builder.ToString();
    }

    private static bool IsRelativeFilePath(string dataSource) =>
        !string.IsNullOrWhiteSpace(dataSource)
        && !dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
        && !dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
        && !Path.IsPathFullyQualified(dataSource);

    public static void ConfigureDatabase(SqliteConnection connection)
    {
        Execute(connection, $"PRAGMA busy_timeout={BusyTimeoutMilliseconds};");
        Execute(connection, "PRAGMA journal_mode=WAL;");
        Execute(connection, "PRAGMA synchronous=NORMAL;");
    }

    private static void Execute(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }
}
