using Lorekeeper.Models;

namespace Lorekeeper.Persistence.Repositories;

public interface IProjectRepository
{
    Task<List<Project>> ListAsync(CancellationToken cancellationToken = default);
    Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Project?> GetSnapshotByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Project?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default);
    Task<bool> SlugExistsAsync(string slug, CancellationToken cancellationToken = default);
    Task AddAsync(Project project, CancellationToken cancellationToken = default);
    void Update(Project project);
    void Remove(Project project);
}
