using Lorekeeper.ImportExport;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.VersionHistory.Compare;
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
    DateTime? AcknowledgedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record ProjectVersionTimelineView(
    ProjectVersionRepositoryView Repository,
    IReadOnlyList<ProjectVersionCheckpointView> Checkpoints,
    IReadOnlyList<ProjectVersionOperationView> Operations);

public sealed record ProjectVersionStatusView(
    ProjectVersionRepositoryView Repository,
    string? CurrentContentHash);

/// <summary>
/// A validated live snapshot captured while the project mutation lease is
/// held. Restore operations use this context so their concurrency check and
/// SQLite application are based on one serialized view of the project.
/// </summary>
internal sealed record ProjectVersionReviewSnapshotContext(
    ProjectVersionStatusView Status,
    VersionHistorySnapshotArtifact Current,
    ProjectVersionLoadedCheckpoint Approved);

/// <summary>
/// Immutable values captured when a review view is loaded. Approval operations
/// must present the same token so a later manual edit, checkpoint, or restore
/// cannot be silently accepted against a different baseline.
/// </summary>
public sealed record ProjectVersionReviewConcurrencyToken(
    Guid RepositoryId,
    string? HeadCommitSha,
    string? HeadContentHash,
    string CurrentContentHash);

/// <summary>
/// Identifies one chapter in the exact Core or release content target that the
/// Review surface is displaying.
/// </summary>
public sealed record ProjectVersionReviewTarget(
    Guid ChapterId,
    EditorContentTarget ContentTarget);

/// <summary>
/// Target-scoped semantic before/after state projected from two validated
/// canonical snapshots. The service owns Git and snapshot loading; consumers
/// receive only the chapter payloads needed to render the review.
/// </summary>
public sealed record ProjectVersionReviewChapter(
    Guid ChapterId,
    EditorContentTarget ContentTarget,
    ProjectExportChapter? Before,
    ProjectExportChapter? After,
    ProjectVersionReviewConcurrencyToken ConcurrencyToken,
    IReadOnlyList<ProjectVersionReviewComposition>? Compositions = null)
{
    public IReadOnlyList<ProjectVersionReviewComposition> CompositionChanges => Compositions ?? [];
    public string TargetKey => ContentTarget.StorageKey;
    /// <summary>
    /// Indicates that the target has a manuscript change that belongs in the
    /// chapter review surface. Chapter metadata is intentionally exposed via
    /// the non-manuscript dependency groups instead.
    /// </summary>
    public bool HasChanges => HasManuscriptChanges || HasVisualChanges;

    public bool HasManuscriptChanges => !ManuscriptSemanticallyEquals(Before, After);

    public bool HasMetadataChanges => !MetadataSemanticallyEquals(Before, After);

    public bool HasVisualChanges => CompositionChanges.Any(composition => composition.HasChanges);

    /// <summary>
    /// Compares the canonical chapter values rather than relying on record
    /// equality. The export DTO contains mutable collection properties, so
    /// its generated equality would compare those collections by reference.
    /// </summary>
    public static bool SemanticallyEquals(ProjectExportChapter? before, ProjectExportChapter? after)
    {
        if (ReferenceEquals(before, after))
            return true;
        if (before is null || after is null)
            return false;

        return before.Id == after.Id
            && before.ActId == after.ActId
            && string.Equals(before.Title, after.Title, StringComparison.Ordinal)
            && string.Equals(before.ManuscriptJson, after.ManuscriptJson, StringComparison.Ordinal)
            && before.ManuscriptRevision == after.ManuscriptRevision
            && string.Equals(before.Synopsis, after.Synopsis, StringComparison.Ordinal)
            && before.Order == after.Order
            && before.VisualMode == after.VisualMode
            && before.PageLayoutKind == after.PageLayoutKind
            && string.Equals(before.PageLayoutJson, after.PageLayoutJson, StringComparison.Ordinal)
            && string.Equals(before.IllustrationLayoutJson, after.IllustrationLayoutJson, StringComparison.Ordinal)
            && before.ExplicitImageContextImageIds.SequenceEqual(after.ExplicitImageContextImageIds);
    }

    public static bool ManuscriptSemanticallyEquals(ProjectExportChapter? before, ProjectExportChapter? after)
    {
        if (ReferenceEquals(before, after))
            return true;
        if (before is null || after is null)
            return false;

        return before.Id == after.Id
            && string.Equals(before.ManuscriptJson, after.ManuscriptJson, StringComparison.Ordinal)
            && before.ManuscriptRevision == after.ManuscriptRevision;
    }

    public static bool MetadataSemanticallyEquals(ProjectExportChapter? before, ProjectExportChapter? after)
    {
        if (ReferenceEquals(before, after))
            return true;
        if (before is null || after is null)
            return false;

        return before.Id == after.Id
            && before.ActId == after.ActId
            && string.Equals(before.Title, after.Title, StringComparison.Ordinal)
            && string.Equals(before.Synopsis, after.Synopsis, StringComparison.Ordinal)
            && before.Order == after.Order
            && before.VisualMode == after.VisualMode
            && before.PageLayoutKind == after.PageLayoutKind
            && string.Equals(before.PageLayoutJson, after.PageLayoutJson, StringComparison.Ordinal)
            && string.Equals(before.IllustrationLayoutJson, after.IllustrationLayoutJson, StringComparison.Ordinal)
            && before.ExplicitImageContextImageIds.SequenceEqual(after.ExplicitImageContextImageIds);
    }
}

/// <summary>
/// A changed Designed Page composition attached to its owning chapter target.
/// The payload is read-only review data; target-scoped approval and restore
/// contracts own mutation while the composition service remains the canonical
/// authoring boundary.
/// </summary>
public sealed record ProjectVersionReviewComposition(
    Guid CompositionId,
    Guid? ChapterId,
    Guid? EditionId,
    ProjectExportPageComposition? Before,
    ProjectExportPageComposition? After)
{
    public bool HasChanges => !SemanticallyEquals(Before, After);

    public static bool SemanticallyEquals(
        ProjectExportPageComposition? before,
        ProjectExportPageComposition? after)
    {
        if (ReferenceEquals(before, after))
            return true;
        if (before is null || after is null)
            return false;

        return before.Id == after.Id
            && before.ChapterId == after.ChapterId
            && before.EditionId == after.EditionId
            && string.Equals(before.Name, after.Name, StringComparison.Ordinal)
            && string.Equals(before.SemanticManuscriptJson, after.SemanticManuscriptJson, StringComparison.Ordinal)
            && before.Revision == after.Revision
            && before.ActiveAuthoringVariantId == after.ActiveAuthoringVariantId
            && before.SourceCompositionId == after.SourceCompositionId
            && before.PublicationSectionId == after.PublicationSectionId
            && before.Variants
                .OrderBy(item => item.Id)
                .SequenceEqual(after.Variants.OrderBy(item => item.Id));
    }
}

/// <summary>
/// A review-owned grouping of changes that must be considered together by the
/// owning aggregate. This is deliberately richer than a raw area summary so
/// review surfaces can explain why an Other action is atomic.
/// </summary>
public sealed record ProjectVersionReviewDependencyGroup(
    string Key,
    string Label,
    bool IsAtomic,
    bool IsManuscriptScoped,
    IReadOnlyList<string> Areas,
    IReadOnlyList<string> Categories,
    IReadOnlyList<VersionHistorySnapshotChangeEntry> Entries);

/// <summary>
/// Current live canonical state compared with the approved Git head. The
/// comparison is null only before the project has an approved head checkpoint.
/// </summary>
public sealed record ProjectVersionReviewView(
    ProjectVersionRepositoryView Repository,
    ProjectVersionCheckpointView? ApprovedCheckpoint,
    ProjectVersionReviewConcurrencyToken Token,
    VersionHistorySnapshotComparison? Comparison,
    IReadOnlyList<ProjectVersionReviewChapter> Chapters,
    IReadOnlyList<ProjectVersionReviewDependencyGroup> DependencyGroups);

/// <summary>
/// The newest immutable checkpoint that changed one chapter target. This is
/// deliberately separate from the current review token: it describes a
/// historical comparison and must never be used as an approval token for the
/// live project.
/// </summary>
public sealed record ProjectVersionHistoricalChapterReview(
    Guid ChapterId,
    EditorContentTarget ContentTarget,
    GitCommitMetadata Commit,
    ProjectVersionCheckpointView? Checkpoint,
    ProjectExportChapter? Before,
    ProjectExportChapter? After,
    IReadOnlyList<ProjectVersionReviewComposition>? Compositions = null);

/// <summary>
/// A historical undo restores the full rich manuscript document through the
/// normal manuscript mutation boundary. It intentionally does not fabricate a
/// pending assistant change; the restored live state is reviewed against the
/// approved Git head on the next review load.
/// </summary>
public sealed record ProjectVersionHistoricalRestoreResult(
    Guid ChapterId,
    EditorContentTarget ContentTarget,
    string HistoricalCommitSha,
    long RestoredRevision,
    string RestoredSourceHash,
    ProjectVersionReviewConcurrencyToken ConcurrencyToken);

public sealed record ProjectVersionReviewBlockMutationResult(
    IReadOnlyList<string> BlockIds,
    ProjectVersionReviewConcurrencyToken ConcurrencyToken);

public sealed class ProjectVersionReviewConcurrencyException(string message) : InvalidOperationException(message);

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

    Task<int> ClearFailedOperationNoticesAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);

    Task<ProjectVersionStatusView?> GetStatusAsync(
        Guid projectId,
        bool includeCurrentSnapshotHash = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes the Review Edits workflow under the same project lease used for
    /// history. Enabling initializes the first approved HEAD if needed;
    /// disabling requires an already-clean live snapshot or the dedicated
    /// approval-and-disable operation below.
    /// </summary>
    Task SetReviewEditsEnabledAsync(
        Guid projectId,
        bool enabled,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Approves the complete live project and disables Review Edits as one
    /// serialized operation. The live snapshot and approved Git head must
    /// still match the supplied review token; a stale token fails closed.
    /// </summary>
    Task<ProjectVersionCheckpointView> ApproveAllAndDisableReviewEditsAsync(
        Guid projectId,
        ProjectVersionReviewConcurrencyToken expectedToken,
        string semanticMessage = "Approved Review Edits before disabling Review Edits",
        string? requestKey = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Compares the validated approved Git head with the current live SQLite
    /// snapshot and projects requested chapters into their exact content target.
    /// When no targets are supplied, changed Core and edition manuscript or
    /// chapter-attached composition targets are returned.
    /// </summary>
    Task<ProjectVersionReviewView?> GetReviewAsync(
        Guid projectId,
        IReadOnlyCollection<ProjectVersionReviewTarget>? targets = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns one target-scoped chapter review plus the concurrency token used
    /// to approve the loaded state.
    /// </summary>
    Task<ProjectVersionReviewChapter?> GetReviewChapterAsync(
        Guid projectId,
        Guid chapterId,
        EditorContentTarget contentTarget,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the newest Git checkpoint whose target-scoped chapter state
    /// differs from its first parent. The result is null when the target has
    /// never changed in the local history.
    /// </summary>
    Task<ProjectVersionHistoricalChapterReview?> GetLatestAffectingChapterAsync(
        Guid projectId,
        Guid chapterId,
        EditorContentTarget contentTarget,
        int maxCommits = 100,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores a chapter's rich manuscript document from the supplied
    /// historical snapshot after rechecking the live review token. Callers
    /// showing an affecting checkpoint should supply its first parent SHA so
    /// undo returns the chapter to the checkpoint's approved-side value. The
    /// restoration is a normal revision-checked live mutation, so the resulting
    /// state remains pending against the approved Git head until the user
    /// reviews it.
    /// </summary>
    Task<ProjectVersionHistoricalRestoreResult> RestoreHistoricalChapterAsync(
        Guid projectId,
        Guid chapterId,
        EditorContentTarget contentTarget,
        string historicalCommitSha,
        ProjectVersionReviewConcurrencyToken expectedToken,
        string semanticMessage = "Undo manuscript to historical checkpoint",
        string? requestKey = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a ReviewApproval checkpoint only if the live project and approved
    /// head still match the token captured by the Review surface.
    /// </summary>
    Task<ProjectVersionCheckpointView> CreateReviewApprovalCheckpointAsync(
        Guid projectId,
        ProjectVersionReviewConcurrencyToken expectedToken,
        string semanticMessage = "Approved Review Edits",
        string? requestKey = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Approves the complete semantic manuscript and all Designed Pages owned
    /// by one chapter/content target. Other chapter targets and non-manuscript
    /// changes remain pending against the new approved Git head.
    /// </summary>
    Task<ProjectVersionCheckpointView> CreateReviewApprovalForChapterAsync(
        Guid projectId,
        ProjectVersionReviewTarget target,
        ProjectVersionReviewConcurrencyToken expectedToken,
        string semanticMessage = "Approved chapter review changes",
        string? requestKey = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Approves only non-manuscript snapshot changes. The synthesized Git
    /// checkpoint keeps approved Core chapters and edition chapter overrides,
    /// so pending manuscript targets remain pending while project metadata and
    /// other major areas advance.
    /// </summary>
    Task<ProjectVersionCheckpointView> CreateReviewApprovalForOtherAsync(
        Guid projectId,
        ProjectVersionReviewConcurrencyToken expectedToken,
        string semanticMessage = "Approved other project changes",
        string? requestKey = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Approves only the selected stable manuscript blocks by committing a
    /// validated synthesized Git baseline. SQLite remains unchanged; the
    /// unselected live blocks stay pending against the new baseline.
    /// </summary>
    Task<ProjectVersionCheckpointView> CreateReviewApprovalForBlocksAsync(
        Guid projectId,
        ProjectVersionReviewTarget target,
        IReadOnlyCollection<string> blockIds,
        ProjectVersionReviewConcurrencyToken expectedToken,
        string semanticMessage = "Approved selected manuscript changes",
        string? requestKey = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Approves one changed Designed Page composition for the reviewed chapter
    /// target by synthesizing only that current composition into approved HEAD.
    /// SQLite remains unchanged; other manuscript and project changes stay
    /// pending against the new baseline.
    /// </summary>
    Task<ProjectVersionCheckpointView> ApproveReviewCompositionAsync(
        Guid projectId,
        ProjectVersionReviewTarget target,
        Guid compositionId,
        ProjectVersionReviewConcurrencyToken expectedToken,
        string semanticMessage = "Approved Designed Page change",
        string? requestKey = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores only the selected stable manuscript blocks from the approved
    /// Git baseline through the rich-document mutation boundary.
    /// </summary>
    Task<ProjectVersionReviewBlockMutationResult> RestoreReviewBlocksAsync(
        Guid projectId,
        ProjectVersionReviewTarget target,
        IReadOnlyCollection<string> blockIds,
        ProjectVersionReviewConcurrencyToken expectedToken,
        string semanticMessage = "Undid selected manuscript changes",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces one pending live manuscript block's text after validating the
    /// Review surface token. Inline marks and structural block metadata remain
    /// intact, and the edited live state remains pending against Git HEAD.
    /// </summary>
    Task<ProjectVersionReviewBlockMutationResult> EditReviewBlockAsync(
        Guid projectId,
        ProjectVersionReviewTarget target,
        string blockId,
        string text,
        ProjectVersionReviewConcurrencyToken expectedToken,
        CancellationToken cancellationToken = default);

    Task<ProjectVersionLoadedCheckpoint> LoadCheckpointAsync(
        Guid projectId,
        string commitSha,
        CancellationToken cancellationToken = default);

    Task<ProjectVersionLoadedCheckpoint> LoadCheckpointForComparisonAsync(
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
