using Lorekeeper.Models;

namespace Lorekeeper.Persistence.Repositories;

public interface IWebIngestCandidateRepository
{
    Task<List<WebIngestCandidate>> ListByProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<List<WebIngestCandidate>> ListResearchByProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<List<WebIngestCandidate>> ListStagedByProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<List<WebIngestCandidate>> ListStagedByProjectAsync(Guid projectId, Guid? researchConversationId, CancellationToken cancellationToken = default);
    Task<List<WebIngestCandidate>> ListByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default);
    Task<WebIngestCandidate?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<WebIngestCandidate?> FindByUrlAsync(Guid projectId, string url, CancellationToken cancellationToken = default);
    Task AddAsync(WebIngestCandidate candidate, CancellationToken cancellationToken = default);
    void Update(WebIngestCandidate candidate);
    void Remove(WebIngestCandidate candidate);
}
