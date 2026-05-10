using Lorekeeper.Models;

namespace Lorekeeper.EditorChat;

public interface IEditorContestService
{
    Task<EditorContestSettings> GetSettingsAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task SetContestModeEnabledAsync(Guid projectId, bool enabled, CancellationToken cancellationToken = default);
    Task SetContestProviderAsync(Guid projectId, int slot, int? providerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ContestBatch>> ListContestBatchesAsync(Guid projectId, CancellationToken cancellationToken = default);
    IAsyncEnumerable<EditorContestRunUpdate> StartContestAsync(
        Guid projectId,
        Guid conversationId,
        Guid? assistantMessageId,
        EditorContestStartRequest request,
        ContestTurnSnapshot snapshot,
        CancellationToken cancellationToken = default);
    Task StageCandidateAsync(Guid candidateId, CancellationToken cancellationToken = default);
}
