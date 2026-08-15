using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public class ProjectRepository(AppDatabaseReadOperation operation) : IProjectRepository
{
    public Task<List<Project>> ListAsync(CancellationToken cancellationToken = default) =>
        operation.Db.Projects.AsNoTracking().OrderByDescending(p => p.UpdatedAt).ToListAsync(cancellationToken);

    public Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        operation.Db.Projects.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public Task<Project?> GetSnapshotByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        operation.Db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public Task<Project?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
        operation.Db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Slug == slug, cancellationToken);

    public Task<bool> SlugExistsAsync(string slug, CancellationToken cancellationToken = default) =>
        operation.Db.Projects.AnyAsync(p => p.Slug == slug, cancellationToken);

    public async Task AddAsync(Project project, CancellationToken cancellationToken = default) =>
        await operation.Db.Projects.AddAsync(project, cancellationToken);

    public void Update(Project project) => operation.Db.MarkModified(project);

    public void Remove(Project project) => operation.Db.MarkDeleted(project);
}
