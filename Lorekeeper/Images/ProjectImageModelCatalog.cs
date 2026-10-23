using Lorekeeper.Composition;

namespace Lorekeeper.Images;

public sealed record ProjectImageRequestSettings(
    string ImageModel,
    string Quality,
    string OutputFormat,
    int? OutputCompression,
    string Background);

/// <summary>
/// The bundled GPT Image model sets and the validation of per-request image
/// settings against a resolved model's capabilities.
/// </summary>
public static class ProjectImageModelCatalog
{
    private static readonly string[] Image2Qualities = ["auto", "low", "medium", "high"];
    private static readonly string[] Image25Qualities = ["auto", "low", "medium", "high", "xhigh", "max"];
    private static readonly string[] GptImageFormats = ["png", "jpeg", "webp"];
    private static readonly string[] CodexQualities = ["auto"];
    private static readonly string[] CodexFormats = ["png", "jpeg"];

    /// <summary>GPT Image models served by the OpenAI Images API; the first is the default.</summary>
    public static IReadOnlyList<DiscoveredImageModel> GptImageModels { get; } = Array.AsReadOnly(new[]
    {
        GptImage("gpt-image-2.5-flare", "GPT Image 2.5 Flare", Image25Qualities),
        GptImage("gpt-image-2.5-sunburst", "GPT Image 2.5 Sunburst", Image25Qualities),
        GptImage("gpt-image-2", "GPT Image 2", Image2Qualities),
        GptImage("gpt-image-2.5-flare-2026-09-08", "GPT Image 2.5 Flare (2026-09-08)", Image25Qualities),
        GptImage("gpt-image-2.5-sunburst-2026-09-08", "GPT Image 2.5 Sunburst (2026-09-08)", Image25Qualities),
        GptImage("gpt-image-2-2026-04-21", "GPT Image 2 (2026-04-21)", Image2Qualities),
    });

    /// <summary>
    /// The same GPT Image models as the ChatGPT (Codex) image endpoints serve
    /// them; the first is the default. The backend picks quality, ignores size,
    /// and returns PNG, which Lorekeeper converts when JPEG is requested.
    /// </summary>
    public static IReadOnlyList<DiscoveredImageModel> CodexImageModels { get; } = Array.AsReadOnly(GptImageModels
        .Select(model => model with
        {
            Capabilities = model.Capabilities with
            {
                Qualities = CodexQualities,
                OutputFormats = CodexFormats,
                Streaming = false,
            },
            Constraints = ImageRasterConstraints.CodexImage,
        })
        .ToArray());

    public static ProjectImageRequestSettings Resolve(
        ResolvedImageModel model,
        string? quality,
        string? outputFormat,
        int? outputCompression,
        string? background,
        ProjectImageGenerationOptions defaults,
        bool layoutBound = false)
    {
        ArgumentNullException.ThrowIfNull(model);
        var capabilities = model.Capabilities;
        var defaultQuality = capabilities.Qualities.Contains(Normalize(defaults.DefaultQuality, "auto"))
            ? defaults.DefaultQuality
            : capabilities.Qualities[0];
        var effectiveQuality = Normalize(quality, defaultQuality);
        if (!capabilities.Qualities.Contains(effectiveQuality))
        {
            throw new ArgumentException(
                $"Quality '{effectiveQuality}' is not supported by {model.ModelLabel}. Use {string.Join(", ", capabilities.Qualities)}.",
                nameof(quality));
        }

        var defaultFormat = capabilities.OutputFormats.Contains(Normalize(defaults.DefaultOutputFormat, "png"))
            ? defaults.DefaultOutputFormat
            : capabilities.OutputFormats[0];
        var format = Normalize(outputFormat, defaultFormat);
        if (format == "jpg") format = "jpeg";
        if (format is not ("png" or "jpeg" or "webp"))
            throw new ArgumentException("Image format must be PNG, JPEG, or WebP.", nameof(outputFormat));
        if (layoutBound && !capabilities.OutputFormats.Contains("png"))
            throw new ArgumentException($"{model.ModelLabel} cannot produce PNG, which layout-bound images require.", nameof(outputFormat));
        if (!layoutBound && !capabilities.OutputFormats.Contains(format))
        {
            throw new ArgumentException(
                $"{model.ModelLabel} does not produce {format.ToUpperInvariant()} output. Use {string.Join(", ", capabilities.OutputFormats)}.",
                nameof(outputFormat));
        }

        var effectiveBackground = NormalizeBackground(background);
        if (effectiveBackground == "transparent" && !capabilities.TransparentBackground)
            throw new ArgumentException($"{model.ModelLabel} does not support transparent backgrounds.", nameof(background));
        if (effectiveBackground == "transparent" && format == "jpeg")
            throw new ArgumentException("Transparent backgrounds require PNG or WebP output.", nameof(outputFormat));
        if (outputCompression is < 0 or > 100)
            throw new ArgumentException("Image compression must be between 0 and 100.", nameof(outputCompression));

        return new ProjectImageRequestSettings(model.ModelId, effectiveQuality,
            layoutBound ? "png" : format,
            layoutBound || format == "png" ? null : outputCompression,
            effectiveBackground);
    }

    /// <summary>Rejects references and edits the model cannot accept, before a job exists.</summary>
    public static void ValidateInputs(
        ResolvedImageModel model,
        bool edit,
        int referenceCount,
        int applicationReferenceLimit)
    {
        ArgumentNullException.ThrowIfNull(model);
        var capabilities = model.Capabilities;
        if (!edit && !capabilities.Generate)
            throw new ArgumentException($"{model.ModelLabel} on {model.ProviderLabel} does not generate new images.");
        if (edit && !capabilities.Edit)
            throw new ArgumentException($"{model.ModelLabel} on {model.ProviderLabel} does not edit images.");
        var limit = ReferenceLimit(model, applicationReferenceLimit);
        if (referenceCount > limit)
            throw new ArgumentException($"{model.ModelLabel} on {model.ProviderLabel} accepts at most {limit} reference image(s).");
    }

    /// <summary>The reference cap for one request: the model's limit, bounded by the application setting.</summary>
    public static int ReferenceLimit(ResolvedImageModel model, int applicationReferenceLimit) =>
        Math.Min(Math.Max(0, applicationReferenceLimit), model.Capabilities.MaxReferenceImages);

    public static string NormalizeBackground(string? background)
    {
        var value = Normalize(background, "auto");
        return value is "auto" or "opaque" or "transparent"
            ? value
            : throw new ArgumentException("Image background must be Auto, Opaque, or Transparent.", nameof(background));
    }

    /// <summary>Assistant guidance for the given ready models, default first.</summary>
    public static string BuildToolGuidance(IReadOnlyList<ResolvedImageModel> models)
    {
        if (models.Count == 0)
            return " " + ImageProviderNotConfiguredException.DefaultMessage + " Image generation and editing fail until one is configured.";

        var defaultModel = models[0];
        var providerModels = models.Where(model => model.ProviderId == defaultModel.ProviderId).ToList();
        var modelList = string.Join(", ", providerModels.Select(model =>
            $"{model.ModelId} (quality {string.Join("/", model.Capabilities.Qualities)}{(model.IsDefaultModel ? "; default" : string.Empty)})"));
        var capabilities = defaultModel.Capabilities;
        var limits = new List<string>();
        if (!capabilities.Edit) limits.Add("no edits");
        if (!capabilities.TransparentBackground) limits.Add("no transparent backgrounds");
        limits.Add($"at most {capabilities.MaxReferenceImages} reference image(s)");
        return $" Images use {defaultModel.ProviderLabel}. Optional imageModel selects one of: {modelList}; omit it for {defaultModel.ModelId}."
            + $" The default model allows {string.Join(", ", limits)}."
            + " Background is auto, opaque, or transparent; transparent requires PNG or WebP, never JPEG. Output compression is 0-100 for JPEG/WebP only; layout-bound output is PNG."
            + " Inspect warningCodes and the complete image before accepting or placing it; an unmet transparency requirement leaves the output unattached.";
    }

    private static DiscoveredImageModel GptImage(string id, string label, string[] qualities) => new(
        id,
        label,
        new ImageModelCapabilities(
            qualities,
            GptImageFormats,
            Generate: true,
            Edit: true,
            MaxReferenceImages: 16,
            TransparentBackground: true,
            Streaming: true),
        ImageRasterConstraints.GptImage,
        Bundled: true);

    private static string Normalize(string? value, string fallback) =>
        (string.IsNullOrWhiteSpace(value) ? fallback : value).Trim().ToLowerInvariant();
}
