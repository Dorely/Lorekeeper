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
    [property: System.Text.Json.Serialization.JsonIgnore] ImageRasterConstraints Constraints,
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

/// <summary>
/// Derives provider-valid output rasters for physical targets. Every provider
/// rule (alignment, pixel window, edge and aspect caps) comes from the
/// <see cref="ImageRasterConstraints"/> of the image model that will run.
/// </summary>
public static class LayoutImageSizeResolver
{
    public const double PreferredAspectError = .001d;
    /// <summary>The pixel-count tolerance, in log space, of an approximate raster match (about 4%).</summary>
    public const double ApproximatePixelError = .04d;
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

    public static LayoutImageSize Resolve(ImageRasterConstraints constraints, double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            throw new ArgumentException("Image target dimensions must be finite positive values.");

        return ResolveAspect(constraints, width / height);
    }

    public static LayoutImageSize ResolveNearest(
        ImageRasterConstraints constraints,
        double desiredWidthPixels,
        double desiredHeightPixels)
    {
        ArgumentNullException.ThrowIfNull(constraints);
        if (!double.IsFinite(desiredWidthPixels)
            || !double.IsFinite(desiredHeightPixels)
            || desiredWidthPixels <= 0
            || desiredHeightPixels <= 0)
            throw new ArgumentException("Desired image pixels must be finite positive values.");

        var aspect = desiredWidthPixels / desiredHeightPixels;
        if (!constraints.AspectInRange(aspect))
            throw new ArgumentException($"Image aspect ratio must be between {constraints.AspectRangeLabel}.", nameof(desiredWidthPixels));

        var candidates = new List<Candidate>();
        foreach (var (width, height) in EnumerateRasters(constraints, aspect))
        {
            var actualAspect = (double)width / height;
            var aspectError = Math.Abs(Math.Log(actualAspect / aspect));
            if (aspectError > PreferredAspectError)
                continue;

            var distance = (long)Math.Round(
                Math.Pow(width - desiredWidthPixels, 2)
                + Math.Pow(height - desiredHeightPixels, 2));
            candidates.Add(new Candidate(width, height, aspectError, distance));
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

    /// <summary>
    /// A delivered raster matches the requested one when its edges are equal, or
    /// when its aspect matches and its pixel count is within
    /// <see cref="ApproximatePixelError"/>, as a fixed-budget model delivers it.
    /// </summary>
    public static bool RasterMatches(LayoutImageSize requested, int width, int height) =>
        width == requested.Width && height == requested.Height
        || width > 0
            && height > 0
            && requested.Width > 0
            && requested.Height > 0
            && AspectMatches((double)width / height, (double)requested.Width / requested.Height)
            && Math.Abs(Math.Log((double)width * height / requested.PixelCount)) <= ApproximatePixelError;

    public static LayoutImageSize ResolveAspect(ImageRasterConstraints constraints, double aspect)
    {
        ArgumentNullException.ThrowIfNull(constraints);
        if (!constraints.AspectInRange(aspect))
            throw new ArgumentException($"Image aspect ratio must be between {constraints.AspectRangeLabel}.", nameof(aspect));

        var candidates = new List<Candidate>();
        foreach (var (width, height) in EnumerateRasters(constraints, aspect))
        {
            var actualAspect = (double)width / height;
            if (!constraints.AspectInRange(actualAspect))
                continue;

            candidates.Add(new Candidate(
                width,
                height,
                Math.Abs(Math.Log(actualAspect / aspect)),
                Math.Abs((long)width * height - constraints.TargetPixels)));
        }

        if (candidates.Count == 0)
            throw new ArgumentException("Could not derive a provider-valid raster size for the requested aspect ratio.", nameof(aspect));

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
        ImageRasterConstraints constraints,
        double widthInches,
        double heightInches,
        double minimumDpi)
    {
        ArgumentNullException.ThrowIfNull(constraints);
        ValidatePhysicalDimensions(widthInches, heightInches);
        ValidateMinimumDpi(minimumDpi);

        var full = ResolveMinimumDpiCore(constraints, widthInches, heightInches, minimumDpi);
        if (full.Raster is not null)
            return full with { PanelSuggestions = [] };

        var suggestions = BuildPanelSuggestions(constraints, widthInches, heightInches, minimumDpi);
        return full with { PanelSuggestions = suggestions };
    }

    public static LayoutImageDpiResolution ResolveFillMinimumDpi(
        ImageRasterConstraints constraints,
        double widthInches,
        double heightInches,
        double minimumDpi)
    {
        ArgumentNullException.ThrowIfNull(constraints);
        ValidatePhysicalDimensions(widthInches, heightInches);
        ValidateMinimumDpi(minimumDpi);

        var targetAspect = widthInches / heightInches;
        var providerAspect = Math.Clamp(targetAspect, constraints.MinimumAspectRatio, constraints.MaximumAspectRatio);
        var compatible = BuildAspectCompatibleSizes(constraints, providerAspect, useNearestAspectFallback: true)
            .Select(size => new CandidateWithDpi(
                size,
                Math.Min(size.Width / widthInches, size.Height / heightInches),
                Math.Abs(Math.Log(((double)size.Width / size.Height) / providerAspect))))
            .ToList();
        var requiredRaster = new LayoutImageRequiredRaster(
            RoundUpToMultiple(widthInches * minimumDpi, constraints.SizeMultiple),
            RoundUpToMultiple(heightInches * minimumDpi, constraints.SizeMultiple));
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

        LayoutPrintUpscalePlan? printUpscalePlan = null;
        if (raster is null && maximum is not null)
        {
            try
            {
                printUpscalePlan = CreateFillPrintUpscalePlan(
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
            ResolveBindingProviderLimits(constraints, targetAspect, requiredRaster, raster, maximum),
            [],
            constraints,
            printUpscalePlan);
    }

    public static double EffectiveDpi(LayoutImageSize raster, double widthInches, double heightInches)
    {
        ValidatePhysicalDimensions(widthInches, heightInches);
        ArgumentNullException.ThrowIfNull(raster);
        ValidatePositiveRaster(raster);
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

    public static LayoutPrintUpscalePlan CreateFillPrintUpscalePlan(
        double widthInches,
        double heightInches,
        double dpi,
        LayoutImageSize nativeRaster,
        double nativeEffectiveDpi)
    {
        ValidatePhysicalDimensions(widthInches, heightInches);
        ValidateMinimumDpi(dpi);
        ArgumentNullException.ThrowIfNull(nativeRaster);
        ValidatePositiveRaster(nativeRaster);

        var minimum = ResolvePrintRaster(widthInches, heightInches, dpi);
        var nativeAspect = (double)nativeRaster.Width / nativeRaster.Height;
        var targetAspect = widthInches / heightInches;
        int width;
        int height;
        if (nativeAspect >= targetAspect)
        {
            height = minimum.Height;
            width = CeilingRasterDimension(height * nativeAspect);
        }
        else
        {
            width = minimum.Width;
            height = CeilingRasterDimension(width / nativeAspect);
        }
        ValidatePrintRaster(width, height);
        return new LayoutPrintUpscalePlan(
            nativeRaster,
            nativeEffectiveDpi,
            new LayoutImageSize(width, height),
            dpi);
    }

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

    /// <summary>Parses a WIDTHxHEIGHT raster with positive edges; provider limits are checked by <see cref="Validate"/>.</summary>
    public static LayoutImageSize Parse(string size)
    {
        var parts = size.Trim().ToLowerInvariant().Split('x', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2
            || !int.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var width)
            || !int.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var height)
            || width <= 0
            || height <= 0)
            throw new ArgumentException("Image size must be WIDTHxHEIGHT with positive pixel edges.", nameof(size));
        return new LayoutImageSize(width, height);
    }

    public static LayoutImageSize ParseValid(ImageRasterConstraints constraints, string size)
    {
        var parsed = Parse(size);
        Validate(constraints, parsed.Width, parsed.Height);
        return parsed;
    }

    /// <summary>
    /// Parses an explicitly requested raster for a model. A fixed-budget model
    /// cannot honor other pixel counts, so a size outside its limits is rescaled
    /// to the budget at the same aspect; other models must accept it as given.
    /// </summary>
    public static LayoutImageSize ParseRequested(ImageRasterConstraints constraints, string size)
    {
        ArgumentNullException.ThrowIfNull(constraints);
        var parsed = Parse(size);
        if (constraints.FixedPixelBudget && ValidationError(constraints, parsed.Width, parsed.Height) is not null)
            return ResolveAspect(constraints, (double)parsed.Width / parsed.Height);
        Validate(constraints, parsed.Width, parsed.Height);
        return parsed;
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

    public static void Validate(ImageRasterConstraints constraints, int width, int height)
    {
        if (ValidationError(constraints, width, height) is { } error)
            throw new ArgumentException(error);
    }

    private static string? ValidationError(ImageRasterConstraints constraints, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(constraints);
        var multiple = constraints.SizeMultiple;
        if (width <= 0 || height <= 0 || width % multiple != 0 || height % multiple != 0)
            return $"Image width and height must be positive and divisible by {multiple}.";
        if (width > constraints.MaximumEdge || height > constraints.MaximumEdge)
            return $"Image edges cannot exceed {constraints.MaximumEdge} pixels.";
        var pixels = (long)width * height;
        if (pixels < constraints.MinimumPixels || pixels > constraints.MaximumPixels)
            return $"Image size must contain {constraints.MinimumPixels:N0}-{constraints.MaximumPixels:N0} pixels.";
        if (!constraints.AspectInRange((double)width / height))
            return $"Image aspect ratio must be between {constraints.AspectRangeLabel}.";
        return null;
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
        ImageRasterConstraints constraints,
        double widthInches,
        double heightInches,
        double minimumDpi)
    {
        var aspect = widthInches / heightInches;
        var compatible = BuildAspectCompatibleSizes(constraints, aspect)
            .Select(size => new CandidateWithDpi(
                size,
                Math.Min(size.Width / widthInches, size.Height / heightInches),
                Math.Abs(Math.Log(((double)size.Width / size.Height) / aspect))))
            .ToList();
        var requiredRaster = new LayoutImageRequiredRaster(
            RoundUpToMultiple(widthInches * minimumDpi, constraints.SizeMultiple),
            RoundUpToMultiple(heightInches * minimumDpi, constraints.SizeMultiple));
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
        var bindingProviderLimits = ResolveBindingProviderLimits(constraints, aspect, requiredRaster, raster, maximum);
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
            constraints,
            printUpscalePlan);
    }

    private static IReadOnlyList<string> ResolveBindingProviderLimits(
        ImageRasterConstraints constraints,
        double aspect,
        LayoutImageRequiredRaster requiredRaster,
        LayoutImageSize? raster,
        CandidateWithDpi? maximum)
    {
        if (raster is not null)
            return [];

        var limits = new List<string>();
        if (!constraints.AspectInRange(aspect))
            limits.Add("aspect_ratio");
        if (requiredRaster.Width > constraints.MaximumEdge || requiredRaster.Height > constraints.MaximumEdge)
            limits.Add("maximum_edge");
        if (requiredRaster.Width > constraints.MaximumPixels / requiredRaster.Height)
            limits.Add("maximum_pixels");
        if (limits.Count == 0 && maximum is null)
            limits.Add("size_alignment_or_aspect_tolerance");
        return limits;
    }

    private static IReadOnlyList<LayoutImagePanelSuggestion> BuildPanelSuggestions(
        ImageRasterConstraints constraints,
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
                var panel = ResolveMinimumDpiCore(constraints, panelWidth, panelHeight, minimumDpi);
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

    private static IReadOnlyList<LayoutImageSize> BuildAspectCompatibleSizes(
        ImageRasterConstraints constraints,
        double aspect,
        bool useNearestAspectFallback = false)
    {
        var candidates = new List<(LayoutImageSize Size, double AspectError)>();
        foreach (var (width, height) in EnumerateRasters(constraints, aspect))
        {
            var actualAspect = (double)width / height;
            if (!constraints.AspectInRange(actualAspect))
                continue;
            candidates.Add((
                new LayoutImageSize(width, height),
                Math.Abs(Math.Log(actualAspect / aspect))));
        }

        var compatible = candidates
            .Where(candidate => candidate.AspectError <= PreferredAspectError)
            .Select(candidate => candidate.Size)
            .ToList();
        if (compatible.Count > 0 || !useNearestAspectFallback || candidates.Count == 0)
            return compatible;

        var nearestError = candidates.Min(candidate => candidate.AspectError);
        return candidates
            .Where(candidate => Math.Abs(candidate.AspectError - nearestError) <= ExactAspectError)
            .Select(candidate => candidate.Size)
            .ToList();
    }

    /// <summary>
    /// Yields every aligned raster within the edge and pixel limits whose height
    /// is one of the three aligned heights nearest the requested aspect at each width.
    /// </summary>
    private static IEnumerable<(int Width, int Height)> EnumerateRasters(ImageRasterConstraints constraints, double aspect)
    {
        var multiple = constraints.SizeMultiple;
        for (var width = multiple; width <= constraints.MaximumEdge; width += multiple)
        {
            var nearestHeight = (int)Math.Round(width / aspect / multiple) * multiple;
            for (var offset = -1; offset <= 1; offset++)
            {
                var height = nearestHeight + offset * multiple;
                if (height <= 0 || height > constraints.MaximumEdge)
                    continue;
                var pixels = (long)width * height;
                if (pixels < constraints.MinimumPixels || pixels > constraints.MaximumPixels)
                    continue;
                yield return (width, height);
            }
        }
    }

    private static void ValidatePositiveRaster(LayoutImageSize raster)
    {
        if (raster.Width <= 0 || raster.Height <= 0)
            throw new ArgumentException("Image raster dimensions must be positive.", nameof(raster));
    }

    private static long RoundUpToMultiple(double value, int multiple)
    {
        var multiples = Math.Ceiling(value / multiple);
        if (!double.IsFinite(multiples) || multiples > long.MaxValue / multiple)
            return long.MaxValue;
        return checked((long)multiples * multiple);
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
