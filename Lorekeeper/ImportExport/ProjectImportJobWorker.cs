using Lorekeeper.Models;
using Lorekeeper.Persistence.Repositories;

namespace Lorekeeper.ImportExport;

public sealed class ProjectImportJobWorker(
    IServiceScopeFactory scopeFactory,
    IProjectImportJobQueue queue,
    ILogger<ProjectImportJobWorker> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            await MarkInterruptedJobsAsync(stoppingToken);
            await EnqueueQueuedJobsAsync(stoppingToken);

            await foreach (var jobId in queue.ReadAllAsync(stoppingToken))
            {
                if (stoppingToken.IsCancellationRequested) break;
                await RunJobAsync(jobId, stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "ProjectImportJobWorker terminated unexpectedly.");
        }
    }

    private async Task MarkInterruptedJobsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProjectImportRepository>();
        var interrupted = await repo.ListInterruptedJobsAsync(cancellationToken);
        foreach (var job in interrupted)
        {
            job.Status = ProjectImportJobStatus.Failed;
            job.CurrentMessage = "Import stopped after application restart.";
            job.ErrorMessage = "The application restarted while this import was running.";
            job.CompletedAt = DateTime.UtcNow;
            job.UpdatedAt = DateTime.UtcNow;
            repo.UpdateJob(job);
        }

        if (interrupted.Count > 0)
            await repo.SaveChangesAsync(cancellationToken);
    }

    private async Task EnqueueQueuedJobsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProjectImportRepository>();
        var queued = await repo.ListQueuedJobsAsync(cancellationToken);
        foreach (var job in queued)
            queue.Enqueue(job.Id);
    }

    private async Task RunJobAsync(Guid jobId, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredService<ProjectImportJobProcessor>();
            await processor.RunAsync(jobId, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled project import job failure for {JobId}", jobId);
        }
    }
}
