namespace Lorekeeper.Manuscripts;

public static class ManuscriptRangeResolver
{
    public static IReadOnlyList<ManuscriptBlock> ResolveBlocks(
        ManuscriptDocument document,
        IReadOnlyList<ManuscriptRangeReference> references)
    {
        var blocks = document.Content.ToDictionary(block => block.Id, StringComparer.Ordinal);
        return references.Select(reference =>
        {
            if (!blocks.TryGetValue(reference.BlockId, out var block))
                throw new InvalidDataException($"Content reference '{reference.BlockId}' does not exist in the composition manuscript.");
            var text = ManuscriptCodec.Text(block);
            var (start, end) = ValidateRange(reference, text);
            var cursor = 0;
            var content = new List<ManuscriptInline>();
            foreach (var inline in block.Content)
            {
                var inlineStart = cursor;
                var inlineEnd = cursor + inline.Text.Length;
                cursor = inlineEnd;
                if (inline.Type != ManuscriptInlineType.Text && inlineStart == inlineEnd)
                {
                    // At a frame boundary, atoms belong to the following frame. The
                    // final frame also owns trailing atoms, including atom-only blocks.
                    if (ContainsAtom(start, end, text.Length, inlineStart))
                        content.Add(inline);
                    continue;
                }
                var sliceStart = Math.Max(start, inlineStart);
                var sliceEnd = Math.Min(end, inlineEnd);
                if (sliceEnd <= sliceStart) continue;
                content.Add(inline with { Text = inline.Text[(sliceStart - inlineStart)..(sliceEnd - inlineStart)] });
            }
            return block with { Content = content };
        }).ToList();
    }

    public static string ResolveText(
        ManuscriptDocument document,
        IReadOnlyList<ManuscriptRangeReference> references)
    {
        var blocks = document.Content.ToDictionary(block => block.Id, StringComparer.Ordinal);
        return string.Join(" ", references.Select(reference =>
        {
            if (!blocks.TryGetValue(reference.BlockId, out var block))
                throw new InvalidDataException($"Content reference '{reference.BlockId}' does not exist in the composition manuscript.");
            var text = ManuscriptCodec.Text(block);
            var (start, end) = ValidateRange(reference, text);
            return text[start..end];
        }));
    }

    public static IReadOnlyList<string> ValidateCoverage(
        ManuscriptDocument document,
        IEnumerable<IReadOnlyList<ManuscriptRangeReference>> referenceSets)
    {
        var blocks = document.Content.ToDictionary(block => block.Id, StringComparer.Ordinal);
        var rangesByBlock = new Dictionary<string, List<(int Start, int End)>>(StringComparer.Ordinal);
        foreach (var reference in referenceSets.SelectMany(references => references))
        {
            if (!blocks.TryGetValue(reference.BlockId, out var block))
                throw new InvalidDataException($"Content reference '{reference.BlockId}' does not exist in the composition manuscript.");
            var range = ValidateRange(reference, ManuscriptCodec.Text(block));
            var ranges = rangesByBlock.GetValueOrDefault(reference.BlockId);
            if (ranges is null)
            {
                ranges = [];
                rangesByBlock.Add(reference.BlockId, ranges);
            }
            if (ranges.Any(existing => range.Start < existing.End && existing.Start < range.End
                || range == existing))
                throw new InvalidDataException($"Composition content reference '{reference.BlockId}' overlaps another text frame binding.");
            ranges.Add(range);
        }

        var unplaced = new List<string>();
        foreach (var block in document.Content)
        {
            var text = ManuscriptCodec.Text(block);
            rangesByBlock.TryGetValue(block.Id, out var ranges);
            var covered = ranges?.OrderBy(range => range.Start).ToList() ?? [];
            var atomOffset = 0;
            foreach (var inline in block.Content)
            {
                if (inline.Type != ManuscriptInlineType.Text && inline.Text.Length == 0
                    && !covered.Any(range => ContainsAtom(range.Start, range.End, text.Length, atomOffset)))
                {
                    unplaced.Add(block.Id);
                    break;
                }
                atomOffset += inline.Text.Length;
            }
            if (string.IsNullOrWhiteSpace(text))
                continue;
            var cursor = 0;
            foreach (var range in covered)
            {
                if (!string.IsNullOrWhiteSpace(text[cursor..range.Start]))
                {
                    if (!unplaced.Contains(block.Id, StringComparer.Ordinal))
                        unplaced.Add(block.Id);
                    break;
                }
                cursor = range.End;
            }
            if (!unplaced.Contains(block.Id, StringComparer.Ordinal)
                && !string.IsNullOrWhiteSpace(text[cursor..]))
            {
                unplaced.Add(block.Id);
            }
        }
        return unplaced;
    }

    private static bool ContainsAtom(int start, int end, int textLength, int offset) =>
        offset >= start && (offset < end || offset == end && end == textLength);

    private static (int Start, int End) ValidateRange(ManuscriptRangeReference reference, string text)
    {
        var start = reference.StartOffset ?? 0;
        var end = reference.EndOffset ?? text.Length;
        if (text.Length == 0 && start == 0 && end == 0)
            return (0, 0);
        if (start < 0 || end <= start || end > text.Length)
            throw new InvalidDataException($"Content reference '{reference.BlockId}' has an invalid text range.");
        RequireUnicodeBoundary(text, start, reference.BlockId);
        RequireUnicodeBoundary(text, end, reference.BlockId);
        return (start, end);
    }

    private static void RequireUnicodeBoundary(string text, int offset, string blockId)
    {
        if (offset > 0 && offset < text.Length
            && char.IsHighSurrogate(text[offset - 1])
            && char.IsLowSurrogate(text[offset]))
        {
            throw new InvalidDataException($"Content reference '{blockId}' splits a Unicode surrogate pair.");
        }
    }
}
