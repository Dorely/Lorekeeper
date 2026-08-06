using Lorekeeper.Models;

namespace Lorekeeper.Manuscripts;

public static class ManuscriptStyleTemplateExtractor
{
    public static bool SupportsParagraphStyle(ManuscriptBlock block) =>
        block.Type is ManuscriptBlockType.Paragraph
            or ManuscriptBlockType.Heading
            or ManuscriptBlockType.BlockQuote
            or ManuscriptBlockType.ListItem;

    public static ManuscriptStyleProperties Extract(
        ManuscriptBlock block,
        IReadOnlyList<ManuscriptStyleView> styles)
    {
        if (!SupportsParagraphStyle(block))
            throw new InvalidOperationException("Choose a paragraph, heading, block quote, or list item to create a Book Text Style.");

        var inherited = styles.FirstOrDefault(style =>
                style.Kind == ManuscriptStyleKind.Paragraph
                && string.Equals(style.SemanticRole, block.StyleRole, StringComparison.OrdinalIgnoreCase))
            ?.Definition ?? new ManuscriptStyleProperties();
        var presentation = block.ParagraphPresentation;
        var definition = inherited with
        {
            FontFamilyKey = presentation?.FontFamilyKey ?? inherited.FontFamilyKey,
            FontSizePoints = presentation?.FontSizePoints ?? inherited.FontSizePoints,
            FontWeight = presentation?.FontWeight ?? inherited.FontWeight,
            Italic = presentation?.Italic ?? inherited.Italic,
            SmallCaps = presentation?.SmallCaps ?? inherited.SmallCaps,
            LineHeight = presentation?.LineHeight ?? inherited.LineHeight,
            TextAlign = presentation?.Alignment switch
            {
                ParagraphAlignment.Start => "left",
                ParagraphAlignment.Center => "center",
                ParagraphAlignment.End => "right",
                ParagraphAlignment.Justify => "justify",
                _ => inherited.TextAlign,
            },
            LeftIndentEm = presentation?.LeftIndentEm ?? inherited.LeftIndentEm,
            RightIndentEm = presentation?.RightIndentEm ?? inherited.RightIndentEm,
            FirstLineIndentEm = presentation?.FirstLineIndentEm ?? inherited.FirstLineIndentEm,
            SpaceBeforePoints = presentation?.SpacingBeforePoints ?? inherited.SpaceBeforePoints,
            SpaceAfterPoints = presentation?.SpacingAfterPoints ?? inherited.SpaceAfterPoints,
            KeepWithNext = presentation?.KeepWithNext ?? inherited.KeepWithNext,
            StartOnNewPage = presentation?.StartOnNewPage ?? inherited.StartOnNewPage,
        };

        var textRuns = block.Content
            .Where(inline => !string.IsNullOrWhiteSpace(inline.Text))
            .ToList();
        if (textRuns.Count > 0 && textRuns.All(inline => HasMark(inline, ManuscriptMarkType.Strong)))
            definition = definition with { FontWeight = 700 };
        if (textRuns.Count > 0 && textRuns.All(inline => HasMark(inline, ManuscriptMarkType.Emphasis)))
            definition = definition with { Italic = true };
        if (textRuns.Count > 0 && textRuns.All(inline => HasMark(inline, ManuscriptMarkType.SmallCaps)))
            definition = definition with { SmallCaps = true };

        return ManuscriptStyleService.NormalizeDefinition(definition);
    }

    public static IReadOnlyList<ManuscriptOperation> BuildApplyOperations(
        ManuscriptDocument document,
        string semanticRole,
        IReadOnlyCollection<string>? blockIds)
    {
        var selectedIds = blockIds is { Count: > 0 }
            ? blockIds
                .Select(blockId => ResolveBlock(document.Content, blockId).Id)
                .ToHashSet(StringComparer.Ordinal)
            : null;

        var targets = document.Content
            .Where(block => (selectedIds is null || selectedIds.Contains(block.Id)) && SupportsParagraphStyle(block))
            .ToList();
        if (selectedIds is not null && targets.Count != selectedIds.Count)
            throw new InvalidOperationException("Book Text Styles can be applied only to paragraphs, headings, block quotes, and list items.");
        if (targets.Count == 0)
            throw new InvalidOperationException("No compatible manuscript paragraphs were found.");

        return targets.SelectMany<ManuscriptBlock, ManuscriptOperation>(block =>
            [
                new SetManuscriptBlockStyle(block.Id, semanticRole),
                new SetParagraphPresentation(block.Id, null),
            ]).ToList();
    }

    private static ManuscriptBlock ResolveBlock(IReadOnlyList<ManuscriptBlock> blocks, string blockId)
    {
        try
        {
            return ManuscriptOperations.FindBlock(blocks, blockId);
        }
        catch (KeyNotFoundException exception)
        {
            throw new InvalidOperationException($"Manuscript block '{blockId}' was not found.", exception);
        }
    }

    private static bool HasMark(ManuscriptInline inline, ManuscriptMarkType type) =>
        inline.Marks.Any(mark => mark.Type == type);
}
