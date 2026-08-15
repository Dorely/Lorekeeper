using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public class ActRepository(AppDatabaseReadOperation operation) : IActRepository
{
    public Task<List<Act>> ListByProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        operation.Db.Acts.AsNoTracking().Where(a => a.ProjectId == projectId)
               .OrderBy(a => a.Order)
               .ToListAsync(cancellationToken);

    public Task<Act?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        operation.Db.Acts.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

    public async Task<int> GetMaxOrderAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var any = await operation.Db.Acts.AnyAsync(a => a.ProjectId == projectId, cancellationToken);
        if (!any) return -1;
        return await operation.Db.Acts.Where(a => a.ProjectId == projectId).MaxAsync(a => a.Order, cancellationToken);
    }

    public async Task AddAsync(Act act, CancellationToken cancellationToken = default) =>
        await operation.Db.Acts.AddAsync(act, cancellationToken);

    public void Update(Act act) => operation.Db.MarkModified(act);

    public void Remove(Act act) => operation.Db.MarkDeleted(act);

    public async Task ReorderAsync(Guid projectId, IReadOnlyList<Guid> orderedIds, CancellationToken cancellationToken = default)
    {
        var acts = await operation.Db.Acts
            .Where(a => a.ProjectId == projectId)
            .ToListAsync(cancellationToken);

        var byId = acts.ToDictionary(a => a.Id);
        for (var i = 0; i < orderedIds.Count; i++)
        {
            if (byId.TryGetValue(orderedIds[i], out var act) && act.Order != i)
            {
                act.Order = i;
                act.UpdatedAt = DateTime.UtcNow;
            }
        }
    }
}
