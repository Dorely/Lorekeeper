using Lorekeeper.Startup;

namespace Lorekeeper.VersionHistory.Sync;

/// <summary>
/// Startup-gated worker for durable automatic checkpoint pushes. The queue is
/// only a wake-up mechanism; each pass queries SQLite for pending or
/// interrupted intent rows and creates a fresh scoped processor.
/// </summary>
public sealed class ProjectVersionAutoPushWorker(
    IProjectVersionAutoPushQueue queue,
    IApplicationStartupState startup,
    IServiceScopeFactory scopeFactory,
    ILogger<ProjectVersionAutoPushWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (!await startup.WaitForDatabaseReadyAsync(stoppingToken))
                return;

            queue.Signal();
            while (await queue.WaitAsync(stoppingToken))
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var processor = scope.ServiceProvider
                        .GetRequiredService<ProjectVersionAutoPushService>();
                    await processor.ProcessPendingAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // A later durable signal or application restart can
                    // retry the rows. The worker itself remains available.
                    logger.LogError(exception, "The automatic version-history push worker could not process a pass.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "The automatic version-history push worker stopped before becoming ready.");
        }
    }
}
