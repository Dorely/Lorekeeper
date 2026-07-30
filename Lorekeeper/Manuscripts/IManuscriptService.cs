namespace Lorekeeper.Manuscripts;

public interface IManuscriptService
{
    Task<ManuscriptSnapshot?> GetManuscriptAsync(
        Guid chapterId,
        CancellationToken cancellationToken = default);

    Task<ManuscriptMutationResult> ReplacePlainTextAsync(
        Guid chapterId,
        long expectedRevision,
        string plainText,
        CancellationToken cancellationToken = default);

    Task<ManuscriptMutationResult> ReplaceDocumentAsync(
        Guid chapterId,
        long expectedRevision,
        ManuscriptDocument document,
        CancellationToken cancellationToken = default);

    Task<ManuscriptMutationResult> ApplyAsync(
        Guid chapterId,
        long expectedRevision,
        IReadOnlyList<ManuscriptOperation> operations,
        CancellationToken cancellationToken = default);
}
