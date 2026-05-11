using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public sealed class ContestRepository(AppDbContext db) : IContestRepository
{
    public Task<List<ContestBatch>> ListCurrentByProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        db.ContestBatches
            .Include(batch => batch.Candidates.OrderBy(candidate => candidate.Order))
            .Where(batch => batch.ProjectId == projectId && batch.Status != ContestBatchStatus.Cancelled)
            .OrderByDescending(batch => batch.CreatedAt)
            .Take(1)
            .ToListAsync(cancellationToken);

    public async Task<List<ContestBatch>> ListHistoryByProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var latestBatchId = await db.ContestBatches
            .Where(batch => batch.ProjectId == projectId && batch.Status != ContestBatchStatus.Cancelled)
            .OrderByDescending(batch => batch.CreatedAt)
            .Select(batch => (Guid?)batch.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (latestBatchId is null)
            return [];

        return await db.ContestBatches
            .Include(batch => batch.Candidates.OrderBy(candidate => candidate.Order))
            .Where(batch => batch.ProjectId == projectId
                && batch.Status != ContestBatchStatus.Cancelled
                && batch.Id != latestBatchId.Value)
            .OrderByDescending(batch => batch.CreatedAt)
            .Take(10)
            .ToListAsync(cancellationToken);
    }

    public Task<ContestBatch?> GetBatchAsync(Guid batchId, CancellationToken cancellationToken = default) =>
        db.ContestBatches
            .Include(batch => batch.Candidates.OrderBy(candidate => candidate.Order))
            .FirstOrDefaultAsync(batch => batch.Id == batchId, cancellationToken);

    public Task<ContestCandidate?> GetCandidateAsync(Guid candidateId, CancellationToken cancellationToken = default) =>
        db.ContestCandidates
            .Include(candidate => candidate.Batch)
                .ThenInclude(batch => batch.Candidates.OrderBy(candidate => candidate.Order))
            .FirstOrDefaultAsync(candidate => candidate.Id == candidateId, cancellationToken);

    public async Task AddBatchAsync(ContestBatch batch, CancellationToken cancellationToken = default) =>
        await db.ContestBatches.AddAsync(batch, cancellationToken);

    public async Task AddCandidateAsync(ContestCandidate candidate, CancellationToken cancellationToken = default) =>
        await db.ContestCandidates.AddAsync(candidate, cancellationToken);

    public void UpdateBatch(ContestBatch batch) => db.ContestBatches.Update(batch);

    public void UpdateCandidate(ContestCandidate candidate) => db.ContestCandidates.Update(candidate);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        db.SaveChangesAsync(cancellationToken);
}
