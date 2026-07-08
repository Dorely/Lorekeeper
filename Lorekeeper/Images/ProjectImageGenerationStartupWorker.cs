namespace Lorekeeper.Images;

public sealed class ProjectImageGenerationStartupWorker(
    IProjectImageGenerationRuntime runtime,
    ILogger<ProjectImageGenerationStartupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
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
