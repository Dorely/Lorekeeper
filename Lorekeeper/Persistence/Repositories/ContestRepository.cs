using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public sealed class ContestRepository(AppDatabaseReadOperation operation) : IContestRepository
{
    public Task<List<ContestBatch>> ListCurrentByProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        operation.Db.ContestBatches
            .AsNoTracking()
            .Include(batch => batch.Candidates.OrderBy(candidate => candidate.Order))
            .Where(batch => batch.ProjectId == projectId
                && (batch.Status == ContestBatchStatus.Running || batch.Status == ContestBatchStatus.Completed))
            .OrderByDescending(batch => batch.CreatedAt)
            .Take(1)
            .ToListAsync(cancellationToken);

    public async Task DeleteInactiveByProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var batches = await operation.Db.ContestBatches
            .Include(batch => batch.Candidates.OrderBy(candidate => candidate.Order))
            .Where(batch => batch.ProjectId == projectId
                && batch.Status != ContestBatchStatus.Running
                && batch.Status != ContestBatchStatus.Completed)
            .ToListAsync(cancellationToken);

        if (batches.Count == 0) return;

        operation.Db.ContestBatches.RemoveRange(batches);
    }

    public Task<ContestBatch?> GetBatchAsync(Guid batchId, CancellationToken cancellationToken = default) =>
        operation.Db.ContestBatches
            .Include(batch => batch.Candidates.OrderBy(candidate => candidate.Order))
            .FirstOrDefaultAsync(batch => batch.Id == batchId, cancellationToken);

    public Task<ContestCandidate?> GetCandidateAsync(Guid candidateId, CancellationToken cancellationToken = default) =>
        operation.Db.ContestCandidates
            .Include(candidate => candidate.Batch)
                .ThenInclude(batch => batch.Candidates.OrderBy(candidate => candidate.Order))
            .FirstOrDefaultAsync(candidate => candidate.Id == candidateId, cancellationToken);

    public async Task AddBatchAsync(ContestBatch batch, CancellationToken cancellationToken = default) =>
        await operation.Db.ContestBatches.AddAsync(batch, cancellationToken);

    public async Task AddCandidateAsync(ContestCandidate candidate, CancellationToken cancellationToken = default) =>
        await operation.Db.ContestCandidates.AddAsync(candidate, cancellationToken);

    public void UpdateBatch(ContestBatch batch) => operation.Db.MarkModified(batch);

    public void UpdateCandidate(ContestCandidate candidate) => operation.Db.MarkModified(candidate);
}
