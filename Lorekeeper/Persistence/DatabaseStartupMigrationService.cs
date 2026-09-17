using Lorekeeper.Chapters;
using Lorekeeper.Composition;
using Lorekeeper.EntityVisuals;
using Lorekeeper.Images;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Publish;
using Lorekeeper.Persistence.Legacy;
using Lorekeeper.Startup;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    IDesignedPageMigrationService designedPageMigration,
    IPrintArtifactProfileMigrationService printArtifactProfileMigration,
    IDatabaseMigrationRecoveryService recovery,
    IServiceProvider? serviceProvider = null) : IDatabaseStartupMigrationService
{
    private static readonly JsonSerializerOptions LegacyReviewPayloadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private const string PublicationSectionOrderMigrationId = "20260813204554_AddPublicationSectionOrderOverrides";
    private const string AuthoringHistoryMigrationId = "20260814202943_AddPersistentAuthoringHistoryV29";
    internal const string AuthoringHistoryCleanupMigrationId = "20260821100100_RemovePersistentAuthoringHistory";
    internal const string ReviewWorkflowAdditiveMigrationId = "20260826200707_PrepareReviewWorkflowTransition";
    internal const string ReviewWorkflowCleanupMigrationId = "20260826200708_FinalizeReviewWorkflowTransition";
    private const string RectoChapterStartsMigrationId = "20260830174820_AddConfigurableRectoChapterStarts";
    private const string BarnesAndNoblePrintMigrationId = "20260830201715_BarnesAndNoblePrintPublishingV31";
    private const string ArtifactOnlyPrintSettingsMigrationId = "20260830212921_ArtifactOnlyPrintSettingsV32";

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
        await EnsureBarnesAndNoblePrintCompatibilityColumnsAsync(db, cancellationToken);
        await EnsureReviewPreferenceCompatibilityColumnAsync(db, cancellationToken);
        await EnsureAuthoringHistoryCompatibilityColumnsAsync(db, cancellationToken);
        await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);
        await EnsureRectoChapterStartsCompatibilityColumnsAsync(db, cancellationToken);
        Report(progress, "Checking manuscripts", "Validating chapters, illustrations, and revision history.", 2, totalSteps);
        await manuscriptMigration.ApplyPendingAsync(db, cancellationToken);
        await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);
        await EnsureRectoChapterStartsCompatibilityColumnsAsync(db, cancellationToken);
        Report(progress, "Checking publication editions", "Preparing edition-owned publishing records.", 3, totalSteps);
        await editionMigration.ApplyPendingAsync(db, cancellationToken);
        await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);
        await EnsureRectoChapterStartsCompatibilityColumnsAsync(db, cancellationToken);
        Report(progress, "Checking press data", "Validating publication layouts, artifacts, and packages.", 4, totalSteps);
        await pressMigration.ApplyPendingAsync(db, cancellationToken);
        await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);
        await EnsureRectoChapterStartsCompatibilityColumnsAsync(db, cancellationToken);
        await EnsurePrintProductCompatibilityColumnsAsync(db, cancellationToken);
        // Older installs and fresh databases may only now have publication
        // tables. The visual migration reads them through the current EF model.
        await EnsureBarnesAndNoblePrintCompatibilityColumnsAsync(db, cancellationToken);

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
            await EnsureRectoChapterStartsCompatibilityColumnsAsync(db, cancellationToken);
            await visualCompositionMigration.ApplyPendingAsync(db, cancellationToken);
            if (!await recovery.IsRecoveryRequiredAsync(cancellationToken))
                await visualCompositionMigration.ApplyFinalSchemaAsync(db, cancellationToken);
        }
        else
        {
            await EnsureEditionCompatibilityColumnsAsync(db, cancellationToken);
            await visualCompositionMigration.ApplyFinalSchemaAsync(db, cancellationToken);
            await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);
            await EnsureRectoChapterStartsCompatibilityColumnsAsync(db, cancellationToken);
            await visualCompositionMigration.ApplyPendingAsync(db, cancellationToken);
        }
        // Visual cleanup rebuilds several tables from its historical model and
        // therefore intentionally drops future compatibility columns.
        await EnsureEditionCompatibilityColumnsAsync(db, cancellationToken);
        await EnsureAuthoringHistoryCompatibilityColumnsAsync(db, cancellationToken);
        await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);
        await EnsureRectoChapterStartsCompatibilityColumnsAsync(db, cancellationToken);

        var migrationsBeforeAuthoring = (await db.Database.GetAppliedMigrationsAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        if (!migrationsBeforeAuthoring.Contains(PublicationCoreMigrationService.SchemaMigrationId))
            await db.GetService<IMigrator>().MigrateAsync(
                PublicationCoreMigrationService.SchemaMigrationId,
                cancellationToken);
        // A brand-new database has no Projects table when the initial
        // compatibility check runs at the top of startup. The historical
        // migrations above create that table with the retired
        // AiChangeApprovalEnabled column, while current EF queries already
        // bind Project.ReviewEditsEnabled. Add/backfill the transition column
        // before any owner migration reads Project (Publication Core is the
        // first such reader on a fresh database).
        await EnsureReviewPreferenceCompatibilityColumnAsync(db, cancellationToken);
        await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);
        await EnsureRectoChapterStartsCompatibilityColumnsAsync(db, cancellationToken);

        Report(progress, "Checking authoring pages", "Preparing active page layouts and project page setup.", 6, totalSteps);
        await EnsurePublicationSectionCompatibilityColumnsAsync(db, cancellationToken);
        await EnsurePrintProductCompatibilityColumnsAsync(db, cancellationToken);
        await EnsureBarnesAndNoblePrintCompatibilityColumnsAsync(db, cancellationToken);
        await authoringPageMigration.ApplyPendingAsync(db, cancellationToken);
        await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);
        await EnsureRectoChapterStartsCompatibilityColumnsAsync(db, cancellationToken);
        Report(progress, "Checking publication structure", "Validating Core Book content and ownership.", 7, totalSteps);
        await publicationCoreMigration.ApplyPendingAsync(db, cancellationToken);
        await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);
        await EnsureRectoChapterStartsCompatibilityColumnsAsync(db, cancellationToken);

        Report(progress, "Checking edition content", "Preparing release-specific manuscript content.", 8, totalSteps);
        await RemoveEditionCompatibilityColumnsAsync(db, cancellationToken);
        await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);
        await EnsureRectoChapterStartsCompatibilityColumnsAsync(db, cancellationToken);
        await RemovePublicationSectionCompatibilityColumnsAsync(db, cancellationToken);
        await editionContentMigration.ApplyPendingAsync(db, cancellationToken);
        await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);
        await EnsureRectoChapterStartsCompatibilityColumnsAsync(db, cancellationToken);
        await EnsurePrintProductCompatibilityColumnsAsync(db, cancellationToken);
        await EnsureBarnesAndNoblePrintCompatibilityColumnsAsync(db, cancellationToken);
        await RemovePublicationSectionCompatibilityColumnsAsync(db, cancellationToken);
        Report(progress, "Checking publication sections", "Validating front matter, body order, and back matter.", 9, totalSteps);
        await publicationSectionMigration.ApplyPendingAsync(db, cancellationToken);
        await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);
        await EnsureRectoChapterStartsCompatibilityColumnsAsync(db, cancellationToken);

        Report(progress, "Checking print artifacts", "Preparing artifact profiles and cover configuration.", 10, totalSteps);
        var migrationsBeforeArtifactProfiles = (await db.Database.GetAppliedMigrationsAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        if (!migrationsBeforeArtifactProfiles.Contains(PrintArtifactProfileMigrationService.CleanupMigrationId))
        {
            if (!migrationsBeforeArtifactProfiles.Contains(PrintArtifactProfileMigrationService.AdditiveMigrationId))
            {
                await RemoveBarnesAndNoblePrintCompatibilityColumnsAsync(db, cancellationToken);
                await RemovePrintProductCompatibilityColumnsAsync(db, cancellationToken);
                await db.GetService<IMigrator>().MigrateAsync(
                    PrintArtifactProfileMigrationService.AdditiveMigrationId,
                    cancellationToken);
                await EnsurePrintProductCompatibilityColumnsAsync(db, cancellationToken);
                await EnsureBarnesAndNoblePrintCompatibilityColumnsAsync(db, cancellationToken);
                await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);
                await EnsureRectoChapterStartsCompatibilityColumnsAsync(db, cancellationToken);
            }
            await printArtifactProfileMigration.ApplyPendingAsync(db, cancellationToken);
            await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);
            await EnsureRectoChapterStartsCompatibilityColumnsAsync(db, cancellationToken);
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
        await EnsureRectoChapterStartsCompatibilityColumnsAsync(db, cancellationToken);
        if (!migrationsBeforeCleanup.Contains(PrintArtifactProfileMigrationService.CleanupMigrationId))
        {
            await RemovePrintArtifactProfileCompatibilityColumnsAsync(db, cancellationToken);
            await db.GetService<IMigrator>().MigrateAsync(
                PrintArtifactProfileMigrationService.CleanupMigrationId,
                cancellationToken);
        }
        await EnsurePrintProductCompatibilityColumnsAsync(db, cancellationToken);
        await EnsureBarnesAndNoblePrintCompatibilityColumnsAsync(db, cancellationToken);
        // Historical cleanup migrations rebuild PublicationEditions from their
        // own immutable models. Restore the compatibility column, remove it at
        // the current boundary, then let the additive migration own it.
        await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);
        await EnsureRectoChapterStartsCompatibilityColumnsAsync(db, cancellationToken);
        await RemovePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);
        await RemoveAuthoringHistoryCompatibilityColumnsAsync(db, cancellationToken);
        // Run the review-baseline transition only after the existing startup
        // migration stages have brought the schema to their final boundary.
        // Calling EF Migrate to the new additive migration at the beginning
        // would skip the owner services for earlier data migrations.
        await PrepareAuthoringHistoryCleanupAsync(db, cancellationToken);
        if (await recovery.IsRecoveryRequiredAsync(cancellationToken))
            return false;
        await PrepareReviewWorkflowTransitionAsync(db, cancellationToken);
        if (await recovery.IsRecoveryRequiredAsync(cancellationToken))
            return false;
        await RemoveRectoChapterStartsCompatibilityColumnsAsync(db, cancellationToken);
        await RemoveBarnesAndNoblePrintCompatibilityColumnsAsync(db, cancellationToken);
        await RemovePrintArtifactProfileCompatibilityColumnsAsync(db, cancellationToken);
        await CleanupDetachedCompositionsAsync(db, cancellationToken);
        await PrepareDesignedPageTransitionAsync(db, designedPageMigration, cancellationToken);
        if (await recovery.IsRecoveryRequiredAsync(cancellationToken))
            return false;
        await db.GetService<IMigrator>().MigrateAsync(cancellationToken: cancellationToken);
        await publicationSectionMigration.RepairSemanticRevisionDriftAsync(db, cancellationToken);
        return !await recovery.IsRecoveryRequiredAsync(cancellationToken);
    }

    private async Task PrepareDesignedPageTransitionAsync(
        AppDbContext db,
        IDesignedPageMigrationService migration,
        CancellationToken cancellationToken)
    {
        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        if (applied.Contains(DesignedPageMigrationService.CleanupMigrationId))
            return;
        var backupPath = await recovery.CreateBackupAsync(
            "designed-pages",
            "pre-m2-page-ownership",
            cancellationToken);
        try
        {
            if (!applied.Contains(DesignedPageMigrationService.AdditiveMigrationId))
            {
                await db.GetService<IMigrator>().MigrateAsync(
                    DesignedPageMigrationService.AdditiveMigrationId,
                    cancellationToken);
                db.ChangeTracker.Clear();
            }
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                await migration.ApplyPendingAsync(db, cancellationToken);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException("The Designed Page data transform failed.", exception);
            }
            await db.GetService<IMigrator>().MigrateAsync(
                DesignedPageMigrationService.CleanupMigrationId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }
        catch (Exception exception)
        {
            db.ChangeTracker.Clear();
            try
            {
                await recovery.EnterRecoveryModeAsync(
                    db,
                    backupPath,
                    "designed-pages-v1",
                    4,
                    ManuscriptDocument.CurrentSchemaVersion,
                    exception,
                    cancellationToken);
            }
            catch (Exception recoveryException)
            {
                throw new AggregateException(
                    "The Designed Page migration failed and recovery mode could not be recorded.",
                    exception,
                    recoveryException);
            }
        }
    }

    internal static async Task CleanupDetachedCompositionsAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (!await HasTableAsync(db, "PageCompositions", cancellationToken))
            return;
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

    private async Task PrepareReviewWorkflowTransitionAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        if (applied.Contains(ReviewWorkflowCleanupMigrationId))
            return;

        var backupPath = await recovery.CreateBackupAsync(
            "review-workflow",
            "pre-legacy-review-cleanup",
            cancellationToken);
        try
        {
            if (!applied.Contains(ReviewWorkflowAdditiveMigrationId))
            {
                await RemoveReviewPreferenceCompatibilityColumnAsync(db, cancellationToken);
                await db.GetService<IMigrator>().MigrateAsync(
                    ReviewWorkflowAdditiveMigrationId,
                    cancellationToken);
                db.ChangeTracker.Clear();
            }

            // Keep the data transform and destructive cleanup in one SQLite
            // transaction. If startup is interrupted after one legacy change
            // is materialized, the revision fence and legacy row are rolled
            // back together, so the next startup can safely retry.
            await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
            {
                await BackfillContestStateAsync(db, cancellationToken);
                await ValidateUnresolvedContestUniquenessAsync(db, cancellationToken);
                await MaterializeLegacyReviewChangesAsync(db, cancellationToken);
                await db.GetService<IMigrator>().MigrateAsync(
                    ReviewWorkflowCleanupMigrationId,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                db.ChangeTracker.Clear();
            }
        }
        catch (Exception exception)
        {
            await recovery.EnterRecoveryModeAsync(
                db,
                backupPath,
                "review-workflow-v1",
                30,
                32,
                exception,
                cancellationToken);
        }
    }

    private static async Task ValidateUnresolvedContestUniquenessAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var duplicates = await db.ContestBatches
            .AsNoTracking()
            .Where(batch => batch.Status == ContestBatchStatus.Running
                || batch.Status == ContestBatchStatus.Completed
                || batch.Status == ContestBatchStatus.Failed)
            .GroupBy(batch => batch.ProjectId)
            .Where(group => group.Count() > 1)
            .Select(group => new { ProjectId = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        if (duplicates.Count == 0)
            return;

        var detail = string.Join(", ", duplicates.Select(item => $"{item.ProjectId:N} ({item.Count})"));
        throw new InvalidDataException(
            $"Multiple unresolved contests were found for one or more projects: {detail}. A recovery backup was created; resolve the duplicate contest rows before retrying startup.");
    }

    private static async Task BackfillContestStateAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (!await HasTableAsync(db, "ContestBatches", cancellationToken)
            || !await HasColumnAsync(db, "ContestBatches", "OriginalManuscriptHash", cancellationToken))
            return;

        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        var batches = new List<LegacyContestBatch>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = """
                SELECT Id, ProjectId, ChapterId, ContentTargetKind,
                       ContentTargetEditionId, OriginalManuscriptJson,
                       AcceptedManuscriptJson, OriginalManuscriptRevision,
                       OriginalManuscriptHash, SelectedCandidateId, WinningCandidateId,
                       Status
                FROM ContestBatches;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                batches.Add(new LegacyContestBatch(
                    ReadGuid(reader, 0),
                    ReadGuid(reader, 1),
                    ReadGuid(reader, 2),
                    string.IsNullOrWhiteSpace(reader.IsDBNull(3) ? null : reader.GetString(3))
                        ? "Core"
                        : reader.GetString(3),
                    ReadNullableGuid(reader, 4),
                    reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                    reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                    reader.IsDBNull(7) ? 0 : reader.GetInt64(7),
                    reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                    ReadNullableGuid(reader, 9),
                    ReadNullableGuid(reader, 10),
                    reader.IsDBNull(11) ? string.Empty : reader.GetString(11)));
            }
        }

        foreach (var batch in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(batch.OriginalManuscriptJson))
                throw new InvalidDataException($"Contest {batch.Id:N} has no original manuscript snapshot.");

            ManuscriptDocument original;
            try
            {
                original = ManuscriptCodec.Deserialize(batch.OriginalManuscriptJson);
                if (original.ManuscriptId != batch.ChapterId)
                    throw new InvalidDataException("The contest snapshot does not belong to its chapter.");
            }
            catch (Exception exception) when (exception is InvalidDataException or JsonException)
            {
                throw new InvalidDataException($"Contest {batch.Id:N} has an invalid original manuscript snapshot.", exception);
            }

            // Contest resolution fences compare semantic manuscript content,
            // not the serialized envelope. Keep migration backfill aligned
            // with EditorContestService so migrated contests can resolve
            // without tripping their source hash check.
            var originalHash = HashManuscriptContent(original);
            if (!string.IsNullOrWhiteSpace(batch.OriginalManuscriptHash)
                && !FixedEquals(batch.OriginalManuscriptHash, originalHash))
            {
                throw new InvalidDataException($"Contest {batch.Id:N} has a conflicting original manuscript hash.");
            }
            if (!string.Equals(batch.ContentTargetKind, "Core", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(batch.ContentTargetKind, "Edition", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Contest {batch.Id:N} has an unsupported content target.");
            }
            if (string.Equals(batch.ContentTargetKind, "Edition", StringComparison.OrdinalIgnoreCase)
                && batch.ContentTargetEditionId is not Guid editionId)
            {
                throw new InvalidDataException($"Contest {batch.Id:N} has no edition target.");
            }
            if (!Enum.TryParse<ContestBatchStatus>(batch.Status, ignoreCase: true, out var contestStatus)
                || !Enum.IsDefined(contestStatus))
                throw new InvalidDataException($"Contest {batch.Id:N} has an unsupported status.");
            var isUnresolvedContest = contestStatus is ContestBatchStatus.Running
                or ContestBatchStatus.Completed
                or ContestBatchStatus.Failed;

            await ValidateContestTargetOwnershipAsync(db, batch, cancellationToken);

            var candidates = await db.ContestCandidates
                .AsTracking()
                .Where(candidate => candidate.BatchId == batch.Id)
                .OrderBy(candidate => candidate.Order)
                .ToListAsync(cancellationToken);
            var selected = batch.SelectedCandidateId is Guid selectedId
                ? candidates.FirstOrDefault(candidate => candidate.Id == selectedId)
                : null;
            if (batch.SelectedCandidateId is Guid selectedReferenceId
                && selected is null)
            {
                throw new InvalidDataException($"Contest {batch.Id:N} references a missing selected candidate.");
            }
            selected ??= batch.WinningCandidateId is Guid winningId
                ? candidates.FirstOrDefault(candidate => candidate.Id == winningId)
                : null;
            if (batch.WinningCandidateId is Guid winningCandidateId
                && candidates.All(candidate => candidate.Id != winningCandidateId))
            {
                throw new InvalidDataException($"Contest {batch.Id:N} references a missing winning candidate.");
            }
            selected ??= candidates.FirstOrDefault(candidate => candidate.Status == ContestCandidateStatus.Completed);

            foreach (var candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate.ProposedManuscriptJson))
                {
                    try
                    {
                        var proposal = ManuscriptCodec.Deserialize(candidate.ProposedManuscriptJson);
                        if (proposal.ManuscriptId != batch.ChapterId)
                            throw new InvalidDataException("The candidate manuscript does not belong to its chapter.");
                    }
                    catch (Exception exception) when (exception is InvalidDataException or JsonException)
                    {
                        throw new InvalidDataException($"Contest candidate {candidate.Id:N} has an invalid manuscript proposal.", exception);
                    }
                }
                else if (candidate.Status is ContestCandidateStatus.Completed or ContestCandidateStatus.Selected)
                {
                    throw new InvalidDataException($"Contest candidate {candidate.Id:N} is completed but has no manuscript proposal.");
                }

                if (string.IsNullOrWhiteSpace(candidate.DraftManuscriptJson))
                {
                    if (!string.IsNullOrWhiteSpace(candidate.ProposedManuscriptJson))
                        candidate.DraftManuscriptJson = candidate.ProposedManuscriptJson;
                }
                else
                {
                    try
                    {
                        var draft = ManuscriptCodec.Deserialize(candidate.DraftManuscriptJson);
                        if (draft.ManuscriptId != batch.ChapterId)
                            throw new InvalidDataException("The candidate draft does not belong to its chapter.");
                    }
                    catch (Exception exception) when (exception is InvalidDataException or JsonException)
                    {
                        throw new InvalidDataException($"Contest candidate {candidate.Id:N} has an invalid saved draft.", exception);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(batch.AcceptedManuscriptJson))
            {
                ManuscriptDocument accepted;
                try
                {
                    accepted = ManuscriptCodec.Deserialize(batch.AcceptedManuscriptJson);
                    if (accepted.ManuscriptId != batch.ChapterId)
                        throw new InvalidDataException("The accepted manuscript does not belong to its chapter.");
                }
                catch (Exception exception) when (exception is InvalidDataException or JsonException)
                {
                    throw new InvalidDataException($"Contest {batch.Id:N} has an invalid accepted manuscript.", exception);
                }

                if (isUnresolvedContest && !ManuscriptCodec.ContentEquals(original, accepted))
                {
                    selected ??= candidates.FirstOrDefault();
                    if (selected is null)
                        throw new InvalidDataException($"Contest {batch.Id:N} has a mixed accepted manuscript but no candidate draft.");
                    selected.DraftManuscriptJson = batch.AcceptedManuscriptJson;
                }
            }

            var current = await ReadContestTargetAsync(db, batch, cancellationToken);
            var contentMatchesOriginal = ManuscriptCodec.ContentEquals(current.Document, original);
            var revisionMatchesOriginal = current.Document.Revision == original.Revision;
            if (isUnresolvedContest && (!contentMatchesOriginal || !revisionMatchesOriginal))
            {
                if (!contentMatchesOriginal && string.IsNullOrWhiteSpace(batch.AcceptedManuscriptJson))
                    throw new InvalidDataException($"Contest {batch.Id:N} changed its live manuscript without an accepted snapshot.");

                if (!contentMatchesOriginal)
                {
                    ManuscriptDocument accepted;
                    try
                    {
                        accepted = ManuscriptCodec.Deserialize(batch.AcceptedManuscriptJson);
                    }
                    catch (Exception exception) when (exception is InvalidDataException or JsonException)
                    {
                        throw new InvalidDataException($"Contest {batch.Id:N} has an invalid accepted manuscript.", exception);
                    }

                    if (!ManuscriptCodec.ContentEquals(current.Document, accepted))
                        throw new InvalidDataException($"Contest {batch.Id:N} has conflicting live and accepted manuscript state.");
                }
                await RestoreContestTargetAsync(db, batch, original, cancellationToken);
            }

            var originalRevision = batch.OriginalManuscriptRevision == 0
                ? original.Revision
                : batch.OriginalManuscriptRevision;
            if (originalRevision != original.Revision)
                throw new InvalidDataException($"Contest {batch.Id:N} has an inconsistent original manuscript revision.");

            var selectedCandidateId = selected?.Id;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE ContestBatches
                SET OriginalManuscriptRevision = {originalRevision},
                    OriginalManuscriptHash = {originalHash},
                    SelectedCandidateId = {selectedCandidateId},
                    Status = CASE lower(Status)
                        WHEN 'running' THEN 'Running'
                        WHEN 'completed' THEN 'Completed'
                        WHEN 'failed' THEN 'Failed'
                        WHEN 'finished' THEN 'Resolved'
                        WHEN 'cancelled' THEN 'Discarded'
                        WHEN 'resolved' THEN 'Resolved'
                        WHEN 'discarded' THEN 'Discarded'
                        ELSE Status END,
                    UpdatedAt = {DateTime.UtcNow}
                WHERE Id = {batch.Id};
                """, cancellationToken);
        }

        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
    }

    private static async Task<ContestTargetSnapshot> ReadContestTargetAsync(
        AppDbContext db,
        LegacyContestBatch batch,
        CancellationToken cancellationToken)
    {
        var chapter = await db.Chapters
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == batch.ChapterId && item.ProjectId == batch.ProjectId, cancellationToken)
            ?? throw new InvalidDataException($"Contest {batch.Id:N} references a missing chapter.");
        if (string.Equals(batch.ContentTargetKind, "Core", StringComparison.OrdinalIgnoreCase))
        {
            return new(chapter.ManuscriptJson, chapter.Manuscript);
        }

        if (batch.ContentTargetEditionId is not Guid editionId)
            throw new InvalidDataException($"Contest {batch.Id:N} has no edition target.");

        var chapterOverride = await db.PublicationEditionChapterOverrides
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.EditionId == editionId && item.ChapterId == batch.ChapterId, cancellationToken);
        if (chapterOverride is null)
            return new(chapter.ManuscriptJson, chapter.Manuscript);
        return new(chapterOverride.ManuscriptJson, ManuscriptCodec.Deserialize(
            chapterOverride.ManuscriptJson,
            batch.ChapterId,
            chapterOverride.Revision));
    }

    private static async Task ValidateContestTargetOwnershipAsync(
        AppDbContext db,
        LegacyContestBatch batch,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(batch.ContentTargetKind, "Edition", StringComparison.OrdinalIgnoreCase))
            return;

        if (batch.ContentTargetEditionId is not Guid editionId
            || !await db.PublicationEditions.AsNoTracking()
                .AnyAsync(edition => edition.Id == editionId && edition.ProjectId == batch.ProjectId, cancellationToken))
        {
            throw new InvalidDataException(
                $"Contest {batch.Id:N} references an edition that does not belong to project {batch.ProjectId:N}.");
        }
    }

    private static async Task RestoreContestTargetAsync(
        AppDbContext db,
        LegacyContestBatch batch,
        ManuscriptDocument original,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        if (string.Equals(batch.ContentTargetKind, "Edition", StringComparison.OrdinalIgnoreCase)
            && batch.ContentTargetEditionId is Guid editionId)
        {
            var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE PublicationEditionChapterOverrides
                SET ManuscriptJson = {batch.OriginalManuscriptJson},
                    Revision = {original.Revision},
                    UpdatedAt = {now}
                WHERE EditionId = {editionId} AND ChapterId = {batch.ChapterId};
                """, cancellationToken);
            if (updated != 1)
                throw new InvalidDataException(
                    $"Contest {batch.Id:N} could not restore its edition manuscript target.");
            return;
        }

        var coreUpdated = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE Chapters
            SET ManuscriptJson = {batch.OriginalManuscriptJson},
                ManuscriptRevision = {original.Revision},
                UpdatedAt = {now}
            WHERE Id = {batch.ChapterId} AND ProjectId = {batch.ProjectId};
            """, cancellationToken);
        if (coreUpdated != 1)
            throw new InvalidDataException(
                $"Contest {batch.Id:N} could not restore its Core manuscript target.");
    }

    private sealed record LegacyContestBatch(
        Guid Id,
        Guid ProjectId,
        Guid ChapterId,
        string ContentTargetKind,
        Guid? ContentTargetEditionId,
        string OriginalManuscriptJson,
        string AcceptedManuscriptJson,
        long OriginalManuscriptRevision,
        string OriginalManuscriptHash,
        Guid? SelectedCandidateId,
        Guid? WinningCandidateId,
        string Status);

    private sealed record ContestTargetSnapshot(string Json, ManuscriptDocument Document);

    /// <summary>
    /// Applies the small, migration-owned subset of the retired review queue
    /// that can be represented directly by the canonical manuscript tables.
    /// This intentionally uses the legacy tables through SQL only: they are
    /// not part of the current EF model and are dropped by the guarded
    /// workflow cleanup migration immediately afterwards.
    /// </summary>
    private async Task MaterializeLegacyReviewChangesAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var hasChanges = await HasTableAsync(db, "AiChanges", cancellationToken);
        var hasBatches = await HasTableAsync(db, "AiChangeBatches", cancellationToken);
        if (hasChanges != hasBatches)
        {
            throw new InvalidDataException(
                "The legacy review tables are incomplete: AiChanges and AiChangeBatches must either both exist or both be absent."
                + " The protected pre-transition backup retains the source database.");
        }
        if (!hasChanges)
            return;

        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        // A foreign key normally prevents this shape, but older databases may
        // have been created before enforcement was enabled. Never drop an
        // orphaned proposal while cleaning up the legacy tables.
        await using (var orphanCommand = connection.CreateCommand())
        {
            orphanCommand.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            orphanCommand.CommandText = """
                SELECT COUNT(*)
                FROM AiChanges AS change
                LEFT JOIN AiChangeBatches AS batch ON batch.Id = change.BatchId
                WHERE batch.Id IS NULL;
                """;
            var orphanCount = Convert.ToInt64(await orphanCommand.ExecuteScalarAsync(cancellationToken));
            if (orphanCount != 0)
            {
                throw new InvalidDataException(
                    $"The legacy review tables contain {orphanCount} orphaned AI change(s) without a batch. "
                    + "The protected pre-transition backup retains the source database.");
            }
        }

        var changes = new List<LegacyReviewChange>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = """
                 SELECT change.Id, change.Status, change.ToolName,
                       change.ArgumentsJson,
                       change.BeforeJson, change.AfterJson, change.DraftAfterJson,
                       change.DependsOnChangeIdsJson, change.ResourceId,
                       batch.ProjectId, batch.ContentTargetKind,
                       batch.ContentTargetEditionId
                 FROM AiChanges AS change
                 JOIN AiChangeBatches AS batch ON batch.Id = change.BatchId
                 ORDER BY batch.CreatedAt, change."Order";
                 """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                changes.Add(new LegacyReviewChange(
                    ReadGuid(reader, 0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? "{}" : reader.GetString(3),
                    reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? "[]" : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    ReadGuid(reader, 9),
                    string.IsNullOrWhiteSpace(reader.IsDBNull(10) ? null : reader.GetString(10))
                        ? "Core"
                        : reader.GetString(10),
                    ReadNullableGuid(reader, 11)));
            }
        }

        if (changes.Count == 0)
            return;

        foreach (var change in changes)
        {
            if (!IsPendingLegacyReviewChange(change)
                && !IsMaterializedLegacyReviewChange(change)
                && !IsDiscardedLegacyReviewChange(change))
            {
                throw new InvalidDataException(
                    $"Legacy review change {change.Id:N} has unsupported status '{change.Status}'. "
                    + "The protected pre-transition backup retains the source database.");
            }
        }

        var changesById = changes.ToDictionary(change => change.Id);
        var applied = new HashSet<Guid>();
        var visiting = new HashSet<Guid>();
        foreach (var change in changes.Where(IsPendingLegacyReviewChange))
            await MaterializeLegacyReviewChangeAsync(
                db,
                change,
                changesById,
                applied,
                visiting,
                cancellationToken);
        // Owning services normally flush their own mutations. The startup-only
        // Designed Page path queues a composition/variant and manuscript update
        // directly on this context, so persist any remaining tracked work before
        // the cleanup migration drops the legacy source rows.
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
    }

    private async Task MaterializeLegacyReviewChangeAsync(
        AppDbContext db,
        LegacyReviewChange change,
        IReadOnlyDictionary<Guid, LegacyReviewChange> changesById,
        ISet<Guid> applied,
        ISet<Guid> visiting,
        CancellationToken cancellationToken)
    {
        if (applied.Contains(change.Id))
            return;
        if (!IsPendingLegacyReviewChange(change))
        {
            if (IsMaterializedLegacyReviewChange(change))
            {
                applied.Add(change.Id);
                return;
            }

            throw new InvalidDataException(
                $"Legacy review change {change.Id:N} has unsupported dependency status '{change.Status}'.");
        }
        if (!visiting.Add(change.Id))
            throw new InvalidDataException($"Legacy review change {change.Id:N} has a dependency cycle.");

        foreach (var dependencyId in ReadLegacyGuidList(change.DependsOnChangeIdsJson))
        {
            if (!changesById.TryGetValue(dependencyId, out var dependency))
                throw new InvalidDataException(
                    $"Legacy review change {change.Id:N} references missing dependency {dependencyId:N}.");
            await MaterializeLegacyReviewChangeAsync(
                db,
                dependency,
                changesById,
                applied,
                visiting,
                cancellationToken);
        }

        await ValidateLegacyTargetOwnershipAsync(db, change, cancellationToken);

        var effectiveJson = string.IsNullOrWhiteSpace(change.DraftAfterJson)
            ? change.AfterJson
            : change.DraftAfterJson;
        await ValidateLegacyResourceOwnershipAsync(db, change, effectiveJson, cancellationToken);
        if (change.ToolName is "edit_chapter"
            or "edit_assigned_chapter"
            or "apply_manuscript_operations"
            or "apply_assigned_manuscript_operations"
            or "apply_manuscript_style"
            or "insert_manuscript_figure"
            or "patch_manuscript_figure")
        {
            await MaterializeLegacyManuscriptChangeAsync(db, change, effectiveJson, cancellationToken);
        }
        else
            await MaterializeLegacyOwningChangeAsync(db, change, effectiveJson, cancellationToken);

        visiting.Remove(change.Id);
        applied.Add(change.Id);
    }

    /// <summary>
    /// Replays the non-manuscript tools that the retired review service accepted.
    /// The startup operation is ambient, so these owning services use the same
    /// DbContext and transaction as the schema transition. This keeps their
    /// normal ownership/dependency validation in one place while ensuring a
    /// crash rolls the live mutation and legacy-table cleanup back together.
    /// </summary>
    private async Task MaterializeLegacyOwningChangeAsync(
        AppDbContext db,
        LegacyReviewChange change,
        string effectiveJson,
        CancellationToken cancellationToken)
    {
        var acts = RequireService<IActService>();
        var chapters = RequireService<IChapterService>();
        var entities = RequireService<IEntityService>();
        var annotations = RequireService<IManuscriptAnnotationService>();
        var manuscriptStyles = RequireService<IManuscriptStyleService>();
        var entityVisualExamples = RequireService<IEntityVisualExampleService>();
        var projectImages = RequireService<IProjectImageService>();
        var contentTarget = EditorContentTarget.From(
            Enum.TryParse<EditorContentTargetKind>(change.ContentTargetKind, true, out var targetKind)
                ? targetKind
                : EditorContentTargetKind.Core,
            change.ContentTargetEditionId);

        switch (change.ToolName)
        {
            case "complete_manuscript_annotation":
            {
                var before = ReadLegacyPayload<ManuscriptAnnotationView>(change.BeforeJson, change, "annotation");
                var manuscriptDependencies = ReadLegacyGuidList(change.DependsOnChangeIdsJson);
                if (manuscriptDependencies.Count > 1)
                    throw new InvalidDataException($"Legacy annotation change {change.Id:N} has more than one manuscript dependency.");
                var expectedRevision = checked(before.Revision + manuscriptDependencies.Count);
                var current = await annotations.GetAsync(change.ProjectId, contentTarget, before.Id, cancellationToken)
                    ?? throw new InvalidDataException($"Legacy annotation change {change.Id:N} references a missing annotation.");
                if (current.Revision != expectedRevision)
                    throw new InvalidDataException($"Legacy annotation change {change.Id:N} no longer matches annotation revision {expectedRevision}.");
                await annotations.CompleteAsync(change.ProjectId, contentTarget, before.Id, current.Revision, cancellationToken);
                break;
            }
            case "create_act":
            {
                var after = ReadLegacyPayload<OutlineActChange>(effectiveJson, change, "act");
                await acts.CreateAsync(change.ProjectId, after.Title, after.Synopsis, after.Id, cancellationToken);
                break;
            }
            case "update_act":
            {
                var after = ReadLegacyPayload<OutlineActChange>(effectiveJson, change, "act");
                await acts.UpdateAsync(after.Id, after.Title, after.Synopsis, cancellationToken);
                break;
            }
            case "delete_act":
                await acts.DeleteAsync(ParseLegacyResourceGuid(change), cancellationToken);
                break;
            case "reorder_acts":
            {
                var after = ReadLegacyPayload<OutlineReorderChange>(effectiveJson, change, "act ordering");
                await acts.ReorderAsync(change.ProjectId, after.OrderedIds, cancellationToken);
                break;
            }
            case "create_chapter":
            {
                var after = ReadLegacyPayload<OutlineChapterChange>(effectiveJson, change, "chapter");
                await chapters.CreateAsync(change.ProjectId, after.ActId, after.Title, after.Synopsis, after.Id, cancellationToken);
                break;
            }
            case "update_chapter":
            {
                var after = ReadLegacyPayload<OutlineChapterChange>(effectiveJson, change, "chapter");
                await chapters.UpdateAsync(after.Id, after.Title, after.Synopsis, new ChapterActAssignment(after.ActId), cancellationToken);
                break;
            }
            case "delete_chapter":
            {
                // ChapterService also performs external index/history cleanup and opens
                // its own transaction. During startup we must keep the canonical row
                // mutation in the transition transaction, so remove the owned graph
                // through EF and let the configured cascades clean its dependents.
                var chapterId = ParseLegacyResourceGuid(change);
                var chapter = await db.Chapters
                    .FirstOrDefaultAsync(item => item.ProjectId == change.ProjectId && item.Id == chapterId, cancellationToken)
                    ?? throw new InvalidDataException($"Legacy chapter deletion {change.Id:N} references a missing chapter.");
                db.Chapters.Remove(chapter);
                break;
            }
            case "reorder_chapters":
            {
                var after = ReadLegacyPayload<OutlineReorderChange>(effectiveJson, change, "chapter ordering");
                await chapters.ReorderAsync(change.ProjectId, after.ParentId, after.OrderedIds, cancellationToken);
                break;
            }
            case "upsert_manuscript_style":
            case "create_paragraph_style_from_block":
            {
                var staged = ReadLegacyPayload<ManuscriptStyleChange>(effectiveJson, change, "manuscript style");
                var input = staged.After
                    ?? throw new InvalidDataException($"Legacy style change {change.Id:N} has no target state.");
                await manuscriptStyles.UpsertAsync(change.ProjectId, input, cancellationToken);
                break;
            }
            case "delete_manuscript_style":
            {
                var staged = ReadLegacyPayload<ManuscriptStyleChange>(effectiveJson, change, "manuscript style");
                var before = staged.Before
                    ?? throw new InvalidDataException($"Legacy style change {change.Id:N} has no source state.");
                await manuscriptStyles.DeleteAsync(change.ProjectId, before.Id, before.Revision, cancellationToken);
                break;
            }
            case "create_entity":
            {
                var after = ReadLegacyPayload<OutlineEntityChange>(effectiveJson, change, "entity");
                await entities.CreateAsync(change.ProjectId, after.Type, after.Name, after.Properties, after.ParentId, after.Order, after.Id, cancellationToken);
                break;
            }
            case "update_entity":
            {
                var before = ReadLegacyOptionalPayload<OutlineEntityChange>(change.BeforeJson, change, "entity");
                var after = ReadLegacyPayload<OutlineEntityChange>(effectiveJson, change, "entity");
                var propertiesToRemove = before?.Properties.Keys
                    .Where(key => !after.Properties.ContainsKey(key))
                    .ToArray();
                await entities.UpdateAsync(change.ProjectId, after.Id, after.Name, after.Properties, propertiesToRemove, cancellationToken);
                break;
            }
            case "delete_entity":
                await entities.DeleteAsync(change.ProjectId, ParseLegacyResourceGuid(change), cancellationToken);
                break;
            case "reorder_entities":
            {
                var after = ReadLegacyPayload<OutlineEntityReorderChange>(effectiveJson, change, "entity ordering");
                await entities.ReorderAsync(change.ProjectId, after.Type, after.ParentId, after.OrderedIds, cancellationToken);
                break;
            }
            case "link_entities":
            {
                var after = ReadLegacyPayload<OutlineEntityLinkChange>(effectiveJson, change, "entity link");
                await entities.LinkAsync(change.ProjectId, after.FromId, after.ToId, after.EdgeType, after.Properties, cancellationToken);
                break;
            }
            case "attach_entity_canonical_reference":
            case "crop_project_image":
            {
                var after = ReadLegacyPayload<EntityVisualChange>(effectiveJson, change, "entity visual reference");
                if (after.EntityId is not Guid entityId || after.ImageId is not Guid imageId)
                    throw new InvalidDataException($"Legacy visual change {change.Id:N} has no entity and image ids.");
                await entityVisualExamples.AttachAsync(change.ProjectId, entityId, imageId, after.Label, EntityVisualExampleOrigin.Agent, cancellationToken: cancellationToken);
                break;
            }
            case "update_entity_canonical_reference":
            {
                var after = ReadLegacyPayload<EntityVisualChange>(effectiveJson, change, "entity visual reference");
                if (after.ExampleId is not Guid exampleId)
                    throw new InvalidDataException($"Legacy visual change {change.Id:N} has no example id.");
                await entityVisualExamples.UpdateAsync(change.ProjectId, exampleId, after.Label, after.SortOrder, cancellationToken: cancellationToken);
                break;
            }
            case "detach_entity_canonical_reference":
            {
                var before = ReadLegacyPayload<EntityVisualChange>(change.BeforeJson, change, "entity visual reference");
                if (before.ExampleId is not Guid exampleId)
                    throw new InvalidDataException($"Legacy visual change {change.Id:N} has no example id.");
                await entityVisualExamples.DetachAsync(change.ProjectId, exampleId, cancellationToken);
                break;
            }
            case "import_web_image_as_entity_reference":
            {
                var after = ReadLegacyPayload<EntityVisualChange>(effectiveJson, change, "entity visual import");
                if (after.CandidateId is not Guid candidateId)
                    throw new InvalidDataException($"Legacy visual import {change.Id:N} has no source candidate.");
                var sourceImage = await entityVisualExamples.PromoteCandidateAsync(change.ProjectId, candidateId, cancellationToken);
                var referenceImage = after.Crop is null
                    ? sourceImage
                    : await projectImages.CropAsync(change.ProjectId, sourceImage.Id, new ProjectImageCropRequest(
                        after.Crop,
                        after.CropFileName,
                        after.CropAltText), cancellationToken);
                foreach (var target in after.Targets ?? [])
                    await entityVisualExamples.AttachAsync(
                        change.ProjectId,
                        target.EntityId,
                        referenceImage.Id,
                        target.Label,
                        EntityVisualExampleOrigin.Research,
                        candidateId,
                        cancellationToken);
                break;
            }
            case "insert_manuscript_designed_page":
                await MaterializeLegacyDesignedPageAsync(db, change, contentTarget, effectiveJson, cancellationToken);
                break;
            default:
                throw new InvalidDataException(
                    $"Legacy pending review change {change.Id:N} ({change.ToolName}) is not a recognized owning-service proposal. "
                    + "The protected pre-transition recovery backup retains the proposal.");
        }
    }

    private T RequireService<T>() where T : notnull
    {
        if (serviceProvider is null)
            throw new InvalidDataException(
                $"Legacy review materialization requires owning service {typeof(T).Name}, but startup was created without a service provider.");
        return serviceProvider.GetRequiredService<T>();
    }

    private async Task MaterializeLegacyDesignedPageAsync(
        AppDbContext db,
        LegacyReviewChange change,
        EditorContentTarget contentTarget,
        string effectiveJson,
        CancellationToken cancellationToken)
    {
        var before = ReadLegacyPayload<ChapterManuscriptChange>(change.BeforeJson, change, "Designed Page source manuscript");
        var after = ReadLegacyPayload<ChapterManuscriptChange>(effectiveJson, change, "Designed Page manuscript");
        if (before.Id != after.Id)
            throw new InvalidDataException($"Legacy Designed Page change {change.Id:N} changes chapter identity.");

        var chapter = await db.Chapters.AsTracking()
            .SingleOrDefaultAsync(item => item.ProjectId == change.ProjectId && item.Id == after.Id, cancellationToken)
            ?? throw new InvalidDataException($"Legacy Designed Page change {change.Id:N} references a missing chapter.");
        ManuscriptDocument current;
        long currentRevision;
        if (contentTarget.IsCore)
        {
            current = ManuscriptCodec.Deserialize(chapter.ManuscriptJson, chapter.Id, chapter.ManuscriptRevision);
            currentRevision = chapter.ManuscriptRevision;
        }
        else
        {
            if (contentTarget.EditionId is not Guid editionId)
                throw new InvalidDataException($"Legacy Designed Page change {change.Id:N} has no edition target.");
            var overrideRow = await db.PublicationEditionChapterOverrides.AsTracking()
                .SingleOrDefaultAsync(item => item.EditionId == editionId && item.ChapterId == after.Id, cancellationToken)
                ?? throw new InvalidDataException($"Legacy Designed Page change {change.Id:N} references a missing edition manuscript target.");
            current = ManuscriptCodec.Deserialize(overrideRow.ManuscriptJson, after.Id, overrideRow.Revision);
            currentRevision = overrideRow.Revision;
        }
        if (currentRevision != before.Revision || !ManuscriptCodec.ContentEquals(current, before.Manuscript))
            throw new InvalidDataException($"Legacy Designed Page change {change.Id:N} no longer matches its source manuscript.");

        var arguments = ReadLegacyPayload<DesignedPageToolArguments>(change.ArgumentsJson, change, "Designed Page arguments");
        if (arguments.ChapterId != after.Id || arguments.ExpectedRevision != before.Revision)
            throw new InvalidDataException($"Legacy Designed Page change {change.Id:N} has arguments for a different manuscript revision.");

        var beforeIds = before.Manuscript.Content.Select(block => block.Id).ToHashSet(StringComparer.Ordinal);
        var addedBlocks = after.Manuscript.Content.Where(block => !beforeIds.Contains(block.Id)).ToList();
        if (addedBlocks is not [var added]
            || added.Type != ManuscriptBlockType.DesignedPage
            || added.DesignedPageId is not Guid compositionId)
            throw new InvalidDataException($"Legacy Designed Page change {change.Id:N} must add exactly one Designed Page block.");

        var projected = ManuscriptOperations.Apply(
            before.Manuscript,
            [new InsertManuscriptBlock(
                arguments.BlockIndex,
                ManuscriptBlockType.DesignedPage,
                string.Empty,
                ManuscriptStyleRoles.DesignedPage,
                DesignedPageId: compositionId,
                BlockId: added.Id)]).Document;
        if (projected.Revision != after.Revision || !ManuscriptCodec.ContentEquals(projected, after.Manuscript))
            throw new InvalidDataException($"Legacy Designed Page change {change.Id:N} has an inconsistent manuscript projection.");

        if (await db.LegacyPageCompositions.IgnoreQueryFilters().AnyAsync(item => item.Id == compositionId, cancellationToken))
            throw new InvalidDataException($"Legacy Designed Page change {change.Id:N} reuses composition {compositionId:N}.");

        var layoutMode = arguments.LayoutMode;
        CompositionScene scene;
        if (contentTarget.EditionId is Guid targetEditionId)
        {
            var edition = await db.PublicationEditions.AsNoTracking()
                .SingleOrDefaultAsync(item => item.ProjectId == change.ProjectId && item.Id == targetEditionId, cancellationToken)
                ?? throw new InvalidDataException($"Legacy Designed Page change {change.Id:N} references a missing edition.");
            scene = DesignedPageService.CreatePageScene(edition, layoutMode);
        }
        else
        {
            var setup = await db.ProjectPageSetups.AsTracking()
                .SingleOrDefaultAsync(item => item.ProjectId == change.ProjectId, cancellationToken);
            if (setup is null)
            {
                setup = new ProjectPageSetup { ProjectId = change.ProjectId };
                db.ProjectPageSetups.Add(setup);
            }
            scene = DesignedPageService.CreatePageScene(setup, layoutMode);
        }

        if (arguments.ImageId is Guid imageId)
        {
            var image = await db.PublishAssets.AsNoTracking()
                .SingleOrDefaultAsync(item => item.ProjectId == change.ProjectId
                    && item.Id == imageId
                    && (item.ContentType == "image/png" || item.ContentType == "image/jpeg"), cancellationToken)
                ?? throw new InvalidDataException($"Legacy Designed Page change {change.Id:N} references an invalid artwork image.");
            var layer = scene.Layers.SingleOrDefault()
                ?? throw new InvalidDataException($"Legacy Designed Page change {change.Id:N} has no content layer.");
            var altText = arguments.Decorative
                ? string.Empty
                : string.IsNullOrWhiteSpace(arguments.AltText) ? image.AltText.Trim() : arguments.AltText.Trim();
            scene = scene with
            {
                Objects =
                [
                    new CompositionObject
                    {
                        Id = Guid.NewGuid(),
                        LayerId = layer.Id,
                        Kind = CompositionObjectKind.Image,
                        Name = image.FileName,
                        ImageId = image.Id,
                        ImageFit = arguments.ImageFit,
                        CropXPercent = Math.Clamp(arguments.CropXPercent, 0, 100),
                        CropYPercent = Math.Clamp(arguments.CropYPercent, 0, 100),
                        AltText = altText,
                        Decorative = arguments.Decorative,
                        AccessibilityDecisionPending = !arguments.Decorative && string.IsNullOrWhiteSpace(altText),
                        SemanticRole = arguments.Decorative ? CompositionSemanticRole.Artifact : CompositionSemanticRole.Figure,
                        ReadingOrder = arguments.Decorative ? null : 1,
                    },
                ],
            };
        }

        var composition = new LegacyPageComposition
        {
            Id = compositionId,
            ProjectId = change.ProjectId,
            ChapterId = after.Id,
            EditionId = contentTarget.EditionId,
            Name = string.IsNullOrWhiteSpace(arguments.Name) ? "Designed page" : arguments.Name.Trim(),
            SemanticManuscriptJson = ManuscriptCodec.Serialize(ManuscriptCodec.CreateEmpty(compositionId)),
        };
        var variant = new LegacyPageCompositionVariant
        {
            Id = Guid.NewGuid(),
            CompositionId = composition.Id,
            Composition = composition,
            GeometryKey = DesignedPageService.SceneGeometryKey(scene),
            SceneJson = JsonSerializer.Serialize(scene, ManuscriptCodec.JsonOptions),
        };
        composition.ActiveAuthoringVariantId = variant.Id;
        db.LegacyPageCompositions.Add(composition);
        db.LegacyPageCompositionVariants.Add(variant);

        if (contentTarget.IsCore)
        {
            chapter.ManuscriptJson = after.ManuscriptJson;
            chapter.ManuscriptRevision = after.Revision;
            chapter.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            var editionId = contentTarget.EditionId!.Value;
            var overrideRow = await db.PublicationEditionChapterOverrides
                .SingleAsync(item => item.EditionId == editionId && item.ChapterId == after.Id, cancellationToken);
            overrideRow.ManuscriptJson = after.ManuscriptJson;
            overrideRow.Revision = after.Revision;
            overrideRow.UpdatedAt = DateTime.UtcNow;
        }
        var project = await db.Projects.FirstOrDefaultAsync(item => item.Id == change.ProjectId, cancellationToken)
            ?? throw new InvalidDataException($"Legacy Designed Page change {change.Id:N} references a missing project.");
        project.UpdatedAt = DateTime.UtcNow;
    }

    private sealed record DesignedPageToolArguments
    {
        public Guid ChapterId { get; init; }
        public int BlockIndex { get; init; }
        public string Name { get; init; } = "Designed page";
        public long ExpectedRevision { get; init; }
        public DesignedPageLayoutMode LayoutMode { get; init; } = DesignedPageLayoutMode.SinglePage;
        public Guid? ImageId { get; init; }
        public string? AltText { get; init; }
        public bool Decorative { get; init; }
        public FigureImageFit ImageFit { get; init; } = FigureImageFit.Cover;
        public double CropXPercent { get; init; } = 50;
        public double CropYPercent { get; init; } = 50;
    }

    private static T ReadLegacyPayload<T>(string json, LegacyReviewChange change, string description)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException($"Legacy {description} change {change.Id:N} has no payload.");
        try
        {
            return JsonSerializer.Deserialize<T>(json, LegacyReviewPayloadJsonOptions)
                ?? throw new InvalidDataException($"Legacy {description} change {change.Id:N} has a null payload.");
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException)
        {
            throw new InvalidDataException($"Legacy {description} change {change.Id:N} has an invalid payload.", exception);
        }
    }

    private static T? ReadLegacyOptionalPayload<T>(string json, LegacyReviewChange change, string description)
    {
        if (string.IsNullOrWhiteSpace(json))
            return default;
        try
        {
            return JsonSerializer.Deserialize<T>(json, LegacyReviewPayloadJsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Legacy {description} change {change.Id:N} has an invalid payload.", exception);
        }
    }

    private static async Task MaterializeLegacyManuscriptChangeAsync(
        AppDbContext db,
        LegacyReviewChange change,
        string effectiveJson,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(effectiveJson))
            throw new InvalidDataException($"Legacy review change {change.Id:N} has no proposed manuscript.");

        ChapterManuscriptChange proposed;
        try
        {
            proposed = JsonSerializer.Deserialize<ChapterManuscriptChange>(
                effectiveJson,
                ManuscriptCodec.JsonOptions)
                ?? throw new InvalidDataException("The proposed manuscript payload is null.");
            _ = proposed.Manuscript;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException)
        {
            throw new InvalidDataException(
                $"Legacy review change {change.Id:N} has an invalid proposed manuscript.",
                exception);
        }

        var resourceId = ParseLegacyResourceGuid(change);
        if (resourceId != proposed.Id)
        {
            throw new InvalidDataException(
                $"Legacy review change {change.Id:N} resource {resourceId:N} does not match its proposed manuscript {proposed.Id:N}.");
        }

        var beforeRevision = proposed.Revision - 1;
        if (!string.IsNullOrWhiteSpace(change.BeforeJson))
        {
            try
            {
                var before = JsonSerializer.Deserialize<ChapterManuscriptChange>(
                    change.BeforeJson,
                    ManuscriptCodec.JsonOptions);
                if (before is not null)
                    beforeRevision = before.Revision;
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    $"Legacy review change {change.Id:N} has an invalid source manuscript.",
                    exception);
            }
        }

        ManuscriptCodec.Validate(proposed.Manuscript, proposed.Id, proposed.Revision);
        if (!await db.Chapters.AsNoTracking()
                .AnyAsync(chapter => chapter.Id == proposed.Id && chapter.ProjectId == change.ProjectId, cancellationToken))
        {
            throw new InvalidDataException(
                $"Legacy review change {change.Id:N} references a chapter that does not belong to project {change.ProjectId:N}.");
        }
        if (string.Equals(change.ContentTargetKind, "Edition", StringComparison.OrdinalIgnoreCase))
        {
            if (change.ContentTargetEditionId is not Guid editionId)
                throw new InvalidDataException($"Legacy review change {change.Id:N} has no edition target.");
            if (!await db.PublicationEditionChapterOverrides.AsNoTracking()
                    .AnyAsync(overrideRow => overrideRow.EditionId == editionId && overrideRow.ChapterId == proposed.Id, cancellationToken))
            {
                throw new InvalidDataException(
                    $"Legacy review change {change.Id:N} references a missing edition manuscript target.");
            }
            var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE PublicationEditionChapterOverrides
                SET ManuscriptJson = {proposed.ManuscriptJson},
                    Revision = {proposed.Revision}, UpdatedAt = {DateTime.UtcNow}
                WHERE EditionId = {editionId} AND ChapterId = {proposed.Id}
                  AND Revision = {beforeRevision};
                """, cancellationToken);
            if (updated != 1)
                throw new InvalidDataException($"Legacy review change {change.Id:N} no longer matches its edition source revision.");
        }
        else
        {
            var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE Chapters
                SET ManuscriptJson = {proposed.ManuscriptJson},
                    ManuscriptRevision = {proposed.Revision}, UpdatedAt = {DateTime.UtcNow}
                WHERE Id = {proposed.Id} AND ProjectId = {change.ProjectId}
                  AND ManuscriptRevision = {beforeRevision};
                """, cancellationToken);
            if (updated != 1)
                throw new InvalidDataException($"Legacy review change {change.Id:N} no longer matches its chapter source revision.");
        }
    }

    private static IReadOnlyList<Guid> ReadLegacyGuidList(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<Guid>>(json) ?? [];
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Legacy review dependency data is malformed.", exception);
        }
    }

    private static Guid ParseLegacyResourceGuid(LegacyReviewChange change)
    {
        var parts = (change.ResourceId ?? string.Empty)
            .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var guidText = parts.Length == 0 ? change.ResourceId : parts[^1];
        if (!Guid.TryParse(guidText, out var result))
            throw new InvalidDataException(
                $"Legacy review change {change.Id:N} does not have a valid resource id.");
        return result;
    }

    private static bool IsPendingLegacyReviewChange(LegacyReviewChange change) =>
        string.Equals(change.Status, "Pending", StringComparison.OrdinalIgnoreCase);

    private static bool IsMaterializedLegacyReviewChange(LegacyReviewChange change) =>
        string.Equals(change.Status, "Applied", StringComparison.OrdinalIgnoreCase)
        || string.Equals(change.Status, "Superseded", StringComparison.OrdinalIgnoreCase)
        || string.Equals(change.Status, "Resolved", StringComparison.OrdinalIgnoreCase);

    private static bool IsDiscardedLegacyReviewChange(LegacyReviewChange change) =>
        string.Equals(change.Status, "Rejected", StringComparison.OrdinalIgnoreCase)
        || string.Equals(change.Status, "Conflict", StringComparison.OrdinalIgnoreCase);

    private static async Task ValidateLegacyTargetOwnershipAsync(
        AppDbContext db,
        LegacyReviewChange change,
        CancellationToken cancellationToken)
    {
        if (!await db.Projects.AsNoTracking().AnyAsync(project => project.Id == change.ProjectId, cancellationToken))
            throw new InvalidDataException(
                $"Legacy review change {change.Id:N} references a missing project {change.ProjectId:N}.");
        if (string.Equals(change.ContentTargetKind, "Core", StringComparison.OrdinalIgnoreCase))
            return;
        if (!string.Equals(change.ContentTargetKind, "Edition", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Legacy review change {change.Id:N} has an unsupported content target '{change.ContentTargetKind}'.");

        if (change.ContentTargetEditionId is not Guid editionId
            || !await db.PublicationEditions.AsNoTracking()
                .AnyAsync(edition => edition.Id == editionId && edition.ProjectId == change.ProjectId, cancellationToken))
        {
            throw new InvalidDataException(
                $"Legacy review change {change.Id:N} references an edition that does not belong to project {change.ProjectId:N}.");
        }
    }

    private static async Task ValidateLegacyResourceOwnershipAsync(
        AppDbContext db,
        LegacyReviewChange change,
        string effectiveJson,
        CancellationToken cancellationToken)
    {
        switch (change.ToolName)
        {
            case "update_act":
            case "delete_act":
            {
                var id = change.ToolName == "delete_act"
                    ? ParseLegacyResourceGuid(change)
                    : ReadLegacyPayload<OutlineActChange>(effectiveJson, change, "act").Id;
                if (!await db.Acts.AsNoTracking().AnyAsync(item => item.Id == id && item.ProjectId == change.ProjectId, cancellationToken))
                    throw new InvalidDataException($"Legacy act change {change.Id:N} references an act outside its project.");
                break;
            }
            case "create_act":
            {
                var after = ReadLegacyPayload<OutlineActChange>(effectiveJson, change, "act");
                if (await db.Acts.AsNoTracking().AnyAsync(item => item.Id == after.Id, cancellationToken))
                    throw new InvalidDataException($"Legacy act change {change.Id:N} reuses an existing act id.");
                break;
            }
            case "create_chapter":
            {
                var after = ReadLegacyPayload<OutlineChapterChange>(effectiveJson, change, "chapter");
                if (await db.Chapters.AsNoTracking().AnyAsync(item => item.Id == after.Id, cancellationToken))
                    throw new InvalidDataException($"Legacy chapter change {change.Id:N} reuses an existing chapter id.");
                if (after.ActId is Guid actId
                    && !await db.Acts.AsNoTracking().AnyAsync(item => item.Id == actId && item.ProjectId == change.ProjectId, cancellationToken))
                    throw new InvalidDataException($"Legacy chapter change {change.Id:N} references an act outside its project.");
                break;
            }
            case "update_chapter":
            case "delete_chapter":
            {
                var id = change.ToolName == "delete_chapter"
                    ? ParseLegacyResourceGuid(change)
                    : ReadLegacyPayload<OutlineChapterChange>(effectiveJson, change, "chapter").Id;
                var chapter = await db.Chapters.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
                if (chapter is null || chapter.ProjectId != change.ProjectId)
                    throw new InvalidDataException($"Legacy chapter change {change.Id:N} references a chapter outside its project.");
                if (change.ToolName == "update_chapter")
                {
                    var after = ReadLegacyPayload<OutlineChapterChange>(effectiveJson, change, "chapter");
                    if (after.ActId is Guid actId
                        && !await db.Acts.AsNoTracking().AnyAsync(item => item.Id == actId && item.ProjectId == change.ProjectId, cancellationToken))
                        throw new InvalidDataException($"Legacy chapter change {change.Id:N} references an act outside its project.");
                }
                break;
            }
            case "reorder_acts":
            {
                var after = ReadLegacyPayload<OutlineReorderChange>(effectiveJson, change, "act ordering");
                var ids = after.OrderedIds.Distinct().ToArray();
                if (ids.Length != after.OrderedIds.Count
                    || await db.Acts.AsNoTracking().CountAsync(item => item.ProjectId == change.ProjectId && ids.Contains(item.Id), cancellationToken) != ids.Length
                    || await db.Acts.AsNoTracking().CountAsync(item => item.ProjectId == change.ProjectId, cancellationToken) != ids.Length)
                    throw new InvalidDataException($"Legacy act reorder {change.Id:N} does not contain exactly this project's acts.");
                break;
            }
            case "reorder_chapters":
            {
                var after = ReadLegacyPayload<OutlineReorderChange>(effectiveJson, change, "chapter ordering");
                if (after.ParentId is Guid parentId
                    && !await db.Acts.AsNoTracking().AnyAsync(item => item.Id == parentId && item.ProjectId == change.ProjectId, cancellationToken))
                    throw new InvalidDataException($"Legacy chapter reorder {change.Id:N} references an act outside its project.");
                var ids = after.OrderedIds.Distinct().ToArray();
                var query = db.Chapters.AsNoTracking().Where(item => item.ProjectId == change.ProjectId && ids.Contains(item.Id));
                var owned = await query.ToListAsync(cancellationToken);
                if (ids.Length != after.OrderedIds.Count || owned.Count != ids.Length
                    || owned.Any(item => item.ActId != after.ParentId))
                    throw new InvalidDataException($"Legacy chapter reorder {change.Id:N} does not contain exactly the requested chapter bucket.");
                var bucketCount = await db.Chapters.AsNoTracking()
                    .CountAsync(item => item.ProjectId == change.ProjectId && item.ActId == after.ParentId, cancellationToken);
                if (bucketCount != ids.Length)
                    throw new InvalidDataException($"Legacy chapter reorder {change.Id:N} omits chapters from its bucket.");
                break;
            }
        }
    }

    private sealed record LegacyReviewChange(
        Guid Id,
        string Status,
        string ToolName,
        string ArgumentsJson,
        string BeforeJson,
        string AfterJson,
        string? DraftAfterJson,
        string DependsOnChangeIdsJson,
        string? ResourceId,
        Guid ProjectId,
        string ContentTargetKind,
        Guid? ContentTargetEditionId);

    private async Task PrepareAuthoringHistoryCleanupAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        if (applied.Contains(AuthoringHistoryCleanupMigrationId))
            return;

        // The cleanup migration drops the retired authoring history tables.
        // Keep the schema transition behind the recovery boundary so an
        // interrupted startup can restore the source database.
        var backupPath = await recovery.CreateBackupAsync(
            "authoring",
            "pre-review-baseline-cleanup",
            cancellationToken);
        try
        {
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
                "authoring-history-cleanup-v1",
                29,
                31,
                exception,
                cancellationToken);
        }
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

    private static string HashManuscriptContent(ManuscriptDocument document) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(document.Content, ManuscriptCodec.JsonOptions))));

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

    internal static async Task RemovePublicationSectionStartSideCompatibilityColumnAsync(
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
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    internal static async Task EnsureRectoChapterStartsCompatibilityColumnsAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        if (applied.Contains(RectoChapterStartsMigrationId))
            return;

        (string Table, string AddSql)[] columns =
        [
            ("PublicationBooks", "ALTER TABLE \"PublicationBooks\" ADD COLUMN \"RectoChapterStarts\" INTEGER NOT NULL DEFAULT 0;"),
            ("PublicationEditions", "ALTER TABLE \"PublicationEditions\" ADD COLUMN \"RectoChapterStarts\" INTEGER NOT NULL DEFAULT 0;"),
        ];
        foreach (var (table, addSql) in columns)
        {
            if (await HasTableAsync(db, table, cancellationToken)
                && !await HasColumnAsync(db, table, "RectoChapterStarts", cancellationToken))
            {
                await db.Database.ExecuteSqlRawAsync(addSql, cancellationToken);
            }
        }
        db.ChangeTracker.Clear();
    }

    internal static async Task RemoveRectoChapterStartsCompatibilityColumnsAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        if (applied.Contains(RectoChapterStartsMigrationId))
            return;

        (string Table, string DropSql)[] columns =
        [
            ("PublicationBooks", "ALTER TABLE \"PublicationBooks\" DROP COLUMN \"RectoChapterStarts\";"),
            ("PublicationEditions", "ALTER TABLE \"PublicationEditions\" DROP COLUMN \"RectoChapterStarts\";"),
        ];
        foreach (var (table, dropSql) in columns)
        {
            if (await HasTableAsync(db, table, cancellationToken)
                && await HasColumnAsync(db, table, "RectoChapterStarts", cancellationToken))
            {
                await db.Database.ExecuteSqlRawAsync(dropSql, cancellationToken);
            }
        }
        db.ChangeTracker.Clear();
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

    internal static async Task RemovePublicationSectionOrderCompatibilityColumnAsync(
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
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $table";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$table";
        parameter.Value = table;
        command.Parameters.Add(parameter);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static async Task EnsureReviewPreferenceCompatibilityColumnAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (!await HasTableAsync(db, "Projects", cancellationToken)
            || await HasColumnAsync(db, "Projects", "ReviewEditsEnabled", cancellationToken)
            || !await HasColumnAsync(db, "Projects", "AiChangeApprovalEnabled", cancellationToken))
        {
            return;
        }

        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"Projects\" ADD COLUMN \"ReviewEditsEnabled\" INTEGER NOT NULL DEFAULT 0;",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE \"Projects\" SET \"ReviewEditsEnabled\" = \"AiChangeApprovalEnabled\";",
            cancellationToken);
        db.ChangeTracker.Clear();
    }

    internal static async Task RemoveReviewPreferenceCompatibilityColumnAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (!await HasColumnAsync(db, "Projects", "ReviewEditsEnabled", cancellationToken)
            || !await HasColumnAsync(db, "Projects", "AiChangeApprovalEnabled", cancellationToken))
        {
            return;
        }

        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"Projects\" DROP COLUMN \"ReviewEditsEnabled\";",
            cancellationToken);
        db.ChangeTracker.Clear();
    }

    internal static async Task EnsurePrintProductCompatibilityColumnsAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        if (applied.Contains(ArtifactOnlyPrintSettingsMigrationId)) return;
        (string Name, string Definition)[] columns =
        [
            ("GenericPrintTemplateJson", "TEXT NOT NULL DEFAULT ''"),
            ("PrintRegistryVersion", "TEXT NOT NULL DEFAULT ''"),
            ("PrintProductKey", "TEXT NOT NULL DEFAULT ''"),
            ("PrintArtifactRegistryVersion", "TEXT NOT NULL DEFAULT ''"),
            ("PrintArtifactProfileKey", "TEXT NOT NULL DEFAULT ''"),
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
        if (applied.Contains(PrintArtifactProfileMigrationService.AdditiveMigrationId))
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                UPDATE PublicationEditions
                SET PrintArtifactRegistryVersion = PrintRegistryVersion,
                    PrintArtifactProfileKey = PrintProductKey
                WHERE PrintArtifactRegistryVersion = '' OR PrintArtifactProfileKey = '';
                """,
                cancellationToken);
            db.ChangeTracker.Clear();
            return;
        }
        if (!await HasColumnAsync(db, "PublicationCoverDesigns", "SurfaceScenesJson", cancellationToken))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"PublicationCoverDesigns\" ADD COLUMN \"SurfaceScenesJson\" TEXT NOT NULL DEFAULT '{{}}';",
                cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE PublicationEditions
            SET PrintArtifactRegistryVersion = CASE WHEN Format IN ('Paperback','Hardcover') THEN '2026.08.1' ELSE '' END,
                PrintArtifactProfileKey = CASE
                    WHEN Format = 'Paperback' AND Vendor = 'AmazonKdp' AND Ink = 'Color' THEN 'kdp-pb-premium-color'
                    WHEN Format = 'Paperback' AND Vendor = 'AmazonKdp' AND Paper = 'Cream' THEN 'kdp-pb-bw-50-2500'
                    WHEN Format = 'Paperback' AND Vendor = 'AmazonKdp' THEN 'kdp-pb-bw-50-2252'
                    WHEN Format = 'Paperback' AND Vendor = 'IngramSpark' AND Ink = 'Color' THEN 'ingram-pb-premium70'
                    WHEN Format = 'Paperback' AND Vendor = 'IngramSpark' AND Paper = 'Cream' THEN 'ingram-pb-bw-50-2225'
                    WHEN Format = 'Paperback' AND Vendor = 'IngramSpark' THEN 'ingram-pb-bw-50-2009'
                    WHEN Format = 'Paperback' THEN 'generic-perfectbound-template'
                    WHEN Format = 'Hardcover' AND Vendor = 'AmazonKdp' THEN 'kdp-hc-bw-50-2252'
                    WHEN Format = 'Hardcover' AND Vendor = 'IngramSpark' THEN 'ingram-hc-case-bw-50-2009'
                    WHEN Format = 'Hardcover' THEN 'generic-casebound-template'
                    ELSE '' END
            WHERE PrintArtifactProfileKey = '';

            UPDATE PublicationEditions
            SET PrintRegistryVersion = PrintArtifactRegistryVersion,
                PrintProductKey = PrintArtifactProfileKey
            WHERE PrintRegistryVersion = '' OR PrintProductKey = '';
            """,
            cancellationToken);
        db.ChangeTracker.Clear();
    }

    internal static async Task RemovePrintProductCompatibilityColumnsAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        if (applied.Contains(PrintArtifactProfileMigrationService.AdditiveMigrationId)) return;
        foreach (var name in new[] { "GenericPrintTemplateJson", "PrintRegistryVersion", "PrintProductKey", "PrintArtifactRegistryVersion", "PrintArtifactProfileKey", "PrintFinish", "PrintCoverMode" })
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

    internal static async Task RemovePrintArtifactProfileCompatibilityColumnsAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        if (applied.Contains(ArtifactOnlyPrintSettingsMigrationId)
            || !await HasTableAsync(db, "PublicationEditions", cancellationToken))
            return;

        if (await HasColumnAsync(db, "PublicationEditions", "PrintRegistryVersion", cancellationToken)
            && await HasColumnAsync(db, "PublicationEditions", "PrintArtifactRegistryVersion", cancellationToken))
        {
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE PublicationEditions SET PrintRegistryVersion = PrintArtifactRegistryVersion, PrintProductKey = PrintArtifactProfileKey;",
                cancellationToken);
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"PublicationEditions\" DROP COLUMN \"PrintArtifactRegistryVersion\";",
                cancellationToken);
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"PublicationEditions\" DROP COLUMN \"PrintArtifactProfileKey\";",
                cancellationToken);
            db.ChangeTracker.Clear();
        }
    }

    internal static async Task EnsureBarnesAndNoblePrintCompatibilityColumnsAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        if (applied.Contains(BarnesAndNoblePrintMigrationId)) return;
        if (await HasTableAsync(db, "PublicationEditions", cancellationToken)
            && !await HasColumnAsync(db, "PublicationEditions", "PrintTemplateEvidenceJson", cancellationToken))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"PublicationEditions\" ADD COLUMN \"PrintTemplateEvidenceJson\" TEXT NOT NULL DEFAULT '';",
                cancellationToken);
        if (await HasTableAsync(db, "PublicationEditions", cancellationToken)
            && await HasColumnAsync(db, "PublicationEditions", "GenericPrintTemplateJson", cancellationToken))
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE \"PublicationEditions\" SET \"PrintTemplateEvidenceJson\" = \"GenericPrintTemplateJson\" WHERE \"PrintTemplateEvidenceJson\" = '';",
                cancellationToken);
        foreach (var (name, defaultValue) in new[]
                 {
                     ("PrintCoverSubmissionMode", 0),
                     ("PrintIdentifierMode", 2),
                     ("PrintProjectUse", 1),
                 })
        {
            if (!await HasTableAsync(db, "PublicationEditions", cancellationToken)) break;
            if (await HasColumnAsync(db, "PublicationEditions", name, cancellationToken)) continue;
#pragma warning disable EF1002
            await db.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE \"PublicationEditions\" ADD COLUMN \"{name}\" INTEGER NOT NULL DEFAULT {defaultValue};",
                cancellationToken);
#pragma warning restore EF1002
        }
        if (await HasTableAsync(db, "PublicationCoverDesigns", cancellationToken)
            && !await HasColumnAsync(db, "PublicationCoverDesigns", "SpineReadingDirection", cancellationToken))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"PublicationCoverDesigns\" ADD COLUMN \"SpineReadingDirection\" INTEGER NOT NULL DEFAULT 0;",
                cancellationToken);
        db.ChangeTracker.Clear();
    }

    internal static async Task RemoveBarnesAndNoblePrintCompatibilityColumnsAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        if (applied.Contains(BarnesAndNoblePrintMigrationId)) return;
        foreach (var (table, name) in new[]
                 {
                     ("PublicationEditions", "PrintTemplateEvidenceJson"),
                     ("PublicationEditions", "PrintCoverSubmissionMode"),
                     ("PublicationEditions", "PrintIdentifierMode"),
                     ("PublicationEditions", "PrintProjectUse"),
                     ("PublicationCoverDesigns", "SpineReadingDirection"),
                 })
        {
            if (!await HasColumnAsync(db, table, name, cancellationToken)) continue;
#pragma warning disable EF1002
            await db.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE \"{table}\" DROP COLUMN \"{name}\";",
                cancellationToken);
#pragma warning restore EF1002
        }
        db.ChangeTracker.Clear();
    }
}
