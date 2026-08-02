using System.Diagnostics;
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
            Assert.Contains("never falls back", readiness.Message, StringComparison.Ordinal);
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
        Directory.CreateDirectory(runtimeRoot);
        try
        {
            var executableName = OperatingSystem.IsWindows()
                ? "lorekeeper-press.exe"
                : "lorekeeper-press";
            var executablePath = Path.Combine(runtimeRoot, executableName);
            File.WriteAllText(executablePath, "fixture");
            var executableHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(executablePath)));
            var fontPath = Path.Combine(runtimeRoot, "Lora-Regular.ttf");
            File.WriteAllText(fontPath, "owned font fixture");
            var fontHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fontPath)));
            File.WriteAllText(
                Path.Combine(runtimeRoot, "lorekeeper-press-runtime.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    schemaVersion = 3,
                    platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "macos",
                    architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
                    description = new
                    {
                        protocolVersion = 5,
                        rendererVersion = "2.0.0",
                        profiles = new[] { "generic-paperback-v1", "generic-digital-pdf-v1", "kdp-paperback-v1", "ingram-paperback-pdfx1a-v1" },
                        limits = new { maximumPages = 10_000 },
                        capabilities = new { designedPages = true, flowFigures = true, digitalBookPdf = true, taggedPdf = true, projectFonts = true },
                    },
                    files = new[]
                    {
                        new
                        {
                            relativePath = executableName,
                            byteLength = new FileInfo(executablePath).Length,
                            sha256 = executableHash,
                        },
                        new
                        {
                            relativePath = "Lora-Regular.ttf",
                            byteLength = new FileInfo(fontPath).Length,
                            sha256 = fontHash,
                        },
                    },
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

            var start = runtime.CreateStartInfo(Guid.NewGuid(), output);

            Assert.Equal(Path.Combine(runtimeRoot, executableName), start.FileName);
            Assert.Equal(runtimeRoot, start.WorkingDirectory);
            Assert.Equal(new[] { "render", "--job-root", Path.GetFullPath(output) }, start.ArgumentList);
            Assert.False(start.RedirectStandardInput);
            Assert.DoesNotContain(start.Environment.Keys, key => key.Contains("PYTHON", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("2.0.0", runtime.GetDescription().RendererVersion);

            File.WriteAllText(Path.Combine(runtimeRoot, "unreviewed.dll"), "payload");
            var withUnexpectedPayload = runtime.GetReadiness();
            Assert.False(withUnexpectedPayload.IsReady);
            Assert.Contains("exactly match", withUnexpectedPayload.Message, StringComparison.Ordinal);
            File.Delete(Path.Combine(runtimeRoot, "unreviewed.dll"));

            File.Delete(fontPath);
            var withMissingFont = runtime.GetReadiness();
            Assert.False(withMissingFont.IsReady);
            Assert.Contains("missing", withMissingFont.Message, StringComparison.Ordinal);
            File.WriteAllText(fontPath, "owned font fixture");

            File.WriteAllText(executablePath, "fixturf");
            var withModifiedExecutable = runtime.GetReadiness();
            Assert.False(withModifiedExecutable.IsReady);
            Assert.Contains("fingerprint", withModifiedExecutable.Message, StringComparison.Ordinal);
            File.WriteAllText(executablePath, "fixture");

            File.Delete(executablePath);
            var withMissingExecutable = runtime.GetReadiness();
            Assert.False(withMissingExecutable.IsReady);
            Assert.Contains("missing", withMissingExecutable.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void JpegCoverIsStagedWithoutCsharpRasterization()
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

        var staged = PublicationRenderProcessor.StageAsset(asset);

        Assert.Equal($"assets/{asset.Id:N}.jpg", staged.RelativePath);
        Assert.Equal("image/jpeg", staged.ContentType);
        Assert.Equal(asset.Data, staged.Data);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(asset.Data)), staged.Sha256);
        Assert.Equal(2, staged.WidthPixels);
        Assert.Equal(3, staged.HeightPixels);
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
            RendererVersion = "1.0.0",
        };

        var current = PublicationRenderService.View(job, [artifact], "rendered-source", "1.0.0");
        var stale = PublicationRenderService.View(job, [artifact], "changed-source", "1.0.0");
        var upgraded = PublicationRenderService.View(job, [artifact], "rendered-source", "2.0.0");

        Assert.False(Assert.Single(current.Artifacts).IsStale);
        Assert.True(Assert.Single(stale.Artifacts).IsStale);
        Assert.True(Assert.Single(upgraded.Artifacts).IsStale);

        artifact.Kind = PublicationArtifactKind.PublicationPackage;
        var package = PublicationRenderService.View(job, [artifact], "rendered-source", "2.0.0");
        Assert.False(Assert.Single(package.Artifacts).IsStale);
    }

    [Fact]
    public void RuntimeReadinessRequiresTheSelectedVendorProfile()
    {
        var service = new PublicationRenderService(
            null!, null!, null!,
            new FixedPressRuntime(profiles: ["generic-paperback-v1"]),
            null!);

        Assert.True(service.GetRuntimeReadiness(PublicationEditionFormat.Paperback, PublicationVendor.Generic).IsReady);
        var kdp = service.GetRuntimeReadiness(PublicationEditionFormat.Paperback, PublicationVendor.AmazonKdp);
        Assert.False(kdp.IsReady);
        Assert.Contains("kdp-paperback-v1", kdp.Message, StringComparison.Ordinal);
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

    [Fact]
    public void QueuedSourceConflictsAreRecordedAsStaleInsteadOfRuntimeFailures()
    {
        var diagnostic = PublicationRenderWorker.FailureDiagnostic(
            new InvalidOperationException("The edition changed while this render was queued. Request a new render."));

        Assert.Equal("PRESS_SOURCE_STALE", diagnostic.Code);
        Assert.Equal("error", diagnostic.Severity);

        var unsupported = PublicationRenderWorker.FailureDiagnostic(
            new InvalidOperationException("The edition's publication profile is unsupported by Lorekeeper Press."));
        Assert.Equal("PRESS_PROFILE_UNSUPPORTED", unsupported.Code);

        var renderer = PublicationRenderWorker.FailureDiagnostic(
            new InvalidOperationException("The queued render targets an older Lorekeeper Press renderer."));
        Assert.Equal("PRESS_RENDERER_STALE", renderer.Code);
    }

    [Fact]
    public void RendererRejectionsPreserveStructuredDiagnosticsAndEvidence()
    {
        var job = new PublicationRenderJob
        {
            EditionId = Guid.NewGuid(),
            Status = PublicationRenderStatus.Rendering,
        };
        using var evidenceDocument = System.Text.Json.JsonDocument.Parse(
            """{"validationStatus":"invalid","pdfVersion":"1.3"}""");
        var diagnostics = new[]
        {
            new PublicationRenderDiagnostic(
                "error",
                "PRESS_TOTAL_INK_EXCEEDED",
                "The CMYK paint exceeds 240% total ink."),
        };

        var terminal = PublicationRenderProcessor.ApplyTerminalResponse(
            job,
            "rejected",
            "1.0.0",
            diagnostics,
            evidenceDocument.RootElement.Clone());

        Assert.True(terminal);
        Assert.Equal(PublicationRenderStatus.Failed, job.Status);
        Assert.Equal("1.0.0", job.RendererVersion);
        Assert.Contains("PRESS_TOTAL_INK_EXCEEDED", job.DiagnosticsJson, StringComparison.Ordinal);
        Assert.Contains("\"validationStatus\":\"invalid\"", job.EvidenceJson, StringComparison.Ordinal);
        Assert.Equal("The CMYK paint exceeds 240% total ink.", job.ProgressMessage);
        Assert.NotNull(job.CompletedAt);
    }

    [Fact]
    public void PressRequestsAreUtf8JsonWithoutAByteOrderMark()
    {
        var bytes = PublicationRenderProcessor.SerializeRequest(new
        {
            protocolVersion = 5,
            jobId = Guid.Empty.ToString("N"),
        });

        Assert.True(bytes.AsSpan().StartsWith("{"u8));
        Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
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
                    new FixedPressRuntime(),
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
                VendorProfileVersion = "generic-paperback-v1",
            };
            var job = new PublicationRenderJob
            {
                EditionId = edition.Id,
                Status = status,
                ProfileId = "generic-paperback-v1",
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

    private sealed class FixedPressRuntime(
        string rendererVersion = "1.0.0",
        IReadOnlyList<string>? profiles = null) : IPublicationPressRuntime
    {
        public PublicationPressRuntimeReadiness GetReadiness() => new(true, "Ready");

        public PublicationPressDescription GetDescription() => new(
            3,
            rendererVersion,
            profiles ?? ["generic-paperback-v1", "kdp-paperback-v1", "ingram-paperback-pdfx1a-v1"],
            default,
            default);

        public ProcessStartInfo CreateStartInfo(Guid jobId, string jobRoot) =>
            throw new NotSupportedException();
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
