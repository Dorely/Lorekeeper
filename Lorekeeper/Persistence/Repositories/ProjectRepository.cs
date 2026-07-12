using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public class ProjectRepository(AppDbContext db) : IProjectRepository
{
    public Task<List<Project>> ListAsync(CancellationToken cancellationToken = default) =>
        db.Projects.AsNoTracking().OrderByDescending(p => p.UpdatedAt).ToListAsync(cancellationToken);

    public Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.Projects.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public Task<Project?> GetSnapshotByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public Task<Project?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
        db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Slug == slug, cancellationToken);

    public Task<bool> SlugExistsAsync(string slug, CancellationToken cancellationToken = default) =>
        db.Projects.AnyAsync(p => p.Slug == slug, cancellationToken);

    public async Task AddAsync(Project project, CancellationToken cancellationToken = default) =>
        await db.Projects.AddAsync(project, cancellationToken);

    public void Update(Project project) => db.Projects.Update(project);

    public void Remove(Project project) => db.Projects.Remove(project);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        db.SaveChangesAsync(cancellationToken);
}
