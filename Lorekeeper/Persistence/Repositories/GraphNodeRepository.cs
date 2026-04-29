using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public class GraphNodeRepository(AppDbContext db) : IGraphNodeRepository
{
    public Task<GraphNode?> GetByIdAsync(long id, CancellationToken cancellationToken = default) =>
        db.GraphNodes.FirstOrDefaultAsync(n => n.Id == id, cancellationToken);

    public Task<GraphNode?> FindAsync(string nodeType, string key, CancellationToken cancellationToken = default) =>
        db.GraphNodes.FirstOrDefaultAsync(n => n.NodeType == nodeType && n.Key == key, cancellationToken);

    public async Task<List<GraphNode>> GetByIdsAsync(IEnumerable<long> ids, CancellationToken cancellationToken = default)
    {
        var idList = ids.ToList();
        if (idList.Count == 0) return [];
        return await db.GraphNodes.Where(n => idList.Contains(n.Id)).ToListAsync(cancellationToken);
    }

    public async Task AddAsync(GraphNode node, CancellationToken cancellationToken = default) =>
        await db.GraphNodes.AddAsync(node, cancellationToken);

    public void Update(GraphNode node) => db.GraphNodes.Update(node);

    public void Remove(GraphNode node) => db.GraphNodes.Remove(node);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        db.SaveChangesAsync(cancellationToken);
}
