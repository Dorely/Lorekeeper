namespace Lorekeeper.Images;

public interface IProjectImageGenerationRuntime
{
    event EventHandler? StateChanged;

    ProjectImageGenerationRuntimeSnapshot GetSnapshot();
    Task EnqueueProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task CancelJobAsync(Guid projectId, Guid jobId, CancellationToken cancellationToken = default);
    Task<bool> WaitForJobCompletionAsync(Guid jobId, TimeSpan timeout, CancellationToken cancellationToken = default);
    Task ReconcileInterruptedJobsAsync(CancellationToken cancellationToken = default);
}
