using System.Text.Json;
using Lorekeeper.Composition;

namespace Lorekeeper.Images;

public static class ProjectImageRegionalGuide
{
    public const string PromptInstruction =
        "Treat the supplied alpha mask as approximate visual guidance for the location of the requested change, not as a hard pixel boundary. Focus the edit in the transparent region and preserve unmentioned content outside it as closely as possible. Minor boundary or surrounding adjustments may occur when they are needed for one coherent complete image. Preserve the source framing, do not broadly restyle or recompose the image, and inspect the complete result rather than assuming pixels outside the guide are unchanged.";

    public static LayoutImageSize ResolveOutputSize(
        ImageRasterConstraints constraints,
        int sourceWidth,
        int sourceHeight,
        string? requestedSize)
    {
        ArgumentNullException.ThrowIfNull(constraints);
        if (sourceWidth <= 0 || sourceHeight <= 0)
            throw new InvalidOperationException("Regional-guide source dimensions are invalid.");

        var sourceAspect = (double)sourceWidth / sourceHeight;
        if (!constraints.AspectInRange(sourceAspect))
            throw new InvalidOperationException($"Regional-guide edits require a source aspect ratio between {constraints.AspectRangeLabel}.");

        var normalized = requestedSize?.Trim() ?? string.Empty;
        var output = normalized.Length == 0 || normalized.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? LayoutImageSizeResolver.ResolveNearest(constraints, sourceWidth, sourceHeight)
            : LayoutImageSizeResolver.ParseValid(constraints, normalized);

        if (!LayoutImageSizeResolver.AspectMatches((double)output.Width / output.Height, sourceAspect))
        {
            throw new InvalidOperationException(
                $"Regional-guide output size {output.Size} must preserve the source image aspect ratio {sourceWidth}:{sourceHeight}. Use an unmasked edit for reframing or layout changes.");
        }

        return output;
    }

    public static void ValidateOutputSize(
        ImageRasterConstraints constraints,
        int sourceWidth,
        int sourceHeight,
        string requestedSize) =>
        _ = ResolveOutputSize(constraints, sourceWidth, sourceHeight, requestedSize);

    public static void ValidateTargetGeometry(
        string? value,
        int sourceWidth,
        int sourceHeight,
        string requestedSize)
    {
        const string error = "Regional-guide edits must use source-bound, non-layout target geometry compiled by Lorekeeper, without reserved regions.";
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(value ?? string.Empty);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(error, ex);
        }

        var expectedSourceRaster = $"{sourceWidth}x{sourceHeight}";
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("regionalGuide", out var regionalGuide)
            || regionalGuide.ValueKind != JsonValueKind.True
            || !root.TryGetProperty("layoutBound", out var layoutBound)
            || layoutBound.ValueKind != JsonValueKind.False
            || !root.TryGetProperty("sourceRaster", out var sourceRaster)
            || sourceRaster.ValueKind != JsonValueKind.String
            || !string.Equals(sourceRaster.GetString(), expectedSourceRaster, StringComparison.OrdinalIgnoreCase)
            || !root.TryGetProperty("size", out var size)
            || size.ValueKind != JsonValueKind.String
            || !string.Equals(size.GetString(), requestedSize.Trim(), StringComparison.OrdinalIgnoreCase)
            || !root.TryGetProperty("reservedTextRegions", out var regions)
            || regions.ValueKind != JsonValueKind.Array
            || regions.GetArrayLength() != 0)
        {
            throw new InvalidOperationException(error);
        }
    }
}
