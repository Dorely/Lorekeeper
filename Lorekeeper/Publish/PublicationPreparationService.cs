using System.Text.Json;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Lorekeeper.Authoring;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Startup;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Publish;

public sealed record PublicationPreparationJobView(
    Guid Id,
    PublicationTargetKind TargetKind,
    Guid? EditionId,
    PublicationPreparationStatus Status,
    string Step,
    int ProgressPercent,
    string Message,
    IReadOnlyList<PublicationPreflightItem> Diagnostics,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    PublicationImagePreparationSummary? ImagePreparationSummary = null);

public interface IPublicationPreparationService
{
    Task<PublicationPreparationJobView> PrepareCoreAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<PublicationPreparationJobView> PrepareReleaseAsync(Guid projectId, Guid editionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PublicationPreparationJobView>> ListAsync(Guid projectId, PublicationTargetKind targetKind, Guid? editionId = null, CancellationToken cancellationToken = default);
    Task<PublicationPreparationJobView> CancelAsync(Guid projectId, Guid jobId, CancellationToken cancellationToken = default);
}

public interface IPublicationPreparationQueue
{
    ValueTask EnqueueAsync(Guid jobId, CancellationToken cancellationToken);
    IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Bridges a user's preparation cancellation request to the background worker
/// without making the persisted job row the worker's only cancellation signal.
/// The row remains authoritative across process restarts.
/// </summary>
public sealed class PublicationPreparationCancellationRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _tokens = new();

    public CancellationToken Register(Guid jobId, CancellationToken stoppingToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        if (!_tokens.TryAdd(jobId, source))
        {
            source.Dispose();
            throw new InvalidOperationException($"Publication preparation job {jobId:N} is already running.");
        }
        return source.Token;
    }

    public void Cancel(Guid jobId)
    {
        if (_tokens.TryGetValue(jobId, out var source))
        {
            try { source.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    public void Unregister(Guid jobId)
    {
        if (_tokens.TryRemove(jobId, out var source))
            source.Dispose();
    }
}

public sealed class PublicationPreparationQueue : IPublicationPreparationQueue
{
    private readonly Channel<Guid> _jobs = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });
    public ValueTask EnqueueAsync(Guid jobId, CancellationToken cancellationToken) => _jobs.Writer.WriteAsync(jobId, cancellationToken);
    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) => _jobs.Reader.ReadAllAsync(cancellationToken);
}

public sealed class PublicationPreparationService(
    IAppDatabaseOperationFactory database,
    IPublicationPreparationQueue queue,
    IPublicationBookService books,
    IPublicationEditionService editions,
    IPublicationRenderService renders,
    PublicationPreparationCancellationRegistry cancellationRegistry,
    ILogger<PublicationPreparationService> logger,
    IAuthoringMutationFence authoringFence) : IPublicationPreparationService
{
    internal static JsonSerializerOptions DiagnosticsJsonOptions { get; } = new(JsonSerializerDefaults.Web);

    public Task<PublicationPreparationJobView> PrepareCoreAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        ExecuteFenceAsync(projectId, "capture the Core Book before preparation", token =>
            CreateAsync(projectId, PublicationTargetKind.CoreBook, null, token), cancellationToken);

    public Task<PublicationPreparationJobView> PrepareReleaseAsync(Guid projectId, Guid editionId, CancellationToken cancellationToken = default) =>
        ExecuteFenceAsync(projectId, "capture the publication release before preparation", token =>
            CreateAsync(projectId, PublicationTargetKind.Release, editionId, token), cancellationToken);

    private async Task<PublicationPreparationJobView> CreateAsync(
        Guid projectId,
        PublicationTargetKind targetKind,
        Guid? editionId,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        _ = await books.GetOrCreateAsync(projectId, cancellationToken);
        var fingerprint = targetKind == PublicationTargetKind.CoreBook
            ? await books.GetSourceFingerprintAsync(projectId, cancellationToken)
            : await editions.GetSourceFingerprintAsync(projectId, editionId!.Value, cancellationToken);
        if (targetKind == PublicationTargetKind.Release
            && !await db.PublicationEditions.AnyAsync(item => item.ProjectId == projectId && item.Id == editionId, cancellationToken))
            throw new KeyNotFoundException("Publication release not found.");
        if (await db.PublicationPreparationJobs.AnyAsync(item => item.ProjectId == projectId
            && item.TargetKind == targetKind && item.EditionId == editionId
            && (item.Status == PublicationPreparationStatus.Queued || item.Status == PublicationPreparationStatus.Preparing), cancellationToken))
            throw new InvalidOperationException("This target already has an active file-preparation job.");
        var terminalJobs = await db.PublicationPreparationJobs
            .Where(item => item.ProjectId == projectId
                && item.TargetKind == targetKind && item.EditionId == editionId
                && item.Status != PublicationPreparationStatus.Queued
                && item.Status != PublicationPreparationStatus.Preparing)
            .ToListAsync(cancellationToken);
        db.PublicationPreparationJobs.RemoveRange(terminalJobs);
        var job = new PublicationPreparationJob
        {
            ProjectId = projectId,
            TargetKind = targetKind,
            EditionId = editionId,
            SourceFingerprint = fingerprint,
            Status = PublicationPreparationStatus.Queued,
            Message = targetKind == PublicationTargetKind.CoreBook ? "Reading PDF queued" : "Publication files queued",
        };
        db.PublicationPreparationJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        await queue.EnqueueAsync(job.Id, CancellationToken.None);
        logger.LogDebug(
            "Preparation job {JobId} requested for project {ProjectId}, edition {EditionId}: targetKind={TargetKind}, fingerprint={Fingerprint}",
            job.Id, projectId, editionId, targetKind, fingerprint);
        return View(job);
    }

    public async Task<IReadOnlyList<PublicationPreparationJobView>> ListAsync(
        Guid projectId,
        PublicationTargetKind targetKind,
        Guid? editionId = null,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        return (await db.PublicationPreparationJobs.AsNoTracking()
                    .Include(item => item.BookRenderJob)
                    .Include(item => item.InteriorRenderJob)
                    .Include(item => item.CoverRenderJob)
                    .Where(item => item.ProjectId == projectId
                    && item.TargetKind == targetKind && item.EditionId == editionId)
                    .OrderByDescending(item => item.CreatedAt).ToListAsync(cancellationToken)).Select(View).ToList();
    }
    public async Task<PublicationPreparationJobView> CancelAsync(Guid projectId, Guid jobId, CancellationToken cancellationToken = default)
    {
        // Signal an active worker before waiting for the project mutation
        // lease. The lease may be held by image preparation itself; delaying
        // this signal until after OpenWriteAsync would make cancellation
        // unable to reach the pre-commit rollback point.
        cancellationRegistry.Cancel(jobId);
        await using var databaseOperation = await database.OpenWriteAsync(CancellationToken.None);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var job = await db.PublicationPreparationJobs.SingleAsync(
            item => item.ProjectId == projectId && item.Id == jobId,
            CancellationToken.None);
        if (job.Status is PublicationPreparationStatus.Queued or PublicationPreparationStatus.Preparing)
        {
            job.CancellationRequested = true;
            job.Status = PublicationPreparationStatus.Cancelled;
            job.Message = "Cancelled";
            job.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            cancellationRegistry.Cancel(jobId);
            var renderJobIds = new[] { job.BookRenderJobId, job.InteriorRenderJobId, job.CoverRenderJobId }
                .OfType<Guid>()
                .Distinct()
                .ToList();
            foreach (var renderJobId in renderJobIds)
            {
                if (job.TargetKind == PublicationTargetKind.CoreBook)
                    await renders.CancelCoreAsync(projectId, renderJobId, CancellationToken.None);
                else if (job.EditionId is Guid editionId)
                    await renders.CancelAsync(projectId, editionId, renderJobId, CancellationToken.None);
            }
        }
        return View(job);
    }

    internal static PublicationPreparationJobView View(PublicationPreparationJob job)
    {
        var diagnostics = DeserializeDiagnostics(job.DiagnosticsJson);
        var imageSummary = DeserializeImagePreparationSummary(job.ImagePreparationSummaryJson);
        var lastRenderJob = job.CoverRenderJob ?? job.InteriorRenderJob ?? job.BookRenderJob;
        if (lastRenderJob is not null
            && (diagnostics.Count == 0
                || diagnostics.Any(item => !IsUsable(item)
                    || item.Code is "PREPARATION_FAILED" or "PREPARATION_DIAGNOSTICS_INVALID")))
        {
            var renderDiagnostics = DeserializeRenderDiagnostics(lastRenderJob.DiagnosticsJson);
            if (renderDiagnostics.Count > 0)
                diagnostics = renderDiagnostics;
        }
        return new(
            job.Id, job.TargetKind, job.EditionId, job.Status,
            PublicationDiagnosticText.SanitizeUserFacing(job.Step),
            job.ProgressPercent,
            PublicationDiagnosticText.SanitizeUserFacing(job.Message),
            diagnostics, job.CreatedAt, job.CompletedAt, imageSummary);
    }

    internal static IReadOnlyList<PublicationPreflightItem> DeserializeDiagnostics(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<PublicationPreflightItem>>(json, DiagnosticsJsonOptions) ?? [];
        }
        catch (JsonException) { return [new("error", "PREPARATION_DIAGNOSTICS_INVALID", "Stored preparation diagnostics are invalid.")]; }
    }

    private static IReadOnlyList<PublicationPreflightItem> DeserializeRenderDiagnostics(string json)
    {
        try
        {
            return (JsonSerializer.Deserialize<List<PublicationRenderDiagnostic>>(json, DiagnosticsJsonOptions) ?? [])
                .Select(item => new PublicationPreflightItem(
                    item.Severity, item.Code, item.Message, null,
                    item.Page, item.SourceKind, item.SourceId))
                .Where(IsUsable)
                .ToList();
        }
        catch (JsonException)
        {
            return [new("error", "PREPARATION_DIAGNOSTICS_INVALID", "Stored preparation diagnostics are invalid.")];
        }
    }

    private static bool IsUsable(PublicationPreflightItem item) =>
        !string.IsNullOrWhiteSpace(item.Severity)
        && !string.IsNullOrWhiteSpace(item.Code)
        && !string.IsNullOrWhiteSpace(item.Message);

    private static PublicationImagePreparationSummary? DeserializeImagePreparationSummary(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || string.Equals(json.Trim(), "{}", StringComparison.Ordinal))
            return null;
        try
        {
            return JsonSerializer.Deserialize<PublicationImagePreparationSummary>(json, DiagnosticsJsonOptions);
        }
        catch (JsonException)
        {
            return new(
                0,
                0,
                0,
                0,
                [new("IMAGE_PREPARATION_SUMMARY_INVALID", "The stored image-preparation summary is invalid.")]);
        }
    }

    private Task<T> ExecuteFenceAsync<T>(
        Guid projectId,
        string purpose,
        Func<CancellationToken, Task<T>> consume,
        CancellationToken cancellationToken)
    {
        if (projectId == Guid.Empty)
            throw new ArgumentException("A project ID is required.", nameof(projectId));
        return authoringFence.ExecuteAsync(
            new AuthoringFenceRequest(projectId, [], purpose),
            (_, token) => consume(token),
            cancellationToken);
    }
}

public sealed class PublicationPreparationWorker(
    IServiceScopeFactory scopeFactory,
    IAppDatabaseOperationFactory database,
    IPublicationPreparationQueue queue,
    IApplicationStartupState startup,
    ILogger<PublicationPreparationWorker> logger,
    PublicationPreparationCancellationRegistry cancellationRegistry) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (!await startup.WaitForDatabaseReadyAsync(stoppingToken))
                return;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        await RecoverAsync(stoppingToken);
        try
        {
            await foreach (var jobId in queue.ReadAllAsync(stoppingToken))
            {
                var jobCancellation = cancellationRegistry.Register(jobId, stoppingToken);
                try { await ProcessAsync(jobId, jobCancellation); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Publication preparation {JobId} failed.", jobId);
                    await FailAsync(jobId, exception, CancellationToken.None);
                }
                finally
                {
                    cancellationRegistry.Unregister(jobId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task ProcessAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        PublicationPreparationJob job;
        await using (var operation = await database.OpenWriteAsync(cancellationToken))
        {
            var db = operation.Db;
            job = await db.PublicationPreparationJobs.Include(item => item.Edition)
                .SingleAsync(item => item.Id == jobId, cancellationToken);
            if (job.CancellationRequested || job.Status == PublicationPreparationStatus.Cancelled)
                return;
            job.Status = PublicationPreparationStatus.Preparing;
            job.Step = "Checking readiness";
            job.ProgressPercent = 5;
            job.StartedAt = DateTime.UtcNow;
            logger.LogDebug(
                "Preparation job {JobId} started: targetKind={TargetKind}, edition={EditionId}, fingerprint={Fingerprint}",
                jobId, job.TargetKind, job.EditionId, job.SourceFingerprint);

            var blockers = await ReadinessBlockersAsync(
                database,
                db,
                job,
                scope.ServiceProvider.GetRequiredService<IPrintArtifactProfileRegistry>(),
                cancellationToken);
            if (blockers.Count > 0)
            {
                job.Status = PublicationPreparationStatus.Blocked;
                job.Message = blockers[0].Message;
                job.DiagnosticsJson = JsonSerializer.Serialize(blockers, PublicationPreparationService.DiagnosticsJsonOptions);
                job.CompletedAt = DateTime.UtcNow;
                await operation.SaveChangesAsync(cancellationToken);
                logger.LogWarning("Preparation job {JobId} blocked: {Message}", jobId, blockers[0].Message);
                return;
            }

            await operation.SaveChangesAsync(cancellationToken);
        }

        var preparationDiagnostics = new List<PublicationPreflightItem>();
        if (job.TargetKind == PublicationTargetKind.CoreBook)
        {
            var imagePreparation = await PrepareImagesOrBlockAsync(
                job,
                scope.ServiceProvider,
                PublicationRenderScope.Book,
                job.SourceFingerprint,
                "Preparing reading-copy images",
                8,
                cancellationToken);
            if (imagePreparation is null || await IsCancelledAsync(jobId, CancellationToken.None))
                return;
            var renders = scope.ServiceProvider.GetRequiredService<IPublicationRenderService>();
            var current = (await renders.ListCoreAsync(job.ProjectId, cancellationToken)).FirstOrDefault(item =>
                item.Status == PublicationRenderStatus.Completed && item.Artifacts.Any(artifact => artifact.Kind == PublicationArtifactKind.ReadingPdf && !artifact.IsStale));
            var render = current ?? await renders.RequestCoreAsync(job.ProjectId, cancellationToken);
            job.BookRenderJobId = render.Id;
            await UpdateJobAsync(jobId, candidate =>
            {
                candidate.BookRenderJobId = render.Id;
                candidate.Step = "Typesetting and validating reading PDF";
                candidate.ProgressPercent = 10;
            }, cancellationToken);
            preparationDiagnostics.AddRange(await WaitForRenderAsync(job, render.Id, 10, 85, cancellationToken));
        }
        else if (job.Edition!.Format == PublicationEditionFormat.Epub)
        {
            var imagePreparation = await PrepareImagesOrBlockAsync(
                job,
                scope.ServiceProvider,
                PublicationRenderScope.Book,
                job.SourceFingerprint,
                "Preparing EPUB images",
                8,
                cancellationToken);
            if (imagePreparation is null || await IsCancelledAsync(jobId, CancellationToken.None))
                return;
            await UpdateJobAsync(jobId, candidate =>
            {
                candidate.Step = "Exporting and validating EPUB";
                candidate.ProgressPercent = 35;
            }, cancellationToken);
            var packages = scope.ServiceProvider.GetRequiredService<IPublicationPackageService>();
            var report = await PreflightOrBlockAsync(job, packages, cancellationToken);
            if (report is null)
                return;
            await UpdateJobAsync(jobId, candidate =>
            {
                candidate.Step = "Building EPUB package";
                candidate.ProgressPercent = 92;
            }, cancellationToken);
            await packages.BuildFromPreflightAsync(job.ProjectId, job.EditionId!.Value, report, cancellationToken);
        }
        else if (job.Edition.Format is PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover)
        {
            if (!await PreparePhysicalReleaseAsync(job, scope.ServiceProvider, preparationDiagnostics, cancellationToken))
                return;
        }
        else
        {
            var imagePreparation = await PrepareImagesOrBlockAsync(
                job,
                scope.ServiceProvider,
                PublicationRenderScope.Book,
                job.SourceFingerprint,
                "Preparing publication images",
                8,
                cancellationToken);
            if (imagePreparation is null || await IsCancelledAsync(jobId, CancellationToken.None))
                return;
            var renders = scope.ServiceProvider.GetRequiredService<IPublicationRenderService>();
            var current = (await renders.ListAsync(job.ProjectId, job.EditionId!.Value, cancellationToken)).FirstOrDefault(item =>
                item.Status == PublicationRenderStatus.Completed && item.Artifacts.Any() && item.Artifacts.All(artifact => !artifact.IsStale));
            var render = current ?? await renders.RequestAsync(
                job.ProjectId,
                job.EditionId.Value,
                PublicationRenderScope.Book,
                cancellationToken: cancellationToken);
            job.BookRenderJobId = render.Id;
            await UpdateJobAsync(jobId, candidate =>
            {
                candidate.BookRenderJobId = render.Id;
                candidate.Step = "Rendering and validating publication files";
                candidate.ProgressPercent = 10;
            }, cancellationToken);
            preparationDiagnostics.AddRange(await WaitForRenderAsync(job, render.Id, 10, 75, cancellationToken));
            await UpdateJobAsync(jobId, candidate =>
            {
                candidate.Step = "Checking publication files";
                candidate.ProgressPercent = 88;
            }, cancellationToken);
            var packages = scope.ServiceProvider.GetRequiredService<IPublicationPackageService>();
            var report = await PreflightOrBlockAsync(job, packages, cancellationToken);
            if (report is null)
                return;
            await UpdateJobAsync(jobId, candidate =>
            {
                candidate.Step = "Building publication package";
                candidate.ProgressPercent = 92;
            }, cancellationToken);
            await packages.BuildFromPreflightAsync(job.ProjectId, job.EditionId!.Value, report, cancellationToken);
        }

        var completedFingerprint = job.TargetKind == PublicationTargetKind.CoreBook
            ? await scope.ServiceProvider.GetRequiredService<IPublicationBookService>()
                .GetSourceFingerprintAsync(job.ProjectId, cancellationToken)
            : await scope.ServiceProvider.GetRequiredService<IPublicationEditionService>()
                .GetSourceFingerprintAsync(job.ProjectId, job.EditionId!.Value, cancellationToken);
        await UpdateJobAsync(jobId, candidate =>
        {
            if (candidate.CancellationRequested || candidate.Status == PublicationPreparationStatus.Cancelled)
                return;
            candidate.Status = PublicationPreparationStatus.Ready;
            candidate.Step = "Ready";
            candidate.ProgressPercent = 100;
            candidate.Message = candidate.TargetKind == PublicationTargetKind.CoreBook
                ? preparationDiagnostics.Count > 0 ? "Reading PDF ready with warnings" : "Reading PDF ready"
                : "Publication files ready";
            candidate.SourceFingerprint = completedFingerprint;
            candidate.DiagnosticsJson = JsonSerializer.Serialize(preparationDiagnostics, PublicationPreparationService.DiagnosticsJsonOptions);
            candidate.CompletedAt = DateTime.UtcNow;
        }, cancellationToken);
        logger.LogInformation("Preparation job {JobId} ready (diagnostics={DiagnosticCount})", jobId, preparationDiagnostics.Count);
    }

    private async Task<bool> PreparePhysicalReleaseAsync(
        PublicationPreparationJob job,
        IServiceProvider services,
        List<PublicationPreflightItem> preparationDiagnostics,
        CancellationToken cancellationToken)
    {
        var editionId = job.EditionId!.Value;
        var editions = services.GetRequiredService<IPublicationEditionService>();
        var renders = services.GetRequiredService<IPublicationRenderService>();
        var interiorFingerprint = await editions.GetInteriorFingerprintAsync(job.ProjectId, editionId, cancellationToken);
        var renderJobs = await renders.ListAsync(job.ProjectId, editionId, cancellationToken);
        var interiorRender = renderJobs.FirstOrDefault(candidate =>
            candidate.Status == PublicationRenderStatus.Completed
            && candidate.Scope == PublicationRenderScope.Interior
            && candidate.SourceFingerprint == interiorFingerprint
            && candidate.Artifacts.Any(artifact =>
                artifact.Kind == PublicationArtifactKind.InteriorPdf
                && !artifact.IsLegacy
                && !artifact.IsStale));
        var interior = interiorRender?.Artifacts.First(artifact =>
            artifact.Kind == PublicationArtifactKind.InteriorPdf
            && !artifact.IsLegacy
            && !artifact.IsStale);

        if (interior is null)
        {
            var imagePreparation = await PrepareImagesOrBlockAsync(
                job,
                services,
                PublicationRenderScope.Interior,
                interiorFingerprint,
                "Preparing interior images",
                8,
                cancellationToken);
            if (imagePreparation is null || await IsCancelledAsync(job.Id, CancellationToken.None))
                return false;

            var render = await renders.RequestAsync(
                job.ProjectId,
                editionId,
                PublicationRenderScope.Interior,
                cancellationToken: cancellationToken);
            job.InteriorRenderJobId = render.Id;
            await UpdateJobAsync(job.Id, candidate =>
            {
                candidate.InteriorRenderJobId = render.Id;
                candidate.Step = "Rendering and validating interior PDF";
                candidate.ProgressPercent = 12;
            }, cancellationToken);
            preparationDiagnostics.AddRange(await WaitForRenderAsync(job, render.Id, 12, 36, cancellationToken));
            preparationDiagnostics.Add(new(
                "info",
                "PREPARATION_INTERIOR_RENDERED",
                "The interior PDF was regenerated and validated."));
            interiorRender = await renders.GetAsync(job.ProjectId, editionId, render.Id, cancellationToken);
            interior = interiorRender.Artifacts.FirstOrDefault(artifact =>
                artifact.Kind == PublicationArtifactKind.InteriorPdf
                && !artifact.IsLegacy
                && !artifact.IsStale
                && artifact.SourceFingerprint == interiorFingerprint);
            if (interior is null)
                throw new InvalidOperationException("The validated interior PDF was not available after rendering.");
        }
        else
        {
            job.InteriorRenderJobId = interiorRender!.Id;
            preparationDiagnostics.Add(new(
                "info",
                "PREPARATION_INTERIOR_REUSED",
                "The existing validated interior PDF is still current and was reused."));
            await UpdateJobAsync(job.Id, candidate =>
            {
                candidate.InteriorRenderJobId = interiorRender.Id;
                candidate.Step = "Interior PDF is current";
                candidate.ProgressPercent = 48;
            }, cancellationToken);
        }

        var pageCount = interior.PageCount
            ?? throw new InvalidOperationException("The current interior PDF has no page count.");
        var coverFingerprint = await editions.GetCoverFingerprintAsync(
            job.ProjectId,
            editionId,
            pageCount,
            cancellationToken);
        var requiredCoverKinds = RequiredCoverArtifactKinds(
            job.Edition!,
            services.GetRequiredService<IPrintArtifactProfileRegistry>());
        renderJobs = await renders.ListAsync(job.ProjectId, editionId, cancellationToken);
        var coverRender = renderJobs.FirstOrDefault(candidate =>
            candidate.Status == PublicationRenderStatus.Completed
            && candidate.Scope == PublicationRenderScope.Cover
            && candidate.InteriorPageCount == pageCount
            && candidate.SourceFingerprint == coverFingerprint
            && requiredCoverKinds.All(kind => candidate.Artifacts.Any(artifact =>
                artifact.Kind == kind
                && !artifact.IsLegacy
                && !artifact.IsStale
                && artifact.SourceFingerprint == coverFingerprint)));
        if (coverRender is null)
        {
            var imagePreparation = await PrepareImagesOrBlockAsync(
                job,
                services,
                PublicationRenderScope.Cover,
                coverFingerprint,
                "Preparing cover images",
                50,
                cancellationToken);
            if (imagePreparation is null || await IsCancelledAsync(job.Id, CancellationToken.None))
                return false;

            var render = await renders.RequestAsync(
                job.ProjectId,
                editionId,
                PublicationRenderScope.Cover,
                pageCount,
                cancellationToken);
            job.CoverRenderJobId = render.Id;
            await UpdateJobAsync(job.Id, candidate =>
            {
                candidate.CoverRenderJobId = render.Id;
                candidate.Step = "Rendering and validating cover files";
                candidate.ProgressPercent = 52;
            }, cancellationToken);
            preparationDiagnostics.AddRange(await WaitForRenderAsync(job, render.Id, 52, 33, cancellationToken));
            preparationDiagnostics.Add(new(
                "info",
                "PREPARATION_COVER_RENDERED",
                "The cover files were regenerated and validated."));
        }
        else
        {
            job.CoverRenderJobId = coverRender.Id;
            preparationDiagnostics.Add(new(
                "info",
                "PREPARATION_COVER_REUSED",
                "The existing validated cover files are still current and were reused."));
            await UpdateJobAsync(job.Id, candidate =>
            {
                candidate.CoverRenderJobId = coverRender.Id;
                candidate.Step = "Cover files are current";
                candidate.ProgressPercent = 85;
            }, cancellationToken);
        }

        await UpdateJobAsync(job.Id, candidate =>
        {
            candidate.Step = "Checking publication files";
            candidate.ProgressPercent = 88;
        }, cancellationToken);
        var packages = services.GetRequiredService<IPublicationPackageService>();
        var report = await PreflightOrBlockAsync(job, packages, cancellationToken);
        if (report is null)
            return false;
        await UpdateJobAsync(job.Id, candidate =>
        {
            candidate.Step = "Building publication package";
            candidate.ProgressPercent = 92;
        }, cancellationToken);
        await packages.BuildFromPreflightAsync(job.ProjectId, editionId, report, cancellationToken);
        return true;
    }

    private static IReadOnlyList<PublicationArtifactKind> RequiredCoverArtifactKinds(
        PublicationEdition edition,
        IPrintArtifactProfileRegistry profiles)
    {
        var profile = profiles.GetRequired(edition.PrintArtifactProfileKey);
        var kinds = new List<PublicationArtifactKind>();
        if (edition.Vendor == PublicationVendor.BarnesAndNoblePress
            && edition.PrintCoverSubmissionMode == PrintCoverSubmissionMode.SeparatePanelsVendorSpine)
        {
            kinds.Add(PublicationArtifactKind.FrontCoverPdf);
            kinds.Add(PublicationArtifactKind.BackCoverPdf);
        }
        else
        {
            if (profile.RequiresPerfectBoundCover) kinds.Add(PublicationArtifactKind.PerfectBoundCoverPdf);
            if (profile.RequiresCaseCover) kinds.Add(PublicationArtifactKind.CaseCoverPdf);
            if (profile.RequiresDustJacket) kinds.Add(PublicationArtifactKind.DustJacketPdf);
        }
        if (profile.RequiresClothManifest || edition.Vendor == PublicationVendor.BarnesAndNoblePress)
            kinds.Add(PublicationArtifactKind.PrintSetupManifest);
        return kinds;
    }

    private async Task<PublicationImagePreparationResult?> PrepareImagesOrBlockAsync(
        PublicationPreparationJob job,
        IServiceProvider services,
        PublicationRenderScope renderScope,
        string expectedFingerprint,
        string step,
        int progressPercent,
        CancellationToken cancellationToken)
    {
        await UpdateJobAsync(job.Id, candidate =>
        {
            candidate.Step = step;
            candidate.ProgressPercent = progressPercent;
        }, cancellationToken);

        PublicationImagePreparationResult result;
        try
        {
            result = await services.GetRequiredService<IPublicationImagePreparationService>().PrepareAsync(
                job.ProjectId,
                job.TargetKind,
                job.EditionId,
                renderScope,
                expectedFingerprint,
                cancellationToken);
        }
        catch (PublicationImagePreparationException exception)
        {
            await UpdateJobAsync(job.Id, candidate =>
            {
                if (candidate.CancellationRequested || candidate.Status == PublicationPreparationStatus.Cancelled)
                    return;
                candidate.Status = PublicationPreparationStatus.Blocked;
                candidate.Message = exception.Summary.Failures.FirstOrDefault()?.Message ?? exception.Message;
                candidate.DiagnosticsJson = JsonSerializer.Serialize(
                    exception.Summary.Failures.Select(item => new PublicationPreflightItem(
                        "error", item.Code, item.Message, null, item.PageNumber, item.Surface, item.AssetId?.ToString("D"))).ToArray(),
                    PublicationPreparationService.DiagnosticsJsonOptions);
                candidate.ImagePreparationSummaryJson = JsonSerializer.Serialize(
                    exception.Summary, PublicationPreparationService.DiagnosticsJsonOptions);
                candidate.CompletedAt = DateTime.UtcNow;
            }, CancellationToken.None);
            return null;
        }

        await UpdateJobAsync(job.Id, candidate =>
        {
            candidate.ImagePreparationSummaryJson = JsonSerializer.Serialize(
                result.Summary, PublicationPreparationService.DiagnosticsJsonOptions);
            candidate.SourceFingerprint = result.SourceFingerprint;
            if (candidate.CancellationRequested || candidate.Status == PublicationPreparationStatus.Cancelled)
                return;
            if (result.Summary.Failures.Count > 0)
            {
                candidate.Status = PublicationPreparationStatus.Blocked;
                candidate.Message = result.Summary.Failures[0].Message;
                candidate.DiagnosticsJson = JsonSerializer.Serialize(
                    result.Summary.Failures.Select(item => new PublicationPreflightItem(
                        "error", item.Code, item.Message, null, item.PageNumber, item.Surface, item.AssetId?.ToString("D"))).ToArray(),
                    PublicationPreparationService.DiagnosticsJsonOptions);
                candidate.CompletedAt = DateTime.UtcNow;
            }
            else
            {
                candidate.Step = "Typesetting and validating publication images";
                candidate.ProgressPercent = 10;
            }
        }, CancellationToken.None);
        return result.Summary.Failures.Count == 0 ? result : null;
    }

    private async Task<bool> IsCancelledAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        return await operation.Db.PublicationPreparationJobs.AsNoTracking()
            .Where(item => item.Id == jobId)
            .Select(item => item.CancellationRequested || item.Status == PublicationPreparationStatus.Cancelled)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private async Task<PublicationPreflightReport?> PreflightOrBlockAsync(
        PublicationPreparationJob job,
        IPublicationPackageService packages,
        CancellationToken cancellationToken)
    {
        var report = await packages.PreflightAsync(job.ProjectId, job.EditionId!.Value, cancellationToken);
        var blockers = report.Items.Where(item => item.Severity == "error").ToList();
        if (blockers.Count == 0) return report;

        await UpdateJobAsync(job.Id, candidate =>
        {
            candidate.Status = PublicationPreparationStatus.Blocked;
            candidate.Message = blockers[0].Message;
            candidate.DiagnosticsJson = JsonSerializer.Serialize(blockers, PublicationPreparationService.DiagnosticsJsonOptions);
            candidate.CompletedAt = DateTime.UtcNow;
        }, cancellationToken);
        return null;
    }

    private async Task<IReadOnlyList<PublicationPreflightItem>> WaitForRenderAsync(
        PublicationPreparationJob preparation,
        Guid renderJobId,
        int progressStart,
        int progressRange,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await using var operation = await database.OpenWriteAsync(cancellationToken);
            var db = operation.Db;
            var state = await db.PublicationRenderJobs.AsNoTracking().SingleAsync(item => item.Id == renderJobId, cancellationToken);
            var diagnostics = JsonSerializer.Deserialize<List<PublicationRenderDiagnostic>>(
                state.DiagnosticsJson,
                PublicationPreparationService.DiagnosticsJsonOptions) ?? [];
            var mappedProgress = progressStart + state.ProgressPercent * progressRange / 100;
            await db.PublicationPreparationJobs
                .Where(item => item.Id == preparation.Id
                    && item.Status == PublicationPreparationStatus.Preparing
                    && item.ProgressPercent < mappedProgress)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.ProgressPercent, mappedProgress)
                    .SetProperty(item => item.Step, state.ProgressMessage), cancellationToken);
            if (state.Status == PublicationRenderStatus.Completed)
                return diagnostics.Select(item => new PublicationPreflightItem(
                    item.Severity, item.Code, item.Message, null,
                    item.Page, item.SourceKind, item.SourceId)).ToList();
            if (state.Status is PublicationRenderStatus.Failed or PublicationRenderStatus.Cancelled)
            {
                var details = string.Join(' ', diagnostics
                    .Select(item => item.Message.Trim())
                    .Where(message => !string.IsNullOrWhiteSpace(message))
                    .Distinct(StringComparer.Ordinal));
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(details)
                    ? state.ProgressMessage
                    : details);
            }
            await operation.DisposeAsync();
            await Task.Delay(250, cancellationToken);
        }
    }

    private static async Task<List<PublicationPreflightItem>> ReadinessBlockersAsync(
        IAppDatabaseOperationFactory database,
        AppDbContext db,
        PublicationPreparationJob job,
        IPrintArtifactProfileRegistry printArtifactProfiles,
        CancellationToken cancellationToken)
    {
        string title; string author; string language;
        if (job.TargetKind == PublicationTargetKind.CoreBook)
        {
            var core = await db.PublicationBooks.AsNoTracking().SingleAsync(item => item.ProjectId == job.ProjectId, cancellationToken);
            (title, author, language) = (core.Title, core.Author, core.Language);
        }
        else
        {
            var resolver = new PublicationEffectiveConfigurationResolver(database);
            var effective = await resolver.ResolveReleaseAsync(db, job.ProjectId, job.EditionId!.Value, cancellationToken);
            (title, author, language) = (effective.Edition.TitleOverride, effective.Edition.Author, effective.Edition.Language);
        }
        var result = new List<PublicationPreflightItem>();
        if (string.IsNullOrWhiteSpace(title)) result.Add(new("error", "TITLE_REQUIRED", "Add the book title in Core Book."));
        if (string.IsNullOrWhiteSpace(author)) result.Add(new("error", "AUTHOR_REQUIRED", "Add the author in Core Book or customize it for this release."));
        if (string.IsNullOrWhiteSpace(language)) result.Add(new("error", "LANGUAGE_REQUIRED", "Choose the book language in Core Book."));
        if (job.Edition?.Vendor == PublicationVendor.IngramSpark && string.IsNullOrWhiteSpace(job.Edition.Isbn))
            result.Add(new("error", "ISBN_REQUIRED", "Add the ISBN assigned to this IngramSpark release."));
        if (job.Edition is { Format: PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover } printEdition
            && PrintGeometryService.MissingPrinterDimensions(printEdition, printArtifactProfiles.GetRequired(printEdition.PrintArtifactProfileKey)) is { } missing)
            result.Add(new("error", "PRINTER_DIMENSIONS_REQUIRED", missing));
        return result;
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        var recoveredIds = new List<Guid>();
        await using var operation = await database.OpenWriteAsync(cancellationToken);
        var db = operation.Db;
        var jobs = await db.PublicationPreparationJobs.Where(item => item.Status == PublicationPreparationStatus.Queued
            || item.Status == PublicationPreparationStatus.Preparing).ToListAsync(cancellationToken);
        foreach (var job in jobs)
        {
            job.Status = PublicationPreparationStatus.Queued;
            job.Message = "Recovered after restart";
            recoveredIds.Add(job.Id);
        }
        await operation.SaveChangesAsync(cancellationToken);
        await operation.DisposeAsync();
        foreach (var jobId in recoveredIds)
            await queue.EnqueueAsync(jobId, cancellationToken);
    }

    private async Task FailAsync(Guid jobId, Exception exception, CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenWriteAsync(cancellationToken);
        var db = operation.Db;
        var job = await db.PublicationPreparationJobs.SingleOrDefaultAsync(item => item.Id == jobId, cancellationToken);
        if (job is null || job.Status == PublicationPreparationStatus.Cancelled) return;
        job.Status = PublicationPreparationStatus.Blocked;
        job.Message = exception.Message;
        job.DiagnosticsJson = JsonSerializer.Serialize(
            new[] { new PublicationPreflightItem("error", "PREPARATION_FAILED", exception.Message) },
            PublicationPreparationService.DiagnosticsJsonOptions);
        job.CompletedAt = DateTime.UtcNow;
        await operation.SaveChangesAsync(cancellationToken);
        logger.LogWarning("Preparation job {JobId} marked blocked: {Message}", jobId, exception.Message);
    }

    private async Task UpdateJobAsync(
        Guid jobId,
        Action<PublicationPreparationJob> update,
        CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenWriteAsync(cancellationToken);
        var job = await operation.Db.PublicationPreparationJobs
            .SingleAsync(candidate => candidate.Id == jobId, cancellationToken);
        update(job);
        await operation.SaveChangesAsync(cancellationToken);
    }

}
