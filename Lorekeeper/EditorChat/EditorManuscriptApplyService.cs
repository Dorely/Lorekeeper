using System.Text.Json;
using Lorekeeper.Chapters;
using Lorekeeper.Manuscripts;

namespace Lorekeeper.EditorChat;

public sealed class EditorManuscriptApplyService(
    IChapterService chapters,
    IManuscriptService manuscripts,
    IManuscriptStyleService manuscriptStyles)
{
    public async Task<string> ApplyAsync(
        EditorChatContext context,
        Guid chapterId,
        long expectedRevision,
        ManuscriptOperationInput[] operations)
    {
        var chapter = await chapters.GetAsync(chapterId, context.TurnCancellationToken);
        if (chapter is null || chapter.ProjectId != context.ProjectId)
            return $"Error: chapter {chapterId:N} was not found in this project.";

        var snapshot = await manuscripts.GetManuscriptAsync(
            context.ContentTarget,
            chapterId,
            context.TurnCancellationToken);
        if (snapshot is null)
            return $"Error: manuscript {chapterId:N} was not found.";

        var source = context.ReviewEdits
            && context.EditorStaging?.TryGetChapterManuscriptDraft(chapterId, out var staged) == true
                ? staged
                : snapshot.Document;
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
            var styleCatalog = context.ReviewEdits && context.EditorStaging is not null
                ? await context.EditorStaging.ListManuscriptStyleDraftsAsync(
                    manuscriptStyles,
                    context.TurnCancellationToken)
                : null;
            await manuscripts.ValidateDocumentReferencesAsync(
                context.ContentTarget,
                chapterId,
                document,
                styleCatalog,
                context.TurnCancellationToken);

            if (context.ReviewEdits && context.EditorStaging is not null)
            {
                var stagedResult = JsonSerializer.Serialize(new
                {
                    ok = true,
                    staged = true,
                    targetId = chapterId,
                    expectedRevision,
                    revision = document.Revision,
                    sourceHash,
                    changedIds = changedBlockIds,
                    changedBlockCount = changedBlockIds.Count,
                    beforeBlockCount = source.Content.Count,
                    afterBlockCount = document.Content.Count,
                    operationCounts,
                    diagnostics,
                    requiresReadback,
                    readbackRanges,
                    summary = $"Applied manuscript edit to {changedBlockIds.Count} block(s); it is ready for review.",
                }, ManuscriptCodec.JsonOptions);
                await context.EditorStaging.StageChapterManuscriptEditAsync(
                    chapter,
                    source,
                    document,
                    $"Edit {changedBlockIds.Count} manuscript block(s)",
                    stagedResult,
                    context.TurnCancellationToken);
                return stagedResult;
            }

            var result = await manuscripts.ReplaceDocumentAsync(
                context.ContentTarget,
                chapterId,
                expectedRevision,
                document,
                context.TurnCancellationToken);
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

        var sourceIndexes = source.Content
            .Select((block, index) => (block.Id, index))
            .ToDictionary(item => item.Id, item => item.index, StringComparer.Ordinal);
        var resultIndexes = document.Content
            .Select((block, index) => (block.Id, index))
            .ToDictionary(item => item.Id, item => item.index, StringComparer.Ordinal);
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

    private sealed record ManuscriptMutationDiagnostic(string Severity, string Code, string Message);

    private sealed record ManuscriptReadbackRange(Guid ChapterId, int StartBlock, int BlockCount);
}
