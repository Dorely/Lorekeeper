using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Lorekeeper.Publish;

public interface IPublicationPressMigrationService
{
    Task ApplyPendingAsync(AppDbContext db, CancellationToken cancellationToken = default);
}

public sealed class PublicationPressMigrationService(
    IConfiguration configuration,
    IDatabaseMigrationRecoveryService recovery,
    ILogger<PublicationPressMigrationService> logger) : IPublicationPressMigrationService
{
    public const string MigrationName = "lorekeeper-press-v15";
    public const string EfMigrationId = "20260801053905_LorekeeperPressV15";
    internal const string PreviousSchemaMigrationId = "20260801022548_PublicationCoverImagesV14";
    private static readonly string[] RequiredPreservedPublicationTables =
    [
        "PublicationArtifacts",
        "PublicationCoverDesigns",
        "PublicationEditions",
        "PublicationEditionAuditEntries",
        "PublicationEditionOutlineItems",
        "PublicationEditionStyleMappings",
        "PublicationImagePlacements",
        "PublicationMatter",
        "PublicationPageMapEntries",
        "PublicationRenderJobs",
        "PublishAssets",
        "PublishConversations",
        "PublishMessages",
    ];
    private readonly string _connectionString = SqliteConnectionSettings.BuildConnectionString(configuration);

    public async Task ApplyPendingAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        await using var migrationLock = await PublicationMigrationLock.AcquireAsync(
            _connectionString,
            cancellationToken);
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();
        if (pending.Contains(EfMigrationId, StringComparer.Ordinal)
            && pending.Contains(PreviousSchemaMigrationId, StringComparer.Ordinal))
        {
            await db.Database.GetService<IMigrator>()
                .MigrateAsync(PreviousSchemaMigrationId, cancellationToken);
            db.ChangeTracker.Clear();
            pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();
        }
        PressSnapshotMarker? marker;
        try
        {
            marker = await ReadMarkerAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            marker = await RecoverMalformedMarkerAsync(db, pending, exception, cancellationToken);
            if (marker is null && !pending.Contains(EfMigrationId, StringComparer.Ordinal))
                return;
        }
        if (pending.Contains(EfMigrationId, StringComparer.Ordinal))
        {
            var snapshot = await SnapshotAsync(cancellationToken);
            var backup = await recovery.CreateBackupAsync("press", "pre-press", cancellationToken);
            marker = new(snapshot, backup);
            await WriteMarkerAsync(marker, cancellationToken);
            try
            {
                await db.Database.GetService<IMigrator>().MigrateAsync(EfMigrationId, cancellationToken);
                await ReconcileRecoveredJobsAsync(db, cancellationToken);
                await ValidateAndJournalAsync(db, marker, resumed: false, cancellationToken);
                File.Delete(MarkerPath());
            }
            catch (Exception exception)
            {
                await FailSafelyAsync(db, marker.BackupPath, exception, cancellationToken);
            }
            return;
        }

        if (marker is not null)
        {
            try
            {
                await ReconcileRecoveredJobsAsync(db, cancellationToken);
                await ValidateAndJournalAsync(db, marker, resumed: true, cancellationToken);
                File.Delete(MarkerPath());
            }
            catch (Exception exception)
            {
                await FailSafelyAsync(db, marker.BackupPath, exception, cancellationToken);
            }
            return;
        }

        if (!(await db.Database.GetAppliedMigrationsAsync(cancellationToken))
                .Contains(EfMigrationId, StringComparer.Ordinal)
            || await HasJournalAsync(db, cancellationToken))
        {
            return;
        }

        var baseline = await SnapshotAsync(cancellationToken);
        var postMigrationBackup = await recovery.CreateBackupAsync(
            "press", "post-press-reconciliation", cancellationToken);
        await AddJournalAsync(
            db,
            baseline,
            baseline,
            postMigrationBackup,
            "post-v15-reconciliation",
            cancellationToken);
    }

    private async Task ValidateAndJournalAsync(
        AppDbContext db,
        PressSnapshotMarker marker,
        bool resumed,
        CancellationToken cancellationToken)
    {
        await EnsureHealthyAsync(cancellationToken);
        var target = await SnapshotAsync(cancellationToken);
        if (marker.Snapshot.EditionCount != target.EditionCount
            || marker.Snapshot.RenderJobCount != target.RenderJobCount
            || marker.Snapshot.ArtifactCount != target.ArtifactCount
            || !marker.Snapshot.PreservedRowCounts.SequenceEqual(target.PreservedRowCounts)
            || !string.Equals(marker.Snapshot.ArtifactHash, target.ArtifactHash, StringComparison.Ordinal)
            || !string.Equals(marker.Snapshot.PreservedDataHash, target.PreservedDataHash, StringComparison.Ordinal)
            || !string.Equals(marker.Snapshot.UnknownProfileHash, target.UnknownProfileHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Lorekeeper Press migration validation found changed publication or manuscript data.");
        }
        await AddJournalAsync(
            db,
            marker.Snapshot,
            target,
            marker.BackupPath,
            resumed ? "resumed" : "guarded-cutover",
            cancellationToken);
    }

    private static async Task ReconcileRecoveredJobsAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            WITH recovered AS (
                SELECT Id,
                       ROW_NUMBER() OVER (PARTITION BY EditionId ORDER BY CreatedAt DESC, Id DESC) AS RecoveryRank
                FROM PublicationRenderJobs
                WHERE IsLegacy = 0
                  AND RendererVersion = ''
                  AND Status = 'Queued'
                  AND ProgressMessage = 'Recovered for Lorekeeper Press 1.0'
            )
            UPDATE PublicationRenderJobs
            SET Status = 'Failed',
                IsLegacy = 1,
                ProgressPercent = 100,
                ProgressMessage = 'Source changed before Lorekeeper Press recovery',
                DiagnosticsJson = '[{{"severity":"error","code":"PRESS_SOURCE_STALE","message":"A newer interrupted render superseded this source during the Lorekeeper Press cutover."}}]',
                CompletedAt = CURRENT_TIMESTAMP
            WHERE Id IN (SELECT Id FROM recovered WHERE RecoveryRank > 1);
            """,
            cancellationToken);
    }

    private async Task AddJournalAsync(
        AppDbContext db,
        PressSnapshot source,
        PressSnapshot target,
        string backupPath,
        string mode,
        CancellationToken cancellationToken)
    {
        if (await HasJournalAsync(db, cancellationToken))
            return;
        db.PublicationEditionMigrationJournals.Add(new PublicationEditionMigrationJournal
        {
            MigrationName = MigrationName,
            Status = "Completed",
            BackupPath = backupPath,
            SourceProfileCount = checked((int)source.EditionCount),
            SourceSelectionCount = checked((int)source.RenderJobCount),
            SourcePlacementCount = checked((int)source.ArtifactCount),
            EditionCount = checked((int)target.EditionCount),
            OutlineItemCount = checked((int)target.RenderJobCount),
            PlacementCount = checked((int)target.ArtifactCount),
            SourceHash = source.CombinedHash,
            TargetHash = target.CombinedHash,
            ValidationReportJson = JsonSerializer.Serialize(new
            {
                quickCheck = "ok",
                foreignKeys = "ok",
                artifactBytesAndHashes = "preserved",
                unknownProfiles = "preserved",
                mode,
            }),
            CompletedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static Task<bool> HasJournalAsync(AppDbContext db, CancellationToken cancellationToken) =>
        db.PublicationEditionMigrationJournals.AsNoTracking().AnyAsync(
            journal => journal.MigrationName == MigrationName && journal.Status == "Completed",
            cancellationToken);

    private async Task FailSafelyAsync(
        AppDbContext db,
        string backupPath,
        Exception exception,
        CancellationToken cancellationToken)
    {
        logger.LogError(exception, "Lorekeeper Press cutover failed; protected backup {BackupPath} remains available.", backupPath);
        db.ChangeTracker.Clear();
        await db.Database.CloseConnectionAsync();
        await recovery.EnterRecoveryModeAsync(
            db, backupPath, MigrationName, 14, 15, exception, cancellationToken);
        if (File.Exists(MarkerPath()))
            File.Delete(MarkerPath());
    }

    private async Task<PressSnapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureHealthyAsync(connection, cancellationToken);
        var tables = new[]
        {
            "Projects", "Acts", "Chapters", "ManuscriptStyleDefinitions",
        };
        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in tables.Concat(RequiredPreservedPublicationTables))
        {
            await RequireTableAsync(connection, table, cancellationToken);
            counts[table] = await ScalarAsync(connection, $"SELECT COUNT(*) FROM \"{table}\";", cancellationToken);
        }
        var editionCount = await CountIfPresentAsync(connection, "PublicationEditions", cancellationToken);
        var jobCount = await CountIfPresentAsync(connection, "PublicationRenderJobs", cancellationToken);
        var artifactCount = await CountIfPresentAsync(connection, "PublicationArtifacts", cancellationToken);
        var artifactHash = await ArtifactHashAsync(connection, cancellationToken);
        var unknownProfileHash = await UnknownProfileHashAsync(connection, cancellationToken);
        var preservedDataHash = await PreservedDataHashAsync(connection, cancellationToken);
        var combined = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { editionCount, jobCount, artifactCount, counts, artifactHash, unknownProfileHash, preservedDataHash }))));
        return new(editionCount, jobCount, artifactCount, counts, artifactHash, unknownProfileHash, preservedDataHash, combined);
    }

    private static async Task<string> ArtifactHashAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "PublicationArtifacts", cancellationToken))
            return string.Empty;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, EditionId, RenderJobId, Kind, FileName, MediaType, Data, Sha256,
                   ByteLength, PageCount, SourceFingerprint, RendererVersion, ProfileId, CreatedAt
            FROM PublicationArtifacts ORDER BY Id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            AppendRow(hash, reader);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static async Task<string> PreservedDataHashAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var tables = await ApplicationTablesAsync(connection, cancellationToken);
        foreach (var table in tables)
        {
            IReadOnlyCollection<string> excluded = table switch
            {
                "PublicationEditions" => ["VendorProfileVersion"],
                "PublicationArtifacts" => ["IsLegacy"],
                "PublicationRenderJobs" => ["RendererVersion", "ProfileId", "Status", "DiagnosticsJson", "ProgressPercent", "ProgressMessage", "StartedAt", "CompletedAt", "IsLegacy"],
                _ => [],
            };
            await AppendTableAsync(connection, hash, table, excluded, cancellationToken);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static async Task<IReadOnlyList<string>> ApplicationTablesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var tables = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' " +
            "AND (sql IS NULL OR upper(sql) NOT LIKE 'CREATE VIRTUAL TABLE%') " +
            "AND name NOT IN ('__EFMigrationsHistory','PublicationEditionMigrationJournals') ORDER BY name;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            tables.Add(reader.GetString(0));
        foreach (var required in RequiredPreservedPublicationTables)
        {
            if (!tables.Contains(required, StringComparer.Ordinal))
                throw new InvalidDataException($"The required publication table '{required}' is missing.");
        }
        return tables;
    }

    private static async Task AppendTableAsync(
        SqliteConnection connection,
        IncrementalHash hash,
        string table,
        IReadOnlyCollection<string> excluded,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, table, cancellationToken))
            return;
        var columns = new List<string>();
        await using (var info = connection.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info(\"{table}\");";
            await using var reader = await info.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var column = reader.GetString(1);
                if (!excluded.Contains(column))
                    columns.Add(column);
            }
        }
        if (columns.Count == 0)
            return;
        hash.AppendData(Encoding.UTF8.GetBytes(table));
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {string.Join(",", columns.Select(column => $"\"{column}\""))} FROM \"{table}\" ORDER BY \"{columns[0]}\";";
        await using var rows = await command.ExecuteReaderAsync(cancellationToken);
        while (await rows.ReadAsync(cancellationToken))
            AppendRow(hash, rows);
    }

    private static void AppendRow(IncrementalHash hash, SqliteDataReader reader)
    {
        for (var index = 0; index < reader.FieldCount; index++)
        {
            if (reader.IsDBNull(index))
            {
                hash.AppendData([0]);
                continue;
            }
            var value = reader.GetValue(index);
            var bytes = value switch
            {
                byte[] blob => blob,
                _ => Encoding.UTF8.GetBytes(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty),
            };
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }
    }

    private static async Task<string> UnknownProfileHashAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "PublicationEditions", cancellationToken))
            return string.Empty;
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, VendorProfileVersion FROM PublicationEditions
            WHERE VendorProfileVersion NOT IN ('preview-1', 'kdp-paperback-6x9-preview-v1',
                'ingram-pdf-x1a-preview-v1', 'kdp-paperback-v1',
                'ingram-paperback-pdfx1a-v1', 'generic-paperback-v1', 'epub3-v1')
            ORDER BY Id;
            """;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(reader.GetString(0)));
            hash.AppendData(Encoding.UTF8.GetBytes(reader.GetString(1)));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private async Task EnsureHealthyAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureHealthyAsync(connection, cancellationToken);
    }

    private static async Task EnsureHealthyAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (await TextScalarAsync(connection, "PRAGMA quick_check;", cancellationToken) != "ok"
            || await ScalarAsync(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;", cancellationToken) != 0)
        {
            throw new InvalidDataException("The database failed integrity or foreign-key validation.");
        }
    }

    private async Task<PressSnapshotMarker?> ReadMarkerAsync(CancellationToken cancellationToken)
    {
        var markerPath = MarkerPath();
        if (!File.Exists(markerPath))
            return null;
        ProtectPath(markerPath, directory: false);
        var marker = JsonSerializer.Deserialize<PressSnapshotMarker>(
            await File.ReadAllTextAsync(markerPath, cancellationToken))
            ?? throw new InvalidDataException("The Lorekeeper Press migration marker is malformed.");
        var backupRoot = Path.GetFullPath(Path.GetDirectoryName(markerPath)!);
        var backup = Path.GetFullPath(marker.BackupPath);
        if (!backup.StartsWith(backupRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(backup))
        {
            throw new InvalidDataException("The Lorekeeper Press migration marker references an invalid protected backup.");
        }
        return marker;
    }

    private async Task WriteMarkerAsync(PressSnapshotMarker marker, CancellationToken cancellationToken)
    {
        var path = MarkerPath();
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(marker), cancellationToken);
        File.Move(temporary, path, overwrite: true);
        ProtectPath(path, directory: false);
    }

    private string MarkerPath()
    {
        var database = new SqliteConnectionStringBuilder(_connectionString).DataSource;
        var directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(database))!, ".migration-backups", "press");
        Directory.CreateDirectory(directory);
        ProtectPath(directory, directory: true);
        return Path.Combine(directory, "lorekeeper-press-v15.pending.json");
    }

    private async Task<PressSnapshotMarker?> RecoverMalformedMarkerAsync(
        AppDbContext db,
        IEnumerable<string> pending,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var marker = MarkerPath();
        var quarantine = marker + $".{DateTime.UtcNow:yyyyMMddHHmmssfff}.invalid";
        File.Move(marker, quarantine, overwrite: false);
        ProtectPath(quarantine, directory: false);
        logger.LogError(exception, "Quarantined malformed Lorekeeper Press migration marker {MarkerPath}.", quarantine);
        if (pending.Contains(EfMigrationId, StringComparer.Ordinal))
            return null;

        var backup = Directory
            .EnumerateFiles(Path.GetDirectoryName(marker)!, "*-pre-press.db", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (backup is null)
            throw new InvalidDataException("The malformed Lorekeeper Press marker has no protected source backup.", exception);
        await recovery.EnterRecoveryModeAsync(
            db,
            backup,
            MigrationName,
            14,
            15,
            new InvalidDataException("The Lorekeeper Press recovery marker was malformed.", exception),
            cancellationToken);
        return null;
    }

    private static void ProtectPath(string path, bool directory)
    {
        if (OperatingSystem.IsWindows())
        {
            var identity = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
            FileSystemSecurity security = directory ? new DirectorySecurity() : new FileSecurity();
            security.SetOwner(identity);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                identity,
                FileSystemRights.FullControl,
                directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Allow));
            if (directory)
                new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
            else
                new FileInfo(path).SetAccessControl((FileSecurity)security);
            return;
        }
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(
                path,
                directory
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static async Task<long> CountIfPresentAsync(SqliteConnection connection, string table, CancellationToken cancellationToken) =>
        await TableExistsAsync(connection, table, cancellationToken)
            ? await ScalarAsync(connection, $"SELECT COUNT(*) FROM \"{table}\";", cancellationToken)
            : 0;

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static async Task RequireTableAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, table, cancellationToken))
            throw new InvalidDataException($"The required preserved table '{table}' is missing.");
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<string> TextScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)) ?? string.Empty;
    }

    private sealed record PressSnapshot(
        long EditionCount,
        long RenderJobCount,
        long ArtifactCount,
        SortedDictionary<string, long> PreservedRowCounts,
        string ArtifactHash,
        string UnknownProfileHash,
        string PreservedDataHash,
        string CombinedHash);

    private sealed record PressSnapshotMarker(PressSnapshot Snapshot, string BackupPath);
}
