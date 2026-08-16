using System.Diagnostics;
using Lorekeeper.Authoring;
using Lorekeeper.Knowledge;
using Lorekeeper.Outline;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Startup;

public sealed class ApplicationStartupWorker(
    IServiceScopeFactory scopeFactory,
    ApplicationStartupState startup,
    ApplicationStartupOptions options,
    ILogger<ApplicationStartupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the web host and Electron bridge become available before any
        // provider may complete its asynchronous SQLite work synchronously.
        await Task.Yield();
        var elapsed = Stopwatch.StartNew();
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var startupMigration = scope.ServiceProvider.GetRequiredService<IDatabaseStartupMigrationService>();
            if (!await startupMigration.ApplyAsync(stoppingToken, startup))
            {
                await WaitForMinimumSplashDurationAsync(elapsed, stoppingToken);
                startup.CompleteRecovery();
                return;
            }

            startup.ReportInitialization(
                "Recovering interrupted work",
                "Closing authoring batches that were interrupted by an earlier shutdown.",
                76);
            await scope.ServiceProvider.GetRequiredService<IAuthoringHistoryService>()
                .FinalizeAbandonedBatchesAsync(stoppingToken);

            startup.ReportInitialization(
                "Preparing search memory",
                "Initializing local search and semantic-memory storage.",
                84);
            var database = scope.ServiceProvider.GetRequiredService<IAppDatabaseOperationFactory>();
            int? embeddingDimensions;
            IReadOnlyList<Guid> projectIds;
            await using (var read = await database.OpenReadAsync(stoppingToken))
            {
                embeddingDimensions = await read.Db.EmbeddingConfigurations
                    .Select(configuration => (int?)configuration.Dimensions)
                    .FirstOrDefaultAsync(stoppingToken);
                projectIds = (await read.Repositories.Projects.ListAsync(stoppingToken))
                    .Select(project => project.Id)
                    .ToList();
            }

            scope.ServiceProvider.GetRequiredService<IVectorStoreMaintenance>()
                .Initialize(embeddingDimensions);

            var outlineGraphSync = scope.ServiceProvider.GetRequiredService<IOutlineGraphSync>();
            for (var index = 0; index < projectIds.Count; index++)
            {
                startup.ReportInitialization(
                    "Checking project structure",
                    $"Repairing project graph {index + 1} of {projectIds.Count}.",
                    88 + (projectIds.Count == 0 ? 0 : (int)Math.Round((index + 1) * 8d / projectIds.Count)));
                await outlineGraphSync.RepairProjectAsync(projectIds[index], stoppingToken);
            }

            if (projectIds.Count == 0)
            {
                startup.ReportInitialization(
                    "Finishing startup",
                    "Your workspace is ready.",
                    96);
            }

            await WaitForMinimumSplashDurationAsync(elapsed, stoppingToken);
            startup.Complete();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "Application startup failed before the workspace became ready.");
            await WaitForMinimumSplashDurationAsync(elapsed, CancellationToken.None);
            startup.Fail(exception);
        }
    }

    private async Task WaitForMinimumSplashDurationAsync(
        Stopwatch elapsed,
        CancellationToken cancellationToken)
    {
        var remaining = options.MinimumSplashDuration - elapsed.Elapsed;
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining, cancellationToken);
    }
}
