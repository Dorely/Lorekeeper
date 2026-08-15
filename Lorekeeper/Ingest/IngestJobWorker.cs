using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Persistence.Repositories;
using Microsoft.Extensions.Hosting;

namespace Lorekeeper.Ingest;

public sealed class IngestJobWorker(
    IServiceScopeFactory scopeFactory,
    IAppDatabaseOperationFactory database,
    IIngestJobQueue queue,
    IIngestJobNotifier notifier,
    ILogger<IngestJobWorker> logger) : BackgroundService
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
            logger.LogError(ex, "IngestJobWorker terminated unexpectedly.");
        }
    }

    private async Task MarkInterruptedJobsAsync(CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenWriteAsync(cancellationToken);
        var repo = operation.Repositories.Ingest;
        var interrupted = await repo.ListInterruptedJobsAsync(cancellationToken);
        var repairedJobs = new List<(Guid ProjectId, Guid JobId)>();
        foreach (var job in interrupted)
        {
            foreach (var chunk in job.Chunks.Where(chunk => chunk.Status == IngestJobChunkStatus.Running))
            {
                chunk.Status = IngestJobChunkStatus.Stopped;
                chunk.ErrorMessage = "Stopped after application restart.";
                chunk.CompletedAt = DateTime.UtcNow;
                chunk.UpdatedAt = DateTime.UtcNow;
                repo.UpdateJobChunk(chunk);
            }

            job.Status = IngestJobStatus.Stopped;
            job.CurrentMessage = "Stopped after application restart.";
            job.ErrorMessage = null;
            job.CompletedSourceChunks = job.Chunks.Count(chunk => chunk.Status == IngestJobChunkStatus.Completed);
            job.CompletedAt = DateTime.UtcNow;
            job.UpdatedAt = DateTime.UtcNow;
            repo.UpdateJob(job);
            await repo.AddEventAsync(new IngestJobEvent
            {
                JobId = job.Id,
                Level = IngestJobEventLevel.Warning,
                EventType = "job.interrupted_recovered",
                Message = "The application restarted while this ingest job was marked running, so it was moved to Stopped and can be resumed.",
            }, cancellationToken);
            repairedJobs.Add((job.ProjectId, job.Id));
        }
        if (interrupted.Count > 0)
        {
            await operation.SaveChangesAsync(cancellationToken);
            foreach (var repairedJob in repairedJobs)
                notifier.Notify(new IngestJobUpdate(repairedJob.ProjectId, repairedJob.JobId, IngestJobUpdateKind.Stopped, DateTime.UtcNow));
        }
    }

    private async Task EnqueueQueuedJobsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> queuedIds;
        await using (var operation = await database.OpenReadAsync(cancellationToken))
            queuedIds = (await operation.Repositories.Ingest.ListQueuedJobsAsync(cancellationToken))
                .Select(job => job.Id)
                .ToList();
        foreach (var jobId in queuedIds)
            queue.Enqueue(jobId);
    }

    private async Task RunJobAsync(Guid jobId, CancellationToken stoppingToken)
    {
        using var jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        queue.RegisterCancellation(jobId, jobCancellation);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredService<IngestJobProcessor>();
            await processor.RunAsync(jobId, jobCancellation.Token);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled ingest job failure for {JobId}", jobId);
        }
        finally
        {
            queue.ClearCancellation(jobId);
        }
    }
}
