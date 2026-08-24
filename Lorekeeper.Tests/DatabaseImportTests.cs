using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class DatabaseImportTests
{
    [Fact]
    public async Task StagedImportIsAppliedAtStartupAndPreservesTheReplacedDatabase()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var sourcePath = Path.Combine(directory, "other-install.db");
            await CreateDatabaseWithProjectAsync(sourcePath, "Imported Project");

            var livePath = Path.Combine(directory, "live.db");
            await CreateDatabaseWithProjectAsync(livePath, "Original Project");

            var import = CreateImportService(livePath, out var recovery);
            DatabaseImportCandidate candidate;
            await using (var upload = File.OpenRead(sourcePath))
                candidate = await import.StageAsync(upload, DatabaseImportService.DefaultMaxImportBytes);

            Assert.Equal(1, candidate.ProjectCount);
            Assert.Contains("Imported Project", candidate.ProjectNames);
            Assert.Equal(0, candidate.PendingMigrationCount);
            Assert.True(File.Exists(candidate.StagedPath));

            var request = await import.PrepareImportAsync(candidate.StagedPath);
            await import.ScheduleImportAsync(candidate.StagedPath, request.ConfirmationToken);

            // The next startup applies the scheduled import before migrations or workers.
            Assert.True(await recovery.ApplyScheduledRestoreAsync());

            var liveNames = await ReadProjectNamesAsync($"Data Source={livePath}");
            Assert.Contains("Imported Project", liveNames);
            Assert.DoesNotContain("Original Project", liveNames);

            // The replaced database survives as a protected diagnostic backup.
            var recoveryBackups = Directory.EnumerateFiles(
                    Path.Combine(directory, DatabaseMigrationRecoveryService.BackupDirectoryName, "recovery"),
                    "*.db")
                .Where(path => path.Contains("diagnostic", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var preserved = false;
            foreach (var backup in recoveryBackups)
                preserved |= (await ReadProjectNamesAsync($"Data Source={backup};Mode=ReadOnly"))
                    .Contains("Original Project");
            Assert.True(preserved, "The replaced database must remain restorable from a protected backup.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ImportFailsClosedForForeignCorruptOversizedAndNewerDatabases()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var livePath = Path.Combine(directory, "live.db");
            await CreateDatabaseWithProjectAsync(livePath, "Original Project");
            var import = CreateImportService(livePath, out _);
            var markerPath = Path.Combine(
                directory,
                DatabaseMigrationRecoveryService.BackupDirectoryName,
                "scheduled-restore.json");

            // Not SQLite at all.
            var randomBytes = new byte[4096];
            Random.Shared.NextBytes(randomBytes);
            randomBytes[0] = 0x00; // Cannot accidentally match the SQLite magic.
            await using (var upload = new MemoryStream(randomBytes))
                await Assert.ThrowsAsync<InvalidDataException>(
                    () => import.StageAsync(upload, DatabaseImportService.DefaultMaxImportBytes));

            // A real SQLite database without the Lorekeeper schema.
            var foreignPath = Path.Combine(directory, "foreign.db");
            await using (var connection = new SqliteConnection($"Data Source={foreignPath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE Notes (Id INTEGER PRIMARY KEY, Body TEXT);"
                    + "INSERT INTO Notes (Body) VALUES ('padding');";
                await command.ExecuteNonQueryAsync();
            }
            await using (var upload = File.OpenRead(foreignPath))
            {
                var foreign = await Assert.ThrowsAsync<InvalidDataException>(
                    () => import.StageAsync(upload, DatabaseImportService.DefaultMaxImportBytes));
                Assert.Contains("no Lorekeeper schema", foreign.Message, StringComparison.Ordinal);
            }

            // A database from a newer Lorekeeper with an unknown migration.
            var futurePath = Path.Combine(directory, "future.db");
            await CreateDatabaseWithProjectAsync(futurePath, "Future Project");
            await using (var connection = new SqliteConnection($"Data Source={futurePath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    "INSERT INTO \"__EFMigrationsHistory\" (MigrationId, ProductVersion)"
                    + " VALUES ('99990101000000_FromTheFuture', '99.0.0');";
                await command.ExecuteNonQueryAsync();
            }
            await using (var upload = File.OpenRead(futurePath))
            {
                var newer = await Assert.ThrowsAsync<InvalidDataException>(
                    () => import.StageAsync(upload, DatabaseImportService.DefaultMaxImportBytes));
                Assert.Contains("newer Lorekeeper version", newer.Message, StringComparison.Ordinal);
            }

            // An upload beyond the size bound.
            await using (var upload = File.OpenRead(futurePath))
                await Assert.ThrowsAsync<InvalidDataException>(
                    () => import.StageAsync(upload, maxBytes: 2048));

            // Only the staged candidate path is schedulable.
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => import.PrepareImportAsync(livePath));

            // Every failure leaves the live database untouched and nothing scheduled.
            Assert.False(File.Exists(markerPath));
            Assert.Contains("Original Project", await ReadProjectNamesAsync($"Data Source={livePath}"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static IDatabaseImportService CreateImportService(
        string livePath,
        out DatabaseMigrationRecoveryService recovery)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = $"Data Source={livePath}",
            })
            .Build();
        recovery = new DatabaseMigrationRecoveryService(
            configuration,
            NullLogger<DatabaseMigrationRecoveryService>.Instance);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={livePath}")
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options;
        var database = new AppDatabaseOperationFactory(
            new TestDbContextFactory(options),
            new AppDatabaseWriteCoordinator(),
            new ProjectMutationCoordinator());
        return new DatabaseImportService(
            configuration,
            database,
            recovery,
            NullLogger<DatabaseImportService>.Instance);
    }

    private static async Task CreateDatabaseWithProjectAsync(string path, string projectName)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;
        await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
        {
            await db.Database.MigrateAsync();
            db.Projects.Add(new Project
            {
                Name = projectName,
                Slug = projectName.ToLowerInvariant().Replace(' ', '-'),
            });
            await db.SaveChangesAsync();
        }
        SqliteConnection.ClearAllPools();
    }

    private static async Task<IReadOnlyList<string>> ReadProjectNamesAsync(string connectionString)
    {
        var names = new List<string>();
        await using var connection = new SqliteConnection(connectionString + ";Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Name FROM \"Projects\";";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(0));
        return names;
    }

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options, NullLogger<AppDbContext>.Instance);
    }
}
