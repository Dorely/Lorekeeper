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
            await visualCompositionMigration.ApplyPendingAsync(db, cancellationToken);
            if (!await recovery.IsRecoveryRequiredAsync(cancellationToken))
                await visualCompositionMigration.ApplyFinalSchemaAsync(db, cancellationToken);
        }
        else
        {
            await visualCompositionMigration.ApplyFinalSchemaAsync(db, cancellationToken);
            await visualCompositionMigration.ApplyPendingAsync(db, cancellationToken);
        }

        var migrationsBeforeAuthoring = (await db.Database.GetAppliedMigrationsAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        if (!migrationsBeforeAuthoring.Contains(PublicationCoreMigrationService.SchemaMigrationId))
            await db.GetService<IMigrator>().MigrateAsync(
                PublicationCoreMigrationService.SchemaMigrationId,
                cancellationToken);

        await authoringPageMigration.ApplyPendingAsync(db, cancellationToken);
        await publicationCoreMigration.ApplyPendingAsync(db, cancellationToken);

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
}
