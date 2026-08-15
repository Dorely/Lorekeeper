using Lorekeeper.Models;

namespace Lorekeeper.Persistence.Repositories;

public interface IActRepository
{
    Task<List<Act>> ListByProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<Act?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<int> GetMaxOrderAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task AddAsync(Act act, CancellationToken cancellationToken = default);
    void Update(Act act);
    void Remove(Act act);
    Task ReorderAsync(Guid projectId, IReadOnlyList<Guid> orderedIds, CancellationToken cancellationToken = default);
}
