using Lorekeeper.Models;
using Lorekeeper.EditorChat;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Manuscripts;

public sealed class ManuscriptAnnotationService(
    IAppDatabaseOperationFactory database,
    IEditorContestMutationGuard contestGuard) : IManuscriptAnnotationService
{
    public const int MaxSelectionLength = 32_000;
    public const int MaxNoteLength = 8_000;
    private const int _maxPageSize = 200;

    public async Task<ManuscriptAnnotationView?> GetAsync(
        Guid projectId,
        EditorContentTarget target,
        Guid annotationId,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        await ValidateTargetOwnershipAsync(operation.Db, projectId, target, null, cancellationToken);
        var annotation = await operation.Db.ManuscriptAnnotations.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == annotationId && item.ProjectId == projectId && item.EditionId == target.EditionId,
            cancellationToken);
        return annotation is null ? null : ToView(annotation);
    }

    public async Task<IReadOnlyList<ManuscriptAnnotationView>> ListChapterAsync(
        Guid projectId,
        EditorContentTarget target,
        Guid chapterId,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        await ValidateTargetOwnershipAsync(operation.Db, projectId, target, chapterId, cancellationToken);
        var editionId = target.EditionId;
        var items = await operation.Db.ManuscriptAnnotations.AsNoTracking()
            .Where(item => item.ProjectId == projectId && item.ChapterId == chapterId && item.EditionId == editionId)
            .ToListAsync(cancellationToken);
        var document = await LoadDocumentAsync(
            operation.Db,
            projectId,
            target,
            chapterId,
            await EffectiveRevisionAsync(operation.Db, target, chapterId, cancellationToken),
            cancellationToken);
        var order = document.Content.Select((block, index) => (block.Id, index))
            .ToDictionary(item => item.Id, item => item.index, StringComparer.Ordinal);
        return items
            .OrderBy(item => item.AnchorState)
            .ThenBy(item => order.GetValueOrDefault(item.StartBlockId, int.MaxValue))
            .ThenBy(item => item.StartOffset)
            .ThenBy(item => item.CreatedAt)
            .Select(ToView)
            .ToList();
    }

    public async Task<ManuscriptAnnotationPage> ListTargetAsync(
        Guid projectId,
        EditorContentTarget target,
        int offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, _maxPageSize);
        await using var operation = await database.OpenReadAsync(cancellationToken);
        await ValidateTargetOwnershipAsync(operation.Db, projectId, target, null, cancellationToken);
        var editionId = target.EditionId;
        var query = operation.Db.ManuscriptAnnotations.AsNoTracking()
            .Where(item => item.ProjectId == projectId && item.EditionId == editionId);
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(item => item.Chapter.Order)
            .ThenBy(item => item.AnchorState)
            .ThenBy(item => item.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);
        return new ManuscriptAnnotationPage(items.Select(ToView).ToList(), offset, limit, total);
    }

    public async Task<ManuscriptAnnotationView> CreateAsync(
        Guid projectId,
        EditorContentTarget target,
        Guid chapterId,
        ManuscriptAnnotationKind kind,
        string? noteText,
        ManuscriptAnnotationRange range,
        long expectedManuscriptRevision,
        CancellationToken cancellationToken = default)
    {
        var normalizedNote = NormalizeNote(kind, noteText);
        await using var operation = await database.OpenWriteAsync(projectId, cancellationToken);
        operation.ShareWithNestedOperations();
        await contestGuard.EnsureMutationAllowedAsync(projectId, cancellationToken);
        var document = await LoadDocumentAsync(operation.Db, projectId, target, chapterId, expectedManuscriptRevision, cancellationToken);
        var resolved = ManuscriptAnnotationAnchors.ResolveSelection(document, range);
        var now = DateTime.UtcNow;
        var annotation = new ManuscriptAnnotation
        {
            ProjectId = projectId,
            ChapterId = chapterId,
            EditionId = target.EditionId,
            Kind = kind,
            NoteText = normalizedNote,
            Revision = 1,
            AnchorManuscriptRevision = expectedManuscriptRevision,
            AnchorState = ManuscriptAnnotationAnchorState.Current,
            StartBlockId = range.StartBlockId,
            StartOffset = range.StartOffset,
            EndBlockId = range.EndBlockId,
            EndOffset = range.EndOffset,
            OriginalQuote = resolved.Quote,
            ContextBefore = resolved.ContextBefore,
            ContextAfter = resolved.ContextAfter,
            CreatedAt = now,
            UpdatedAt = now,
        };
        operation.Db.ManuscriptAnnotations.Add(annotation);
        await operation.SaveChangesAsync(cancellationToken);
        return ToView(annotation);
    }

    public Task<ManuscriptAnnotationView> UpdateNoteAsync(
        Guid projectId,
        EditorContentTarget target,
        Guid annotationId,
        string noteText,
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        MutateAsync(projectId, target, annotationId, expectedRevision, annotation =>
        {
            annotation.NoteText = NormalizeNote(ManuscriptAnnotationKind.Note, noteText);
            annotation.Kind = ManuscriptAnnotationKind.Note;
        }, cancellationToken);

    public Task<ManuscriptAnnotationView> ConvertAsync(
        Guid projectId,
        EditorContentTarget target,
        Guid annotationId,
        ManuscriptAnnotationKind kind,
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        MutateAsync(projectId, target, annotationId, expectedRevision, annotation =>
        {
            annotation.Kind = kind;
            if (kind == ManuscriptAnnotationKind.Highlight)
                annotation.NoteText = string.Empty;
            else
                annotation.NoteText = NormalizeNote(kind, annotation.NoteText);
        }, cancellationToken);

    public async Task<ManuscriptAnnotationView> ReattachAsync(
        Guid projectId,
        EditorContentTarget target,
        Guid annotationId,
        ManuscriptAnnotationRange range,
        long expectedRevision,
        long expectedManuscriptRevision,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenWriteAsync(projectId, cancellationToken);
        operation.ShareWithNestedOperations();
        await contestGuard.EnsureMutationAllowedAsync(projectId, cancellationToken);
        await ValidateTargetOwnershipAsync(operation.Db, projectId, target, null, cancellationToken);
        var annotation = await RequireAnnotationAsync(operation.Db, projectId, target, annotationId, expectedRevision, cancellationToken);
        var document = await LoadDocumentAsync(operation.Db, projectId, target, annotation.ChapterId, expectedManuscriptRevision, cancellationToken);
        var resolved = ManuscriptAnnotationAnchors.ResolveSelection(document, range);
        annotation.StartBlockId = range.StartBlockId;
        annotation.StartOffset = range.StartOffset;
        annotation.EndBlockId = range.EndBlockId;
        annotation.EndOffset = range.EndOffset;
        annotation.OriginalQuote = resolved.Quote;
        annotation.ContextBefore = resolved.ContextBefore;
        annotation.ContextAfter = resolved.ContextAfter;
        annotation.AnchorState = ManuscriptAnnotationAnchorState.Current;
        annotation.AnchorManuscriptRevision = expectedManuscriptRevision;
        Touch(annotation);
        await operation.SaveChangesAsync(cancellationToken);
        return ToView(annotation);
    }

    public async Task CompleteAsync(
        Guid projectId,
        EditorContentTarget target,
        Guid annotationId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenWriteAsync(projectId, cancellationToken);
        operation.ShareWithNestedOperations();
        await contestGuard.EnsureMutationAllowedAsync(projectId, cancellationToken);
        await ValidateTargetOwnershipAsync(operation.Db, projectId, target, null, cancellationToken);
        var annotation = await RequireAnnotationAsync(operation.Db, projectId, target, annotationId, expectedRevision, cancellationToken);
        operation.Db.ManuscriptAnnotations.Remove(annotation);
        await operation.SaveChangesAsync(cancellationToken);
    }

    public async Task RebaseForManuscriptMutationAsync(
        Guid projectId,
        EditorContentTarget target,
        Guid chapterId,
        ManuscriptDocument document,
        long manuscriptRevision,
        CancellationToken cancellationToken = default)
    {
        await contestGuard.EnsureMutationAllowedAsync(projectId, cancellationToken);
        await using var operation = await database.OpenWriteAsync(projectId, cancellationToken);
        operation.ShareWithNestedOperations();
        var db = operation.Db;
        await ValidateTargetOwnershipAsync(db, projectId, target, chapterId, cancellationToken);
        if (target.IsCore)
        {
            var inheritedEditionIds = await db.PublicationEditions
                .Where(edition => edition.ProjectId == projectId
                    && !db.PublicationEditionChapterOverrides.Any(item => item.EditionId == edition.Id && item.ChapterId == chapterId))
                .Select(edition => edition.Id)
                .ToListAsync(cancellationToken);
            var annotations = await db.ManuscriptAnnotations
                .Where(item => item.ProjectId == projectId && item.ChapterId == chapterId
                    && (item.EditionId == null || item.EditionId != null && inheritedEditionIds.Contains(item.EditionId.Value)))
                .ToListAsync(cancellationToken);
            foreach (var annotation in annotations)
                Rebase(annotation, document, manuscriptRevision);
        }
        else
        {
            var annotations = await db.ManuscriptAnnotations
                .Where(item => item.ProjectId == projectId && item.ChapterId == chapterId && item.EditionId == target.EditionId)
                .ToListAsync(cancellationToken);
            foreach (var annotation in annotations)
                Rebase(annotation, document, manuscriptRevision);
        }
    }

    private async Task<ManuscriptAnnotationView> MutateAsync(
        Guid projectId,
        EditorContentTarget target,
        Guid annotationId,
        long expectedRevision,
        Action<ManuscriptAnnotation> mutation,
        CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenWriteAsync(projectId, cancellationToken);
        operation.ShareWithNestedOperations();
        await contestGuard.EnsureMutationAllowedAsync(projectId, cancellationToken);
        await ValidateTargetOwnershipAsync(operation.Db, projectId, target, null, cancellationToken);
        var annotation = await RequireAnnotationAsync(operation.Db, projectId, target, annotationId, expectedRevision, cancellationToken);
        mutation(annotation);
        Touch(annotation);
        await operation.SaveChangesAsync(cancellationToken);
        return ToView(annotation);
    }

    private static void Rebase(ManuscriptAnnotation annotation, ManuscriptDocument document, long manuscriptRevision)
    {
        ManuscriptAnnotationAnchors.Resolved? current = null;
        try
        {
            current = ManuscriptAnnotationAnchors.TryResolveSelection(document, new ManuscriptAnnotationRange(
                annotation.StartBlockId, annotation.StartOffset, annotation.EndBlockId, annotation.EndOffset));
        }
        catch (InvalidDataException)
        {
        }
        ManuscriptAnnotationRange? relocated = null;
        if (current?.Quote == annotation.OriginalQuote)
            relocated = current.Range;
        else
            relocated = ManuscriptAnnotationAnchors.FindUnambiguous(document, annotation.OriginalQuote, annotation.ContextBefore, annotation.ContextAfter);

        if (relocated is not null)
        {
            annotation.StartBlockId = relocated.StartBlockId;
            annotation.StartOffset = relocated.StartOffset;
            annotation.EndBlockId = relocated.EndBlockId;
            annotation.EndOffset = relocated.EndOffset;
            annotation.AnchorState = ManuscriptAnnotationAnchorState.Current;
        }
        else
        {
            annotation.AnchorState = ManuscriptAnnotationAnchorState.Outdated;
        }
        annotation.AnchorManuscriptRevision = manuscriptRevision;
        annotation.UpdatedAt = DateTime.UtcNow;
        annotation.Revision = checked(annotation.Revision + 1);
    }

    private static async Task<ManuscriptAnnotation> RequireAnnotationAsync(
        AppDbContext db,
        Guid projectId,
        EditorContentTarget target,
        Guid annotationId,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        var annotation = await db.ManuscriptAnnotations.SingleOrDefaultAsync(item => item.Id == annotationId, cancellationToken)
            ?? throw new KeyNotFoundException("The annotation was not found.");
        if (annotation.ProjectId != projectId || annotation.EditionId != target.EditionId)
            throw new InvalidOperationException("The annotation does not belong to the selected project and content target.");
        if (!await db.Chapters.AnyAsync(
            item => item.Id == annotation.ChapterId && item.ProjectId == projectId,
            cancellationToken))
            throw new InvalidOperationException("The annotation chapter does not belong to the selected project.");
        if (annotation.Revision != expectedRevision)
            throw new InvalidOperationException($"Annotation revision conflict: expected {expectedRevision}, current revision is {annotation.Revision}.");
        return annotation;
    }

    private static async Task<ManuscriptDocument> LoadDocumentAsync(
        AppDbContext db,
        Guid projectId,
        EditorContentTarget target,
        Guid chapterId,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        await ValidateTargetOwnershipAsync(db, projectId, target, chapterId, cancellationToken);
        var chapter = await db.Chapters.SingleAsync(item => item.Id == chapterId && item.ProjectId == projectId, cancellationToken);
        if (target.IsCore)
        {
            if (chapter.ManuscriptRevision != expectedRevision)
                throw new ManuscriptRevisionConflictException(expectedRevision, chapter.ManuscriptRevision);
            return ManuscriptCodec.Deserialize(chapter.ManuscriptJson, chapter.Id, chapter.ManuscriptRevision);
        }
        var chapterOverride = await db.PublicationEditionChapterOverrides.SingleOrDefaultAsync(
            item => item.EditionId == target.EditionId && item.ChapterId == chapterId, cancellationToken);
        var actualRevision = chapterOverride?.Revision ?? chapter.ManuscriptRevision;
        if (actualRevision != expectedRevision)
            throw new ManuscriptRevisionConflictException(expectedRevision, actualRevision);
        return chapterOverride is null
            ? ManuscriptCodec.Deserialize(chapter.ManuscriptJson, chapter.Id, chapter.ManuscriptRevision)
            : ManuscriptCodec.Deserialize(chapterOverride.ManuscriptJson, chapter.Id, chapterOverride.Revision);
    }

    private static async Task<long> EffectiveRevisionAsync(
        AppDbContext db,
        EditorContentTarget target,
        Guid chapterId,
        CancellationToken cancellationToken)
    {
        var coreRevision = await db.Chapters.Where(item => item.Id == chapterId)
            .Select(item => item.ManuscriptRevision)
            .SingleAsync(cancellationToken);
        if (target.IsCore)
            return coreRevision;
        return await db.PublicationEditionChapterOverrides
            .Where(item => item.EditionId == target.EditionId && item.ChapterId == chapterId)
            .Select(item => (long?)item.Revision)
            .SingleOrDefaultAsync(cancellationToken) ?? coreRevision;
    }

    private static async Task ValidateTargetOwnershipAsync(
        AppDbContext db,
        Guid projectId,
        EditorContentTarget target,
        Guid? chapterId,
        CancellationToken cancellationToken)
    {
        if (!await db.Projects.AnyAsync(project => project.Id == projectId, cancellationToken))
            throw new KeyNotFoundException("The project was not found.");
        if (chapterId is Guid id && !await db.Chapters.AnyAsync(chapter => chapter.Id == id && chapter.ProjectId == projectId, cancellationToken))
            throw new KeyNotFoundException("The chapter was not found in this project.");
        if (!target.IsCore && !await db.PublicationEditions.AnyAsync(edition => edition.Id == target.EditionId && edition.ProjectId == projectId, cancellationToken))
            throw new KeyNotFoundException("The edition was not found in this project.");
    }

    private static string NormalizeNote(ManuscriptAnnotationKind kind, string? noteText)
    {
        var normalized = (noteText ?? string.Empty).Trim();
        if (normalized.Length > MaxNoteLength)
            throw new InvalidDataException($"Annotation notes cannot exceed {MaxNoteLength:N0} characters.");
        if (kind == ManuscriptAnnotationKind.Note && normalized.Length == 0)
            throw new InvalidDataException("A note annotation requires note text.");
        return kind == ManuscriptAnnotationKind.Highlight ? string.Empty : normalized;
    }

    private static void Touch(ManuscriptAnnotation annotation)
    {
        annotation.Revision = checked(annotation.Revision + 1);
        annotation.UpdatedAt = DateTime.UtcNow;
    }

    private static ManuscriptAnnotationView ToView(ManuscriptAnnotation annotation) => new(
        annotation.Id,
        annotation.ProjectId,
        annotation.ChapterId,
        annotation.EditionId,
        annotation.Kind,
        annotation.NoteText,
        annotation.Revision,
        annotation.AnchorManuscriptRevision,
        annotation.AnchorState,
        new ManuscriptAnnotationRange(annotation.StartBlockId, annotation.StartOffset, annotation.EndBlockId, annotation.EndOffset),
        annotation.OriginalQuote,
        annotation.CreatedAt,
        annotation.UpdatedAt);
}

internal static class ManuscriptAnnotationAnchors
{
    internal sealed record Resolved(
        ManuscriptAnnotationRange Range,
        string Quote,
        string ContextBefore,
        string ContextAfter);

    private sealed record BlockSpan(ManuscriptBlock Block, int Start, int End);
    private sealed record Flow(string Text, IReadOnlyList<BlockSpan> Blocks);

    public static Resolved ResolveSelection(ManuscriptDocument document, ManuscriptAnnotationRange range) =>
        TryResolveSelection(document, range)
        ?? throw new InvalidDataException("The selected annotation range is not a valid flowing manuscript range.");

    public static Resolved? TryResolveSelection(ManuscriptDocument document, ManuscriptAnnotationRange range)
    {
        foreach (var flow in BuildFlows(document))
        {
            var startBlock = flow.Blocks.FirstOrDefault(item => item.Block.Id == range.StartBlockId);
            var endBlock = flow.Blocks.FirstOrDefault(item => item.Block.Id == range.EndBlockId);
            if (startBlock is null || endBlock is null)
                continue;
            var startText = Text(startBlock.Block);
            var endText = Text(endBlock.Block);
            ValidateOffset(startText, range.StartOffset, nameof(range.StartOffset));
            ValidateOffset(endText, range.EndOffset, nameof(range.EndOffset));
            var start = startBlock.Start + range.StartOffset;
            var end = endBlock.Start + range.EndOffset;
            if (end <= start)
                throw new InvalidDataException("An annotation requires a non-empty forward selection.");
            if (end - start > ManuscriptAnnotationService.MaxSelectionLength)
                throw new InvalidDataException($"An annotation selection cannot exceed {ManuscriptAnnotationService.MaxSelectionLength:N0} UTF-16 units.");
            return new Resolved(
                range,
                flow.Text[start..end],
                SliceWithoutSplittingSurrogate(flow.Text, Math.Max(0, start - 96), start),
                SliceWithoutSplittingSurrogate(flow.Text, end, Math.Min(flow.Text.Length, end + 96)));
        }
        return null;
    }

    public static ManuscriptAnnotationRange? FindUnambiguous(
        ManuscriptDocument document,
        string quote,
        string contextBefore,
        string contextAfter)
    {
        if (string.IsNullOrEmpty(quote))
            return null;
        var matches = new List<(Flow Flow, int Start)>();
        foreach (var flow in BuildFlows(document))
        {
            var searchAt = 0;
            while (searchAt <= flow.Text.Length - quote.Length)
            {
                var found = flow.Text.IndexOf(quote, searchAt, StringComparison.Ordinal);
                if (found < 0)
                    break;
                var before = flow.Text[Math.Max(0, found - contextBefore.Length)..found];
                var afterStart = found + quote.Length;
                var after = flow.Text[afterStart..Math.Min(flow.Text.Length, afterStart + contextAfter.Length)];
                if (before == contextBefore && after == contextAfter)
                    matches.Add((flow, found));
                searchAt = found + 1;
            }
        }
        if (matches.Count != 1)
            return null;
        var match = matches[0];
        return MapRange(match.Flow, match.Start, match.Start + quote.Length);
    }

    private static IReadOnlyList<Flow> BuildFlows(ManuscriptDocument document)
    {
        var result = new List<Flow>();
        var current = new List<ManuscriptBlock>();
        void Flush()
        {
            if (current.Count == 0)
                return;
            var spans = new List<BlockSpan>();
            var parts = new List<string>();
            var cursor = 0;
            foreach (var block in current)
            {
                var text = Text(block);
                spans.Add(new BlockSpan(block, cursor, cursor + text.Length));
                parts.Add(text);
                cursor += text.Length + 1;
            }
            result.Add(new Flow(string.Join('\n', parts), spans));
            current = [];
        }

        foreach (var block in document.Content)
        {
            if (IsFlowing(block))
                current.Add(block);
            else
                Flush();
        }
        Flush();
        return result;
    }

    private static ManuscriptAnnotationRange MapRange(Flow flow, int start, int end)
    {
        var startBlock = flow.Blocks.Last(item => item.Start <= start && start <= item.End);
        var endBlock = flow.Blocks.Last(item => item.Start < end && end <= item.End);
        return new ManuscriptAnnotationRange(
            startBlock.Block.Id,
            start - startBlock.Start,
            endBlock.Block.Id,
            end - endBlock.Start);
    }

    private static bool IsFlowing(ManuscriptBlock block) => block.Type is
        ManuscriptBlockType.Paragraph or
        ManuscriptBlockType.Heading or
        ManuscriptBlockType.BlockQuote or
        ManuscriptBlockType.ListItem
        || block.Type == ManuscriptBlockType.Figure && Text(block).Length > 0;

    private static string Text(ManuscriptBlock block) => string.Concat(block.Content.Select(item => item.Text));

    private static string SliceWithoutSplittingSurrogate(string text, int start, int end)
    {
        if (start > 0 && start < text.Length && char.IsHighSurrogate(text[start - 1]) && char.IsLowSurrogate(text[start]))
            start--;
        if (end > 0 && end < text.Length && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end]))
            end++;
        return text[start..end];
    }

    private static void ValidateOffset(string text, int offset, string parameterName)
    {
        if (offset < 0 || offset > text.Length)
            throw new InvalidDataException($"{parameterName} falls outside its manuscript block.");
        if (offset > 0 && offset < text.Length && char.IsHighSurrogate(text[offset - 1]) && char.IsLowSurrogate(text[offset]))
            throw new InvalidDataException($"{parameterName} splits a UTF-16 surrogate pair.");
    }
}
