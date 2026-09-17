namespace Lorekeeper.Manuscripts;

public interface IManuscriptService
{
    Task<ManuscriptSnapshot?> GetManuscriptAsync(
        EditorContentTarget target,
        Guid chapterId,
        CancellationToken cancellationToken = default);

    Task<ManuscriptMutationResult> ReplaceDocumentAsync(
        EditorContentTarget target,
        Guid chapterId,
        long expectedRevision,
        ManuscriptDocument document,
        CancellationToken cancellationToken = default);

    Task<ManuscriptMutationResult> ApplyAsync(
        EditorContentTarget target,
        Guid chapterId,
        long expectedRevision,
        IReadOnlyList<ManuscriptOperation> operations,
        CancellationToken cancellationToken = default);

    Task<ManuscriptMutationResult> ApplyUnderProjectMutationLeaseAsync(
        EditorContentTarget target,
        Guid chapterId,
        long expectedRevision,
        IReadOnlyList<ManuscriptOperation> operations,
        CancellationToken cancellationToken = default);

    Task<ManuscriptMutationResult> ApplyPersistedUnderProjectMutationLeaseAsync(
        EditorContentTarget target,
        Guid chapterId,
        long expectedRevision,
        IReadOnlyList<ManuscriptOperation> operations,
        CancellationToken cancellationToken = default) =>
        ApplyUnderProjectMutationLeaseAsync(target, chapterId, expectedRevision, operations, cancellationToken);

    Task RefreshDerivedStateAsync(
        EditorContentTarget target,
        Guid chapterId,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    Task ValidateDocumentReferencesAsync(
        EditorContentTarget target,
        Guid chapterId,
        ManuscriptDocument document,
        IReadOnlyList<ManuscriptStyleView>? styleCatalog = null,
        CancellationToken cancellationToken = default);

}
