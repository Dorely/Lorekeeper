using Lorekeeper.Models;
using Lorekeeper.Outline;

namespace Lorekeeper.EditorChat;

public sealed record EditorContestSettings(
    bool Enabled,
    int? ProviderSlot1Id,
    int? ProviderSlot2Id,
    int? ProviderSlot3Id);

public sealed record EditorContestStartRequest(
    Guid ChapterId);

public sealed record ContestTurnSnapshot(
    IReadOnlyList<ContestChatMessageSnapshot> Messages);

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
    IReadOnlyList<ContestChapterMutation> Mutations,
    string? Notes = null);

public sealed record ContestChapterMutation(
    string MutationKind,
    int? StartLine,
    int? EndLine,
    string ReplacementText,
    string? Rationale = null);

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
