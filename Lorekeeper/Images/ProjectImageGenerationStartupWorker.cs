using Lorekeeper.Startup;

namespace Lorekeeper.Images;

public sealed class ProjectImageGenerationStartupWorker(
    IProjectImageGenerationRuntime runtime,
    IApplicationStartupState startup,
    ILogger<ProjectImageGenerationStartupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (!await startup.WaitForDatabaseReadyAsync(stoppingToken))
                return;
            await runtime.ReconcileInterruptedJobsAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Project image generation startup reconciliation failed.");
        }
    }
}
