using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public class GraphNodeRepository(AppDatabaseReadOperation operation) : IGraphNodeRepository
{
    public Task<GraphNode?> GetByIdAsync(long id, CancellationToken cancellationToken = default) =>
        operation.Db.GraphNodes.FirstOrDefaultAsync(n => n.Id == id, cancellationToken);

    public Task<GraphNode?> FindAsync(Guid projectId, string nodeType, string key, CancellationToken cancellationToken = default) =>
        operation.Db.GraphNodes.FirstOrDefaultAsync(n => n.ProjectId == projectId && n.NodeType == nodeType && n.Key == key, cancellationToken);

    public Task<GraphNode?> FindByKeyAsync(Guid projectId, string key, CancellationToken cancellationToken = default) =>
        operation.Db.GraphNodes.SingleOrDefaultAsync(n => n.ProjectId == projectId && n.Key == key, cancellationToken);

    public async Task<List<GraphNode>> ListByProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await operation.Db.GraphNodes
            .AsNoTracking()
            .Where(n => n.ProjectId == projectId)
            .OrderBy(n => n.NodeType)
            .ThenBy(n => n.Label ?? n.Key)
            .ThenBy(n => n.Key)
            .ToListAsync(cancellationToken);

    public async Task<List<GraphNode>> ListByTypeAsync(Guid projectId, string nodeType, CancellationToken cancellationToken = default) =>
        await operation.Db.GraphNodes
            .AsNoTracking()
            .Where(n => n.ProjectId == projectId && n.NodeType == nodeType)
            .OrderBy(n => n.Label ?? n.Key)
            .ThenBy(n => n.Key)
            .ToListAsync(cancellationToken);

    public Task<List<string>> ListTypesAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        operation.Db.GraphNodes
            .Where(n => n.ProjectId == projectId)
            .Select(n => n.NodeType)
            .Distinct()
            .OrderBy(t => t)
            .ToListAsync(cancellationToken);

    public async Task<List<GraphNode>> GetByIdsAsync(IEnumerable<long> ids, CancellationToken cancellationToken = default)
    {
        var idList = ids.ToList();
        if (idList.Count == 0) return [];
        return await operation.Db.GraphNodes.AsNoTracking().Where(n => idList.Contains(n.Id)).ToListAsync(cancellationToken);
    }

    public async Task AddAsync(GraphNode node, CancellationToken cancellationToken = default) =>
        await operation.Db.GraphNodes.AddAsync(node, cancellationToken);

    public void Update(GraphNode node) => operation.Db.MarkModified(node);

    public void Remove(GraphNode node) => operation.Db.MarkDeleted(node);
}
