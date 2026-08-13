using System.Text.Json;
using System.Threading.Channels;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
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
    AppDbContext db,
    IPublicationPreparationQueue queue,
    IPublicationBookService books,
    IPublicationEditionService editions,
    IPublicationRenderService renders,
    IProjectMutationCoordinator projectMutations) : IPublicationPreparationService
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
        _ = await books.GetOrCreateAsync(projectId, cancellationToken);
        var fingerprint = targetKind == PublicationTargetKind.CoreBook
            ? await books.GetSourceFingerprintAsync(projectId, cancellationToken)
            : await editions.GetSourceFingerprintAsync(projectId, editionId!.Value, cancellationToken);
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
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
        CancellationToken cancellationToken = default) =>
        (await db.PublicationPreparationJobs.AsNoTracking()
            .Include(item => item.RenderJob)
            .Where(item => item.ProjectId == projectId
            && item.TargetKind == targetKind && item.EditionId == editionId)
            .OrderByDescending(item => item.CreatedAt).ToListAsync(cancellationToken)).Select(View).ToList();

    public async Task<PublicationPreparationJobView> CancelAsync(Guid projectId, Guid jobId, CancellationToken cancellationToken = default)
    {
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
        if (diagnostics.Any(item => !IsUsable(item)) && job.RenderJob is not null)
            diagnostics = DeserializeRenderDiagnostics(job.RenderJob.DiagnosticsJson);
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
    IPublicationPreparationQueue queue,
    ILogger<PublicationPreparationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.PublicationPreparationJobs.Include(item => item.Edition).SingleAsync(item => item.Id == jobId, cancellationToken);
        if (job.CancellationRequested || job.Status == PublicationPreparationStatus.Cancelled) return;
        job.Status = PublicationPreparationStatus.Preparing;
        job.Step = "Checking readiness";
        job.ProgressPercent = 5;
        job.StartedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        var preparationDiagnostics = new List<PublicationPreflightItem>();

        var blockers = await ReadinessBlockersAsync(db, job, cancellationToken);
        if (blockers.Count > 0)
        {
            job.Status = PublicationPreparationStatus.Blocked;
            job.Message = blockers[0].Message;
            job.DiagnosticsJson = JsonSerializer.Serialize(blockers, PublicationPreparationService.DiagnosticsJsonOptions);
            job.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        if (job.TargetKind == PublicationTargetKind.CoreBook)
        {
            var renders = scope.ServiceProvider.GetRequiredService<IPublicationRenderService>();
            var current = (await renders.ListCoreAsync(job.ProjectId, cancellationToken)).FirstOrDefault(item =>
                item.Status == PublicationRenderStatus.Completed && item.Artifacts.Any(artifact => artifact.Kind == PublicationArtifactKind.ReadingPdf && !artifact.IsStale));
            var render = current ?? await renders.RequestCoreAsync(job.ProjectId, cancellationToken);
            job.RenderJobId = render.Id;
            job.Step = "Typesetting and validating reading PDF";
            job.ProgressPercent = 20;
            await db.SaveChangesAsync(cancellationToken);
            preparationDiagnostics.AddRange(await WaitForRenderAsync(db, job, cancellationToken));
        }
        else if (job.Edition!.Format == PublicationEditionFormat.Epub)
        {
            job.Step = "Exporting and validating EPUB";
            job.ProgressPercent = 35;
            await db.SaveChangesAsync(cancellationToken);
            var packages = scope.ServiceProvider.GetRequiredService<IPublicationPackageService>();
            if (await BlockForPreflightAsync(db, job, packages, cancellationToken)) return;
            await packages.BuildAsync(job.ProjectId, job.EditionId!.Value, cancellationToken);
        }
        else
        {
            var renders = scope.ServiceProvider.GetRequiredService<IPublicationRenderService>();
            var current = (await renders.ListAsync(job.ProjectId, job.EditionId!.Value, cancellationToken)).FirstOrDefault(item =>
                item.Status == PublicationRenderStatus.Completed && item.Artifacts.Any() && item.Artifacts.All(artifact => !artifact.IsStale));
            var render = current ?? await renders.RequestAsync(job.ProjectId, job.EditionId.Value, cancellationToken);
            job.RenderJobId = render.Id;
            job.Step = "Rendering and validating publication files";
            job.ProgressPercent = 20;
            await db.SaveChangesAsync(cancellationToken);
            preparationDiagnostics.AddRange(await WaitForRenderAsync(db, job, cancellationToken));
            db.ChangeTracker.Clear();
            job = await db.PublicationPreparationJobs.Include(item => item.Edition).SingleAsync(item => item.Id == jobId, cancellationToken);
            job.Step = "Building publication package";
            job.ProgressPercent = 85;
            await db.SaveChangesAsync(cancellationToken);
            var packages = scope.ServiceProvider.GetRequiredService<IPublicationPackageService>();
            if (await BlockForPreflightAsync(db, job, packages, cancellationToken)) return;
            await packages.BuildAsync(job.ProjectId, job.EditionId!.Value, cancellationToken);
        }

        db.ChangeTracker.Clear();
        job = await db.PublicationPreparationJobs.SingleAsync(item => item.Id == jobId, cancellationToken);
        if (job.CancellationRequested || job.Status == PublicationPreparationStatus.Cancelled)
            return;
        job.Status = PublicationPreparationStatus.Ready;
        job.Step = "Ready";
        job.ProgressPercent = 100;
        job.Message = job.TargetKind == PublicationTargetKind.CoreBook
            ? preparationDiagnostics.Count > 0 ? "Reading PDF ready with warnings" : "Reading PDF ready"
            : "Publication files ready";
        job.DiagnosticsJson = JsonSerializer.Serialize(preparationDiagnostics, PublicationPreparationService.DiagnosticsJsonOptions);
        job.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<bool> BlockForPreflightAsync(
        AppDbContext db,
        PublicationPreparationJob job,
        IPublicationPackageService packages,
        CancellationToken cancellationToken)
    {
        var report = await packages.PreflightAsync(job.ProjectId, job.EditionId!.Value, cancellationToken);
        var blockers = report.Items.Where(item => item.Severity == "error").ToList();
        if (blockers.Count == 0) return false;

        job.Status = PublicationPreparationStatus.Blocked;
        job.Message = blockers[0].Message;
        job.DiagnosticsJson = JsonSerializer.Serialize(blockers, PublicationPreparationService.DiagnosticsJsonOptions);
        job.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static async Task<IReadOnlyList<PublicationPreflightItem>> WaitForRenderAsync(
        AppDbContext db,
        PublicationPreparationJob preparation,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            db.ChangeTracker.Clear();
            var state = await db.PublicationRenderJobs.AsNoTracking().SingleAsync(item => item.Id == preparation.RenderJobId, cancellationToken);
            var diagnostics = JsonSerializer.Deserialize<List<PublicationRenderDiagnostic>>(
                state.DiagnosticsJson,
                PublicationPreparationService.DiagnosticsJsonOptions) ?? [];
            if (state.Status == PublicationRenderStatus.Completed)
                return diagnostics.Select(item => new PublicationPreflightItem(
                    item.Severity, item.Code, item.Message, null,
                    item.Page, item.SourceKind, item.SourceId)).ToList();
            if (state.Status is PublicationRenderStatus.Failed or PublicationRenderStatus.Cancelled)
                throw new InvalidOperationException(state.ProgressMessage + " " + string.Join(' ', diagnostics.Select(item => item.Message)));
            await Task.Delay(250, cancellationToken);
        }
    }

    private static async Task<List<PublicationPreflightItem>> ReadinessBlockersAsync(AppDbContext db, PublicationPreparationJob job, CancellationToken cancellationToken)
    {
        string title; string author; string language;
        if (job.TargetKind == PublicationTargetKind.CoreBook)
        {
            var core = await db.PublicationBooks.AsNoTracking().SingleAsync(item => item.ProjectId == job.ProjectId, cancellationToken);
            (title, author, language) = (core.Title, core.Author, core.Language);
        }
        else
        {
            var resolver = new PublicationEffectiveConfigurationResolver(db);
            var effective = await resolver.ResolveReleaseAsync(job.ProjectId, job.EditionId!.Value, cancellationToken);
            (title, author, language) = (effective.Edition.TitleOverride, effective.Edition.Author, effective.Edition.Language);
        }
        var result = new List<PublicationPreflightItem>();
        if (string.IsNullOrWhiteSpace(title)) result.Add(new("error", "TITLE_REQUIRED", "Add the book title in Core Book."));
        if (string.IsNullOrWhiteSpace(author)) result.Add(new("error", "AUTHOR_REQUIRED", "Add the author in Core Book or customize it for this release."));
        if (string.IsNullOrWhiteSpace(language)) result.Add(new("error", "LANGUAGE_REQUIRED", "Choose the book language in Core Book."));
        if (job.Edition?.Vendor == PublicationVendor.IngramSpark && string.IsNullOrWhiteSpace(job.Edition.Isbn))
            result.Add(new("error", "ISBN_REQUIRED", "Add the ISBN assigned to this IngramSpark release."));
        return result;
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var jobs = await db.PublicationPreparationJobs.Where(item => item.Status == PublicationPreparationStatus.Queued
            || item.Status == PublicationPreparationStatus.Preparing).ToListAsync(cancellationToken);
        foreach (var job in jobs) { job.Status = PublicationPreparationStatus.Queued; job.Message = "Recovered after restart"; await queue.EnqueueAsync(job.Id, cancellationToken); }
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task FailAsync(Guid jobId, Exception exception, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.PublicationPreparationJobs.SingleOrDefaultAsync(item => item.Id == jobId, cancellationToken);
        if (job is null || job.Status == PublicationPreparationStatus.Cancelled) return;
        job.Status = PublicationPreparationStatus.Blocked;
        job.Message = exception.Message;
        job.DiagnosticsJson = JsonSerializer.Serialize(
            new[] { new PublicationPreflightItem("error", "PREPARATION_FAILED", exception.Message) },
            PublicationPreparationService.DiagnosticsJsonOptions);
        job.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }
}
