using Lorekeeper.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class DatabaseMigrationRecoveryTests
{
    [Theory]
    [InlineData("20260813010000_RemoveLegacyPrintProductColumnsV28")]
    [InlineData("20260821100100_RemovePersistentAuthoringHistory")]
    public async Task RecoveryFromHistoricalStartupPreservesBackupAndOriginalFailure(string migration)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var connectionString = $"Data Source={Path.Combine(directory, "fixture.db")}";
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString)
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking).Options;
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = connectionString,
                }).Build();
            var recovery = new DatabaseMigrationRecoveryService(
                configuration, NullLogger<DatabaseMigrationRecoveryService>.Instance);
            await using var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance);
            await db.GetService<IMigrator>().MigrateAsync(migration);
            var projectId = Guid.NewGuid();
            await LegacyProjectSeed.InsertAsync(db, projectId, "Preserved project", "preserved", "Preserved guidance");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Projects ADD COLUMN ReviewEditsEnabled INTEGER NOT NULL DEFAULT 0;");
            await db.Database.ExecuteSqlRawAsync("UPDATE Projects SET ReviewEditsEnabled = AiChangeApprovalEnabled;");
            await DatabaseStartupMigrationService.EnsureAuthoringHistoryCompatibilityColumnsAsync(db, default);
            await DatabaseStartupMigrationService.EnsurePublicationSectionOrderCompatibilityColumnAsync(db, default);
            await DatabaseStartupMigrationService.EnsurePublicationSectionStartSideCompatibilityColumnAsync(db, default);
            await DatabaseStartupMigrationService.EnsureRectoChapterStartsCompatibilityColumnsAsync(db, default);
            await DatabaseStartupMigrationService.EnsurePrintProductCompatibilityColumnsAsync(db, default);
            await DatabaseStartupMigrationService.EnsureBarnesAndNoblePrintCompatibilityColumnsAsync(db, default);
            var backup = await recovery.CreateBackupAsync("manuscripts", "pre-startup-failure");

            await recovery.EnterRecoveryModeAsync(db, backup, "fixture-cutover", 29, 32,
                new InvalidDataException("Original migration validation failure"));

            var state = await recovery.GetStateAsync();
            Assert.True(state.RecoveryRequired);
            Assert.Equal("InvalidDataException: Original migration validation failure", state.Error);
            Assert.Equal(backup, state.BackupPath);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Empty(await db.Projects.ToListAsync());
            await using var preserved = new SqliteConnection($"Data Source={backup};Mode=ReadOnly");
            await preserved.OpenAsync();
            await using var command = preserved.CreateCommand();
            command.CommandText = "SELECT Id, Name, ProjectGuidance, AiChangeApprovalEnabled, ReviewEditsEnabled FROM Projects;";
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(projectId, Guid.Parse(reader.GetString(0)));
            Assert.Equal("Preserved project", reader.GetString(1));
            Assert.Equal("Preserved guidance", reader.GetString(2));
            Assert.Equal(1, reader.GetInt32(3));
            Assert.Equal(1, reader.GetInt32(4));
            Assert.False(await reader.ReadAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RecoveryStateNamesTheFailureAndStopsStartupBeforeOpeningTheRecoveryShell()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var connectionString = $"Data Source={Path.Combine(directory, "fixture.db")}";
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString)
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking).Options;
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = connectionString,
                })
                .Build();
            var recovery = new DatabaseMigrationRecoveryService(
                configuration,
                NullLogger<DatabaseMigrationRecoveryService>.Instance);
            await using var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance);
            await db.Database.MigrateAsync();
            var backup = await recovery.CreateBackupAsync("editions", "pre-editions");

            await recovery.EnterRecoveryModeAsync(
                db,
                backup,
                "publication-editions-v10",
                9,
                10,
                new InvalidDataException("hostile fixture"));

            var state = await recovery.GetStateAsync();
            Assert.True(state.RecoveryRequired);
            Assert.Equal("publication-editions-v10", state.MigrationName);
            Assert.Equal(backup, state.BackupPath);
            Assert.Contains("hostile fixture", state.Error, StringComparison.Ordinal);

            var startup = new DatabaseStartupMigrationService(
                new ThrowingDatabaseOperationFactory(),
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                recovery);

            Assert.False(await startup.ApplyAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PruningPreservesAnOldBackupReferencedByAnIncompleteMigration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var connectionString = $"Data Source={Path.Combine(directory, "fixture.db")}";
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString)
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking).Options;
            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
                await db.Database.EnsureCreatedAsync();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = connectionString,
                })
                .Build();
            var recovery = new DatabaseMigrationRecoveryService(
                configuration,
                NullLogger<DatabaseMigrationRecoveryService>.Instance);
            var backups = new List<string>();
            for (var index = 0; index < 7; index++)
            {
                var path = await recovery.CreateBackupAsync("manuscripts", $"checkpoint-{index}");
                File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(index - 10));
                backups.Add(path);
            }
            var protectedOldest = backups[0];

            await recovery.PruneAutomaticBackupsAsync(
                "manuscripts",
                maximum: 5,
                [protectedOldest]);

            Assert.True(File.Exists(protectedOldest));
            Assert.Equal(6, Directory.EnumerateFiles(
                Path.GetDirectoryName(protectedOldest)!,
                "*.db",
                SearchOption.TopDirectoryOnly).Count());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class ThrowingDatabaseOperationFactory : IAppDatabaseOperationFactory
    {
        public AppDatabaseReadOperation OpenRead() => throw UnexpectedOpen();

        public AppDatabaseWriteOperation OpenWrite() => throw UnexpectedOpen();

        public ValueTask<AppDatabaseReadOperation> OpenReadAsync(
            CancellationToken cancellationToken = default) => throw UnexpectedOpen();

        public ValueTask<AppDatabaseWriteOperation> OpenWriteAsync(
            CancellationToken cancellationToken = default) => throw UnexpectedOpen();

        public ValueTask<AppDatabaseWriteOperation> OpenWriteAsync(
            Guid projectId,
            CancellationToken cancellationToken = default) => throw UnexpectedOpen();

        private static InvalidOperationException UnexpectedOpen() =>
            new("Startup opened a recovery-shell database before honoring its recovery marker.");
    }
}
