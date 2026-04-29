using Lorekeeper.Models;

namespace Lorekeeper.Persistence.Repositories;

public interface IGraphNodeRepository
{
    Task<GraphNode?> GetByIdAsync(long id, CancellationToken cancellationToken = default);
    Task<GraphNode?> FindAsync(Guid projectId, string nodeType, string key, CancellationToken cancellationToken = default);
    Task<List<GraphNode>> GetByIdsAsync(IEnumerable<long> ids, CancellationToken cancellationToken = default);
    Task AddAsync(GraphNode node, CancellationToken cancellationToken = default);
    void Update(GraphNode node);
    void Remove(GraphNode node);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
