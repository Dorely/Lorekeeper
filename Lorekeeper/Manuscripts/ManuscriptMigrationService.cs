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
    IDatabaseMigrationRecoveryService recovery,
    ILogger<ManuscriptMigrationService> logger) : IManuscriptMigrationService
{
    public const string MigrationName = "structured-manuscript-v1";
    public const string SchemaV2MigrationName = "semantic-manuscript-v2";
    public const string SchemaV3MigrationName = "semantic-manuscript-v3";
    public const string SchemaV6MigrationName = "semantic-manuscript-v6";
    public const string SchemaV2EfMigrationId = "20260730180725_SemanticManuscriptV2";
    private const int MaxAutomaticBackups = 5;
    private readonly string _connectionString = SqliteConnectionSettings.BuildConnectionString(configuration);

    public async Task ApplyPendingAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        await using var migrationLock = await AcquireExclusiveLockAsync(cancellationToken);
        var needsDataMigration = await HasColumnAsync("Chapters", "Body", cancellationToken);
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        var manuscriptSchemaPending = pending.Contains(SchemaV2EfMigrationId, StringComparer.Ordinal);
        var hasCurrentColumns = await HasColumnAsync("Chapters", "ManuscriptJson", cancellationToken);
        var hasNonconformingManuscripts = hasCurrentColumns
            && await ContainsUnstructuredManuscriptsAsync(cancellationToken);
        var canResumeInterruptedTransform = hasNonconformingManuscripts
            && await IsResumableInterruptedTransformAsync(db, cancellationToken);
        var hasInvalidCurrentManuscripts = hasNonconformingManuscripts
            && !canResumeInterruptedTransform;
        var needsSchemaV2Upgrade = hasCurrentColumns
            && await ContainsSchemaV1ManuscriptsAsync(cancellationToken);
        var includesSchemaV5 = needsSchemaV2Upgrade
            && await ContainsSchemaVersionAsync(5, cancellationToken);
        var includesPreV5Schema = needsSchemaV2Upgrade
            && (await ContainsSchemaVersionAsync(1, cancellationToken)
                || await ContainsSchemaVersionAsync(2, cancellationToken)
                || await ContainsSchemaVersionAsync(3, cancellationToken)
                || await ContainsSchemaVersionAsync(4, cancellationToken));
        if (!needsDataMigration
            && !manuscriptSchemaPending
            && !canResumeInterruptedTransform
            && !hasInvalidCurrentManuscripts
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
                backupPath = await recovery.CreateBackupAsync(
                    "manuscripts",
                    "pre-manuscript",
                    cancellationToken);
            }

            if (hasInvalidCurrentManuscripts)
            {
                activeMigrationName = SchemaV3MigrationName;
                activeSourceVersion = ManuscriptDocument.CurrentSchemaVersion;
                activeTargetVersion = ManuscriptDocument.CurrentSchemaVersion;
                throw new InvalidDataException(
                    "The database contains malformed current manuscript data. "
                    + "Lorekeeper will not reinterpret it as legacy prose.");
            }

            if (needsDataMigration || manuscriptSchemaPending)
            {
                await ClearStrandedMigrationLockAsync(db, cancellationToken);
                await MigrateManuscriptSchemaAsync(db, cancellationToken);
            }

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
                var isV5OnlyUpgrade = includesSchemaV5 && !includesPreV5Schema;
                activeMigrationName = isV5OnlyUpgrade ? SchemaV6MigrationName : SchemaV3MigrationName;
                activeSourceVersion = isV5OnlyUpgrade ? 5 : 1;
                activeTargetVersion = ManuscriptDocument.CurrentSchemaVersion;
                await UpgradeSchemaV1Async(
                    backupPath ?? string.Empty,
                    activeMigrationName,
                    activeSourceVersion,
                    cancellationToken);
            }

            await EnsureHealthyAsync(_connectionString, cancellationToken);
            var protectedBackupPaths = await db.ManuscriptMigrationJournals.AsNoTracking()
                .Where(journal => journal.Status != ManuscriptMigrationStatus.Completed
                    && journal.BackupPath != string.Empty)
                .Select(journal => journal.BackupPath)
                .ToListAsync(cancellationToken);
            await recovery.PruneAutomaticBackupsAsync(
                "manuscripts",
                MaxAutomaticBackups,
                protectedBackupPaths,
                cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Structured manuscript migration failed.");
            if (backupPath is not null)
            {
                await recovery.EnterRecoveryModeAsync(
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
            await recovery.IsRecoveryRequiredAsync(cancellationToken)
                || journals.FirstOrDefault() is { Status: ManuscriptMigrationStatus.Failed },
            journals,
            await recovery.ListBackupsAsync(cancellationToken));
    }

    public Task<ManuscriptRestoreRequest> PrepareRestoreAsync(
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        return recovery.PrepareRestoreAsync(backupPath, cancellationToken);
    }

    public async Task RestoreAsync(
        string backupPath,
        string confirmationToken,
        CancellationToken cancellationToken = default)
    {
        await recovery.ScheduleRestoreAsync(backupPath, confirmationToken, cancellationToken);
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
        var chapterReport = await TransformChaptersAsync(
            connection,
            transaction,
            sourceHashes,
            targetHashes,
            cancellationToken);
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
            chapterReport.ChapterCount,
            chapterReport.StaleIllustrationAnchorHashCount,
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

    private static async Task<ChapterMigrationReport> TransformChaptersAsync(
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

        var staleIllustrationAnchorHashCount = 0;
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
            var illustrationJson = MigrateLegacyIllustrations(
                row.Id,
                row.Body,
                row.Illustrations,
                manuscript,
                out var staleAnchorHashCount);
            staleIllustrationAnchorHashCount += staleAnchorHashCount;
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

        return new ChapterMigrationReport(rows.Count, staleIllustrationAnchorHashCount);
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

        // Failed/invalid candidates can legitimately have no generated
        // manuscript. Preserve that empty audit value rather than turning it
        // into an empty manuscript merely because another legacy payload
        // caused this transform to run.
        foreach (var row in rows.Where(row =>
                     !string.IsNullOrWhiteSpace(row.Value)
                     && !IsManuscript(row.Value, row.ChapterId)))
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
                resultJson = LegacySnapshot("result", row.ResultJson, allowPlainText: true);
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

    private static string LegacySnapshot(
        string field,
        string value,
        bool allowPlainText = false) =>
        JsonSerializer.Serialize(new
        {
            format = "legacy-chapter-body-v7",
            field,
            payload = ReadLegacySnapshotPayload(value, allowPlainText),
        });

    private static JsonElement ReadLegacySnapshotPayload(
        string value,
        bool allowPlainText)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.Clone();
        }
        catch (JsonException) when (allowPlainText)
        {
            return JsonSerializer.SerializeToElement(value);
        }
    }

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
            AttachLegacyPageReferences(current, manuscript),
            ManuscriptCodec.JsonOptions);
    }

    private static PicturePageLayout AttachLegacyPageReferences(
        PicturePageLayout layout,
        ManuscriptDocument manuscript)
    {
        var blockCursor = 0;
        var mapped = new List<PicturePageTextElement>(layout.TextElements.Count);
        foreach (var element in layout.TextElements.OrderBy(element => element.ReadingOrder))
        {
            var blockCount = string.IsNullOrWhiteSpace(ManuscriptCodec.NormalizePlainText(element.Text))
                ? 0
                : ManuscriptCodec.FromPlainText(Guid.Empty, element.Text).Content.Count;
            var references = manuscript.Content.Skip(blockCursor).Take(blockCount)
                .Select(block => new ManuscriptRangeReference(block.Id)).ToList();
            if (references.Count != blockCount)
                throw new InvalidDataException($"Designed page text element {element.Id:N} could not be mapped to manuscript blocks.");
            blockCursor += blockCount;
            mapped.Add(element with { ContentReferences = references });
        }
        if (blockCursor != manuscript.Content.Count)
            throw new InvalidDataException("Designed page text references do not cover the full manuscript.");
        return layout with { TextElements = mapped };
    }

    internal static string MigrateLegacyIllustrations(
        Guid chapterId,
        string body,
        string json,
        ManuscriptDocument manuscript,
        out int staleAnchorHashCount)
    {
        staleAnchorHashCount = 0;
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
        var migrated = new List<IllustratedProseImageBlock>(legacy.Images.Count);
        foreach (var image in legacy.Images)
        {
            if (image.ParagraphIndex < 0 || image.ParagraphIndex >= paragraphs.Count)
                throw new InvalidDataException($"Chapter {chapterId:N} illustration {image.Id:N} has an invalid paragraph index.");
            if (!IsLegacyParagraphHash(image.ParagraphHash))
                throw new InvalidDataException($"Chapter {chapterId:N} illustration {image.Id:N} has an invalid paragraph hash.");
            if (!string.Equals(
                image.ParagraphHash,
                LegacyParagraphHash(paragraphs[image.ParagraphIndex]),
                StringComparison.Ordinal))
            {
                // The legacy runtime rendered, moved, and exported illustrations by
                // ParagraphIndex. Its normalizer filled only an empty hash, so an
                // ordinary later body edit could leave this drift sentinel stale.
                // Preserve the exact location that the legacy runtime displayed.
                staleAnchorHashCount++;
            }
            migrated.Add(new IllustratedProseImageBlock(
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
            });
        }
        return JsonSerializer.Serialize(new IllustratedProseLayout(migrated), ManuscriptCodec.JsonOptions);
    }

    private async Task<bool> ContainsUnstructuredManuscriptsAsync(CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync("Chapters", cancellationToken))
            return false;

        var checks = new List<string>
        {
            """
            EXISTS (
                SELECT 1 FROM Chapters
                WHERE CASE WHEN json_valid(ManuscriptJson) = 1
                    THEN COALESCE(json_extract(ManuscriptJson, '$.schemaVersion'), 0)
                    ELSE 0 END NOT IN (1, 2, 3, 4, 5, 6)
                   OR COALESCE(json_extract(ManuscriptJson, '$.manuscriptId'), '') COLLATE NOCASE != Id COLLATE NOCASE
                   OR COALESCE(json_extract(ManuscriptJson, '$.revision'), -1) != ManuscriptRevision)
            """
        };

        if (await HasColumnAsync("ContestBatches", "OriginalManuscriptJson", cancellationToken))
        {
            checks.Add("""
                EXISTS (
                    SELECT 1 FROM ContestBatches
                    WHERE CASE WHEN json_valid(OriginalManuscriptJson) = 1
                            THEN COALESCE(json_extract(OriginalManuscriptJson, '$.schemaVersion'), 0)
                            ELSE 0 END NOT IN (1, 2, 3, 4, 5, 6)
                       )
                """);
        }

        // AcceptedManuscriptJson was removed at the current review-workflow
        // boundary. Keep its historical check independent so the current
        // ContestBatches schema still validates OriginalManuscriptJson.
        if (await HasColumnAsync("ContestBatches", "AcceptedManuscriptJson", cancellationToken))
        {
            checks.Add("""
                EXISTS (
                    SELECT 1 FROM ContestBatches
                    WHERE NULLIF(trim(AcceptedManuscriptJson), '') IS NOT NULL
                      AND CASE WHEN json_valid(AcceptedManuscriptJson) = 1
                        THEN COALESCE(json_extract(AcceptedManuscriptJson, '$.schemaVersion'), 0)
                        ELSE 0 END NOT IN (1, 2, 3, 4, 5, 6))
                """);
        }

        if (await HasColumnAsync("ContestCandidates", "ProposedManuscriptJson", cancellationToken))
        {
            checks.Add("""
                EXISTS (
                    SELECT 1 FROM ContestCandidates
                    WHERE NULLIF(trim(ProposedManuscriptJson), '') IS NOT NULL
                      AND CASE WHEN json_valid(ProposedManuscriptJson) = 1
                        THEN COALESCE(json_extract(ProposedManuscriptJson, '$.schemaVersion'), 0)
                        ELSE 0 END NOT IN (1, 2, 3, 4, 5, 6))
                """);
            checks.Add("""
                EXISTS (
                    SELECT 1 FROM ContestCandidates
                    WHERE lower(Status) IN ('completed', 'selected')
                      AND NULLIF(trim(ProposedManuscriptJson), '') IS NULL)
                """);
        }

        if (await HasColumnAsync("ContestCandidates", "DraftManuscriptJson", cancellationToken))
        {
            checks.Add("""
                EXISTS (
                    SELECT 1 FROM ContestCandidates
                    WHERE NULLIF(trim(DraftManuscriptJson), '') IS NOT NULL
                      AND CASE WHEN json_valid(DraftManuscriptJson) = 1
                        THEN COALESCE(json_extract(DraftManuscriptJson, '$.schemaVersion'), 0)
                        ELSE 0 END NOT IN (1, 2, 3, 4, 5, 6))
                """);
        }

        if (await HasColumnAsync("EditorRevisionSessions", "OriginalManuscriptJson", cancellationToken))
        {
            checks.Add("""
                EXISTS (
                    SELECT 1 FROM EditorRevisionSessions
                    WHERE CASE WHEN json_valid(OriginalManuscriptJson) = 1
                        THEN COALESCE(json_extract(OriginalManuscriptJson, '$.schemaVersion'), 0)
                        ELSE 0 END NOT IN (1, 2, 3, 4, 5, 6))
                """);
        }

        await using var connection = await OpenAsync(_connectionString, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT CASE WHEN {string.Join(" OR ", checks)} THEN 1 ELSE 0 END;";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1)
            return true;

        if (!await TableExistsAsync("AiChanges", cancellationToken)
            || !await HasColumnAsync("AiChanges", "BeforeJson", cancellationToken)
            || !await HasColumnAsync("AiChanges", "AfterJson", cancellationToken))
            return false;

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

    private static async Task<bool> IsResumableInterruptedTransformAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var completedStructuredMigration = await db.ManuscriptMigrationJournals.AsNoTracking()
            .AnyAsync(
                journal => journal.Status == ManuscriptMigrationStatus.Completed
                    && (journal.MigrationName == MigrationName
                        || journal.MigrationName == SchemaV2MigrationName
                        || journal.MigrationName == SchemaV3MigrationName
                        || journal.MigrationName == SchemaV6MigrationName),
                cancellationToken);
        if (completedStructuredMigration)
            return false;

        var appliedMigrations = await db.Database.GetAppliedMigrationsAsync(cancellationToken);
        return !appliedMigrations.Any(
            migration => string.CompareOrdinal(migration, SchemaV2EfMigrationId) > 0);
    }

    private async Task<bool> ContainsSchemaV1ManuscriptsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(_connectionString, cancellationToken);
        foreach (var spec in SchemaUpgradeColumns)
        {
            if (!await TableExistsAsync(_connectionString, spec.Table, cancellationToken))
                continue;
            var columns = await ExistingColumnsAsync(connection, spec.Table, spec.Columns, cancellationToken);
            if (columns.Count == 0)
                continue;
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {string.Join(", ", columns)} FROM {spec.Table};";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                for (var index = 0; index < columns.Count; index++)
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

    private async Task<bool> ContainsSchemaVersionAsync(
        int version,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(_connectionString, cancellationToken);
        foreach (var spec in SchemaUpgradeColumns)
        {
            if (!await TableExistsAsync(_connectionString, spec.Table, cancellationToken))
                continue;
            var columns = await ExistingColumnsAsync(connection, spec.Table, spec.Columns, cancellationToken);
            if (columns.Count == 0)
                continue;
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {string.Join(", ", columns)} FROM {spec.Table};";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                for (var index = 0; index < columns.Count; index++)
                {
                    if (!reader.IsDBNull(index)
                        && ManuscriptSchemaUpgrade.ContainsDocumentVersion(reader.GetString(index), version))
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
        string migrationName,
        int sourceSchemaVersion,
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
                    $id, $name, $sourceVersion, $targetVersion, 'Transform', 'Running',
                    $backup, 0, 0, 0, 0, '', '', '{}', NULL, $startedAt, NULL);
                """;
            insertJournal.Parameters.AddWithValue("$id", journalId.ToString());
            insertJournal.Parameters.AddWithValue("$name", migrationName);
            insertJournal.Parameters.AddWithValue("$sourceVersion", sourceSchemaVersion);
            insertJournal.Parameters.AddWithValue("$targetVersion", ManuscriptDocument.CurrentSchemaVersion);
            insertJournal.Parameters.AddWithValue("$backup", backupPath);
            insertJournal.Parameters.AddWithValue("$startedAt", startedAt);
            if (await insertJournal.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidDataException("The manuscript schema migration journal could not be created.");
        }

        var sourceHashes = new List<string>();
        var targetHashes = new List<string>();
        var upgradedByTable = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var spec in SchemaUpgradeColumns)
        {
            if (!await TableExistsAsync(_connectionString, spec.Table, cancellationToken))
                continue;
            var columns = await ExistingColumnsAsync(connection, spec.Table, spec.Columns, cancellationToken);
            if (columns.Count == 0)
                continue;
            var rows = new List<(string Id, string?[] Values)>();
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = $"SELECT {spec.IdColumn}, {string.Join(", ", columns)} FROM {spec.Table};";
                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var values = new string?[columns.Count];
                    for (var index = 0; index < columns.Count; index++)
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
                    assignments.Add($"{columns[index]} = $value{index}");
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
            throw new InvalidDataException("Manuscript schema migration hash validation failed.");
        if (sourceHashes.Count == 0)
            throw new InvalidDataException("Manuscript schema migration found no upgradeable documents.");

        var completedAt = DateTime.UtcNow;
        var report = JsonSerializer.Serialize(new
        {
            sourceSchemaVersion,
            targetSchemaVersion = ManuscriptDocument.CurrentSchemaVersion,
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
                throw new InvalidDataException("The manuscript schema migration journal could not be finalized.");
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
        var payloadQueries = new List<string>();
        await AddDirectPayloadQueryAsync(
            connection,
            transaction,
            payloadQueries,
            "Chapters",
            "ProjectId",
            "ManuscriptJson",
            0,
            cancellationToken);
        await AddDirectPayloadQueryAsync(
            connection,
            transaction,
            payloadQueries,
            "ContestBatches",
            "ProjectId",
            "OriginalManuscriptJson",
            0,
            cancellationToken);
        await AddDirectPayloadQueryAsync(
            connection,
            transaction,
            payloadQueries,
            "ContestBatches",
            "ProjectId",
            "AcceptedManuscriptJson",
            0,
            cancellationToken);

        var candidateColumns = await ExistingColumnsAsync(
            connection,
            "ContestCandidates",
            ["BatchId", "ProposedManuscriptJson", "DraftManuscriptJson", "ReviewStateJson"],
            cancellationToken);
        var batchColumns = await ExistingColumnsAsync(
            connection,
            "ContestBatches",
            ["Id", "ProjectId"],
            cancellationToken);
        if (batchColumns.Count == 2 && candidateColumns.Contains("BatchId", StringComparer.OrdinalIgnoreCase))
        {
            foreach (var column in new[] { "ProposedManuscriptJson", "DraftManuscriptJson", "ReviewStateJson" })
            {
                if (!candidateColumns.Contains(column, StringComparer.OrdinalIgnoreCase))
                    continue;
                payloadQueries.Add($"""
                    SELECT batch.ProjectId, candidate.{column}, 0
                    FROM ContestCandidates candidate
                    JOIN ContestBatches batch ON batch.Id = candidate.BatchId
                    """);
            }
        }

        var sessionColumns = await ExistingColumnsAsync(
            connection,
            "EditorRevisionSessions",
            ["JobId", "OriginalManuscriptJson", "OperationsJson", "ProposalJson"],
            cancellationToken);
        var jobColumns = await ExistingColumnsAsync(
            connection,
            "EditorRevisionJobs",
            ["Id", "ProjectId"],
            cancellationToken);
        if (jobColumns.Count == 2 && sessionColumns.Contains("JobId", StringComparer.OrdinalIgnoreCase))
        {
            foreach (var column in new[] { "OriginalManuscriptJson", "OperationsJson", "ProposalJson" })
            {
                if (!sessionColumns.Contains(column, StringComparer.OrdinalIgnoreCase))
                    continue;
                payloadQueries.Add($"""
                    SELECT job.ProjectId, session.{column}, 0
                    FROM EditorRevisionSessions session
                    JOIN EditorRevisionJobs job ON job.Id = session.JobId
                    """);
            }
        }

        var changeColumns = await ExistingColumnsAsync(
            connection,
            "AiChanges",
            ["BatchId", "ArgumentsJson", "BeforeJson", "AfterJson", "DraftAfterJson", "ReviewStateJson", "ResultJson"],
            cancellationToken);
        var changeBatchColumns = await ExistingColumnsAsync(
            connection,
            "AiChangeBatches",
            ["Id", "ProjectId"],
            cancellationToken);
        if (changeBatchColumns.Count == 2 && changeColumns.Contains("BatchId", StringComparer.OrdinalIgnoreCase))
        {
            foreach (var column in new[] { "ArgumentsJson", "BeforeJson", "AfterJson", "DraftAfterJson", "ReviewStateJson", "ResultJson" })
            {
                if (!changeColumns.Contains(column, StringComparer.OrdinalIgnoreCase))
                    continue;
                var resultFlag = string.Equals(column, "ResultJson", StringComparison.Ordinal) ? 1 : 0;
                payloadQueries.Add($"""
                    SELECT batch.ProjectId, change.{column}, {resultFlag}
                    FROM AiChanges change
                    JOIN AiChangeBatches batch ON batch.Id = change.BatchId
                    """);
            }
        }

        await using (var selectPayloads = connection.CreateCommand())
        {
            selectPayloads.Transaction = transaction;
            selectPayloads.CommandText = string.Join("\nUNION ALL\n", payloadQueries);
            await using var reader = await selectPayloads.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.IsDBNull(1) || string.IsNullOrWhiteSpace(reader.GetString(1)))
                    continue;
                var projectId = reader.GetString(0);
                var payload = reader.GetString(1);
                var manuscripts = reader.GetInt32(2) == 1 && !IsSyntacticallyValidJson(payload)
                    // ResultJson historically also stored plain-text tool results.
                    // Preserve those audit bytes; they cannot contain style roles.
                    ? []
                    : ManuscriptSchemaUpgrade.ExtractCurrentDocuments(payload);
                if (!rolesByProject.TryGetValue(projectId, out var rolesByKind))
                {
                    rolesByKind = new Dictionary<ManuscriptStyleKind, HashSet<string>>
                    {
                        [ManuscriptStyleKind.Paragraph] = new(StringComparer.OrdinalIgnoreCase),
                        [ManuscriptStyleKind.Character] = new(StringComparer.OrdinalIgnoreCase),
                    };
                    rolesByProject[projectId] = rolesByKind;
                }
                foreach (var manuscript in manuscripts)
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

    private static bool IsSyntacticallyValidJson(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task AddDirectPayloadQueryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ICollection<string> payloadQueries,
        string table,
        string projectColumn,
        string payloadColumn,
        int resultFlag,
        CancellationToken cancellationToken)
    {
        var columns = await ExistingColumnsAsync(
            connection,
            table,
            [projectColumn, payloadColumn],
            cancellationToken);
        if (columns.Count != 2)
            return;
        payloadQueries.Add($"SELECT {projectColumn}, {payloadColumn}, {resultFlag} FROM {table}");
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

    private static async Task<IReadOnlyList<string>> ExistingColumnsAsync(
        SqliteConnection connection,
        string table,
        IReadOnlyList<string> candidates,
        CancellationToken cancellationToken)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\");";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            existing.Add(reader.GetString(1));

        return candidates.Where(existing.Contains).ToArray();
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

    private static bool IsLegacyParagraphHash(string? value) =>
        value is { Length: 16 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

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

    private sealed record SchemaUpgradeColumnSet(
        string Table,
        string IdColumn,
        IReadOnlyList<string> Columns);

    private static readonly IReadOnlyList<SchemaUpgradeColumnSet> SchemaUpgradeColumns =
    [
        new("Chapters", "Id", ["ManuscriptJson"]),
        new("PublicationEditionChapterOverrides", "Id", ["ManuscriptJson"]),
        new("PublicationSections", "Id", ["ManuscriptJson"]),
        new("DesignedPageContents", "Id", ["SemanticManuscriptJson"]),
        new("PageCompositions", "Id", ["SemanticManuscriptJson"]),
        new("PublicationBooks", "Id", ["ManuscriptJson"]),
        new("PublicationBookMatter", "Id", ["ManuscriptJson"]),
        new("PublicationMatter", "Id", ["ManuscriptJson"]),
        new("ContestBatches", "Id", ["OriginalManuscriptJson", "AcceptedManuscriptJson"]),
        new("ContestCandidates", "Id", ["ProposedManuscriptJson", "DraftManuscriptJson", "ReviewStateJson"]),
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

    private sealed record ChapterMigrationReport(
        int ChapterCount,
        int StaleIllustrationAnchorHashCount);

    private sealed record ManuscriptMigrationReport(
        int ChapterCount,
        int StaleIllustrationAnchorHashCount,
        int ContestBatchCount,
        int ContestCandidateCount,
        int RevisionSessionCount,
        int AiChangeCount,
        string SourceHash,
        string TargetHash,
        DateTime ValidatedAtUtc);
}
