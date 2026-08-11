using System.Text.Json;
using Lorekeeper.Chapters;
using Lorekeeper.Manuscripts;

namespace Lorekeeper.EditorChat;

public sealed class EditorManuscriptPreviewService(
    IChapterService chapters,
    IManuscriptService manuscripts,
    IManuscriptStyleService manuscriptStyles)
{
    public async Task<string> PreviewAsync(
        EditorChatContext context,
        Guid chapterId,
        long expectedRevision,
        ManuscriptOperationInput[] operations)
    {
        var chapter = await chapters.GetAsync(chapterId, context.TurnCancellationToken);
        if (chapter is null || chapter.ProjectId != context.ProjectId)
            return $"Error: chapter {chapterId:N} was not found in this project.";
        var snapshot = await manuscripts.GetManuscriptAsync(context.ContentTarget, chapterId, context.TurnCancellationToken);
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
            var preview = context.StageManuscriptPreview(
                chapterId,
                source,
                document,
                changedBlockIds);
            var sourcePlainText = ManuscriptCodec.ProjectPlainText(source);
            var projectedPlainText = ManuscriptCodec.ProjectPlainText(document);
            return JsonSerializer.Serialize(new
            {
                preview = true,
                previewId = preview.Id,
                chapterId,
                expectedRevision,
                nextRevision = document.Revision,
                changedBlockIds,
                sourceHash = ManuscriptCodec.HashPlainText(sourcePlainText),
                projectedHash = ManuscriptCodec.HashPlainText(projectedPlainText),
                sourceBlockCount = source.Content.Count,
                projectedBlockCount = document.Content.Count,
                sourcePlainTextChars = sourcePlainText.Length,
                projectedPlainTextChars = projectedPlainText.Length,
            }, ManuscriptCodec.JsonOptions);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException or InvalidDataException)
        {
            return $"Error: {exception.Message}";
        }
    }

    public async Task<string> ApplyAsync(EditorChatContext context, Guid previewId)
    {
        if (!context.TryTakeManuscriptPreview(previewId, out var preview))
            return $"Error: manuscript preview {previewId:N} was not found in this turn, was superseded, or was already applied.";

        var chapterId = preview.ChapterId;
        var chapter = await chapters.GetAsync(chapterId, context.TurnCancellationToken);
        if (chapter is null || chapter.ProjectId != context.ProjectId)
            return $"Error: chapter {chapterId:N} was not found in this project.";
        try
        {
            var snapshot = await manuscripts.GetManuscriptAsync(context.ContentTarget, chapterId, context.TurnCancellationToken)
                ?? throw new InvalidOperationException($"Manuscript {chapterId:N} was not found.");
            var source = context.ReviewEdits
                && context.EditorStaging?.TryGetChapterManuscriptDraft(chapterId, out var staged) == true
                    ? staged
                    : snapshot.Document;
            if (source.Revision != preview.SourceDocument.Revision
                || !ManuscriptCodec.ContentEquals(source, preview.SourceDocument))
            {
                return $"Error: manuscript preview {previewId:N} is stale because the chapter changed after preview. Read the manuscript and create a new preview.";
            }

            if (context.ReviewEdits && context.EditorStaging is not null)
            {
                var styleCatalog = await context.EditorStaging.ListManuscriptStyleDraftsAsync(
                    manuscriptStyles,
                    context.TurnCancellationToken);
                await manuscripts.ValidateDocumentReferencesAsync(
                    context.ContentTarget,
                    chapterId,
                    preview.ProjectedDocument,
                    styleCatalog,
                    context.TurnCancellationToken);
                var summary = $"Edit {preview.ChangedBlockIds.Count} manuscript block(s)";
                var payload = JsonSerializer.Serialize(new
                {
                    staged = true,
                    previewId,
                    expectedRevision = preview.SourceDocument.Revision,
                    nextRevision = preview.ProjectedDocument.Revision,
                    changedBlockIds = preview.ChangedBlockIds,
                });
                await context.EditorStaging.StageChapterManuscriptEditAsync(
                    chapter,
                    source,
                    preview.ProjectedDocument,
                    summary,
                    payload,
                    context.TurnCancellationToken);
                return payload;
            }

            var result = await manuscripts.ReplaceDocumentAsync(
                context.ContentTarget,
                chapterId,
                preview.SourceDocument.Revision,
                preview.ProjectedDocument,
                context.TurnCancellationToken);
            context.OnMutated();
            return JsonSerializer.Serialize(new
            {
                applied = true,
                previewId,
                result.Snapshot.Revision,
                result.Snapshot.SourceHash,
                changedBlockIds = preview.ChangedBlockIds,
            });
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException or InvalidDataException)
        {
            return $"Error: {exception.Message}";
        }
    }
}
