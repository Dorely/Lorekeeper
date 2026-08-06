using Lorekeeper.Models;

namespace Lorekeeper.Persistence.Repositories;

public interface IAiChangeRepository
{
    Task<List<AiChangeBatch>> ListPendingBatchesAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<List<AiChange>> ListPendingRevisionWorkerChangesAsync(
        Guid projectId,
        Guid conversationId,
        Guid? assistantMessageId,
        string toolCallId,
        CancellationToken cancellationToken = default);
    Task<AiChangeBatch?> GetBatchAsync(Guid batchId, CancellationToken cancellationToken = default);
    Task<AiChange?> GetChangeAsync(Guid changeId, CancellationToken cancellationToken = default);
    Task AddBatchAsync(AiChangeBatch batch, CancellationToken cancellationToken = default);
    Task AddChangeAsync(AiChange change, CancellationToken cancellationToken = default);
    void UpdateBatch(AiChangeBatch batch);
    void UpdateChange(AiChange change);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
