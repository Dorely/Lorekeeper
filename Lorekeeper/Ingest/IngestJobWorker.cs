using Lorekeeper.Models;
using Lorekeeper.Persistence.Repositories;
using Microsoft.Extensions.Hosting;

namespace Lorekeeper.Ingest;

public sealed class IngestJobWorker(
    IServiceScopeFactory scopeFactory,
    IIngestJobQueue queue,
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
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IIngestRepository>();
        var interrupted = await repo.ListInterruptedJobsAsync(cancellationToken);
        foreach (var job in interrupted)
        {
            job.Status = IngestJobStatus.Stopped;
            job.CurrentMessage = "Stopped after application restart.";
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
        var repo = scope.ServiceProvider.GetRequiredService<IIngestRepository>();
        var queued = await repo.ListQueuedJobsAsync(cancellationToken);
        foreach (var job in queued)
            queue.Enqueue(job.Id);
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