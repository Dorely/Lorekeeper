using Lorekeeper.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class DatabaseSchemaChangeGuardTests
{
    [Fact]
    public async Task BlockedGuardFailsClosedOnPendingOrUnknownMigrationsAndPassesOnExactMatch()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var connectionString = $"Data Source={Path.Combine(directory, "fixture.db")}";
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString)
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking).Options;
            var operations = new AppDatabaseOperationFactory(
                new TestDbContextFactory(options),
                new AppDatabaseWriteCoordinator(),
                new ProjectMutationCoordinator(connectionString));
            var blocked = new DatabaseSchemaChangeGuard(operations, blockSchemaMigrations: true);
            var allowed = new DatabaseSchemaChangeGuard(operations, blockSchemaMigrations: false);

            await using var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance);
            var migrations = db.Database.GetMigrations().ToList();
            await db.GetService<IMigrator>().MigrateAsync(migrations[^2]);

            var pending = await Assert.ThrowsAsync<InvalidOperationException>(
                () => blocked.EnsureSchemaUnchangedAsync());
            Assert.Contains(migrations[^1], pending.Message, StringComparison.Ordinal);
            Assert.Equal([migrations[^1]], await db.Database.GetPendingMigrationsAsync());
            await allowed.EnsureSchemaUnchangedAsync();

            await db.Database.MigrateAsync();
            await blocked.EnsureSchemaUnchangedAsync();

            const string futureMigration = "99991231000000_FutureRelease";
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ({0}, '0');",
                futureMigration);
            var unknown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => blocked.EnsureSchemaUnchangedAsync());
            Assert.Contains(futureMigration, unknown.Message, StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() =>
            new(options, NullLogger<AppDbContext>.Instance);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
