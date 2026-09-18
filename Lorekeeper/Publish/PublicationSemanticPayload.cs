using Lorekeeper.Citations;
using Lorekeeper.Manuscripts;

namespace Lorekeeper.Publish;

internal static class PublicationSemanticPayload
{
    internal static object BlockPayload(
        ManuscriptBlock block,
        string topLevel,
        IReadOnlyList<string> placementPath,
        IReadOnlyDictionary<string, FormattedCitationCluster> citations) => new
    {
        id = block.Id,
        type = block.Type.ToString(),
        block.StyleRole,
        block.HeadingLevel,
        list = block.List,
        assetId = block.ImageId,
        caption = string.Concat(block.Content.Select(inline => InlineText(inline, topLevel, placementPath, citations))),
        block.Decorative,
        block.AltText,
        language = PublicationLanguage.NormalizeOptional(block.Language),
        accessibilityRole = block.AccessibilityRole.ToString(),
        presentation = block.FigurePresentation,
        paragraphPresentation = block.ParagraphPresentation,
        designedPageId = block.DesignedPageId,
        table = block.Table is null ? null : new
        {
            id = block.Table.Id,
            columnWidthWeights = block.Table.ColumnWidthWeights.ToArray(),
            block.Table.HeaderRowCount,
            rows = block.Table.Rows.Select(row => new
            {
                id = row.Id,
                cells = row.Cells.Select(cell => new
                {
                    id = cell.Id,
                    cell.RowSpan,
                    cell.ColumnSpan,
                    content = ManuscriptLists.Resolve(cell.Content).Select(child => BlockPayload(
                        child,
                        topLevel,
                        [.. placementPath, $"table:{block.Table.Id}", $"row:{row.Id}", $"cell:{cell.Id}"],
                        citations)).ToArray(),
                }).ToArray(),
            }).ToArray(),
        },
        content = block.Content.Select(inline => InlinePayload(inline, topLevel, placementPath, citations)).ToArray(),
    };

    internal static object NotePayload(
        ManuscriptNote note,
        string topLevel,
        IReadOnlyList<string> placementPath,
        IReadOnlyDictionary<string, FormattedCitationCluster> citations,
        int? number = null) => new
    {
        id = note.Id,
        kind = note.Kind.ToString(),
        number,
        content = ManuscriptLists.Resolve(note.Content).Select(block => BlockPayload(block, topLevel, placementPath, citations)).ToArray(),
    };

    private static object InlinePayload(
        ManuscriptInline inline,
        string topLevel,
        IReadOnlyList<string> placementPath,
        IReadOnlyDictionary<string, FormattedCitationCluster> citations)
    {
        var formatted = ResolveCitation(inline, topLevel, placementPath, citations);
        return new
        {
            inline.Id,
            type = inline.Type.ToString(),
            text = formatted?.InlineText ?? inline.Text,
            citationRuns = formatted?.InlineRuns,
            inline.NoteId,
            citationNoteNumber = formatted?.NoteNumber,
            marks = inline.Marks.Select(MarkPayload)
                .Concat(formatted?.NoteNumber is not null
                    ? [new { type = ManuscriptMarkType.Superscript.ToString(), value = (string?)null }]
                    : [])
                .ToArray(),
        };
    }

    private static object MarkPayload(ManuscriptMark mark) => new
    {
        type = mark.Type.ToString(),
        value = mark.Type == ManuscriptMarkType.Language
            ? PublicationLanguage.NormalizeOptional(mark.Value)
            : mark.Value,
    };

    private static string InlineText(
        ManuscriptInline inline,
        string topLevel,
        IReadOnlyList<string> placementPath,
        IReadOnlyDictionary<string, FormattedCitationCluster> citations) =>
        ResolveCitation(inline, topLevel, placementPath, citations)?.InlineText ?? inline.Text;

    private static FormattedCitationCluster? ResolveCitation(
        ManuscriptInline inline,
        string topLevel,
        IReadOnlyList<string> placementPath,
        IReadOnlyDictionary<string, FormattedCitationCluster> citations) =>
        inline.Type == ManuscriptInlineType.Citation && inline.Id is { } atomId
            ? citations.GetValueOrDefault(CitationPayloadKey(topLevel, placementPath, atomId))
                ?? throw new InvalidDataException($"Citation '{atomId}' has no effective publication occurrence.")
            : null;

    internal static string CitationPayloadKey(
        string topLevel,
        IReadOnlyList<string> placementPath,
        string atomId) => System.Text.Json.JsonSerializer.Serialize(new { topLevel, placementPath, atomId });

    internal static string CitationContainerTitle(PublishDocument document, string containerId)
    {
        if (containerId.StartsWith("chapter:", StringComparison.Ordinal)
            && Guid.TryParse(containerId["chapter:".Length..], out var chapterId))
        {
            return document.Sections.SelectMany(section => section.Chapters)
                .FirstOrDefault(chapter => chapter.Id == chapterId)?.Title ?? "Untitled chapter";
        }
        if (containerId.StartsWith("publication-section:", StringComparison.Ordinal)
            && Guid.TryParse(containerId["publication-section:".Length..], out var sectionId))
        {
            return document.PublicationSections.FirstOrDefault(section => section.Id == sectionId)?.Title
                ?? "Untitled publication section";
        }
        return "Untitled document";
    }

}
