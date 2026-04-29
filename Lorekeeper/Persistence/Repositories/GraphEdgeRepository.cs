using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public class GraphEdgeRepository(AppDbContext db) : IGraphEdgeRepository
{
    public Task<GraphEdge?> GetByIdAsync(long id, CancellationToken cancellationToken = default) =>
        db.GraphEdges.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    public async Task<List<GraphEdge>> GetAdjacentAsync(
        long nodeId,
        EdgeDirection direction,
        IReadOnlyCollection<string>? edgeTypes,
        int? maxResults,
        CancellationToken cancellationToken = default)
    {
        IQueryable<GraphEdge> query = db.GraphEdges;

        query = direction switch
        {
            EdgeDirection.Outgoing => query.Where(e => e.FromNodeId == nodeId),
            EdgeDirection.Incoming => query.Where(e => e.ToNodeId == nodeId),
            EdgeDirection.Both => query.Where(e => e.FromNodeId == nodeId || e.ToNodeId == nodeId),
            _ => throw new ArgumentOutOfRangeException(nameof(direction))
        };

        if (edgeTypes is { Count: > 0 })
        {
            var typeList = edgeTypes.ToList();
            query = query.Where(e => typeList.Contains(e.EdgeType));
        }

        if (maxResults is int max)
            query = query.Take(max);

        return await query.ToListAsync(cancellationToken);
    }

    public Task<GraphEdge?> FindAsync(long fromId, long toId, string edgeType, CancellationToken cancellationToken = default) =>
        db.GraphEdges.FirstOrDefaultAsync(
            e => e.FromNodeId == fromId && e.ToNodeId == toId && e.EdgeType == edgeType,
            cancellationToken);

    public async Task AddAsync(GraphEdge edge, CancellationToken cancellationToken = default) =>
        await db.GraphEdges.AddAsync(edge, cancellationToken);

    public void Update(GraphEdge edge) => db.GraphEdges.Update(edge);

    public void Remove(GraphEdge edge) => db.GraphEdges.Remove(edge);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        db.SaveChangesAsync(cancellationToken);
}
