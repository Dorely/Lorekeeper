using Lorekeeper.Models;

namespace Lorekeeper.Persistence.Repositories;

public interface IGraphEdgeRepository
{
    Task<GraphEdge?> GetByIdAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns edges adjacent to <paramref name="nodeId"/> filtered by direction and (optionally) edge type.
    /// </summary>
    Task<List<GraphEdge>> GetAdjacentAsync(
        long nodeId,
        EdgeDirection direction,
        IReadOnlyCollection<string>? edgeTypes,
        int? maxResults,
        CancellationToken cancellationToken = default);

    /// <summary>Returns every edge whose endpoints both belong to the project.</summary>
    Task<List<GraphEdge>> ListByProjectAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<GraphEdge?> FindAsync(long fromId, long toId, string edgeType, CancellationToken cancellationToken = default);
    Task AddAsync(GraphEdge edge, CancellationToken cancellationToken = default);
    void Update(GraphEdge edge);
    void Remove(GraphEdge edge);
}

public enum EdgeDirection
{
    Outgoing,
    Incoming,
    Both
}
