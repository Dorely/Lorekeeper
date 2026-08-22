using Lorekeeper.VersionHistory.Restore;
using Lorekeeper.VersionHistory.Services;

namespace Lorekeeper.VersionHistory.Sync;

public enum ProjectVersionRemoteUpdateErrorCode
{
    InvalidRequest,
    DirtyWorkspace,
    HistoryConflict,
    CheckoutFailed,
    Canceled,
}

public sealed class ProjectVersionRemoteUpdateException(
    ProjectVersionRemoteUpdateErrorCode code,
    string message,
    Exception? innerException = null) : InvalidOperationException(message, innerException)
{
    public ProjectVersionRemoteUpdateErrorCode Code { get; } = code;
}

/// <summary>
/// The result of a clean, fast-forward-only remote update. A null checkout
/// means the fetched remote did not advance local main, so the caller should
/// preserve the returned comparison status and any remote-tracking refs.
/// </summary>
public sealed record VersionHistoryRemoteUpdateResult(
    Guid ProjectId,
    Guid ProjectVersionRepositoryId,
    ProjectVersionSyncStatus SyncStatus,
    ProjectVersionCheckpointView SafetyCheckpoint,
    VersionHistoryCheckoutResult? Checkout,
    IReadOnlyList<string> Warnings);

public interface IProjectVersionRemoteUpdateService
{
    Task<VersionHistoryRemoteUpdateResult> FastForwardAndApplyAsync(
        Guid remoteId,
        CancellationToken cancellationToken = default);
}
