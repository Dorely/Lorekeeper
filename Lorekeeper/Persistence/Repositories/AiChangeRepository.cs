using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public sealed class AiChangeRepository(AppDbContext db) : IAiChangeRepository
{
    public Task<List<AiChangeBatch>> ListPendingBatchesAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        db.AiChangeBatches
            .AsNoTracking()
            .Include(b => b.Changes.OrderBy(c => c.Order))
            .Where(b => b.ProjectId == projectId && b.Status == AiChangeBatchStatus.Pending)
            .OrderBy(b => b.CreatedAt)
            .ToListAsync(cancellationToken);

    public Task<List<AiChange>> ListPendingRevisionWorkerChangesAsync(
        Guid projectId,
        Guid conversationId,
        Guid? assistantMessageId,
        string toolCallId,
        CancellationToken cancellationToken = default) =>
        db.AiChanges
            .AsNoTracking()
            .Include(change => change.Batch)
            .Where(change =>
                change.Batch.ProjectId == projectId
                && change.Batch.ConversationKind == AiChangeConversationKind.Editor
                && change.Batch.ConversationId == conversationId
                && change.Batch.AssistantMessageId == assistantMessageId
                && change.ToolCallId == toolCallId
                && change.ToolName == "apply_assigned_manuscript_operations"
                && change.Status == AiChangeStatus.Pending)
            .OrderBy(change => change.CreatedAt)
            .ToListAsync(cancellationToken);

    public Task<AiChangeBatch?> GetBatchAsync(Guid batchId, CancellationToken cancellationToken = default) =>
        db.AiChangeBatches
            .Include(b => b.Changes.OrderBy(c => c.Order))
            .FirstOrDefaultAsync(b => b.Id == batchId, cancellationToken);

    public Task<AiChange?> GetChangeAsync(Guid changeId, CancellationToken cancellationToken = default) =>
        db.AiChanges
            .Include(c => c.Batch)
                .ThenInclude(b => b.Changes.OrderBy(c => c.Order))
            .FirstOrDefaultAsync(c => c.Id == changeId, cancellationToken);

    public async Task AddBatchAsync(AiChangeBatch batch, CancellationToken cancellationToken = default) =>
        await db.AiChangeBatches.AddAsync(batch, cancellationToken);

    public async Task AddChangeAsync(AiChange change, CancellationToken cancellationToken = default) =>
        await db.AiChanges.AddAsync(change, cancellationToken);

    public void UpdateBatch(AiChangeBatch batch) => db.AiChangeBatches.Update(batch);

    public void UpdateChange(AiChange change) => db.AiChanges.Update(change);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        db.SaveChangesAsync(cancellationToken);
}
