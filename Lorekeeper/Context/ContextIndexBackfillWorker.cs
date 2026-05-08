using Lorekeeper.Persistence.Repositories;

namespace Lorekeeper.Context;

public sealed class ContextIndexBackfillWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<ContextIndexBackfillWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Yield();
            using var scope = scopeFactory.CreateScope();
            var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
            var indexer = scope.ServiceProvider.GetRequiredService<IContextIndexingService>();

            foreach (var project in await projects.ListAsync(stoppingToken))
            {
                if (stoppingToken.IsCancellationRequested) break;
                await indexer.ReindexProjectAsync(project.Id, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Context vector backfill did not complete.");
        }
    }
}