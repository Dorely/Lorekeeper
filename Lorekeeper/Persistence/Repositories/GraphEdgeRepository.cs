using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public class GraphEdgeRepository(AppDatabaseReadOperation operation) : IGraphEdgeRepository
{
    public Task<GraphEdge?> GetByIdAsync(long id, CancellationToken cancellationToken = default) =>
        operation.Db.GraphEdges.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    public async Task<List<GraphEdge>> GetAdjacentAsync(
        long nodeId,
        EdgeDirection direction,
        IReadOnlyCollection<string>? edgeTypes,
        int? maxResults,
        CancellationToken cancellationToken = default)
    {
        IQueryable<GraphEdge> query = operation.Db.GraphEdges.AsNoTracking();

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

        query = query
            .OrderBy(e => e.SortOrder ?? int.MaxValue)
            .ThenBy(e => e.CreatedAt)
            .ThenBy(e => e.Id);

        if (maxResults is int max)
            query = query.Take(max);

        return await query.ToListAsync(cancellationToken);
    }

    public async Task<List<GraphEdge>> ListByProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await operation.Db.GraphEdges
            .AsNoTracking()
            .Where(e => e.FromNode.ProjectId == projectId && e.ToNode.ProjectId == projectId)
            .OrderBy(e => e.EdgeType)
            .ThenBy(e => e.SortOrder ?? int.MaxValue)
            .ThenBy(e => e.CreatedAt)
            .ThenBy(e => e.Id)
            .ToListAsync(cancellationToken);

    public Task<GraphEdge?> FindAsync(long fromId, long toId, string edgeType, CancellationToken cancellationToken = default) =>
        operation.Db.GraphEdges.FirstOrDefaultAsync(
            e => e.FromNodeId == fromId && e.ToNodeId == toId && e.EdgeType == edgeType,
            cancellationToken);

    public async Task AddAsync(GraphEdge edge, CancellationToken cancellationToken = default) =>
        await operation.Db.GraphEdges.AddAsync(edge, cancellationToken);

    public void Update(GraphEdge edge) => operation.Db.MarkModified(edge);

    public void Remove(GraphEdge edge) => operation.Db.MarkDeleted(edge);
}
