using Lorekeeper.Manuscripts;
using Lorekeeper.Publish;
using Lorekeeper.Startup;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Data.Common;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lorekeeper.Persistence;

public interface IDatabaseStartupMigrationService
{
    Task<bool> ApplyAsync(
        CancellationToken cancellationToken = default,
        IProgress<DatabaseStartupMigrationProgress>? progress = null);
}

public sealed class DatabaseStartupMigrationService(
    IAppDatabaseOperationFactory database,
    IManuscriptMigrationService manuscriptMigration,
    IPublicationEditionMigrationService editionMigration,
    IPublicationPressMigrationService pressMigration,
    IVisualCompositionMigrationService visualCompositionMigration,
    IAuthoringPageMigrationService authoringPageMigration,
    IPublicationCoreMigrationService publicationCoreMigration,
    IEditionContentMigrationService editionContentMigration,
    IPublicationSectionMigrationService publicationSectionMigration,
    IPrintProductMigrationService printProductMigration,
    IDatabaseMigrationRecoveryService recovery) : IDatabaseStartupMigrationService
{
    private const string PublicationSectionOrderMigrationId = "20260813204554_AddPublicationSectionOrderOverrides";
    private const string AuthoringHistoryMigrationId = "20260814202943_AddPersistentAuthoringHistoryV29";
    internal const string AssistantReviewBaselineMigrationId = "20260821100000_AddAssistantReviewBaselines";
    internal const string AuthoringHistoryCleanupMigrationId = "20260821100100_RemovePersistentAuthoringHistory";

    public async Task<bool> ApplyAsync(
        CancellationToken cancellationToken = default,
        IProgress<DatabaseStartupMigrationProgress>? progress = null)
    {
        const int totalSteps = 11;
        Report(progress, "Checking database compatibility", "Preparing safe schema boundaries.", 1, totalSteps);
        _ = await recovery.ApplyScheduledRestoreAsync(cancellationToken);
        if (await recovery.IsRecoveryRequiredAsync(cancellationToken))
            return false;

        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        await EnsureAuthoringHistoryCompatibilityColumnsAsync(db, cancellationToken);
        await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);
        Report(progress, "Checking manuscripts", "Validating chapters, illustrations, and revision history.", 2, totalSteps);
        await manuscriptMigration.ApplyPendingAsync(db, cancellationToken);
        await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);
        Report(progress, "Checking publication editions", "Preparing edition-owned publishing records.", 3, totalSteps);
        await editionMigration.ApplyPendingAsync(db, cancellationToken);
        await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);
        Report(progress, "Checking press data", "Validating publication layouts, artifacts, and packages.", 4, totalSteps);
        await pressMigration.ApplyPendingAsync(db, cancellationToken);
        await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);
        await EnsurePrintProductCompatibilityColumnsAsync(db, cancellationToken);

        Report(progress, "Checking designed pages", "Migrating visual compositions and semantic text bindings.", 5, totalSteps);
        var appliedMigrations = (await db.Database.GetAppliedMigrationsAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        if (!appliedMigrations.Contains(VisualCompositionMigrationService.CleanupMigrationId))
        {
            await db.GetService<IMigrator>().MigrateAsync(
                VisualCompositionMigrationService.AdditiveMigrationId,
                cancellationToken);
            await EnsureAuthoringHistoryCompatibilityColumnsAsync(db, cancellationToken);
            await EnsureEditionCompatibilityColumnsAsync(db, cancellationToken);
            await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);
            await visualCompositionMigration.ApplyPendingAsync(db, cancellationToken);
            if (!await recovery.IsRecoveryRequiredAsync(cancellationToken))
                await visualCompositionMigration.ApplyFinalSchemaAsync(db, cancellationToken);
        }
        else
        {
            await EnsureEditionCompatibilityColumnsAsync(db, cancellationToken);
            await visualCompositionMigration.ApplyFinalSchemaAsync(db, cancellationToken);
            await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);
            await visualCompositionMigration.ApplyPendingAsync(db, cancellationToken);
        }
        // Visual cleanup rebuilds several tables from its historical model and
        // therefore intentionally drops future compatibility columns.
        await EnsureEditionCompatibilityColumnsAsync(db, cancellationToken);
        await EnsureAuthoringHistoryCompatibilityColumnsAsync(db, cancellationToken);
        await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);

        var migrationsBeforeAuthoring = (await db.Database.GetAppliedMigrationsAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        if (!migrationsBeforeAuthoring.Contains(PublicationCoreMigrationService.SchemaMigrationId))
            await db.GetService<IMigrator>().MigrateAsync(
                PublicationCoreMigrationService.SchemaMigrationId,
                cancellationToken);
        await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);

        Report(progress, "Checking authoring pages", "Preparing active page layouts and project page setup.", 6, totalSteps);
        await EnsurePublicationSectionCompatibilityColumnsAsync(db, cancellationToken);
        await EnsurePrintProductCompatibilityColumnsAsync(db, cancellationToken);
        await authoringPageMigration.ApplyPendingAsync(db, cancellationToken);
        await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);
        Report(progress, "Checking publication structure", "Validating Core Book content and ownership.", 7, totalSteps);
        await publicationCoreMigration.ApplyPendingAsync(db, cancellationToken);
        await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);

        Report(progress, "Checking edition content", "Preparing release-specific manuscript content.", 8, totalSteps);
        await RemoveEditionCompatibilityColumnsAsync(db, cancellationToken);
        await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);
        await RemovePublicationSectionCompatibilityColumnsAsync(db, cancellationToken);
        await editionContentMigration.ApplyPendingAsync(db, cancellationToken);
        await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);
        await EnsurePrintProductCompatibilityColumnsAsync(db, cancellationToken);
        await RemovePublicationSectionCompatibilityColumnsAsync(db, cancellationToken);
        Report(progress, "Checking publication sections", "Validating front matter, body order, and back matter.", 9, totalSteps);
        await publicationSectionMigration.ApplyPendingAsync(db, cancellationToken);
        await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);

        Report(progress, "Checking print products", "Preparing physical-product and cover configuration.", 10, totalSteps);
        var migrationsBeforePrintProducts = (await db.Database.GetAppliedMigrationsAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        if (!migrationsBeforePrintProducts.Contains(PrintProductMigrationService.CleanupMigrationId))
        {
            if (!migrationsBeforePrintProducts.Contains(PrintProductMigrationService.AdditiveMigrationId))
            {
                await RemovePrintProductCompatibilityColumnsAsync(db, cancellationToken);
                await db.GetService<IMigrator>().MigrateAsync(
                    PrintProductMigrationService.AdditiveMigrationId,
                    cancellationToken);
                await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);
            }
            await printProductMigration.ApplyPendingAsync(db, cancellationToken);
            await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);
        }

        if (await recovery.IsRecoveryRequiredAsync(cancellationToken))
            return false;

        Report(progress, "Finalizing database schema", "Applying the remaining forward migrations and integrity repairs.", 11, totalSteps);
        await RemovePublicationSectionStartSideCompatibilityColumnAsync(db, cancellationToken);

        var migrationsBeforeCleanup = (await db.Database.GetAppliedMigrationsAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        if (!migrationsBeforeCleanup.Contains(PublicationCoreMigrationService.CleanupMigrationId))
            await db.GetService<IMigrator>().MigrateAsync(
                PublicationCoreMigrationService.CleanupMigrationId,
                cancellationToken);
        await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);
        if (!migrationsBeforeCleanup.Contains(PrintProductMigrationService.CleanupMigrationId))
            await db.GetService<IMigrator>().MigrateAsync(
                PrintProductMigrationService.CleanupMigrationId,
                cancellationToken);
        // Historical cleanup migrations rebuild PublicationEditions from their
        // own immutable models. Restore the compatibility column, remove it at
        // the current boundary, then let the additive migration own it.
        await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);
        await RemovePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);
        await RemoveAuthoringHistoryCompatibilityColumnsAsync(db, cancellationToken);
        // Run the review-baseline transition only after the existing startup
        // migration stages have brought the schema to their final boundary.
        // Calling EF Migrate to the new additive migration at the beginning
        // would skip the owner services for earlier data migrations.
        await PrepareAssistantReviewBaselineTransitionAsync(db, cancellationToken);
        if (await recovery.IsRecoveryRequiredAsync(cancellationToken))
            return false;
        await db.GetService<IMigrator>().MigrateAsync(cancellationToken: cancellationToken);
        await CleanupDetachedCompositionsAsync(db, cancellationToken);
        await publicationSectionMigration.RepairSemanticRevisionDriftAsync(db, cancellationToken);
        return !await recovery.IsRecoveryRequiredAsync(cancellationToken);
    }

    internal static async Task CleanupDetachedCompositionsAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        // Detached rows are retained only while an in-process history snapshot
        // can restore them. History is empty before the first startup request,
        // so remove detached variants first, then detached compositions with
        // no remaining live variants. The guarded predicate preserves any
        // unexpectedly live composition and all of its IDs/content.
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM \"PageCompositionVariants\" WHERE \"DetachedAt\" IS NOT NULL;",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """
            DELETE FROM "PageCompositions"
            WHERE "DetachedAt" IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1
                  FROM "PageCompositionVariants" AS variants
                  WHERE variants."CompositionId" = "PageCompositions"."Id");
            """,
            cancellationToken);
        db.ChangeTracker.Clear();
    }

    private async Task PrepareAssistantReviewBaselineTransitionAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        if (applied.Contains(AuthoringHistoryCleanupMigrationId))
            return;

        // The baseline backfill reads the legacy history and the following
        // migration drops every history table. Keep both schema changes and
        // the data transform behind one protected recovery boundary so a
        // partially completed transition can always restore the source DB.
        var backupPath = await recovery.CreateBackupAsync(
            "authoring",
            "pre-review-baseline-cleanup",
            cancellationToken);
        try
        {
            if (!applied.Contains(AssistantReviewBaselineMigrationId))
            {
                await db.GetService<IMigrator>().MigrateAsync(
                    AssistantReviewBaselineMigrationId,
                    cancellationToken);
                db.ChangeTracker.Clear();
            }

            await BackfillAssistantReviewBaselinesAsync(db, cancellationToken);
            await db.GetService<IMigrator>().MigrateAsync(
                AuthoringHistoryCleanupMigrationId,
                cancellationToken);
            db.ChangeTracker.Clear();
        }
        catch (Exception exception)
        {
            await recovery.EnterRecoveryModeAsync(
                db,
                backupPath,
                "assistant-review-baseline-v1",
                29,
                31,
                exception,
                cancellationToken);
        }
    }

    internal static async Task BackfillAssistantReviewBaselinesAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (!await HasTableAsync(db, "AssistantReviewBaselines", cancellationToken)
            || !await HasTableAsync(db, "AuthoringHistoryStreams", cancellationToken))
            return;

        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        var streams = new List<LegacyReviewStream>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT Id, ProjectId, DocumentKind, DocumentId, EditionId,
                       BaselineSnapshot, CursorSequence,
                       LatestReviewBeforeJson, LatestReviewBeforeHash,
                       LatestReviewAssistantTurnId, LatestReviewActionLabel,
                       LatestReviewCapturedAt
                FROM AuthoringHistoryStreams;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var kind = reader.GetString(2);
                if (!string.Equals(kind, "CoreChapter", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(kind, "EditionChapter", StringComparison.OrdinalIgnoreCase))
                    continue;

                streams.Add(new LegacyReviewStream(
                    ReadGuid(reader, 0),
                    ReadGuid(reader, 1),
                    kind,
                    ReadGuid(reader, 3),
                    ReadNullableGuid(reader, 4),
                    reader.IsDBNull(5) ? [] : (byte[])reader.GetValue(5),
                    reader.GetInt64(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    ReadNullableGuid(reader, 9),
                    reader.IsDBNull(10) ? null : reader.GetString(10),
                    reader.IsDBNull(11) ? null : reader.GetDateTime(11)));
            }
        }

        foreach (var stream in streams)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = TryReadChapterTarget(stream);
            if (target is null)
                continue;

            var durableCandidate = TryReadDurableReviewCandidate(stream, target.ChapterId);
            var entries = await ReadLegacyEntriesAsync(connection, stream.Id, stream.CursorSequence, cancellationToken);
            var legacyCandidate = await FindLegacyReviewCandidateAsync(
                stream,
                target.ChapterId,
                entries,
                cancellationToken);
            var candidate = durableCandidate;
            if (legacyCandidate is not null
                && (candidate is null || legacyCandidate.CapturedAt > candidate.CapturedAt))
            {
                candidate = legacyCandidate;
            }
            if (candidate is null)
                continue;

            var existing = await db.AssistantReviewBaselines
                .AsTracking()
                .SingleOrDefaultAsync(item => item.ProjectId == stream.ProjectId
                    && item.ChapterId == target.ChapterId
                    && item.TargetKey == target.TargetKey, cancellationToken);
            if (existing is null)
            {
                db.AssistantReviewBaselines.Add(new Lorekeeper.Models.AssistantReviewBaseline
                {
                    ProjectId = stream.ProjectId,
                    ChapterId = target.ChapterId,
                    TargetKind = target.TargetKind,
                    EditionId = target.EditionId,
                    TargetKey = target.TargetKey,
                    BeforeManuscriptJson = candidate.BeforeJson,
                    BeforeHash = candidate.BeforeHash,
                    AssistantTurnId = candidate.AssistantTurnId,
                    ActionLabel = candidate.ActionLabel,
                    CapturedAt = candidate.CapturedAt,
                });
            }
            else if (candidate.CapturedAt > existing.CapturedAt)
            {
                existing.TargetKind = target.TargetKind;
                existing.EditionId = target.EditionId;
                existing.BeforeManuscriptJson = candidate.BeforeJson;
                existing.BeforeHash = candidate.BeforeHash;
                existing.AssistantTurnId = candidate.AssistantTurnId;
                existing.ActionLabel = candidate.ActionLabel;
                existing.CapturedAt = candidate.CapturedAt;
            }
        }

        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(cancellationToken);
    }

    private static ReviewTarget? TryReadChapterTarget(LegacyReviewStream stream)
    {
        if (string.Equals(stream.DocumentKind, "CoreChapter", StringComparison.OrdinalIgnoreCase))
            return new(EditorContentTargetKind.Core, null, "core", stream.DocumentId);
        if (string.Equals(stream.DocumentKind, "EditionChapter", StringComparison.OrdinalIgnoreCase)
            && stream.EditionId is Guid editionId
            && editionId != Guid.Empty)
        {
            return new(
                EditorContentTargetKind.Edition,
                editionId,
                $"edition:{editionId:N}",
                stream.DocumentId);
        }
        return null;
    }

    private static ReviewCandidate? TryReadDurableReviewCandidate(
        LegacyReviewStream stream,
        Guid chapterId)
    {
        if (stream.LatestReviewCapturedAt is not DateTime capturedAt
            || string.IsNullOrWhiteSpace(stream.LatestReviewBeforeJson)
            || string.IsNullOrWhiteSpace(stream.LatestReviewBeforeHash))
            return null;
        return ValidateReviewCandidate(
            stream.LatestReviewBeforeJson,
            stream.LatestReviewBeforeHash,
            chapterId,
            stream.LatestReviewAssistantTurnId,
            stream.LatestReviewActionLabel,
            capturedAt);
    }

    private static Task<ReviewCandidate?> FindLegacyReviewCandidateAsync(
        LegacyReviewStream stream,
        Guid chapterId,
        IReadOnlyList<LegacyReviewEntry> entries,
        CancellationToken cancellationToken)
    {
        foreach (var entry in entries
                     .Where(item => string.Equals(item.Origin, "Assistant", StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(item => item.Sequence))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var preceding = entries.LastOrDefault(item => item.Sequence < entry.Sequence)?.ResultSnapshot
                ?? stream.BaselineSnapshot;
            var beforeJson = TryReadManuscriptJson(preceding);
            if (beforeJson is null)
                continue;
            var candidate = ValidateReviewCandidate(
                beforeJson,
                Hash(beforeJson),
                chapterId,
                entry.AssistantTurnId,
                entry.ActionLabel,
                entry.CreatedAt);
            if (candidate is not null)
                return Task.FromResult<ReviewCandidate?>(candidate);
        }
        return Task.FromResult<ReviewCandidate?>(null);
    }

    private static ReviewCandidate? ValidateReviewCandidate(
        string beforeJson,
        string expectedHash,
        Guid chapterId,
        Guid? assistantTurnId,
        string? actionLabel,
        DateTime capturedAt)
    {
        try
        {
            var document = ManuscriptCodec.Deserialize(beforeJson);
            if (document.ManuscriptId != chapterId
                || !FixedEquals(Hash(beforeJson), expectedHash))
                return null;
            return new(
                beforeJson,
                Hash(beforeJson),
                assistantTurnId,
                string.IsNullOrWhiteSpace(actionLabel) ? "Assistant change" : actionLabel.Trim(),
                capturedAt);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<LegacyReviewEntry>> ReadLegacyEntriesAsync(
        DbConnection connection,
        Guid streamId,
        long cursorSequence,
        CancellationToken cancellationToken)
    {
        var entries = new List<LegacyReviewEntry>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Sequence, Origin, AssistantTurnId, ResultSnapshot,
                   ActionLabel, CreatedAt
            FROM AuthoringHistoryEntries
            WHERE StreamId = $streamId AND Sequence <= $cursorSequence
            ORDER BY Sequence;
            """;
        AddParameter(command, "$streamId", streamId);
        AddParameter(command, "$cursorSequence", cursorSequence);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new LegacyReviewEntry(
                reader.GetInt64(0),
                reader.GetString(1),
                ReadNullableGuid(reader, 2),
                reader.IsDBNull(3) ? [] : (byte[])reader.GetValue(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetDateTime(5)));
        }
        return entries;
    }

    private static string? TryReadManuscriptJson(byte[] compressedSnapshot)
    {
        if (compressedSnapshot.Length == 0)
            return null;
        try
        {
            using var input = new MemoryStream(compressedSnapshot);
            using var brotli = new BrotliStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(brotli, Encoding.UTF8);
            using var document = JsonDocument.Parse(reader.ReadToEnd());
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "manuscriptJson", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String)
                    return property.Value.GetString();
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or JsonException)
        {
            // A corrupt or non-manuscript history snapshot must not block
            // startup or become a Review baseline.
        }
        return null;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static Guid ReadGuid(DbDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value switch
        {
            Guid id => id,
            byte[] bytes when bytes.Length == 16 => new Guid(bytes),
            _ => Guid.Parse(value.ToString()!),
        };
    }

    private static Guid? ReadNullableGuid(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ReadGuid(reader, ordinal);

    private static bool FixedEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(left),
            Encoding.UTF8.GetBytes(right));

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record LegacyReviewStream(
        Guid Id,
        Guid ProjectId,
        string DocumentKind,
        Guid DocumentId,
        Guid? EditionId,
        byte[] BaselineSnapshot,
        long CursorSequence,
        string? LatestReviewBeforeJson,
        string? LatestReviewBeforeHash,
        Guid? LatestReviewAssistantTurnId,
        string? LatestReviewActionLabel,
        DateTime? LatestReviewCapturedAt);

    private sealed record LegacyReviewEntry(
        long Sequence,
        string Origin,
        Guid? AssistantTurnId,
        byte[] ResultSnapshot,
        string? ActionLabel,
        DateTime CreatedAt);

    private sealed record ReviewTarget(
        EditorContentTargetKind TargetKind,
        Guid? EditionId,
        string TargetKey,
        Guid ChapterId);

    private sealed record ReviewCandidate(
        string BeforeJson,
        string BeforeHash,
        Guid? AssistantTurnId,
        string ActionLabel,
        DateTime CapturedAt);

    private static void Report(
        IProgress<DatabaseStartupMigrationProgress>? progress,
        string title,
        string detail,
        int step,
        int totalSteps) =>
        progress?.Report(new(title, detail, step, totalSteps));

    internal static async Task EnsureAuthoringHistoryCompatibilityColumnsAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        if (applied.Contains(AuthoringHistoryMigrationId))
            return;
        (string Table, string Column)[] columns =
        [
            ("PageCompositions", "DetachedAt"),
            ("PageCompositionVariants", "DetachedAt"),
        ];
        foreach (var (table, column) in columns)
        {
            if (!await HasTableAsync(db, table, cancellationToken)
                || await HasColumnAsync(db, table, column, cancellationToken))
                continue;
#pragma warning disable EF1002 // Table and column come exclusively from the fixed list above.
            await db.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" TEXT NULL;",
                cancellationToken);
#pragma warning restore EF1002
        }
        db.ChangeTracker.Clear();
    }

    internal static async Task RemoveAuthoringHistoryCompatibilityColumnsAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        if (applied.Contains(AuthoringHistoryMigrationId))
            return;
        foreach (var table in new[] { "PageCompositionVariants", "PageCompositions" })
        {
            if (!await HasTableAsync(db, table, cancellationToken)
                || !await HasColumnAsync(db, table, "DetachedAt", cancellationToken))
                continue;
#pragma warning disable EF1002 // Table comes exclusively from the fixed list above.
            await db.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE \"{table}\" DROP COLUMN \"DetachedAt\";",
                cancellationToken);
#pragma warning restore EF1002
        }
        db.ChangeTracker.Clear();
    }

    private static async Task EnsureEditionCompatibilityColumnsAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        if (applied.Contains(EditionContentMigrationService.AdditiveMigrationId))
            return;
        (string Table, string Column, string Definition)[] columns =
        [
            ("PublicationEditions", "EditionSpecificContentEnabled", "INTEGER NOT NULL DEFAULT 0"),
            ("PageCompositions", "EditionId", "TEXT NULL"),
            ("PageCompositions", "SourceCompositionId", "TEXT NULL"),
            ("EditorRevisionJobs", "ContentTargetEditionId", "TEXT NULL"),
            ("EditorRevisionJobs", "ContentTargetKind", "TEXT NOT NULL DEFAULT ''"),
            ("EditorMessages", "ContentTargetEditionId", "TEXT NULL"),
            ("EditorMessages", "ContentTargetKind", "TEXT NOT NULL DEFAULT ''"),
            ("ContestBatches", "ContentTargetEditionId", "TEXT NULL"),
            ("ContestBatches", "ContentTargetKind", "TEXT NOT NULL DEFAULT ''"),
            ("CompositionMutationStages", "ContentTargetEditionId", "TEXT NULL"),
            ("CompositionMutationStages", "ContentTargetKind", "TEXT NOT NULL DEFAULT ''"),
            ("AiChangeBatches", "ContentTargetEditionId", "TEXT NULL"),
            ("AiChangeBatches", "ContentTargetKind", "TEXT NOT NULL DEFAULT ''"),
        ];
        foreach (var (table, column, definition) in columns)
        {
            if (await HasColumnAsync(db, table, column, cancellationToken))
                continue;
            // Table, column, and definition come exclusively from the fixed list above.
#pragma warning disable EF1002
            await db.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition};",
                cancellationToken);
#pragma warning restore EF1002
        }
        db.ChangeTracker.Clear();
    }

    internal static async Task RemoveEditionCompatibilityColumnsAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        if (applied.Contains(EditionContentMigrationService.AdditiveMigrationId))
            return;
        await db.Database.ExecuteSqlRawAsync(
            "DROP TRIGGER IF EXISTS TR_PageCompositions_ActiveAuthoringVariant_Update;",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "DROP TRIGGER IF EXISTS TR_PageCompositionVariants_ClearAuthoringSelection;",
            cancellationToken);
        (string Table, string Column)[] columns =
        [
            ("PublicationEditions", "EditionSpecificContentEnabled"),
            ("PageCompositions", "EditionId"),
            ("PageCompositions", "SourceCompositionId"),
            ("EditorRevisionJobs", "ContentTargetEditionId"),
            ("EditorRevisionJobs", "ContentTargetKind"),
            ("EditorMessages", "ContentTargetEditionId"),
            ("EditorMessages", "ContentTargetKind"),
            ("ContestBatches", "ContentTargetEditionId"),
            ("ContestBatches", "ContentTargetKind"),
            ("CompositionMutationStages", "ContentTargetEditionId"),
            ("CompositionMutationStages", "ContentTargetKind"),
            ("AiChangeBatches", "ContentTargetEditionId"),
            ("AiChangeBatches", "ContentTargetKind"),
        ];
        foreach (var (table, column) in columns)
        {
            if (!await HasColumnAsync(db, table, column, cancellationToken))
                continue;
            // Table and column come exclusively from the fixed list above.
#pragma warning disable EF1002
            await db.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE \"{table}\" DROP COLUMN \"{column}\";",
                cancellationToken);
#pragma warning restore EF1002
        }
        db.ChangeTracker.Clear();
    }

    internal static async Task EnsurePublicationSectionCompatibilityColumnsAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        if (applied.Contains(PublicationSectionMigrationService.AdditiveMigrationId)
            || await HasColumnAsync(db, "PageCompositions", "PublicationSectionId", cancellationToken))
            return;
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"PageCompositions\" ADD COLUMN \"PublicationSectionId\" TEXT NULL;",
            cancellationToken);
        db.ChangeTracker.Clear();
    }

    internal static async Task RemovePublicationSectionCompatibilityColumnsAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        if (applied.Contains(PublicationSectionMigrationService.AdditiveMigrationId)
            || !await HasColumnAsync(db, "PageCompositions", "PublicationSectionId", cancellationToken))
            return;
        await db.Database.ExecuteSqlRawAsync(
            "DROP TRIGGER IF EXISTS TR_PageCompositions_ActiveAuthoringVariant_Update;",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "DROP TRIGGER IF EXISTS TR_PageCompositionVariants_ClearAuthoringSelection;",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"PageCompositions\" DROP COLUMN \"PublicationSectionId\";",
            cancellationToken);
        db.ChangeTracker.Clear();
    }

    internal static async Task EnsurePublicationSectionStartSideCompatibilityColumnAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        if (applied.Contains(PublicationSectionMigrationService.StartSideMigrationId)
            || await HasColumnAsync(db, "PublicationSections", "StartSide", cancellationToken))
            return;
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"PublicationSections\" ADD COLUMN \"StartSide\" TEXT NOT NULL DEFAULT 'Next';",
            cancellationToken);
        db.ChangeTracker.Clear();
    }

    private static async Task RemovePublicationSectionStartSideCompatibilityColumnAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        if (applied.Contains(PublicationSectionMigrationService.StartSideMigrationId)
            || !await HasColumnAsync(db, "PublicationSections", "StartSide", cancellationToken))
            return;
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"PublicationSections\" DROP COLUMN \"StartSide\";",
            cancellationToken);
        db.ChangeTracker.Clear();
    }

    private static async Task<bool> HasColumnAsync(
        AppDbContext db,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    internal static async Task EnsurePublicationSectionOrderCompatibilityColumnAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        if (applied.Contains(PublicationSectionOrderMigrationId))
            return;
        if (!await HasTableAsync(db, "PublicationEditions", cancellationToken))
            return;
        if (!await HasColumnAsync(db, "PublicationEditions", "PublicationSectionOrderJson", cancellationToken))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"PublicationEditions\" ADD COLUMN \"PublicationSectionOrderJson\" TEXT NOT NULL DEFAULT '{{}}';",
                cancellationToken);
        }
        db.ChangeTracker.Clear();
    }

    private static async Task RemovePublicationSectionOrderCompatibilityColumnAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        if (applied.Contains(PublicationSectionOrderMigrationId)
            || !await HasColumnAsync(db, "PublicationEditions", "PublicationSectionOrderJson", cancellationToken))
            return;
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"PublicationEditions\" DROP COLUMN \"PublicationSectionOrderJson\";",
            cancellationToken);
        db.ChangeTracker.Clear();
    }

    private static async Task<bool> HasTableAsync(
        AppDbContext db,
        string table,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $table";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$table";
        parameter.Value = table;
        command.Parameters.Add(parameter);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    internal static async Task EnsurePrintProductCompatibilityColumnsAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        if (applied.Contains(PrintProductMigrationService.AdditiveMigrationId)) return;
        (string Name, string Definition)[] columns =
        [
            ("GenericPrintTemplateJson", "TEXT NOT NULL DEFAULT ''"),
            ("PrintRegistryVersion", "TEXT NOT NULL DEFAULT ''"),
            ("PrintProductKey", "TEXT NOT NULL DEFAULT ''"),
            ("PrintFinish", "TEXT NOT NULL DEFAULT 'Matte'"),
            ("PrintCoverMode", "TEXT NOT NULL DEFAULT 'Simplex'"),
        ];
        foreach (var (name, definition) in columns)
        {
            if (await HasColumnAsync(db, "PublicationEditions", name, cancellationToken)) continue;
#pragma warning disable EF1002
            await db.Database.ExecuteSqlRawAsync($"ALTER TABLE \"PublicationEditions\" ADD COLUMN \"{name}\" {definition};", cancellationToken);
#pragma warning restore EF1002
        }
        if (!await HasColumnAsync(db, "PublicationCoverDesigns", "SurfaceScenesJson", cancellationToken))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"PublicationCoverDesigns\" ADD COLUMN \"SurfaceScenesJson\" TEXT NOT NULL DEFAULT '{{}}';",
                cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE PublicationEditions
            SET PrintRegistryVersion = CASE WHEN Format IN ('Paperback','Hardcover') THEN '2026.08.1' ELSE '' END,
                PrintProductKey = CASE
                    WHEN Format = 'Paperback' AND Vendor = 'AmazonKdp' AND Ink = 'Color' THEN 'kdp-pb-premium-color'
                    WHEN Format = 'Paperback' AND Vendor = 'AmazonKdp' AND Paper = 'Cream' THEN 'kdp-pb-bw-cream'
                    WHEN Format = 'Paperback' AND Vendor = 'AmazonKdp' THEN 'kdp-pb-bw-white'
                    WHEN Format = 'Paperback' AND Vendor = 'IngramSpark' AND Ink = 'Color' THEN 'ingram-pb-premium70'
                    WHEN Format = 'Paperback' AND Vendor = 'IngramSpark' AND Paper = 'Cream' THEN 'ingram-pb-bw-cream50'
                    WHEN Format = 'Paperback' AND Vendor = 'IngramSpark' THEN 'ingram-pb-bw-white50'
                    WHEN Format = 'Paperback' THEN 'generic-perfectbound-template'
                    WHEN Format = 'Hardcover' AND Vendor = 'AmazonKdp' THEN 'kdp-hc-bw-white'
                    WHEN Format = 'Hardcover' AND Vendor = 'IngramSpark' THEN 'ingram-hc-case-bw-white50'
                    WHEN Format = 'Hardcover' THEN 'generic-casebound-template'
                    ELSE '' END
            WHERE PrintProductKey = '';
            """,
            cancellationToken);
        db.ChangeTracker.Clear();
    }

    internal static async Task RemovePrintProductCompatibilityColumnsAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        if (applied.Contains(PrintProductMigrationService.AdditiveMigrationId)) return;
        foreach (var name in new[] { "GenericPrintTemplateJson", "PrintRegistryVersion", "PrintProductKey", "PrintFinish", "PrintCoverMode" })
        {
            if (!await HasColumnAsync(db, "PublicationEditions", name, cancellationToken)) continue;
#pragma warning disable EF1002
            await db.Database.ExecuteSqlRawAsync($"ALTER TABLE \"PublicationEditions\" DROP COLUMN \"{name}\";", cancellationToken);
#pragma warning restore EF1002
        }
        if (await HasColumnAsync(db, "PublicationCoverDesigns", "SurfaceScenesJson", cancellationToken))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"PublicationCoverDesigns\" DROP COLUMN \"SurfaceScenesJson\";",
                cancellationToken);
        db.ChangeTracker.Clear();
    }
}
