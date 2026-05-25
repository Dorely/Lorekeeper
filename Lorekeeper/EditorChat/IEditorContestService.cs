using Lorekeeper.Models;

namespace Lorekeeper.EditorChat;

public interface IEditorContestService
{
    Task<EditorContestSettings> GetSettingsAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task SetContestModeEnabledAsync(Guid projectId, bool enabled, CancellationToken cancellationToken = default);
    Task SetContestProviderAsync(Guid projectId, int slot, int? providerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ContestBatch>> ListCurrentContestBatchesAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task DiscardInactiveContestBatchesAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task ResolveCandidateLineAsync(Guid projectId, Guid chapterId, ContestCandidateReviewLineResolution request, CancellationToken cancellationToken = default);
    Task KeepCandidateAsync(Guid candidateId, CancellationToken cancellationToken = default);
    Task FinishContestBatchAsync(Guid batchId, CancellationToken cancellationToken = default);
    IAsyncEnumerable<EditorContestRunUpdate> StartContestAsync(
        Guid projectId,
        Guid conversationId,
        Guid? assistantMessageId,
        EditorContestStartRequest request,
        ContestTurnSnapshot snapshot,
        CancellationToken cancellationToken = default);
}
