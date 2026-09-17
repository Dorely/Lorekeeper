using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Persistence.Repositories;
using Lorekeeper.Startup;

namespace Lorekeeper.ImportExport;

public sealed class ProjectImportJobWorker(
    IServiceScopeFactory scopeFactory,
    IAppDatabaseOperationFactory database,
    IProjectImportJobQueue queue,
    IApplicationStartupState startup,
    IProjectImportFileStore fileStore,
    ILogger<ProjectImportJobWorker> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (!await startup.WaitForDatabaseReadyAsync(stoppingToken))
                return;
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
        await using var operation = await database.OpenWriteAsync(cancellationToken);
        var repo = operation.Repositories.ProjectImports;
        var interrupted = await repo.ListInterruptedJobsAsync(cancellationToken);
        var terminalFileKeys = new List<ProjectImportJobFileKey>();
        foreach (var job in interrupted)
        {
            if (job.Status is ProjectImportJobStatus.Committed or ProjectImportJobStatus.Indexing)
            {
                // Creative state is already durable. Leave this work runnable so
                // the processor can perform a fresh, post-commit index pass.
                job.CurrentMessage = "Import committed before restart; resuming post-commit indexing.";
                job.ErrorMessage = null;
            }
            else
            {
                job.Status = ProjectImportJobStatus.Failed;
                job.CurrentMessage = "Import stopped after application restart.";
                job.ErrorMessage = "The application restarted while this import was applying; it was not resumed automatically.";
                job.CompletedAt = DateTime.UtcNow;
                terminalFileKeys.Add(new ProjectImportJobFileKey(job.StagedFileKey));
            }
            job.UpdatedAt = DateTime.UtcNow;
            repo.UpdateJob(job);
        }

        if (interrupted.Count > 0)
        {
            await operation.SaveChangesAsync(cancellationToken);
            foreach (var terminalFileKey in terminalFileKeys)
                TryDeleteStagedFile(terminalFileKey);
        }
    }

    private void TryDeleteStagedFile(ProjectImportJobFileKey key)
    {
        try
        {
            fileStore.Delete(key);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not remove staged import file {FileKey}", key.Value);
        }
    }

    private async Task EnqueueQueuedJobsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> queuedIds;
        await using (var operation = await database.OpenReadAsync(cancellationToken))
            queuedIds = (await operation.Repositories.ProjectImports.ListRunnableJobsAsync(cancellationToken))
                .Select(job => job.Id)
                .ToList();
        foreach (var jobId in queuedIds)
            queue.Enqueue(jobId);
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
