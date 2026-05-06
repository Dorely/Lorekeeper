using Microsoft.Data.Sqlite;

namespace Lorekeeper.Persistence;

public static class SqliteConnectionSettings
{
    public const int BusyTimeoutSeconds = 60;
    public const int BusyTimeoutMilliseconds = BusyTimeoutSeconds * 1000;

    public static string BuildConnectionString(IConfiguration configuration)
    {
        var configured = configuration.GetConnectionString("DefaultConnection")
            ?? "Data Source=lorekeeper.db";
        var builder = new SqliteConnectionStringBuilder(configured);
        if (builder.DefaultTimeout < BusyTimeoutSeconds)
            builder.DefaultTimeout = BusyTimeoutSeconds;
        return builder.ToString();
    }

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