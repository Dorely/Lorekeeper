namespace Lorekeeper.Llm;

public sealed class EmbeddingRebuildWorker(
    IEmbeddingRebuildQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<EmbeddingRebuildWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            EmbeddingRebuildRequest request;
            try
            {
                request = await queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            EmbeddingRebuildRun? run = null;
            try
            {
                run = queue.BeginRun(request, stoppingToken);
                if (run.IsSkipped)
                    continue;

                await using var scope = scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<EmbeddingRebuildService>();
                await service.RebuildAsync(request, run.CancellationToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Embedding rebuild was cancelled.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Embedding rebuild failed.");
            }
            finally
            {
                if (run is not null)
                {
                    queue.CompleteRun(run);
                    run.Dispose();
                }
            }
        }
    }
}
