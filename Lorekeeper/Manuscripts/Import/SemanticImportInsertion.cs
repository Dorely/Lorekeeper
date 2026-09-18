namespace Lorekeeper.Manuscripts.Import;

/// <summary>Inserts a fragment at an exact UTF-16 position without replacing existing content.</summary>
public static class SemanticImportInsertion
{
    public static ManuscriptDocument Insert(ManuscriptDocument source, ManuscriptPosition position,
        ManuscriptDocument fragment, string trailingBlockId)
    {
        ManuscriptCodec.Validate(fragment, fragment.ManuscriptId, fragment.Revision);
        var segment = ManuscriptTraversal.EnumerateText(source).SingleOrDefault(segment =>
            segment.Start.ContainerPath.SequenceEqual(position.ContainerPath, StringComparer.Ordinal)
            && (segment.Block.Id == position.BlockOrAtomId || segment.Block.Content.Any(inline => inline.Id == position.BlockOrAtomId)))
            ?? throw new InvalidDataException("The import position no longer exists.");
        if (position.DocumentId != source.ManuscriptId) throw new InvalidDataException("The import position belongs to another document.");
        var target = segment.Block;
        var offset = ResolveOffset(target, position);
        if (target.Type is ManuscriptBlockType.Figure or ManuscriptBlockType.SceneBreak or ManuscriptBlockType.DesignedPage)
            throw new InvalidDataException("Choose a text paragraph as the import insertion point.");
        var imported = ManuscriptClone.Document(fragment);
        var before = Slice(target.Content, 0, offset);
        var after = Slice(target.Content, offset, target.Content.Sum(inline => inline.Type == ManuscriptInlineType.Text ? inline.Text.Length : 1));
        var replacements = new List<ManuscriptBlock>();
        if (before.Count > 0) replacements.Add(target with { Content = before });
        replacements.AddRange(imported.Content);
        if (after.Count > 0) replacements.Add(target with { Id = trailingBlockId, Content = after });
        if (replacements.Count == 0)
            throw new InvalidDataException("The import contains no supported manuscript content.");
        List<ManuscriptBlock> Rewrite(IEnumerable<ManuscriptBlock> blocks) => blocks.SelectMany(block =>
        {
            if (block.Id == target.Id) return replacements;
            if (block.Table is not { } table) return new List<ManuscriptBlock> { block };
            return [block with { Table = table with { Rows = table.Rows.Select(row => row with
            {
                Cells = row.Cells.Select(cell => cell with { Content = Rewrite(cell.Content) }).ToList(),
            }).ToList() } }];
        }).ToList();
        var result = source with
        {
            Content = Rewrite(source.Content),
            Notes = [.. source.Notes.Select(note => note with { Content = Rewrite(note.Content) }), .. imported.Notes],
        };
        // This also rejects unsupported nested tables, nested note ownership, and identity collisions.
        ManuscriptCodec.Validate(result, source.ManuscriptId, source.Revision);
        return result;
    }

    private static int ResolveOffset(ManuscriptBlock block, ManuscriptPosition position)
    {
        var text = 0;
        var editor = 0;
        foreach (var inline in block.Content)
        {
            if (inline.Type != ManuscriptInlineType.Text)
            {
                if (inline.Id == position.BlockOrAtomId)
                {
                    if (text != position.Offset) throw new InvalidDataException("The import atom offset changed.");
                    return editor + (position.Affinity == ManuscriptPositionAffinity.After ? 1 : 0);
                }
                if (block.Id == position.BlockOrAtomId && text == position.Offset && position.Affinity == ManuscriptPositionAffinity.Before) return editor;
                editor++; continue;
            }
            if (block.Id == position.BlockOrAtomId && position.Offset >= text && position.Offset < text + inline.Text.Length)
            {
                var offset = position.Offset - text;
                if (offset > 0 && char.IsHighSurrogate(inline.Text[offset - 1]) && char.IsLowSurrogate(inline.Text[offset]))
                    throw new InvalidDataException("The import position splits a Unicode character.");
                return editor + offset;
            }
            editor += inline.Text.Length; text += inline.Text.Length;
        }
        if (block.Id == position.BlockOrAtomId && text == position.Offset) return editor;
        throw new InvalidDataException("The import position is outside the paragraph.");
    }

    private static List<ManuscriptInline> Slice(IEnumerable<ManuscriptInline> content, int start, int end)
    {
        var result = new List<ManuscriptInline>();
        var offset = 0;
        foreach (var inline in content)
        {
            var length = inline.Type == ManuscriptInlineType.Text ? inline.Text.Length : 1;
            var from = Math.Max(start, offset);
            var to = Math.Min(end, offset + length);
            if (from < to)
                result.Add(inline.Type == ManuscriptInlineType.Text
                    ? inline with { Text = inline.Text[(from - offset)..(to - offset)] }
                    : inline);
            offset += length;
        }
        return result;
    }
}
