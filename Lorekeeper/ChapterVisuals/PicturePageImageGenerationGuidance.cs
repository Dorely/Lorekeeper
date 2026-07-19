using System.Globalization;
using Lorekeeper.Models;
using Lorekeeper.Publish;

namespace Lorekeeper.ChapterVisuals;

public static class PicturePageImageGenerationGuidance
{
    private const int SizeMultiple = 16;
    private const int MinImagePixels = 655_360;
    private const int MaxImagePixels = 8_294_400;
    private const int MaxImageEdge = 3840;
    private const double MaxImageAspectRatio = 3.0;
    private const double TextArtBufferInches = 0.125;

    public static PicturePageImageGenerationTarget ForPage(BookPageGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        var recommended = RecommendedImageSize(
            geometry.RecommendedRasterWidthPixels,
            geometry.RecommendedRasterHeightPixels,
            geometry.SurfaceAspectRatio);

        return new PicturePageImageGenerationTarget(
            TargetKind: "fullPage",
            ImageElementId: null,
            PageLayoutKind: geometry.LayoutKind,
            PageWidthInches: geometry.PageWidthInches,
            PageHeightInches: geometry.PageHeightInches,
            IsDoubleSpread: geometry.IsDouble,
            SurfaceWidthInches: geometry.SurfaceWidthInches,
            SurfaceHeightInches: geometry.SurfaceHeightInches,
            TargetXPercent: null,
            TargetYPercent: null,
            TargetWidthPercent: null,
            TargetHeightPercent: null,
            TargetWidthInches: geometry.SurfaceWidthInches,
            TargetHeightInches: geometry.SurfaceHeightInches,
            AspectRatio: AspectRatioLabel(geometry.SurfaceWidthInches, geometry.SurfaceHeightInches),
            RecommendedSize: $"{recommended.Width}x{recommended.Height}",
            RecommendedWidthPixels: recommended.Width,
            RecommendedHeightPixels: recommended.Height);
    }

    public static PicturePageCanvasGeometry CanvasGeometry(BookPageGeometry geometry)
    {
        var page = ForPage(geometry);
        return new PicturePageCanvasGeometry(
            page.PageLayoutKind,
            page.PageWidthInches,
            page.PageHeightInches,
            page.SurfaceWidthInches,
            page.SurfaceHeightInches,
            page.SurfaceWidthInches >= page.SurfaceHeightInches ? "landscape" : "portrait",
            page.AspectRatio,
            page.IsDoubleSpread,
            page.IsDoubleSpread ? 50d : null);
    }

    public static PicturePageImageGenerationTarget ForSlot(
        BookPageGeometry geometry,
        PicturePageImageElement image)
    {
        var page = ForPage(geometry);
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
        BookPageGeometry geometry,
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
            target = ForPage(geometry);
            return true;
        }

        var image = state.PageLayout.Images.FirstOrDefault(candidate => candidate.Id == elementId);
        if (image is null)
        {
            error = $"Error: picture page image element {elementId:N} was not found in chapter {state.ChapterId:N}.";
            return false;
        }

        target = ForSlot(geometry, image);
        return true;
    }

    public static string BuildPromptAppendix(
        PicturePageImageGenerationTarget target,
        IReadOnlyList<PicturePageTextElement> textElements)
    {
        var builder = new List<string>
        {
            "",
            "Output composition constraints:",
            $"- Canvas: {target.AspectRatio} aspect ratio for {(target.TargetKind == "imageSlot" ? "a placed illustration" : "full-bleed page art")}.",
            "- Extend artwork through every canvas edge for bleed-aware cropping while keeping important subjects and details away from trim-loss areas.",
            "- Do not depict a page border, book mockup, binding seam, fold, or simulated gutter in the artwork.",
            "- Do not render text, logos, watermarks, or border decorations unless the target brief explicitly requests them.",
        };

        if (target.IsDoubleSpread)
            builder.Add("- Two-page spread: the center gutter is a protected geometric risk area, not a visible feature to draw. Keep faces, characters, focal action, and important details away from it and the adjacent safety margin on both sides.");

        var safeAreas = DescribeTextSafeAreas(target, textElements);
        if (safeAreas.Length > 0)
        {
            builder.Add($"- Hard layout requirement for this generation attempt: create naturally integrated, quiet negative space for the full editable copy area at these canvas-local percentages: {safeAreas}.");
            builder.Add("- Preserve every reserved region as one fully usable text field: use simple forms, low detail, low contrast variation, and a stable light or dark value throughout it. Keep faces, hands, characters, focal objects, important action, sharp edges, high-frequency texture, and strong value transitions outside it.");
            builder.Add("- The reserved area must feel like part of the scene, not a visible placeholder rectangle, frame, sign, caption panel, or blank graphic box.");
        }

        return string.Join(Environment.NewLine, builder);
    }

    public static IReadOnlyList<string> BuildManifestLines(ChapterVisualState state, BookPageGeometry geometry)
    {
        if (state.VisualMode != ChapterVisualMode.PicturePage)
            return [];

        var page = ForPage(geometry);
        var lines = new List<string>
        {
            $"Canvas geometry: {FormatNumber(page.SurfaceWidthInches)} x {FormatNumber(page.SurfaceHeightInches)} inches, {page.AspectRatio} aspect, {(page.SurfaceWidthInches >= page.SurfaceHeightInches ? "landscape" : "portrait")} orientation.",
            "Choose each generated raster for its intended frame; placement determines physical size.",
        };

        if (page.IsDoubleSpread)
            lines.Add("Two-page spread geometry: do not depict a gutter or binding seam; keep faces, characters, focal action, and important details away from the protected center gutter area.");

        var safeAreas = DescribeTextSafeAreas(page, state.PageLayout.TextElements);
        if (safeAreas.Length > 0)
            lines.Add($"Buffered text-safe rectangles for image generation: {safeAreas}.");

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

    private static string DescribeTextSafeAreas(
        PicturePageImageGenerationTarget target,
        IReadOnlyList<PicturePageTextElement> textElements)
    {
        var targetX = target.TargetXPercent ?? 0;
        var targetY = target.TargetYPercent ?? 0;
        var targetWidth = target.TargetWidthPercent ?? 100;
        var targetHeight = target.TargetHeightPercent ?? 100;
        var bufferX = TextArtBufferInches / target.SurfaceWidthInches * 100;
        var bufferY = TextArtBufferInches / target.SurfaceHeightInches * 100;
        var areas = textElements
            .OrderBy(text => text.ReadingOrder)
            .Select(text =>
            {
                var left = Math.Max(targetX, text.XPercent - bufferX);
                var top = Math.Max(targetY, text.YPercent - bufferY);
                var right = Math.Min(targetX + targetWidth, text.XPercent + text.WidthPercent + bufferX);
                var bottom = Math.Min(targetY + targetHeight, text.YPercent + text.HeightPercent + bufferY);
                if (right <= left || bottom <= top)
                    return null;
                var localX = (left - targetX) / targetWidth * 100;
                var localY = (top - targetY) / targetHeight * 100;
                var localWidth = (right - left) / targetWidth * 100;
                var localHeight = (bottom - top) / targetHeight * 100;
                return $"text {text.ReadingOrder} at {FormatPercent(localX)}%,{FormatPercent(localY)}% size {FormatPercent(localWidth)}%x{FormatPercent(localHeight)}%";
            })
            .Where(area => area is not null)
            .ToList();
        return string.Join("; ", areas);
    }

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

public sealed record PicturePageCanvasGeometry(
    ChapterPageLayoutKind LayoutKind,
    double LeafWidthInches,
    double LeafHeightInches,
    double CanvasWidthInches,
    double CanvasHeightInches,
    string Orientation,
    string AspectRatio,
    bool IsSpread,
    double? GutterCenterXPercent);
