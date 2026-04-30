using Lorekeeper.Models;

namespace Lorekeeper.Persistence.Repositories;

public interface IGraphNodeRepository
{
    Task<GraphNode?> GetByIdAsync(long id, CancellationToken cancellationToken = default);
    Task<GraphNode?> FindAsync(Guid projectId, string nodeType, string key, CancellationToken cancellationToken = default);
    /// <summary>
    /// Returns the single node with the given <paramref name="key"/> within the project, regardless of
    /// node type. Useful when chat tools refer to entities by id without remembering their type.
    /// Returns null when no node matches; throws if multiple share the key (callers should ensure
    /// project-wide unique keys, e.g. by generating GUIDs).
    /// </summary>
    Task<GraphNode?> FindByKeyAsync(Guid projectId, string key, CancellationToken cancellationToken = default);
    /// <summary>Returns every node in the project of the given type, ordered by <c>Label</c> then key.</summary>
    Task<List<GraphNode>> ListByTypeAsync(Guid projectId, string nodeType, CancellationToken cancellationToken = default);
    Task<List<GraphNode>> GetByIdsAsync(IEnumerable<long> ids, CancellationToken cancellationToken = default);
    Task AddAsync(GraphNode node, CancellationToken cancellationToken = default);
    void Update(GraphNode node);
    void Remove(GraphNode node);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
