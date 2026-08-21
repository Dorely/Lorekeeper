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
                    changedIds = changedBlockIds,
                    changedBlockCount = changedBlockIds.Count,
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
                summary = $"Applied manuscript edit to {changedBlockIds.Count} block(s).",
                mutation = new { kind = "manuscript", id = chapterId },
            }, ManuscriptCodec.JsonOptions);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException or InvalidDataException)
        {
            return $"Error: {exception.Message}";
        }
    }
}
