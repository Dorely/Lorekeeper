using System.Security.Cryptography;
using Lorekeeper.ImportExport;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Publish;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lorekeeper.Tests;

public sealed class PublicationRenderTests
{
    [Fact]
    public async Task WorkerTreatsHostShutdownCancellationAsNormalCompletion()
    {
        await using var fixture = await RenderWorkerFixture.CreateAsync();
        using var worker = new PublicationRenderWorker(
            fixture.Queue,
            fixture.Provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PublicationRenderWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await worker.StopAsync(timeout.Token);
        var execution = worker.ExecuteTask
            ?? throw new InvalidOperationException("The publication render worker did not start.");
        await execution.WaitAsync(timeout.Token);

        Assert.True(execution.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ActiveRenderRemainsRecoverableWhenTheHostStops()
    {
        var publishing = new BlockingPublishService();
        await using var fixture = await RenderWorkerFixture.CreateAsync(publishing);
        var jobId = await fixture.AddJobAsync(PublicationRenderStatus.Queued);
        await fixture.Queue.EnqueueAsync(jobId, CancellationToken.None);
        using var worker = new PublicationRenderWorker(
            fixture.Queue,
            fixture.Provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PublicationRenderWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await publishing.Entered.WaitAsync(timeout.Token);

        await worker.StopAsync(timeout.Token);
        await (worker.ExecuteTask
            ?? throw new InvalidOperationException("The publication render worker did not start."))
            .WaitAsync(timeout.Token);
        await fixture.Queue.Completed.WaitAsync(timeout.Token);

        var job = await fixture.GetJobAsync(jobId);
        Assert.Equal(PublicationRenderStatus.Rendering, job.Status);
        Assert.False(job.CancellationRequested);
        Assert.Null(job.CompletedAt);
    }

    [Fact]
    public async Task QueuedCancellationBeforeRegistrationRemainsCancelled()
    {
        await using var fixture = await RenderWorkerFixture.CreateAsync();
        var jobId = await fixture.AddJobAsync(
            PublicationRenderStatus.Cancelled,
            cancellationRequested: true);
        await fixture.Queue.EnqueueAsync(jobId, CancellationToken.None);
        fixture.Queue.Cancel(jobId);
        using var worker = new PublicationRenderWorker(
            fixture.Queue,
            fixture.Provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PublicationRenderWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await fixture.Queue.Completed.WaitAsync(timeout.Token);

        await worker.StopAsync(timeout.Token);
        await (worker.ExecuteTask
            ?? throw new InvalidOperationException("The publication render worker did not start."))
            .WaitAsync(timeout.Token);

        var job = await fixture.GetJobAsync(jobId);
        Assert.Equal(PublicationRenderStatus.Cancelled, job.Status);
        Assert.True(job.CancellationRequested);
        Assert.NotNull(job.CompletedAt);
        Assert.NotEqual("Render failed", job.ProgressMessage);
    }

    [Fact]
    public async Task QueueCancellationAndCompletionHaveAnAtomicLifecycle()
    {
        var queue = new PublicationRenderQueue();
        var jobId = Guid.NewGuid();
        _ = queue.Register(jobId);

        await Task.WhenAll(
            Task.Run(() => queue.Cancel(jobId)),
            Task.Run(() => queue.Complete(jobId)));

        queue.Cancel(jobId);
        var replacement = queue.Register(jobId);
        Assert.False(replacement.IsCancellationRequested);
        queue.Complete(jobId);
    }

    [Fact]
    public async Task CorruptedStoredArtifactCannotCrossTheDownloadBoundary()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance);
        await db.Database.EnsureCreatedAsync();
        var project = new Project { Name = "Book", Slug = $"book-{Guid.NewGuid():N}" };
        var edition = new PublicationEdition { ProjectId = project.Id, Name = "Paperback" };
        var validData = "%PDF-valid"u8.ToArray();
        var valid = new PublicationArtifact
        {
            EditionId = edition.Id,
            Kind = PublicationArtifactKind.InteriorPdf,
            FileName = "valid.pdf",
            MediaType = "application/pdf",
            Data = validData,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(validData)),
            ByteLength = validData.Length,
        };
        var corrupt = new PublicationArtifact
        {
            EditionId = edition.Id,
            Kind = PublicationArtifactKind.CoverPdf,
            FileName = "corrupt.pdf",
            MediaType = "application/pdf",
            Data = "%PDF-corrupt"u8.ToArray(),
            Sha256 = valid.Sha256,
            ByteLength = valid.ByteLength,
        };
        db.AddRange(project, edition, valid, corrupt);
        await db.SaveChangesAsync();
        var service = new PublicationRenderService(db, null!, null!, null!, null!);

        Assert.NotNull(await service.GetArtifactAsync(project.Id, valid.Id));
        Assert.Null(await service.GetArtifactAsync(project.Id, corrupt.Id));
        Assert.Null(await service.GetArtifactAsync(Guid.NewGuid(), valid.Id));
    }

    [Fact]
    public void ArtifactStalenessUsesTheCurrentEditionFingerprint()
    {
        var job = new PublicationRenderJob
        {
            EditionId = Guid.NewGuid(),
            SourceFingerprint = "rendered-source",
            Status = PublicationRenderStatus.Completed,
        };
        var artifact = new PublicationArtifact
        {
            EditionId = job.EditionId,
            RenderJobId = job.Id,
            Kind = PublicationArtifactKind.InteriorPdf,
            FileName = "interior.pdf",
            MediaType = "application/pdf",
            Sha256 = new string('a', 64),
            ByteLength = 42,
            SourceFingerprint = job.SourceFingerprint,
        };

        var current = PublicationRenderService.View(job, [artifact], "rendered-source");
        var stale = PublicationRenderService.View(job, [artifact], "changed-source");

        Assert.False(Assert.Single(current.Artifacts).IsStale);
        Assert.True(Assert.Single(stale.Artifacts).IsStale);
    }

    [Fact]
    public void InvalidStoredDiagnosticsFailClosed()
    {
        var job = new PublicationRenderJob
        {
            EditionId = Guid.NewGuid(),
            DiagnosticsJson = "{not-json",
        };

        var view = PublicationRenderService.View(job, [], job.SourceFingerprint);

        var diagnostic = Assert.Single(view.Diagnostics);
        Assert.Equal("PRESS_DIAGNOSTICS_INVALID", diagnostic.Code);
        Assert.Equal("error", diagnostic.Severity);
    }

    private sealed class RenderWorkerFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<AppDbContext> _options;

        private RenderWorkerFixture(
            SqliteConnection connection,
            DbContextOptions<AppDbContext> options,
            ServiceProvider provider,
            RecordingRenderQueue queue)
        {
            _connection = connection;
            _options = options;
            Provider = provider;
            Queue = queue;
        }

        public ServiceProvider Provider { get; }
        public RecordingRenderQueue Queue { get; }

        public static async Task<RenderWorkerFixture> CreateAsync(
            IPublishService? publishing = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
            var queue = new RecordingRenderQueue();
            var services = new ServiceCollection()
                .AddScoped(_ => new AppDbContext(options, NullLogger<AppDbContext>.Instance))
                .AddScoped(serviceProvider => new PublicationRenderProcessor(
                    serviceProvider.GetRequiredService<AppDbContext>(),
                    publishing ?? new BlockingPublishService(),
                    null!,
                    null!,
                    Options.Create(new PublicationPressOptions()),
                    null!));
            var provider = services.BuildServiceProvider();
            await using (var setupScope = provider.CreateAsyncScope())
            {
                var db = setupScope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.EnsureCreatedAsync();
            }
            return new(connection, options, provider, queue);
        }

        public async Task<Guid> AddJobAsync(
            PublicationRenderStatus status,
            bool cancellationRequested = false)
        {
            await using var db = new AppDbContext(_options, NullLogger<AppDbContext>.Instance);
            var project = new Project
            {
                Name = "Book",
                Slug = $"book-{Guid.NewGuid():N}",
            };
            var edition = new PublicationEdition
            {
                ProjectId = project.Id,
                Name = "Paperback",
            };
            var job = new PublicationRenderJob
            {
                EditionId = edition.Id,
                Status = status,
                CancellationRequested = cancellationRequested,
                CompletedAt = status == PublicationRenderStatus.Cancelled
                    ? DateTime.UtcNow
                    : null,
            };
            db.AddRange(project, edition, job);
            await db.SaveChangesAsync();
            return job.Id;
        }

        public async Task<PublicationRenderJob> GetJobAsync(Guid jobId)
        {
            await using var db = new AppDbContext(_options, NullLogger<AppDbContext>.Instance);
            return await db.PublicationRenderJobs.AsNoTracking().SingleAsync(job => job.Id == jobId);
        }

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class RecordingRenderQueue : IPublicationRenderQueue
    {
        private readonly PublicationRenderQueue _inner = new();
        private readonly TaskCompletionSource<Guid> _completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Guid> Completed => _completed.Task;

        public ValueTask EnqueueAsync(Guid jobId, CancellationToken cancellationToken) =>
            _inner.EnqueueAsync(jobId, cancellationToken);

        public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) =>
            _inner.ReadAllAsync(cancellationToken);

        public CancellationToken Register(Guid jobId) => _inner.Register(jobId);

        public void Cancel(Guid jobId) => _inner.Cancel(jobId);

        public void Complete(Guid jobId)
        {
            _inner.Complete(jobId);
            _completed.TrySetResult(jobId);
        }
    }

    private sealed class BlockingPublishService : IPublishService
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public Task<PublishWorkspaceView> GetWorkspaceAsync(
            Guid projectId,
            Guid editionId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<PublishDocument> GetDocumentAsync(
            Guid projectId,
            Guid editionId,
            CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The blocking fixture should be cancelled.");
        }

        public Task<PublishDocument> GetPrintDocumentAsync(
            Guid projectId,
            Guid editionId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProjectExportFile> ExportAsync(
            Guid projectId,
            Guid editionId,
            PublishExportFormat format,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
