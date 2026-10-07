namespace Lorekeeper.Manuscripts;

public sealed record ManuscriptDiagnostic(
    string Severity,
    string Code,
    string Message,
    string? BlockId = null);

public sealed record ManuscriptInspectionResult(
    bool IsValid,
    IReadOnlyList<ManuscriptDiagnostic> Diagnostics,
    int DiagnosticCount,
    IReadOnlyList<ManuscriptBlock> Matches,
    int MatchCount,
    int Start,
    bool HasMore);

public static class ManuscriptInspection
{
    public static ManuscriptInspectionResult Inspect(
        ManuscriptDocument document,
        string? query = null,
        string? blockType = null,
        string? styleRole = null,
        int start = 0,
        int count = 40)
    {
        start = Math.Max(0, start);
        count = Math.Clamp(count, 1, 40);
        var diagnostics = new List<ManuscriptDiagnostic>();
        try
        {
            ManuscriptCodec.Validate(document, document.ManuscriptId, document.Revision);
        }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(new ManuscriptDiagnostic(
                "error",
                "invalid-schema",
                exception.Message));
        }

        ManuscriptBlockType? parsedType = null;
        if (!string.IsNullOrWhiteSpace(blockType))
        {
            if (Enum.TryParse<ManuscriptBlockType>(blockType, ignoreCase: true, out var value))
            {
                parsedType = value;
            }
            else
            {
                diagnostics.Add(new ManuscriptDiagnostic(
                    "error",
                    "invalid-block-type-filter",
                    $"Block type must be one of: {string.Join(", ", Enum.GetNames<ManuscriptBlockType>())}."));
            }
        }

        foreach (var block in document.Content)
        {
            // Scene breaks, figures, Designed Page placements and tables carry no
            // flowing inline text, so an empty Content is their valid state.
            if (block.Type is ManuscriptBlockType.Paragraph or ManuscriptBlockType.Heading
                    or ManuscriptBlockType.BlockQuote or ManuscriptBlockType.ListItem
                && string.IsNullOrEmpty(ManuscriptCodec.Text(block)))
            {
                diagnostics.Add(new ManuscriptDiagnostic(
                    "warning",
                    "empty-text-block",
                    "Text block is empty.",
                    block.Id));
            }

            if (block.Content.Zip(block.Content.Skip(1)).Any(pair =>
                    MarksEqual(pair.First.Marks, pair.Second.Marks)))
            {
                diagnostics.Add(new ManuscriptDiagnostic(
                    "info",
                    "merge-adjacent-inline-runs",
                    "Adjacent inline runs use identical marks and can be normalized.",
                    block.Id));
            }

            foreach (var inline in block.Content)
            {
                foreach (var mark in inline.Marks)
                {
                    if (mark.Type is ManuscriptMarkType.Link
                        or ManuscriptMarkType.Language
                        or ManuscriptMarkType.CharacterStyle
                        && string.IsNullOrWhiteSpace(mark.Value))
                    {
                        diagnostics.Add(new ManuscriptDiagnostic(
                            "error",
                            "missing-mark-value",
                            $"{mark.Type} requires a value.",
                            block.Id));
                    }
                }

                if (inline.Marks.Any(mark => mark.Type == ManuscriptMarkType.Superscript)
                    && inline.Marks.Any(mark => mark.Type == ManuscriptMarkType.Subscript))
                {
                    diagnostics.Add(new ManuscriptDiagnostic(
                        "error",
                        "conflicting-baseline-marks",
                        "Text cannot be both superscript and subscript.",
                        block.Id));
                }
            }
        }

        var allMatches = document.Content
            .Where(block => parsedType is null || block.Type == parsedType)
            .Where(block => string.IsNullOrWhiteSpace(styleRole)
                || string.Equals(block.StyleRole, styleRole, StringComparison.OrdinalIgnoreCase))
            .Where(block => string.IsNullOrWhiteSpace(query)
                || ManuscriptCodec.Text(block).Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var matches = allMatches.Skip(start).Take(count).ToList();

        return new ManuscriptInspectionResult(
            diagnostics.All(diagnostic => diagnostic.Severity != "error"),
            diagnostics.Take(40).ToList(),
            diagnostics.Count,
            matches,
            allMatches.Count,
            start,
            start + matches.Count < allMatches.Count);
    }

    private static bool MarksEqual(
        IReadOnlyList<ManuscriptMark> left,
        IReadOnlyList<ManuscriptMark> right) =>
        left.Count == right.Count
        && left.OrderBy(mark => mark.Type).ThenBy(mark => mark.Value, StringComparer.Ordinal)
            .SequenceEqual(right.OrderBy(mark => mark.Type).ThenBy(mark => mark.Value, StringComparer.Ordinal));
}
