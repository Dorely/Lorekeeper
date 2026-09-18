using System.Text.Json;
using Lorekeeper.Manuscripts;

namespace Lorekeeper.Authoring;

public sealed record AuthoringReductionResultV1(
    ManuscriptDocument Document,
    IReadOnlyList<AuthoringOperationV1> CanonicalInverse,
    IReadOnlyList<string> ChangedBlockIds);

public static class AuthoringBatchReducer
{
    public static (IReadOnlyList<AuthoringOperationV1> Forward, IReadOnlyList<AuthoringOperationV1> Inverse)
        CreateCanonicalDelta(
            ManuscriptDocument before,
            ManuscriptDocument after,
            int targetOrdinal = 0) =>
        (CreateCanonicalDeltaCore(before, after, targetOrdinal),
            CreateCanonicalDeltaCore(after, before, targetOrdinal));

    public static AuthoringReductionResultV1 Apply(
        ManuscriptDocument source,
        IReadOnlyList<AuthoringOperationV1> operations,
        bool allowCanonicalInverseOperations = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(operations);
        var document = Clone(source);
        var inverse = new List<AuthoringOperationV1>();
        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var wire in operations)
        {
            var kind = wire.Kind.Trim().ToLowerInvariant();
            if (kind == "restoreblock")
            {
                if (!allowCanonicalInverseOperations || wire.CanonicalBlock is null || wire.Index is null)
                    throw new ArgumentException("RestoreBlock is reserved for server-derived canonical inverses.");
                var block = Clone(wire.CanonicalBlock);
                var existing = document.Content.FindIndex(item => ManuscriptOperations.AreEquivalentBlockIds(item.Id, block.Id));
                if (existing >= 0)
                    document.Content.RemoveAt(existing);
                document.Content.Insert(Math.Clamp(wire.Index.Value, 0, document.Content.Count), block);
                changed.Add(block.Id);
                continue;
            }

            var before = document;
            var targetBlockId = TargetBlockId(wire);
            var oldIndex = targetBlockId is null
                ? -1
                : document.Content.FindIndex(item => ManuscriptOperations.AreEquivalentBlockIds(item.Id, targetBlockId));
            var oldBlock = oldIndex >= 0 ? Clone(document.Content[oldIndex]) : null;
            var operation = ToOperation(document, wire);
            var applied = ManuscriptOperations.Apply(document, [operation]);
            document = applied.Document;
            foreach (var blockId in applied.ChangedBlockIds)
                changed.Add(blockId);

            var stepInverse = DeriveInverse(wire, before, document, oldBlock, oldIndex);
            inverse.InsertRange(0, stepInverse);
        }

        ManuscriptCodec.Validate(document, source.ManuscriptId, document.Revision);
        return new(document, inverse, changed.ToList());
    }

    public static bool ExactPreconditionsMatch(
        ManuscriptDocument current,
        IReadOnlyList<AuthoringOperationV1> operations)
    {
        foreach (var operation in operations)
        {
            var kind = operation.Kind.Trim().ToLowerInvariant();
            if (kind == "replaceinlinecontent")
            {
                if (operation.Position is null || operation.InlineContent is null
                    || string.IsNullOrWhiteSpace(operation.ExpectedElementFingerprint)) return false;
                try
                {
                    if (operation.Position.Offset != 0 || operation.BlockId != operation.Position.BlockOrAtomId
                        || Fingerprint(ManuscriptTraversal.Resolve(current, operation.Position).Block) != operation.ExpectedElementFingerprint)
                        return false;
                }
                catch (InvalidDataException) { return false; }
                continue;
            }
            if (kind == "replacerichdocument")
            {
                if (operation.RichDocument is null
                    || string.IsNullOrWhiteSpace(operation.ExpectedDocumentFingerprint)
                    || !string.Equals(
                        operation.ExpectedDocumentFingerprint,
                        Fingerprint(current),
                        StringComparison.Ordinal))
                {
                    return false;
                }
                continue;
            }
            var blockId = TargetBlockId(operation);
            if (kind is "insertblock" or "insertdesignedpageplacement")
            {
                if (!ExactOrderMatches(
                        current.Content,
                        operation.ExpectedOrder,
                        operation.Index,
                        operation.InsertAt,
                        movingBlockId: null))
                    return false;
                var anchorId = operation.InsertAt?.BeforeBlockId ?? operation.InsertAt?.AfterBlockId;
                if (string.IsNullOrWhiteSpace(anchorId))
                {
                    if (current.Content.Count != 0 || operation.Index != 0)
                        return false;
                    continue;
                }
                if (string.IsNullOrWhiteSpace(operation.ExpectedAnchorFingerprint))
                    return false;
                var anchor = current.Content.SingleOrDefault(item => ManuscriptOperations.AreEquivalentBlockIds(item.Id, anchorId));
                if (anchor is null || !string.Equals(
                        operation.ExpectedAnchorFingerprint,
                        Fingerprint(anchor),
                        StringComparison.Ordinal))
                {
                    return false;
                }
                continue;
            }
            if (blockId is null || string.IsNullOrWhiteSpace(operation.ExpectedElementFingerprint))
                return false;
            var block = current.Content.SingleOrDefault(item => ManuscriptOperations.AreEquivalentBlockIds(item.Id, blockId));
            if (block is null || !string.Equals(
                    operation.ExpectedElementFingerprint,
                    Fingerprint(block),
                    StringComparison.Ordinal))
            {
                return false;
            }
            if (kind == "mergeblocks")
            {
                if (string.IsNullOrWhiteSpace(operation.SecondBlockId)
                    || string.IsNullOrWhiteSpace(operation.ExpectedSecondElementFingerprint))
                    return false;
                var secondIndex = current.Content.FindIndex(item =>
                    ManuscriptOperations.AreEquivalentBlockIds(item.Id, operation.SecondBlockId));
                var firstIndex = current.Content.FindIndex(item =>
                    ManuscriptOperations.AreEquivalentBlockIds(item.Id, blockId));
                if (secondIndex != firstIndex + 1
                    || !string.Equals(
                        operation.ExpectedSecondElementFingerprint,
                        Fingerprint(current.Content[secondIndex]),
                        StringComparison.Ordinal))
                    return false;
            }
            if (kind == "moveblock"
                && !ExactOrderMatches(
                    current.Content,
                    operation.ExpectedOrder,
                    operation.Index,
                    operation.InsertAt,
                    blockId))
                return false;
        }
        return true;
    }

    private static bool ExactOrderMatches(
        IReadOnlyList<ManuscriptBlock> source,
        AuthoringOrderPreconditionV1? expected,
        int? requestedIndex,
        AuthoringInsertAnchorV1? anchor,
        string? movingBlockId)
    {
        if (expected is null)
            return false;
        var blocks = source
            .Where(item => movingBlockId is null
                || !ManuscriptOperations.AreEquivalentBlockIds(item.Id, movingBlockId))
            .ToList();
        int insertionIndex;
        if (requestedIndex is int index)
        {
            insertionIndex = index;
        }
        else if (anchor?.BeforeBlockId is { Length: > 0 } beforeId)
        {
            insertionIndex = blocks.FindIndex(item =>
                ManuscriptOperations.AreEquivalentBlockIds(item.Id, beforeId));
        }
        else if (anchor?.AfterBlockId is { Length: > 0 } afterId)
        {
            var anchorIndex = blocks.FindIndex(item =>
                ManuscriptOperations.AreEquivalentBlockIds(item.Id, afterId));
            insertionIndex = anchorIndex < 0 ? -1 : anchorIndex + 1;
        }
        else
        {
            return false;
        }
        if (insertionIndex < 0 || insertionIndex > blocks.Count)
            return false;

        if (expected.PreviousBlockId is { Length: > 0 } previousId)
        {
            var previousIndex = insertionIndex - 1;
            if (previousIndex < 0
                || !ManuscriptOperations.AreEquivalentBlockIds(blocks[previousIndex].Id, previousId)
                || string.IsNullOrWhiteSpace(expected.PreviousBlockFingerprint)
                || !string.Equals(expected.PreviousBlockFingerprint, Fingerprint(blocks[previousIndex]), StringComparison.Ordinal))
                return false;
        }
        else if (expected.PreviousBlockFingerprint is not null || insertionIndex != 0)
        {
            return false;
        }

        if (expected.NextBlockId is { Length: > 0 } nextId)
        {
            if (insertionIndex >= blocks.Count
                || !ManuscriptOperations.AreEquivalentBlockIds(blocks[insertionIndex].Id, nextId)
                || string.IsNullOrWhiteSpace(expected.NextBlockFingerprint)
                || !string.Equals(expected.NextBlockFingerprint, Fingerprint(blocks[insertionIndex]), StringComparison.Ordinal))
                return false;
        }
        else if (expected.NextBlockFingerprint is not null || insertionIndex != blocks.Count)
        {
            return false;
        }
        return true;
    }

    public static string Fingerprint(ManuscriptBlock block) =>
        AuthoringPersistence.Fingerprint(JsonSerializer.Serialize(block, ManuscriptCodec.JsonOptions));

    public static string Fingerprint(ManuscriptDocument document) =>
        AuthoringPersistence.Fingerprint(ManuscriptCodec.Serialize(document));

    private static IReadOnlyList<AuthoringOperationV1> CreateCanonicalDeltaCore(
        ManuscriptDocument source,
        ManuscriptDocument destination,
        int targetOrdinal)
    {
        var inlineDelta = CreateInlineDelta(source, destination, targetOrdinal);
        if (inlineDelta is not null) return inlineDelta;
        if (source.Notes.Count != 0
            || destination.Notes.Count != 0
            || source.Content.Any(block => block.Type == ManuscriptBlockType.Table || block.List is not null)
            || destination.Content.Any(block => block.Type == ManuscriptBlockType.Table || block.List is not null))
        {
            return
            [
                new(
                    targetOrdinal,
                    "replaceRichDocument",
                    RichDocument: ManuscriptClone.Document(destination),
                    ExpectedDocumentFingerprint: Fingerprint(source)),
            ];
        }
        var current = source.Content.Select(Clone).ToList();
        var operations = new List<AuthoringOperationV1>();
        var desiredIds = destination.Content.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        for (var index = current.Count - 1; index >= 0; index--)
        {
            if (desiredIds.Contains(current[index].Id))
                continue;
            operations.Add(new(
                targetOrdinal,
                "deleteBlock",
                BlockId: current[index].Id,
                ExpectedElementFingerprint: Fingerprint(current[index])));
            current.RemoveAt(index);
        }
        for (var index = 0; index < destination.Content.Count; index++)
        {
            var desired = Clone(destination.Content[index]);
            var currentIndex = current.FindIndex(item =>
                ManuscriptOperations.AreEquivalentBlockIds(item.Id, desired.Id));
            if (currentIndex < 0)
            {
                operations.Add(new(targetOrdinal, "restoreBlock", Index: index, CanonicalBlock: desired));
                current.Insert(index, desired);
                continue;
            }
            if (!JsonSerializer.Serialize(current[currentIndex], ManuscriptCodec.JsonOptions).Equals(
                    JsonSerializer.Serialize(desired, ManuscriptCodec.JsonOptions),
                    StringComparison.Ordinal))
            {
                operations.Add(new(targetOrdinal, "restoreBlock", Index: index, CanonicalBlock: desired));
                current.RemoveAt(currentIndex);
                current.Insert(index, desired);
                continue;
            }
            if (currentIndex == index)
                continue;
            operations.Add(new(
                targetOrdinal,
                "moveBlock",
                BlockId: desired.Id,
                Index: index,
                ExpectedElementFingerprint: Fingerprint(desired)));
            current.RemoveAt(currentIndex);
            current.Insert(index, desired);
        }
        return operations;
    }

    private static IReadOnlyList<AuthoringOperationV1>? CreateInlineDelta(
        ManuscriptDocument source, ManuscriptDocument destination, int targetOrdinal)
    {
        static ManuscriptBlock Structure(ManuscriptBlock block) => block with
        {
            Content = [],
            Table = block.Table is not { } table ? null : table with
            {
                Rows = table.Rows.Select(row => row with
                {
                    Cells = row.Cells.Select(cell => cell with { Content = cell.Content.Select(Structure).ToList() }).ToList(),
                }).ToList(),
            },
        };
        static string StructureJson(ManuscriptDocument document) => JsonSerializer.Serialize(document with
        {
            Revision = 0,
            Content = document.Content.Select(Structure).ToList(),
            Notes = document.Notes.Select(note => note with { Content = note.Content.Select(Structure).ToList() }).ToList(),
        }, ManuscriptCodec.JsonOptions);
        if (StructureJson(source) != StructureJson(destination)) return null;
        var before = ManuscriptTraversal.EnumerateText(source).ToDictionary(segment => segment.Block.Id, StringComparer.Ordinal);
        return ManuscriptTraversal.EnumerateText(destination)
            .Where(segment => Fingerprint(segment.Block) != Fingerprint(before[segment.Block.Id].Block))
            .Select(segment => new AuthoringOperationV1(targetOrdinal, "replaceInlineContent",
                BlockId: segment.Block.Id, ExpectedElementFingerprint: Fingerprint(before[segment.Block.Id].Block),
                Position: before[segment.Block.Id].Start, InlineContent: ManuscriptClone.Block(segment.Block).Content)).ToList();
    }

    public static IReadOnlyList<ManuscriptOperation> ToManuscriptOperations(
        ManuscriptDocument source,
        IReadOnlyList<AuthoringOperationV1> operations,
        bool allowCanonicalInverseOperations = false)
    {
        var current = Clone(source);
        var result = new List<ManuscriptOperation>(operations.Count);
        foreach (var wire in operations)
        {
            if (wire.Kind.Equals("restoreBlock", StringComparison.OrdinalIgnoreCase))
            {
                if (!allowCanonicalInverseOperations || wire.CanonicalBlock is null || wire.Index is null)
                    throw new ArgumentException("RestoreBlock is reserved for server-derived canonical inverses.");
                var existing = current.Content.FindIndex(item => ManuscriptOperations.AreEquivalentBlockIds(item.Id, wire.CanonicalBlock.Id));
                if (existing >= 0)
                    result.Add(new DeleteManuscriptBlock(current.Content[existing].Id));
                result.Add(new PutRichManuscriptBlock(
                    Math.Clamp(wire.Index.Value, 0, current.Content.Count - (existing >= 0 ? 1 : 0)),
                    ManuscriptClone.Block(wire.CanonicalBlock)));
                current = Apply(current, [wire], allowCanonicalInverseOperations: true).Document;
                continue;
            }
            var operation = ToOperation(current, wire);
            result.Add(operation);
            current = ManuscriptOperations.Apply(current, [operation]).Document;
        }
        return result;
    }

    private static ManuscriptOperation ToOperation(ManuscriptDocument current, AuthoringOperationV1 wire)
    {
        var kind = wire.Kind.Trim().ToLowerInvariant();
        if (kind == "replaceinlinecontent")
        {
            var position = wire.Position ?? throw new ArgumentException("position is required for ReplaceInlineContent.");
            if (position.BlockOrAtomId != wire.BlockId)
                throw new ArgumentException("The inline-content position does not match blockId.");
            return new ReplaceManuscriptInlineContent(position,
                wire.InlineContent ?? throw new ArgumentException("inlineContent is required for ReplaceInlineContent."));
        }
        if (kind == "replacerichdocument")
        {
            var rich = wire.RichDocument
                ?? throw new ArgumentException("richDocument is required for ReplaceRichDocument.");
            if (rich.ManuscriptId != current.ManuscriptId)
                throw new ArgumentException("richDocument belongs to another manuscript.");
            return new ReplaceManuscriptStructure(rich.Content, rich.Notes);
        }
        if (kind == "removedesignedpageplacement")
            return new DeleteManuscriptBlock(Required(wire.PlacementBlockId, "placementBlockId"));
        if (kind == "insertdesignedpageplacement")
        {
            var placementId = Required(wire.PlacementBlockId, "placementBlockId");
            var pageId = wire.PageId ?? throw new ArgumentException("pageId is required.");
            var index = ResolveInsertionIndex(current, wire.Index, wire.InsertAt);
            return new InsertManuscriptBlock(
                index,
                ManuscriptBlockType.DesignedPage,
                string.Empty,
                ManuscriptStyleRoles.DesignedPage,
                DesignedPageId: pageId,
                BlockId: placementId);
        }

        if (kind == "insertblock")
        {
            if (string.IsNullOrWhiteSpace(wire.BlockId))
                throw new ArgumentException("AuthoringBatchProtocolV1 requires a stable blockId for InsertBlock.");
            var blockType = Enum.TryParse<ManuscriptBlockType>(wire.BlockType, ignoreCase: true, out var parsedType)
                && Enum.IsDefined(parsedType)
                ? parsedType
                : throw new ArgumentException("blockType is required for InsertBlock.");
            return new InsertManuscriptBlock(
                ResolveInsertionIndex(current, wire.Index, wire.InsertAt),
                blockType,
                wire.Text ?? string.Empty,
                wire.StyleRole,
                wire.ImageId,
                wire.AltText,
                wire.HeadingLevel,
                wire.Decorative ?? false,
                wire.FigurePresentation,
                wire.DesignedPageId,
                wire.Language,
                wire.AccessibilityRole ?? FigureAccessibilityRole.Figure,
                wire.BlockId);
        }
        if (kind == "setblocktype")
        {
            var blockType = Enum.TryParse<ManuscriptBlockType>(wire.BlockType, ignoreCase: true, out var parsedType)
                && Enum.IsDefined(parsedType)
                ? parsedType
                : throw new ArgumentException("blockType is required for SetBlockType.");
            return new SetManuscriptBlockType(
                Required(wire.BlockId, "blockId"),
                blockType,
                wire.StyleRole,
                wire.ImageId,
                wire.AltText,
                wire.HeadingLevel,
                wire.Decorative ?? false,
                wire.FigurePresentation,
                wire.DesignedPageId,
                wire.Language,
                wire.AccessibilityRole ?? FigureAccessibilityRole.Figure);
        }
        if (kind == "setfigurepresentation")
        {
            return new SetFigurePresentation(
                Required(wire.BlockId, "blockId"),
                wire.ImageId ?? throw new ArgumentException("imageId is required for SetFigurePresentation."),
                wire.AltText,
                wire.Decorative ?? false,
                wire.Language,
                wire.FigurePresentation ?? new FigurePresentation(),
                wire.AccessibilityRole ?? FigureAccessibilityRole.Figure);
        }
        return ManuscriptOperationInput.ToOperations(
        [
            new(
                wire.Kind,
                wire.BlockId,
                wire.SecondBlockId,
                wire.Index,
                wire.BlockType,
                wire.Text,
                wire.StyleRole,
                wire.StartOffset,
                wire.EndOffset,
                wire.Mark,
                wire.Enabled,
                wire.Value,
                wire.ImageId,
                wire.AltText,
                wire.HeadingLevel,
                wire.ParagraphPresentation)
        ]).Single();
    }

    private static IReadOnlyList<AuthoringOperationV1> DeriveInverse(
        AuthoringOperationV1 wire,
        ManuscriptDocument before,
        ManuscriptDocument after,
        ManuscriptBlock? oldBlock,
        int oldIndex)
    {
        var kind = wire.Kind.Trim().ToLowerInvariant();
        if (kind == "replaceinlinecontent")
            return CreateCanonicalDeltaCore(after, before, wire.TargetOrdinal);
        if (kind == "replacerichdocument")
        {
            return
            [
                new(
                    wire.TargetOrdinal,
                    "replaceRichDocument",
                    RichDocument: ManuscriptClone.Document(before),
                    ExpectedDocumentFingerprint: Fingerprint(after)),
            ];
        }
        if (kind is "insertblock" or "insertdesignedpageplacement")
        {
            return [new(wire.TargetOrdinal, "deleteBlock", BlockId: TargetBlockId(wire))];
        }
        if (kind == "moveblock")
            return [new(wire.TargetOrdinal, "moveBlock", BlockId: wire.BlockId, Index: oldIndex)];

        if (oldBlock is not null)
        {
            var added = after.Content
                .Where(item => before.Content.All(previous => !ManuscriptOperations.AreEquivalentBlockIds(previous.Id, item.Id)))
                .Select(item => new AuthoringOperationV1(wire.TargetOrdinal, "deleteBlock", BlockId: item.Id))
                .ToList();
            added.Add(new AuthoringOperationV1(
                wire.TargetOrdinal,
                "restoreBlock",
                Index: oldIndex,
                CanonicalBlock: oldBlock));
            if (kind == "mergeblocks" && !string.IsNullOrWhiteSpace(wire.SecondBlockId))
            {
                var secondIndex = before.Content.FindIndex(item => ManuscriptOperations.AreEquivalentBlockIds(item.Id, wire.SecondBlockId));
                if (secondIndex >= 0)
                {
                    added.Add(new AuthoringOperationV1(
                        wire.TargetOrdinal,
                        "restoreBlock",
                        Index: secondIndex,
                        CanonicalBlock: Clone(before.Content[secondIndex])));
                }
            }
            return added;
        }

        throw new InvalidOperationException($"The server could not derive an inverse for {wire.Kind}.");
    }

    private static int ResolveInsertionIndex(
        ManuscriptDocument current,
        int? requested,
        AuthoringInsertAnchorV1? anchor)
    {
        if (requested is int index)
            return index;
        if (anchor?.BeforeBlockId is { Length: > 0 } before)
        {
            var found = current.Content.FindIndex(item => ManuscriptOperations.AreEquivalentBlockIds(item.Id, before));
            return found >= 0 ? found : throw new KeyNotFoundException($"Insertion anchor {before} was not found.");
        }
        if (anchor?.AfterBlockId is { Length: > 0 } after)
        {
            var found = current.Content.FindIndex(item => ManuscriptOperations.AreEquivalentBlockIds(item.Id, after));
            return found >= 0 ? found + 1 : throw new KeyNotFoundException($"Insertion anchor {after} was not found.");
        }
        throw new ArgumentException("An insertion index or exact before/after anchor is required.");
    }

    private static string? TargetBlockId(AuthoringOperationV1 operation) =>
        operation.Kind.Trim().ToLowerInvariant() switch
        {
            "insertdesignedpageplacement" or "removedesignedpageplacement" => operation.PlacementBlockId,
            _ => operation.BlockId,
        };

    private static string Required(string? value, string name) =>
        !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"{name} is required.");

    private static ManuscriptDocument Clone(ManuscriptDocument document) => ManuscriptClone.Document(document);

    private static ManuscriptBlock Clone(ManuscriptBlock block) => ManuscriptClone.Block(block);
}
