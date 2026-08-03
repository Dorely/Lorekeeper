using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Lorekeeper.Composition;
using Lorekeeper.Fonts;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lorekeeper.Publish;

public sealed record PublicationRenderJobView(
    Guid Id,
    Guid EditionId,
    PublicationRenderStatus Status,
    string SourceFingerprint,
    string RendererVersion,
    string ProfileId,
    int ProgressPercent,
    string ProgressMessage,
    bool CancellationRequested,
    IReadOnlyList<PublicationRenderDiagnostic> Diagnostics,
    IReadOnlyList<PublicationArtifactView> Artifacts,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt);

public sealed record PublicationRenderDiagnostic(
    string Severity,
    string Code,
    string Message,
    string? ArtifactKind = null,
    int? Page = null);

public sealed record PublicationArtifactView(
    Guid Id,
    PublicationArtifactKind Kind,
    string FileName,
    string MediaType,
    string Sha256,
    long ByteLength,
    int? PageCount,
    string SourceFingerprint,
    string RendererVersion,
    string ProfileId,
    DateTime CreatedAt,
    bool IsLegacy,
    bool IsStale);

public sealed record PublicationPageMapView(Guid ChapterId, Guid BlockId, int PageNumber);

public sealed record PublicationRenderComparison(
    Guid LeftJobId,
    Guid RightJobId,
    int? LeftPageCount,
    int? RightPageCount,
    int PageCountDelta,
    int MovedBlockCount,
    IReadOnlyList<PublicationPageMovement> Movements,
    string Explanation);

public sealed record PublicationPageMovement(Guid ChapterId, Guid BlockId, int FromPage, int ToPage);

public interface IPublicationRenderService
{
    PublicationPressRuntimeReadiness GetRuntimeReadiness(
        PublicationEditionFormat? format = null,
        PublicationVendor? vendor = null);
    PublicationPressDescription GetRuntimeDescription();
    Task<PublicationRenderJobView> RequestAsync(Guid projectId, Guid editionId, CancellationToken cancellationToken = default);
    Task<PublicationRenderJobView> CancelAsync(Guid projectId, Guid editionId, Guid jobId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PublicationRenderJobView>> ListAsync(Guid projectId, Guid editionId, CancellationToken cancellationToken = default);
    Task<PublicationRenderJobView> GetAsync(Guid projectId, Guid editionId, Guid jobId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PublicationPageMapView>> GetPageMapAsync(Guid projectId, Guid editionId, Guid jobId, CancellationToken cancellationToken = default);
    Task<PublicationRenderComparison> CompareAsync(Guid projectId, Guid editionId, Guid leftJobId, Guid rightJobId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PublicationArtifactView>> ListArtifactsAsync(Guid projectId, Guid editionId, CancellationToken cancellationToken = default);
    Task<PublicationArtifact?> GetArtifactAsync(Guid projectId, Guid artifactId, CancellationToken cancellationToken = default);
}

public interface IPublicationRenderQueue
{
    ValueTask EnqueueAsync(Guid jobId, CancellationToken cancellationToken);
    IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken);
    CancellationToken Register(Guid jobId);
    void Cancel(Guid jobId);
    void Complete(Guid jobId);
}

public sealed class PublicationRenderQueue : IPublicationRenderQueue
{
    private readonly Channel<Guid> _jobs = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Dictionary<Guid, CancellationTokenSource> _cancellations = [];
    private readonly object _cancellationLock = new();

    public ValueTask EnqueueAsync(Guid jobId, CancellationToken cancellationToken) =>
        _jobs.Writer.WriteAsync(jobId, cancellationToken);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) =>
        _jobs.Reader.ReadAllAsync(cancellationToken);

    public CancellationToken Register(Guid jobId)
    {
        lock (_cancellationLock)
        {
            if (!_cancellations.TryGetValue(jobId, out var source))
            {
                source = new CancellationTokenSource();
                _cancellations.Add(jobId, source);
            }
            return source.Token;
        }
    }

    public void Cancel(Guid jobId)
    {
        lock (_cancellationLock)
        {
            if (_cancellations.TryGetValue(jobId, out var source))
                source.Cancel();
        }
    }

    public void Complete(Guid jobId)
    {
        lock (_cancellationLock)
        {
            if (_cancellations.Remove(jobId, out var source))
                source.Dispose();
        }
    }
}

public sealed class PublicationRenderService(
    AppDbContext db,
    IPublicationEditionService editions,
    IPublicationRenderQueue queue,
    IPublicationPressRuntime pressRuntime,
    IProjectMutationCoordinator projectMutations) : IPublicationRenderService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public PublicationPressRuntimeReadiness GetRuntimeReadiness(
        PublicationEditionFormat? format = null,
        PublicationVendor? vendor = null)
    {
        var readiness = pressRuntime.GetReadiness();
        if (!readiness.IsReady || format is null || vendor is null)
            return readiness;
        var profile = PublicationRenderProcessor.ProfileFor(format.Value, vendor.Value);
        return pressRuntime.GetDescription().Profiles.Contains(profile, StringComparer.Ordinal)
            ? readiness
            : new(false, $"Lorekeeper Press does not advertise the required profile '{profile}'.");
    }

    public PublicationPressDescription GetRuntimeDescription() => pressRuntime.GetDescription();

    public async Task<PublicationRenderJobView> RequestAsync(
        Guid projectId,
        Guid editionId,
        CancellationToken cancellationToken = default)
    {
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var edition = await db.PublicationEditions.AsNoTracking().FirstOrDefaultAsync(
            candidate => candidate.ProjectId == projectId && candidate.Id == editionId,
            cancellationToken) ?? throw new KeyNotFoundException("Publication edition not found.");
        if (edition.Status != PublicationEditionStatus.Draft)
            throw new InvalidOperationException("Archived editions cannot be rendered.");
        if (edition.Format is not (PublicationEditionFormat.Paperback or PublicationEditionFormat.DigitalPdf))
            throw new InvalidOperationException("PDF rendering is available for paperback and Digital PDF editions.");
        var runtimeReadiness = GetRuntimeReadiness(edition.Format, edition.Vendor);
        if (!runtimeReadiness.IsReady)
            throw new InvalidOperationException(runtimeReadiness.Message);
        var active = await db.PublicationRenderJobs.AnyAsync(
            job => job.EditionId == editionId
                && (job.Status == PublicationRenderStatus.Queued || job.Status == PublicationRenderStatus.Rendering),
            cancellationToken);
        if (active)
            throw new InvalidOperationException("This edition already has an active render.");

        var job = new PublicationRenderJob
        {
            EditionId = editionId,
            SourceFingerprint = await editions.GetSourceFingerprintAsync(projectId, editionId, cancellationToken),
            PaginationFingerprint = await editions.GetPaginationFingerprintAsync(projectId, editionId, cancellationToken),
            ProfileId = PublicationRenderProcessor.ProfileFor(edition.Format, edition.Vendor),
            RendererVersion = pressRuntime.GetDescription().RendererVersion,
        };
        db.PublicationRenderJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        await queue.EnqueueAsync(job.Id, CancellationToken.None);
        return View(job, [], job.SourceFingerprint, job.RendererVersion);
    }

    public async Task<PublicationRenderJobView> CancelAsync(
        Guid projectId,
        Guid editionId,
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        var job = await GetTrackedAsync(projectId, editionId, jobId, cancellationToken);
        if (job.Status is PublicationRenderStatus.Completed or PublicationRenderStatus.Failed or PublicationRenderStatus.Cancelled)
            return await GetAsync(projectId, editionId, jobId, cancellationToken);
        job.CancellationRequested = true;
        if (job.Status == PublicationRenderStatus.Queued)
        {
            job.Status = PublicationRenderStatus.Cancelled;
            job.ProgressMessage = "Cancelled";
            job.CompletedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);
        queue.Cancel(jobId);
        return await GetAsync(projectId, editionId, jobId, cancellationToken);
    }

    public async Task<IReadOnlyList<PublicationRenderJobView>> ListAsync(
        Guid projectId,
        Guid editionId,
        CancellationToken cancellationToken = default)
    {
        var fingerprint = await editions.GetSourceFingerprintAsync(projectId, editionId, cancellationToken);
        var jobs = await db.PublicationRenderJobs
            .AsNoTracking()
            .Where(job => job.EditionId == editionId && job.Edition.ProjectId == projectId)
            .Include(job => job.Artifacts)
            .OrderByDescending(job => job.CreatedAt)
            .ToListAsync(cancellationToken);
        var rendererVersion = CurrentRendererVersion();
        return jobs.Select(job => View(job, job.Artifacts, fingerprint, rendererVersion)).ToList();
    }

    public async Task<PublicationRenderJobView> GetAsync(
        Guid projectId,
        Guid editionId,
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        var fingerprint = await editions.GetSourceFingerprintAsync(projectId, editionId, cancellationToken);
        var job = await db.PublicationRenderJobs
            .AsNoTracking()
            .Include(candidate => candidate.Artifacts)
            .FirstOrDefaultAsync(candidate => candidate.Id == jobId
                && candidate.EditionId == editionId
                && candidate.Edition.ProjectId == projectId, cancellationToken)
            ?? throw new KeyNotFoundException("Publication render job not found.");
        return View(job, job.Artifacts, fingerprint, CurrentRendererVersion());
    }

    public async Task<IReadOnlyList<PublicationPageMapView>> GetPageMapAsync(
        Guid projectId,
        Guid editionId,
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await EnsureJobAsync(projectId, editionId, jobId, cancellationToken);
        return await db.PublicationPageMapEntries.AsNoTracking()
            .Where(entry => entry.RenderJobId == jobId)
            .OrderBy(entry => entry.PageNumber)
            .ThenBy(entry => entry.ChapterId)
            .Select(entry => new PublicationPageMapView(entry.ChapterId, entry.BlockId, entry.PageNumber))
            .ToListAsync(cancellationToken);
    }

    public async Task<PublicationRenderComparison> CompareAsync(
        Guid projectId,
        Guid editionId,
        Guid leftJobId,
        Guid rightJobId,
        CancellationToken cancellationToken = default)
    {
        var left = await GetAsync(projectId, editionId, leftJobId, cancellationToken);
        var right = await GetAsync(projectId, editionId, rightJobId, cancellationToken);
        var leftMap = (await GetPageMapAsync(projectId, editionId, leftJobId, cancellationToken))
            .ToDictionary(entry => (entry.ChapterId, entry.BlockId));
        var rightMap = (await GetPageMapAsync(projectId, editionId, rightJobId, cancellationToken))
            .ToDictionary(entry => (entry.ChapterId, entry.BlockId));
        var movements = leftMap.Keys.Intersect(rightMap.Keys)
            .Where(key => leftMap[key].PageNumber != rightMap[key].PageNumber)
            .Select(key => new PublicationPageMovement(
                key.ChapterId,
                key.BlockId,
                leftMap[key].PageNumber,
                rightMap[key].PageNumber))
            .OrderBy(movement => movement.FromPage)
            .ToList();
        var leftPages = PrimaryBookArtifact(left.Artifacts)?.PageCount;
        var rightPages = PrimaryBookArtifact(right.Artifacts)?.PageCount;
        var delta = (rightPages ?? 0) - (leftPages ?? 0);
        var explanation = left.SourceFingerprint == right.SourceFingerprint
            ? $"The same source produced a {delta:+#;-#;0}-page difference; inspect renderer/profile versions."
            : $"Content or edition settings changed, moving {movements.Count} mapped blocks and changing the publication by {delta:+#;-#;0} pages.";
        return new(leftJobId, rightJobId, leftPages, rightPages, delta, movements.Count, movements, explanation);
    }

    private static PublicationArtifactView? PrimaryBookArtifact(IReadOnlyList<PublicationArtifactView> artifacts) =>
        artifacts.FirstOrDefault(artifact => artifact.Kind == PublicationArtifactKind.BookPdf)
        ?? artifacts.FirstOrDefault(artifact => artifact.Kind == PublicationArtifactKind.InteriorPdf);

    public async Task<PublicationArtifact?> GetArtifactAsync(
        Guid projectId,
        Guid artifactId,
        CancellationToken cancellationToken = default)
    {
        var artifact = await db.PublicationArtifacts.AsNoTracking().FirstOrDefaultAsync(
            artifact => artifact.Id == artifactId && artifact.Edition.ProjectId == projectId,
            cancellationToken);
        if (artifact is null
            || artifact.ByteLength != artifact.Data.LongLength
            || !string.Equals(
                artifact.Sha256,
                Convert.ToHexStringLower(SHA256.HashData(artifact.Data)),
                StringComparison.Ordinal))
        {
            return null;
        }
        return artifact;
    }

    public async Task<IReadOnlyList<PublicationArtifactView>> ListArtifactsAsync(
        Guid projectId,
        Guid editionId,
        CancellationToken cancellationToken = default)
    {
        var fingerprint = await editions.GetSourceFingerprintAsync(projectId, editionId, cancellationToken);
        var rendererVersion = CurrentRendererVersion();
        return (await db.PublicationArtifacts.AsNoTracking()
                .Where(artifact => artifact.EditionId == editionId && artifact.Edition.ProjectId == projectId)
                .OrderByDescending(artifact => artifact.CreatedAt)
                .ToListAsync(cancellationToken))
            .Select(artifact => ArtifactView(artifact, fingerprint, rendererVersion))
            .ToList();
    }

    private string? CurrentRendererVersion() => pressRuntime.GetReadiness().IsReady
        ? pressRuntime.GetDescription().RendererVersion
        : null;

    private async Task<PublicationRenderJob> GetTrackedAsync(
        Guid projectId,
        Guid editionId,
        Guid jobId,
        CancellationToken cancellationToken) =>
        await db.PublicationRenderJobs.FirstOrDefaultAsync(
            job => job.Id == jobId && job.EditionId == editionId && job.Edition.ProjectId == projectId,
            cancellationToken) ?? throw new KeyNotFoundException("Publication render job not found.");

    private async Task EnsureJobAsync(Guid projectId, Guid editionId, Guid jobId, CancellationToken cancellationToken)
    {
        if (!await db.PublicationRenderJobs.AnyAsync(
            job => job.Id == jobId && job.EditionId == editionId && job.Edition.ProjectId == projectId,
            cancellationToken))
            throw new KeyNotFoundException("Publication render job not found.");
    }

    internal static PublicationRenderJobView View(
        PublicationRenderJob job,
        IEnumerable<PublicationArtifact> artifacts,
        string currentFingerprint,
        string? currentRendererVersion = null) =>
        new(
            job.Id,
            job.EditionId,
            job.Status,
            job.SourceFingerprint,
            job.RendererVersion,
            job.ProfileId,
            job.ProgressPercent,
            job.ProgressMessage,
            job.CancellationRequested,
            DeserializeDiagnostics(job.DiagnosticsJson),
            artifacts.Select(artifact => ArtifactView(artifact, currentFingerprint, currentRendererVersion)).ToList(),
            job.CreatedAt,
            job.StartedAt,
            job.CompletedAt);

    private static PublicationArtifactView ArtifactView(
        PublicationArtifact artifact,
        string currentFingerprint,
        string? currentRendererVersion) =>
        new(
            artifact.Id,
            artifact.Kind,
            artifact.FileName,
            artifact.MediaType,
            artifact.Sha256,
            artifact.ByteLength,
            artifact.PageCount,
            artifact.SourceFingerprint,
            artifact.RendererVersion,
            artifact.ProfileId,
            artifact.CreatedAt,
            artifact.IsLegacy,
            !string.Equals(artifact.SourceFingerprint, currentFingerprint, StringComparison.Ordinal)
                || artifact.Kind is PublicationArtifactKind.InteriorPdf or PublicationArtifactKind.CoverPdf
                    && currentRendererVersion is not null
                    && !string.Equals(artifact.RendererVersion, currentRendererVersion, StringComparison.Ordinal));

    private static IReadOnlyList<PublicationRenderDiagnostic> DeserializeDiagnostics(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<PublicationRenderDiagnostic>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [new("error", "PRESS_DIAGNOSTICS_INVALID", "Stored renderer diagnostics could not be read.")];
        }
    }
}

public sealed class PublicationRenderWorker(
    IPublicationRenderQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<PublicationRenderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            logger.LogInformation("Recovering publication render queue.");
            await RecoverInterruptedJobsAsync(stoppingToken);
            logger.LogInformation("Publication render queue is ready.");
            await foreach (var jobId in queue.ReadAllAsync(stoppingToken))
            {
                var jobCancellation = queue.Register(jobId);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, jobCancellation);
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<PublicationRenderProcessor>()
                        .ProcessAsync(jobId, linked.Token);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Leave the durable Queued/Rendering state for restart recovery.
                }
                catch (OperationCanceledException exception)
                {
                    if (jobCancellation.IsCancellationRequested
                        || await IsCancellationRequestedAsync(jobId, CancellationToken.None))
                    {
                        await MarkCancelledAsync(jobId, CancellationToken.None);
                    }
                    else
                    {
                        logger.LogError(exception, "Publication render {JobId} was cancelled unexpectedly.", jobId);
                        await MarkFailedAsync(jobId, exception, CancellationToken.None);
                    }
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Publication render {JobId} failed.", jobId);
                    await MarkFailedAsync(jobId, exception, stoppingToken);
                }
                finally
                {
                    PublicationRenderProcessor.Cleanup(jobId);
                    queue.Complete(jobId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task<bool> IsCancellationRequestedAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PublicationRenderJobs.AsNoTracking()
            .Where(job => job.Id == jobId)
            .Select(job => job.CancellationRequested
                || job.Status == PublicationRenderStatus.Cancelled)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private async Task RecoverInterruptedJobsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var jobs = await db.PublicationRenderJobs
            .Where(job => job.Status == PublicationRenderStatus.Queued
                || job.Status == PublicationRenderStatus.Rendering)
            .ToListAsync(cancellationToken);
        foreach (var job in jobs)
        {
            if (job.CancellationRequested)
            {
                job.Status = PublicationRenderStatus.Cancelled;
                job.ProgressMessage = "Cancelled during restart recovery";
                job.CompletedAt = DateTime.UtcNow;
                continue;
            }
            job.Status = PublicationRenderStatus.Queued;
            job.ProgressPercent = 0;
            job.ProgressMessage = "Recovered after restart";
            job.StartedAt = null;
            await queue.EnqueueAsync(job.Id, cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task MarkCancelledAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.PublicationRenderJobs.FirstOrDefaultAsync(candidate => candidate.Id == jobId, cancellationToken);
        if (job is null || job.Status == PublicationRenderStatus.Completed)
            return;
        job.Status = PublicationRenderStatus.Cancelled;
        job.ProgressMessage = "Cancelled";
        job.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task MarkFailedAsync(Guid jobId, Exception exception, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.PublicationRenderJobs.FirstOrDefaultAsync(candidate => candidate.Id == jobId, cancellationToken);
        if (job is null)
            return;
        job.Status = PublicationRenderStatus.Failed;
        job.ProgressMessage = "Render failed";
        job.DiagnosticsJson = JsonSerializer.Serialize(new[] { FailureDiagnostic(exception) });
        job.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    internal static PublicationRenderDiagnostic FailureDiagnostic(Exception exception) =>
        exception is InvalidOperationException
            && exception.Message.Contains("edition changed", StringComparison.OrdinalIgnoreCase)
            ? new("error", "PRESS_SOURCE_STALE", exception.Message)
            : exception is InvalidOperationException
                && exception.Message.Contains("profile is unsupported", StringComparison.OrdinalIgnoreCase)
                ? new("error", "PRESS_PROFILE_UNSUPPORTED", exception.Message)
            : exception is InvalidOperationException
                && exception.Message.Contains("renderer", StringComparison.OrdinalIgnoreCase)
                && (exception.Message.Contains("older", StringComparison.OrdinalIgnoreCase)
                    || exception.Message.Contains("different", StringComparison.OrdinalIgnoreCase))
                ? new("error", "PRESS_RENDERER_STALE", exception.Message)
            : new("error", "PRESS_RUNTIME_FAILED", exception.Message);
}

public sealed class PublicationRenderProcessor(
    AppDbContext db,
    IPublishService publishing,
    IPublicationEditionService editions,
    IPublicationCoverService covers,
    IProjectFontService projectFonts,
    IPublicationPressRuntime pressRuntime,
    IOptions<PublicationPressOptions> options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string ProfileFor(PublicationEditionFormat format, PublicationVendor vendor) =>
        format == PublicationEditionFormat.DigitalPdf
            ? "generic-digital-pdf-v1"
            : vendor == PublicationVendor.IngramSpark
        ? "ingram-paperback-pdfx1a-v1"
        : vendor == PublicationVendor.AmazonKdp
            ? "kdp-paperback-v1"
            : "generic-paperback-v1";

    public async Task ProcessAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await db.PublicationRenderJobs
            .Include(candidate => candidate.Edition)
            .FirstOrDefaultAsync(candidate => candidate.Id == jobId, cancellationToken)
            ?? throw new KeyNotFoundException("Publication render job not found.");
        if (job.Status == PublicationRenderStatus.Cancelled || job.CancellationRequested)
            throw new OperationCanceledException(cancellationToken);
        if (!IsSupportedProfile(job.Edition.VendorProfileVersion)
            || !string.Equals(
                job.ProfileId,
                job.Edition.VendorProfileVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The edition's publication profile is unsupported by Lorekeeper Press.");
        }
        var runtime = pressRuntime.GetDescription();
        if (!runtime.Profiles.Contains(job.ProfileId, StringComparer.Ordinal))
            throw new InvalidOperationException("The queued publication profile is unsupported by the installed Lorekeeper Press runtime.");
        if (string.IsNullOrWhiteSpace(job.RendererVersion))
            job.RendererVersion = runtime.RendererVersion;
        else if (!string.Equals(job.RendererVersion, runtime.RendererVersion, StringComparison.Ordinal))
            throw new InvalidOperationException("The queued render targets an older Lorekeeper Press renderer. Request a new render.");
        job.Status = PublicationRenderStatus.Rendering;
        job.ProgressPercent = 10;
        job.ProgressMessage = "Preparing semantic manuscript";
        job.StartedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        var document = await publishing.GetDocumentAsync(job.Edition.ProjectId, job.EditionId, cancellationToken);
        var fingerprintBeforeRender = await editions.GetSourceFingerprintAsync(
            job.Edition.ProjectId,
            job.EditionId,
            cancellationToken);
        if (!string.Equals(fingerprintBeforeRender, job.SourceFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("The edition changed while this render was queued. Request a new render.");
        var expectedPageMap = document.Sections
            .SelectMany(section => section.Chapters)
            .SelectMany(chapter => chapter.Manuscript.Content.Select(block => (
                ChapterId: chapter.Id,
                BlockId: Guid.Parse(block.Id))))
            .ToHashSet();
        var coverDesign = await covers.GetAsync(job.Edition.ProjectId, job.EditionId, cancellationToken);
        if (coverDesign.Diagnostics.Any(diagnostic => diagnostic.Contains("requires", StringComparison.OrdinalIgnoreCase)
            || diagnostic.Contains("must contain", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(string.Join(" ", coverDesign.Diagnostics));
        var request = await BuildRequestAsync(job, document, coverDesign, cancellationToken);
        job.ProgressPercent = 30;
        job.ProgressMessage = "Typesetting interior and cover";
        await db.SaveChangesAsync(cancellationToken);

        Cleanup(job.Id);
        var result = await InvokeAsync(job.Id, request, cancellationToken);
        if (result.ProtocolVersion != 5
            || !string.Equals(result.JobId, job.Id.ToString("N"), StringComparison.Ordinal))
            throw new InvalidOperationException("The press renderer returned a mismatched protocol or job identity.");
        if (!string.Equals(result.RendererVersion, job.RendererVersion, StringComparison.Ordinal))
            throw new InvalidOperationException("The press renderer returned a different renderer version than the queued job.");
        if (ApplyTerminalResponse(
            job,
            result.Status,
            result.RendererVersion,
            result.Diagnostics,
            result.Evidence))
        {
            await db.SaveChangesAsync(cancellationToken);
            Cleanup(job.Id);
            return;
        }
        var resultArtifacts = result.Artifacts
            ?? throw new InvalidOperationException("The press renderer omitted its artifact list.");
        var resultKinds = resultArtifacts.Select(artifact => artifact.Kind).Order().ToArray();
        var expectedKinds = job.Edition.Format == PublicationEditionFormat.DigitalPdf
            ? new[] { "book-pdf" }
            : new[] { "cover-pdf", "interior-pdf" };
        if (!resultKinds.SequenceEqual(expectedKinds, StringComparer.Ordinal))
            throw new InvalidOperationException(job.Edition.Format == PublicationEditionFormat.DigitalPdf
                ? "The press renderer must return exactly one Digital PDF book artifact."
                : "The press renderer must return exactly one interior and one cover PDF.");
        var interiorResult = resultArtifacts.Single(artifact => artifact.Kind is "interior-pdf" or "book-pdf");
        if (interiorResult.PageCount is not > 0 or > 100_000)
            throw new InvalidOperationException("The renderer returned an invalid interior page count.");
        if (job.Edition.Format != PublicationEditionFormat.DigitalPdf
            && resultArtifacts.Single(artifact => artifact.Kind == "cover-pdf").PageCount != 1)
            throw new InvalidOperationException("The renderer must return a one-page full-wrap cover.");
        job.ProgressPercent = 80;
        job.ProgressMessage = "Verifying immutable artifacts";
        await db.SaveChangesAsync(cancellationToken);
        var outputRoot = JobRoot(job.Id);
        foreach (var resultArtifact in resultArtifacts)
        {
            if (!string.Equals(resultArtifact.MediaType, "application/pdf", StringComparison.Ordinal)
                || resultArtifact.ByteLength is < 5 or > 256L * 1024 * 1024)
                throw new InvalidOperationException("The renderer returned an invalid PDF artifact envelope.");
            var fullPath = Path.GetFullPath(Path.Combine(outputRoot, resultArtifact.RelativePath));
            var relative = Path.GetRelativePath(outputRoot, fullPath);
            if (Path.IsPathRooted(relative)
                || relative == ".."
                || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                throw new InvalidOperationException("The renderer returned an artifact outside its job directory.");
            var file = new FileInfo(fullPath);
            if (!file.Exists
                || file.Length != resultArtifact.ByteLength
                || file.Length > 256L * 1024 * 1024
                || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException($"Artifact integrity failed for {resultArtifact.RelativePath}.");
            var data = await File.ReadAllBytesAsync(fullPath, cancellationToken);
            if (!data.AsSpan().StartsWith("%PDF-"u8))
                throw new InvalidOperationException($"Artifact {resultArtifact.RelativePath} is not a PDF.");
            var sha = Convert.ToHexStringLower(SHA256.HashData(data));
            if (!string.Equals(sha, resultArtifact.Sha256, StringComparison.OrdinalIgnoreCase)
                || data.LongLength != resultArtifact.ByteLength)
                throw new InvalidOperationException($"Artifact integrity failed for {resultArtifact.RelativePath}.");
            db.PublicationArtifacts.Add(new PublicationArtifact
            {
                EditionId = job.EditionId,
                RenderJobId = job.Id,
                Kind = ParseKind(resultArtifact.Kind),
                FileName = Path.GetFileName(fullPath),
                MediaType = resultArtifact.MediaType,
                Data = data,
                Sha256 = sha,
                ByteLength = data.LongLength,
                PageCount = resultArtifact.PageCount,
                SourceFingerprint = job.SourceFingerprint,
                PaginationFingerprint = job.PaginationFingerprint,
                RendererVersion = job.RendererVersion,
                ProfileId = job.ProfileId,
            });
        }

        var parsedPageMap = new List<(Guid ChapterId, Guid BlockId, int PageNumber)>();
        foreach (var entry in result.PageMap ?? [])
        {
            if (!Guid.TryParse(entry.ChapterId, out var chapterId)
                || !Guid.TryParse(entry.BlockId, out var blockId)
                || entry.PageNumber < 1
                || entry.PageNumber > interiorResult.PageCount)
                throw new InvalidOperationException("The renderer returned an invalid page-map entry.");
            parsedPageMap.Add((chapterId, blockId, entry.PageNumber));
        }
        if (parsedPageMap.Select(entry => (entry.ChapterId, entry.BlockId)).Distinct().Count() != parsedPageMap.Count
            || !parsedPageMap.Select(entry => (entry.ChapterId, entry.BlockId)).ToHashSet().SetEquals(expectedPageMap))
            throw new InvalidOperationException("The renderer returned an incomplete or duplicate semantic page map.");
        var fingerprintAfterRender = await editions.GetSourceFingerprintAsync(
            job.Edition.ProjectId,
            job.EditionId,
            cancellationToken);
        if (!string.Equals(fingerprintAfterRender, job.SourceFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("The edition changed during rendering. The generated artifacts were discarded.");
        foreach (var entry in parsedPageMap)
            db.PublicationPageMapEntries.Add(new PublicationPageMapEntry
            {
                RenderJobId = job.Id,
                ChapterId = entry.ChapterId,
                BlockId = entry.BlockId,
                PageNumber = entry.PageNumber,
            });
        job.Status = PublicationRenderStatus.Completed;
        job.ProgressPercent = 100;
        job.ProgressMessage = "Lorekeeper validated";
        job.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        Cleanup(job.Id);
    }

    private static bool IsSupportedProfile(string profile) => profile is
        "generic-paperback-v1" or
        "generic-digital-pdf-v1" or
        "kdp-paperback-v1" or
        "ingram-paperback-pdfx1a-v1";

    internal static bool ApplyTerminalResponse(
        PublicationRenderJob job,
        string status,
        string? rendererVersion,
        IReadOnlyList<PublicationRenderDiagnostic>? diagnostics,
        JsonElement evidence)
    {
        job.RendererVersion = rendererVersion ?? string.Empty;
        job.DiagnosticsJson = JsonSerializer.Serialize(diagnostics ?? [], JsonOptions);
        job.EvidenceJson = evidence.ValueKind == JsonValueKind.Undefined
            ? "{}"
            : evidence.GetRawText();
        if (string.Equals(status, "completed", StringComparison.Ordinal))
            return false;

        var cancelled = string.Equals(status, "cancelled", StringComparison.Ordinal);
        job.Status = cancelled
            ? PublicationRenderStatus.Cancelled
            : PublicationRenderStatus.Failed;
        job.CancellationRequested |= cancelled;
        job.ProgressPercent = 100;
        job.ProgressMessage = cancelled
            ? "Cancelled"
            : diagnostics?.FirstOrDefault(diagnostic => diagnostic.Severity == "error")?.Message
                ?? "Render failed";
        job.CompletedAt = DateTime.UtcNow;
        return true;
    }

    private async Task<PressPreparedRequest> BuildRequestAsync(
        PublicationRenderJob job,
        PublishDocument document,
        PublicationCoverDesignView coverDesign,
        CancellationToken cancellationToken)
    {
        var assets = document.Assets
            .GroupBy(asset => asset.Id)
            .Select(group => StageAsset(group.First()))
            .ToArray();
        var coverScene = JsonSerializer.Deserialize<CompositionScene>(
            coverDesign.CompositionSceneJson,
            ManuscriptCodec.JsonOptions);
        var usedFontKeys = document.NamedStyles
            .Select(style => style.Definition.FontFamilyKey)
            .Concat(document.Sections.SelectMany(section => section.Chapters)
                .SelectMany(chapter => chapter.PageCompositions)
                .SelectMany(composition => composition.Variants)
                .SelectMany(variant => variant.Scene.Objects.Select(item => item.FontFamilyKey)
                    .Concat(variant.Scene.Styles.Select(style => style.FontFamilyKey))))
            .Concat(coverScene?.Objects.Select(item => item.FontFamilyKey) ?? [])
            .Concat(coverScene?.Styles.Select(style => style.FontFamilyKey) ?? [])
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fontDocuments = document.Fonts
            .Where(font => usedFontKeys.Contains(font.FamilyKey))
            .ToList();
        var availableFamilyKeys = fontDocuments
            .Select(font => font.FamilyKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var familyKey in usedFontKeys.Where(key => !availableFamilyKeys.Contains(key)).Order())
        {
            var family = PublicationBuiltInFonts.Find(familyKey);
            if (family is null)
                continue;
            var familyId = DeterministicFontId(family.Key);
            foreach (var faceView in family.Faces)
            {
                var face = await projectFonts.ResolveFaceAsync(
                    document.ProjectId,
                    family.Key,
                    faceView.Weight,
                    faceView.Italic,
                    requireExact: true,
                    cancellationToken)
                    ?? throw new InvalidOperationException(
                        $"Bundled publication font '{family.Name}' is missing {faceView.SubfamilyName}.");
                fontDocuments.Add(new PublishFontDocument(
                    family.Key,
                    familyId,
                    family.Name,
                    DeterministicFontId($"{family.Key}|{face.Weight}|{face.Italic}"),
                    face.FileName,
                    face.ContentType,
                    face.Weight,
                    face.Italic,
                    face.Data));
            }
            availableFamilyKeys.Add(family.Key);
        }
        var stagedFonts = fontDocuments
            .OrderBy(font => font.FamilyKey, StringComparer.Ordinal)
            .ThenBy(font => font.Weight)
            .ThenBy(font => font.Italic)
            .ThenBy(font => font.FaceId)
            .Select(StageFont)
            .ToArray();
        var stagedFamilyKeys = stagedFonts.Select(font => font.FamilyKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingFontKeys = usedFontKeys.Where(key => !stagedFamilyKeys.Contains(key)).Order().ToArray();
        if (missingFontKeys.Length > 0)
            throw new InvalidOperationException($"Publication content references unavailable fonts: {string.Join(", ", missingFontKeys)}.");
        var sections = document.Sections.Select(section => new
        {
            id = section.ActId,
            section.Title,
            synopsis = document.Profile.IncludeActSynopses ? section.Synopsis : string.Empty,
            section.IncludePage,
            section.IncludeHeading,
            chapters = section.Chapters.Select(chapter => new
            {
                id = chapter.Id,
                title = string.IsNullOrWhiteSpace(chapter.Title) ? "Untitled chapter" : chapter.Title,
                synopsis = document.Profile.IncludeChapterSynopses ? chapter.Synopsis : string.Empty,
                chapter.IncludeHeading,
                blocks = chapter.Manuscript.Content.Select(BlockPayload).ToArray(),
                pageCompositions = chapter.PageCompositions.Select(composition => new
                {
                    id = composition.Id,
                    composition.Name,
                    revision = composition.Revision,
                    semanticBlocks = composition.SemanticManuscript.Content.Select(BlockPayload).ToArray(),
                    variants = composition.Variants.Select(variant => new
                    {
                        id = variant.Id,
                        variant.GeometryKey,
                        variant.Revision,
                        scene = CompositionService.WithDerivedTextSemanticRoles(
                            variant.Scene,
                            composition.SemanticManuscript),
                    }).ToArray(),
                }).ToArray(),
            }).ToArray(),
        }).ToArray();
        var matter = document.Matter
            .OrderBy(item => item.Location)
            .ThenBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .Select(item => new
            {
                id = item.Id,
                location = item.Location.ToString(),
                kind = item.Kind.ToString(),
                title = PublicationMatterFormatting.Title(item),
                blocks = item.Manuscript.Content.Select(BlockPayload).ToArray(),
            })
            .ToArray();
        if (sections.Sum(section => section.chapters.Length) == 0)
            throw new InvalidOperationException("Include at least one non-empty chapter before rendering.");
        var missingVariants = sections.SelectMany(section => section.chapters)
            .SelectMany(chapter => chapter.pageCompositions)
            .Where(composition => composition.variants.Length != 1)
            .Select(composition => composition.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (missingVariants.Count > 0)
            throw new InvalidOperationException($"Create and review an exact layout variant for this edition geometry: {string.Join(", ", missingVariants)}.");
        var payload = new
        {
            protocolVersion = 5,
            jobId = job.Id.ToString("N"),
            profile = job.ProfileId,
            ink = job.Edition.Ink == PublicationInk.Digital
                ? PublicationInk.Color.ToString()
                : job.Edition.Ink.ToString(),
            document = new
            {
                title = string.IsNullOrWhiteSpace(document.DisplayTitle) ? document.ProjectName : document.DisplayTitle,
                subtitle = document.Profile.Subtitle,
                author = string.IsNullOrWhiteSpace(document.Profile.Author) ? "Unknown author" : document.Profile.Author,
                language = string.IsNullOrWhiteSpace(document.Profile.Language) ? "en" : document.Profile.Language,
                publisher = document.Profile.Publisher,
                copyright = document.Profile.Copyright,
                matter,
                includeTitlePage = document.Profile.IncludeTitlePage,
                includeVisibleTableOfContents = document.Profile.IncludeVisibleTableOfContents,
                includeActHeadings = document.Profile.IncludeActHeadings,
                includeChapterHeadings = document.Profile.IncludeChapterHeadings,
                numberActs = document.Profile.NumberActs,
                numberChapters = document.Profile.NumberChapters,
                sections,
                styles = document.NamedStyles.Select(style => new
                {
                    style.Name,
                    kind = style.Kind.ToString(),
                    style.SemanticRole,
                    definition = style.Definition,
                }).ToArray(),
                placements = document.Placements
                    .OrderBy(placement => placement.SortOrder)
                    .Select(placement => new
                    {
                        placement.Id,
                        assetId = placement.Asset.Id,
                        targetKind = placement.TargetKind.ToString(),
                        placement.TargetId,
                        placementKind = placement.PlacementKind.ToString(),
                        placement.Caption,
                        presentation = placement.Presentation ?? new FigurePresentation { Placement = FigurePlacementIntent.DedicatedPage },
                        placement.AltText,
                        placement.Decorative,
                        placement.Language,
                        accessibilityRole = placement.AccessibilityRole.ToString(),
                        placement.SortOrder,
                    }).ToArray(),
                outputMode = job.Edition.Format == PublicationEditionFormat.DigitalPdf ? "DigitalPdf" : "Print",
                allowDesignedPageOverrides = job.Edition.AllowDesignedPageOverrides,
            },
            trim = new
            {
                widthInches = document.Profile.PageWidthInches,
                heightInches = document.Profile.PageHeightInches,
                marginInches = document.Profile.PageMarginInches,
                bodyFontSizePoints = document.Profile.BodyFontSizePoints,
                bodyLineHeight = document.Profile.BodyLineHeight,
                bleedInches = job.Edition.Bleed && job.Edition.Format == PublicationEditionFormat.Paperback ? 0.125 : 0,
                mirrorMargins = true,
                rectoChapterStarts = true,
                minimumWidowLines = 2,
                minimumOrphanLines = 2,
            },
            cover = new
            {
                bleedInches = job.Edition.Bleed ? 0.125 : 0,
                paperCaliperInchesPerPage = job.Edition.Paper == PublicationPaper.Cream ? 0.0025 : 0.002252,
                backCopy = coverDesign.BackCopy,
                title = coverDesign.Title,
                subtitle = coverDesign.Subtitle,
                author = coverDesign.Author,
                spineText = coverDesign.SpineText,
                backgroundColor = coverDesign.BackgroundColor,
                isbn = job.Edition.Isbn,
                barcodeMode = coverDesign.BarcodeMode.ToString(),
                assetId = document.CoverAsset?.Id,
                imageCropXPercent = coverDesign.ImageCropXPercent,
                imageCropYPercent = coverDesign.ImageCropYPercent,
                scene = coverScene,
            },
            assets = assets.Select(asset => new
            {
                id = asset.Id,
                asset.RelativePath,
                mediaType = asset.ContentType,
                byteLength = asset.Data.LongLength,
                asset.Sha256,
                asset.WidthPixels,
                asset.HeightPixels,
                asset.AltText,
            }).ToArray(),
            fonts = stagedFonts.Select(font => new
            {
                font.Id,
                font.FamilyKey,
                font.Weight,
                font.Italic,
                font.RelativePath,
                font.MediaType,
                byteLength = font.Data.LongLength,
                font.Sha256,
                embeddingRightsConfirmed = true,
            }).ToArray(),
        };
        return new PressPreparedRequest(payload, assets, stagedFonts);
    }

    private static PressStagedFont StageFont(PublishFontDocument face)
    {
        _ = Lorekeeper.Fonts.ProjectFontBinary.Normalize(face.Data, face.FileName);
        var extension = face.ContentType == "font/otf" ? ".otf" : ".ttf";
        return new PressStagedFont(
            face.FaceId.ToString("N"),
            face.FamilyKey,
            face.Weight,
            face.Italic,
            $"fonts/{face.FaceId:N}{extension}",
            face.ContentType,
            face.Data,
            Convert.ToHexStringLower(SHA256.HashData(face.Data)));
    }

    private static Guid DeterministicFontId(string value) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));

    internal static PressStagedAsset StageAsset(PublishAssetDocument asset)
    {
        if (asset.Data.Length is 0 or > 20_000_000)
            throw new InvalidOperationException($"Publication image '{asset.FileName}' must be non-empty and no larger than 20 MB.");
        var contentType = asset.ContentType.Trim().ToLowerInvariant() switch
        {
            "image/png" => "image/png",
            "image/jpeg" or "image/jpg" => "image/jpeg",
            _ => throw new InvalidOperationException($"Publication image '{asset.FileName}' must be PNG or JPEG."),
        };
        int width;
        int height;
        try
        {
            (width, height) = ReadRasterDimensions(asset.Data, contentType);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                $"Publication image '{asset.FileName}' ({asset.Id:N}) does not contain valid {contentType} data.",
                exception);
        }
        if (width <= 0 || height <= 0 || (long)width * height > 16_000_000)
        {
            throw new InvalidOperationException($"Publication image '{asset.FileName}' dimensions exceed the renderer limit.");
        }
        return new PressStagedAsset(
            asset.Id,
            $"assets/{asset.Id:N}{(contentType == "image/jpeg" ? ".jpg" : ".png")}",
            contentType,
            asset.Data,
            Convert.ToHexStringLower(SHA256.HashData(asset.Data)),
            width,
            height,
            asset.AltText);
    }

    private static (int Width, int Height) ReadRasterDimensions(byte[] data, string contentType)
    {
        ReadOnlySpan<byte> pngSignature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
        if (contentType == "image/png"
            && data.Length >= 24
            && data.AsSpan(0, 8).SequenceEqual(pngSignature))
        {
            return (
                System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16, 4)),
                System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20, 4)));
        }
        if (contentType == "image/jpeg" && data.Length >= 4 && data[0] == 0xff && data[1] == 0xd8)
        {
            var offset = 2;
            while (offset + 9 < data.Length)
            {
                if (data[offset++] != 0xff) continue;
                while (offset < data.Length && data[offset] == 0xff) offset++;
                if (offset >= data.Length) break;
                var marker = data[offset++];
                if (marker is 0xd8 or 0xd9) continue;
                if (offset + 2 > data.Length) break;
                var segmentLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
                if (segmentLength < 2 || offset + segmentLength > data.Length) break;
                if (marker is >= 0xc0 and <= 0xc3 or >= 0xc5 and <= 0xc7 or >= 0xc9 and <= 0xcb or >= 0xcd and <= 0xcf)
                {
                    return (
                        System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 5, 2)),
                        System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 3, 2)));
                }
                offset += segmentLength;
            }
        }
        throw new InvalidOperationException("Publication image headers are invalid or unsupported.");
    }

    private async Task<PressResponse> InvokeAsync(
        Guid jobId,
        PressPreparedRequest request,
        CancellationToken cancellationToken)
    {
        var jobRoot = JobRoot(jobId);
        var inputRoot = Path.Combine(jobRoot, "input");
        Directory.CreateDirectory(Path.Combine(inputRoot, "assets"));
        foreach (var asset in request.Assets)
        {
            var path = Path.GetFullPath(Path.Combine(inputRoot, asset.RelativePath));
            if (!IsContainedBy(inputRoot, path))
                throw new InvalidOperationException("A staged Press asset escaped the bounded input directory.");
            await File.WriteAllBytesAsync(path, asset.Data, cancellationToken);
        }
        Directory.CreateDirectory(Path.Combine(inputRoot, "fonts"));
        foreach (var font in request.Fonts)
        {
            var path = Path.GetFullPath(Path.Combine(inputRoot, font.RelativePath));
            if (!IsContainedBy(inputRoot, path))
                throw new InvalidOperationException("A staged Press font escaped the bounded input directory.");
            await File.WriteAllBytesAsync(path, font.Data, cancellationToken);
        }
        await File.WriteAllBytesAsync(
            Path.Combine(inputRoot, "request.json"),
            SerializeRequest(request.Payload),
            cancellationToken);
        var start = pressRuntime.CreateStartInfo(jobId, jobRoot);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("The configured press renderer could not be started.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(
            Math.Clamp(options.Value.RenderTimeoutSeconds, 10, 1800)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var stdoutTask = ReadBoundedAsync(process.StandardOutput, 4 * 1024 * 1024, linked.Token);
        var stderrTask = ReadBoundedAsync(process.StandardError, 64 * 1024, linked.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (string.IsNullOrWhiteSpace(stdout))
                throw new InvalidOperationException($"Press renderer returned no response. {Limit(stderr)}");
            var response = JsonSerializer.Deserialize<PressResponse>(stdout, JsonOptions)
                ?? throw new InvalidOperationException("Press renderer returned an empty JSON response.");
            if (process.ExitCode != 0 && string.Equals(response.Status, "completed", StringComparison.Ordinal))
                throw new InvalidOperationException("The press renderer reported completion with a failing exit code.");
            return response;
        }
        catch (OperationCanceledException)
        {
            try
            {
                await File.WriteAllTextAsync(
                    Path.Combine(jobRoot, "cancel.requested"),
                    "cancelled",
                    CancellationToken.None);
            }
            catch (IOException)
            {
            }
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }
    }

    private static string JobRoot(Guid jobId) => Path.GetFullPath(Path.Combine(
        Path.GetTempPath(),
        "Lorekeeper",
        "press-jobs",
        jobId.ToString("N")));

    internal static byte[] SerializeRequest(object payload) =>
        JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);

    private static bool IsContainedBy(string rootPath, string candidatePath)
    {
        var relative = Path.GetRelativePath(rootPath, candidatePath);
        return !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static object BlockPayload(ManuscriptBlock block) => new
    {
        id = block.Id,
        type = block.Type.ToString(),
        block.StyleRole,
        block.HeadingLevel,
        assetId = block.ImageId,
        caption = string.Concat(block.Content.Select(inline => inline.Text)),
        block.Decorative,
        block.AltText,
        language = block.Language,
        accessibilityRole = block.AccessibilityRole.ToString(),
        presentation = block.FigurePresentation,
        paragraphPresentation = block.ParagraphPresentation,
        pageCompositionId = block.PageCompositionId,
        content = block.Content.Select(inline => new
        {
            type = inline.Type.ToString(),
            inline.Text,
            marks = inline.Marks.Select(mark => new
            {
                type = mark.Type.ToString(),
                mark.Value,
            }).ToArray(),
        }).ToArray(),
    };

    private static PublicationArtifactKind ParseKind(string kind) => kind switch
    {
        "interior-pdf" => PublicationArtifactKind.InteriorPdf,
        "cover-pdf" => PublicationArtifactKind.CoverPdf,
        "book-pdf" => PublicationArtifactKind.BookPdf,
        _ => throw new InvalidOperationException($"Unsupported renderer artifact kind '{kind}'."),
    };

    private static string Limit(string value) =>
        value.Length <= 2_000 ? value : value[..2_000];

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(Math.Min(maximumCharacters, 16_384));
        var buffer = new char[8_192];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                return builder.ToString();
            if (builder.Length + read > maximumCharacters)
                throw new InvalidOperationException("The press renderer exceeded its bounded response size.");
            builder.Append(buffer, 0, read);
        }
    }

    internal static void Cleanup(Guid jobId) => TryDeleteDirectory(JobRoot(jobId));

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record PressResponse(
        int ProtocolVersion,
        string? RendererVersion,
        string JobId,
        string Status,
        PressArtifact[]? Artifacts,
        PublicationRenderDiagnostic[]? Diagnostics,
        JsonElement Evidence,
        PressPageMap[]? PageMap);

    private sealed record PressArtifact(
        string Kind,
        string RelativePath,
        string MediaType,
        string Sha256,
        long ByteLength,
        int? PageCount);

    private sealed record PressPageMap(string ChapterId, string BlockId, int PageNumber);

    internal sealed record PressStagedAsset(
        Guid Id,
        string RelativePath,
        string ContentType,
        byte[] Data,
        string Sha256,
        int WidthPixels,
        int HeightPixels,
        string AltText);

    private sealed record PressStagedFont(
        string Id,
        string FamilyKey,
        int Weight,
        bool Italic,
        string RelativePath,
        string MediaType,
        byte[] Data,
        string Sha256);

    private sealed record PressPreparedRequest(
        object Payload,
        IReadOnlyList<PressStagedAsset> Assets,
        IReadOnlyList<PressStagedFont> Fonts);
}
