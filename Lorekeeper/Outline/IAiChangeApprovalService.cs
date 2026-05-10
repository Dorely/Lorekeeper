using Lorekeeper.Models;

namespace Lorekeeper.Outline;

public interface IAiChangeApprovalService
{
    Task<IReadOnlyList<AiChangeBatch>> ListPendingBatchesAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<AiChangeBatch?> GetBatchAsync(Guid batchId, CancellationToken cancellationToken = default);
    Task ApplyChangeAsync(Guid changeId, CancellationToken cancellationToken = default);
    Task RejectChangeAsync(Guid changeId, string? message, CancellationToken cancellationToken = default);
    Task ApplyBatchAsync(Guid batchId, CancellationToken cancellationToken = default);
    Task RejectBatchAsync(Guid batchId, string? message, CancellationToken cancellationToken = default);
}