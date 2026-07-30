namespace Lorekeeper.Manuscripts;

public static class ManuscriptOperations
{
    public static (ManuscriptDocument Document, IReadOnlyList<string> ChangedBlockIds) Apply(
        ManuscriptDocument source,
        IReadOnlyList<ManuscriptOperation> operations)
    {
        var blocks = source.Content.Select(Clone).ToList();
        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in operations)
        {
            switch (operation)
            {
                case InsertManuscriptBlock insert:
                    if (insert.Index < 0 || insert.Index > blocks.Count)
                        throw new ArgumentOutOfRangeException(nameof(insert.Index));
                    ValidateBlockText(insert.Type, insert.Text);
                    var inserted = NewBlock(insert.Type, insert.Text, insert.StyleRole);
                    blocks.Insert(insert.Index, inserted);
                    changed.Add(inserted.Id);
                    break;

                case ReplaceManuscriptBlockText replace:
                    var replaceIndex = Find(blocks, replace.BlockId);
                    RequireTextBlock(blocks[replaceIndex]);
                    ValidateBlockText(blocks[replaceIndex].Type, replace.Text);
                    blocks[replaceIndex] = blocks[replaceIndex] with
                    {
                        Content = [new ManuscriptInline { Text = replace.Text }],
                    };
                    changed.Add(replace.BlockId);
                    break;

                case DeleteManuscriptBlock delete:
                    blocks.RemoveAt(Find(blocks, delete.BlockId));
                    changed.Add(delete.BlockId);
                    break;

                case MoveManuscriptBlock move:
                    var moveIndex = Find(blocks, move.BlockId);
                    if (move.TargetIndex < 0 || move.TargetIndex >= blocks.Count)
                        throw new ArgumentOutOfRangeException(nameof(move.TargetIndex));
                    var moved = blocks[moveIndex];
                    blocks.RemoveAt(moveIndex);
                    blocks.Insert(move.TargetIndex, moved);
                    changed.Add(move.BlockId);
                    break;

                case SplitManuscriptBlock split:
                    var splitIndex = Find(blocks, split.BlockId);
                    var splitBlock = blocks[splitIndex];
                    RequireTextBlock(splitBlock);
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
                        splitBlock.StyleRole);
                    blocks.Insert(splitIndex + 1, tail);
                    changed.Add(split.BlockId);
                    changed.Add(tail.Id);
                    break;

                case MergeManuscriptBlocks merge:
                    var firstIndex = Find(blocks, merge.FirstBlockId);
                    var secondIndex = Find(blocks, merge.SecondBlockId);
                    if (secondIndex != firstIndex + 1)
                        throw new InvalidOperationException("Only adjacent blocks can be merged.");
                    RequireTextBlock(blocks[firstIndex]);
                    RequireTextBlock(blocks[secondIndex]);
                    var mergedContent = new List<ManuscriptInline>();
                    foreach (var inline in blocks[firstIndex].Content.Concat(blocks[secondIndex].Content))
                        AppendInline(mergedContent, Clone(inline));
                    blocks[firstIndex] = blocks[firstIndex] with
                    {
                        Content = mergedContent,
                    };
                    blocks.RemoveAt(secondIndex);
                    changed.Add(merge.FirstBlockId);
                    changed.Add(merge.SecondBlockId);
                    break;

                case SetManuscriptBlockStyle style:
                    if (string.IsNullOrWhiteSpace(style.StyleRole))
                        throw new ArgumentException("Style role is required.", nameof(style.StyleRole));
                    var styleIndex = Find(blocks, style.BlockId);
                    blocks[styleIndex] = blocks[styleIndex] with { StyleRole = style.StyleRole.Trim() };
                    changed.Add(style.BlockId);
                    break;

                case SetManuscriptInlineMark mark:
                    ApplyMark(blocks, mark);
                    changed.Add(mark.BlockId);
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported manuscript operation {operation.GetType().Name}.");
            }
        }

        var result = source with
        {
            Revision = checked(source.Revision + 1),
            Content = blocks,
        };
        ManuscriptCodec.Validate(result, source.ManuscriptId, result.Revision);
        return (result, changed.ToList());
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
        if (type == ManuscriptBlockType.SceneBreak)
        {
            if (!string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("Scene-break blocks cannot contain text.", nameof(text));
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
        string? styleRole) =>
        NewBlock(
            type,
            type == ManuscriptBlockType.SceneBreak
                ? []
                : [new ManuscriptInline { Text = text }],
            styleRole);

    private static ManuscriptBlock NewBlock(
        ManuscriptBlockType type,
        List<ManuscriptInline> content,
        string? styleRole) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = type,
            StyleRole = string.IsNullOrWhiteSpace(styleRole) ? DefaultStyle(type) : styleRole.Trim(),
            Content = type == ManuscriptBlockType.SceneBreak
                ? []
                : content,
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

    private static ManuscriptBlock Clone(ManuscriptBlock block) =>
        block with
        {
            Content = block.Content.Select(inline => inline with
            {
                Marks = inline.Marks.Select(mark => mark with { }).ToList(),
            }).ToList(),
        };

    private static int Find(IReadOnlyList<ManuscriptBlock> blocks, string blockId)
    {
        var index = blocks.ToList().FindIndex(block => string.Equals(block.Id, blockId, StringComparison.Ordinal));
        return index >= 0 ? index : throw new KeyNotFoundException($"Manuscript block {blockId} was not found.");
    }

    private static void RequireTextBlock(ManuscriptBlock block)
    {
        if (block.Type == ManuscriptBlockType.SceneBreak)
            throw new InvalidOperationException($"Scene-break block {block.Id} has no editable text.");
    }

    private static string DefaultStyle(ManuscriptBlockType type) =>
        type switch
        {
            ManuscriptBlockType.Heading => ManuscriptStyleRoles.Heading,
            ManuscriptBlockType.SceneBreak => ManuscriptStyleRoles.SceneBreak,
            ManuscriptBlockType.BlockQuote => ManuscriptStyleRoles.BlockQuote,
            ManuscriptBlockType.ListItem => ManuscriptStyleRoles.ListItem,
            _ => ManuscriptStyleRoles.Body,
        };
}
