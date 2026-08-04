using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Lorekeeper.Models;
using Lorekeeper.Manuscripts;
using Lorekeeper.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Lorekeeper.Publish;

public interface IPublicationEditionMigrationService
{
    Task ApplyPendingAsync(AppDbContext db, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PublicationEditionMigrationJournal>> GetHistoryAsync(
        CancellationToken cancellationToken = default);
}

public sealed class PublicationEditionMigrationService(
    IConfiguration configuration,
    IDatabaseMigrationRecoveryService recovery,
    ILogger<PublicationEditionMigrationService> logger) : IPublicationEditionMigrationService
{
    public const string MigrationName = "publication-editions-v10";
    public const string EfMigrationId = "20260730203619_PublicationEditionsV10";
    internal const string CoverImagesV14MigrationId = "20260801022548_PublicationCoverImagesV14";
    private readonly string _connectionString = SqliteConnectionSettings.BuildConnectionString(configuration);

    public async Task ApplyPendingAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        await using var migrationLock = await PublicationMigrationLock.AcquireAsync(
            _connectionString,
            cancellationToken);
        await ReconcileInterruptedCoverMigrationAsync(db, cancellationToken);
        var pending = await db.Database.GetPendingMigrationsAsync(cancellationToken);
        if (!pending.Contains(EfMigrationId, StringComparer.Ordinal)
            && File.Exists(MarkerPath()))
        {
            await ResumeValidationAsync(db, cancellationToken);
            return;
        }
        if (!pending.Contains(EfMigrationId, StringComparer.Ordinal))
            return;

        await using var source = new SqliteConnection(_connectionString);
        await source.OpenAsync(cancellationToken);
        await EnsureHealthyAsync(source, cancellationToken);
        var sourceCounts = await ReadSourceCountsAsync(source, cancellationToken);
        var sourceHash = await ReadShapeHashAsync(source, legacy: true, cancellationToken);
        var backupPath = await CreateBackupAsync(source, cancellationToken);
        var marker = new MigrationMarker(sourceCounts, sourceHash, backupPath);
        await File.WriteAllTextAsync(
            MarkerPath(),
            JsonSerializer.Serialize(marker),
            cancellationToken);
        ProtectPath(MarkerPath(), directory: false);
        try
        {
            var migrator = db.Database.GetService<IMigrator>();
            await migrator.MigrateAsync(EfMigrationId, cancellationToken);
            db.ChangeTracker.Clear();
            await db.Database.OpenConnectionAsync(cancellationToken);
            await EnsureHealthyAsync((SqliteConnection)db.Database.GetDbConnection(), cancellationToken);
            var targetCounts = await ReadTargetCountsAsync(
                (SqliteConnection)db.Database.GetDbConnection(),
                cancellationToken);
            var targetHash = await ReadShapeHashAsync(
                (SqliteConnection)db.Database.GetDbConnection(),
                legacy: false,
                cancellationToken);
            Validate(sourceCounts, targetCounts, sourceHash, targetHash);
            await ValidateMatterAsync((SqliteConnection)db.Database.GetDbConnection(), cancellationToken);

            db.PublicationEditionMigrationJournals.Add(new PublicationEditionMigrationJournal
            {
                MigrationName = MigrationName,
                Status = "Completed",
                BackupPath = backupPath,
                SourceProfileCount = sourceCounts.Profiles,
                SourceSelectionCount = sourceCounts.Selections,
                SourcePlacementCount = sourceCounts.Placements,
                EditionCount = targetCounts.Editions,
                OutlineItemCount = targetCounts.Selections,
                PlacementCount = targetCounts.Placements,
                SourceHash = sourceHash,
                TargetHash = targetHash,
                ValidationReportJson =
                    """{"quickCheck":"ok","foreignKeys":"ok","mappingHash":"equal","matter":"schema-v2"}""",
                CompletedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(cancellationToken);
            File.Delete(MarkerPath());
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Publication-edition migration failed. The protected source backup is {BackupPath}.",
                backupPath);
            db.ChangeTracker.Clear();
            await db.Database.CloseConnectionAsync();
            await recovery.EnterRecoveryModeAsync(
                db,
                backupPath,
                MigrationName,
                sourceVersion: 9,
                targetVersion: 10,
                exception,
                cancellationToken);
            File.Delete(MarkerPath());
            logger.LogWarning(
                "Publication-edition migration entered the recoverable projectless shell. Restore {BackupPath} from Data Recovery.",
                backupPath);
            return;
        }
    }

    internal static async Task ReconcileInterruptedCoverMigrationAsync(
        AppDbContext db,
        CancellationToken cancellationToken = default)
    {
        if ((await db.Database.GetAppliedMigrationsAsync(cancellationToken))
            .Contains(CoverImagesV14MigrationId, StringComparer.Ordinal))
        {
            return;
        }

        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        if (!await TableExistsAsync(connection, "PublicationEditions", cancellationToken))
            return;
        var columns = await TableColumnsAsync(connection, "PublicationEditions", cancellationToken);
        var hasLegacyColumn = columns.Contains("SelectedCoverChapterId");
        var hasImageColumn = columns.Contains("SelectedCoverImageId");
        var hasTemporaryTable = await TableExistsAsync(
            connection,
            "ef_temp_PublicationEditions",
            cancellationToken);

        if (hasLegacyColumn && !hasImageColumn && !hasTemporaryTable)
            return;
        if (hasLegacyColumn && hasImageColumn && hasTemporaryTable)
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                PRAGMA foreign_keys = 0;
                BEGIN IMMEDIATE;
                DROP TABLE "PublicationEditions";
                ALTER TABLE "ef_temp_PublicationEditions" RENAME TO "PublicationEditions";
                COMMIT;
                PRAGMA foreign_keys = 1;
                """,
                cancellationToken);
            columns = await TableColumnsAsync(connection, "PublicationEditions", cancellationToken);
            hasLegacyColumn = columns.Contains("SelectedCoverChapterId");
            hasImageColumn = columns.Contains("SelectedCoverImageId");
            hasTemporaryTable = false;
        }

        if (hasLegacyColumn || !hasImageColumn || hasTemporaryTable)
        {
            throw new InvalidOperationException(
                "The interrupted cover-image migration is in an unrecognized schema state and cannot be resumed safely.");
        }
        if (!await HasCoverImageForeignKeyAsync(connection, cancellationToken))
            throw new InvalidOperationException("The interrupted cover-image migration is missing its image foreign key.");
        await EnsureNoForeignKeyViolationsAsync(connection, cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_PublicationEditions_ProjectId_IsDefault"
                ON "PublicationEditions" ("ProjectId", "IsDefault") WHERE "IsDefault" = 1;
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_PublicationEditions_ProjectId_Name"
                ON "PublicationEditions" ("ProjectId", "Name");
            CREATE INDEX IF NOT EXISTS "IX_PublicationEditions_SelectedCoverImageId"
                ON "PublicationEditions" ("SelectedCoverImageId");
            INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
            SELECT '20260801022548_PublicationCoverImagesV14', '10.0.5'
            WHERE NOT EXISTS (
                SELECT 1 FROM "__EFMigrationsHistory"
                WHERE "MigrationId" = '20260801022548_PublicationCoverImagesV14');
            """,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<HashSet<string>> TableColumnsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info('{tableName.Replace("'", "''", StringComparison.Ordinal)}');";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var columns = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
            columns.Add(reader.GetString(1));
        return columns;
    }

    private static async Task<bool> HasCoverImageForeignKeyAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_list('PublicationEditions');";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(2), "PublishAssets", StringComparison.Ordinal)
                && string.Equals(reader.GetString(3), "SelectedCoverImageId", StringComparison.Ordinal)
                && string.Equals(reader.GetString(6), "SET NULL", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static async Task EnsureNoForeignKeyViolationsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("The interrupted cover-image migration contains foreign-key violations.");
    }

    private async Task ResumeValidationAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var marker = JsonSerializer.Deserialize<MigrationMarker>(
            await File.ReadAllTextAsync(MarkerPath(), cancellationToken))
            ?? throw new InvalidDataException("The publication migration recovery marker is malformed.");
        db.ChangeTracker.Clear();
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await EnsureHealthyAsync(connection, cancellationToken);
        var target = await ReadTargetCountsAsync(connection, cancellationToken);
        var targetHash = await ReadShapeHashAsync(connection, legacy: false, cancellationToken);
        Validate(marker.SourceCounts, target, marker.SourceHash, targetHash);
        await ValidateMatterAsync(connection, cancellationToken);
        if (!await db.PublicationEditionMigrationJournals.AnyAsync(
            journal => journal.MigrationName == MigrationName && journal.Status == "Completed",
            cancellationToken))
        {
            db.PublicationEditionMigrationJournals.Add(new PublicationEditionMigrationJournal
            {
                MigrationName = MigrationName,
                Status = "Completed",
                BackupPath = marker.BackupPath,
                SourceProfileCount = marker.SourceCounts.Profiles,
                SourceSelectionCount = marker.SourceCounts.Selections,
                SourcePlacementCount = marker.SourceCounts.Placements,
                EditionCount = target.Editions,
                OutlineItemCount = target.Selections,
                PlacementCount = target.Placements,
                SourceHash = marker.SourceHash,
                TargetHash = targetHash,
                ValidationReportJson =
                    """{"quickCheck":"ok","foreignKeys":"ok","mappingHash":"equal","matter":"schema-v2","resumed":true}""",
                CompletedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        File.Delete(MarkerPath());
    }

    public async Task<IReadOnlyList<PublicationEditionMigrationJournal>> GetHistoryAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        if (!await TableExistsAsync(connection, "PublicationEditionMigrationJournals", cancellationToken))
            return [];
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, MigrationName, Status, BackupPath, SourceProfileCount, SourceSelectionCount,
                   SourcePlacementCount, EditionCount, OutlineItemCount, PlacementCount, SourceHash,
                   TargetHash, ValidationReportJson, ErrorDetail, StartedAt, CompletedAt
            FROM PublicationEditionMigrationJournals
            ORDER BY StartedAt DESC;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<PublicationEditionMigrationJournal>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new PublicationEditionMigrationJournal
            {
                Id = Guid.Parse(reader.GetString(0)),
                MigrationName = reader.GetString(1),
                Status = reader.GetString(2),
                BackupPath = reader.GetString(3),
                SourceProfileCount = reader.GetInt32(4),
                SourceSelectionCount = reader.GetInt32(5),
                SourcePlacementCount = reader.GetInt32(6),
                EditionCount = reader.GetInt32(7),
                OutlineItemCount = reader.GetInt32(8),
                PlacementCount = reader.GetInt32(9),
                SourceHash = reader.GetString(10),
                TargetHash = reader.GetString(11),
                ValidationReportJson = reader.GetString(12),
                ErrorDetail = reader.IsDBNull(13) ? null : reader.GetString(13),
                StartedAt = reader.GetDateTime(14),
                CompletedAt = reader.IsDBNull(15) ? null : reader.GetDateTime(15),
            });
        }
        return result;
    }

    private async Task<string> CreateBackupAsync(
        SqliteConnection source,
        CancellationToken cancellationToken)
    {
        var dataSource = new SqliteConnectionStringBuilder(_connectionString).DataSource;
        var root = Path.GetDirectoryName(Path.GetFullPath(dataSource))!;
        var directory = Path.Combine(root, ".migration-backups", "editions");
        Directory.CreateDirectory(directory);
        ProtectPath(directory, directory: true);
        var path = Path.Combine(directory, $"pre-editions-{DateTime.UtcNow:yyyyMMddHHmmssfff}.db");
        await using var destination = new SqliteConnection($"Data Source={path}");
        await destination.OpenAsync(cancellationToken);
        source.BackupDatabase(destination);
        await EnsureHealthyAsync(destination, cancellationToken);
        ProtectPath(path, directory: false);
        return path;
    }

    private string MarkerPath()
    {
        var dataSource = new SqliteConnectionStringBuilder(_connectionString).DataSource;
        var root = Path.GetDirectoryName(Path.GetFullPath(dataSource))!;
        var directory = Path.Combine(root, ".migration-backups", "editions");
        Directory.CreateDirectory(directory);
        ProtectPath(directory, directory: true);
        return Path.Combine(directory, "publication-editions-v10.pending.json");
    }

    private static void ProtectPath(string path, bool directory)
    {
        if (OperatingSystem.IsWindows())
        {
            var identity = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
            FileSystemSecurity security = directory
                ? new DirectorySecurity()
                : new FileSecurity();
            security.SetOwner(identity);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                identity,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            if (directory)
                new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
            else
                new FileInfo(path).SetAccessControl((FileSecurity)security);
            return;
        }
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        File.SetUnixFileMode(
            path,
            directory
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static async Task ValidateMatterAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, ManuscriptJson, Revision FROM PublicationMatter;";
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = Guid.Parse(reader.GetString(0));
                _ = Manuscripts.ManuscriptCodec.Deserialize(reader.GetString(1), id, reader.GetInt64(2));
            }
        }
        var foreignKeyFailures = await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM pragma_foreign_key_check;",
            cancellationToken);
        if (foreignKeyFailures != 0)
            throw new InvalidDataException("Publication-edition migration produced invalid foreign keys.");
    }

    private static void Validate(
        MigrationCounts source,
        MigrationCounts target,
        string sourceHash,
        string targetHash)
    {
        if (source.ExpectedEditions != target.Editions
            || source.Selections != target.Selections
            || source.Placements != target.Placements
            || !string.Equals(sourceHash, targetHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Publication-edition row counts or ownership mappings changed during migration.");
        }
    }

    private static async Task<MigrationCounts> ReadSourceCountsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var profiles = await ScalarAsync(connection, "SELECT COUNT(*) FROM PublishProfiles;", cancellationToken);
        var selections = await ScalarAsync(connection, "SELECT COUNT(*) FROM PublishOutlineSelections;", cancellationToken);
        var placements = await ScalarAsync(connection, "SELECT COUNT(*) FROM PublishImagePlacements;", cancellationToken);
        var profileless = await ScalarAsync(
            connection,
            """
            SELECT COUNT(*) FROM Projects p
            WHERE NOT EXISTS (SELECT 1 FROM PublishProfiles profile WHERE profile.ProjectId = p.Id)
              AND (
                EXISTS (SELECT 1 FROM PublishOutlineSelections item WHERE item.ProjectId = p.Id)
                OR EXISTS (SELECT 1 FROM PublishImagePlacements placement WHERE placement.ProjectId = p.Id)
              );
            """,
            cancellationToken);
        return new(profiles, selections, placements, profiles + profileless);
    }

    private static async Task<MigrationCounts> ReadTargetCountsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var releases = await ScalarAsync(connection, "SELECT COUNT(*) FROM PublicationEditions;", cancellationToken);
        return new(
            releases,
            await ScalarAsync(connection, "SELECT COUNT(*) FROM PublicationEditionOutlineItems;", cancellationToken),
            await ScalarAsync(connection, "SELECT COUNT(*) FROM PublicationImagePlacements;", cancellationToken),
            releases);
    }

    private static async Task<string> ReadShapeHashAsync(
        SqliteConnection connection,
        bool legacy,
        CancellationToken cancellationToken)
    {
        var sql = legacy
            ? """
              SELECT value FROM (
                SELECT 'E|' || p.ProjectId || '|' || p.Id AS value FROM PublishProfiles p
                UNION ALL
                SELECT 'E|' || p.Id || '|' || p.Id FROM Projects p
                WHERE NOT EXISTS (SELECT 1 FROM PublishProfiles profile WHERE profile.ProjectId = p.Id)
                  AND (EXISTS (SELECT 1 FROM PublishOutlineSelections s WHERE s.ProjectId = p.Id)
                    OR EXISTS (SELECT 1 FROM PublishImagePlacements i WHERE i.ProjectId = p.Id))
                UNION ALL
                SELECT 'S|' || s.Id || '|' || COALESCE(p.Id, s.ProjectId)
                FROM PublishOutlineSelections s LEFT JOIN PublishProfiles p ON p.ProjectId = s.ProjectId
                UNION ALL
                SELECT 'I|' || i.Id || '|' || COALESCE(p.Id, i.ProjectId)
                FROM PublishImagePlacements i LEFT JOIN PublishProfiles p ON p.ProjectId = i.ProjectId
              ) ORDER BY value;
              """
            : """
              SELECT value FROM (
                SELECT 'E|' || ProjectId || '|' || Id AS value FROM PublicationEditions
                UNION ALL SELECT 'S|' || Id || '|' || EditionId FROM PublicationEditionOutlineItems
                UNION ALL SELECT 'I|' || Id || '|' || EditionId FROM PublicationImagePlacements
              ) ORDER BY value;
              """;
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var canonical = new StringBuilder();
        while (await reader.ReadAsync(cancellationToken))
            canonical.AppendLine(reader.GetString(0));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static async Task<int> ScalarAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task EnsureHealthyAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        var result = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"SQLite quick_check failed: {result}");
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string name,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private sealed record MigrationCounts(
        int Profiles,
        int Selections,
        int Placements,
        int ExpectedEditions)
    {
        public int Editions => Profiles;
    }

    private sealed record MigrationMarker(
        MigrationCounts SourceCounts,
        string SourceHash,
        string BackupPath);
}
