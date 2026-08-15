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

    public async Task<bool> ApplyAsync(CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        await EnsureAuthoringHistoryCompatibilityColumnsAsync(db, cancellationToken);
        await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);
        await manuscriptMigration.ApplyPendingAsync(db, cancellationToken);
        await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);
        await editionMigration.ApplyPendingAsync(db, cancellationToken);
        await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);
        await pressMigration.ApplyPendingAsync(db, cancellationToken);
        await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);
        await EnsurePrintProductCompatibilityColumnsAsync(db, cancellationToken);

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

        await EnsurePublicationSectionCompatibilityColumnsAsync(db, cancellationToken);
        await EnsurePrintProductCompatibilityColumnsAsync(db, cancellationToken);
        await authoringPageMigration.ApplyPendingAsync(db, cancellationToken);
        await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);
        await publicationCoreMigration.ApplyPendingAsync(db, cancellationToken);
        await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);

        await RemoveEditionCompatibilityColumnsAsync(db, cancellationToken);
        await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);
        await RemovePublicationSectionCompatibilityColumnsAsync(db, cancellationToken);
        await editionContentMigration.ApplyPendingAsync(db, cancellationToken);
        await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);
        await EnsurePrintProductCompatibilityColumnsAsync(db, cancellationToken);
        await RemovePublicationSectionCompatibilityColumnsAsync(db, cancellationToken);
        await publicationSectionMigration.ApplyPendingAsync(db, cancellationToken);
        await EnsurePublicationSectionOrderCompatibilityColumnAsync(db, cancellationToken);

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
        await db.GetService<IMigrator>().MigrateAsync(cancellationToken: cancellationToken);
        await publicationSectionMigration.RepairSemanticRevisionDriftAsync(db, cancellationToken);
        return !await recovery.IsRecoveryRequiredAsync(cancellationToken);
    }

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
