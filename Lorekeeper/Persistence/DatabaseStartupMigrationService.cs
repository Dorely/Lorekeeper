using Lorekeeper.Manuscripts;
using Lorekeeper.Publish;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Lorekeeper.Persistence;

public interface IDatabaseStartupMigrationService
{
    Task<bool> ApplyAsync(CancellationToken cancellationToken = default);
}

public sealed class DatabaseStartupMigrationService(
    AppDbContext db,
    IManuscriptMigrationService manuscriptMigration,
    IPublicationEditionMigrationService editionMigration,
    IPublicationPressMigrationService pressMigration,
    IVisualCompositionMigrationService visualCompositionMigration,
    IAuthoringPageMigrationService authoringPageMigration,
    IPublicationCoreMigrationService publicationCoreMigration,
    IEditionContentMigrationService editionContentMigration,
    IDatabaseMigrationRecoveryService recovery) : IDatabaseStartupMigrationService
{
    public async Task<bool> ApplyAsync(CancellationToken cancellationToken = default)
    {
        await manuscriptMigration.ApplyPendingAsync(db, cancellationToken);
        await editionMigration.ApplyPendingAsync(db, cancellationToken);
        await pressMigration.ApplyPendingAsync(db, cancellationToken);

        var appliedMigrations = (await db.Database.GetAppliedMigrationsAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        if (!appliedMigrations.Contains(VisualCompositionMigrationService.CleanupMigrationId))
        {
            await db.GetService<IMigrator>().MigrateAsync(
                VisualCompositionMigrationService.AdditiveMigrationId,
                cancellationToken);
            await EnsureEditionCompatibilityColumnsAsync(db, cancellationToken);
            await visualCompositionMigration.ApplyPendingAsync(db, cancellationToken);
            if (!await recovery.IsRecoveryRequiredAsync(cancellationToken))
                await visualCompositionMigration.ApplyFinalSchemaAsync(db, cancellationToken);
        }
        else
        {
            await EnsureEditionCompatibilityColumnsAsync(db, cancellationToken);
            await visualCompositionMigration.ApplyFinalSchemaAsync(db, cancellationToken);
            await visualCompositionMigration.ApplyPendingAsync(db, cancellationToken);
        }
        // Visual cleanup rebuilds several tables from its historical model and
        // therefore intentionally drops future compatibility columns.
        await EnsureEditionCompatibilityColumnsAsync(db, cancellationToken);

        var migrationsBeforeAuthoring = (await db.Database.GetAppliedMigrationsAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        if (!migrationsBeforeAuthoring.Contains(PublicationCoreMigrationService.SchemaMigrationId))
            await db.GetService<IMigrator>().MigrateAsync(
                PublicationCoreMigrationService.SchemaMigrationId,
                cancellationToken);

        await authoringPageMigration.ApplyPendingAsync(db, cancellationToken);
        await publicationCoreMigration.ApplyPendingAsync(db, cancellationToken);

        await RemoveEditionCompatibilityColumnsAsync(db, cancellationToken);
        await editionContentMigration.ApplyPendingAsync(db, cancellationToken);

        if (await recovery.IsRecoveryRequiredAsync(cancellationToken))
            return false;

        var migrationsBeforeCleanup = (await db.Database.GetAppliedMigrationsAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        if (!migrationsBeforeCleanup.Contains(PublicationCoreMigrationService.CleanupMigrationId))
            await db.GetService<IMigrator>().MigrateAsync(
                PublicationCoreMigrationService.CleanupMigrationId,
                cancellationToken);
        await db.GetService<IMigrator>().MigrateAsync(cancellationToken: cancellationToken);
        return true;
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
}
