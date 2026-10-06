using Lorekeeper.Authoring;
using Lorekeeper.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class AuthoringFenceUnresponsiveWriterTests
{
    private static readonly TimeSpan TestBound = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task WriterThatNeverAcknowledgesResumeDoesNotWedgeLaterFencesOrRegistrations()
    {
        await WithFenceAsync(async fence =>
        {
            var projectId = Guid.NewGuid();
            var target = new AuthoringTargetReferenceV1(projectId, "chapter:lost");
            var sessionId = Guid.NewGuid();
            // A closed window's circuit flushes cleanly then never answers the resume call.
            await fence.RegisterWriterAsync(new(
                target,
                sessionId,
                Guid.NewGuid(),
                (sequence, _) => Task.FromResult(new AuthoringWriterFlushResult(sequence, false, true, true)),
                _ => new TaskCompletionSource().Task));

            var first = await fence.ExecuteAsync(
                new AuthoringFenceRequest(projectId, [], "first"),
                (_, _) => Task.FromResult(1)).WaitAsync(TestBound);
            Assert.Equal(1, first);

            var unreachable = await Assert.ThrowsAsync<AuthoringMutationFenceException>(() =>
                fence.ExecuteAsync(
                    new AuthoringFenceRequest(projectId, [], "second"),
                    (_, _) => Task.FromResult(2)).WaitAsync(TestBound));
            Assert.Equal("AUTHORING_CLIENT_UNREACHABLE", unreachable.Code);

            // The same session can reattach once its window reconnects or remounts.
            await using var reattached = await fence.RegisterWriterAsync(new(
                target,
                sessionId,
                Guid.NewGuid(),
                (sequence, _) => Task.FromResult(new AuthoringWriterFlushResult(sequence, false, true, true)),
                _ => Task.CompletedTask)).AsTask().WaitAsync(TestBound);
        });
    }

    [Fact]
    public async Task WriterThatNeverAnswersFlushFailsTheFenceInsteadOfHanging()
    {
        await WithFenceAsync(async fence =>
        {
            var projectId = Guid.NewGuid();
            await fence.RegisterWriterAsync(new(
                new AuthoringTargetReferenceV1(projectId, "chapter:silent"),
                Guid.NewGuid(),
                Guid.NewGuid(),
                (_, _) => new TaskCompletionSource<AuthoringWriterFlushResult>().Task,
                _ => Task.CompletedTask));

            var consumed = false;
            var failure = await Assert.ThrowsAsync<AuthoringMutationFenceException>(() =>
                fence.ExecuteAsync(
                    new AuthoringFenceRequest(projectId, [], "flush"),
                    (_, _) =>
                    {
                        consumed = true;
                        return Task.FromResult(0);
                    }).WaitAsync(TestBound));
            Assert.Equal("AUTHORING_FLUSH_FAILED", failure.Code);
            Assert.False(consumed);

            var next = await Assert.ThrowsAsync<AuthoringMutationFenceException>(() =>
                fence.ExecuteAsync(
                    new AuthoringFenceRequest(projectId, [], "next"),
                    (_, _) => Task.FromResult(0)).WaitAsync(TestBound));
            Assert.Equal("AUTHORING_CLIENT_UNREACHABLE", next.Code);
        });
    }

    private static async Task WithFenceAsync(Func<IAuthoringMutationFence, Task> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "lorekeeper-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "fence.db")}")
                .Options;
            await using (var setup = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
                await setup.Database.MigrateAsync();

            var projectMutations = new ProjectMutationCoordinator();
            var database = new AppDatabaseOperationFactory(
                new TestDbContextFactory(options),
                new AppDatabaseWriteCoordinator(),
                projectMutations);
            await test(new AuthoringMutationFence(
                database,
                projectMutations,
                new AuthoringDeltaHistoryRuntime(),
                TimeSpan.FromMilliseconds(250)));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options, NullLogger<AppDbContext>.Instance);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
