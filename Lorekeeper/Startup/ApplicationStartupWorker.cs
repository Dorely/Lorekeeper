using System.Diagnostics;
using Lorekeeper.Knowledge;
using Lorekeeper.Outline;
using Lorekeeper.Persistence;
using Lorekeeper.VersionHistory.Services;
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
                "Checking project history",
                "Reconciling local project checkpoints.",
                72);
            var historyReconciliation = scope.ServiceProvider
                .GetRequiredService<IProjectVersionHistoryReconciliationService>();
            var historyReport = await historyReconciliation.ReconcileAsync(stoppingToken);
            foreach (var item in historyReport.Items.Where(item => item.State is
                         ProjectVersionReconciliationState.Missing
                         or ProjectVersionReconciliationState.Corrupt
                         or ProjectVersionReconciliationState.Diverged))
            {
                logger.LogWarning(
                    "Project history reconciliation reported {State} for project {ProjectId} and repository {RepositoryId}: {Diagnostic}",
                    item.State,
                    item.ProjectId,
                    item.RepositoryId,
                    item.Diagnostic);
            }
            foreach (var tombstone in historyReport.DeletionTombstones.Where(item =>
                         item.State == ProjectVersionDeletionTombstoneState.Preserved))
            {
                logger.LogWarning(
                    "Project history deletion tombstone was preserved at {Path} for repository {RepositoryId}: {Diagnostic}",
                    tombstone.Path,
                    tombstone.RepositoryId,
                    tombstone.Diagnostic);
            }

            startup.ReportInitialization(
                "Preparing search memory",
                "Initializing local search and semantic-memory storage.",
                76);
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
