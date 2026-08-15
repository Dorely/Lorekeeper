using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public class GraphEntityTypeRepository(AppDatabaseReadOperation operation) : IGraphEntityTypeRepository
{
    public Task<List<GraphEntityType>> ListByProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        operation.Db.GraphEntityTypes
            .AsNoTracking()
            .Where(t => t.ProjectId == projectId)
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.PluralLabel)
            .ThenBy(t => t.Type)
            .ToListAsync(cancellationToken);

    public Task<GraphEntityType?> FindAsync(Guid projectId, string type, CancellationToken cancellationToken = default) =>
        operation.Db.GraphEntityTypes.FirstOrDefaultAsync(
            t => t.ProjectId == projectId && t.Type == type,
            cancellationToken);

    public async Task AddAsync(GraphEntityType entityType, CancellationToken cancellationToken = default) =>
        await operation.Db.GraphEntityTypes.AddAsync(entityType, cancellationToken);

    public void Update(GraphEntityType entityType) => operation.Db.MarkModified(entityType);
}
