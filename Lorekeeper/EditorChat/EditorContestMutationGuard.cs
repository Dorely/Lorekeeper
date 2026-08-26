using Lorekeeper.Persistence;

namespace Lorekeeper.EditorChat;

/// <summary>
/// Repository-backed contest lock for manuscript, layout, and assistant mutation
/// services. It intentionally does not depend on <see cref="IEditorContestService"/>
/// so the contest service can resolve a manuscript through the same owning service
/// without creating a dependency cycle.
/// </summary>
public sealed class EditorContestMutationGuard(
    IAppDatabaseOperationFactory database,
    IEditorContestMutationContext context) : IEditorContestMutationGuard
{
    public Task<EditorContestLockState> GetLockStateAsync(
        Guid projectId,
        CancellationToken cancellationToken = default) =>
        GetLockStateCoreAsync(projectId, cancellationToken);

    public Task EnsureMutationAllowedAsync(
        Guid projectId,
        CancellationToken cancellationToken = default) =>
        context.IsAuthorized(projectId)
            ? Task.CompletedTask
            : EnsureMutationAllowedCoreAsync(projectId, cancellationToken);

    private async Task EnsureMutationAllowedCoreAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var state = await GetLockStateCoreAsync(projectId, cancellationToken);
        if (state.IsLocked)
            throw new InvalidOperationException(state.Message
                ?? "Contest Review must be resolved or discarded before changing the Editor.");
    }

    private async Task<EditorContestLockState> GetLockStateCoreAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        var batch = await operation.Repositories.Contests.GetUnresolvedByProjectAsync(projectId, cancellationToken);
        return batch is null
            ? EditorContestLockState.Unlocked
            : new EditorContestLockState(
                true,
                batch.Id,
                batch.ChapterId,
                batch.ChapterTitle,
                batch.Status,
                "Contest Review must be resolved or discarded before the Editor can be changed.");
    }
}
