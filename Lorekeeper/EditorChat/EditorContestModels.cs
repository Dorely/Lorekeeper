using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.EntityVisuals;
using Lorekeeper.Manuscripts;

namespace Lorekeeper.EditorChat;

public sealed record EditorContestSettings(
    bool Enabled,
    int? ProviderSlot1Id,
    int? ProviderSlot2Id,
    int? ProviderSlot3Id);

public sealed record EditorContestStartRequest(
    Guid ChapterId,
    EditorContentTarget ContentTarget,
    string Task,
    // Retained for deserializing schema-v1 contest envelopes; new contests use
    // nullable boundary anchors instead of a selected block-ID list.
    IReadOnlyList<string>? TargetBlockIds,
    long ExpectedRevision,
    string? BeforeBlockId = null,
    string? AfterBlockId = null);

public sealed record ContestTurnSnapshot(
    IReadOnlyList<ContestChatMessageSnapshot> Messages,
    IReadOnlyList<EntityVisualContextReference> Visuals);

public sealed record ContestChatMessageSnapshot(
    string Role,
    string Content);

/// <summary>
/// Immutable, versioned evidence captured at the moment the coordinator starts
/// a contest. The old contest rows contain a bare <see cref="ContestTurnSnapshot" />;
/// keeping this as a new envelope lets the runtime continue to recognize those
/// rows while making new contests self-describing and restart-safe.
/// </summary>
public sealed record ContestContextEnvelope(
    int SchemaVersion,
    EditorContestStartRequest Request,
    ContestTurnSnapshot Snapshot)
{
    public const int CurrentSchemaVersion = 2;
}

public abstract record EditorContestRunUpdate;

public sealed record EditorContestStarted(Guid BatchId) : EditorContestRunUpdate;

public sealed record EditorContestCandidateUpdated(Guid BatchId, Guid CandidateId, ContestCandidateStatus Status) : EditorContestRunUpdate;

public sealed record EditorContestCandidateRawResponseDelta(
    Guid BatchId,
    Guid CandidateId,
    string Delta,
    string RawResponse) : EditorContestRunUpdate;

public sealed record EditorContestCompleted(Guid BatchId, ContestBatchStatus Status) : EditorContestRunUpdate;

public sealed record ContestCandidateProvider(
    int Id,
    string Name,
    string ModelName);

public sealed record ContestCandidateReviewLineResolution(
    Guid CandidateId,
    string BlockId,
    int? PairId,
    int? OldLineNumber,
    int? NewLineNumber,
    string? OldText,
    string? NewText,
    ChapterBodyReviewLineAction Action,
    string? EditedText = null,
    string? ExpectedDraftHash = null);

/// <summary>
/// The complete contest review projection. Candidate drafts are returned as
/// independent semantic manuscript documents; the projection never represents
/// an accepted or merged live manuscript.
/// </summary>
public sealed record EditorContestReviewSnapshot(
    Guid BatchId,
    Guid ProjectId,
    Guid ChapterId,
    EditorContentTarget ContentTarget,
    ContestBatchStatus Status,
    string OriginalManuscriptJson,
    long OriginalManuscriptRevision,
    string OriginalManuscriptHash,
    Guid? SelectedCandidateId,
    IReadOnlyList<EditorContestCandidateReviewSnapshot> Candidates);

public sealed record EditorContestCandidateReviewSnapshot(
    Guid CandidateId,
    int Order,
    string ProviderName,
    string ModelName,
    ContestCandidateStatus Status,
    string Summary,
    string DraftManuscriptJson,
    string? ErrorMessage,
    bool IsTerminal,
    string? DraftHash = null,
    string RawResponse = "",
    string? Notes = null,
    double? DurationMs = null);

public sealed record EditorContestLockState(
    bool IsLocked,
    Guid? BatchId,
    Guid? ChapterId,
    string? ChapterTitle,
    ContestBatchStatus? Status,
    string? Message)
{
    public static EditorContestLockState Unlocked { get; } = new(
        false,
        null,
        null,
        null,
        null,
        null);
}
