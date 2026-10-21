using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lorekeeper.Composition;

/// <summary>
/// The output rasters an image model accepts: edges aligned to
/// <see cref="SizeMultiple"/>, a pixel-count window, an edge cap, and an aspect
/// cap. <see cref="TargetPixels"/> is the size preferred when only an aspect is known.
/// </summary>
public sealed record ImageRasterConstraints(
    int SizeMultiple,
    long MinimumPixels,
    long MaximumPixels,
    int MaximumEdge,
    double MaximumAspectRatio,
    long TargetPixels)
{
    /// <summary>The GPT Image size rules.</summary>
    public static ImageRasterConstraints GptImage { get; } = new(16, 655_360, 8_294_400, 3840, 3d, 1_572_864);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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
    };

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
        return this;
    }

    private static string FormatRatio(double value) =>
        value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
