namespace Lorekeeper.Composition;

public sealed record LayoutImageSize(int Width, int Height)
{
    public string Size => $"{Width}x{Height}";
    public long PixelCount => (long)Width * Height;
}

public static class LayoutImageSizeResolver
{
    public const int SizeMultiple = 16;
    public const int MinimumPixels = 655_360;
    public const int MaximumPixels = 8_294_400;
    public const int MaximumEdge = 3840;
    public const int TargetPixels = 1_572_864;
    public const double MaximumAspectRatio = 3d;

    public const double PreferredAspectError = .001d;
    private const double ExactAspectError = 1e-10;

    public static LayoutImageSize Resolve(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            throw new ArgumentException("Image target dimensions must be finite positive values.");

        return ResolveAspect(width / height);
    }

    public static bool AspectMatches(double actualAspect, double targetAspect) =>
        double.IsFinite(actualAspect)
        && double.IsFinite(targetAspect)
        && actualAspect > 0
        && targetAspect > 0
        && Math.Abs(Math.Log(actualAspect / targetAspect)) <= PreferredAspectError;

    public static LayoutImageSize ResolveAspect(double aspect)
    {
        if (!double.IsFinite(aspect)
            || aspect is < (1d / MaximumAspectRatio) or > MaximumAspectRatio)
        {
            throw new ArgumentException("Image aspect ratio must be between 1:3 and 3:1.", nameof(aspect));
        }

        var candidates = new List<Candidate>();
        for (var width = SizeMultiple; width <= MaximumEdge; width += SizeMultiple)
        {
            var idealHeight = width / aspect;
            var nearestHeight = (int)Math.Round(idealHeight / SizeMultiple) * SizeMultiple;
            for (var offset = -1; offset <= 1; offset++)
            {
                var height = nearestHeight + offset * SizeMultiple;
                if (height is <= 0 or > MaximumEdge)
                    continue;

                var pixels = (long)width * height;
                if (pixels is < MinimumPixels or > MaximumPixels)
                    continue;
                var actualAspect = (double)width / height;
                if (actualAspect is < (1d / MaximumAspectRatio) or > MaximumAspectRatio)
                    continue;

                candidates.Add(new Candidate(
                    width,
                    height,
                    Math.Abs(Math.Log(actualAspect / aspect)),
                    Math.Abs(pixels - TargetPixels)));
            }
        }

        if (candidates.Count == 0)
            throw new ArgumentException("Could not derive a valid gpt-image-2 raster size for the requested aspect ratio.", nameof(aspect));

        var exact = candidates.Where(candidate => candidate.AspectError <= ExactAspectError).ToList();
        var preferred = candidates.Where(candidate => candidate.AspectError <= PreferredAspectError).ToList();
        var pool = exact.Count > 0 ? exact : preferred.Count > 0 ? preferred : candidates;
        var selected = pool
            .OrderBy(candidate => exact.Count > 0 || preferred.Count > 0 ? candidate.AreaDistance : candidate.AspectError)
            .ThenBy(candidate => exact.Count > 0 || preferred.Count > 0 ? candidate.AspectError : candidate.AreaDistance)
            .ThenBy(candidate => candidate.Width)
            .ThenBy(candidate => candidate.Height)
            .First();
        return new LayoutImageSize(selected.Width, selected.Height);
    }

    public static LayoutImageSize Parse(string size)
    {
        var parts = size.Trim().ToLowerInvariant().Split('x', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var width) || !int.TryParse(parts[1], out var height))
            throw new ArgumentException("Image size must be WIDTHxHEIGHT.", nameof(size));
        Validate(width, height);
        return new LayoutImageSize(width, height);
    }

    public static bool TryParse(string? size, out LayoutImageSize result)
    {
        try
        {
            result = Parse(size ?? string.Empty);
            return true;
        }
        catch (ArgumentException)
        {
            result = new LayoutImageSize(0, 0);
            return false;
        }
    }

    public static void Validate(int width, int height)
    {
        if (width <= 0 || height <= 0 || width % SizeMultiple != 0 || height % SizeMultiple != 0)
            throw new ArgumentException($"Image width and height must be positive and divisible by {SizeMultiple}.");
        if (width > MaximumEdge || height > MaximumEdge)
            throw new ArgumentException($"Image edges cannot exceed {MaximumEdge} pixels.");
        var pixels = (long)width * height;
        if (pixels is < MinimumPixels or > MaximumPixels)
            throw new ArgumentException($"Image size must contain {MinimumPixels:N0}-{MaximumPixels:N0} pixels.");
        var aspect = (double)width / height;
        if (aspect is < (1d / MaximumAspectRatio) or > MaximumAspectRatio)
            throw new ArgumentException("Image aspect ratio must be between 1:3 and 3:1.");
    }

    private sealed record Candidate(
        int Width,
        int Height,
        double AspectError,
        long AreaDistance);
}
