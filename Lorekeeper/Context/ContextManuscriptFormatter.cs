using System.Text.Json;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;

namespace Lorekeeper.Context;

internal static class ContextManuscriptFormatter
{
    public static string SerializeCurrentChapter(Chapter chapter, ManuscriptSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(chapter);

        if (snapshot is null)
        {
            return JsonSerializer.Serialize(new
            {
                complete = false,
                chapter = new { chapter.Id, chapter.Title },
                note = "No structured manuscript snapshot was available. Use the manuscript read tool before editing.",
                plainText = chapter.PlainText,
            }, ContextPayloadJson.Options);
        }

        return JsonSerializer.Serialize(new
        {
            complete = true,
            source = "persisted",
            note = "This is the authoritative active manuscript snapshot for this turn. Use its revision and stable block IDs directly for semantic edits. Read the manuscript again only if this snapshot is missing, incomplete, or stale.",
            chapter = new { chapter.Id, chapter.Title },
            manuscript = new
            {
                snapshot.Document.ManuscriptId,
                snapshot.Document.SchemaVersion,
                snapshot.Revision,
                snapshot.SourceHash,
                blockCount = snapshot.Document.Content.Count,
                blocks = snapshot.Document.Content
                    .Select((block, index) => BlockPayload(block, index))
                    .ToList(),
            },
        }, ContextPayloadJson.Options);
    }

    public static string SerializeStyles(
        IReadOnlyList<ManuscriptStyleView> styles,
        ManuscriptDocument? document)
    {
        ArgumentNullException.ThrowIfNull(styles);

        var roleCounts = document?.Content
            .GroupBy(block => block.StyleRole, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var directFormatting = document?.Content
            .Select((block, index) => (block, index))
            .Where(item => item.block.ParagraphPresentation is not null)
            .Select(item => new
            {
                item.block.Id,
                index = item.index,
                styleRole = item.block.StyleRole,
                paragraphPresentation = item.block.ParagraphPresentation,
            })
            .ToList();
        var specialBlocks = document?.Content
            .Select((block, index) => (block, index))
            .Where(item => item.block.Type != ManuscriptBlockType.Paragraph
                || !string.Equals(item.block.StyleRole, ManuscriptStyleRoles.Body, StringComparison.OrdinalIgnoreCase))
            .Select(item => new
            {
                item.block.Id,
                index = item.index,
                type = item.block.Type,
                styleRole = item.block.StyleRole,
                item.block.HeadingLevel,
                item.block.PageCompositionId,
            })
            .ToList();

        return JsonSerializer.Serialize(new
        {
            note = "Named styles define reusable typography. Block styleRole and direct paragraphPresentation in the active manuscript take precedence for the current chapter.",
            namedStyles = styles.Count == 0
                ? null
                : styles.Select(StylePayload).ToList(),
            roleCounts,
            directFormatting,
            specialBlocks,
            manuscriptSnapshotAvailable = document is not null,
        }, ContextPayloadJson.Options);
    }

    private static object StylePayload(ManuscriptStyleView style) => new
    {
        style.Id,
        style.Name,
        style.Kind,
        style.SemanticRole,
        style.Definition,
        style.Revision,
    };

    private static object BlockPayload(ManuscriptBlock block, int index) => new
    {
        index,
        block.Id,
        type = block.Type,
        styleRole = block.StyleRole,
        block.HeadingLevel,
        text = HasUnmarkedText(block) ? string.Concat(block.Content.Select(inline => inline.Text)) : null,
        inlines = HasMarkedContent(block)
            ? block.Content.Select(InlinePayload).ToList()
            : null,
        paragraphPresentation = block.ParagraphPresentation,
        imageId = block.Type == ManuscriptBlockType.Figure ? block.ImageId : null,
        altText = block.Type == ManuscriptBlockType.Figure && !block.Decorative ? block.AltText : null,
        decorative = block.Type == ManuscriptBlockType.Figure ? block.Decorative : (bool?)null,
        language = block.Type == ManuscriptBlockType.Figure ? block.Language : null,
        accessibilityRole = block.Type == ManuscriptBlockType.Figure ? block.AccessibilityRole : null,
        figurePresentation = block.Type == ManuscriptBlockType.Figure ? block.FigurePresentation : null,
        pageCompositionId = block.Type == ManuscriptBlockType.DesignedPage ? block.PageCompositionId : null,
    };

    private static object InlinePayload(ManuscriptInline inline) => new
    {
        inline.Text,
        marks = inline.Marks.Select(mark => new { mark.Type, mark.Value }).ToList(),
    };

    private static bool HasMarkedContent(ManuscriptBlock block) =>
        block.Content.Any(inline => inline.Marks.Count > 0);

    private static bool HasUnmarkedText(ManuscriptBlock block) =>
        block.Content.Count > 0 && !HasMarkedContent(block);
}
