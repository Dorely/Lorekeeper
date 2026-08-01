using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lorekeeper.Publish;

public sealed class PublicationPressOptions
{
    public const string SectionName = "Publishing:Press";

    public string ExecutablePath { get; set; } = string.Empty;
    public string ProjectPath { get; set; } = "../Lorekeeper.Press.Weasy";
    public string CmykProfilePath { get; set; } = string.Empty;
    public string NativeLibraryPath { get; set; } = ".tmp/verified-native-binaries";
    public int RenderTimeoutSeconds { get; set; } = 300;
}

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
    IPublishService publishing,
    IPublicationRenderQueue queue,
    IProjectMutationCoordinator projectMutations) : IPublicationRenderService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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
        if (edition.Format != PublicationEditionFormat.Paperback)
            throw new InvalidOperationException("PDF rendering is available only for paperback editions.");
        var document = await publishing.GetDocumentAsync(projectId, editionId, cancellationToken);
        if (HasUnsupportedInteriorVisuals(document))
        {
            throw new InvalidOperationException(
                "The installed prose-only Preview press runtime cannot render Picture Pages, illustrated-prose images, semantic figures, or edition image placements. Remove them from this paperback edition or use EPUB export.");
        }
        if (edition.Vendor == PublicationVendor.IngramSpark && document.CoverAsset is not null)
        {
            throw new InvalidOperationException(
                "Selected cover images are not yet supported by the contained Ingram CMYK Preview profile.");
        }
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
            ProfileId = PublicationRenderProcessor.ProfileFor(edition.Vendor),
        };
        db.PublicationRenderJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        await queue.EnqueueAsync(job.Id, CancellationToken.None);
        return View(job, [], job.SourceFingerprint);
    }

    private static bool HasUnsupportedInteriorVisuals(PublishDocument document) =>
        document.Placements.Count > 0
        || document.Matter.Any(item =>
            item.Manuscript.Content.Any(block => block.Type == ManuscriptBlockType.Figure))
        || document.Sections.SelectMany(section => section.Chapters).Any(chapter =>
            chapter.VisualMode != ChapterVisualMode.Prose
            || chapter.IllustrationLayout.Images.Count > 0
            || chapter.Manuscript.Content.Any(block => block.Type == ManuscriptBlockType.Figure));

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
        return jobs.Select(job => View(job, job.Artifacts, fingerprint)).ToList();
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
        return View(job, job.Artifacts, fingerprint);
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
        var leftPages = left.Artifacts.FirstOrDefault(a => a.Kind == PublicationArtifactKind.InteriorPdf)?.PageCount;
        var rightPages = right.Artifacts.FirstOrDefault(a => a.Kind == PublicationArtifactKind.InteriorPdf)?.PageCount;
        var delta = (rightPages ?? 0) - (leftPages ?? 0);
        var explanation = left.SourceFingerprint == right.SourceFingerprint
            ? $"The same source produced a {delta:+#;-#;0}-page difference; inspect renderer/profile versions."
            : $"Content or edition settings changed, moving {movements.Count} mapped blocks and changing the interior by {delta:+#;-#;0} pages.";
        return new(leftJobId, rightJobId, leftPages, rightPages, delta, movements.Count, movements, explanation);
    }

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
        return (await db.PublicationArtifacts.AsNoTracking()
                .Where(artifact => artifact.EditionId == editionId && artifact.Edition.ProjectId == projectId)
                .OrderByDescending(artifact => artifact.CreatedAt)
                .ToListAsync(cancellationToken))
            .Select(artifact => ArtifactView(artifact, fingerprint))
            .ToList();
    }

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
        string currentFingerprint) =>
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
            artifacts.Select(artifact => ArtifactView(artifact, currentFingerprint)).ToList(),
            job.CreatedAt,
            job.StartedAt,
            job.CompletedAt);

    private static PublicationArtifactView ArtifactView(PublicationArtifact artifact, string currentFingerprint) =>
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
            !string.Equals(artifact.SourceFingerprint, currentFingerprint, StringComparison.Ordinal));

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
        job.DiagnosticsJson = JsonSerializer.Serialize(
            new[] { new PublicationRenderDiagnostic("error", "PRESS_RUNTIME_FAILED", exception.Message) });
        job.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class PublicationRenderProcessor(
    AppDbContext db,
    IPublishService publishing,
    IPublicationEditionService editions,
    IPublicationCoverService covers,
    IOptions<PublicationPressOptions> options,
    IWebHostEnvironment environment)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string ProfileFor(PublicationVendor vendor) => vendor == PublicationVendor.IngramSpark
        ? "ingram-pdf-x1a-preview-v1"
        : "kdp-paperback-6x9-preview-v1";

    public async Task ProcessAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await db.PublicationRenderJobs
            .Include(candidate => candidate.Edition)
            .FirstOrDefaultAsync(candidate => candidate.Id == jobId, cancellationToken)
            ?? throw new KeyNotFoundException("Publication render job not found.");
        if (job.Status == PublicationRenderStatus.Cancelled || job.CancellationRequested)
            throw new OperationCanceledException(cancellationToken);
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
        var request = BuildRequest(job, document, coverDesign);
        job.ProgressPercent = 30;
        job.ProgressMessage = "Typesetting interior and cover";
        await db.SaveChangesAsync(cancellationToken);

        Cleanup(job.Id);
        var result = await InvokeAsync(job.Id, request, cancellationToken);
        if (result.ProtocolVersion != 2
            || !string.Equals(result.JobId, job.Id.ToString("N"), StringComparison.Ordinal))
            throw new InvalidOperationException("The press renderer returned a mismatched protocol or job identity.");
        var resultArtifacts = result.Artifacts
            ?? throw new InvalidOperationException("The press renderer omitted its artifact list.");
        var resultKinds = resultArtifacts.Select(artifact => artifact.Kind).Order().ToArray();
        if (!resultKinds.SequenceEqual(new[] { "cover-pdf", "interior-pdf" }, StringComparer.Ordinal))
            throw new InvalidOperationException("The press renderer must return exactly one interior and one cover PDF.");
        var interiorResult = resultArtifacts.Single(artifact => artifact.Kind == "interior-pdf");
        var coverResult = resultArtifacts.Single(artifact => artifact.Kind == "cover-pdf");
        if (interiorResult.PageCount is not > 0 or > 100_000)
            throw new InvalidOperationException("The renderer returned an invalid interior page count.");
        if (coverResult.PageCount != 1)
            throw new InvalidOperationException("The renderer must return a one-page full-wrap cover.");
        job.RendererVersion = result.RendererVersion ?? string.Empty;
        job.DiagnosticsJson = JsonSerializer.Serialize(result.Diagnostics ?? [], JsonOptions);
        job.EvidenceJson = result.Evidence.ValueKind == JsonValueKind.Undefined ? "{}" : result.Evidence.GetRawText();
        if (!string.Equals(result.Status, "completed", StringComparison.Ordinal))
            throw new InvalidOperationException(
                result.Diagnostics?.FirstOrDefault(d => d.Severity == "error")?.Message
                ?? "The press renderer rejected the job.");

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
        job.ProgressMessage = "Preview ready";
        job.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        Cleanup(job.Id);
    }

    private object BuildRequest(
        PublicationRenderJob job,
        PublishDocument document,
        PublicationCoverDesignView coverDesign)
    {
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
        return new
        {
            protocolVersion = 2,
            jobId = job.Id.ToString("N"),
            profile = job.ProfileId,
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
                sections,
                styles = document.NamedStyles.Select(style => new
                {
                    style.Name,
                    kind = style.Kind.ToString(),
                    style.SemanticRole,
                    definition = style.Definition,
                }).ToArray(),
            },
            trim = new
            {
                widthInches = document.Profile.PageWidthInches,
                heightInches = document.Profile.PageHeightInches,
                marginInches = document.Profile.PageMarginInches,
                bodyFontSizePoints = document.Profile.BodyFontSizePoints,
                bodyLineHeight = document.Profile.BodyLineHeight,
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
                imageDataUri = document.CoverAsset is null
                    ? string.Empty
                    : $"data:{document.CoverAsset.ContentType};base64,{Convert.ToBase64String(document.CoverAsset.Data)}",
                imageFocalXPercent = coverDesign.ImageFocalXPercent,
                imageFocalYPercent = coverDesign.ImageFocalYPercent,
            },
        };
    }

    private async Task<PressResponse> InvokeAsync(Guid jobId, object request, CancellationToken cancellationToken)
    {
        var pressOptions = options.Value;
        var outputRoot = JobRoot(jobId);
        Directory.CreateDirectory(outputRoot);
        var executable = pressOptions.ExecutablePath.Trim();
        var projectPath = Path.GetFullPath(
            Path.Combine(environment.ContentRootPath, pressOptions.ProjectPath));
        var start = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(executable) ? "uv" : executable,
            WorkingDirectory = projectPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (string.IsNullOrWhiteSpace(executable))
        {
            start.ArgumentList.Add("run");
            start.ArgumentList.Add("--frozen");
            start.ArgumentList.Add("lorekeeper-press-weasy");
        }
        start.ArgumentList.Add("--output-root");
        start.ArgumentList.Add(outputRoot);
        if (!string.IsNullOrWhiteSpace(pressOptions.CmykProfilePath))
        {
            start.ArgumentList.Add("--cmyk-profile");
            start.ArgumentList.Add(Path.GetFullPath(pressOptions.CmykProfilePath));
        }
        start.Environment.Clear();
        var inheritedPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var nativeLibraryPath = Path.GetFullPath(Path.Combine(projectPath, pressOptions.NativeLibraryPath));
        if (string.IsNullOrWhiteSpace(executable) && Directory.Exists(nativeLibraryPath))
        {
            start.Environment["WEASYPRINT_DLL_DIRECTORIES"] = nativeLibraryPath;
            start.Environment["PATH"] = $"{nativeLibraryPath}{Path.PathSeparator}{inheritedPath}";
        }
        else
        {
            start.Environment["PATH"] = inheritedPath;
        }
        start.Environment["PYTHONUTF8"] = "1";
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("The configured press renderer could not be started.");
        var input = JsonSerializer.Serialize(request, JsonOptions);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(
            Math.Clamp(pressOptions.RenderTimeoutSeconds, 10, 1800)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await process.StandardInput.WriteAsync(input.AsMemory(), linked.Token);
            process.StandardInput.Close();
            var stdoutTask = ReadBoundedAsync(process.StandardOutput, 4 * 1024 * 1024, linked.Token);
            var stderrTask = ReadBoundedAsync(process.StandardError, 64 * 1024, linked.Token);
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

    private static object BlockPayload(ManuscriptBlock block) => new
    {
        id = block.Id,
        type = block.Type.ToString(),
        block.StyleRole,
        block.HeadingLevel,
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
}
