using Lorekeeper.Persistence.Repositories;
using Microsoft.Extensions.Hosting;

namespace Lorekeeper.Chapters;

/// <summary>
/// Drains stale chapters: a startup sweep over every chapter currently flagged
/// non-UpToDate, then ongoing notifications from <see cref="IStaleChapterNotifier"/>.
/// Each retry runs in its own DI scope so EF Core / repos are properly scoped.
/// </summary>
public class StaleChapterReindexer(
    IServiceScopeFactory scopeFactory,
    IStaleChapterNotifier notifier,
    ILogger<StaleChapterReindexer> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            await SweepStartupAsync(stoppingToken);

            await foreach (var chapterId in notifier.ReadAllAsync(stoppingToken))
            {
                if (stoppingToken.IsCancellationRequested) break;
                await TryReindexAsync(chapterId, stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "StaleChapterReindexer terminated unexpectedly.");
        }
    }

    private async Task SweepStartupAsync(CancellationToken cancellationToken)
    {
        List<Guid> ids;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IChapterRepository>();
            var stale = await repo.ListStaleAsync(cancellationToken);
            ids = stale.Select(c => c.Id).ToList();
        }

        if (ids.Count == 0) return;
        logger.LogInformation("Found {Count} stale chapters at startup; reindexing.", ids.Count);
        foreach (var id in ids)
        {
            if (cancellationToken.IsCancellationRequested) break;
            await TryReindexAsync(id, cancellationToken);
        }
    }

    private async Task TryReindexAsync(Guid chapterId, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IChapterService>();
            await service.ReindexAsync(chapterId, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Background reindex failed for chapter {ChapterId}; will retry on next change.", chapterId);
            try { await Task.Delay(FailureBackoff, cancellationToken); }
            catch (OperationCanceledException) { throw; }
        }
    }
}
