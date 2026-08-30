using System.Text.Json;
using System.Threading.Channels;
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
    DateTime? CompletedAt);

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
    IPublicationRenderService renders) : IPublicationPreparationService
{
    internal static JsonSerializerOptions DiagnosticsJsonOptions { get; } = new(JsonSerializerDefaults.Web);

    public Task<PublicationPreparationJobView> PrepareCoreAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        CreateAsync(projectId, PublicationTargetKind.CoreBook, null, cancellationToken);

    public Task<PublicationPreparationJobView> PrepareReleaseAsync(Guid projectId, Guid editionId, CancellationToken cancellationToken = default) =>
        CreateAsync(projectId, PublicationTargetKind.Release, editionId, cancellationToken);

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
                    .Include(item => item.RenderJob)
                    .Where(item => item.ProjectId == projectId
                    && item.TargetKind == targetKind && item.EditionId == editionId)
                    .OrderByDescending(item => item.CreatedAt).ToListAsync(cancellationToken)).Select(View).ToList();
    }
    public async Task<PublicationPreparationJobView> CancelAsync(Guid projectId, Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var job = await db.PublicationPreparationJobs.SingleAsync(item => item.ProjectId == projectId && item.Id == jobId, cancellationToken);
        if (job.Status is PublicationPreparationStatus.Queued or PublicationPreparationStatus.Preparing)
        {
            job.CancellationRequested = true;
            job.Status = PublicationPreparationStatus.Cancelled;
            job.Message = "Cancelled";
            job.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            if (job.RenderJobId is Guid renderJobId)
            {
                if (job.TargetKind == PublicationTargetKind.CoreBook)
                    await renders.CancelCoreAsync(projectId, renderJobId, cancellationToken);
                else if (job.EditionId is Guid editionId)
                    await renders.CancelAsync(projectId, editionId, renderJobId, cancellationToken);
            }
        }
        return View(job);
    }

    internal static PublicationPreparationJobView View(PublicationPreparationJob job)
    {
        var diagnostics = DeserializeDiagnostics(job.DiagnosticsJson);
        if (job.RenderJob is not null
            && (diagnostics.Count == 0
                || diagnostics.Any(item => !IsUsable(item)
                    || item.Code is "PREPARATION_FAILED" or "PREPARATION_DIAGNOSTICS_INVALID")))
        {
            var renderDiagnostics = DeserializeRenderDiagnostics(job.RenderJob.DiagnosticsJson);
            if (renderDiagnostics.Count > 0)
                diagnostics = renderDiagnostics;
        }
        return new(
            job.Id, job.TargetKind, job.EditionId, job.Status, job.Step, job.ProgressPercent, job.Message,
            diagnostics, job.CreatedAt, job.CompletedAt);
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
}

public sealed class PublicationPreparationWorker(
    IServiceScopeFactory scopeFactory,
    IAppDatabaseOperationFactory database,
    IPublicationPreparationQueue queue,
    IApplicationStartupState startup,
    ILogger<PublicationPreparationWorker> logger) : BackgroundService
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
                try { await ProcessAsync(jobId, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Publication preparation {JobId} failed.", jobId);
                    await FailAsync(jobId, exception, CancellationToken.None);
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

            var blockers = await ReadinessBlockersAsync(database, db, job, cancellationToken);
            if (blockers.Count > 0)
            {
                job.Status = PublicationPreparationStatus.Blocked;
                job.Message = blockers[0].Message;
                job.DiagnosticsJson = JsonSerializer.Serialize(blockers, PublicationPreparationService.DiagnosticsJsonOptions);
                job.CompletedAt = DateTime.UtcNow;
                await operation.SaveChangesAsync(cancellationToken);
                return;
            }

            await operation.SaveChangesAsync(cancellationToken);
        }

        var preparationDiagnostics = new List<PublicationPreflightItem>();

        if (job.TargetKind == PublicationTargetKind.CoreBook)
        {
            var renders = scope.ServiceProvider.GetRequiredService<IPublicationRenderService>();
            var current = (await renders.ListCoreAsync(job.ProjectId, cancellationToken)).FirstOrDefault(item =>
                item.Status == PublicationRenderStatus.Completed && item.Artifacts.Any(artifact => artifact.Kind == PublicationArtifactKind.ReadingPdf && !artifact.IsStale));
            var render = current ?? await renders.RequestCoreAsync(job.ProjectId, cancellationToken);
            job.RenderJobId = render.Id;
            await UpdateJobAsync(jobId, candidate =>
            {
                candidate.RenderJobId = render.Id;
                candidate.Step = "Typesetting and validating reading PDF";
                candidate.ProgressPercent = 10;
            }, cancellationToken);
            preparationDiagnostics.AddRange(await WaitForRenderAsync(job, cancellationToken));
        }
        else if (job.Edition!.Format == PublicationEditionFormat.Epub)
        {
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
        else
        {
            var renders = scope.ServiceProvider.GetRequiredService<IPublicationRenderService>();
            var current = (await renders.ListAsync(job.ProjectId, job.EditionId!.Value, cancellationToken)).FirstOrDefault(item =>
                item.Status == PublicationRenderStatus.Completed && item.Artifacts.Any() && item.Artifacts.All(artifact => !artifact.IsStale));
            var render = current ?? await renders.RequestAsync(job.ProjectId, job.EditionId.Value, cancellationToken);
            job.RenderJobId = render.Id;
            await UpdateJobAsync(jobId, candidate =>
            {
                candidate.RenderJobId = render.Id;
                candidate.Step = "Rendering and validating publication files";
                candidate.ProgressPercent = 10;
            }, cancellationToken);
            preparationDiagnostics.AddRange(await WaitForRenderAsync(job, cancellationToken));
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
            candidate.DiagnosticsJson = JsonSerializer.Serialize(preparationDiagnostics, PublicationPreparationService.DiagnosticsJsonOptions);
            candidate.CompletedAt = DateTime.UtcNow;
        }, cancellationToken);
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
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await using var operation = await database.OpenWriteAsync(cancellationToken);
            var db = operation.Db;
            var state = await db.PublicationRenderJobs.AsNoTracking().SingleAsync(item => item.Id == preparation.RenderJobId, cancellationToken);
            var diagnostics = JsonSerializer.Deserialize<List<PublicationRenderDiagnostic>>(
                state.DiagnosticsJson,
                PublicationPreparationService.DiagnosticsJsonOptions) ?? [];
            var renderRange = preparation.TargetKind == PublicationTargetKind.CoreBook ? 85 : 75;
            var mappedProgress = 10 + state.ProgressPercent * renderRange / 100;
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
        if (job.Edition?.Vendor == PublicationVendor.BarnesAndNoblePress
            && job.Edition.PrintIdentifierMode == PrintIdentifierMode.VendorAssignedIsbn)
            result.Add(new("warning", "BN_ISBN_ASSIGNED_AT_HANDOFF", "B&N Press assigns the free ISBN in its external wizard; preparation may continue."));
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
