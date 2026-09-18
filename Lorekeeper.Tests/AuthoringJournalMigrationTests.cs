using Lorekeeper.Authoring;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class AuthoringJournalMigrationTests
{
    private const string PreviousMigration = "20260917140001_FinalizeDesignedPagesM2";

    [Fact]
    public async Task PopulatedPriorDatabaseMigratesToDurableAuthoringJournal()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "authoring-journal.db")}")
                .Options;
            var projectId = Guid.NewGuid();

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
                var now = DateTime.UtcNow;
                await db.Database.ExecuteSqlInterpolatedAsync($$"""
                    INSERT INTO Projects
                        (Id, ReviewEditsEnabled, ContestModeEnabled, CreatedAt,
                         IncludeCurrentChapterInContext, Name, ProjectGuidance, Slug, UpdatedAt)
                    VALUES
                        ({{projectId}}, 1, 0, {{now}}, 1,
                         {{"Authoring journal migration fixture"}}, {{string.Empty}}, {{$"authoring-journal-{projectId:N}"}}, {{now}})
                    """);
            }

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
                await db.Database.MigrateAsync();

            var sessionId = Guid.NewGuid();
            var batchId = Guid.NewGuid();
            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                db.AuthoringSessions.Add(new AuthoringSession
                {
                    Id = sessionId,
                    ProjectId = projectId,
                    ProcessIncarnationId = Guid.NewGuid(),
                    LastSequence = 7,
                });
                db.AuthoringBatchReceipts.Add(new AuthoringBatchReceipt
                {
                    ProjectId = projectId,
                    SessionId = sessionId,
                    BatchId = batchId,
                    Sequence = 7,
                    RequestHash = "sha256:fixture",
                    ResultJson = "{}",
                });
                db.AuthoringTargetGenerations.Add(new AuthoringTargetGeneration
                {
                    ProjectId = projectId,
                    TargetId = $"chapter:{Guid.NewGuid():D}",
                    Generation = 3,
                });
                await db.SaveChangesAsync();
            }

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                Assert.Equal("Authoring journal migration fixture", (await db.Projects.SingleAsync()).Name);
                Assert.Equal(7, (await db.AuthoringSessions.SingleAsync()).LastSequence);
                Assert.Equal(batchId, (await db.AuthoringBatchReceipts.SingleAsync()).BatchId);
                Assert.Equal(3, (await db.AuthoringTargetGenerations.SingleAsync()).Generation);
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReceiptAcknowledgementRetainsTheDurableReceiptAndFailsClosedOnHashMismatch()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var databasePath = Path.Combine(directory, "receipt-ack.db");
            var connectionString = $"Data Source={databasePath}";
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connectionString)
                .Options;
            var projectId = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
            var batchId = Guid.NewGuid();
            var receiptId = Guid.NewGuid();
            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.Database.MigrateAsync();
                db.Projects.Add(new Project
                {
                    Id = projectId,
                    Name = "Receipt acknowledgement",
                    Slug = $"receipt-{projectId:N}",
                });
                db.AuthoringSessions.Add(new AuthoringSession
                {
                    Id = sessionId,
                    ProjectId = projectId,
                    ProcessIncarnationId = Guid.NewGuid(),
                });
                db.AuthoringBatchReceipts.Add(new AuthoringBatchReceipt
                {
                    Id = receiptId,
                    ProjectId = projectId,
                    SessionId = sessionId,
                    BatchId = batchId,
                    Sequence = 1,
                    RequestHash = "sha256:receipt",
                    ResultJson = "{}",
                });
                await db.SaveChangesAsync();
            }

            var operations = new AppDatabaseOperationFactory(
                new TestDbContextFactory(options),
                new AppDatabaseWriteCoordinator(),
                new ProjectMutationCoordinator(connectionString));
            var service = new AuthoringBatchService(
                operations,
                null!,
                null!,
                null!,
                null!,
                NullLogger<AuthoringBatchService>.Instance);
            await Assert.ThrowsAsync<AuthoringIdempotencyException>(() => service.AcknowledgeReceiptAsync(
                new(projectId, sessionId, batchId, receiptId, "sha256:different")));
            await service.AcknowledgeReceiptAsync(
                new(projectId, sessionId, batchId, receiptId, "sha256:receipt"));

            await using var verify = new AppDbContext(options, NullLogger<AppDbContext>.Instance);
            var retained = await verify.AuthoringBatchReceipts.SingleAsync();
            Assert.Equal(receiptId, retained.Id);
            Assert.NotNull(retained.AcknowledgedAt);
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
