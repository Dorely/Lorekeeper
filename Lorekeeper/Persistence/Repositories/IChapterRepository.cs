using Lorekeeper.Models;

namespace Lorekeeper.Persistence.Repositories;

public interface IChapterRepository
{
    Task<List<Chapter>> ListByProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<List<Chapter>> ListStaleAsync(CancellationToken cancellationToken = default);
    Task<Chapter?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<int> GetMaxOrderAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<int> CountByProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task AddAsync(Chapter chapter, CancellationToken cancellationToken = default);
    void Update(Chapter chapter);
    void Remove(Chapter chapter);
    Task ReorderAsync(Guid projectId, IReadOnlyList<Guid> orderedIds, CancellationToken cancellationToken = default);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
