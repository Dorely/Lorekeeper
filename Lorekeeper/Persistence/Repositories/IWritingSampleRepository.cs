using Lorekeeper.Models;

namespace Lorekeeper.Persistence.Repositories;

public interface IWritingSampleRepository
{
    Task<List<WritingSample>> ListByProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<WritingSample?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<int> CountByProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task AddAsync(WritingSample sample, CancellationToken cancellationToken = default);
    void Update(WritingSample sample);
    void Remove(WritingSample sample);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}