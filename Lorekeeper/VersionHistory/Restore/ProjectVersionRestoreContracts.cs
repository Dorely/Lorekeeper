using Lorekeeper.VersionHistory.Services;
using Lorekeeper.VersionHistory.Compare;
using Lorekeeper.VersionHistory.Snapshots;
using Lorekeeper.VersionHistory.Sync;

namespace Lorekeeper.VersionHistory.Restore;

public interface IProjectVersionRestoreService
{
    Task<VersionHistoryRestoreResult> RestoreAsync(
        Guid projectId,
        string targetCommitSha,
        VersionHistoryRestoreSelection selection,
        string? safetyMessage = null,
        CancellationToken cancellationToken = default);

    Task<VersionHistoryImportResult> ImportValidatedSnapshotAsync(
        VersionHistoryValidatedSnapshotImport import,
        CancellationToken cancellationToken = default);

    Task<VersionHistoryImportResult> ImportCloneAsync(
        VersionHistoryImportCloneResult clone,
        VersionHistorySnapshotArtifact artifact,
        CancellationToken cancellationToken = default);

    Task<VersionHistoryCheckoutResult> CheckoutValidatedSnapshotAsync(
        VersionHistoryValidatedProjectCheckout checkout,
        CancellationToken cancellationToken = default);
}

public sealed record VersionHistoryRestoreResult(
    Guid ProjectId,
    string TargetCommitSha,
    ProjectVersionCheckpointView PreRestoreCheckpoint,
    ProjectVersionCheckpointView PostRestoreCheckpoint,
    VersionHistoryRestoreSelection Selection,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<VersionHistoryUnresolvedReference> UnresolvedReferences);

/// <summary>
/// A strict snapshot artifact and the immutable Git head metadata that names
/// the imported checkpoint. Git transport validates and supplies this value;
/// Restore only applies the already-materialized artifact.
/// </summary>
public sealed record VersionHistoryValidatedSnapshotImport(
    VersionHistorySnapshotArtifact Artifact,
    string HeadCommitSha,
    string HeadTreeSha,
    string? ParentCommitSha = null,
    string? Message = null);

public sealed record VersionHistoryImportResult(
    Guid ProjectId,
    Guid RepositoryId,
    string HeadCommitSha,
    string HeadTreeSha,
    ProjectVersionCheckpointView ImportedCheckpoint,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<VersionHistoryUnresolvedReference> UnresolvedReferences);

/// <summary>
/// A validated remote head that the sync coordinator has already advanced in
/// the local Git repository. Restore aligns the existing project's SQLite
/// state to this exact head without creating another Git commit.
/// </summary>
public sealed record VersionHistoryValidatedProjectCheckout(
    Guid ProjectId,
    VersionHistorySnapshotArtifact Artifact,
    string HeadCommitSha,
    string HeadTreeSha,
    string? ParentCommitSha = null,
    string? Message = null);

public sealed record VersionHistoryCheckoutResult(
    Guid ProjectId,
    Guid RepositoryId,
    string HeadCommitSha,
    string HeadTreeSha,
    ProjectVersionCheckpointView Checkpoint,
    bool CheckpointCreated,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<VersionHistoryUnresolvedReference> UnresolvedReferences);

public sealed record VersionHistoryUnresolvedReference(
    Guid ReferencedProjectId,
    Guid? ReferencedRepositoryId,
    string ReferencedProjectName,
    string ReferencedProjectSlug,
    string Reason);

public sealed class VersionHistoryRestoreException(
    string code,
    string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
