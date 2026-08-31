using Lorekeeper.Manuscripts;

namespace Lorekeeper.Composition;

public sealed record LayoutImageSize(int Width, int Height)
{
    public string Size => $"{Width}x{Height}";
    public long PixelCount => (long)Width * Height;
}

public sealed record LayoutImageRequiredRaster(long Width, long Height)
{
    public string Size => $"{Width}x{Height}";
}

public sealed record LayoutImageDpiResolution(
    double WidthInches,
    double HeightInches,
    double MinimumDpi,
    LayoutImageRequiredRaster RequiredRaster,
    LayoutImageSize? Raster,
    double MaximumAchievableDpi,
    LayoutImageSize? MaximumRaster,
    IReadOnlyList<string> BindingProviderLimits,
    IReadOnlyList<LayoutImagePanelSuggestion> PanelSuggestions,
    LayoutPrintUpscalePlan? PrintUpscalePlan = null)
{
    public bool MeetsMinimumDpi => Raster is not null;
}

public sealed record LayoutPrintUpscalePlan(
    LayoutImageSize NativeRaster,
    double NativeEffectiveDpi,
    LayoutImageSize PrintRaster,
    double TargetDpi);

public sealed record LayoutImagePanelSuggestion(
    int Rows,
    int Columns,
    IReadOnlyList<LayoutImagePanel> Panels,
    double MaximumAchievableDpi)
{
    public int PanelCount => Rows * Columns;
}

public sealed record LayoutImagePanel(
    int Index,
    double XPercent,
    double YPercent,
    double WidthPercent,
    double HeightPercent,
    double WidthInches,
    double HeightInches,
    string AspectRatio,
    LayoutImageSize Raster,
    double EffectiveDpi);

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
    private const double RasterIntegerTolerance = 1e-9;
    private const int MaximumSuggestedPanels = 64;

    public const int PrintMaximumEdge = 12_000;
    public const long PrintMaximumPixels = 120_000_000;

    public static (double WidthInches, double HeightInches) ResolveFlowingFigurePhysicalSize(
        double pageWidthInches,
        double pageHeightInches,
        double pageMarginInches,
        FigurePresentation presentation,
        double bleedInches = 0)
    {
        var contentWidth = Math.Max(.25, pageWidthInches - pageMarginInches * 2);
        var contentHeight = Math.Max(.25, pageHeightInches - pageMarginInches * 2);
        if (!double.IsFinite(presentation.WidthPercent))
            throw new ArgumentException("Figure width must be finite.", nameof(presentation));
        var width = presentation.Placement switch
        {
            FigurePlacementIntent.FullBleed => pageWidthInches + Math.Max(0, bleedInches) * 2,
            FigurePlacementIntent.FullWidth => contentWidth,
            _ => contentWidth * Math.Clamp(presentation.WidthPercent, 10, 100) / 100,
        };
        var height = presentation.Placement switch
        {
            FigurePlacementIntent.FullBleed => pageHeightInches + Math.Max(0, bleedInches) * 2,
            FigurePlacementIntent.DedicatedPage => contentHeight * .75,
            _ => Math.Min(contentHeight * .34, width * 1.25),
        };
        return (width, height);
    }

    public static LayoutImageSize Resolve(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            throw new ArgumentException("Image target dimensions must be finite positive values.");

        return ResolveAspect(width / height);
    }

    public static LayoutImageSize ResolveNearest(double desiredWidthPixels, double desiredHeightPixels)
    {
        if (!double.IsFinite(desiredWidthPixels)
            || !double.IsFinite(desiredHeightPixels)
            || desiredWidthPixels <= 0
            || desiredHeightPixels <= 0)
            throw new ArgumentException("Desired image pixels must be finite positive values.");

        var aspect = desiredWidthPixels / desiredHeightPixels;
        if (aspect is < (1d / MaximumAspectRatio) or > MaximumAspectRatio)
            throw new ArgumentException("Image aspect ratio must be between 1:3 and 3:1.", nameof(desiredWidthPixels));

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
                var aspectError = Math.Abs(Math.Log(actualAspect / aspect));
                if (aspectError > PreferredAspectError)
                    continue;

                var distance = (long)Math.Round(
                    Math.Pow(width - desiredWidthPixels, 2)
                    + Math.Pow(height - desiredHeightPixels, 2));
                candidates.Add(new Candidate(width, height, aspectError, distance));
            }
        }

        if (candidates.Count == 0)
            throw new ArgumentException("Could not derive a provider-valid raster near the requested pixel dimensions.");

        var selected = candidates
            .OrderBy(candidate => candidate.AreaDistance)
            .ThenBy(candidate => candidate.AspectError)
            .ThenBy(candidate => candidate.Width)
            .ThenBy(candidate => candidate.Height)
            .First();
        return new LayoutImageSize(selected.Width, selected.Height);
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

    public static LayoutImageDpiResolution ResolveMinimumDpi(
        double widthInches,
        double heightInches,
        double minimumDpi)
    {
        ValidatePhysicalDimensions(widthInches, heightInches);
        ValidateMinimumDpi(minimumDpi);

        var full = ResolveMinimumDpiCore(widthInches, heightInches, minimumDpi);
        if (full.Raster is not null)
            return full with { PanelSuggestions = [] };

        var suggestions = BuildPanelSuggestions(widthInches, heightInches, minimumDpi);
        return full with { PanelSuggestions = suggestions };
    }

    public static double EffectiveDpi(LayoutImageSize raster, double widthInches, double heightInches)
    {
        ValidatePhysicalDimensions(widthInches, heightInches);
        ArgumentNullException.ThrowIfNull(raster);
        Validate(raster.Width, raster.Height);
        return Math.Min(raster.Width / widthInches, raster.Height / heightInches);
    }

    public static LayoutImageSize ResolvePrintRaster(double widthInches, double heightInches, double dpi)
    {
        ValidatePhysicalDimensions(widthInches, heightInches);
        ValidateMinimumDpi(dpi);
        var width = CeilingRasterDimension(widthInches * dpi);
        var height = CeilingRasterDimension(heightInches * dpi);
        ValidatePrintRaster(width, height);
        return new LayoutImageSize(width, height);
    }

    public static void ValidatePrintRaster(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException("Print raster dimensions must be positive.", nameof(width));
        if (width > PrintMaximumEdge || height > PrintMaximumEdge)
            throw new ArgumentException($"Print raster edges cannot exceed {PrintMaximumEdge:N0} pixels.", nameof(width));
        if ((long)width * height > PrintMaximumPixels)
            throw new ArgumentException($"Print raster cannot exceed {PrintMaximumPixels:N0} pixels.", nameof(width));
    }

    public static LayoutPrintUpscalePlan CreatePrintUpscalePlan(
        double widthInches,
        double heightInches,
        double dpi,
        LayoutImageSize nativeRaster,
        double nativeEffectiveDpi) => new(
        nativeRaster,
        nativeEffectiveDpi,
        ResolvePrintRaster(widthInches, heightInches, dpi),
        dpi);

    internal static int CeilingRasterDimension(double value)
    {
        if (!double.IsFinite(value) || value <= 0 || value > int.MaxValue)
            throw new ArgumentException("Derived raster dimension must be a finite positive pixel value.");
        var nearestInteger = Math.Round(value);
        return nearestInteger >= 1 && Math.Abs(value - nearestInteger) <= RasterIntegerTolerance
            ? (int)nearestInteger
            : (int)Math.Ceiling(value);
    }

    public static IReadOnlyList<LayoutImagePanelSuggestion> MapPanelSuggestions(
        IReadOnlyList<LayoutImagePanelSuggestion> suggestions,
        double xPercent,
        double yPercent,
        double widthPercent,
        double heightPercent) => suggestions.Select(suggestion => suggestion with
        {
            Panels = suggestion.Panels.Select(panel => panel with
            {
                XPercent = xPercent + panel.XPercent / 100 * widthPercent,
                YPercent = yPercent + panel.YPercent / 100 * heightPercent,
                WidthPercent = panel.WidthPercent / 100 * widthPercent,
                HeightPercent = panel.HeightPercent / 100 * heightPercent,
            }).ToList(),
        }).ToList();

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

    public static void ValidatePhysicalDimensions(double widthInches, double heightInches)
    {
        if (!double.IsFinite(widthInches) || !double.IsFinite(heightInches)
            || widthInches <= 0 || heightInches <= 0)
            throw new ArgumentException("Physical image dimensions must be finite positive values.");
    }

    public static void ValidateMinimumDpi(double minimumDpi)
    {
        if (!double.IsFinite(minimumDpi) || minimumDpi <= 0)
            throw new ArgumentException("Minimum DPI must be a finite positive value.", nameof(minimumDpi));
    }

    private static LayoutImageDpiResolution ResolveMinimumDpiCore(
        double widthInches,
        double heightInches,
        double minimumDpi)
    {
        var aspect = widthInches / heightInches;
        var compatible = BuildAspectCompatibleSizes(aspect)
            .Select(size => new CandidateWithDpi(
                size,
                Math.Min(size.Width / widthInches, size.Height / heightInches),
                Math.Abs(Math.Log(((double)size.Width / size.Height) / aspect))))
            .ToList();
        var requiredRaster = new LayoutImageRequiredRaster(
            RoundUpToMultiple(widthInches * minimumDpi),
            RoundUpToMultiple(heightInches * minimumDpi));
        var maximum = compatible
            .OrderByDescending(candidate => candidate.EffectiveDpi)
            .ThenBy(candidate => candidate.AspectError)
            .ThenBy(candidate => candidate.Size.PixelCount)
            .ThenBy(candidate => candidate.Size.Width)
            .ThenBy(candidate => candidate.Size.Height)
            .FirstOrDefault();
        var raster = compatible
            .Where(candidate => candidate.EffectiveDpi + 1e-9 >= minimumDpi)
            .OrderBy(candidate => candidate.Size.PixelCount)
            .ThenBy(candidate => candidate.AspectError)
            .ThenBy(candidate => candidate.EffectiveDpi)
            .ThenBy(candidate => candidate.Size.Width)
            .ThenBy(candidate => candidate.Size.Height)
            .Select(candidate => candidate.Size)
            .FirstOrDefault();
        var bindingProviderLimits = ResolveBindingProviderLimits(aspect, requiredRaster, raster, maximum);
        LayoutPrintUpscalePlan? printUpscalePlan = null;
        if (raster is null && maximum is not null)
        {
            try
            {
                printUpscalePlan = CreatePrintUpscalePlan(
                    widthInches,
                    heightInches,
                    minimumDpi,
                    maximum.Size,
                    maximum.EffectiveDpi);
            }
            catch (ArgumentException)
            {
                printUpscalePlan = null;
            }
        }
        return new LayoutImageDpiResolution(
            widthInches,
            heightInches,
            minimumDpi,
            requiredRaster,
            raster,
            maximum?.EffectiveDpi ?? 0,
            maximum?.Size,
            bindingProviderLimits,
            [],
            printUpscalePlan);
    }

    private static IReadOnlyList<string> ResolveBindingProviderLimits(
        double aspect,
        LayoutImageRequiredRaster requiredRaster,
        LayoutImageSize? raster,
        CandidateWithDpi? maximum)
    {
        if (raster is not null)
            return [];

        var limits = new List<string>();
        if (aspect is < (1d / MaximumAspectRatio) or > MaximumAspectRatio)
            limits.Add("aspect_ratio");
        if (requiredRaster.Width > MaximumEdge || requiredRaster.Height > MaximumEdge)
            limits.Add("maximum_edge");
        if (requiredRaster.Width > MaximumPixels / requiredRaster.Height)
            limits.Add("maximum_pixels");
        if (limits.Count == 0 && maximum is null)
            limits.Add("size_alignment_or_aspect_tolerance");
        return limits;
    }

    private static IReadOnlyList<LayoutImagePanelSuggestion> BuildPanelSuggestions(
        double widthInches,
        double heightInches,
        double minimumDpi)
    {
        var suggestions = new List<LayoutImagePanelSuggestion>();
        for (var rows = 1; rows <= MaximumSuggestedPanels; rows++)
        {
            for (var columns = 1; columns <= MaximumSuggestedPanels / rows; columns++)
            {
                var panelWidth = widthInches / columns;
                var panelHeight = heightInches / rows;
                var panel = ResolveMinimumDpiCore(panelWidth, panelHeight, minimumDpi);
                if (panel.Raster is null)
                    continue;

                var panels = new List<LayoutImagePanel>(rows * columns);
                for (var row = 0; row < rows; row++)
                {
                    for (var column = 0; column < columns; column++)
                    {
                        var xPercent = column * 100d / columns;
                        var yPercent = row * 100d / rows;
                        panels.Add(new LayoutImagePanel(
                            row * columns + column,
                            xPercent,
                            yPercent,
                            100d / columns,
                            100d / rows,
                            panelWidth,
                            panelHeight,
                            AspectLabel(panelWidth, panelHeight),
                            panel.Raster,
                            EffectiveDpi(panel.Raster, panelWidth, panelHeight)));
                    }
                }
                suggestions.Add(new LayoutImagePanelSuggestion(rows, columns, panels, panel.MaximumAchievableDpi));
            }
        }

        return suggestions
            .OrderBy(suggestion => suggestion.PanelCount)
            .ThenBy(suggestion => suggestion.Rows > 1 && suggestion.Columns > 1 ? 1 : 0)
            .ThenBy(suggestion => widthInches >= heightInches
                ? suggestion.Columns >= suggestion.Rows ? 0 : 1
                : suggestion.Rows >= suggestion.Columns ? 0 : 1)
            .ThenByDescending(suggestion => suggestion.MaximumAchievableDpi)
            .ThenBy(suggestion => suggestion.Rows)
            .ThenBy(suggestion => suggestion.Columns)
            .Take(1)
            .ToList();
    }

    private static IReadOnlyList<LayoutImageSize> BuildAspectCompatibleSizes(double aspect)
    {
        var sizes = new List<LayoutImageSize>();
        for (var width = SizeMultiple; width <= MaximumEdge; width += SizeMultiple)
        {
            var nearestHeight = (int)Math.Round(width / aspect / SizeMultiple) * SizeMultiple;
            for (var offset = -1; offset <= 1; offset++)
            {
                var height = nearestHeight + offset * SizeMultiple;
                if (height is <= 0 or > MaximumEdge)
                    continue;
                var pixels = (long)width * height;
                if (pixels is < MinimumPixels or > MaximumPixels)
                    continue;
                var actualAspect = (double)width / height;
                if (actualAspect is < (1d / MaximumAspectRatio) or > MaximumAspectRatio
                    || !AspectMatches(actualAspect, aspect))
                    continue;
                sizes.Add(new LayoutImageSize(width, height));
            }
        }
        return sizes;
    }

    private static long RoundUpToMultiple(double value)
    {
        var multiples = Math.Ceiling(value / SizeMultiple);
        if (!double.IsFinite(multiples) || multiples > long.MaxValue / SizeMultiple)
            return long.MaxValue;
        return checked((long)multiples * SizeMultiple);
    }

    private static string AspectLabel(double width, double height)
    {
        var scaledWidth = (long)Math.Round(width * 1_000_000);
        var scaledHeight = (long)Math.Round(height * 1_000_000);
        var gcd = GreatestCommonDivisor(scaledWidth, scaledHeight);
        return $"{scaledWidth / gcd}:{scaledHeight / gcd}";
    }

    private static long GreatestCommonDivisor(long left, long right)
    {
        while (right != 0)
            (left, right) = (right, left % right);
        return Math.Max(left, 1);
    }

    private sealed record Candidate(
        int Width,
        int Height,
        double AspectError,
        long AreaDistance);

    private sealed record CandidateWithDpi(
        LayoutImageSize Size,
        double EffectiveDpi,
        double AspectError)
    {
        public double MaximumAchievableDpi => EffectiveDpi;
    }
}
