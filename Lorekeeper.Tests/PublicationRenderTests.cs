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
using SkiaSharp;

namespace Lorekeeper.Tests;

public sealed class PublicationRenderTests
{
    [Fact]
    public void PressRuntimeFailsClosedWithoutUsingMachineInstalledTools()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var runtime = new PublicationPressRuntime(
                Options.Create(new PublicationPressOptions()),
                new TestPressInstallationRoot(root));

            var readiness = runtime.GetReadiness();

            Assert.False(readiness.IsReady);
            Assert.Contains("app-owned runtime folder", readiness.Message, StringComparison.Ordinal);
            Assert.Contains("will not fall back", readiness.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PressRuntimeBuildsAControlledEnvironmentFromItsOwnedBundle()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        var runtimeRoot = Path.Combine(root, "press-runtime");
        Directory.CreateDirectory(Path.Combine(runtimeRoot, "fonts"));
        Directory.CreateDirectory(Path.Combine(runtimeRoot, "licenses"));
        Directory.CreateDirectory(Path.Combine(runtimeRoot, "profiles"));
        try
        {
            var executableName = OperatingSystem.IsWindows()
                ? "lorekeeper-press-weasy.exe"
                : "lorekeeper-press-weasy";
            foreach (var path in new[]
            {
                Path.Combine(runtimeRoot, executableName),
                Path.Combine(runtimeRoot, "fonts", "fonts.conf"),
                Path.Combine(runtimeRoot, "fonts", "LiberationSerif-Regular.ttf"),
                Path.Combine(runtimeRoot, "fonts", "LiberationSerif-Bold.ttf"),
                Path.Combine(runtimeRoot, "licenses", "Liberation-Fonts-LICENSE.txt"),
                Path.Combine(runtimeRoot, "profiles", "printing2009.icc"),
            })
            {
                File.WriteAllText(path, "fixture");
            }
            File.WriteAllText(
                Path.Combine(runtimeRoot, "lorekeeper-press-weasy-binaries.json"),
                "{\"binaryCount\":1}");
            var executablePath = Path.Combine(runtimeRoot, executableName);
            var executableHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(executablePath)));
            var bundleFiles = Directory.EnumerateFiles(runtimeRoot, "*", SearchOption.AllDirectories)
                .Select(path => new
                {
                    relativePath = Path.GetRelativePath(runtimeRoot, path).Replace(Path.DirectorySeparatorChar, '/'),
                    sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))),
                })
                .ToArray();
            var inventoryHash = bundleFiles.Single(file =>
                file.relativePath == "lorekeeper-press-weasy-binaries.json").sha256;
            var profileHash = bundleFiles.Single(file =>
                file.relativePath == "profiles/printing2009.icc").sha256;
            File.WriteAllText(
                Path.Combine(runtimeRoot, "lorekeeper-press-weasy-build.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    schemaVersion = 2,
                    platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "macos",
                    architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
                    binaryInventory = new
                    {
                        releaseLicenseGatePassed = true,
                        uncontrolledBinaryCount = 0,
                        sha256 = inventoryHash,
                    },
                    executable = new { sha256 = executableHash },
                    cmykProfile = new
                    {
                        relativePath = "profiles/printing2009.icc",
                        sha256 = profileHash,
                    },
                    bundleFiles,
                }));
            var runtime = new PublicationPressRuntime(
                Options.Create(new PublicationPressOptions()),
                new TestPressInstallationRoot(root));
            var output = Path.Combine(root, "job");
            Assert.NotEqual(
                Path.GetFullPath(Environment.CurrentDirectory),
                Path.GetFullPath(root));

            if (!OperatingSystem.IsWindows())
            {
                var notExecutable = runtime.GetReadiness();
                Assert.False(notExecutable.IsReady);
                Assert.Contains("not marked executable", notExecutable.Message, StringComparison.Ordinal);
                File.SetUnixFileMode(
                    executablePath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var start = runtime.CreateStartInfo(Guid.NewGuid(), output, requireCmykProfile: false);

            Assert.Equal(Path.Combine(runtimeRoot, executableName), start.FileName);
            Assert.Equal(runtimeRoot, start.WorkingDirectory);
            Assert.Equal(Path.Combine(runtimeRoot, "fonts", "fonts.conf"), start.Environment["FONTCONFIG_FILE"]);
            Assert.DoesNotContain("uv", start.FileName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("fixture-machine-path", start.Environment["PATH"], StringComparison.Ordinal);

            var ingramStart = runtime.CreateStartInfo(Guid.NewGuid(), output, requireCmykProfile: true);
            Assert.Contains(Path.Combine(runtimeRoot, "profiles", "printing2009.icc"), ingramStart.ArgumentList);

            File.WriteAllText(Path.Combine(runtimeRoot, "unreviewed.dll"), "payload");
            var withUnexpectedPayload = runtime.GetReadiness();
            Assert.False(withUnexpectedPayload.IsReady);
            Assert.Contains("exactly match", withUnexpectedPayload.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void JpegCoverIsNormalizedToTheBoundedPngRendererContract()
    {
        using var bitmap = new SKBitmap(2, 3);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        var asset = new PublishAssetDocument(
            Guid.NewGuid(),
            "cover.jpg",
            "image/jpeg",
            encoded.ToArray(),
            string.Empty);

        var dataUri = PublicationRenderProcessor.CoverImageDataUri(asset);

        Assert.StartsWith("data:image/png;base64,", dataUri, StringComparison.Ordinal);
        var png = Convert.FromBase64String(dataUri[(dataUri.IndexOf(',') + 1)..]);
        using var decoded = SKCodec.Create(new SKMemoryStream(png));
        Assert.NotNull(decoded);
        Assert.Equal(SKEncodedImageFormat.Png, decoded.EncodedFormat);
        Assert.Equal(2, decoded.Info.Width);
        Assert.Equal(3, decoded.Info.Height);
    }

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
        var service = new PublicationRenderService(db, null!, null!, null!, null!, null!);

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
                    null!,
                    Options.Create(new PublicationPressOptions())));
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

    private sealed class TestPressInstallationRoot(string rootPath) : IPublicationPressInstallationRoot
    {
        public string RootPath { get; } = rootPath;
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

        public Task<ProjectExportFile> ExportAsync(
            Guid projectId,
            Guid editionId,
            PublishExportFormat format,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
