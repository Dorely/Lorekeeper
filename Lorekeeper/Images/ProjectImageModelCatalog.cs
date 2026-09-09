namespace Lorekeeper.Images;

public sealed record ProjectImageModelDefinition(string Id, string Label, IReadOnlyList<string> Qualities);

public sealed record ProjectImageRequestSettings(
    string ImageModel,
    string Quality,
    string OutputFormat,
    int? OutputCompression,
    string Background);

public static class ProjectImageModelCatalog
{
    public const string DefaultModel = "gpt-image-2.5-flare";
    public const string ToolParameterGuidance = " Optional imageModel selects gpt-image-2.5-flare (default), gpt-image-2.5-sunburst, or an existing gpt-image-2 selection. Quality defaults to auto; use low, medium, high, xhigh, or max (GPT Image 2 supports up to high). Background is auto, opaque, or transparent; transparent requires PNG or WebP, never JPEG. Output compression is 0–100 for JPEG/WebP only; layout-bound output is PNG. Inspect warningCodes and the complete image before accepting or placing it; an unmet transparency requirement leaves the output unattached.";
    private static readonly IReadOnlyList<string> Image2Qualities = Array.AsReadOnly(new[] { "auto", "low", "medium", "high" });
    private static readonly IReadOnlyList<string> Image25Qualities = Array.AsReadOnly(new[] { "auto", "low", "medium", "high", "xhigh", "max" });

    public static IReadOnlyList<ProjectImageModelDefinition> Models { get; } = Array.AsReadOnly(new[]
    {
        new ProjectImageModelDefinition(DefaultModel, "GPT Image 2.5 Flare", Image25Qualities),
        new ProjectImageModelDefinition("gpt-image-2.5-sunburst", "GPT Image 2.5 Sunburst", Image25Qualities),
        new ProjectImageModelDefinition("gpt-image-2", "GPT Image 2", Image2Qualities),
        new ProjectImageModelDefinition("gpt-image-2.5-flare-2026-09-08", "GPT Image 2.5 Flare (2026-09-08)", Image25Qualities),
        new ProjectImageModelDefinition("gpt-image-2.5-sunburst-2026-09-08", "GPT Image 2.5 Sunburst (2026-09-08)", Image25Qualities),
        new ProjectImageModelDefinition("gpt-image-2-2026-04-21", "GPT Image 2 (2026-04-21)", Image2Qualities),
    });

    public static ProjectImageRequestSettings Resolve(
        string? imageModel,
        string? quality,
        string? outputFormat,
        int? outputCompression,
        string? background,
        ProjectImageGenerationOptions defaults,
        bool layoutBound = false)
    {
        var modelId = string.IsNullOrWhiteSpace(imageModel) ? defaults.DefaultImageModel.Trim() : imageModel.Trim();
        var model = Models.FirstOrDefault(candidate => candidate.Id == modelId)
            ?? throw new ArgumentException($"Unsupported image model '{modelId}'. Choose a supported GPT Image model.", nameof(imageModel));
        var effectiveQuality = Normalize(quality, defaults.DefaultQuality);
        if (!model.Qualities.Contains(effectiveQuality))
            throw new ArgumentException($"Quality '{effectiveQuality}' is not supported by {model.Label}.", nameof(quality));

        var format = Normalize(outputFormat, defaults.DefaultOutputFormat);
        if (format == "jpg") format = "jpeg";
        if (format is not ("png" or "jpeg" or "webp"))
            throw new ArgumentException("Image format must be PNG, JPEG, or WebP.", nameof(outputFormat));
        var effectiveBackground = NormalizeBackground(background);
        if (effectiveBackground == "transparent" && format == "jpeg")
            throw new ArgumentException("Transparent backgrounds require PNG or WebP output.", nameof(outputFormat));
        if (outputCompression is < 0 or > 100)
            throw new ArgumentException("Image compression must be between 0 and 100.", nameof(outputCompression));

        return new ProjectImageRequestSettings(modelId, effectiveQuality,
            layoutBound ? "png" : format,
            layoutBound || format == "png" ? null : outputCompression,
            effectiveBackground);
    }

    public static string NormalizeBackground(string? background)
    {
        var value = Normalize(background, "auto");
        return value is "auto" or "opaque" or "transparent"
            ? value
            : throw new ArgumentException("Image background must be Auto, Opaque, or Transparent.", nameof(background));
    }

    private static string Normalize(string? value, string fallback) =>
        (string.IsNullOrWhiteSpace(value) ? fallback : value).Trim().ToLowerInvariant();
}
