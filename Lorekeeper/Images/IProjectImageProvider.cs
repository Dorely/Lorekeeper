namespace Lorekeeper.Images;

public interface IProjectImageProvider
{
    Task<ProjectImageProviderResult> GenerateAsync(
        ProjectImageProviderGenerateRequest request,
        CancellationToken cancellationToken = default,
        IProgress<ProjectImageProviderProgress>? progress = null);

    Task<ProjectImageProviderResult> EditAsync(
        ProjectImageProviderEditRequest request,
        CancellationToken cancellationToken = default,
        IProgress<ProjectImageProviderProgress>? progress = null);
}
