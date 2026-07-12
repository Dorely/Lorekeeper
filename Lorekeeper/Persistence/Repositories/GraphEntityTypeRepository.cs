using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public class GraphEntityTypeRepository(AppDbContext db) : IGraphEntityTypeRepository
{
    public Task<List<GraphEntityType>> ListByProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        db.GraphEntityTypes
            .AsNoTracking()
            .Where(t => t.ProjectId == projectId)
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.PluralLabel)
            .ThenBy(t => t.Type)
            .ToListAsync(cancellationToken);

    public Task<GraphEntityType?> FindAsync(Guid projectId, string type, CancellationToken cancellationToken = default) =>
        db.GraphEntityTypes.FirstOrDefaultAsync(
            t => t.ProjectId == projectId && t.Type == type,
            cancellationToken);

    public async Task AddAsync(GraphEntityType entityType, CancellationToken cancellationToken = default) =>
        await db.GraphEntityTypes.AddAsync(entityType, cancellationToken);

    public void Update(GraphEntityType entityType) => db.GraphEntityTypes.Update(entityType);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        db.SaveChangesAsync(cancellationToken);
}
