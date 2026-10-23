using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lorekeeper.Composition;

/// <summary>
/// The output rasters an image model accepts: edges aligned to
/// <see cref="SizeMultiple"/>, a pixel-count window, an edge cap, and an aspect
/// cap. <see cref="TargetPixels"/> is the size preferred when only an aspect is known.
/// An aspect-tier model (one that takes an aspect ratio and a resolution tier
/// instead of pixels) also lists <see cref="AspectRatios"/> and
/// <see cref="ResolutionTiers"/>; its pixel limits are then an approximation
/// used for planning, and the adapter maps each planned raster to the nearest
/// ratio and tier. A fixed-budget model (<see cref="FixedPixelBudget"/>)
/// renders the aspect named in the prompt at about <see cref="TargetPixels"/>
/// with unaligned edges; its narrow pixel window keeps planned rasters near
/// that budget, and an explicit size outside it is rescaled to the budget at
/// the same aspect. The decoded output stays authoritative.
/// </summary>
public sealed record ImageRasterConstraints(
    int SizeMultiple,
    long MinimumPixels,
    long MaximumPixels,
    int MaximumEdge,
    double MaximumAspectRatio,
    long TargetPixels,
    IReadOnlyList<string>? AspectRatios = null,
    IReadOnlyList<string>? ResolutionTiers = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool FixedPixelBudget = false)
{
    /// <summary>The aspect ratios assumed when a model accepts aspect_ratio without listing values.</summary>
    public static IReadOnlyList<string> DocumentedAspectRatios { get; } = Array.AsReadOnly(new[]
    {
        "1:1", "16:9", "9:16", "4:3", "3:4", "3:2", "2:3", "4:5", "5:4", "1:2", "2:1", "21:9", "9:21",
    });

    /// <summary>The GPT Image size rules.</summary>
    public static ImageRasterConstraints GptImage { get; } = new(16, 655_360, 8_294_400, 3840, 3d, 1_572_864);

    /// <summary>
    /// The ChatGPT (Codex) image endpoints: the size field is ignored and every
    /// output holds about 1.57 MP at the aspect stated in the prompt.
    /// </summary>
    public static ImageRasterConstraints CodexImage { get; } = new(16, 1_525_000, 1_620_000, 2208, 3d, 1_572_864, FixedPixelBudget: true);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [JsonIgnore]
    public bool UsesAspectTiers => AspectRatios is { Count: > 0 } || ResolutionTiers is { Count: > 0 };

    [JsonIgnore]
    public double MinimumAspectRatio => 1d / MaximumAspectRatio;

    [JsonIgnore]
    public string AspectRangeLabel => $"1:{FormatRatio(MaximumAspectRatio)} and {FormatRatio(MaximumAspectRatio)}:1";

    public bool AspectInRange(double aspect) =>
        double.IsFinite(aspect) && aspect >= MinimumAspectRatio && aspect <= MaximumAspectRatio;

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>The limits as assistant tools report them.</summary>
    public object ToToolPayload() => new
    {
        rasterFormat = "WIDTHxHEIGHT",
        sizeMultiple = SizeMultiple,
        minimumPixels = MinimumPixels,
        maximumPixels = MaximumPixels,
        maximumEdgePixels = MaximumEdge,
        minimumAspectRatio = MinimumAspectRatio,
        maximumAspectRatio = MaximumAspectRatio,
        aspectRatios = AspectRatios,
        resolutionTiers = ResolutionTiers,
        approximate = UsesAspectTiers
            ? "This model takes an aspect ratio and resolution tier; the delivered raster may differ from the requested size and is reported after generation."
            : FixedPixelBudget
                ? "This model renders the requested aspect at a fixed pixel budget near targetPixels; the delivered edges differ slightly from the requested size, and other sizes are rescaled to the budget."
                : null,
        targetPixels = TargetPixels,
    };

    /// <summary>
    /// Pixel-planning limits for an aspect-tier model. An empty aspect list means
    /// every documented ratio; an empty tier list means the 1K tier. Tier names
    /// are square-equivalent edges: 512, 768, 1K = 1024, 1.5K = 1536, 2K, 4K.
    /// </summary>
    public static ImageRasterConstraints FromAspectTiers(IEnumerable<string> aspectRatios, IEnumerable<string> resolutionTiers)
    {
        var aspects = aspectRatios
            .Select(value => value.Trim())
            .Where(value => ParseAspect(value) is not null)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (aspects.Count == 0)
            aspects = [.. DocumentedAspectRatios];
        var tiers = resolutionTiers
            .Select(value => value.Trim())
            .Where(value => TierEdge(value) is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => TierEdge(value))
            .ToList();
        if (tiers.Count == 0)
            tiers = ["1K"];

        var edges = tiers.Select(value => (long)TierEdge(value)!.Value).ToList();
        var maximumAspect = Math.Max(1d, aspects.Max(value => Math.Max(ParseAspect(value)!.Value, 1d / ParseAspect(value)!.Value)));
        var maximumPixels = edges.Max() * edges.Max();
        var minimumPixels = Math.Max(1, edges.Min() * edges.Min() / 2);
        var maximumEdge = (int)Math.Min(16_384, RoundUp(Math.Sqrt(maximumPixels * maximumAspect), 16));
        var targetPixels = Math.Clamp(1024L * 1024, minimumPixels, maximumPixels);
        return new ImageRasterConstraints(16, minimumPixels, maximumPixels, maximumEdge, maximumAspect, targetPixels, aspects, tiers)
            .Validated();
    }

    /// <summary>
    /// The listed ratio nearest the raster's aspect (in log space) and the
    /// smallest tier holding its pixel count, else the largest tier. Either is
    /// null when this model does not list that parameter.
    /// </summary>
    public (string? AspectRatio, string? Resolution) MapToAspectTier(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException("Image dimensions must be positive.");
        var aspect = (double)width / height;
        var ratio = AspectRatios is { Count: > 0 }
            ? AspectRatios
                .Where(value => ParseAspect(value) is not null)
                .OrderBy(value => Math.Abs(Math.Log(ParseAspect(value)!.Value / aspect)))
                .FirstOrDefault()
            : null;
        string? tier = null;
        if (ResolutionTiers is { Count: > 0 })
        {
            var pixels = (long)width * height;
            var ordered = ResolutionTiers
                .Where(value => TierEdge(value) is not null)
                .OrderBy(value => TierEdge(value))
                .ToList();
            tier = ordered.FirstOrDefault(value => (long)TierEdge(value)!.Value * TierEdge(value)!.Value >= pixels * 9 / 10)
                ?? ordered.LastOrDefault();
        }

        return (ratio, tier);
    }

    private static double? ParseAspect(string value)
    {
        var parts = value.Split(':');
        return parts.Length == 2
            && double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var width)
            && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var height)
            && width > 0 && height > 0 && double.IsFinite(width / height)
                ? width / height
                : null;
    }

    private static int? TierEdge(string value)
    {
        var normalized = value.Trim().ToUpperInvariant();
        var thousands = normalized.EndsWith('K');
        var number = thousands ? normalized[..^1] : normalized;
        if (!double.TryParse(number, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            || parsed <= 0)
            return null;
        var edge = thousands ? parsed * 1024 : parsed;
        return edge is >= 64 and <= 16_384 ? (int)Math.Round(edge) : null;
    }

    private static long RoundUp(double value, int multiple) =>
        (long)Math.Ceiling(value / multiple) * multiple;

    public static ImageRasterConstraints FromJson(string? json)
    {
        ImageRasterConstraints? parsed;
        try
        {
            parsed = string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<ImageRasterConstraints>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Image model raster limits are not valid JSON.", nameof(json), ex);
        }

        return (parsed ?? throw new ArgumentException("Image model raster limits are missing.", nameof(json))).Validated();
    }

    /// <summary>Returns this instance after checking that the limits describe at least one usable raster.</summary>
    public ImageRasterConstraints Validated()
    {
        if (SizeMultiple is < 1 or > 512)
            throw new ArgumentException("Raster size multiple must be between 1 and 512.");
        if (MaximumEdge < SizeMultiple || MaximumEdge > 16_384)
            throw new ArgumentException("Raster maximum edge must be between the size multiple and 16,384 pixels.");
        if (MinimumPixels < 1 || MaximumPixels < MinimumPixels || MaximumPixels > (long)MaximumEdge * MaximumEdge)
            throw new ArgumentException("Raster pixel limits must be positive, ordered, and reachable within the maximum edge.");
        if (!double.IsFinite(MaximumAspectRatio) || MaximumAspectRatio < 1 || MaximumAspectRatio > 32)
            throw new ArgumentException("Raster maximum aspect ratio must be between 1 and 32.");
        if (TargetPixels < MinimumPixels || TargetPixels > MaximumPixels)
            throw new ArgumentException("Raster target pixels must fall within the pixel limits.");
        if (AspectRatios is not null && AspectRatios.Any(value => ParseAspect(value) is null))
            throw new ArgumentException("Raster aspect ratios must look like 16:9.");
        if (ResolutionTiers is not null && ResolutionTiers.Any(value => TierEdge(value) is null))
            throw new ArgumentException("Raster resolution tiers must look like 1K, 2K, or 768.");
        return this;
    }

    private static string FormatRatio(double value) =>
        value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
