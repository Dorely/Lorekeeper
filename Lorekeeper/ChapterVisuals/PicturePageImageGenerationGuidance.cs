using System.Globalization;
using Lorekeeper.Models;

namespace Lorekeeper.ChapterVisuals;

public static class PicturePageImageGenerationGuidance
{
    private const int SizeMultiple = 16;
    private const int MinImagePixels = 655_360;
    private const int MaxImagePixels = 8_294_400;
    private const int MaxImageEdge = 3840;
    private const double MaxImageAspectRatio = 3.0;

    public static PicturePageImageGenerationTarget ForPage(ChapterPageLayoutKind kind)
    {
        var normalized = NormalizePageLayoutKind(kind);
        (double PageWidth, double PageHeight, bool IsDouble, double SurfaceWidth, double SurfaceHeight, int WidthPixels, int HeightPixels) metrics = normalized switch
        {
            ChapterPageLayoutKind.SingleLandscape => (11d, 8.5d, false, 11d, 8.5d, 1760, 1360),
            ChapterPageLayoutKind.DoublePortrait => (8.5d, 11d, true, 17d, 11d, 2720, 1760),
            ChapterPageLayoutKind.DoubleLandscape => (11d, 8.5d, true, 22d, 8.5d, 3520, 1360),
            _ => (8.5d, 11d, false, 8.5d, 11d, 1360, 1760),
        };

        return new PicturePageImageGenerationTarget(
            TargetKind: "fullPage",
            ImageElementId: null,
            PageLayoutKind: normalized,
            PageWidthInches: metrics.PageWidth,
            PageHeightInches: metrics.PageHeight,
            IsDoubleSpread: metrics.IsDouble,
            SurfaceWidthInches: metrics.SurfaceWidth,
            SurfaceHeightInches: metrics.SurfaceHeight,
            TargetXPercent: null,
            TargetYPercent: null,
            TargetWidthPercent: null,
            TargetHeightPercent: null,
            TargetWidthInches: metrics.SurfaceWidth,
            TargetHeightInches: metrics.SurfaceHeight,
            AspectRatio: AspectRatioLabel(metrics.SurfaceWidth, metrics.SurfaceHeight),
            RecommendedSize: $"{metrics.WidthPixels}x{metrics.HeightPixels}",
            RecommendedWidthPixels: metrics.WidthPixels,
            RecommendedHeightPixels: metrics.HeightPixels);
    }

    public static PicturePageImageGenerationTarget ForSlot(
        ChapterPageLayoutKind kind,
        PicturePageImageElement image)
    {
        var page = ForPage(kind);
        var xPercent = Clamp(image.XPercent, 0, 100, 0);
        var yPercent = Clamp(image.YPercent, 0, 100, 0);
        var widthPercent = Clamp(image.WidthPercent, 1, 100, 1);
        var heightPercent = Clamp(image.HeightPercent, 1, 100, 1);
        var targetWidthInches = page.SurfaceWidthInches * widthPercent / 100;
        var targetHeightInches = page.SurfaceHeightInches * heightPercent / 100;
        var recommended = RecommendedImageSize(
            page.RecommendedWidthPixels * widthPercent / 100,
            page.RecommendedHeightPixels * heightPercent / 100,
            targetWidthInches / Math.Max(0.01, targetHeightInches));

        return new PicturePageImageGenerationTarget(
            TargetKind: "imageSlot",
            ImageElementId: image.Id,
            PageLayoutKind: page.PageLayoutKind,
            PageWidthInches: page.PageWidthInches,
            PageHeightInches: page.PageHeightInches,
            IsDoubleSpread: page.IsDoubleSpread,
            SurfaceWidthInches: page.SurfaceWidthInches,
            SurfaceHeightInches: page.SurfaceHeightInches,
            TargetXPercent: xPercent,
            TargetYPercent: yPercent,
            TargetWidthPercent: widthPercent,
            TargetHeightPercent: heightPercent,
            TargetWidthInches: targetWidthInches,
            TargetHeightInches: targetHeightInches,
            AspectRatio: AspectRatioLabel(targetWidthInches, targetHeightInches),
            RecommendedSize: $"{recommended.Width}x{recommended.Height}",
            RecommendedWidthPixels: recommended.Width,
            RecommendedHeightPixels: recommended.Height);
    }

    public static bool TryResolveTarget(
        ChapterVisualState state,
        Guid? imageElementId,
        out PicturePageImageGenerationTarget? target,
        out string? error)
    {
        target = null;
        error = null;
        if (state.VisualMode != ChapterVisualMode.PicturePage)
        {
            error = $"Error: chapter {state.ChapterId:N} is {state.VisualMode}, not PicturePage.";
            return false;
        }

        if (imageElementId is not { } elementId || elementId == Guid.Empty)
        {
            target = ForPage(state.PageLayoutKind);
            return true;
        }

        var image = state.PageLayout.Images.FirstOrDefault(candidate => candidate.Id == elementId);
        if (image is null)
        {
            error = $"Error: picture page image element {elementId:N} was not found in chapter {state.ChapterId:N}.";
            return false;
        }

        target = ForSlot(state.PageLayoutKind, image);
        return true;
    }

    public static string BuildPromptAppendix(
        PicturePageImageGenerationTarget target,
        IReadOnlyList<PicturePageTextElement> textElements)
    {
        var targetDescription = target.TargetKind == "imageSlot"
            ? $"existing image slot {FormatGuid(target.ImageElementId)} at {FormatPercent(target.TargetXPercent)}%,{FormatPercent(target.TargetYPercent)}% size {FormatPercent(target.TargetWidthPercent)}%x{FormatPercent(target.TargetHeightPercent)}%"
            : "the full PicturePage page/spread surface";
        var builder = new List<string>
        {
            "",
            "PicturePage layout guidance:",
            $"- Target: {targetDescription} in {target.PageLayoutKind}; physical target {FormatNumber(target.TargetWidthInches)} x {FormatNumber(target.TargetHeightInches)} in.",
            $"- Generate for aspect {target.AspectRatio}; recommended image size {target.RecommendedSize}.",
            "- Compose with clear safe margins and usable negative space where Lorekeeper will overlay text boxes.",
            "- Keep faces, focal objects, and important action out of text-safe areas; avoid text, logos, watermarks, and border decorations unless explicitly requested.",
        };

        if (target.IsDoubleSpread)
            builder.Add("- This is a two-page spread; keep important details away from the center gutter.");

        var safeAreas = DescribeTextSafeAreas(textElements);
        if (safeAreas.Length > 0)
            builder.Add($"- Text-safe areas to keep visually quiet: {safeAreas}.");

        return string.Join(Environment.NewLine, builder);
    }

    public static IReadOnlyList<string> BuildManifestLines(ChapterVisualState state)
    {
        if (state.VisualMode != ChapterVisualMode.PicturePage)
            return [];

        var page = ForPage(state.PageLayoutKind);
        var lines = new List<string>
        {
            $"Image generation target: full page/spread aspect {page.AspectRatio}, recommended size {page.RecommendedSize}.",
            "Image prompt guidance: use a structured image brief with subject, style, composition, lighting, and constraints; reserve quiet negative space for text boxes; avoid text, logos, and watermarks unless explicitly requested.",
        };

        if (page.IsDoubleSpread)
            lines.Add("Image prompt guidance: this layout is a two-page spread; avoid placing important details across the center gutter.");

        var safeAreas = DescribeTextSafeAreas(state.PageLayout.TextElements);
        if (safeAreas.Length > 0)
            lines.Add($"Text-safe areas for image generation: {safeAreas}.");

        return lines;
    }

    public static string DescribeTarget(PicturePageImageGenerationTarget target) =>
        target.TargetKind == "imageSlot"
            ? $"PicturePage slot {FormatGuid(target.ImageElementId)}, aspect {target.AspectRatio}, recommended size {target.RecommendedSize}"
            : $"PicturePage full layout {target.PageLayoutKind}, aspect {target.AspectRatio}, recommended size {target.RecommendedSize}";

    private static (int Width, int Height) RecommendedImageSize(
        double desiredWidth,
        double desiredHeight,
        double desiredAspectRatio)
    {
        var aspect = Math.Clamp(desiredAspectRatio, 1 / MaxImageAspectRatio, MaxImageAspectRatio);
        var area = Math.Clamp(desiredWidth * desiredHeight, MinImagePixels, MaxImagePixels);
        var width = Math.Sqrt(area * aspect);
        var height = width / aspect;
        var maxEdge = Math.Max(width, height);
        if (maxEdge > MaxImageEdge)
        {
            var scale = MaxImageEdge / maxEdge;
            width *= scale;
            height *= scale;
        }

        var roundedWidth = Math.Max(SizeMultiple, CeilingToMultiple(width, SizeMultiple));
        var roundedHeight = Math.Max(SizeMultiple, CeilingToMultiple(height, SizeMultiple));
        while ((long)roundedWidth * roundedHeight > MaxImagePixels
            || roundedWidth > MaxImageEdge
            || roundedHeight > MaxImageEdge)
        {
            roundedWidth = Math.Max(SizeMultiple, roundedWidth - SizeMultiple);
            roundedHeight = Math.Max(SizeMultiple, roundedHeight - SizeMultiple);
        }

        return (roundedWidth, roundedHeight);
    }

    private static string DescribeTextSafeAreas(IReadOnlyList<PicturePageTextElement> textElements)
    {
        var areas = textElements
            .OrderBy(text => text.ReadingOrder)
            .Select(text => $"text {text.ReadingOrder} at {FormatPercent(text.XPercent)}%,{FormatPercent(text.YPercent)}% size {FormatPercent(text.WidthPercent)}%x{FormatPercent(text.HeightPercent)}%")
            .ToList();
        return string.Join("; ", areas);
    }

    private static ChapterPageLayoutKind NormalizePageLayoutKind(ChapterPageLayoutKind kind) =>
        Enum.IsDefined(kind) ? kind : ChapterPageLayoutKind.SinglePortrait;

    private static string AspectRatioLabel(double width, double height)
    {
        var scaledWidth = Math.Max(1, (int)Math.Round(width * 1000, MidpointRounding.AwayFromZero));
        var scaledHeight = Math.Max(1, (int)Math.Round(height * 1000, MidpointRounding.AwayFromZero));
        var gcd = GreatestCommonDivisor(scaledWidth, scaledHeight);
        return $"{scaledWidth / gcd}:{scaledHeight / gcd}";
    }

    private static int GreatestCommonDivisor(int a, int b)
    {
        while (b != 0)
        {
            var next = a % b;
            a = b;
            b = next;
        }

        return Math.Max(1, Math.Abs(a));
    }

    private static int CeilingToMultiple(double value, int multiple) =>
        (int)(Math.Ceiling(value / multiple) * multiple);

    private static double Clamp(double value, double min, double max, double fallback) =>
        double.IsNaN(value) || double.IsInfinity(value)
            ? fallback
            : Math.Min(max, Math.Max(min, value));

    private static string FormatNumber(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string FormatPercent(double? value) =>
        (value ?? 0).ToString("0.#", CultureInfo.InvariantCulture);

    private static string FormatGuid(Guid? value) =>
        value?.ToString("N") ?? "none";
}

public sealed record PicturePageImageGenerationTarget(
    string TargetKind,
    Guid? ImageElementId,
    ChapterPageLayoutKind PageLayoutKind,
    double PageWidthInches,
    double PageHeightInches,
    bool IsDoubleSpread,
    double SurfaceWidthInches,
    double SurfaceHeightInches,
    double? TargetXPercent,
    double? TargetYPercent,
    double? TargetWidthPercent,
    double? TargetHeightPercent,
    double TargetWidthInches,
    double TargetHeightInches,
    string AspectRatio,
    string RecommendedSize,
    int RecommendedWidthPixels,
    int RecommendedHeightPixels);
