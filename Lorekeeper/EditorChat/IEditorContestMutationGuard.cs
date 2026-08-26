namespace Lorekeeper.EditorChat;

/// <summary>
/// Guards all ordinary Editor mutations while a contest is running or awaiting
/// resolution. Contest-owned draft edits and resolution are the only write paths
/// that should bypass this guard.
/// </summary>
public interface IEditorContestMutationGuard
{
    Task<EditorContestLockState> GetLockStateAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);

    Task EnsureMutationAllowedAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Carries the narrow authorization needed while Contest Review applies its
/// selected candidate. Ordinary Editor callers never enter this scope; the
/// owning contest service is the only code path that can authorize it.
/// </summary>
public interface IEditorContestMutationContext
{
    bool IsAuthorized(Guid projectId);

    IDisposable BeginAuthorizedMutation(Guid projectId);
}
