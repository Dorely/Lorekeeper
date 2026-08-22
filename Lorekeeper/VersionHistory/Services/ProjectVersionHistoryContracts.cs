using Lorekeeper.Models;
using Lorekeeper.VersionHistory.Git;
using Lorekeeper.VersionHistory.Snapshots;

namespace Lorekeeper.VersionHistory.Services;

public enum ProjectVersionRepositoryHealth
{
    Uninitialized,
    Healthy,
    Dirty,
    Missing,
    Corrupt,
    Diverged,
}

public sealed record ProjectVersionRepositoryView(
    Guid ProjectId,
    Guid RepositoryId,
    long CreativeRevision,
    long? LastCheckpointRevision,
    string? LastCheckpointContentHash,
    string? HeadCommitSha,
    string? HeadContentHash,
    DateTime? LastCheckpointAt,
    ProjectVersionRepositoryHealth Health,
    bool IsDirty,
    string? Diagnostic);

public sealed record ProjectVersionCheckpointView(
    Guid Id,
    Guid RepositoryId,
    int ManifestSchemaVersion,
    long CreativeRevision,
    string ContentHash,
    string ManifestHash,
    string? CommitSha,
    string? ParentCommitSha,
    ProjectVersionCheckpointKind Kind,
    ProjectVersionCheckpointSource Source,
    string? Message,
    DateTime CreatedAt);

public sealed record ProjectVersionOperationView(
    Guid Id,
    Guid RepositoryId,
    ProjectVersionOperationKind Kind,
    ProjectVersionOperationStatus Status,
    string? RequestKey,
    bool IsResumable,
    int AttemptCount,
    string? ErrorCode,
    string? ErrorMessage,
    DateTime? StartedAt,
    DateTime? HeartbeatAt,
    DateTime? CompletedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record ProjectVersionTimelineView(
    ProjectVersionRepositoryView Repository,
    IReadOnlyList<ProjectVersionCheckpointView> Checkpoints,
    IReadOnlyList<ProjectVersionOperationView> Operations);

public sealed record ProjectVersionStatusView(
    ProjectVersionRepositoryView Repository,
    string? CurrentContentHash);

public sealed record ProjectVersionLoadedCheckpoint(
    GitCommitMetadata Commit,
    VersionHistorySnapshotManifest Manifest,
    VersionHistorySnapshotPayload Payload,
    ProjectVersionCheckpointView? RecordedCheckpoint);

public interface IProjectVersionHistoryService
{
    Task<ProjectVersionRepositoryView?> GetRepositoryAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);

    Task<ProjectVersionRepositoryView> EnsureRepositoryAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);

    Task<ProjectVersionCheckpointView> CreateCheckpointAsync(
        Guid projectId,
        ProjectVersionCheckpointKind kind,
        string semanticMessage,
        string? requestKey = null,
        DateTimeOffset? authoredAt = null,
        CancellationToken cancellationToken = default);

    Task<ProjectVersionTimelineView?> GetTimelineAsync(
        Guid projectId,
        int maxCheckpoints = 100,
        int maxOperations = 100,
        CancellationToken cancellationToken = default);

    Task<ProjectVersionStatusView?> GetStatusAsync(
        Guid projectId,
        bool includeCurrentSnapshotHash = true,
        CancellationToken cancellationToken = default);

    Task<ProjectVersionLoadedCheckpoint> LoadCheckpointAsync(
        Guid projectId,
        string commitSha,
        CancellationToken cancellationToken = default);
}

public enum ProjectVersionReconciliationState
{
    Healthy,
    Initialized,
    Empty,
    Missing,
    Corrupt,
    Diverged,
}

public sealed record ProjectVersionReconciliationItem(
    Guid ProjectId,
    Guid RepositoryId,
    ProjectVersionReconciliationState State,
    string? HeadCommitSha,
    int ImportedCheckpointCount,
    string? Diagnostic);

public enum ProjectVersionDeletionTombstoneState
{
    RolledBack,
    Finalized,
    Preserved,
}

public sealed record ProjectVersionDeletionTombstoneItem(
    Guid? RepositoryId,
    string Path,
    ProjectVersionDeletionTombstoneState State,
    string? Diagnostic);

public sealed record ProjectVersionReconciliationReport(
    DateTime CompletedAt,
    IReadOnlyList<ProjectVersionReconciliationItem> Items)
{
    public IReadOnlyList<ProjectVersionDeletionTombstoneItem> DeletionTombstones { get; init; } = [];
}

public interface IProjectVersionHistoryReconciliationService
{
    Task<ProjectVersionReconciliationReport> ReconcileAsync(
        CancellationToken cancellationToken = default);
}
