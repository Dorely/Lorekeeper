using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public class ChapterRepository(AppDatabaseReadOperation operation) : IChapterRepository
{
    public Task<List<Chapter>> ListByProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        operation.Db.Chapters.AsNoTracking().Where(c => c.ProjectId == projectId)
                   .OrderBy(c => c.Order)
                   .ToListAsync(cancellationToken);

    public Task<Chapter?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        operation.Db.Chapters.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public async Task<Chapter?> ReloadFromStoreAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var trackedEntry = operation.Db.ChangeTracker
            .Entries<Chapter>()
            .FirstOrDefault(entry => entry.Entity.Id == id);

        if (trackedEntry is null)
            return await operation.Db.Chapters.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        if (trackedEntry.State == EntityState.Deleted)
            return null;

        await trackedEntry.ReloadAsync(cancellationToken);
        return trackedEntry.State is EntityState.Detached or EntityState.Deleted
            ? null
            : trackedEntry.Entity;
    }

    public async Task<int> GetMaxOrderAsync(Guid projectId, Guid? actId, CancellationToken cancellationToken = default)
    {
        var bucket = operation.Db.Chapters.Where(c => c.ProjectId == projectId && c.ActId == actId);
        var any = await bucket.AnyAsync(cancellationToken);
        if (!any) return -1;
        return await bucket.MaxAsync(c => c.Order, cancellationToken);
    }

    public Task<int> CountByProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        operation.Db.Chapters.CountAsync(c => c.ProjectId == projectId, cancellationToken);

    public async Task AddAsync(Chapter chapter, CancellationToken cancellationToken = default) =>
        await operation.Db.Chapters.AddAsync(chapter, cancellationToken);

    public void Update(Chapter chapter) => operation.Db.MarkModified(chapter);

    public void Remove(Chapter chapter) => operation.Db.MarkDeleted(chapter);

    public async Task ReorderAsync(Guid projectId, Guid? actId, IReadOnlyList<Guid> orderedIds, CancellationToken cancellationToken = default)
    {
        var chapters = await operation.Db.Chapters
            .Where(c => c.ProjectId == projectId && c.ActId == actId)
            .ToListAsync(cancellationToken);

        var byId = chapters.ToDictionary(c => c.Id);
        for (var i = 0; i < orderedIds.Count; i++)
        {
            if (byId.TryGetValue(orderedIds[i], out var ch) && ch.Order != i)
            {
                ch.Order = i;
                ch.UpdatedAt = DateTime.UtcNow;
            }
        }
    }
}
