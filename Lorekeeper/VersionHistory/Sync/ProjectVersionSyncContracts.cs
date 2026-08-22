using Lorekeeper.Models;
using Lorekeeper.VersionHistory.Git;
using Lorekeeper.VersionHistory.GitHub;
using Lorekeeper.VersionHistory.Snapshots;

namespace Lorekeeper.VersionHistory.Sync;

public enum ProjectVersionSyncErrorCode
{
    InvalidRequest,
    InvalidRemote,
    RepositoryNotFound,
    RemoteNotFound,
    CredentialUnavailable,
    InvalidManifest,
    RepositoryCollision,
    TransportFailure,
    HistoryConflict,
    OperationFailed,
    Canceled,
}

public sealed class ProjectVersionSyncException : Exception
{
    public ProjectVersionSyncException(
        ProjectVersionSyncErrorCode code,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public ProjectVersionSyncErrorCode Code { get; }
}

public sealed record GitHubRepositorySelection(
    Guid ConnectionId,
    GitHubRepositoryInfo Repository,
    string RemoteName = "origin");

public enum ProjectVersionSyncDisposition
{
    Empty,
    AlreadySynchronized,
    RemoteAhead,
    LocalAhead,
    Diverged,
    Unrelated,
    FastForwarded,
    PushReady,
    Pushed,
    PushSkipped,
    Attached,
    Updated,
    Removed,
}

/// <summary>
/// UI-ready local/remote relationship. A fetched remote tip remains in
/// TrackingRef while local main is unchanged unless FastForwardLocalAsync is
/// explicitly requested.
/// </summary>
public sealed record ProjectVersionSyncStatus(
    Guid ProjectVersionRepositoryId,
    Guid? RemoteId,
    string RemoteName,
    string DefaultBranch,
    string TrackingRef,
    string? LocalCommitSha,
    string? RemoteCommitSha,
    string? MergeBaseSha,
    GitHistoryRelation Relation,
    ProjectVersionSyncDisposition Disposition,
    bool LocalUpdated,
    bool RemoteUpdated,
    string Message);

/// <summary>
/// Non-secret remote metadata for UI lists. Account fields are cached from the
/// local connection row; repository fields are cached from the selected
/// GitHub repository. This view never includes OAuth tokens.
/// </summary>
public sealed record ProjectVersionRemoteView(
    Guid Id,
    Guid ProjectVersionRepositoryId,
    Guid? GitHubConnectionId,
    long? GitHubUserId,
    string? AccountLogin,
    DateTime? AccessTokenExpiresAt,
    DateTime? LastValidatedAt,
    DateTime? RevokedAt,
    string RemoteName,
    string Owner,
    string RepositoryName,
    long? GitHubRepositoryId,
    string? CloneUrl,
    string? WebUrl,
    string? DefaultBranch,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record VersionHistoryImportCloneRequest(
    Guid ConnectionId,
    GitHubRepositoryInfo Repository);

public sealed record VersionHistoryImportCloneResult(
    Guid RepositoryId,
    Guid ProjectId,
    string RepositoryPath,
    string HeadCommitSha,
    string HeadTreeSha,
    VersionHistorySnapshotArtifact Artifact,
    GitHubRepositorySelection Selection,
    bool InstalledNewRepository = true)
{
    public VersionHistorySnapshotManifest Manifest => Artifact.Manifest;
}

public interface IProjectVersionSyncService
{
    Task<IReadOnlyList<ProjectVersionRemoteView>> ListRemotesAsync(
        Guid projectVersionRepositoryId,
        CancellationToken cancellationToken = default);

    Task<ProjectVersionSyncStatus> GetCachedStatusAsync(
        Guid remoteId,
        CancellationToken cancellationToken = default);

    Task<ProjectVersionSyncStatus> AttachRemoteAsync(
        Guid projectVersionRepositoryId,
        GitHubRepositorySelection selection,
        CancellationToken cancellationToken = default);

    Task<ProjectVersionSyncStatus> UpdateRemoteAsync(
        Guid remoteId,
        GitHubRepositorySelection selection,
        CancellationToken cancellationToken = default);

    Task<ProjectVersionSyncStatus> RemoveRemoteAsync(
        Guid remoteId,
        CancellationToken cancellationToken = default);

    Task<ProjectVersionSyncStatus> FetchAsync(
        Guid remoteId,
        CancellationToken cancellationToken = default);

    Task<ProjectVersionSyncStatus> FastForwardLocalAsync(
        Guid remoteId,
        CancellationToken cancellationToken = default);

    Task<ProjectVersionSyncStatus> PushAsync(
        Guid remoteId,
        CancellationToken cancellationToken = default);

    Task<VersionHistoryImportCloneResult> CloneForImportAsync(
        VersionHistoryImportCloneRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes only a repository installed by a clone result when its exact
    /// project/repository identity has not been committed to the database.
    /// Existing local identities are never removed by this operation.
    /// </summary>
    Task<bool> RemoveImportedRepositoryIfUnownedAsync(
        VersionHistoryImportCloneResult clone,
        CancellationToken cancellationToken = default);
}
