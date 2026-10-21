using System.Text.Json;
using Lorekeeper.Composition;
using Lorekeeper.Models;

namespace Lorekeeper.Images;

/// <summary>
/// The configuration and resolution boundary for image providers. Callers pick
/// an optional provider and model; everything else (capabilities, raster limits,
/// readiness) comes from the stored rows.
/// </summary>
public interface IImageProviderService
{
    Task<List<ImageProvider>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<ImageProvider?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    Task<ImageProvider> CreateAsync(ImageProvider provider, CancellationToken cancellationToken = default);

    Task<ImageProvider> UpdateAsync(ImageProvider provider, CancellationToken cancellationToken = default);

    Task DeleteAsync(int id, CancellationToken cancellationToken = default);

    Task SetDefaultAsync(int id, CancellationToken cancellationToken = default);

    Task<ImageProviderModel> SaveModelAsync(int providerId, ImageProviderModel model, CancellationToken cancellationToken = default);

    Task DeleteModelAsync(int modelId, CancellationToken cancellationToken = default);

    Task SetDefaultModelAsync(int modelId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks a provider without a billed generation: an account-access check or
    /// a model listing. A saved provider (nonzero ID) records the outcome.
    /// </summary>
    Task<ImageProviderTestResult> TestAsync(ImageProvider provider, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DiscoveredImageModel>> DiscoverModelsAsync(ImageProvider provider, CancellationToken cancellationToken = default);

    /// <summary>Adds or refreshes the provider's discovered models; manual rows are left alone.</summary>
    Task<int> RefreshModelsAsync(int providerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the provider and model a request will use. A null provider means
    /// the default provider; a null model means that provider's default model.
    /// </summary>
    /// <exception cref="ImageProviderNotConfiguredException">No ready provider or model matches.</exception>
    Task<ResolvedImageModel> ResolveAsync(int? providerId = null, string? modelId = null, CancellationToken cancellationToken = default);

    /// <summary>Every model of every ready provider, default provider first, for pickers.</summary>
    Task<IReadOnlyList<ResolvedImageModel>> ListReadyModelsAsync(CancellationToken cancellationToken = default);

    Task<ImageProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Assistant tool guidance naming the default provider's models, qualities,
    /// and capabilities; describes the missing configuration when none is ready.
    /// </summary>
    Task<string> GetToolGuidanceAsync(CancellationToken cancellationToken = default);
}

/// <summary>Non-secret description of the provider and model a request resolves to.</summary>
public sealed record ResolvedImageModel(
    int ProviderId,
    string ProviderName,
    string ProviderLabel,
    ImageProviderKind Kind,
    bool IsDefaultProvider,
    string ModelId,
    string ModelLabel,
    bool IsDefaultModel,
    ImageModelCapabilities Capabilities,
    ImageRasterConstraints Constraints)
{
    public static ResolvedImageModel From(ImageProvider provider, ImageProviderModel model) => new(
        provider.Id,
        provider.Name,
        provider.DisplayName ?? provider.Name,
        provider.Kind,
        provider.IsDefault,
        model.ModelId,
        model.DisplayName ?? model.ModelId,
        model.IsDefault,
        ImageModelCapabilities.From(model),
        ImageRasterConstraints.FromJson(model.RasterConstraintsJson));
}

public sealed record ImageModelCapabilities(
    IReadOnlyList<string> Qualities,
    IReadOnlyList<string> OutputFormats,
    bool Generate,
    bool Edit,
    bool RegionalGuide,
    int MaxReferenceImages,
    bool TransparentBackground,
    bool Streaming)
{
    public static ImageModelCapabilities From(ImageProviderModel model) => new(
        ReadStringArray(model.QualitiesJson, "auto"),
        ReadStringArray(model.OutputFormatsJson, "png"),
        model.SupportsGenerate,
        model.SupportsEdit,
        model.SupportsRegionalGuide,
        Math.Max(0, model.MaxReferenceImages),
        model.SupportsTransparentBackground,
        model.SupportsStreaming);

    public static string ToJson(IEnumerable<string> values) =>
        JsonSerializer.Serialize(values.Select(value => value.Trim().ToLowerInvariant()).Where(value => value.Length > 0).Distinct());

    private static IReadOnlyList<string> ReadStringArray(string? json, string fallback)
    {
        try
        {
            var values = JsonSerializer.Deserialize<List<string>>(json ?? "[]") ?? [];
            var normalized = values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim().ToLowerInvariant())
                .Distinct()
                .ToList();
            return normalized.Count > 0 ? normalized : [fallback];
        }
        catch (JsonException)
        {
            return [fallback];
        }
    }
}

public sealed record DiscoveredImageModel(
    string ModelId,
    string? DisplayName,
    ImageModelCapabilities Capabilities,
    ImageRasterConstraints Constraints,
    bool Bundled = false);

public sealed record ImageProviderTestResult(bool Success, string Message);

/// <summary>Whether image work can start, and the default it would use.</summary>
public sealed record ImageProviderReadiness(bool IsReady, string Message, ResolvedImageModel? Default);

public sealed class ImageProviderNotConfiguredException(string message = ImageProviderNotConfiguredException.DefaultMessage)
    : InvalidOperationException(message)
{
    public const string DefaultMessage = "No image provider configured — Settings > Image providers.";
}
