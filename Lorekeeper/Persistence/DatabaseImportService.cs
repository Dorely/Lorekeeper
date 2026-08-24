using Lorekeeper.Manuscripts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence;

public interface IDatabaseImportService
{
    /// <summary>
    /// Streams an uploaded database file into the protected staging location and
    /// validates it as an importable Lorekeeper database. Throws with a clear
    /// message and stages nothing usable when the file is foreign, corrupt,
    /// oversized, or produced by a newer Lorekeeper.
    /// </summary>
    Task<DatabaseImportCandidate> StageAsync(
        Stream content,
        long maxBytes,
        CancellationToken cancellationToken = default);

    Task<ManuscriptRestoreRequest> PrepareImportAsync(
        string stagedPath,
        CancellationToken cancellationToken = default);

    Task ScheduleImportAsync(
        string stagedPath,
        string confirmationToken,
        CancellationToken cancellationToken = default);
}

/// <summary>Validated summary of a staged database import candidate.</summary>
public sealed record DatabaseImportCandidate(
    string StagedPath,
    long SizeBytes,
    int ProjectCount,
    IReadOnlyList<string> ProjectNames,
    int AppliedMigrationCount,
    int PendingMigrationCount);

/// <summary>
/// Whole-database import boundary. An uploaded database is staged inside the
/// protected backup root and applied through the existing scheduled-restore
/// contract: the next startup creates a diagnostic backup of the current
/// database, restores the staged file before any worker runs, and then the
/// ordered startup migration chain upgrades the imported content exactly like
/// a legacy database opened in place.
/// </summary>
public sealed class DatabaseImportService(
    IConfiguration configuration,
    IAppDatabaseOperationFactory database,
    IDatabaseMigrationRecoveryService recovery,
    ILogger<DatabaseImportService> logger) : IDatabaseImportService
{
    public const long DefaultMaxImportBytes = 8L * 1024 * 1024 * 1024;
    public const string StagedFileName = "staged-import.db";

    private static readonly byte[] SqliteHeader = "SQLite format 3\0"u8.ToArray();
    private readonly string _connectionString = SqliteConnectionSettings.BuildConnectionString(configuration);

    public async Task<DatabaseImportCandidate> StageAsync(
        Stream content,
        long maxBytes,
        CancellationToken cancellationToken = default)
    {
        var stagedPath = StagedImportPath();
        var temporaryPath = stagedPath + ".tmp";
        try
        {
            long totalBytes;
            await using (var destination = new FileStream(
                temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                totalBytes = await CopyBoundedAsync(content, destination, maxBytes, cancellationToken);
            }

            if (totalBytes < 1024)
                throw new InvalidDataException("The uploaded file is too small to be a Lorekeeper database.");

            var candidate = await ValidateAsync(temporaryPath, totalBytes, cancellationToken);
            SqliteConnection.ClearAllPools();
            File.Move(temporaryPath, stagedPath, overwrite: true);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(stagedPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            logger.LogInformation(
                "Staged database import candidate with {ProjectCount} projects and {PendingCount} pending migrations.",
                candidate.ProjectCount,
                candidate.PendingMigrationCount);
            return candidate with { StagedPath = stagedPath };
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public Task<ManuscriptRestoreRequest> PrepareImportAsync(
        string stagedPath,
        CancellationToken cancellationToken = default)
    {
        RequireStagedPath(stagedPath);
        return recovery.PrepareRestoreAsync(stagedPath, cancellationToken);
    }

    public Task ScheduleImportAsync(
        string stagedPath,
        string confirmationToken,
        CancellationToken cancellationToken = default)
    {
        RequireStagedPath(stagedPath);
        return recovery.ScheduleRestoreAsync(stagedPath, confirmationToken, cancellationToken);
    }

    private async Task<DatabaseImportCandidate> ValidateAsync(
        string path,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        await using (var header = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var prefix = new byte[SqliteHeader.Length];
            await header.ReadExactlyAsync(prefix, cancellationToken);
            if (!prefix.AsSpan().SequenceEqual(SqliteHeader))
                throw new InvalidDataException("The uploaded file is not a SQLite database.");
        }

        await using var connection = new SqliteConnection(
            $"Data Source={path};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(cancellationToken);

        var integrity = (string?)await ScalarAsync(connection, "PRAGMA quick_check;", cancellationToken);
        if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The uploaded database failed its integrity check: {integrity}");

        if (!await HasTableAsync(connection, "__EFMigrationsHistory", cancellationToken)
            || !await HasTableAsync(connection, "Projects", cancellationToken))
        {
            throw new InvalidDataException(
                "The uploaded database has no Lorekeeper schema. Choose a lorekeeper.db file from another Lorekeeper installation.");
        }

        var appliedMigrations = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT MigrationId FROM \"__EFMigrationsHistory\" ORDER BY MigrationId;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                appliedMigrations.Add(reader.GetString(0));
        }

        IReadOnlyList<string> knownMigrations;
        await using (var operation = await database.OpenReadAsync(cancellationToken))
            knownMigrations = [.. operation.Db.Database.GetMigrations()];
        var known = knownMigrations.ToHashSet(StringComparer.Ordinal);
        var unknown = appliedMigrations.Where(id => !known.Contains(id)).ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidDataException(
                "The uploaded database was created by a newer Lorekeeper version "
                + $"(unrecognized migration {unknown[0]}). Update this installation first.");
        }

        var projectCount = Convert.ToInt32(
            await ScalarAsync(connection, "SELECT COUNT(*) FROM \"Projects\";", cancellationToken));
        var projectNames = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT Name FROM \"Projects\" ORDER BY Name LIMIT 6;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                projectNames.Add(reader.IsDBNull(0) ? "(unnamed project)" : reader.GetString(0));
        }

        return new DatabaseImportCandidate(
            path,
            totalBytes,
            projectCount,
            projectNames,
            appliedMigrations.Count,
            Math.Max(0, knownMigrations.Count - appliedMigrations.Count));
    }

    private static async Task<long> CopyBoundedAsync(
        Stream source,
        Stream destination,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[1 << 16];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > maxBytes)
                throw new InvalidDataException(
                    $"The uploaded file exceeds the {maxBytes / (1024 * 1024)} MB import limit.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return total;
    }

    private void RequireStagedPath(string stagedPath)
    {
        if (!string.Equals(
                Path.GetFullPath(stagedPath),
                StagedImportPath(),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only the staged import candidate can be scheduled for import.");
        }
    }

    private string StagedImportPath()
    {
        var databasePath = Path.GetFullPath(new SqliteConnectionStringBuilder(_connectionString).DataSource);
        var directory = Path.Combine(
            Path.GetDirectoryName(databasePath)!,
            DatabaseMigrationRecoveryService.BackupDirectoryName,
            "import");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, StagedFileName);
    }

    private static async Task<object?> ScalarAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task<bool> HasTableAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $table;";
        command.Parameters.AddWithValue("$table", table);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }
}
