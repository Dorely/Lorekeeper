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
    EditorContentTarget ContentTarget);

public sealed record ContestTurnSnapshot(
    IReadOnlyList<ContestChatMessageSnapshot> Messages,
    IReadOnlyList<EntityVisualContextReference> Visuals);

public sealed record ContestChatMessageSnapshot(
    string Role,
    string Content);

public abstract record EditorContestRunUpdate;

public sealed record EditorContestStarted(Guid BatchId) : EditorContestRunUpdate;

public sealed record EditorContestCandidateUpdated(Guid BatchId, Guid CandidateId, ContestCandidateStatus Status) : EditorContestRunUpdate;

public sealed record EditorContestCandidateRawResponseDelta(
    Guid BatchId,
    Guid CandidateId,
    string Delta,
    string RawResponse) : EditorContestRunUpdate;

public sealed record EditorContestCompleted(Guid BatchId, ContestBatchStatus Status) : EditorContestRunUpdate;

public sealed record ContestCandidateResponse(
    string Summary,
    long ExpectedRevision,
    IReadOnlyList<ManuscriptOperationInput> Operations,
    string? Notes = null);

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
    string? EditedText = null);
