using System.Text.Json;
using Lorekeeper.Authoring;
using Lorekeeper.Chapters;
using Lorekeeper.Manuscripts;

namespace Lorekeeper.EditorChat;

public sealed class EditorManuscriptApplyService(
    IChapterService chapters,
    IManuscriptService manuscripts,
    IManuscriptStyleService manuscriptStyles,
    IAuthoringMutationFence authoringFence)
{
    public async Task<string> ApplyAsync(
        EditorChatContext context,
        Guid chapterId,
        long expectedRevision,
        ManuscriptOperationInput[] operations)
    {
        try
        {
            return await authoringFence.ExecuteAsync(
                new AuthoringFenceRequest(
                    context.ProjectId,
                    [TargetId(context.ContentTarget, chapterId)],
                    "apply an assistant manuscript mutation"),
                (_, token) => ApplyCoreAsync(context, chapterId, expectedRevision, operations, token),
                context.TurnCancellationToken);
        }
        catch (AuthoringMutationFenceException exception)
        {
            return $"Error: {exception.Code}: {exception.Message}";
        }
    }

    private async Task<string> ApplyCoreAsync(
        EditorChatContext context,
        Guid chapterId,
        long expectedRevision,
        ManuscriptOperationInput[] operations,
        CancellationToken cancellationToken)
    {
        var chapter = await chapters.GetAsync(chapterId, cancellationToken);
        if (chapter is null || chapter.ProjectId != context.ProjectId)
            return $"Error: chapter {chapterId:N} was not found in this project.";

        var snapshot = await manuscripts.GetManuscriptAsync(
            context.ContentTarget,
            chapterId,
            cancellationToken);
        if (snapshot is null)
            return $"Error: manuscript {chapterId:N} was not found.";

        var source = snapshot.Document;
        if (source.Revision != expectedRevision)
            return $"Error: manuscript revision conflict; expected {expectedRevision}, current revision is {source.Revision}.";

        try
        {
            var converted = ManuscriptOperationInput.ToOperations(operations);
            var (document, changedBlockIds) = ManuscriptOperations.Apply(source, converted);
            var operationCounts = new
            {
                insertBlock = converted.Count(operation => operation is InsertManuscriptBlock),
                replaceBlockText = converted.Count(operation => operation is ReplaceManuscriptBlockText),
                replaceInlineContent = converted.Count(operation => operation is ReplaceManuscriptInlineContent),
                deleteBlock = converted.Count(operation => operation is DeleteManuscriptBlock),
                moveBlock = converted.Count(operation => operation is MoveManuscriptBlock),
                splitBlock = converted.Count(operation => operation is SplitManuscriptBlock),
                mergeBlocks = converted.Count(operation => operation is MergeManuscriptBlocks),
                setBlockType = converted.Count(operation => operation is SetManuscriptBlockType),
                setBlockStyle = converted.Count(operation => operation is SetManuscriptBlockStyle),
                setInlineMark = converted.Count(operation => operation is SetManuscriptInlineMark),
                setParagraphPresentation = converted.Count(operation => operation is SetParagraphPresentation),
            };
            var requiresReadback = converted.Any(IsTextOrStructureOperation);
            var readbackRanges = requiresReadback
                ? BuildReadbackRanges(chapterId, source, document, changedBlockIds)
                : [];
            var diagnostics = BuildDiagnostics(source, converted);
            var sourceHash = ManuscriptCodec.HashPlainText(ManuscriptCodec.ProjectPlainText(document));
            var styleCatalog = await manuscriptStyles.ListAsync(
                context.ProjectId,
                cancellationToken);
            await manuscripts.ValidateDocumentReferencesAsync(
                context.ContentTarget,
                chapterId,
                document,
                styleCatalog,
                cancellationToken);

            var result = await manuscripts.ReplaceDocumentAsync(
                context.ContentTarget,
                chapterId,
                expectedRevision,
                document,
                cancellationToken);
            context.OnMutated();
            return JsonSerializer.Serialize(new
            {
                ok = true,
                applied = true,
                targetId = chapterId,
                revision = result.Snapshot.Revision,
                sourceHash = result.Snapshot.SourceHash,
                changedIds = changedBlockIds,
                changedBlockCount = changedBlockIds.Count,
                beforeBlockCount = source.Content.Count,
                afterBlockCount = document.Content.Count,
                operationCounts,
                diagnostics,
                requiresReadback,
                readbackRanges,
                summary = $"Applied manuscript edit to {changedBlockIds.Count} block(s).",
                mutation = new { kind = "manuscript", id = chapterId },
            }, ManuscriptCodec.JsonOptions);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException or InvalidDataException)
        {
            return $"Error: {exception.Message}";
        }
    }

    private static bool IsTextOrStructureOperation(ManuscriptOperation operation) =>
        operation is InsertManuscriptBlock
            or ReplaceManuscriptBlockText
            or ReplaceManuscriptInlineContent
            or DeleteManuscriptBlock
            or MoveManuscriptBlock
            or SplitManuscriptBlock
            or MergeManuscriptBlocks
            or SetManuscriptBlockType;

    private static IReadOnlyList<ManuscriptMutationDiagnostic> BuildDiagnostics(
        ManuscriptDocument source,
        IReadOnlyList<ManuscriptOperation> operations)
    {
        var hasNonEmptyTextInsertion = operations
            .OfType<InsertManuscriptBlock>()
            .Any(operation => operation.Type is not ManuscriptBlockType.Figure
                && !string.IsNullOrWhiteSpace(operation.Text));
        var hasReplacementOrDeletion = operations.Any(operation =>
            operation is ReplaceManuscriptBlockText or DeleteManuscriptBlock);
        if (source.Content.Count == 0 || !hasNonEmptyTextInsertion || hasReplacementOrDeletion)
            return [];

        return
        [
            new(
                "warning",
                "MANUSCRIPT_INSERT_WITHOUT_REPLACEMENT",
                "This batch added text to a non-empty manuscript without replacing or deleting a source block. That is valid only for genuinely additive work. If the user asked to revise existing prose, read every returned range and remove the superseded source before replying."),
        ];
    }

    private static IReadOnlyList<ManuscriptReadbackRange> BuildReadbackRanges(
        Guid chapterId,
        ManuscriptDocument source,
        ManuscriptDocument document,
        IReadOnlyList<string> changedBlockIds)
    {
        if (document.Content.Count == 0)
            return [new(chapterId, 0, 1)];

        var sourceIndexes = IndexOwnedBlocks(source);
        var resultIndexes = IndexOwnedBlocks(document);
        var anchors = new SortedSet<int>();
        foreach (var blockId in changedBlockIds)
        {
            if (sourceIndexes.TryGetValue(blockId, out var sourceIndex))
                anchors.Add(Math.Clamp(sourceIndex, 0, document.Content.Count - 1));
            if (resultIndexes.TryGetValue(blockId, out var resultIndex))
                anchors.Add(resultIndex);
        }

        if (anchors.Count == 0)
            return [new(chapterId, 0, Math.Min(100, document.Content.Count))];

        var merged = new List<(int Start, int EndExclusive)>();
        foreach (var anchor in anchors)
        {
            var start = Math.Max(0, anchor - 1);
            var endExclusive = Math.Min(document.Content.Count, anchor + 2);
            if (merged.Count > 0 && start <= merged[^1].EndExclusive)
            {
                var previous = merged[^1];
                merged[^1] = (previous.Start, Math.Max(previous.EndExclusive, endExclusive));
                continue;
            }

            merged.Add((start, endExclusive));
        }

        var result = new List<ManuscriptReadbackRange>();
        foreach (var range in merged)
        {
            var start = range.Start;
            while (start < range.EndExclusive)
            {
                var count = Math.Min(100, range.EndExclusive - start);
                result.Add(new(chapterId, start, count));
                start += count;
            }
        }
        return result;
    }

    private static Dictionary<string, int> IndexOwnedBlocks(ManuscriptDocument document)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < document.Content.Count; index++)
        {
            var owned = ManuscriptTraversal.EnumerateBlocks(document with { Content = [document.Content[index]], Notes = [] });
            foreach (var block in owned) result[block.Id] = index;
            var noteIds = owned.SelectMany(block => block.Content).Where(inline => inline.Type == ManuscriptInlineType.NoteReference)
                .Select(inline => inline.NoteId).ToHashSet(StringComparer.Ordinal);
            foreach (var note in document.Notes.Where(note => noteIds.Contains(note.Id)))
            foreach (var block in note.Content) result[block.Id] = index;
        }
        return result;
    }

    private sealed record ManuscriptMutationDiagnostic(string Severity, string Code, string Message);

    private sealed record ManuscriptReadbackRange(Guid ChapterId, int StartBlock, int BlockCount);

    private static string TargetId(EditorContentTarget target, Guid chapterId) => target.IsCore
        ? $"chapter:{chapterId:D}"
        : $"release:{target.EditionId!.Value:D}:chapter:{chapterId:D}";
}
