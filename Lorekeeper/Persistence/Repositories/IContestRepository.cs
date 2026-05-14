using Lorekeeper.Models;

namespace Lorekeeper.Persistence.Repositories;

public interface IContestRepository
{
    Task<List<ContestBatch>> ListCurrentByProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task DeleteInactiveByProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<ContestBatch?> GetBatchAsync(Guid batchId, CancellationToken cancellationToken = default);
    Task<ContestCandidate?> GetCandidateAsync(Guid candidateId, CancellationToken cancellationToken = default);
    Task AddBatchAsync(ContestBatch batch, CancellationToken cancellationToken = default);
    Task AddCandidateAsync(ContestCandidate candidate, CancellationToken cancellationToken = default);
    void UpdateBatch(ContestBatch batch);
    void UpdateCandidate(ContestCandidate candidate);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
