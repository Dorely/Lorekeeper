using Lorekeeper.Models;

namespace Lorekeeper.Manuscripts;

public sealed record ManuscriptAnnotationRange(
    string StartBlockId,
    int StartOffset,
    string EndBlockId,
    int EndOffset);

public sealed record ManuscriptAnnotationView(
    Guid Id,
    Guid ProjectId,
    Guid ChapterId,
    Guid? EditionId,
    ManuscriptAnnotationKind Kind,
    string NoteText,
    long Revision,
    long AnchorManuscriptRevision,
    ManuscriptAnnotationAnchorState AnchorState,
    ManuscriptAnnotationRange Range,
    string Quote,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record ManuscriptAnnotationPage(
    IReadOnlyList<ManuscriptAnnotationView> Items,
    int Offset,
    int Limit,
    int Total);

public static class ManuscriptAnnotationText
{
    public static string Bound(string value, int maxLength)
    {
        if (value.Length <= maxLength)
            return value;
        if (maxLength < 4)
            throw new ArgumentOutOfRangeException(nameof(maxLength));
        var end = maxLength - 3;
        if (end > 0 && end < value.Length && char.IsHighSurrogate(value[end - 1]) && char.IsLowSurrogate(value[end]))
            end--;
        return value[..end] + "...";
    }
}

public interface IManuscriptAnnotationService
{
    Task<ManuscriptAnnotationView?> GetAsync(
        Guid projectId,
        EditorContentTarget target,
        Guid annotationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ManuscriptAnnotationView>> ListChapterAsync(
        Guid projectId,
        EditorContentTarget target,
        Guid chapterId,
        CancellationToken cancellationToken = default);

    Task<ManuscriptAnnotationPage> ListTargetAsync(
        Guid projectId,
        EditorContentTarget target,
        int offset,
        int limit,
        CancellationToken cancellationToken = default);

    Task<ManuscriptAnnotationView> CreateAsync(
        Guid projectId,
        EditorContentTarget target,
        Guid chapterId,
        ManuscriptAnnotationKind kind,
        string? noteText,
        ManuscriptAnnotationRange range,
        long expectedManuscriptRevision,
        CancellationToken cancellationToken = default);

    Task<ManuscriptAnnotationView> UpdateNoteAsync(
        Guid projectId,
        EditorContentTarget target,
        Guid annotationId,
        string noteText,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    Task<ManuscriptAnnotationView> ConvertAsync(
        Guid projectId,
        EditorContentTarget target,
        Guid annotationId,
        ManuscriptAnnotationKind kind,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    Task<ManuscriptAnnotationView> ReattachAsync(
        Guid projectId,
        EditorContentTarget target,
        Guid annotationId,
        ManuscriptAnnotationRange range,
        long expectedRevision,
        long expectedManuscriptRevision,
        CancellationToken cancellationToken = default);

    Task CompleteAsync(
        Guid projectId,
        EditorContentTarget target,
        Guid annotationId,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    Task RebaseForManuscriptMutationAsync(
        Guid projectId,
        EditorContentTarget target,
        Guid chapterId,
        ManuscriptDocument document,
        long manuscriptRevision,
        CancellationToken cancellationToken = default);
}
