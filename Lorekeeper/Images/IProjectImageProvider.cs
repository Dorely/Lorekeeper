using Lorekeeper.Models;

namespace Lorekeeper.Images;

/// <summary>
/// One image-provider kind's transport. The runtime passes the stored provider
/// row; adapters read their own endpoint and credentials from it and never
/// return secrets in results or exceptions.
/// </summary>
public interface IProjectImageProvider
{
    ImageProviderKind Kind { get; }

    Task<ProjectImageProviderResult> GenerateAsync(
        ImageProvider provider,
        ProjectImageProviderGenerateRequest request,
        CancellationToken cancellationToken = default,
        IProgress<ProjectImageProviderProgress>? progress = null);

    Task<ProjectImageProviderResult> EditAsync(
        ImageProvider provider,
        ProjectImageProviderEditRequest request,
        CancellationToken cancellationToken = default,
        IProgress<ProjectImageProviderProgress>? progress = null);

    /// <summary>Verifies the connection without a billed generation.</summary>
    Task<ImageProviderTestResult> TestAsync(ImageProvider provider, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DiscoveredImageModel>> DiscoverModelsAsync(ImageProvider provider, CancellationToken cancellationToken = default);

    /// <summary>
    /// The wait before retrying after <paramref name="error"/> on the given
    /// one-based attempt, or null when the error is not worth retrying.
    /// </summary>
    TimeSpan? GetRetryDelay(Exception error, int attempt);
}

public interface IImageProviderClientFactory
{
    IProjectImageProvider Get(ImageProviderKind kind);

    /// <summary>Whether an adapter for this kind is registered, so settings offer only kinds that can run.</summary>
    bool Supports(ImageProviderKind kind);
}

public sealed class ImageProviderClientFactory(IEnumerable<IProjectImageProvider> adapters) : IImageProviderClientFactory
{
    public IProjectImageProvider Get(ImageProviderKind kind) =>
        adapters.FirstOrDefault(adapter => adapter.Kind == kind)
        ?? throw new InvalidOperationException($"Image provider kind '{kind}' is not supported.");

    public bool Supports(ImageProviderKind kind) => adapters.Any(adapter => adapter.Kind == kind);
}
