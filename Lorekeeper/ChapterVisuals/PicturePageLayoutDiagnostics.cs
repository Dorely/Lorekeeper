using System.Globalization;
using Lorekeeper.Models;
using Lorekeeper.Publish;

namespace Lorekeeper.ChapterVisuals;

public static class PicturePageLayoutDiagnostics
{
    public const double MinimumSafeInsetInches = 0.375;
    public const double PreferredInsetInches = 0.5;
    public const int MaximumFontFamiliesPerSpread = 2;
    public const double MinimumBodyFontPoints = 10;
    public const double YoungReaderAdvisoryFontPoints = 14;

    public static IReadOnlyList<ChapterVisualLayoutDiagnostic> Evaluate(
        BookPageGeometry geometry,
        PicturePageLayout layout,
        BookBrief? brief = null)
    {
        var hardX = MinimumSafeInsetInches / geometry.SurfaceWidthInches * 100;
        var hardY = MinimumSafeInsetInches / geometry.SurfaceHeightInches * 100;
        var preferredX = PreferredInsetInches / geometry.SurfaceWidthInches * 100;
        var preferredY = PreferredInsetInches / geometry.SurfaceHeightInches * 100;
        var diagnostics = new List<ChapterVisualLayoutDiagnostic>();

        foreach (var text in layout.TextElements)
        {
            var right = text.XPercent + text.WidthPercent;
            var bottom = text.YPercent + text.HeightPercent;
            if (text.XPercent < hardX || text.YPercent < hardY || right > 100 - hardX || bottom > 100 - hardY)
            {
                diagnostics.Add(Diagnostic(
                    "outside_print_safe_area",
                    "error",
                    "Text enters the trim safety area.",
                    text.Id,
                    MeasuredEdgeInset(geometry, text),
                    $">= {MinimumSafeInsetInches:0.###} in",
                    "Move or resize the text box farther from the trim edge."));
            }
            else if (text.XPercent < preferredX || text.YPercent < preferredY || right > 100 - preferredX || bottom > 100 - preferredY)
            {
                diagnostics.Add(Diagnostic(
                    "inside_preferred_buffer",
                    "warning",
                    "Text is print-safe but inside the preferred edge buffer.",
                    text.Id,
                    MeasuredEdgeInset(geometry, text),
                    $">= {PreferredInsetInches:0.###} in preferred",
                    "Move the box inward when the tighter placement is not intentional."));
            }

            if (geometry.IsDouble && text.XPercent < 50 + hardX && right > 50 - hardX)
            {
                diagnostics.Add(Diagnostic(
                    "inside_gutter_safe_area",
                    "error",
                    "Text enters the safety area on either side of the center gutter.",
                    text.Id,
                    GutterDistance(geometry, text),
                    $">= {MinimumSafeInsetInches:0.###} in from gutter",
                    "Move or resize the box so all text clears both sides of the gutter."));
            }
            else if (geometry.IsDouble && text.XPercent < 50 + preferredX && right > 50 - preferredX)
            {
                diagnostics.Add(Diagnostic(
                    "inside_preferred_gutter_buffer",
                    "warning",
                    "Text is technically gutter-safe but inside the preferred gutter buffer.",
                    text.Id,
                    GutterDistance(geometry, text),
                    $">= {PreferredInsetInches:0.###} in preferred",
                    "Increase gutter clearance unless the close placement is deliberate."));
            }

            AddTypographyDiagnostics(diagnostics, geometry, brief, text);
        }

        AddOverlapDiagnostics(diagnostics, layout);
        AddReadingOrderDiagnostics(diagnostics, layout.TextElements);
        AddFontDisciplineDiagnostics(diagnostics, layout.TextElements);
        return diagnostics;
    }

    private static void AddTypographyDiagnostics(
        ICollection<ChapterVisualLayoutDiagnostic> diagnostics,
        BookPageGeometry geometry,
        BookBrief? brief,
        PicturePageTextElement text)
    {
        if (text.Role == PicturePageTextRole.Body && text.FontSizePoints < MinimumBodyFontPoints)
        {
            diagnostics.Add(Diagnostic(
                "undersized_body_copy",
                "warning",
                "Body copy is unusually small for sustained reading.",
                text.Id,
                $"{text.FontSizePoints:0.#} pt",
                $">= {MinimumBodyFontPoints:0.#} pt",
                "Increase body type or shorten/reflow the copy."));
        }

        if (text.Role == PicturePageTextRole.Body
            && brief?.MaximumReaderAge is <= 8
            && text.FontSizePoints < YoungReaderAdvisoryFontPoints)
        {
            diagnostics.Add(Diagnostic(
                "young_reader_body_size_advisory",
                "warning",
                "Picture-book body copy for readers eight or younger is below Lorekeeper's advisory size.",
                text.Id,
                $"{text.FontSizePoints:0.#} pt",
                $">= {YoungReaderAdvisoryFontPoints:0.#} pt advisory",
                "Increase the type size, reduce copy, or intentionally accept the advisory for this audience."));
        }

        var isGeneralProse = text.Role == PicturePageTextRole.Body
            && WordCount(text.Text) >= 40
            && brief?.BookKind is not (BookKind.PictureBook or BookKind.Poetry);
        if (isGeneralProse)
        {
            var widthInches = geometry.SurfaceWidthInches * text.WidthPercent / 100;
            var estimatedCharacters = Math.Max(1, (int)Math.Round(widthInches * 72 / Math.Max(1, text.FontSizePoints * 0.52)));
            if (estimatedCharacters is < 45 or > 90)
            {
                diagnostics.Add(Diagnostic(
                    "general_prose_line_length",
                    "warning",
                    "Estimated body-copy line length is outside the general-prose range.",
                    text.Id,
                    $"about {estimatedCharacters} characters",
                    "45-90 characters",
                    estimatedCharacters < 45
                        ? "Widen the box or reduce body type if the narrow measure is not intentional."
                        : "Narrow the box or increase body type for easier line tracking."));
            }
        }

        var explicitLines = text.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (explicitLines.Length > 1)
        {
            var finalLength = explicitLines.LastOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim().Length ?? 0;
            var priorLength = explicitLines.Reverse().SkipWhile(string.IsNullOrWhiteSpace).Skip(1).FirstOrDefault()?.Trim().Length ?? 0;
            if (finalLength is > 0 and < 15 && priorLength >= 30)
            {
                diagnostics.Add(Diagnostic(
                    "short_final_line",
                    "warning",
                    "The final explicit line is markedly shorter than the preceding line.",
                    text.Id,
                    $"{finalLength} characters",
                    "avoid conspicuously short final lines",
                    "Adjust the box width, line break, tracking, or copy if the short line is not expressive."));
            }
        }

        var paragraphs = text.Text.Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (paragraphs.Length > 1 && paragraphs.Any(paragraph => WordCount(paragraph) <= 3))
        {
            diagnostics.Add(Diagnostic(
                "widow_or_orphan_risk",
                "warning",
                "A very short paragraph may create a widow or orphan in this box.",
                text.Id,
                "paragraph of 3 words or fewer",
                "keep at least two balanced lines when practical",
                "Reflow the box or revise the break while preserving intentional display fragments."));
        }
    }

    private static void AddOverlapDiagnostics(
        ICollection<ChapterVisualLayoutDiagnostic> diagnostics,
        PicturePageLayout layout)
    {
        for (var index = 0; index < layout.TextElements.Count; index++)
        {
            for (var otherIndex = index + 1; otherIndex < layout.TextElements.Count; otherIndex++)
            {
                if (!Overlaps(layout.TextElements[index], layout.TextElements[otherIndex])) continue;
                diagnostics.Add(new(
                    "overlapping_text_boxes",
                    "warning",
                    "Two text boxes overlap; verify that the overlap is intentional and preserves reading order.",
                    layout.TextElements[index].Id,
                    layout.TextElements[otherIndex].Id,
                    "intersecting bounds",
                    "no unintended overlap",
                    "Move, resize, or deliberately layer the affected boxes."));
            }
        }

        foreach (var text in layout.TextElements)
        {
            foreach (var image in layout.Images.Where(image => image.ZIndex > text.ZIndex && Overlaps(text, image)))
            {
                diagnostics.Add(new(
                    "image_above_text",
                    "error",
                    "A higher-layer image overlaps and may obscure this text box.",
                    text.Id,
                    image.Id,
                    $"image z-index {image.ZIndex}; text z-index {text.ZIndex}",
                    "text unobscured",
                    "Move the elements, change their bounds, or put the text above the image."));
            }
        }
    }

    private static void AddReadingOrderDiagnostics(
        ICollection<ChapterVisualLayoutDiagnostic> diagnostics,
        IReadOnlyList<PicturePageTextElement> texts)
    {
        foreach (var duplicate in texts.GroupBy(text => text.ReadingOrder).Where(group => group.Count() > 1))
        {
            var elements = duplicate.ToList();
            diagnostics.Add(new(
                "duplicate_reading_order",
                "error",
                "Two text boxes have the same reading-order value.",
                elements[0].Id,
                elements[1].Id,
                duplicate.Key.ToString(CultureInfo.InvariantCulture),
                "unique sequential values",
                "Assign a unique reading order that matches the intended narrative path."));
        }

        if (texts.Count < 2) return;
        var declared = texts.OrderBy(text => text.ReadingOrder).Select(text => text.Id).ToList();
        var spatial = texts.OrderBy(text => text.YPercent).ThenBy(text => text.XPercent).Select(text => text.Id).ToList();
        if (!declared.SequenceEqual(spatial))
        {
            diagnostics.Add(Diagnostic(
                "reading_order_visual_conflict",
                "warning",
                "Declared reading order conflicts with the default top-to-bottom, left-to-right visual path.",
                declared.FirstOrDefault(),
                "declared order differs from spatial order",
                "logical and visual order agree",
                "Reorder the elements or strengthen visual cues when the non-spatial path is intentional."));
        }
    }

    private static void AddFontDisciplineDiagnostics(
        ICollection<ChapterVisualLayoutDiagnostic> diagnostics,
        IReadOnlyList<PicturePageTextElement> texts)
    {
        var families = texts.Select(text => text.FontFamilyKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (families.Count > MaximumFontFamiliesPerSpread)
        {
            diagnostics.Add(Diagnostic(
                "excess_font_families",
                "warning",
                "The page uses more font families than Lorekeeper's spread-level default.",
                null,
                $"{families.Count} families",
                $"<= {MaximumFontFamiliesPerSpread}",
                "Consolidate body and display roles unless the additional family has a clear function."));
        }

        var bodyStyles = texts
            .Where(text => text.Role == PicturePageTextRole.Body)
            .Select(text => new { text.FontFamilyKey, text.FontWeight, text.Italic, Size = Math.Round(text.FontSizePoints, 1), LineHeight = Math.Round(text.LineHeight, 2) })
            .Distinct()
            .Count();
        if (bodyStyles > 1)
        {
            diagnostics.Add(Diagnostic(
                "inconsistent_body_style",
                "warning",
                "Body text uses inconsistent family, weight, size, italic, or line-height settings.",
                null,
                $"{bodyStyles} body styles",
                "1 consistent body style per page/spread",
                "Normalize body styles unless the difference marks a deliberate hierarchy."));
        }
    }

    private static ChapterVisualLayoutDiagnostic Diagnostic(
        string code,
        string severity,
        string message,
        Guid? elementId,
        string measuredValue,
        string threshold,
        string correction) =>
        new(code, severity, message, elementId, null, measuredValue, threshold, correction);

    private static string MeasuredEdgeInset(BookPageGeometry geometry, PicturePageTextElement text)
    {
        var left = text.XPercent / 100 * geometry.SurfaceWidthInches;
        var right = (100 - text.XPercent - text.WidthPercent) / 100 * geometry.SurfaceWidthInches;
        var top = text.YPercent / 100 * geometry.SurfaceHeightInches;
        var bottom = (100 - text.YPercent - text.HeightPercent) / 100 * geometry.SurfaceHeightInches;
        return $"{Math.Min(Math.Min(left, right), Math.Min(top, bottom)):0.###} in minimum";
    }

    private static string GutterDistance(BookPageGeometry geometry, PicturePageTextElement text)
    {
        var left = text.XPercent;
        var right = text.XPercent + text.WidthPercent;
        var distancePercent = right <= 50 ? 50 - right : left >= 50 ? left - 50 : 0;
        return $"{distancePercent / 100 * geometry.SurfaceWidthInches:0.###} in";
    }

    private static int WordCount(string value) =>
        value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

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
