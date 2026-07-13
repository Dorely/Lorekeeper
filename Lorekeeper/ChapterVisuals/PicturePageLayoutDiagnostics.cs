using Lorekeeper.Models;

namespace Lorekeeper.ChapterVisuals;

public static class PicturePageLayoutDiagnostics
{
    public const double MinimumSafeInsetInches = 0.375;
    public const double PreferredInsetInches = 0.5;

    public static IReadOnlyList<ChapterVisualLayoutDiagnostic> Evaluate(
        ChapterPageLayoutKind pageLayoutKind,
        PicturePageLayout layout)
    {
        var isLandscape = pageLayoutKind is ChapterPageLayoutKind.SingleLandscape or ChapterPageLayoutKind.DoubleLandscape;
        var isDouble = pageLayoutKind is ChapterPageLayoutKind.DoublePortrait or ChapterPageLayoutKind.DoubleLandscape;
        var pageWidth = isLandscape ? 11.0 : 8.5;
        var pageHeight = isLandscape ? 8.5 : 11.0;
        var surfaceWidth = isDouble ? pageWidth * 2 : pageWidth;
        var hardX = MinimumSafeInsetInches / surfaceWidth * 100;
        var hardY = MinimumSafeInsetInches / pageHeight * 100;
        var preferredX = PreferredInsetInches / surfaceWidth * 100;
        var preferredY = PreferredInsetInches / pageHeight * 100;
        var diagnostics = new List<ChapterVisualLayoutDiagnostic>();

        foreach (var text in layout.TextElements)
        {
            var right = text.XPercent + text.WidthPercent;
            var bottom = text.YPercent + text.HeightPercent;
            if (text.XPercent < hardX || text.YPercent < hardY || right > 100 - hardX || bottom > 100 - hardY)
            {
                diagnostics.Add(new ChapterVisualLayoutDiagnostic(
                    "outside_print_safe_area",
                    "error",
                    "Text enters the 0.375-inch trim safety area.",
                    text.Id));
            }
            else if (text.XPercent < preferredX || text.YPercent < preferredY || right > 100 - preferredX || bottom > 100 - preferredY)
            {
                diagnostics.Add(new ChapterVisualLayoutDiagnostic(
                    "inside_preferred_buffer",
                    "warning",
                    "Text is print-safe but inside the preferred 0.5-inch edge buffer.",
                    text.Id));
            }

            if (isDouble && text.XPercent < 50 + hardX && right > 50 - hardX)
            {
                diagnostics.Add(new ChapterVisualLayoutDiagnostic(
                    "inside_gutter_safe_area",
                    "error",
                    "Text enters the 0.375-inch safety area on either side of the center gutter.",
                    text.Id));
            }
            else if (isDouble && text.XPercent < 50 + preferredX && right > 50 - preferredX)
            {
                diagnostics.Add(new ChapterVisualLayoutDiagnostic(
                    "inside_preferred_gutter_buffer",
                    "warning",
                    "Text is print-safe but inside the preferred 0.5-inch buffer on either side of the center gutter.",
                    text.Id));
            }
        }

        for (var index = 0; index < layout.TextElements.Count; index++)
        {
            for (var otherIndex = index + 1; otherIndex < layout.TextElements.Count; otherIndex++)
            {
                if (Overlaps(layout.TextElements[index], layout.TextElements[otherIndex]))
                {
                    diagnostics.Add(new ChapterVisualLayoutDiagnostic(
                        "overlapping_text_boxes",
                        "warning",
                        "Two text boxes overlap; verify that this is intentional and preserves reading order.",
                        layout.TextElements[index].Id,
                        layout.TextElements[otherIndex].Id));
                }
            }
        }

        foreach (var text in layout.TextElements)
        {
            foreach (var image in layout.Images.Where(image => image.ZIndex > text.ZIndex && Overlaps(text, image)))
            {
                diagnostics.Add(new ChapterVisualLayoutDiagnostic(
                    "image_above_text",
                    "error",
                    "A higher-layer image overlaps and may obscure this text box.",
                    text.Id,
                    image.Id));
            }
        }

        return diagnostics;
    }

    private static bool Overlaps(PicturePageTextElement left, PicturePageTextElement right) =>
        RectanglesOverlap(left.XPercent, left.YPercent, left.WidthPercent, left.HeightPercent,
            right.XPercent, right.YPercent, right.WidthPercent, right.HeightPercent);

    private static bool Overlaps(PicturePageTextElement text, PicturePageImageElement image) =>
        RectanglesOverlap(text.XPercent, text.YPercent, text.WidthPercent, text.HeightPercent,
            image.XPercent, image.YPercent, image.WidthPercent, image.HeightPercent);

    private static bool RectanglesOverlap(
        double leftX,
        double leftY,
        double leftWidth,
        double leftHeight,
        double rightX,
        double rightY,
        double rightWidth,
        double rightHeight) =>
        leftX < rightX + rightWidth
        && leftX + leftWidth > rightX
        && leftY < rightY + rightHeight
        && leftY + leftHeight > rightY;
}
