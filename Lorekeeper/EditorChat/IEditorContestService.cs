using Lorekeeper.Models;

namespace Lorekeeper.EditorChat;

public interface IEditorContestService
{
    Task<EditorContestSettings> GetSettingsAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task SetContestModeEnabledAsync(Guid projectId, bool enabled, CancellationToken cancellationToken = default);
    Task SetContestProviderAsync(Guid projectId, int slot, int? providerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ContestBatch>> ListCurrentContestBatchesAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<EditorContestReviewSnapshot?> GetReviewAsync(Guid projectId, Guid batchId, CancellationToken cancellationToken = default);
    Task<EditorContestLockState> GetEditorLockStateAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task EnsureEditorMutationAllowedAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task DiscardInactiveContestBatchesAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task SelectCandidateAsync(Guid projectId, Guid batchId, Guid candidateId, CancellationToken cancellationToken = default);
    Task ResetCandidateAsync(Guid projectId, Guid candidateId, CancellationToken cancellationToken = default);
    Task ResolveCandidateLineAsync(Guid projectId, Guid chapterId, ContestCandidateReviewLineResolution request, CancellationToken cancellationToken = default);
    Task ResolveContestBatchAsync(Guid projectId, Guid batchId, CancellationToken cancellationToken = default);
    Task DiscardContestBatchAsync(Guid projectId, Guid batchId, CancellationToken cancellationToken = default);
    Task CancelContestBatchAsync(Guid projectId, Guid batchId, CancellationToken cancellationToken = default);
    Task KeepCandidateAsync(Guid projectId, Guid candidateId, CancellationToken cancellationToken = default);
    Task FinishContestBatchAsync(Guid projectId, Guid batchId, CancellationToken cancellationToken = default);
    IAsyncEnumerable<EditorContestRunUpdate> StartContestAsync(
        Guid projectId,
        Guid conversationId,
        Guid? assistantMessageId,
        EditorContestStartRequest request,
        ContestTurnSnapshot snapshot,
        CancellationToken cancellationToken = default);
}
