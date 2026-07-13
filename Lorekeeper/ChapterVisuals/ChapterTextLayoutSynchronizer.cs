using System.Text.Json;
using Lorekeeper.Models;

namespace Lorekeeper.ChapterVisuals;

/// <summary>
/// Keeps the chapter body and any persisted Picture Page text representation aligned.
/// The body is canonical whenever prose is edited outside the Picture Page surface.
/// </summary>
public static class ChapterTextLayoutSynchronizer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static bool SynchronizeFromBody(Chapter chapter, string body, bool ensureLayout = false)
    {
        if (!ensureLayout && string.IsNullOrWhiteSpace(chapter.PageLayoutJson))
            return false;

        var layout = ReadLayout(chapter.PageLayoutJson);
        if (string.Equals(ProjectBody(layout), body, StringComparison.Ordinal))
            return false;

        IReadOnlyList<PicturePageTextElement> textElements = layout.TextElements.Count switch
        {
            1 => [layout.TextElements[0] with { Text = body }],
            _ => [DefaultTextElement(body)],
        };

        chapter.PageLayoutJson = JsonSerializer.Serialize(
            layout with { TextElements = textElements },
            JsonOptions);
        return true;
    }

    public static string ProjectBody(PicturePageLayout layout) =>
        string.Join(
            Environment.NewLine + Environment.NewLine,
            layout.TextElements
                .OrderBy(text => text.ReadingOrder)
                .Select(text => text.Text.Trim())
                .Where(text => !string.IsNullOrWhiteSpace(text)));

    public static PicturePageLayout ReadLayout(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new PicturePageLayout([], []);

        try
        {
            return JsonSerializer.Deserialize<PicturePageLayout>(json, JsonOptions)
                ?? new PicturePageLayout([], []);
        }
        catch (JsonException)
        {
            return new PicturePageLayout([], []);
        }
    }

    private static PicturePageTextElement DefaultTextElement(string body) =>
        new(
            Guid.NewGuid(),
            body,
            XPercent: 12,
            YPercent: 68,
            WidthPercent: 76,
            HeightPercent: 20,
            ZIndex: 10,
            ReadingOrder: 0,
            FontFamilyKey: PicturePageFontKeys.Default,
            FontWeight: 400,
            Italic: false,
            FontSizePoints: 24,
            LetterSpacingEm: 0,
            LineHeight: 1.35,
            Color: "#111827",
            BackgroundColor: "#FFFFFF",
            BackgroundOpacity: 0,
            TextAlign: PicturePageTextAlign.Left,
            VerticalAlign: ChapterTextVerticalAlign.Top,
            Shadow: PicturePageTextShadow.None);
}
