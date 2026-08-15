using Lorekeeper.Models;

namespace Lorekeeper.Persistence.Repositories;

public interface IGraphEntityTypeRepository
{
    Task<List<GraphEntityType>> ListByProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<GraphEntityType?> FindAsync(Guid projectId, string type, CancellationToken cancellationToken = default);
    Task AddAsync(GraphEntityType entityType, CancellationToken cancellationToken = default);
    void Update(GraphEntityType entityType);
}
