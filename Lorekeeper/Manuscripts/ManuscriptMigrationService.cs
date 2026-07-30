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

namespace Lorekeeper.Manuscripts;

public interface IManuscriptMigrationService
{
    Task ApplyPendingAsync(AppDbContext db, CancellationToken cancellationToken = default);
    Task<ManuscriptMigrationState> GetStateAsync(CancellationToken cancellationToken = default);
    Task<ManuscriptRestoreRequest> PrepareRestoreAsync(string backupPath, CancellationToken cancellationToken = default);
    Task RestoreAsync(string backupPath, string confirmationToken, CancellationToken cancellationToken = default);
}

public sealed record ManuscriptMigrationState(
    bool MigrationRequired,
    bool RecoveryRequired,
    IReadOnlyList<ManuscriptMigrationJournal> Journals,
    IReadOnlyList<ManuscriptBackupInfo> Backups);

public sealed record ManuscriptBackupInfo(string Path, long SizeBytes, DateTime CreatedAtUtc);

public sealed record ManuscriptRestoreRequest(string BackupPath, string ConfirmationToken, DateTime ExpiresAtUtc);

public sealed class ManuscriptMigrationService(
    IConfiguration configuration,
    ILogger<ManuscriptMigrationService> logger) : IManuscriptMigrationService
{
    public const string MigrationName = "structured-manuscript-v1";
    public const string SchemaV2MigrationName = "semantic-manuscript-v2";
    public const string SchemaV2EfMigrationId = "20260730180725_SemanticManuscriptV2";
    private const int MaxAutomaticBackups = 5;
    private static readonly TimeSpan RestoreTokenLifetime = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, (string Path, DateTime ExpiresAt)> _restoreTokens = [];
    private readonly object _restoreTokenLock = new();
    private readonly string _connectionString = SqliteConnectionSettings.BuildConnectionString(configuration);

    public async Task ApplyPendingAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        await using var migrationLock = await AcquireExclusiveLockAsync(cancellationToken);
        if (await ApplyScheduledRestoreAsync(cancellationToken))
            db.ChangeTracker.Clear();
        var needsDataMigration = await HasColumnAsync("Chapters", "Body", cancellationToken);
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        var hasCurrentColumns = await HasColumnAsync("Chapters", "ManuscriptJson", cancellationToken);
        var hasInterruptedTransform = hasCurrentColumns
            && await ContainsUnstructuredManuscriptsAsync(cancellationToken);
        var needsSchemaV2Upgrade = hasCurrentColumns
            && await ContainsSchemaV1ManuscriptsAsync(cancellationToken);
        if (!needsDataMigration
            && pending.Count == 0
            && !hasInterruptedTransform
            && !needsSchemaV2Upgrade)
            return;

        string? backupPath = null;
        var activeMigrationName = MigrationName;
        var activeSourceVersion = 7;
        var activeTargetVersion = 8;
        try
        {
            if (await DatabaseHasUserSchemaAsync(cancellationToken))
            {
                await EnsureHealthyAsync(_connectionString, cancellationToken);
                backupPath = await CreateBackupAsync("pre-manuscript", cancellationToken);
            }

            await ClearStrandedMigrationLockAsync(db, cancellationToken);
            await MigrateManuscriptSchemaAsync(db, cancellationToken);

            if (await ContainsUnstructuredManuscriptsAsync(cancellationToken))
            {
                var journal = new ManuscriptMigrationJournal
                {
                    MigrationName = MigrationName,
                    SourceSchemaVersion = 7,
                    TargetSchemaVersion = 8,
                    Phase = ManuscriptMigrationPhase.Transform,
                    Status = ManuscriptMigrationStatus.Running,
                    BackupPath = backupPath ?? string.Empty,
                };
                db.ManuscriptMigrationJournals.Add(journal);
                await db.SaveChangesAsync(cancellationToken);
                _ = await TransformAsync(journal.Id, cancellationToken);
            }

            if (await ContainsSchemaV1ManuscriptsAsync(cancellationToken))
            {
                activeMigrationName = SchemaV2MigrationName;
                activeSourceVersion = 1;
                activeTargetVersion = 2;
                await UpgradeSchemaV1Async(backupPath ?? string.Empty, cancellationToken);
            }

            await EnsureHealthyAsync(_connectionString, cancellationToken);
            PruneAutomaticBackups();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Structured manuscript migration failed.");
            if (backupPath is not null)
            {
                await RestoreDatabaseFileAsync(backupPath, createDiagnosticBackup: false, cancellationToken);
                await CreateRecoveryShellAsync(
                    db,
                    backupPath,
                    activeMigrationName,
                    activeSourceVersion,
                    activeTargetVersion,
                    exception,
                    cancellationToken);
                logger.LogWarning(
                    "Preserved the original database at {BackupPath} and started a recovery shell.",
                    backupPath);
                return;
            }
            throw new InvalidOperationException(
                "Structured manuscript migration failed before a backup could be created.",
                exception);
        }
    }

    private async Task CreateRecoveryShellAsync(
        AppDbContext db,
        string backupPath,
        string migrationName,
        int sourceVersion,
        int targetVersion,
        Exception exception,
        CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await ClearStrandedMigrationLockAsync(db, cancellationToken);
        await MigrateManuscriptSchemaAsync(db, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM Projects;", cancellationToken);
        db.ChangeTracker.Clear();
        db.ManuscriptMigrationJournals.Add(new ManuscriptMigrationJournal
        {
            MigrationName = migrationName,
            SourceSchemaVersion = sourceVersion,
            TargetSchemaVersion = targetVersion,
            Phase = ManuscriptMigrationPhase.Validate,
            Status = ManuscriptMigrationStatus.Failed,
            BackupPath = backupPath,
            ValidationReportJson = JsonSerializer.Serialize(new
            {
                recoveryMode = true,
                message = "The original database is protected in the referenced backup.",
            }),
            ErrorDetail = $"{exception.GetType().Name}: {exception.Message}",
            CompletedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);
        await EnsureHealthyAsync(_connectionString, cancellationToken);
    }

    private static Task MigrateManuscriptSchemaAsync(
        AppDbContext db,
        CancellationToken cancellationToken) =>
        db.Database.GetService<IMigrator>().MigrateAsync(SchemaV2EfMigrationId, cancellationToken);

    public async Task<ManuscriptMigrationState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var journals = new List<ManuscriptMigrationJournal>();
        if (await TableExistsAsync("ManuscriptMigrationJournals", cancellationToken))
        {
            await using var connection = await OpenAsync(_connectionString, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT Id, MigrationName, SourceSchemaVersion, TargetSchemaVersion, Phase, Status,
                       BackupPath, ChapterCount, ContestBatchCount, ContestCandidateCount,
                       RevisionSessionCount, SourceHash, TargetHash, ValidationReportJson,
                       ErrorDetail, StartedAt, CompletedAt
                FROM ManuscriptMigrationJournals
                ORDER BY StartedAt DESC;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                journals.Add(new ManuscriptMigrationJournal
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    MigrationName = reader.GetString(1),
                    SourceSchemaVersion = reader.GetInt32(2),
                    TargetSchemaVersion = reader.GetInt32(3),
                    Phase = Enum.Parse<ManuscriptMigrationPhase>(reader.GetString(4)),
                    Status = Enum.Parse<ManuscriptMigrationStatus>(reader.GetString(5)),
                    BackupPath = reader.GetString(6),
                    ChapterCount = reader.GetInt32(7),
                    ContestBatchCount = reader.GetInt32(8),
                    ContestCandidateCount = reader.GetInt32(9),
                    RevisionSessionCount = reader.GetInt32(10),
                    SourceHash = reader.GetString(11),
                    TargetHash = reader.GetString(12),
                    ValidationReportJson = reader.GetString(13),
                    ErrorDetail = reader.IsDBNull(14) ? null : reader.GetString(14),
                    StartedAt = reader.GetDateTime(15),
                    CompletedAt = reader.IsDBNull(16) ? null : reader.GetDateTime(16),
                });
            }
        }

        return new ManuscriptMigrationState(
            await HasColumnAsync("Chapters", "Body", cancellationToken),
            File.Exists(RestoreMarkerPath())
                || journals.FirstOrDefault() is { Status: ManuscriptMigrationStatus.Failed },
            journals,
            ListBackups());
    }

    public Task<ManuscriptRestoreRequest> PrepareRestoreAsync(
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = ValidateBackupPath(backupPath);
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        var expires = DateTime.UtcNow.Add(RestoreTokenLifetime);
        lock (_restoreTokenLock)
            _restoreTokens[token] = (resolved, expires);
        return Task.FromResult(new ManuscriptRestoreRequest(resolved, token, expires));
    }

    public async Task RestoreAsync(
        string backupPath,
        string confirmationToken,
        CancellationToken cancellationToken = default)
    {
        var resolved = ValidateBackupPath(backupPath);
        lock (_restoreTokenLock)
        {
            if (!_restoreTokens.Remove(confirmationToken, out var request)
                || request.ExpiresAt < DateTime.UtcNow
                || !string.Equals(request.Path, resolved, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The restore confirmation is invalid or expired.");
            }
        }

        await using var migrationLock = await AcquireExclusiveLockAsync(cancellationToken);
        await EnsureHealthyAsync($"Data Source={resolved}", cancellationToken);
        var markerPath = RestoreMarkerPath();
        var temporaryPath = markerPath + ".tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(new ScheduledRestore(resolved)),
            cancellationToken);
        RestrictFile(temporaryPath);
        File.Move(temporaryPath, markerPath, overwrite: true);
        RestrictFile(markerPath);
    }

    private async Task<bool> ApplyScheduledRestoreAsync(CancellationToken cancellationToken)
    {
        var markerPath = RestoreMarkerPath();
        if (!File.Exists(markerPath))
            return false;

        ScheduledRestore request;
        try
        {
            request = JsonSerializer.Deserialize<ScheduledRestore>(
                await File.ReadAllTextAsync(markerPath, cancellationToken))
                ?? throw new InvalidDataException("The scheduled restore marker is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The scheduled restore marker is malformed.", exception);
        }

        var backupPath = ValidateBackupPath(request.BackupPath);
        await EnsureHealthyAsync($"Data Source={backupPath}", cancellationToken);
        await RestoreDatabaseFileAsync(backupPath, createDiagnosticBackup: true, cancellationToken);
        File.Delete(markerPath);
        return true;
    }

    private async Task<FileStream> AcquireExclusiveLockAsync(CancellationToken cancellationToken)
    {
        var directory = BackupDirectory();
        Directory.CreateDirectory(directory);
        RestrictDirectory(directory);
        var lockPath = Path.Combine(directory, $"{MigrationName}.lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
                RestrictFile(lockPath);
                return stream;
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
        }
    }

    private async Task<ManuscriptMigrationReport> TransformAsync(
        Guid journalId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(_connectionString, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var sourceHashes = new List<string>();
        var targetHashes = new List<string>();
        var chapterCount = await TransformChaptersAsync(connection, transaction, sourceHashes, targetHashes, cancellationToken);
        var contestBatchCount = await TransformTextColumnAsync(
            connection, transaction, "ContestBatches", "Id", "ChapterId",
            ["OriginalManuscriptJson", "AcceptedManuscriptJson"], sourceHashes, targetHashes, cancellationToken);
        var contestCandidateCount = await TransformContestCandidatesAsync(
            connection, transaction, sourceHashes, targetHashes, cancellationToken);
        var revisionSessionCount = await TransformRevisionSessionsAsync(
            connection, transaction, sourceHashes, targetHashes, cancellationToken);
        var aiChangeCount = await TransformAiChangesAsync(
            connection, transaction, sourceHashes, targetHashes, cancellationToken);
        await TerminalizeLegacyActiveWorkflowsAsync(connection, transaction, cancellationToken);
        var report = new ManuscriptMigrationReport(
            chapterCount,
            contestBatchCount,
            contestCandidateCount,
            revisionSessionCount,
            aiChangeCount,
            AggregateHash(sourceHashes),
            AggregateHash(targetHashes),
            DateTime.UtcNow);
        if (!string.Equals(report.SourceHash, report.TargetHash, StringComparison.Ordinal))
            throw new InvalidDataException("Structured manuscript migration hash validation failed.");

        await using (var updateJournal = connection.CreateCommand())
        {
            updateJournal.Transaction = transaction;
            updateJournal.CommandText =
                """
                UPDATE ManuscriptMigrationJournals
                SET Phase = 'Complete', Status = 'Completed', ChapterCount = $chapters,
                    ContestBatchCount = $batches, ContestCandidateCount = $candidates,
                    RevisionSessionCount = $sessions, SourceHash = $sourceHash,
                    TargetHash = $targetHash, ValidationReportJson = $report,
                    CompletedAt = $completedAt
                WHERE Id COLLATE NOCASE = $id;
                """;
            updateJournal.Parameters.AddWithValue("$chapters", report.ChapterCount);
            updateJournal.Parameters.AddWithValue("$batches", report.ContestBatchCount);
            updateJournal.Parameters.AddWithValue("$candidates", report.ContestCandidateCount);
            updateJournal.Parameters.AddWithValue("$sessions", report.RevisionSessionCount);
            updateJournal.Parameters.AddWithValue("$sourceHash", report.SourceHash);
            updateJournal.Parameters.AddWithValue("$targetHash", report.TargetHash);
            updateJournal.Parameters.AddWithValue("$report", JsonSerializer.Serialize(report));
            updateJournal.Parameters.AddWithValue("$completedAt", report.ValidatedAtUtc);
            updateJournal.Parameters.AddWithValue("$id", journalId.ToString());
            if (await updateJournal.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidDataException("Structured manuscript migration journal could not be finalized.");
        }

        await transaction.CommitAsync(cancellationToken);
        return report;
    }

    private static async Task<int> TransformChaptersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        List<string> sourceHashes,
        List<string> targetHashes,
        CancellationToken cancellationToken)
    {
        var rows = new List<(Guid Id, long Revision, string Body, string Page, string Illustrations)>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText =
                "SELECT Id, ManuscriptRevision, ManuscriptJson, PageLayoutJson, IllustrationLayoutJson FROM Chapters;";
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((
                    Guid.Parse(reader.GetString(0)),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4)));
            }
        }

        foreach (var row in rows)
        {
            if (IsManuscript(row.Body, row.Id, row.Revision))
                continue;
            var manuscript = ManuscriptCodec.FromPlainText(row.Id, row.Body, revision: 1, deterministicIds: true);
            var sourceHash = ManuscriptCodec.HashPlainText(ManuscriptCodec.CanonicalizePlainText(row.Body));
            var targetHash = ManuscriptCodec.HashPlainText(ManuscriptCodec.ProjectPlainText(manuscript));
            if (!string.Equals(sourceHash, targetHash, StringComparison.Ordinal))
                throw new InvalidDataException($"Chapter {row.Id:N} changed during manuscript conversion.");

            var pageJson = MigrateLegacyPicturePage(row.Id, row.Body, row.Page, manuscript);
            var illustrationJson = MigrateLegacyIllustrations(row.Id, row.Body, row.Illustrations, manuscript);
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE Chapters
                SET ManuscriptJson = $manuscript, ManuscriptRevision = 1,
                    PageLayoutJson = $page, IllustrationLayoutJson = $illustrations
                WHERE Id COLLATE NOCASE = $id;
                """;
            update.Parameters.AddWithValue("$manuscript", ManuscriptCodec.Serialize(manuscript));
            update.Parameters.AddWithValue("$page", pageJson);
            update.Parameters.AddWithValue("$illustrations", illustrationJson);
            update.Parameters.AddWithValue("$id", row.Id.ToString());
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidDataException($"Chapter {row.Id:N} could not be updated uniquely.");
            sourceHashes.Add(sourceHash);
            targetHashes.Add(targetHash);
        }

        return rows.Count;
    }

    private static async Task<int> TransformTextColumnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string idColumn,
        string ownerColumn,
        IReadOnlyList<string> valueColumns,
        List<string> sourceHashes,
        List<string> targetHashes,
        CancellationToken cancellationToken)
    {
        var columns = string.Join(", ", new[] { idColumn, ownerColumn }.Concat(valueColumns));
        var rows = new List<(Guid Id, Guid OwnerId, string[] Values)>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = $"SELECT {columns} FROM {table};";
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var values = valueColumns.Select((_, index) => reader.GetString(index + 2)).ToArray();
                rows.Add((Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), values));
            }
        }

        foreach (var row in rows)
        {
            var assignments = new List<string>();
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            for (var index = 0; index < valueColumns.Count; index++)
            {
                if (IsManuscript(row.Values[index], row.OwnerId))
                    continue;
                var manuscript = ManuscriptCodec.FromPlainText(
                    row.OwnerId,
                    row.Values[index],
                    revision: 1,
                    deterministicIds: true);
                assignments.Add($"{valueColumns[index]} = $value{index}");
                update.Parameters.AddWithValue($"$value{index}", ManuscriptCodec.Serialize(manuscript));
                sourceHashes.Add(ManuscriptCodec.HashPlainText(
                    ManuscriptCodec.CanonicalizePlainText(row.Values[index])));
                targetHashes.Add(ManuscriptCodec.HashPlainText(ManuscriptCodec.ProjectPlainText(manuscript)));
            }
            if (assignments.Count == 0)
                continue;
            update.CommandText =
                $"UPDATE {table} SET {string.Join(", ", assignments)} WHERE {idColumn} COLLATE NOCASE = $id;";
            update.Parameters.AddWithValue("$id", row.Id.ToString());
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidDataException($"{table} row {row.Id:N} could not be updated uniquely.");
        }

        return rows.Count;
    }

    private static async Task<int> TransformContestCandidatesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        List<string> sourceHashes,
        List<string> targetHashes,
        CancellationToken cancellationToken)
    {
        var rows = new List<(Guid Id, Guid ChapterId, string Value)>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText =
                """
                SELECT c.Id, b.ChapterId, c.ProposedManuscriptJson
                FROM ContestCandidates c
                INNER JOIN ContestBatches b ON b.Id = c.BatchId;
                """;
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                rows.Add((Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2)));
        }

        foreach (var row in rows.Where(row => !IsManuscript(row.Value, row.ChapterId)))
        {
            var manuscript = ManuscriptCodec.FromPlainText(row.ChapterId, row.Value, revision: 1, deterministicIds: true);
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                "UPDATE ContestCandidates SET ProposedManuscriptJson = $value WHERE Id COLLATE NOCASE = $id;";
            update.Parameters.AddWithValue("$value", ManuscriptCodec.Serialize(manuscript));
            update.Parameters.AddWithValue("$id", row.Id.ToString());
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidDataException($"Contest candidate {row.Id:N} could not be updated uniquely.");
            sourceHashes.Add(ManuscriptCodec.HashPlainText(
                ManuscriptCodec.CanonicalizePlainText(row.Value)));
            targetHashes.Add(ManuscriptCodec.HashPlainText(ManuscriptCodec.ProjectPlainText(manuscript)));
        }

        return rows.Count;
    }

    private static async Task<int> TransformRevisionSessionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        List<string> sourceHashes,
        List<string> targetHashes,
        CancellationToken cancellationToken)
    {
        var rows = new List<LegacyRevisionSessionRow>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText =
                """
                SELECT Id, ChapterId, Status, OriginalManuscriptJson, OperationFormat,
                       OperationsJson, ProposalJson
                FROM EditorRevisionSessions;
                """;
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new LegacyRevisionSessionRow(
                    Guid.Parse(reader.GetString(0)),
                    Guid.Parse(reader.GetString(1)),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6)));
            }
        }

        foreach (var row in rows)
        {
            var originalJson = row.OriginalManuscriptJson;
            if (!IsManuscript(originalJson, row.ChapterId))
            {
                var manuscript = ManuscriptCodec.FromPlainText(
                    row.ChapterId,
                    originalJson,
                    revision: 1,
                    deterministicIds: true);
                sourceHashes.Add(ManuscriptCodec.HashPlainText(
                    ManuscriptCodec.CanonicalizePlainText(originalJson)));
                targetHashes.Add(ManuscriptCodec.HashPlainText(ManuscriptCodec.ProjectPlainText(manuscript)));
                originalJson = ManuscriptCodec.Serialize(manuscript);
            }

            JsonElement legacyOperation;
            JsonElement legacyProposal;
            try
            {
                legacyOperation = JsonDocument.Parse(row.OperationsJson).RootElement.Clone();
                legacyProposal = JsonDocument.Parse(row.ProposalJson).RootElement.Clone();
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    $"Revision session {row.Id:N} contains malformed legacy operation JSON.",
                    exception);
            }

            var isActive = row.Status is "Queued" or "Running";
            const string operationFormat = "legacy_line_edit_audit";
            var operationsJson = JsonSerializer.Serialize(new
            {
                format = "legacy-revision-session-v7",
                operation = legacyOperation,
            });
            var proposalJson = JsonSerializer.Serialize(new
            {
                format = "legacy-revision-session-v7",
                activeAtMigration = isActive,
                operation = legacyOperation,
                proposal = legacyProposal,
            });

            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE EditorRevisionSessions
                SET OriginalManuscriptJson = $original, Status = $status,
                    OperationFormat = $format, OperationsJson = $operations,
                    ProposalJson = $proposal,
                    ErrorMessage = CASE WHEN $wasActive = 1 THEN $error ELSE ErrorMessage END,
                    CompletedAt = CASE WHEN $wasActive = 1 THEN $completedAt ELSE CompletedAt END
                WHERE Id COLLATE NOCASE = $id;
                """;
            update.Parameters.AddWithValue("$original", originalJson);
            update.Parameters.AddWithValue("$status", isActive ? "Failed" : row.Status);
            update.Parameters.AddWithValue("$format", operationFormat);
            update.Parameters.AddWithValue("$operations", operationsJson);
            update.Parameters.AddWithValue("$proposal", proposalJson);
            update.Parameters.AddWithValue(
                "$error",
                "This legacy revision session was stopped by the structured-manuscript migration and cannot be resumed.");
            update.Parameters.AddWithValue("$wasActive", isActive ? 1 : 0);
            update.Parameters.AddWithValue("$completedAt", DateTime.UtcNow);
            update.Parameters.AddWithValue("$id", row.Id.ToString());
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidDataException($"Revision session {row.Id:N} could not be updated uniquely.");
        }

        return rows.Count;
    }

    private static async Task TerminalizeLegacyActiveWorkflowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var completedAt = DateTime.UtcNow;
        await using (var candidates = connection.CreateCommand())
        {
            candidates.Transaction = transaction;
            candidates.CommandText =
                """
                UPDATE ContestCandidates
                SET Status = 'Failed',
                    ErrorMessage = 'This legacy contest candidate was stopped by the structured-manuscript migration and cannot be resumed.',
                    UpdatedAt = $completedAt,
                    CompletedAt = $completedAt
                WHERE Status IN ('Pending', 'Running');
                """;
            candidates.Parameters.AddWithValue("$completedAt", completedAt);
            await candidates.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var batches = connection.CreateCommand())
        {
            batches.Transaction = transaction;
            batches.CommandText =
                """
                UPDATE ContestBatches
                SET Status = 'Failed',
                    ErrorMessage = 'This legacy contest was stopped by the structured-manuscript migration and cannot be resumed.',
                    UpdatedAt = $completedAt,
                    CompletedAt = $completedAt
                WHERE Status = 'Running';
                """;
            batches.Parameters.AddWithValue("$completedAt", completedAt);
            await batches.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var jobs = connection.CreateCommand();
        jobs.Transaction = transaction;
        jobs.CommandText =
            """
            UPDATE EditorRevisionJobs
            SET Status = 'Failed',
                ErrorMessage = 'This legacy revision job was stopped by the structured-manuscript migration and cannot be resumed.',
                UpdatedAt = $completedAt,
                CompletedAt = $completedAt
            WHERE Status IN ('Queued', 'Running')
              AND EXISTS (
                  SELECT 1
                  FROM EditorRevisionSessions sessions
                  WHERE sessions.JobId = EditorRevisionJobs.Id
                    AND sessions.Status = 'Failed'
                    AND sessions.OperationFormat = 'legacy_line_edit_audit');
            """;
        jobs.Parameters.AddWithValue("$completedAt", completedAt);
        await jobs.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> TransformAiChangesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        List<string> sourceHashes,
        List<string> targetHashes,
        CancellationToken cancellationToken)
    {
        var rows = new List<LegacyAiChangeRow>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText =
                """
                SELECT Id, Status, ResourceKind, ToolName, ArgumentsJson, BeforeJson, AfterJson,
                       DraftAfterJson, ReviewStateJson, ResultJson
                FROM AiChanges
                WHERE ResourceKind = 'ChapterBody'
                   OR ToolName IN ('edit_chapter', 'edit_assigned_chapter');
                """;
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new LegacyAiChangeRow(
                    Guid.Parse(reader.GetString(0)),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.GetString(9)));
            }
        }

        foreach (var row in rows)
        {
            var isPending = string.Equals(row.Status, "Pending", StringComparison.OrdinalIgnoreCase);
            var before = ReadLegacyChapterChange(row.BeforeJson, row.Id, "before");
            var after = ReadLegacyChapterChange(row.AfterJson, row.Id, "after");
            sourceHashes.Add(ManuscriptCodec.HashPlainText(
                ManuscriptCodec.CanonicalizePlainText(before.Body)));
            sourceHashes.Add(ManuscriptCodec.HashPlainText(
                ManuscriptCodec.CanonicalizePlainText(after.Body)));

            string resourceKind;
            string toolName;
            string argumentsJson;
            string beforeJson;
            string afterJson;
            string? draftAfterJson;
            string? reviewStateJson;
            string resultJson;
            if (isPending)
            {
                var beforeDocument = ManuscriptCodec.FromPlainText(
                    before.Id, before.Body, revision: 1, deterministicIds: true);
                var afterDocument = ManuscriptCodec.ReparsePreservingBlockIds(beforeDocument, after.Body);
                resourceKind = "ChapterManuscript";
                toolName = "apply_manuscript_operations";
                argumentsJson = JsonSerializer.Serialize(new
                {
                    migratedFrom = row.ToolName,
                    expectedRevision = beforeDocument.Revision,
                    legacyArguments = JsonDocument.Parse(row.ArgumentsJson).RootElement.Clone(),
                });
                beforeJson = SerializeChange(before, beforeDocument);
                afterJson = SerializeChange(after, afterDocument);
                draftAfterJson = string.IsNullOrWhiteSpace(row.DraftAfterJson)
                    ? null
                    : SerializeDraftChange(row.DraftAfterJson, afterDocument, row.Id);
                reviewStateJson = row.ReviewStateJson;
                resultJson = row.ResultJson;
                targetHashes.Add(ManuscriptCodec.HashPlainText(ManuscriptCodec.ProjectPlainText(beforeDocument)));
                targetHashes.Add(ManuscriptCodec.HashPlainText(ManuscriptCodec.ProjectPlainText(afterDocument)));
            }
            else
            {
                resourceKind = "LegacyChapterBodyAudit";
                toolName = "legacy_chapter_body_audit";
                argumentsJson = LegacySnapshot("arguments", row.ArgumentsJson);
                beforeJson = LegacySnapshot("before", row.BeforeJson);
                afterJson = LegacySnapshot("after", row.AfterJson);
                draftAfterJson = string.IsNullOrWhiteSpace(row.DraftAfterJson)
                    ? null
                    : LegacySnapshot("draftAfter", row.DraftAfterJson);
                reviewStateJson = string.IsNullOrWhiteSpace(row.ReviewStateJson)
                    ? null
                    : LegacySnapshot("reviewState", row.ReviewStateJson);
                resultJson = LegacySnapshot("result", row.ResultJson);
                targetHashes.Add(ManuscriptCodec.HashPlainText(
                    ManuscriptCodec.CanonicalizePlainText(before.Body)));
                targetHashes.Add(ManuscriptCodec.HashPlainText(
                    ManuscriptCodec.CanonicalizePlainText(after.Body)));
            }

            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE AiChanges
                SET ResourceKind = $kind, ToolName = $toolName, ArgumentsJson = $arguments, BeforeJson = $before,
                    AfterJson = $after, DraftAfterJson = $draft, ReviewStateJson = $review,
                    ResultJson = $result
                WHERE Id COLLATE NOCASE = $id;
                """;
            update.Parameters.AddWithValue("$kind", resourceKind);
            update.Parameters.AddWithValue("$toolName", toolName);
            update.Parameters.AddWithValue("$arguments", argumentsJson);
            update.Parameters.AddWithValue("$before", beforeJson);
            update.Parameters.AddWithValue("$after", afterJson);
            update.Parameters.AddWithValue("$draft", (object?)draftAfterJson ?? DBNull.Value);
            update.Parameters.AddWithValue("$review", (object?)reviewStateJson ?? DBNull.Value);
            update.Parameters.AddWithValue("$result", resultJson);
            update.Parameters.AddWithValue("$id", row.Id.ToString());
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidDataException($"AI change {row.Id:N} could not be updated uniquely.");
        }

        return rows.Count;
    }

    private static LegacyChapterChange ReadLegacyChapterChange(string json, Guid changeId, string field)
    {
        try
        {
            return JsonSerializer.Deserialize<LegacyChapterChange>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException($"AI change {changeId:N} has a null {field} chapter body.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"AI change {changeId:N} has malformed {field} chapter-body JSON.", exception);
        }
    }

    private static string SerializeChange(LegacyChapterChange change, ManuscriptDocument document) =>
        JsonSerializer.Serialize(new
        {
            change.Id,
            change.Title,
            document.Revision,
            ManuscriptJson = ManuscriptCodec.Serialize(document),
        });

    private static string SerializeDraftChange(
        string json,
        ManuscriptDocument proposed,
        Guid changeId)
    {
        var draft = ReadLegacyChapterChange(json, changeId, "draft");
        var document = ManuscriptCodec.ReparsePreservingBlockIds(proposed, draft.Body);
        return SerializeChange(draft, document);
    }

    private static string LegacySnapshot(string field, string json) =>
        JsonSerializer.Serialize(new
        {
            format = "legacy-chapter-body-v7",
            field,
            payload = JsonDocument.Parse(json).RootElement.Clone(),
        });

    internal static string MigrateLegacyPicturePage(
        Guid chapterId,
        string body,
        string json,
        ManuscriptDocument manuscript)
    {
        if (string.IsNullOrWhiteSpace(json))
            return string.Empty;
        var legacy = JsonSerializer.Deserialize<LegacyPicturePageLayout>(json, ManuscriptCodec.JsonOptions)
            ?? throw new InvalidDataException($"Chapter {chapterId:N} has a null Picture Page layout.");
        var projected = string.Join(
            "\n\n",
            legacy.TextElements.OrderBy(element => element.ReadingOrder)
                .Select(element => element.Text.Trim())
                .Where(text => !string.IsNullOrWhiteSpace(text)));
        if (!string.Equals(
            ManuscriptCodec.CanonicalizePlainText(projected),
            ManuscriptCodec.CanonicalizePlainText(body),
            StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Chapter {chapterId:N} Picture Page text does not match its chapter body.");
        }

        var current = new PicturePageLayout(
            legacy.Images,
            legacy.TextElements.Select(element => element.ToCurrent()).ToList());
        return JsonSerializer.Serialize(
            ChapterVisuals.ChapterTextLayoutSynchronizer.AttachReferences(current, manuscript),
            ManuscriptCodec.JsonOptions);
    }

    internal static string MigrateLegacyIllustrations(
        Guid chapterId,
        string body,
        string json,
        ManuscriptDocument manuscript)
    {
        if (string.IsNullOrWhiteSpace(json))
            return string.Empty;
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("images", out var existing)
            && existing.ValueKind == JsonValueKind.Array
            && existing.EnumerateArray().All(image => image.TryGetProperty("blockId", out _)))
        {
            return json;
        }

        var legacy = JsonSerializer.Deserialize<LegacyIllustratedProseLayout>(json, ManuscriptCodec.JsonOptions)
            ?? throw new InvalidDataException($"Chapter {chapterId:N} has a null illustration layout.");
        var paragraphs = LegacyParagraphs(body);
        if (paragraphs.Count != manuscript.Content.Count)
            throw new InvalidDataException($"Chapter {chapterId:N} illustration paragraphs could not be mapped unambiguously.");
        var migrated = legacy.Images.Select(image =>
        {
            if (image.ParagraphIndex < 0 || image.ParagraphIndex >= paragraphs.Count)
                throw new InvalidDataException($"Chapter {chapterId:N} illustration {image.Id:N} has an invalid paragraph index.");
            if (!string.Equals(image.ParagraphHash, LegacyParagraphHash(paragraphs[image.ParagraphIndex]), StringComparison.Ordinal))
                throw new InvalidDataException($"Chapter {chapterId:N} illustration {image.Id:N} failed paragraph hash validation.");
            return new IllustratedProseImageBlock(
                image.Id,
                image.ImageId,
                image.AnchorPosition,
                manuscript.Content[image.ParagraphIndex].Id,
                image.WidthPercent,
                image.Alignment,
                image.Caption,
                image.AltTextOverride,
                image.SortOrder,
                image.StartOnNewPage)
            {
                ParagraphIndex = image.ParagraphIndex,
            };
        }).ToList();
        return JsonSerializer.Serialize(new IllustratedProseLayout(migrated), ManuscriptCodec.JsonOptions);
    }

    private async Task<string> CreateBackupAsync(string purpose, CancellationToken cancellationToken)
    {
        var directory = BackupDirectory();
        Directory.CreateDirectory(directory);
        RestrictDirectory(directory);
        var path = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{purpose}.db");
        await using var source = await OpenAsync(_connectionString, cancellationToken);
        await using var destination = await OpenAsync($"Data Source={path}", cancellationToken);
        source.BackupDatabase(destination);
        RestrictFile(path);
        await EnsureHealthyAsync($"Data Source={path}", cancellationToken);
        return path;
    }

    private async Task RestoreDatabaseFileAsync(
        string backupPath,
        bool createDiagnosticBackup,
        CancellationToken cancellationToken)
    {
        if (createDiagnosticBackup && File.Exists(DatabasePath()))
            await CreateBackupAsync("pre-restore-diagnostic", cancellationToken);
        SqliteConnection.ClearAllPools();
        await using var source = await OpenAsync($"Data Source={backupPath};Mode=ReadOnly", cancellationToken);
        await using var destination = await OpenAsync(_connectionString, cancellationToken);
        source.BackupDatabase(destination);
        await EnsureHealthyAsync(_connectionString, cancellationToken);
    }

    private async Task<bool> ContainsUnstructuredManuscriptsAsync(CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync("Chapters", cancellationToken))
            return false;
        await using var connection = await OpenAsync(_connectionString, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT CASE WHEN
                EXISTS (
                    SELECT 1 FROM Chapters
                    WHERE CASE WHEN json_valid(ManuscriptJson) = 1
                        THEN COALESCE(json_extract(ManuscriptJson, '$.schemaVersion'), 0)
                        ELSE 0 END NOT IN (1, 2)
                       OR lower(COALESCE(json_extract(ManuscriptJson, '$.manuscriptId'), '')) != lower(Id)
                       OR COALESCE(json_extract(ManuscriptJson, '$.revision'), -1) != ManuscriptRevision)
                OR EXISTS (
                    SELECT 1 FROM ContestBatches
                    WHERE CASE WHEN json_valid(OriginalManuscriptJson) = 1
                            THEN COALESCE(json_extract(OriginalManuscriptJson, '$.schemaVersion'), 0)
                            ELSE 0 END NOT IN (1, 2)
                       OR CASE WHEN json_valid(AcceptedManuscriptJson) = 1
                            THEN COALESCE(json_extract(AcceptedManuscriptJson, '$.schemaVersion'), 0)
                            ELSE 0 END NOT IN (1, 2))
                OR EXISTS (
                    SELECT 1 FROM ContestCandidates
                    WHERE CASE WHEN json_valid(ProposedManuscriptJson) = 1
                        THEN COALESCE(json_extract(ProposedManuscriptJson, '$.schemaVersion'), 0)
                        ELSE 0 END NOT IN (1, 2))
                OR EXISTS (
                    SELECT 1 FROM EditorRevisionSessions
                    WHERE CASE WHEN json_valid(OriginalManuscriptJson) = 1
                        THEN COALESCE(json_extract(OriginalManuscriptJson, '$.schemaVersion'), 0)
                        ELSE 0 END NOT IN (1, 2))
            THEN 1 ELSE 0 END;
            """;
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1)
            return true;

        await using var aiChanges = connection.CreateCommand();
        aiChanges.CommandText =
            """
            SELECT BeforeJson, AfterJson
            FROM AiChanges
            WHERE ResourceKind = 'ChapterBody'
               OR ToolName IN ('edit_chapter', 'edit_assigned_chapter');
            """;
        await using var reader = await aiChanges.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!ManuscriptSchemaUpgrade.ContainsStructuredDocument(reader.GetString(0))
                || !ManuscriptSchemaUpgrade.ContainsStructuredDocument(reader.GetString(1)))
            {
                return true;
            }
        }
        return false;
    }

    private async Task<bool> ContainsSchemaV1ManuscriptsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(_connectionString, cancellationToken);
        foreach (var spec in SchemaUpgradeColumns)
        {
            if (!await TableExistsAsync(_connectionString, spec.Table, cancellationToken))
                continue;
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {string.Join(", ", spec.Columns)} FROM {spec.Table};";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                for (var index = 0; index < spec.Columns.Count; index++)
                {
                    if (!reader.IsDBNull(index)
                        && ManuscriptSchemaUpgrade.ContainsV1Document(reader.GetString(index)))
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    private async Task UpgradeSchemaV1Async(
        string backupPath,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(_connectionString, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var journalId = Guid.NewGuid();
        var startedAt = DateTime.UtcNow;
        await using (var insertJournal = connection.CreateCommand())
        {
            insertJournal.Transaction = transaction;
            insertJournal.CommandText =
                """
                INSERT INTO ManuscriptMigrationJournals (
                    Id, MigrationName, SourceSchemaVersion, TargetSchemaVersion, Phase, Status,
                    BackupPath, ChapterCount, ContestBatchCount, ContestCandidateCount,
                    RevisionSessionCount, SourceHash, TargetHash, ValidationReportJson,
                    ErrorDetail, StartedAt, CompletedAt)
                VALUES (
                    $id, $name, 1, 2, 'Transform', 'Running',
                    $backup, 0, 0, 0, 0, '', '', '{}', NULL, $startedAt, NULL);
                """;
            insertJournal.Parameters.AddWithValue("$id", journalId.ToString());
            insertJournal.Parameters.AddWithValue("$name", SchemaV2MigrationName);
            insertJournal.Parameters.AddWithValue("$backup", backupPath);
            insertJournal.Parameters.AddWithValue("$startedAt", startedAt);
            if (await insertJournal.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidDataException("The manuscript-v2 migration journal could not be created.");
        }

        var sourceHashes = new List<string>();
        var targetHashes = new List<string>();
        var upgradedByTable = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var spec in SchemaUpgradeColumns)
        {
            if (!await TableExistsAsync(_connectionString, spec.Table, cancellationToken))
                continue;
            var rows = new List<(string Id, string?[] Values)>();
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = $"SELECT {spec.IdColumn}, {string.Join(", ", spec.Columns)} FROM {spec.Table};";
                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var values = new string?[spec.Columns.Count];
                    for (var index = 0; index < spec.Columns.Count; index++)
                        values[index] = reader.IsDBNull(index + 1) ? null : reader.GetString(index + 1);
                    rows.Add((reader.GetString(0), values));
                }
            }

            var tableCount = 0;
            foreach (var row in rows)
            {
                var assignments = new List<string>();
                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                for (var index = 0; index < row.Values.Length; index++)
                {
                    var value = row.Values[index];
                    if (value is null || !ManuscriptSchemaUpgrade.ContainsV1Document(value))
                        continue;
                    var result = ManuscriptSchemaUpgrade.UpgradeEmbeddedV1Documents(value);
                    if (result.Count == 0)
                        continue;
                    assignments.Add($"{spec.Columns[index]} = $value{index}");
                    update.Parameters.AddWithValue($"$value{index}", result.Json);
                    sourceHashes.AddRange(result.SourceHashes);
                    targetHashes.AddRange(result.TargetHashes);
                    tableCount += result.Count;
                }
                if (assignments.Count == 0)
                    continue;
                update.CommandText =
                    $"UPDATE {spec.Table} SET {string.Join(", ", assignments)} "
                    + $"WHERE {spec.IdColumn} COLLATE NOCASE = $id;";
                update.Parameters.AddWithValue("$id", row.Id);
                if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                    throw new InvalidDataException($"{spec.Table} row {row.Id} could not be upgraded uniquely.");
            }
            upgradedByTable[spec.Table] = tableCount;
        }
        var materializedStyleCount = await MaterializeLegacyStylesAsync(
            connection,
            transaction,
            cancellationToken);

        var sourceHash = AggregateHash(sourceHashes);
        var targetHash = AggregateHash(targetHashes);
        if (!string.Equals(sourceHash, targetHash, StringComparison.Ordinal))
            throw new InvalidDataException("Manuscript-v2 migration hash validation failed.");
        if (sourceHashes.Count == 0)
            throw new InvalidDataException("Manuscript-v2 migration found no upgradeable documents.");

        var completedAt = DateTime.UtcNow;
        var report = JsonSerializer.Serialize(new
        {
            sourceSchemaVersion = 1,
            targetSchemaVersion = 2,
            documentCount = sourceHashes.Count,
            upgradedByTable,
            materializedStyleCount,
            sourceHash,
            targetHash,
            validatedAtUtc = completedAt,
        });
        await using (var complete = connection.CreateCommand())
        {
            complete.Transaction = transaction;
            complete.CommandText =
                """
                UPDATE ManuscriptMigrationJournals
                SET Phase = 'Complete', Status = 'Completed',
                    ChapterCount = $chapters, ContestBatchCount = $batches,
                    ContestCandidateCount = $candidates, RevisionSessionCount = $sessions,
                    SourceHash = $sourceHash, TargetHash = $targetHash,
                    ValidationReportJson = $report, CompletedAt = $completedAt
                WHERE Id COLLATE NOCASE = $id;
                """;
            complete.Parameters.AddWithValue("$chapters", upgradedByTable.GetValueOrDefault("Chapters"));
            complete.Parameters.AddWithValue("$batches", upgradedByTable.GetValueOrDefault("ContestBatches"));
            complete.Parameters.AddWithValue("$candidates", upgradedByTable.GetValueOrDefault("ContestCandidates"));
            complete.Parameters.AddWithValue("$sessions", upgradedByTable.GetValueOrDefault("EditorRevisionSessions"));
            complete.Parameters.AddWithValue("$sourceHash", sourceHash);
            complete.Parameters.AddWithValue("$targetHash", targetHash);
            complete.Parameters.AddWithValue("$report", report);
            complete.Parameters.AddWithValue("$completedAt", completedAt);
            complete.Parameters.AddWithValue("$id", journalId.ToString());
            if (await complete.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidDataException("The manuscript-v2 migration journal could not be finalized.");
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<int> MaterializeLegacyStylesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var rolesByProject = new Dictionary<
            string,
            Dictionary<ManuscriptStyleKind, HashSet<string>>>(
                StringComparer.OrdinalIgnoreCase);
        await using (var selectPayloads = connection.CreateCommand())
        {
            selectPayloads.Transaction = transaction;
            selectPayloads.CommandText =
                """
                SELECT ProjectId, ManuscriptJson FROM Chapters
                UNION ALL SELECT ProjectId, OriginalManuscriptJson FROM ContestBatches
                UNION ALL SELECT ProjectId, AcceptedManuscriptJson FROM ContestBatches
                UNION ALL
                    SELECT batch.ProjectId, candidate.ProposedManuscriptJson
                    FROM ContestCandidates candidate
                    JOIN ContestBatches batch ON batch.Id = candidate.BatchId
                UNION ALL
                    SELECT batch.ProjectId, candidate.ReviewStateJson
                    FROM ContestCandidates candidate
                    JOIN ContestBatches batch ON batch.Id = candidate.BatchId
                UNION ALL
                    SELECT job.ProjectId, session.OriginalManuscriptJson
                    FROM EditorRevisionSessions session
                    JOIN EditorRevisionJobs job ON job.Id = session.JobId
                UNION ALL
                    SELECT job.ProjectId, session.OperationsJson
                    FROM EditorRevisionSessions session
                    JOIN EditorRevisionJobs job ON job.Id = session.JobId
                UNION ALL
                    SELECT job.ProjectId, session.ProposalJson
                    FROM EditorRevisionSessions session
                    JOIN EditorRevisionJobs job ON job.Id = session.JobId
                UNION ALL
                    SELECT batch.ProjectId, change.ArgumentsJson
                    FROM AiChanges change
                    JOIN AiChangeBatches batch ON batch.Id = change.BatchId
                UNION ALL
                    SELECT batch.ProjectId, change.BeforeJson
                    FROM AiChanges change
                    JOIN AiChangeBatches batch ON batch.Id = change.BatchId
                UNION ALL
                    SELECT batch.ProjectId, change.AfterJson
                    FROM AiChanges change
                    JOIN AiChangeBatches batch ON batch.Id = change.BatchId
                UNION ALL
                    SELECT batch.ProjectId, change.DraftAfterJson
                    FROM AiChanges change
                    JOIN AiChangeBatches batch ON batch.Id = change.BatchId
                UNION ALL
                    SELECT batch.ProjectId, change.ReviewStateJson
                    FROM AiChanges change
                    JOIN AiChangeBatches batch ON batch.Id = change.BatchId
                UNION ALL
                    SELECT batch.ProjectId, change.ResultJson
                    FROM AiChanges change
                    JOIN AiChangeBatches batch ON batch.Id = change.BatchId;
                """;
            await using var reader = await selectPayloads.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.IsDBNull(1))
                    continue;
                var projectId = reader.GetString(0);
                if (!rolesByProject.TryGetValue(projectId, out var rolesByKind))
                {
                    rolesByKind = new Dictionary<ManuscriptStyleKind, HashSet<string>>
                    {
                        [ManuscriptStyleKind.Paragraph] = new(StringComparer.OrdinalIgnoreCase),
                        [ManuscriptStyleKind.Character] = new(StringComparer.OrdinalIgnoreCase),
                    };
                    rolesByProject[projectId] = rolesByKind;
                }
                foreach (var manuscript in ManuscriptSchemaUpgrade.ExtractCurrentDocuments(
                    reader.GetString(1)))
                {
                    foreach (var role in manuscript.Content
                        .Select(block => block.StyleRole)
                        .Where(role => !ManuscriptStyleService.BuiltInParagraphRoles.Contains(role)))
                    {
                        rolesByKind[ManuscriptStyleKind.Paragraph].Add(role);
                    }
                    foreach (var role in manuscript.Content
                        .SelectMany(block => block.Content)
                        .SelectMany(inline => inline.Marks)
                        .Where(mark => mark.Type == ManuscriptMarkType.CharacterStyle)
                        .Select(mark => mark.Value!))
                    {
                        rolesByKind[ManuscriptStyleKind.Character].Add(role);
                    }
                }
            }
        }

        var created = 0;
        foreach (var (projectId, rolesByKind) in rolesByProject)
        {
            var projectGuid = Guid.Parse(projectId);
            foreach (var (kind, roles) in rolesByKind)
            {
                var existingRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                await using (var selectStyles = connection.CreateCommand())
                {
                    selectStyles.Transaction = transaction;
                    selectStyles.CommandText =
                        """
                        SELECT Name, SemanticRole
                        FROM ManuscriptStyleDefinitions
                        WHERE ProjectId = $projectId AND Kind = $kind;
                        """;
                    selectStyles.Parameters.AddWithValue("$projectId", projectId);
                    selectStyles.Parameters.AddWithValue("$kind", kind.ToString());
                    await using var reader = await selectStyles.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        existingNames.Add(reader.GetString(0));
                        existingRoles.Add(reader.GetString(1));
                    }
                }

                foreach (var role in roles.Order(StringComparer.OrdinalIgnoreCase))
                {
                    if (existingRoles.Contains(role))
                        continue;
                    var name = AllocateMigratedStyleName($"Imported {role}", existingNames);

                    var now = DateTime.UtcNow;
                    await using var insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText =
                        """
                        INSERT INTO ManuscriptStyleDefinitions (
                            Id, ProjectId, Name, NameKey, Kind, SemanticRole, SemanticRoleKey,
                            DefinitionJson, Revision, CreatedAt, UpdatedAt)
                        VALUES (
                            $id, $projectId, $name, $nameKey, $kind, $role, $roleKey,
                            $definition, 1, $now, $now);
                        """;
                    insert.Parameters.AddWithValue(
                        "$id",
                        DeterministicStyleId(projectGuid, kind, role).ToString().ToUpperInvariant());
                    insert.Parameters.AddWithValue("$projectId", projectId);
                    insert.Parameters.AddWithValue("$name", name);
                    insert.Parameters.AddWithValue("$nameKey", name.ToLowerInvariant());
                    insert.Parameters.AddWithValue("$kind", kind.ToString());
                    insert.Parameters.AddWithValue("$role", role);
                    insert.Parameters.AddWithValue("$roleKey", role.ToLowerInvariant());
                    insert.Parameters.AddWithValue(
                        "$definition",
                        JsonSerializer.Serialize(new ManuscriptStyleProperties(), ManuscriptCodec.JsonOptions));
                    insert.Parameters.AddWithValue("$now", now);
                    if (await insert.ExecuteNonQueryAsync(cancellationToken) != 1)
                        throw new InvalidDataException($"Legacy manuscript style '{role}' could not be materialized.");
                    existingRoles.Add(role);
                    created++;
                }
            }
        }
        return created;
    }

    private static string AllocateMigratedStyleName(string requestedName, ISet<string> usedNames)
    {
        var direct = requestedName[..Math.Min(requestedName.Length, 80)];
        if (usedNames.Add(direct))
            return direct;
        var number = 2;
        while (true)
        {
            var suffix = $" {number++}";
            var candidate =
                $"{requestedName[..Math.Min(requestedName.Length, 80 - suffix.Length)]}{suffix}";
            if (usedNames.Add(candidate))
                return candidate;
        }
    }

    private static Guid DeterministicStyleId(
        Guid projectId,
        ManuscriptStyleKind kind,
        string semanticRole)
    {
        var bytes = SHA256.HashData(
            Encoding.UTF8.GetBytes(
                $"lorekeeper-semantic-style-v2:{projectId:N}:{kind}:{semanticRole}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private async Task<bool> DatabaseHasUserSchemaAsync(CancellationToken cancellationToken) =>
        await TableExistsAsync("Chapters", cancellationToken);

    private Task<bool> HasColumnAsync(string table, string column, CancellationToken cancellationToken) =>
        HasColumnAsync(_connectionString, table, column, cancellationToken);

    private static async Task<bool> HasColumnAsync(
        string connectionString,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(connectionString, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\");";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private Task<bool> TableExistsAsync(string table, CancellationToken cancellationToken) =>
        TableExistsAsync(_connectionString, table, cancellationToken);

    private static async Task<bool> TableExistsAsync(
        string connectionString,
        string table,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(connectionString, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static async Task EnsureHealthyAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(connectionString, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        var result = (string?)await command.ExecuteScalarAsync(cancellationToken);
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"SQLite quick_check failed: {result}");
    }

    private static async Task ClearStrandedMigrationLockAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM \"__EFMigrationsLock\";", cancellationToken);
        }
        catch
        {
            // The table does not exist on a fresh database.
        }
    }

    private static async Task<SqliteConnection> OpenAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private string DatabasePath() =>
        Path.GetFullPath(new SqliteConnectionStringBuilder(_connectionString).DataSource);

    private string BackupDirectory() =>
        Path.Combine(Path.GetDirectoryName(DatabasePath())!, ".migration-backups", "manuscripts");

    private string RestoreMarkerPath() =>
        Path.Combine(BackupDirectory(), "scheduled-restore.json");

    private string ValidateBackupPath(string path)
    {
        var resolved = Path.GetFullPath(path);
        var root = Path.GetFullPath(BackupDirectory()) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(resolved))
            throw new InvalidOperationException("The requested file is not a protected Lorekeeper migration backup.");
        return resolved;
    }

    private IReadOnlyList<ManuscriptBackupInfo> ListBackups()
    {
        var directory = BackupDirectory();
        if (!Directory.Exists(directory))
            return [];
        return Directory.EnumerateFiles(directory, "*.db", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.CreationTimeUtc)
            .Select(file => new ManuscriptBackupInfo(file.FullName, file.Length, file.CreationTimeUtc))
            .ToList();
    }

    private void PruneAutomaticBackups()
    {
        var automatic = ListBackups()
            .Where(backup => !Path.GetFileName(backup.Path).Contains("diagnostic", StringComparison.OrdinalIgnoreCase))
            .Skip(MaxAutomaticBackups)
            .ToList();
        foreach (var backup in automatic)
            File.Delete(backup.Path);
    }

    private static void RestrictDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var owner = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("The current Windows account has no security identifier.");
            var security = new DirectorySecurity();
            security.SetOwner(owner);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                owner,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(path), security);
            return;
        }
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void RestrictFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var owner = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("The current Windows account has no security identifier.");
            var security = new FileSecurity();
            security.SetOwner(owner);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                owner,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            FileSystemAclExtensions.SetAccessControl(new FileInfo(path), security);
            return;
        }
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static bool IsManuscript(
        string value,
        Guid expectedManuscriptId,
        long? expectedRevision = null)
    {
        try
        {
            return ManuscriptSchemaUpgrade.IsStructuredManuscript(
                value,
                expectedManuscriptId,
                expectedRevision);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException(
                $"A structured manuscript does not match its owning chapter {expectedManuscriptId:N}.",
                exception);
        }
    }

    private static IReadOnlyList<string> LegacyParagraphs(string body) =>
        ManuscriptCodec.FromPlainText(Guid.Empty, body).Content
            .Select(ManuscriptCodec.Text)
            .ToList();

    private static string LegacyParagraphHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.Trim())))[..16].ToLowerInvariant();

    private static string AggregateHash(IEnumerable<string> hashes)
    {
        var joined = string.Join("\n", hashes);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
    }

    private sealed record LegacyPicturePageLayout(
        IReadOnlyList<PicturePageImageElement> Images,
        IReadOnlyList<LegacyPicturePageTextElement> TextElements);

    private sealed record LegacyPicturePageTextElement(
        Guid Id,
        string Text,
        double XPercent,
        double YPercent,
        double WidthPercent,
        double HeightPercent,
        int ZIndex,
        int ReadingOrder,
        string FontFamilyKey,
        int FontWeight,
        bool Italic,
        double FontSizePoints,
        double LetterSpacingEm,
        double LineHeight,
        string Color,
        string BackgroundColor,
        double BackgroundOpacity,
        PicturePageTextAlign TextAlign,
        ChapterTextVerticalAlign VerticalAlign,
        PicturePageTextShadow Shadow,
        PicturePageTextRole Role = PicturePageTextRole.Body)
    {
        public PicturePageTextElement ToCurrent() =>
            new(
                Id, Text, XPercent, YPercent, WidthPercent, HeightPercent, ZIndex, ReadingOrder,
                FontFamilyKey, FontWeight, Italic, FontSizePoints, LetterSpacingEm, LineHeight,
                Color, BackgroundColor, BackgroundOpacity, TextAlign, VerticalAlign, Shadow, Role);
    }

    private sealed record LegacyIllustratedProseLayout(IReadOnlyList<LegacyIllustratedProseImageBlock> Images);

    private sealed record LegacyIllustratedProseImageBlock(
        Guid Id,
        Guid ImageId,
        ChapterImageAnchorPosition AnchorPosition,
        int ParagraphIndex,
        string ParagraphHash,
        double WidthPercent,
        ChapterImageAlignment Alignment,
        string Caption,
        string AltTextOverride,
        int SortOrder,
        bool StartOnNewPage);

    private sealed record LegacyAiChangeRow(
        Guid Id,
        string Status,
        string ResourceKind,
        string ToolName,
        string ArgumentsJson,
        string BeforeJson,
        string AfterJson,
        string? DraftAfterJson,
        string? ReviewStateJson,
        string ResultJson);

    private sealed record LegacyRevisionSessionRow(
        Guid Id,
        Guid ChapterId,
        string Status,
        string OriginalManuscriptJson,
        string OperationFormat,
        string OperationsJson,
        string ProposalJson);

    private sealed record LegacyChapterChange(Guid Id, string Title, string Body);

    private sealed record ScheduledRestore(string BackupPath);

    private sealed record SchemaUpgradeColumnSet(
        string Table,
        string IdColumn,
        IReadOnlyList<string> Columns);

    private static readonly IReadOnlyList<SchemaUpgradeColumnSet> SchemaUpgradeColumns =
    [
        new("Chapters", "Id", ["ManuscriptJson"]),
        new("ContestBatches", "Id", ["OriginalManuscriptJson", "AcceptedManuscriptJson"]),
        new("ContestCandidates", "Id", ["ProposedManuscriptJson", "ReviewStateJson"]),
        new("EditorRevisionSessions", "Id", ["OriginalManuscriptJson", "OperationsJson", "ProposalJson"]),
        new(
            "AiChanges",
            "Id",
            [
                "ArgumentsJson",
                "BeforeJson",
                "AfterJson",
                "DraftAfterJson",
                "ReviewStateJson",
                "ResultJson",
            ]),
    ];

    private sealed record ManuscriptMigrationReport(
        int ChapterCount,
        int ContestBatchCount,
        int ContestCandidateCount,
        int RevisionSessionCount,
        int AiChangeCount,
        string SourceHash,
        string TargetHash,
        DateTime ValidatedAtUtc);
}
