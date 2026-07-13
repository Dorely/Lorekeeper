using System.Globalization;
using Lorekeeper.Models;

namespace Lorekeeper.ChapterVisuals;

public static class PicturePageImageGenerationGuidance
{
    public const string AgentInstructions = """
        PicturePage design and verification:
        - Treat copy, text geometry, and illustration as one composition. Establish provisional text boxes before generating a full-page background so the image prompt receives exact quiet regions.
        - Use an adaptive picture-book baseline: clear type and predictable reading flow by default, with larger/simpler treatment for early readers and more expressive display treatment for short, art-led read-aloud passages when project context supports it.
        - Prefer one clear text landing zone. Use multiple boxes only for deliberate narrative beats, and preserve an obvious language-appropriate reading path. Default multiline prose to left/top alignment; reserve centered or display treatment for short passages.
        - Keep text at least 0.375 inches from trim edges and from both sides of a spread gutter; prefer 0.5 inches. Keep it away from faces, hands, focal objects, important action, and highly detailed or variable backgrounds.
        - Break lines at natural spoken or syntactic pauses. Avoid widows, orphaned words, dense lines, excessive all-caps, and long italic passages. Use no more than two font families per spread and keep body/accent choices coherent across the book.
        - Target at least 4.5:1 text contrast. Use 3:1 only for genuinely large display type. When art cannot maintain contrast, prefer a translucent solid backing panel; use shadows or halos only as secondary aids.
        - For a new full-page background, generate against the page target, place it with the Background role, then inspect the newest rendered snapshot. Adjust text geometry or backing panels before regenerating art; regenerate only when the composition is fundamentally incompatible or the user explicitly requests it.
        - Freeform placement keeps the normal centered geometry. Background means 0,0,100,100 with Cover behind every other element. ReplaceElement preserves the target image element's geometry and layer.
        - After every corrective PicturePage mutation, read the visual layout again. Do not claim success until allTextFits is true, all error-level diagnostics are cleared unless the user explicitly requests an exception, advisory warnings are reviewed, and the actual newest snapshot has been inspected for contrast, hierarchy, focal conflicts, and reading flow.
        """;

    private const int SizeMultiple = 16;
    private const int MinImagePixels = 655_360;
    private const int MaxImagePixels = 8_294_400;
    private const int MaxImageEdge = 3840;
    private const double MaxImageAspectRatio = 3.0;
    private const double TextArtBufferInches = 0.125;

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
            "- Compose through the target edges for bleed-aware cropping, but do not place important subjects or details in bleed/trim loss areas.",
            "- Preserve the listed buffered rectangles as stable, low-detail, low-variation landing zones for overlaid type.",
            "- Keep faces, focal objects, and important action out of text-safe areas; avoid text, logos, watermarks, and border decorations unless explicitly requested.",
        };

        if (target.IsDoubleSpread)
            builder.Add("- This is a two-page spread; exclude important details from the center gutter and at least 0.375 inches on both sides of it.");

        var safeAreas = DescribeTextSafeAreas(target, textElements);
        if (safeAreas.Length > 0)
            builder.Add($"- Buffered text-safe rectangles in target-local coordinates: {safeAreas}.");
        else if (target.TargetKind == "fullPage")
            builder.Add("- No provisional text boxes are stored yet. Establish text geometry before treating this art as a final background composition.");

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
            $"Physical page target: {FormatNumber(page.SurfaceWidthInches)} x {FormatNumber(page.SurfaceHeightInches)} inches; compose through edges for bleed while keeping important content out of trim loss areas.",
            "Image prompt guidance: plan text and art together; use a structured image brief with subject, style, composition, lighting, and constraints; reserve stable quiet negative space for buffered text boxes; avoid text, logos, and watermarks unless explicitly requested.",
        };

        if (page.IsDoubleSpread)
            lines.Add("Image prompt guidance: this layout is a two-page spread; avoid placing important details across the center gutter.");

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
