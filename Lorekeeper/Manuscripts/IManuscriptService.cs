namespace Lorekeeper.Manuscripts;

public interface IManuscriptService
{
    Task<Lorekeeper.Authoring.LatestAssistantReviewSnapshot?> GetLatestAssistantReviewAsync(
        EditorContentTarget target,
        Guid chapterId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<Lorekeeper.Authoring.LatestAssistantReviewSnapshot?>(null);

    Task<Lorekeeper.Authoring.AuthoringHistoryState> GetHistoryStateAsync(
        EditorContentTarget target,
        Guid chapterId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new Lorekeeper.Authoring.AuthoringHistoryState(0, false, false, null, null, 0, 0));

    Task<ManuscriptHistoryMutationResult> UndoAsync(
        EditorContentTarget target,
        Guid chapterId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Persistent manuscript history is unavailable.");

    Task<ManuscriptHistoryMutationResult> RedoAsync(
        EditorContentTarget target,
        Guid chapterId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Persistent manuscript history is unavailable.");

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

    Task<Guid> EnsureEditionCompositionAsync(
        EditorContentTarget target,
        Guid chapterId,
        Guid sourceCompositionId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Edition composition forking is unavailable.");

}
