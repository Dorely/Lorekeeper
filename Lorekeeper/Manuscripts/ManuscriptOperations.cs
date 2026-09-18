namespace Lorekeeper.Manuscripts;

public static class ManuscriptOperations
{
    public static (ManuscriptDocument Document, IReadOnlyList<string> ChangedBlockIds) Apply(
        ManuscriptDocument source,
        IReadOnlyList<ManuscriptOperation> operations)
    {
        var blocks = source.Content.Select(Clone).ToList();
        var notes = source.Notes.Select(ManuscriptClone.Note).ToList();
        var originallyReferencedNotes = ReferencedNoteIds(blocks);
        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in operations)
        {
            switch (operation)
            {
                case ReplaceManuscriptInlineContent replaceInline:
                    if (replaceInline.Position.Offset != 0)
                        throw new InvalidDataException("Inline-content replacement must address the start of its block.");
                    var currentDocument = source with { Content = blocks, Notes = notes };
                    var target = ManuscriptTraversal.Resolve(currentDocument, replaceInline.Position).Block;
                    var replacement = ManuscriptClone.Block(target with { Content = replaceInline.Content.ToList() });
                    blocks = ReplaceInlineBlock(blocks, target.Id, replacement);
                    notes = notes.Select(note => note with { Content = ReplaceInlineBlock(note.Content, target.Id, replacement) }).ToList();
                    changed.Add(target.Id);
                    break;

                case PutRichManuscriptBlock put:
                    if (put.Index < 0 || put.Index > blocks.Count)
                        throw new ArgumentOutOfRangeException(nameof(put.Index));
                    var richBlock = ManuscriptClone.Block(put.Block);
                    var richExisting = blocks.FindIndex(block => AreEquivalentBlockIds(block.Id, richBlock.Id));
                    if (richExisting >= 0)
                        blocks.RemoveAt(richExisting);
                    blocks.Insert(Math.Clamp(put.Index, 0, blocks.Count), richBlock);
                    changed.Add(richBlock.Id);
                    break;

                case ReplaceManuscriptNotes replaceNotes:
                    notes = replaceNotes.Notes.Select(ManuscriptClone.Note).ToList();
                    foreach (var note in notes)
                        changed.Add(note.Id);
                    break;

                case ReplaceManuscriptStructure replaceStructure:
                    blocks = replaceStructure.Content.Select(ManuscriptClone.Block).ToList();
                    notes = replaceStructure.Notes.Select(ManuscriptClone.Note).ToList();
                    foreach (var block in blocks)
                        changed.Add(block.Id);
                    foreach (var note in notes)
                        changed.Add(note.Id);
                    break;

                case InsertManuscriptBlock insert:
                    if (insert.Index < 0 || insert.Index > blocks.Count)
                        throw new ArgumentOutOfRangeException(nameof(insert.Index));
                    ValidateBlockText(insert.Type, insert.Text);
                    var inserted = NewBlock(
                        insert.Type,
                        insert.Text,
                        insert.StyleRole,
                        insert.ImageId,
                        insert.AltText,
                        insert.HeadingLevel,
                        insert.Decorative,
                        insert.FigurePresentation,
                        insert.DesignedPageId,
                        insert.Language,
                        insert.AccessibilityRole,
                        insert.BlockId);
                    if (blocks.Any(block => string.Equals(block.Id, inserted.Id, StringComparison.Ordinal)))
                        throw new InvalidOperationException($"Manuscript block {inserted.Id} already exists.");
                    blocks.Insert(insert.Index, inserted);
                    changed.Add(inserted.Id);
                    break;

                case ReplaceManuscriptBlockText replace:
                    var replaceIndex = Find(blocks, replace.BlockId);
                    var replacedBlockId = blocks[replaceIndex].Id;
                    RequireTextBlock(blocks[replaceIndex]);
                    ValidateBlockText(blocks[replaceIndex].Type, replace.Text);
                    blocks[replaceIndex] = blocks[replaceIndex] with
                    {
                        Content = ReplaceTextPreservingMarks(blocks[replaceIndex], replace.Text),
                    };
                    changed.Add(replacedBlockId);
                    break;

                case DeleteManuscriptBlock delete:
                    var deleteIndex = Find(blocks, delete.BlockId);
                    var deletedBlockId = blocks[deleteIndex].Id;
                    blocks.RemoveAt(deleteIndex);
                    changed.Add(deletedBlockId);
                    break;

                case MoveManuscriptBlock move:
                    var moveIndex = Find(blocks, move.BlockId);
                    if (move.TargetIndex < 0 || move.TargetIndex >= blocks.Count)
                        throw new ArgumentOutOfRangeException(nameof(move.TargetIndex));
                    var moved = blocks[moveIndex];
                    blocks.RemoveAt(moveIndex);
                    blocks.Insert(move.TargetIndex, moved);
                    changed.Add(moved.Id);
                    break;

                case SplitManuscriptBlock split:
                    var splitIndex = Find(blocks, split.BlockId);
                    var splitBlock = blocks[splitIndex];
                    RequireTextBlock(splitBlock);
                    if (splitBlock.Type == ManuscriptBlockType.Figure)
                        throw new InvalidOperationException("Figure blocks cannot be split; edit the caption instead.");
                    var splitText = ManuscriptCodec.Text(splitBlock);
                    if (split.Offset < 0 || split.Offset > splitText.Length)
                        throw new ArgumentOutOfRangeException(nameof(split.Offset));
                    RequireUnicodeBoundary(splitText, split.Offset, nameof(split.Offset));
                    blocks[splitIndex] = splitBlock with
                    {
                        Content = SliceContent(splitBlock.Content, 0, split.Offset),
                    };
                    var tail = NewBlock(
                        splitBlock.Type,
                        SliceContent(splitBlock.Content, split.Offset, splitText.Length),
                        splitBlock.StyleRole,
                        headingLevel: splitBlock.HeadingLevel);
                    tail = tail with { List = splitBlock.List is { } list ? list with { Start = null } : null };
                    blocks.Insert(splitIndex + 1, tail);
                    changed.Add(splitBlock.Id);
                    changed.Add(tail.Id);
                    break;

                case MergeManuscriptBlocks merge:
                    var firstIndex = Find(blocks, merge.FirstBlockId);
                    var secondIndex = Find(blocks, merge.SecondBlockId);
                    var firstBlockId = blocks[firstIndex].Id;
                    var secondBlockId = blocks[secondIndex].Id;
                    if (secondIndex != firstIndex + 1)
                        throw new InvalidOperationException("Only adjacent blocks can be merged.");
                    RequireTextBlock(blocks[firstIndex]);
                    RequireTextBlock(blocks[secondIndex]);
                    if (blocks[firstIndex].Type == ManuscriptBlockType.Figure
                        || blocks[secondIndex].Type == ManuscriptBlockType.Figure)
                    {
                        throw new InvalidOperationException(
                            "Figure blocks cannot be merged because their image metadata must remain attached.");
                    }
                    if (blocks[firstIndex].Type != blocks[secondIndex].Type
                        || blocks[firstIndex].HeadingLevel != blocks[secondIndex].HeadingLevel
                        || !string.Equals(
                            blocks[firstIndex].StyleRole,
                            blocks[secondIndex].StyleRole,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "Blocks must have the same type, heading level, and style role before merging.");
                    }
                    var mergedContent = new List<ManuscriptInline>();
                    foreach (var inline in blocks[firstIndex].Content.Concat(blocks[secondIndex].Content))
                        AppendInline(mergedContent, Clone(inline));
                    blocks[firstIndex] = blocks[firstIndex] with
                    {
                        Content = mergedContent,
                    };
                    blocks.RemoveAt(secondIndex);
                    changed.Add(firstBlockId);
                    changed.Add(secondBlockId);
                    break;

                case SetManuscriptBlockType blockType:
                    var typeIndex = Find(blocks, blockType.BlockId);
                    var current = blocks[typeIndex];
                    var typeIsUnchanged = blockType.Type == current.Type;
                    if (blockType.Type is ManuscriptBlockType.SceneBreak or ManuscriptBlockType.DesignedPage or ManuscriptBlockType.Table
                        && !string.IsNullOrEmpty(ManuscriptCodec.Text(current)))
                    {
                        throw new InvalidOperationException(
                            "A text block must be empty before it can become a non-flowing block.");
                    }
                    blocks[typeIndex] = current with
                    {
                        Type = blockType.Type,
                        StyleRole = string.IsNullOrWhiteSpace(blockType.StyleRole)
                            ? typeIsUnchanged
                                ? current.StyleRole
                                : DefaultStyle(blockType.Type)
                            : blockType.StyleRole.Trim(),
                        Content = blockType.Type is ManuscriptBlockType.SceneBreak or ManuscriptBlockType.DesignedPage or ManuscriptBlockType.Table
                            ? []
                            : current.Content,
                        ImageId = blockType.Type == ManuscriptBlockType.Figure
                            ? blockType.ImageId ?? (typeIsUnchanged ? current.ImageId : null)
                            : null,
                        AltText = blockType.Type == ManuscriptBlockType.Figure
                            ? blockType.AltText?.Trim() ?? (typeIsUnchanged ? current.AltText : null)
                            : null,
                        Decorative = blockType.Type == ManuscriptBlockType.Figure
                            && (blockType.Decorative || typeIsUnchanged && current.Decorative),
                        FigurePresentation = blockType.Type == ManuscriptBlockType.Figure
                            ? blockType.FigurePresentation
                                ?? (typeIsUnchanged ? current.FigurePresentation : new FigurePresentation())
                            : null,
                        Language = blockType.Type == ManuscriptBlockType.Figure
                            ? blockType.Language?.Trim() ?? (typeIsUnchanged ? current.Language : null)
                            : null,
                        AccessibilityRole = blockType.Type == ManuscriptBlockType.Figure
                            ? blockType.AccessibilityRole
                            : null,
                        DesignedPageId = blockType.Type == ManuscriptBlockType.DesignedPage
                            ? blockType.DesignedPageId
                                ?? (typeIsUnchanged ? current.DesignedPageId : null)
                            : null,
                        Table = blockType.Type == ManuscriptBlockType.Table && typeIsUnchanged
                            ? current.Table
                            : null,
                        List = blockType.Type == ManuscriptBlockType.ListItem ? current.List : null,
                        HeadingLevel = blockType.Type == ManuscriptBlockType.Heading
                            ? blockType.HeadingLevel ?? current.HeadingLevel ?? 2
                            : null,
                    };
                    changed.Add(current.Id);
                    break;

                case SetManuscriptBlockStyle style:
                    if (string.IsNullOrWhiteSpace(style.StyleRole))
                        throw new ArgumentException("Style role is required.", nameof(style.StyleRole));
                    var styleIndex = Find(blocks, style.BlockId);
                    var styledBlockId = blocks[styleIndex].Id;
                    blocks[styleIndex] = blocks[styleIndex] with { StyleRole = style.StyleRole.Trim() };
                    changed.Add(styledBlockId);
                    break;

                case SetManuscriptInlineMark mark:
                    ApplyMark(blocks, mark);
                    changed.Add(blocks[Find(blocks, mark.BlockId)].Id);
                    break;

                case SetFigurePresentation figure:
                    var figureIndex = Find(blocks, figure.BlockId);
                    var currentFigure = blocks[figureIndex];
                    if (currentFigure.Type != ManuscriptBlockType.Figure)
                        throw new InvalidOperationException($"Block {figure.BlockId} is not a Figure.");
                    blocks[figureIndex] = currentFigure with
                    {
                        ImageId = figure.ImageId,
                        AltText = figure.Decorative ? null : figure.AltText?.Trim(),
                        Decorative = figure.Decorative,
                        Language = string.IsNullOrWhiteSpace(figure.Language) ? null : figure.Language.Trim(),
                        FigurePresentation = figure.Presentation,
                        AccessibilityRole = figure.AccessibilityRole,
                    };
                    changed.Add(currentFigure.Id);
                    break;

                case SetParagraphPresentation paragraph:
                    var paragraphIndex = Find(blocks, paragraph.BlockId);
                    var currentParagraph = blocks[paragraphIndex];
                    if (currentParagraph.Type is ManuscriptBlockType.SceneBreak or ManuscriptBlockType.Figure or ManuscriptBlockType.DesignedPage or ManuscriptBlockType.Table)
                        throw new InvalidOperationException($"Block {paragraph.BlockId} does not support paragraph formatting.");
                    blocks[paragraphIndex] = currentParagraph with { ParagraphPresentation = paragraph.Presentation };
                    changed.Add(currentParagraph.Id);
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported manuscript operation {operation.GetType().Name}.");
            }
        }

        var referencedNotes = ReferencedNoteIds(blocks);
        notes.RemoveAll(note => originallyReferencedNotes.Contains(note.Id)
            && !referencedNotes.Contains(note.Id));
        foreach (var removedNoteId in originallyReferencedNotes.Except(referencedNotes, StringComparer.Ordinal))
            changed.Add(removedNoteId);

        var result = source with
        {
            Revision = checked(source.Revision + 1),
            Content = blocks,
            Notes = notes,
        };
        ManuscriptCodec.Validate(result, source.ManuscriptId, result.Revision);
        return (result, changed.ToList());
    }

    private static List<ManuscriptBlock> ReplaceInlineBlock(IEnumerable<ManuscriptBlock> blocks, string id, ManuscriptBlock replacement) =>
        blocks.Select(block => block.Id == id ? replacement : block.Table is not { } table ? block : block with
        {
            Table = table with
            {
                Rows = table.Rows.Select(row => row with
                {
                    Cells = row.Cells.Select(cell => cell with { Content = ReplaceInlineBlock(cell.Content, id, replacement) }).ToList(),
                }).ToList(),
            },
        }).ToList();

    private static HashSet<string> ReferencedNoteIds(IEnumerable<ManuscriptBlock> blocks)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        AddReferencedNoteIds(blocks, result);
        return result;
    }

    private static void AddReferencedNoteIds(
        IEnumerable<ManuscriptBlock> blocks,
        ISet<string> result)
    {
        foreach (var block in blocks)
        {
            foreach (var inline in block.Content)
            {
                if (inline.Type == ManuscriptInlineType.NoteReference
                    && !string.IsNullOrWhiteSpace(inline.NoteId))
                {
                    result.Add(inline.NoteId);
                }
            }
            if (block.Table is not { } table)
                continue;
            foreach (var row in table.Rows)
            foreach (var cell in row.Cells)
                AddReferencedNoteIds(cell.Content, result);
        }
    }

    public static List<ManuscriptInline> ReplaceTextPreservingMarks(
        ManuscriptBlock block,
        string replacement)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(replacement);
        var source = block.Content;
        if (source.Count == 0)
            return [new ManuscriptInline { Text = replacement }];

        var sourceLength = source.Sum(inline => inline.Text.Length);
        if (sourceLength == 0)
            return [new ManuscriptInline { Text = replacement, Marks = source[0].Marks.ToList() }];

        var result = new List<ManuscriptInline>(source.Count);
        var consumed = 0;
        for (var index = 0; index < source.Count; index++)
        {
            var start = index == 0
                ? 0
                : (int)Math.Round((double)consumed * replacement.Length / sourceLength, MidpointRounding.AwayFromZero);
            consumed += source[index].Text.Length;
            var end = index == source.Count - 1
                ? replacement.Length
                : (int)Math.Round((double)consumed * replacement.Length / sourceLength, MidpointRounding.AwayFromZero);
            if (end < start)
                end = start;
            result.Add(new ManuscriptInline
            {
                Text = replacement[start..end],
                Marks = source[index].Marks.ToList(),
            });
        }

        return result;
    }

    private static void ApplyMark(List<ManuscriptBlock> blocks, SetManuscriptInlineMark operation)
    {
        var index = Find(blocks, operation.BlockId);
        var block = blocks[index];
        RequireTextBlock(block);
        var text = ManuscriptCodec.Text(block);
        if (operation.StartOffset < 0
            || operation.EndOffset <= operation.StartOffset
            || operation.EndOffset > text.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(operation.StartOffset));
        }
        RequireUnicodeBoundary(text, operation.StartOffset, nameof(operation.StartOffset));
        RequireUnicodeBoundary(text, operation.EndOffset, nameof(operation.EndOffset));

        var content = new List<ManuscriptInline>();
        var cursor = 0;
        foreach (var inline in block.Content)
        {
            var inlineStart = cursor;
            var inlineEnd = cursor + inline.Text.Length;
            var boundaries = new[]
            {
                inlineStart,
                Math.Clamp(operation.StartOffset, inlineStart, inlineEnd),
                Math.Clamp(operation.EndOffset, inlineStart, inlineEnd),
                inlineEnd,
            }.Distinct().Order().ToList();
            for (var boundary = 0; boundary < boundaries.Count - 1; boundary++)
            {
                var start = boundaries[boundary];
                var end = boundaries[boundary + 1];
                if (start == end)
                    continue;
                var marks = inline.Marks.Select(mark => mark with { }).ToList();
                if (start >= operation.StartOffset && end <= operation.EndOffset)
                {
                    marks.RemoveAll(mark => mark.Type == operation.Mark);
                    if (operation.Enabled)
                        marks.Add(new ManuscriptMark { Type = operation.Mark, Value = operation.Value });
                }
                AppendInline(
                    content,
                    new ManuscriptInline
                    {
                        Text = inline.Text[(start - inlineStart)..(end - inlineStart)],
                        Marks = marks,
                    });
            }
            cursor = inlineEnd;
        }
        blocks[index] = block with { Content = content };
    }

    private static void AppendInline(List<ManuscriptInline> content, ManuscriptInline inline)
    {
        if (inline.Text.Length == 0)
            return;
        if (content.LastOrDefault() is { } previous && MarksEqual(previous.Marks, inline.Marks))
        {
            content[^1] = previous with { Text = previous.Text + inline.Text };
            return;
        }
        content.Add(inline);
    }

    private static bool MarksEqual(
        IReadOnlyList<ManuscriptMark> left,
        IReadOnlyList<ManuscriptMark> right) =>
        left.Count == right.Count
        && left.OrderBy(mark => mark.Type).ThenBy(mark => mark.Value, StringComparer.Ordinal)
            .SequenceEqual(right.OrderBy(mark => mark.Type).ThenBy(mark => mark.Value, StringComparer.Ordinal));

    private static void ValidateBlockText(ManuscriptBlockType type, string text)
    {
        if (type is ManuscriptBlockType.SceneBreak or ManuscriptBlockType.DesignedPage or ManuscriptBlockType.Table)
        {
            if (!string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("Non-flowing blocks cannot contain text.", nameof(text));
            return;
        }
        if (ManuscriptCodec.ContainsBlockDelimiter(text))
            throw new ArgumentException("A single manuscript block cannot contain a paragraph delimiter.", nameof(text));
    }

    private static void RequireUnicodeBoundary(string text, int offset, string parameterName)
    {
        if (offset > 0
            && offset < text.Length
            && char.IsHighSurrogate(text[offset - 1])
            && char.IsLowSurrogate(text[offset]))
        {
            throw new ArgumentOutOfRangeException(parameterName, "The offset splits a Unicode surrogate pair.");
        }
    }

    private static ManuscriptBlock NewBlock(
        ManuscriptBlockType type,
        string text,
        string? styleRole,
        Guid? imageId = null,
        string? altText = null,
        int? headingLevel = null,
        bool decorative = false,
        FigurePresentation? figurePresentation = null,
        Guid? designedPageId = null,
        string? language = null,
        FigureAccessibilityRole accessibilityRole = FigureAccessibilityRole.Figure,
        string? blockId = null) =>
        NewBlock(
            type,
            type is ManuscriptBlockType.SceneBreak or ManuscriptBlockType.DesignedPage or ManuscriptBlockType.Table
                ? []
                : [new ManuscriptInline { Text = text }],
            styleRole,
            imageId,
            altText,
            headingLevel,
            decorative,
            figurePresentation,
            designedPageId,
            language,
            accessibilityRole,
            blockId);

    private static ManuscriptBlock NewBlock(
        ManuscriptBlockType type,
        List<ManuscriptInline> content,
        string? styleRole,
        Guid? imageId = null,
        string? altText = null,
        int? headingLevel = null,
        bool decorative = false,
        FigurePresentation? figurePresentation = null,
        Guid? designedPageId = null,
        string? language = null,
        FigureAccessibilityRole accessibilityRole = FigureAccessibilityRole.Figure,
        string? blockId = null) =>
        new()
        {
            Id = string.IsNullOrWhiteSpace(blockId) ? Guid.NewGuid().ToString("N") : blockId,
            Type = type,
            StyleRole = string.IsNullOrWhiteSpace(styleRole) ? DefaultStyle(type) : styleRole.Trim(),
            Content = type is ManuscriptBlockType.SceneBreak or ManuscriptBlockType.DesignedPage or ManuscriptBlockType.Table
                ? []
                : content,
            ImageId = type == ManuscriptBlockType.Figure ? imageId : null,
            AltText = type == ManuscriptBlockType.Figure ? altText?.Trim() : null,
            Decorative = type == ManuscriptBlockType.Figure && decorative,
            Language = type == ManuscriptBlockType.Figure && !string.IsNullOrWhiteSpace(language) ? language.Trim() : null,
            AccessibilityRole = type == ManuscriptBlockType.Figure ? accessibilityRole : null,
            FigurePresentation = type == ManuscriptBlockType.Figure
                ? figurePresentation ?? new FigurePresentation()
                : null,
            DesignedPageId = type == ManuscriptBlockType.DesignedPage ? designedPageId : null,
            HeadingLevel = type == ManuscriptBlockType.Heading ? headingLevel ?? 2 : null,
        };

    private static List<ManuscriptInline> SliceContent(
        IReadOnlyList<ManuscriptInline> content,
        int start,
        int end)
    {
        var result = new List<ManuscriptInline>();
        var cursor = 0;
        foreach (var inline in content)
        {
            var inlineStart = cursor;
            var inlineEnd = cursor + inline.Text.Length;
            var overlapStart = Math.Max(start, inlineStart);
            var overlapEnd = Math.Min(end, inlineEnd);
            if (overlapStart < overlapEnd)
            {
                AppendInline(
                    result,
                    new ManuscriptInline
                    {
                        Text = inline.Text[(overlapStart - inlineStart)..(overlapEnd - inlineStart)],
                        Marks = inline.Marks.Select(mark => mark with { }).ToList(),
                    });
            }
            cursor = inlineEnd;
        }

        return result;
    }

    private static ManuscriptInline Clone(ManuscriptInline inline) =>
        inline with
        {
            Marks = inline.Marks.Select(mark => mark with { }).ToList(),
        };

    private static ManuscriptBlock Clone(ManuscriptBlock block) => ManuscriptClone.Block(block);

    internal static ManuscriptBlock FindBlock(IReadOnlyList<ManuscriptBlock> blocks, string blockId) =>
        blocks[Find(blocks, blockId)];

    internal static bool AreEquivalentBlockIds(string storedBlockId, string requestedBlockId)
    {
        if (string.Equals(storedBlockId, requestedBlockId, StringComparison.Ordinal))
            return true;

        return TryParseCanonicalGuid(storedBlockId, out var storedGuid)
            && TryParseCanonicalGuid(requestedBlockId, out var requestedGuid)
            && storedGuid == requestedGuid;
    }

    private static int Find(IReadOnlyList<ManuscriptBlock> blocks, string blockId)
    {
        for (var index = 0; index < blocks.Count; index++)
        {
            if (string.Equals(blocks[index].Id, blockId, StringComparison.Ordinal))
                return index;
        }

        if (!TryParseCanonicalGuid(blockId, out _))
            throw new KeyNotFoundException($"Manuscript block {blockId} was not found.");

        int? matchingIndex = null;
        for (var index = 0; index < blocks.Count; index++)
        {
            if (!AreEquivalentBlockIds(blocks[index].Id, blockId))
            {
                continue;
            }

            if (matchingIndex.HasValue)
            {
                throw new InvalidOperationException(
                    $"Manuscript block {blockId} is ambiguous because more than one stored block has that GUID.");
            }

            matchingIndex = index;
        }

        return matchingIndex
            ?? throw new KeyNotFoundException($"Manuscript block {blockId} was not found.");
    }

    private static bool TryParseCanonicalGuid(string value, out Guid result) =>
        Guid.TryParseExact(value, "N", out result)
        || Guid.TryParseExact(value, "D", out result);

    private static void RequireTextBlock(ManuscriptBlock block)
    {
        if (block.Type is ManuscriptBlockType.SceneBreak or ManuscriptBlockType.DesignedPage or ManuscriptBlockType.Table)
            throw new InvalidOperationException($"Block {block.Id} does not contain directly editable text.");
        if (block.Content.Any(inline => inline.Type != ManuscriptInlineType.Text))
            throw new InvalidOperationException($"Block {block.Id} contains semantic atoms. Use ReplaceInlineContent at its full position and preserve unrelated atoms.");
    }

    private static string DefaultStyle(ManuscriptBlockType type) =>
        type switch
        {
            ManuscriptBlockType.Heading => ManuscriptStyleRoles.Heading,
            ManuscriptBlockType.SceneBreak => ManuscriptStyleRoles.SceneBreak,
            ManuscriptBlockType.BlockQuote => ManuscriptStyleRoles.BlockQuote,
            ManuscriptBlockType.ListItem => ManuscriptStyleRoles.ListItem,
            ManuscriptBlockType.Figure => ManuscriptStyleRoles.FigureCaption,
            ManuscriptBlockType.DesignedPage => ManuscriptStyleRoles.DesignedPage,
            ManuscriptBlockType.Table => ManuscriptStyleRoles.Table,
            _ => ManuscriptStyleRoles.Body,
        };
}
