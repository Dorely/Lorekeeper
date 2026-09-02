using System.Diagnostics;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Lorekeeper.Composition;
using Lorekeeper.Fonts;
using Lorekeeper.Images;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Startup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lorekeeper.Publish;

public sealed record PublicationRenderJobView(
    Guid Id,
    Guid? EditionId,
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
    int? Page = null,
    string? SourceKind = null,
    string? SourceId = null);

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
    Task<PublicationRenderJobView> RequestCoreAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<PublicationRenderJobView> CancelAsync(Guid projectId, Guid editionId, Guid jobId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PublicationRenderJobView>> ListAsync(Guid projectId, Guid editionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PublicationRenderJobView>> ListCoreAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<PublicationRenderJobView> CancelCoreAsync(Guid projectId, Guid jobId, CancellationToken cancellationToken = default);
    Task<PublicationRenderJobView> GetAsync(Guid projectId, Guid editionId, Guid jobId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PublicationPageMapView>> GetPageMapAsync(Guid projectId, Guid editionId, Guid jobId, CancellationToken cancellationToken = default);
    Task<PublicationRenderComparison> CompareAsync(Guid projectId, Guid editionId, Guid leftJobId, Guid rightJobId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PublicationArtifactView>> ListArtifactsAsync(Guid projectId, Guid editionId, CancellationToken cancellationToken = default);
    Task<PublicationArtifact?> GetArtifactAsync(Guid projectId, Guid artifactId, CancellationToken cancellationToken = default);
}

public sealed record PublicationPaginationView(
    int PageCount,
    string PaginationFingerprint,
    string RendererVersion,
    string ProfileId,
    bool WasPrepared);

public interface IPublicationPaginationService
{
    Task<PublicationPaginationView> EnsureCurrentAsync(
        Guid projectId,
        Guid editionId,
        CancellationToken cancellationToken = default);
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
    IAppDatabaseOperationFactory database,
    IPublicationEditionService editions,
    IPublicationBookService books,
    IPublicationRenderQueue queue,
    IPublicationPressRuntime pressRuntime) : IPublicationRenderService
{
    private static readonly Expression<Func<PublicationArtifact, PublicationArtifact>> ArtifactMetadataProjection =
        artifact => new PublicationArtifact
        {
            Id = artifact.Id,
            ProjectId = artifact.ProjectId,
            TargetKind = artifact.TargetKind,
            EditionId = artifact.EditionId,
            RenderJobId = artifact.RenderJobId,
            Kind = artifact.Kind,
            FileName = artifact.FileName,
            MediaType = artifact.MediaType,
            Sha256 = artifact.Sha256,
            ByteLength = artifact.ByteLength,
            PageCount = artifact.PageCount,
            SourceFingerprint = artifact.SourceFingerprint,
            PaginationFingerprint = artifact.PaginationFingerprint,
            RendererVersion = artifact.RendererVersion,
            ProfileId = artifact.ProfileId,
            IsLegacy = artifact.IsLegacy,
            CreatedAt = artifact.CreatedAt,
        };

    public PublicationRenderService(
        IAppDatabaseOperationFactory database,
        IPublicationEditionService editions,
        IPublicationRenderQueue queue,
        IPublicationPressRuntime pressRuntime)
        : this(database, editions, new PublicationBookService(database), queue, pressRuntime)
    {
    }

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

    public async Task<PublicationRenderJobView> RequestCoreAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        _ = await books.GetOrCreateAsync(projectId, cancellationToken);
        var runtimeReadiness = GetRuntimeReadiness(PublicationEditionFormat.DigitalPdf, PublicationVendor.Generic);
        if (!runtimeReadiness.IsReady)
            throw new InvalidOperationException(runtimeReadiness.Message);
        if (await db.PublicationRenderJobs.AnyAsync(job => job.ProjectId == projectId
            && job.TargetKind == PublicationTargetKind.CoreBook
            && (job.Status == PublicationRenderStatus.Queued || job.Status == PublicationRenderStatus.Rendering), cancellationToken))
            throw new InvalidOperationException("Core Book already has an active reading-PDF preparation.");
        var job = new PublicationRenderJob
        {
            ProjectId = projectId,
            TargetKind = PublicationTargetKind.CoreBook,
            EditionId = null,
            SourceFingerprint = await books.GetSourceFingerprintAsync(projectId, cancellationToken),
            PaginationFingerprint = await books.GetSourceFingerprintAsync(projectId, cancellationToken),
            RendererVersion = pressRuntime.GetDescription().RendererVersion,
            ProfileId = PublicationRenderProcessor.ProfileFor(PublicationEditionFormat.DigitalPdf, PublicationVendor.Generic),
            Status = PublicationRenderStatus.Queued,
            ProgressMessage = "Reading PDF queued",
        };
        db.PublicationRenderJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        await queue.EnqueueAsync(job.Id, cancellationToken);
        return View(job, [], job.SourceFingerprint, CurrentRendererVersion());
    }

    public async Task<IReadOnlyList<PublicationRenderJobView>> ListCoreAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var fingerprint = await books.GetSourceFingerprintAsync(projectId, cancellationToken);
        var jobs = await db.PublicationRenderJobs.AsNoTracking()
            .Where(job => job.ProjectId == projectId && job.TargetKind == PublicationTargetKind.CoreBook)
            .OrderByDescending(job => job.CreatedAt).ToListAsync(cancellationToken);
        var artifactsByJob = await ReadArtifactMetadataByJobAsync(jobs.Select(job => job.Id), cancellationToken);
        return jobs.Select(job => View(job, artifactsByJob.GetValueOrDefault(job.Id, []), fingerprint, CurrentRendererVersion())).ToList();
    }

    public async Task<PublicationRenderJobView> CancelCoreAsync(Guid projectId, Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var job = await db.PublicationRenderJobs.Include(item => item.Artifacts).SingleAsync(
            item => item.ProjectId == projectId && item.TargetKind == PublicationTargetKind.CoreBook && item.Id == jobId,
            cancellationToken);
        if (job.Status is PublicationRenderStatus.Queued or PublicationRenderStatus.Rendering)
        {
            job.CancellationRequested = true;
            job.ProgressMessage = "Cancellation requested";
            queue.Cancel(job.Id);
            await db.SaveChangesAsync(cancellationToken);
        }
        var fingerprint = await books.GetSourceFingerprintAsync(projectId, cancellationToken);
        return View(job, job.Artifacts, fingerprint, CurrentRendererVersion());
    }

    public async Task<PublicationRenderJobView> RequestAsync(
        Guid projectId,
        Guid editionId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var edition = await db.PublicationEditions.AsNoTracking().FirstOrDefaultAsync(
            candidate => candidate.ProjectId == projectId && candidate.Id == editionId,
            cancellationToken) ?? throw new KeyNotFoundException("Publication release not found.");
        if (edition.Status != PublicationEditionStatus.Draft)
            throw new InvalidOperationException("Archived editions cannot be rendered.");
        if (edition.Format is not (PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover or PublicationEditionFormat.DigitalPdf))
            throw new InvalidOperationException("PDF rendering is available for paperback, hardcover, and Digital PDF editions.");
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
            ProjectId = projectId,
            TargetKind = PublicationTargetKind.Release,
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
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
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
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var fingerprint = await editions.GetSourceFingerprintAsync(projectId, editionId, cancellationToken);
        var jobs = await db.PublicationRenderJobs
            .AsNoTracking()
            .Where(job => job.EditionId == editionId && job.ProjectId == projectId)
            .OrderByDescending(job => job.CreatedAt)
            .ToListAsync(cancellationToken);
        var artifactsByJob = await ReadArtifactMetadataByJobAsync(jobs.Select(job => job.Id), cancellationToken);
        var rendererVersion = CurrentRendererVersion();
        return jobs.Select(job => View(job, artifactsByJob.GetValueOrDefault(job.Id, []), fingerprint, rendererVersion)).ToList();
    }

    public async Task<PublicationRenderJobView> GetAsync(
        Guid projectId,
        Guid editionId,
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var fingerprint = await editions.GetSourceFingerprintAsync(projectId, editionId, cancellationToken);
        var job = await db.PublicationRenderJobs
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == jobId
                && candidate.EditionId == editionId
                && candidate.ProjectId == projectId, cancellationToken)
            ?? throw new KeyNotFoundException("Publication render job not found.");
        var artifacts = await db.PublicationArtifacts.AsNoTracking()
            .Where(artifact => artifact.RenderJobId == job.Id)
            .Select(ArtifactMetadataProjection)
            .ToListAsync(cancellationToken);
        return View(job, artifacts, fingerprint, CurrentRendererVersion());
    }

    public async Task<IReadOnlyList<PublicationPageMapView>> GetPageMapAsync(
        Guid projectId,
        Guid editionId,
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
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
            : $"Content or release settings changed, moving {movements.Count} mapped blocks and changing the publication by {delta:+#;-#;0} pages.";
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
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var artifact = await db.PublicationArtifacts.AsNoTracking().FirstOrDefaultAsync(
            artifact => artifact.Id == artifactId && artifact.ProjectId == projectId,
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
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var fingerprint = await editions.GetSourceFingerprintAsync(projectId, editionId, cancellationToken);
        var rendererVersion = CurrentRendererVersion();
        var editionFormat = await db.PublicationEditions.AsNoTracking()
            .Where(edition => edition.Id == editionId && edition.ProjectId == projectId)
            .Select(edition => edition.Format)
            .SingleAsync(cancellationToken);
        var packageRuntimeVersion = PublicationPackageService.PackageRuntimeVersion(editionFormat);
        return (await db.PublicationArtifacts.AsNoTracking()
                .Where(artifact => artifact.EditionId == editionId && artifact.ProjectId == projectId)
                .OrderByDescending(artifact => artifact.CreatedAt)
                .Select(ArtifactMetadataProjection)
                .ToListAsync(cancellationToken))
            .Select(artifact => ArtifactView(artifact, fingerprint, rendererVersion, packageRuntimeVersion))
            .ToList();
    }

    private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<PublicationArtifact>>> ReadArtifactMetadataByJobAsync(
        IEnumerable<Guid> jobIds,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var ids = jobIds.ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<PublicationArtifact>>();
        return (await db.PublicationArtifacts.AsNoTracking()
                .Where(artifact => artifact.RenderJobId != null && ids.Contains(artifact.RenderJobId.Value))
                .Select(ArtifactMetadataProjection)
                .ToListAsync(cancellationToken))
            .GroupBy(artifact => artifact.RenderJobId!.Value)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<PublicationArtifact>)group.ToList());
    }

    private string? CurrentRendererVersion() => pressRuntime.GetReadiness().IsReady
        ? pressRuntime.GetDescription().RendererVersion
        : null;

    private async Task<PublicationRenderJob> GetTrackedAsync(
        Guid projectId,
        Guid editionId,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        return await db.PublicationRenderJobs.FirstOrDefaultAsync(
                    job => job.Id == jobId && job.EditionId == editionId && job.ProjectId == projectId,
                    cancellationToken) ?? throw new KeyNotFoundException("Publication render job not found.");
    }
    private async Task EnsureJobAsync(Guid projectId, Guid editionId, Guid jobId, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        if (!await db.PublicationRenderJobs.AnyAsync(
            job => job.Id == jobId && job.EditionId == editionId && job.ProjectId == projectId,
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
            PublicationDiagnosticText.SanitizeUserFacing(job.ProgressMessage),
            job.CancellationRequested,
            DeserializeDiagnostics(job.DiagnosticsJson),
            artifacts.Select(artifact => ArtifactView(artifact, currentFingerprint, currentRendererVersion)).ToList(),
            job.CreatedAt,
            job.StartedAt,
            job.CompletedAt);

    private static PublicationArtifactView ArtifactView(
        PublicationArtifact artifact,
        string currentFingerprint,
        string? currentRendererVersion,
        string? currentPackageRuntimeVersion = null)
    {
        var pressArtifact = artifact.Kind is PublicationArtifactKind.ReadingPdf
            or PublicationArtifactKind.InteriorPdf
            or PublicationArtifactKind.PerfectBoundCoverPdf
            or PublicationArtifactKind.CaseCoverPdf
            or PublicationArtifactKind.DustJacketPdf
            or PublicationArtifactKind.BookPdf;
        var packageArtifact = artifact.Kind is PublicationArtifactKind.Epub
            or PublicationArtifactKind.FrontCoverImage
            or PublicationArtifactKind.PublicationPackage
            or PublicationArtifactKind.PreflightReport
            or PublicationArtifactKind.Manifest;
        var isStale = !string.Equals(artifact.SourceFingerprint, currentFingerprint, StringComparison.Ordinal)
            || (pressArtifact
                && currentRendererVersion is not null
                && !string.Equals(artifact.RendererVersion, currentRendererVersion, StringComparison.Ordinal))
            || (packageArtifact
                && currentPackageRuntimeVersion is not null
                && !string.Equals(artifact.RendererVersion, currentPackageRuntimeVersion, StringComparison.Ordinal));
        return new(
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
            isStale);
    }

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
    IAppDatabaseOperationFactory database,
    IApplicationStartupState startup,
    ILogger<PublicationRenderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (!await startup.WaitForDatabaseReadyAsync(stoppingToken))
                return;
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
        await using var operation = await database.OpenReadAsync(cancellationToken);
        var db = operation.Db;
        return await db.PublicationRenderJobs.AsNoTracking()
            .Where(job => job.Id == jobId)
            .Select(job => job.CancellationRequested
                || job.Status == PublicationRenderStatus.Cancelled)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private async Task RecoverInterruptedJobsAsync(CancellationToken cancellationToken)
    {
        List<Guid> recoveredIds = [];
        await using var operation = await database.OpenWriteAsync(cancellationToken);
        var db = operation.Db;
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
            recoveredIds.Add(job.Id);
        }
        await db.SaveChangesAsync(cancellationToken);
        await operation.DisposeAsync();
        foreach (var jobId in recoveredIds)
            await queue.EnqueueAsync(jobId, cancellationToken);
    }

    private async Task MarkCancelledAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenWriteAsync(cancellationToken);
        var db = operation.Db;
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
        await using var operation = await database.OpenWriteAsync(cancellationToken);
        var db = operation.Db;
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
    IAppDatabaseOperationFactory database,
    IPublishService publishing,
    IPublicationEditionService editions,
    IPublicationBookService books,
    IPublicationCoverService covers,
    IProjectFontService projectFonts,
    IPublicationPressRuntime pressRuntime,
    IPrintArtifactProfileRegistry printArtifactProfiles,
    IOptions<PublicationPressOptions> options,
    IOptions<ProjectImageGenerationOptions>? imageOptions = null) : IPublicationPaginationService
{
    public PublicationRenderProcessor(
        IAppDatabaseOperationFactory database,
        IPublishService publishing,
        IPublicationEditionService editions,
        IPublicationCoverService covers,
        IProjectFontService projectFonts,
        IPublicationPressRuntime pressRuntime,
        IOptions<PublicationPressOptions> options)
        : this(database, publishing, editions, null!, covers, projectFonts, pressRuntime,
            new PrintArtifactProfileRegistry(), options)
    {
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string ProfileFor(PublicationEditionFormat format, PublicationVendor vendor) =>
        format == PublicationEditionFormat.DigitalPdf
            ? "generic-digital-pdf-v1"
            : vendor == PublicationVendor.Lulu
                ? "lulu-print-v1"
                : vendor == PublicationVendor.BarnesAndNoblePress
                    ? "bn-print-pdfa1b-v1"
            : format == PublicationEditionFormat.Hardcover && vendor == PublicationVendor.AmazonKdp
                ? "kdp-hardcover-v1"
            : format == PublicationEditionFormat.Hardcover && vendor == PublicationVendor.IngramSpark
                ? "ingram-print-pdfx1a-v2"
            : vendor == PublicationVendor.IngramSpark
        ? "ingram-print-pdfx1a-v2"
        : vendor == PublicationVendor.AmazonKdp
            ? "kdp-paperback-v2"
            : "generic-print-v2";

    public async Task<PublicationPaginationView> EnsureCurrentAsync(
        Guid projectId,
        Guid editionId,
        CancellationToken cancellationToken = default)
    {
        PublicationEdition edition;
        await using (var operation = await database.OpenReadAsync(cancellationToken))
        {
            edition = await operation.Db.PublicationEditions.AsNoTracking().SingleOrDefaultAsync(
                item => item.ProjectId == projectId && item.Id == editionId,
                cancellationToken) ?? throw new KeyNotFoundException("Publication release not found.");
        }
        if (edition.Status != PublicationEditionStatus.Draft)
            throw new InvalidOperationException("Archived editions cannot be paginated.");
        if (edition.Format is not (PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover))
            throw new InvalidOperationException("Interior pagination is required only for physical print editions.");

        var runtime = pressRuntime.GetDescription();
        var profileId = ProfileFor(edition.Format, edition.Vendor);
        if (!runtime.Profiles.Contains(profileId, StringComparer.Ordinal))
            throw new InvalidOperationException("The selected print profile is unsupported by the installed Lorekeeper Press runtime.");
        var paginationFingerprint = await editions.GetPaginationFingerprintAsync(projectId, editionId, cancellationToken);
        PublicationInteriorPagination? snapshot;
        await using (var operation = await database.OpenReadAsync(cancellationToken))
        {
            snapshot = await operation.Db.PublicationInteriorPaginations.AsNoTracking()
                .SingleOrDefaultAsync(item => item.EditionId == editionId, cancellationToken);
        }
        if (snapshot is { PageCount: > 0 }
            && string.Equals(snapshot.PaginationFingerprint, paginationFingerprint, StringComparison.Ordinal)
            && string.Equals(snapshot.RendererVersion, runtime.RendererVersion, StringComparison.Ordinal))
        {
            return new(
                snapshot.PageCount,
                paginationFingerprint,
                runtime.RendererVersion,
                snapshot.ProfileId,
                WasPrepared: false);
        }

        (int PageCount, string ProfileId)? artifactPagination;
        await using (var operation = await database.OpenReadAsync(cancellationToken))
        {
            var candidate = await operation.Db.PublicationArtifacts.AsNoTracking()
                .Where(artifact => artifact.ProjectId == projectId
                    && artifact.EditionId == editionId
                    && artifact.Kind == PublicationArtifactKind.InteriorPdf
                    && !artifact.IsLegacy
                    && artifact.PaginationFingerprint == paginationFingerprint
                    && artifact.RendererVersion == runtime.RendererVersion)
                .OrderByDescending(artifact => artifact.CreatedAt)
                .Select(artifact => new { artifact.PageCount, artifact.ProfileId })
                .Where(artifact => artifact.PageCount > 0)
                .FirstOrDefaultAsync(cancellationToken);
            artifactPagination = candidate?.PageCount is int pageCount
                ? (pageCount, candidate.ProfileId)
                : null;
        }
        if (artifactPagination is { PageCount: > 0 } cachedArtifact)
        {
            await StorePaginationSnapshotAsync(
                projectId,
                editionId,
                cachedArtifact.PageCount,
                paginationFingerprint,
                runtime.RendererVersion,
                cachedArtifact.ProfileId,
                cancellationToken);
            return new(
                cachedArtifact.PageCount,
                paginationFingerprint,
                runtime.RendererVersion,
                cachedArtifact.ProfileId,
                WasPrepared: false);
        }

        var document = await publishing.GetDocumentAsync(projectId, editionId, cancellationToken);
        var jobId = Guid.NewGuid();
        var job = new PublicationRenderJob
        {
            Id = jobId,
            ProjectId = projectId,
            TargetKind = PublicationTargetKind.Release,
            EditionId = editionId,
            Edition = edition,
            SourceFingerprint = await editions.GetSourceFingerprintAsync(projectId, editionId, cancellationToken),
            PaginationFingerprint = paginationFingerprint,
            ProfileId = profileId,
            RendererVersion = runtime.RendererVersion,
        };
        try
        {
            var request = await BuildRequestAsync(
                job,
                document,
                coverDesign: null,
                cancellationToken,
                layoutTraceMode: "pagination");
            var response = await InvokePaginationAsync(jobId, request, cancellationToken);
            if (response.ProtocolVersion != 11)
                throw new InvalidOperationException($"The press renderer returned pagination protocol {response.ProtocolVersion}; protocol 11 is required.");
            if (!string.Equals(response.RendererVersion, runtime.RendererVersion, StringComparison.Ordinal))
                throw new InvalidOperationException("The press renderer returned a different renderer version while paginating the interior.");
            if (!string.Equals(response.JobId, jobId.ToString("N"), StringComparison.Ordinal))
                throw new InvalidOperationException("The press renderer returned pagination for a different job.");
            if (response.PageCount is <= 0 or > 100_000)
                throw new InvalidOperationException("The press renderer returned an invalid interior page count.");

            await StorePaginationSnapshotAsync(
                projectId,
                editionId,
                response.PageCount,
                paginationFingerprint,
                runtime.RendererVersion,
                profileId,
                cancellationToken);
            return new(response.PageCount, paginationFingerprint, runtime.RendererVersion, profileId, WasPrepared: true);
        }
        finally
        {
            Cleanup(jobId);
        }
    }

    private async Task StorePaginationSnapshotAsync(
        Guid projectId,
        Guid editionId,
        int pageCount,
        string paginationFingerprint,
        string rendererVersion,
        string profileId,
        CancellationToken cancellationToken)
    {
        var currentFingerprint = await editions.GetPaginationFingerprintAsync(projectId, editionId, cancellationToken);
        if (!string.Equals(currentFingerprint, paginationFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("The edition changed during interior pagination. Try opening the cover again.");

        await using var write = await database.OpenWriteAsync(projectId, cancellationToken);
        var editionExists = await write.Db.PublicationEditions.AnyAsync(
            item => item.ProjectId == projectId && item.Id == editionId,
            cancellationToken);
        if (!editionExists)
            throw new KeyNotFoundException("Publication release not found.");
        var stored = await write.Db.PublicationInteriorPaginations.SingleOrDefaultAsync(
            item => item.EditionId == editionId,
            cancellationToken);
        if (stored is null)
        {
            stored = new PublicationInteriorPagination { EditionId = editionId };
            write.Db.PublicationInteriorPaginations.Add(stored);
        }
        stored.PageCount = pageCount;
        stored.PaginationFingerprint = paginationFingerprint;
        stored.RendererVersion = rendererVersion;
        stored.ProfileId = profileId;
        stored.UpdatedAt = DateTime.UtcNow;
        await write.SaveChangesAsync(cancellationToken);
    }

    public async Task ProcessAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var job = await db.PublicationRenderJobs
            .Include(candidate => candidate.Edition)
            .FirstOrDefaultAsync(candidate => candidate.Id == jobId, cancellationToken)
            ?? throw new KeyNotFoundException("Publication render job not found.");
        if (job.Status == PublicationRenderStatus.Cancelled || job.CancellationRequested)
            throw new OperationCanceledException(cancellationToken);
        var coreTarget = job.TargetKind == PublicationTargetKind.CoreBook;
        var edition = job.Edition;
        if (!coreTarget && edition is null)
            throw new InvalidOperationException("The queued release render no longer has a publication release.");
        if (!IsSupportedProfile(job.ProfileId)
            || (!coreTarget && !string.Equals(job.ProfileId, edition!.VendorProfileVersion, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "The edition's publication profile is unsupported by Lorekeeper Press.");
        }
        var runtime = pressRuntime.GetDescription();
        if (!string.Equals(runtime.PrintArtifactProfileRegistryVersion, printArtifactProfiles.Version, StringComparison.Ordinal)
            || !string.Equals(runtime.PrintArtifactProfileRegistrySha256, printArtifactProfiles.Sha256, StringComparison.Ordinal))
            throw new InvalidOperationException("The installed Press renderer has a different print-artifact profile registry.");
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

        var document = coreTarget
            ? await publishing.GetCoreDocumentAsync(job.ProjectId, cancellationToken)
            : await publishing.GetDocumentAsync(job.ProjectId, edition!.Id, cancellationToken);
        var fingerprintBeforeRender = coreTarget
            ? await books.GetSourceFingerprintAsync(job.ProjectId, cancellationToken)
            : await editions.GetSourceFingerprintAsync(job.ProjectId, edition!.Id, cancellationToken);
        if (!string.Equals(fingerprintBeforeRender, job.SourceFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("The edition changed while this render was queued. Request a new render.");
        if (!coreTarget
            && edition!.Format is PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover)
        {
            job.ProgressPercent = 20;
            job.ProgressMessage = "Calculating current interior pagination";
            await db.SaveChangesAsync(cancellationToken);
            var pagination = await EnsureCurrentAsync(job.ProjectId, edition.Id, cancellationToken);
            if (!string.Equals(
                pagination.PaginationFingerprint,
                job.PaginationFingerprint,
                StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The edition changed while its cover geometry was being prepared. Request a new render.");
            }
        }
        var expectedChapterPageMap = document.Sections
            .SelectMany(section => section.Chapters)
            .SelectMany(chapter => chapter.Manuscript.Content.Where(IsPressPageMappedBlock).Select(block => (
                OwnerId: chapter.Id.ToString("D"),
                BlockId: block.Id)))
            .ToHashSet();
        var expectedPublicationSectionPageMap = document.PublicationSections
            .Where(section => section.SystemRole != PublicationSectionSystemRole.Contents)
            .SelectMany(section => section.Manuscript.Content.Where(IsPressPageMappedBlock).Select(block => (
                OwnerId: section.Id.ToString("D"),
                BlockId: block.Id)))
            .ToHashSet();
        var expectedPageMap = expectedChapterPageMap
            .Concat(expectedPublicationSectionPageMap)
            .ToHashSet();
        var coverDesign = coreTarget
            ? CoreCoverView(document)
            : await covers.GetAsync(job.ProjectId, edition!.Id, cancellationToken);
        var coverDiagnostics = StructuredCoverDiagnostics(coverDesign);
        var coverErrors = coverDiagnostics
            .Where(diagnostic => string.Equals(diagnostic.Severity, "error", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (coverErrors.Count > 0)
        {
            job.Status = PublicationRenderStatus.Failed;
            job.ProgressPercent = 100;
            job.ProgressMessage = "Cover needs attention";
            job.DiagnosticsJson = JsonSerializer.Serialize(coverErrors.Select(diagnostic => new PublicationRenderDiagnostic(
                diagnostic.Severity,
                diagnostic.Code,
                diagnostic.Message,
                SourceKind: "cover",
                SourceId: diagnostic.ObjectId?.ToString("D"))), JsonOptions);
            job.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return;
        }
        var request = await BuildRequestAsync(job, document, coverDesign, cancellationToken);
        job.ProgressPercent = 30;
        job.ProgressMessage = "Typesetting interior and cover";
        await db.SaveChangesAsync(cancellationToken);

        Cleanup(job.Id);
        var result = await InvokeAsync(
            job.Id,
            request,
            async progress =>
            {
                var mapped = 30 + progress.Percent * 60 / 100;
                if (mapped <= job.ProgressPercent)
                    return;
                job.ProgressPercent = mapped;
                job.ProgressMessage = progress.Message;
                await db.SaveChangesAsync(cancellationToken);
            },
            cancellationToken);
        if (result.ProtocolVersion != 11)
            throw new InvalidOperationException($"The press renderer returned protocol {result.ProtocolVersion}; protocol 11 is required.");
        if (result.JobId is not null
            && !string.Equals(result.JobId, job.Id.ToString("N"), StringComparison.Ordinal))
            throw new InvalidOperationException("The press renderer returned a response for a different job.");
        if (result.JobId is null && string.Equals(result.Status, "completed", StringComparison.Ordinal))
            throw new InvalidOperationException("The press renderer returned an unbound completion response.");
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
        var digitalOutput = coreTarget || edition!.Format == PublicationEditionFormat.DigitalPdf;
        var expectedKinds = digitalOutput ? ["book-pdf"] : ExpectedPhysicalArtifactKinds(edition!, printArtifactProfiles);
        if (!resultKinds.SequenceEqual(expectedKinds, StringComparer.Ordinal))
            throw new InvalidOperationException(digitalOutput
                ? "The press renderer must return exactly one Digital PDF book artifact."
                : "The press renderer returned an artifact set that does not match the selected print artifact settings.");
        var interiorResult = resultArtifacts.Single(artifact => artifact.Kind is "interior-pdf" or "book-pdf");
        if (interiorResult.PageCount is not > 0 or > 100_000)
            throw new InvalidOperationException("The renderer returned an invalid interior page count.");
        foreach (var coverResult in resultArtifacts.Where(artifact => artifact.Kind.EndsWith("cover-pdf", StringComparison.Ordinal)))
        {
            var expectedPages = coverResult.Kind == "perfect-bound-cover-pdf"
                && edition!.PrintCoverMode == PrintCoverMode.Duplex ? 2 : 1;
            if (coverResult.PageCount != expectedPages)
                throw new InvalidOperationException($"The renderer returned {coverResult.PageCount} pages for {coverResult.Kind}; {expectedPages} are required.");
        }
        job.ProgressPercent = 92;
        job.ProgressMessage = "Verifying immutable artifacts";
        await db.SaveChangesAsync(cancellationToken);
        var outputRoot = JobRoot(job.Id);
        foreach (var resultArtifact in resultArtifacts)
        {
            var setupManifest = resultArtifact.Kind == "print-setup-manifest";
            var expectedMediaType = setupManifest ? "application/json" : "application/pdf";
            if (!string.Equals(resultArtifact.MediaType, expectedMediaType, StringComparison.Ordinal)
                || resultArtifact.ByteLength is < 2 or > 256L * 1024 * 1024)
                throw new InvalidOperationException("The renderer returned an invalid artifact envelope.");
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
            if (setupManifest)
            {
                try
                {
                    using var _ = JsonDocument.Parse(data);
                }
                catch (JsonException exception)
                {
                    throw new InvalidOperationException($"Artifact {resultArtifact.RelativePath} is not a valid setup manifest.", exception);
                }
            }
            else if (!data.AsSpan().StartsWith("%PDF-"u8))
            {
                throw new InvalidOperationException($"Artifact {resultArtifact.RelativePath} is not a PDF.");
            }
            var sha = Convert.ToHexStringLower(SHA256.HashData(data));
            if (!string.Equals(sha, resultArtifact.Sha256, StringComparison.OrdinalIgnoreCase)
                || data.LongLength != resultArtifact.ByteLength)
                throw new InvalidOperationException($"Artifact integrity failed for {resultArtifact.RelativePath}.");
            db.PublicationArtifacts.Add(new PublicationArtifact
            {
                ProjectId = job.ProjectId,
                TargetKind = job.TargetKind,
                EditionId = job.EditionId,
                RenderJobId = job.Id,
                Kind = coreTarget && resultArtifact.Kind == "book-pdf"
                    ? PublicationArtifactKind.ReadingPdf
                    : ParseKind(resultArtifact.Kind),
                FileName = coreTarget && resultArtifact.Kind == "book-pdf"
                    ? "core-reading-copy.pdf"
                    : Path.GetFileName(fullPath),
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

        var returnedPageMap = new List<(string OwnerId, string BlockId, int PageNumber)>();
        foreach (var entry in result.PageMap ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry.ChapterId)
                || string.IsNullOrWhiteSpace(entry.BlockId)
                || entry.PageNumber < 1
                || entry.PageNumber > interiorResult.PageCount)
                throw new InvalidOperationException("The renderer returned an invalid page-map entry.");
            returnedPageMap.Add((entry.ChapterId, entry.BlockId, entry.PageNumber));
        }
        var hasDuplicatePageMap = returnedPageMap.Select(entry => (entry.OwnerId, entry.BlockId)).Distinct().Count() != returnedPageMap.Count;
        var returnedPageMapKeys = returnedPageMap.Select(entry => (entry.OwnerId, entry.BlockId)).ToHashSet();
        var isIncompletePageMap = !returnedPageMapKeys.SetEquals(expectedPageMap);
        if (hasDuplicatePageMap || isIncompletePageMap)
        {
            if (coreTarget)
            {
                var mergedDiagnostics = new List<PublicationRenderDiagnostic>(result.Diagnostics ?? Array.Empty<PublicationRenderDiagnostic>());
                var duplicateGroups = returnedPageMap
                    .GroupBy(entry => (entry.OwnerId, entry.BlockId))
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key)
                    .ToList();
                foreach (var dup in duplicateGroups)
                {
                    var sourceKind = expectedChapterPageMap.Contains(dup) ? "chapter" : "publication-section";
                    mergedDiagnostics.Add(new PublicationRenderDiagnostic(
                        "warning",
                        "PRESS_PAGE_MAP_DUPLICATE",
                        $"A block {dup.BlockId} in {sourceKind} {dup.OwnerId} appears more than once in the page map and was de-duplicated for the reading copy.",
                        null,
                        null,
                        sourceKind,
                        dup.OwnerId));
                }

                var missing = expectedPageMap.Except(returnedPageMapKeys).ToList();
                foreach (var group in missing.GroupBy(entry => entry.OwnerId))
                {
                    var ownerId = group.Key;
                    var sourceKind = group.Any(entry => expectedChapterPageMap.Contains(entry)) ? "chapter" : "publication-section";
                    var count = group.Count();
                    var message = count == 1
                        ? $"A block {group.First().BlockId} in {sourceKind} {ownerId} was not placed on a page in the reading copy."
                        : $"{count} blocks in {sourceKind} {ownerId} were not placed on a page in the reading copy.";
                    mergedDiagnostics.Add(new PublicationRenderDiagnostic(
                        "warning",
                        "PRESS_PAGE_MAP_INCOMPLETE",
                        message,
                        null,
                        null,
                        sourceKind,
                        ownerId));
                }

                var extra = returnedPageMapKeys.Except(expectedPageMap).ToList();
                foreach (var group in extra.GroupBy(entry => entry.OwnerId))
                {
                    var ownerId = group.Key;
                    mergedDiagnostics.Add(new PublicationRenderDiagnostic(
                        "warning",
                        "PRESS_PAGE_MAP_UNEXPECTED",
                        $"The reading copy page map contains an unexpected entry for {ownerId}.",
                        null,
                        null,
                        "publication-section",
                        ownerId));
                }

                if (mergedDiagnostics.Count == (result.Diagnostics?.Length ?? 0))
                    mergedDiagnostics.Add(new PublicationRenderDiagnostic(
                        "warning",
                        "PRESS_PAGE_MAP_INCOMPLETE",
                        "The reading copy page map was incomplete and some content may not be navigable.",
                        null,
                        null,
                        null,
                        null));

                job.DiagnosticsJson = JsonSerializer.Serialize(mergedDiagnostics, JsonOptions);
                result = result with { Diagnostics = mergedDiagnostics.ToArray() };
                returnedPageMap = returnedPageMap.DistinctBy(entry => (entry.OwnerId, entry.BlockId)).ToList();
            }
            else
            {
                throw new InvalidOperationException("The renderer returned an incomplete or duplicate semantic page map.");
            }
        }
        // Publication-section block IDs are stable manuscript identifiers, but they are not
        // necessarily GUIDs. Validate them above as part of the complete Press map; persist only
        // chapter entries in the chapter-navigation table whose contract remains Guid-based.
        var parsedPageMap = returnedPageMap
            .Where(entry => expectedChapterPageMap.Contains((entry.OwnerId, entry.BlockId)))
            .Select(entry =>
            {
                if (!Guid.TryParse(entry.OwnerId, out var chapterId)
                    || !Guid.TryParse(entry.BlockId, out var blockId))
                    throw new InvalidOperationException("The renderer returned an invalid chapter page-map entry.");
                return (ChapterId: chapterId, BlockId: blockId, entry.PageNumber);
            })
            .ToList();
        var fingerprintAfterRender = coreTarget
            ? await books.GetSourceFingerprintAsync(job.ProjectId, cancellationToken)
            : await editions.GetSourceFingerprintAsync(job.ProjectId, edition!.Id, cancellationToken);
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
        job.ProgressMessage = coreTarget
            && result.Diagnostics?.Any(diagnostic => diagnostic.Severity == "warning") == true
                ? "Reading PDF ready with warnings"
                : "Lorekeeper validated";
        job.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        Cleanup(job.Id);
    }

    private static PublicationCoverDesignView CoreCoverView(PublishDocument document)
    {
        var cover = document.Cover ?? throw new InvalidOperationException("Core Book requires a front cover before a reading PDF can be prepared.");
        return new PublicationCoverDesignView(
            Guid.Empty,
            Guid.Empty,
            cover.Title,
            cover.Subtitle,
            cover.Author,
            string.Empty,
            document.Profile.Description,
            cover.BackgroundColor,
            PublicationBarcodeMode.None,
            50,
            50,
            JsonSerializer.Serialize(cover.Scene, ManuscriptCodec.JsonOptions),
            0,
            new PublicationCoverTemplate(0, document.Profile.PageWidthInches, document.Profile.PageHeightInches, 0, 0,
                document.Profile.PageWidthInches, document.Profile.PageHeightInches, 0.25, 0, 0, string.Empty, true),
            []);
    }

    private static IReadOnlyList<PublicationCoverDiagnostic> StructuredCoverDiagnostics(
        PublicationCoverDesignView coverDesign) =>
        coverDesign.DiagnosticDetails.Count > 0
            ? coverDesign.DiagnosticDetails
            : coverDesign.Diagnostics
                .Where(message => message.Contains("requires", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("must contain", StringComparison.OrdinalIgnoreCase))
                .Select(message => new PublicationCoverDiagnostic("error", "COVER_DESIGN_LEGACY", message))
                .ToList();

    private static bool IsSupportedProfile(string profile) => profile is
        "generic-print-v2" or
        "generic-digital-pdf-v1" or
        "kdp-paperback-v2" or
        "kdp-hardcover-v1" or
        "ingram-print-pdfx1a-v2" or
        "bn-print-pdfa1b-v1" or
        "lulu-print-v1";

    private static string[] RequiredCoverSurfaces(PrintArtifactProfile product, PrintCoverMode coverMode)
    {
        if (product.RequiresPerfectBoundCover)
            return coverMode == PrintCoverMode.Duplex ? ["perfect-bound-outside", "perfect-bound-inside"] : ["perfect-bound-outside"];
        var surfaces = new List<string>();
        if (product.RequiresCaseCover) surfaces.Add("case-wrap");
        if (product.RequiresDustJacket) surfaces.Add("dust-jacket");
        if (product.RequiresClothManifest) surfaces.Add("digital-cloth-setup");
        return [.. surfaces];
    }

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
        var failureMessage = diagnostics?.FirstOrDefault(diagnostic => diagnostic.Severity == "error")?.Message;
        job.ProgressMessage = cancelled
            ? "Cancelled"
            : string.IsNullOrWhiteSpace(failureMessage)
                ? "Render failed"
                : PublicationDiagnosticText.SanitizeUserFacing(failureMessage);
        job.CompletedAt = DateTime.UtcNow;
        return true;
    }

    private async Task<PressPreparedRequest> BuildRequestAsync(
        PublicationRenderJob job,
        PublishDocument document,
        PublicationCoverDesignView? coverDesign,
        CancellationToken cancellationToken,
        string? layoutTraceMode = null)
    {
        var release = job.Edition;
        var digitalOutput = job.TargetKind == PublicationTargetKind.CoreBook
            || release?.Format == PublicationEditionFormat.DigitalPdf;
        var assets = document.Assets
            .GroupBy(asset => asset.Id)
            .Select(group => StageAsset(group.First(), ConfiguredPrintAssetMaxBytes()))
            .ToArray();
        var coverScene = coverDesign is null ? null : JsonSerializer.Deserialize<CompositionScene>(
            coverDesign.CompositionSceneJson,
            ManuscriptCodec.JsonOptions);
        var coverTextBindings = coverDesign is null
            ? null
            : PublicationTextBindings.Bindings(
                coverDesign.Title,
                coverDesign.Subtitle,
                coverDesign.Author,
                document.Profile.Publisher,
                document.Profile.Copyright,
                document.Profile.Description,
                document.Profile.Isbn,
                coverDesign.SpineText);
        if (coverScene is not null)
            coverScene = NormalizeSceneLanguages(PublicationTextBindings.ResolveScene(
                CoverCompositionFactory.KeepArtworkBehindCopy(coverScene),
                coverTextBindings!));
        var coverSurfaceScenes = (coverDesign?.SurfaceScenes ?? new Dictionary<string, string>()).ToDictionary(
            item => item.Key,
            item => NormalizeSceneLanguages(PublicationTextBindings.ResolveScene(
                CoverCompositionFactory.KeepArtworkBehindCopy(
                    JsonSerializer.Deserialize<CompositionScene>(item.Value, ManuscriptCodec.JsonOptions)
                        ?? throw new InvalidDataException($"Cover surface '{item.Key}' is empty.")),
                coverTextBindings!)),
            StringComparer.Ordinal);
        var usedFontKeys = document.NamedStyles
            .Select(style => style.Definition.FontFamilyKey)
            .Concat(document.Sections.SelectMany(section => section.Chapters)
                .SelectMany(chapter => chapter.Manuscript.Content)
                .Select(block => block.ParagraphPresentation?.FontFamilyKey))
            .Concat(document.Sections.SelectMany(section => section.Chapters)
                .SelectMany(chapter => chapter.PageCompositions)
                .SelectMany(composition => composition.Variants)
                .SelectMany(variant => variant.Scene.Objects.Select(item => item.FontFamilyKey)
                    .Concat(variant.Scene.Styles.Select(style => style.FontFamilyKey))))
            .Concat(document.PublicationSections.SelectMany(section => section.Manuscript.Content)
                .Select(block => block.ParagraphPresentation?.FontFamilyKey))
            .Concat(document.PublicationSections.SelectMany(section => section.PageCompositions)
                .SelectMany(composition => composition.Variants)
                .SelectMany(variant => variant.Scene.Objects.Select(item => item.FontFamilyKey)
                    .Concat(variant.Scene.Styles.Select(style => style.FontFamilyKey))))
            .Concat(coverScene?.Objects.Select(item => item.FontFamilyKey) ?? [])
            .Concat(coverScene?.Styles.Select(style => style.FontFamilyKey) ?? [])
            .Concat(coverSurfaceScenes.Values.SelectMany(scene => scene.Objects.Select(item => item.FontFamilyKey)
                .Concat(scene.Styles.Select(style => style.FontFamilyKey))))
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
                        scene = NormalizeSceneLanguages(CompositionService.WithDerivedTextSemanticRoles(
                            variant.Scene,
                            composition.SemanticManuscript)),
                    }).ToArray(),
                }).ToArray(),
            }).ToArray(),
        }).ToArray();
        var publicationSectionPayloads = document.PublicationSections
            .OrderBy(item => item.Anchor)
            .Select(item => new
            {
                id = item.Id,
                coreSectionId = item.CoreSectionId,
                item.Title,
                kind = item.Kind.ToString(),
                systemRole = item.SystemRole.ToString(),
                anchor = item.Anchor.ToString(),
                targetKind = item.TargetKind?.ToString(),
                targetId = item.TargetId,
                item.LocalOrder,
                startSide = item.StartSide.ToString(),
                blocks = item.Manuscript.Content.Select(BlockPayload).ToArray(),
                pageCompositions = item.PageCompositions.Select(composition => new
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
                        scene = NormalizeSceneLanguages(CompositionService.WithDerivedTextSemanticRoles(
                            variant.Scene,
                            composition.SemanticManuscript)),
                    }).ToArray(),
                }).ToArray(),
            }).ToArray();
        if (sections.Sum(section => section.chapters.Length) == 0 && publicationSectionPayloads.Length == 0)
            throw new InvalidOperationException("Include at least one chapter or publication section before rendering.");
        var missingVariants = sections.SelectMany(section => section.chapters)
            .SelectMany(chapter => chapter.pageCompositions)
            .Where(composition => composition.variants.Length != 1)
            .Select(composition => composition.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        missingVariants.AddRange(publicationSectionPayloads
            .SelectMany(section => section.pageCompositions)
            .Where(composition => composition.variants.Length != 1)
            .Select(composition => composition.Name));
        if (missingVariants.Count > 0)
            throw new InvalidOperationException($"Create and review an exact layout variant for this edition geometry: {string.Join(", ", missingVariants)}.");
        var printProduct = release?.Format is PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover
            ? printArtifactProfiles.GetRequired(release.PrintArtifactProfileKey)
            : null;
        var requiredCoverSurfaces = printProduct is null ? Array.Empty<string>() : RequiredCoverSurfaces(printProduct, release!.PrintCoverMode);
        var payload = new
        {
            protocolVersion = 11,
            jobId = job.Id.ToString("N"),
            profile = job.ProfileId,
            layoutTraceMode,
            outputPurpose = job.TargetKind == PublicationTargetKind.CoreBook
                ? "reading-copy"
                : "publication",
            ink = printProduct?.InteriorProcess.ToString() ?? PrintInteriorProcess.PremiumColor.ToString(),
            printArtifactProfile = printProduct is null ? null : new
            {
                registryVersion = printArtifactProfiles.Version,
                registrySha256 = printArtifactProfiles.Sha256,
                artifactProfileKey = printProduct.Key,
                vendor = printProduct.Vendor.ToString(),
                format = printProduct.Format.ToString(),
                binding = printProduct.Binding.ToString(),
                interiorProcess = printProduct.InteriorProcess.ToString(),
                printProduct.BasisWeightPounds,
                printProduct.Gsm,
                coverMaterial = printProduct.CoverMaterial.ToString(),
                coverMode = release!.PrintCoverMode.ToString(),
                projectUse = release.PrintProjectUse.ToString(),
                identifierMode = release.PrintIdentifierMode.ToString(),
                coverSubmissionMode = release.PrintCoverSubmissionMode.ToString(),
                printProduct.MinimumPages,
                printProduct.MaximumPages,
                printProduct.MinimumSubmittedPages,
                printProduct.MaximumSubmittedPages,
                spineModel = ToPressSpineModel(printProduct.SpineModel),
                requiredCoverSurfaces,
            },
            document = new
            {
                title = string.IsNullOrWhiteSpace(document.DisplayTitle) ? document.ProjectName : document.DisplayTitle,
                subtitle = document.Profile.Subtitle,
                author = string.IsNullOrWhiteSpace(document.Profile.Author) ? "Unknown author" : document.Profile.Author,
                language = PublicationLanguage.Normalize(document.Profile.Language),
                publisher = document.Profile.Publisher,
                copyright = document.Profile.Copyright,
                publicationSections = publicationSectionPayloads,
                printVendor = printProduct?.Vendor.ToString(),
                includeActHeadings = document.Profile.IncludeActHeadings,
                includeChapterHeadings = document.Profile.IncludeChapterHeadings,
                // PublishDocument titles are already numbered consistently for every export format.
                // Press receives display-ready titles and must not add a second prefix.
                numberActs = false,
                numberChapters = false,
                sections,
                styles = document.NamedStyles.Select(style => new
                {
                    style.Name,
                    kind = style.Kind.ToString(),
                    style.SemanticRole,
                    definition = style.Definition,
                }).ToArray(),
                outputMode = digitalOutput ? "DigitalPdf" : "Print",
                allowDesignedPageOverrides = release?.AllowDesignedPageOverrides
                    ?? document.Profile.AllowDesignedPageOverrides,
            },
            trim = new
            {
                widthInches = document.Profile.PageWidthInches,
                heightInches = document.Profile.PageHeightInches,
                marginInches = document.Profile.PageMarginInches,
                bodyFontSizePoints = document.Profile.BodyFontSizePoints,
                bodyLineHeight = document.Profile.BodyLineHeight,
                bleedInches = release?.Bleed == true
                    && release.Format is (PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover) ? 0.125 : 0,
                mirrorMargins = true,
                rectoChapterStarts = document.Profile.RectoChapterStarts,
                minimumWidowLines = 2,
                minimumOrphanLines = 2,
            },
            cover = coverDesign is null ? null : (object)new
            {
                bleedInches = release?.Bleed == true ? 0.125 : 0,
                surfaces = requiredCoverSurfaces,
                description = coverDesign.Description,
                title = coverDesign.Title,
                subtitle = coverDesign.Subtitle,
                author = coverDesign.Author,
                spineText = coverDesign.SpineText,
                spineReadingDirection = coverDesign.SpineReadingDirection.ToString(),
                backgroundColor = coverDesign.BackgroundColor,
                isbn = release?.Isbn ?? string.Empty,
                barcodeMode = coverDesign.BarcodeMode.ToString(),
                assetId = document.CoverAsset?.Id,
                imageCropXPercent = coverDesign.ImageCropXPercent,
                imageCropYPercent = coverDesign.ImageCropYPercent,
                scene = coverScene,
                scenes = coverSurfaceScenes,
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

    private static object ToPressSpineModel(PrintSpineModel model) => new
    {
        model.Kind,
        model.InchesPerPage,
        anchors = model.Anchors ?? [],
        model.BaseInches,
        model.RoundToIncrementInches,
    };

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
        => StageAsset(asset, 256L * 1024 * 1024);

    private static PressStagedAsset StageAsset(PublishAssetDocument asset, long maximumBytes)
    {
        if (asset.Data.LongLength == 0 || asset.Data.LongLength > maximumBytes)
            throw new InvalidOperationException($"Publication image '{asset.FileName}' must be non-empty and no larger than {maximumBytes / (1024d * 1024d):0.##} MiB.");
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
        if (width <= 0 || height <= 0
            || width > LayoutImageSizeResolver.PrintMaximumEdge
            || height > LayoutImageSizeResolver.PrintMaximumEdge
            || (long)width * height > LayoutImageSizeResolver.PrintMaximumPixels)
        {
            throw new InvalidOperationException(
                $"Publication image '{asset.FileName}' dimensions exceed the renderer limit of "
                + $"{LayoutImageSizeResolver.PrintMaximumEdge} pixels per edge and {LayoutImageSizeResolver.PrintMaximumPixels:N0} pixels.");
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

    private long ConfiguredPrintAssetMaxBytes()
    {
        const long pressMaximumBytes = 256L * 1024 * 1024;
        var configured = imageOptions?.Value.MaxPrintUpscaleBytes ?? (int)pressMaximumBytes;
        // Keep managed staging and publication preparation on one authority:
        // the configured image limit, constrained by Press's 256 MiB ceiling.
        return configured > 0 ? Math.Min(configured, pressMaximumBytes) : pressMaximumBytes;
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
        Func<PressProgress, Task> progressChanged,
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
            var exitTask = process.WaitForExitAsync(linked.Token);
            var lastProgress = -1;
            while (!exitTask.IsCompleted)
            {
                await Task.WhenAny(exitTask, Task.Delay(250, linked.Token));
                var progress = await ReadProgressAsync(jobRoot, jobId, linked.Token);
                if (progress is null || progress.Percent <= lastProgress)
                    continue;
                lastProgress = progress.Percent;
                await progressChanged(progress);
            }
            await exitTask;
            var finalProgress = await ReadProgressAsync(jobRoot, jobId, linked.Token);
            if (finalProgress is not null && finalProgress.Percent > lastProgress)
                await progressChanged(finalProgress);
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

    private async Task<PressPaginationResponse> InvokePaginationAsync(
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
        start.ArgumentList[0] = "layout";
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("The configured press renderer could not be started.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(
            Math.Clamp(options.Value.RenderTimeoutSeconds, 10, 1800)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var stdoutTask = ReadBoundedAsync(process.StandardOutput, 1024 * 1024, linked.Token);
        var stderrTask = ReadBoundedAsync(process.StandardError, 64 * 1024, linked.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
            {
                var failure = JsonSerializer.Deserialize<PressResponse>(stdout, JsonOptions);
                var detail = failure?.Diagnostics?.FirstOrDefault(item => item.Severity == "error")?.Message;
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                    ? $"Press interior pagination failed. {Limit(stderr)} {Limit(stdout)}".Trim()
                    : detail);
            }
            return JsonSerializer.Deserialize<PressPaginationResponse>(stdout, JsonOptions)
                ?? throw new InvalidOperationException("Press returned an empty interior-pagination response.");
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }
    }

    private static async Task<PressProgress?> ReadProgressAsync(
        string jobRoot,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(jobRoot, "progress.json");
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is < 2 or > 4_096)
                return null;
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                4_096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var progress = await JsonSerializer.DeserializeAsync<PressProgress>(stream, JsonOptions, cancellationToken);
            return progress is not null
                && progress.Percent is >= 0 and <= 100
                && !string.IsNullOrWhiteSpace(progress.Message)
                && string.Equals(progress.JobId, jobId.ToString("N"), StringComparison.OrdinalIgnoreCase)
                    ? progress
                    : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
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
        language = PublicationLanguage.NormalizeOptional(block.Language),
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
                value = mark.Type == ManuscriptMarkType.Language
                    ? PublicationLanguage.NormalizeOptional(mark.Value)
                    : mark.Value,
            }).ToArray(),
        }).ToArray(),
    };

    private static bool IsPressPageMappedBlock(ManuscriptBlock block) =>
        block.Type is ManuscriptBlockType.SceneBreak or ManuscriptBlockType.Figure or ManuscriptBlockType.DesignedPage
        || !string.IsNullOrWhiteSpace(ManuscriptCodec.Text(block));

    private static CompositionScene NormalizeSceneLanguages(CompositionScene scene) => scene with
    {
        Objects = scene.Objects.Select(item => item with
        {
            Language = PublicationLanguage.NormalizeOptional(item.Language) ?? string.Empty,
        }).ToArray(),
    };

    private static string[] ExpectedPhysicalArtifactKinds(
        PublicationEdition edition,
        IPrintArtifactProfileRegistry printArtifactProfiles)
    {
        var product = printArtifactProfiles.GetRequired(edition.PrintArtifactProfileKey);
        var kinds = new List<string> { "interior-pdf" };
        if (edition.Vendor == PublicationVendor.BarnesAndNoblePress
            && edition.PrintCoverSubmissionMode == PrintCoverSubmissionMode.SeparatePanelsVendorSpine)
        {
            kinds.Add("front-cover-pdf");
            kinds.Add("back-cover-pdf");
        }
        else
        {
            if (product.RequiresPerfectBoundCover) kinds.Add("perfect-bound-cover-pdf");
            if (product.RequiresCaseCover) kinds.Add("case-cover-pdf");
            if (product.RequiresDustJacket) kinds.Add("dust-jacket-pdf");
        }
        if (product.RequiresClothManifest) kinds.Add("print-setup-manifest");
        if (edition.Vendor == PublicationVendor.BarnesAndNoblePress) kinds.Add("print-setup-manifest");
        return [.. kinds.OrderBy(kind => kind, StringComparer.Ordinal)];
    }

    private static PublicationArtifactKind ParseKind(string kind) => kind switch
    {
        "interior-pdf" => PublicationArtifactKind.InteriorPdf,
        "cover-pdf" or "perfect-bound-cover-pdf" => PublicationArtifactKind.PerfectBoundCoverPdf,
        "case-cover-pdf" => PublicationArtifactKind.CaseCoverPdf,
        "dust-jacket-pdf" => PublicationArtifactKind.DustJacketPdf,
        "front-cover-pdf" => PublicationArtifactKind.FrontCoverPdf,
        "back-cover-pdf" => PublicationArtifactKind.BackCoverPdf,
        "print-setup-manifest" => PublicationArtifactKind.PrintSetupManifest,
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

    private sealed record PressPaginationResponse(
        int ProtocolVersion,
        string RendererVersion,
        string JobId,
        int PageCount,
        PublicationRenderDiagnostic[]? Diagnostics);

    private sealed record PressProgress(string JobId, int Percent, string Message);

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
