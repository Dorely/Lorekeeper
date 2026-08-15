using Lorekeeper.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class DatabaseMigrationRecoveryTests
{
    [Fact]
    public async Task RecoveryStateNamesTheFailedMigrationAndProtectedBackup()
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
}
