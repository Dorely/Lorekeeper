namespace Lorekeeper.Manuscripts;

public interface IManuscriptService
{
    Task<ManuscriptSnapshot?> GetManuscriptAsync(
        Guid chapterId,
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

    Task<ManuscriptMutationResult> ApplyUnderProjectMutationLeaseAsync(
        Guid chapterId,
        long expectedRevision,
        IReadOnlyList<ManuscriptOperation> operations,
        CancellationToken cancellationToken = default);

    Task<ManuscriptMutationResult> ApplyPersistedUnderProjectMutationLeaseAsync(
        Guid chapterId,
        long expectedRevision,
        IReadOnlyList<ManuscriptOperation> operations,
        CancellationToken cancellationToken = default) =>
        ApplyUnderProjectMutationLeaseAsync(chapterId, expectedRevision, operations, cancellationToken);

    Task RefreshDerivedStateAsync(
        Guid chapterId,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    Task ValidateDocumentReferencesAsync(
        Guid chapterId,
        ManuscriptDocument document,
        IReadOnlyList<ManuscriptStyleView>? styleCatalog = null,
        CancellationToken cancellationToken = default);
}
