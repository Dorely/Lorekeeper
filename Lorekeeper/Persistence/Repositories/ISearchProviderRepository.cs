using Lorekeeper.Models;

namespace Lorekeeper.Persistence.Repositories;

public interface ISearchProviderRepository
{
    Task<List<SearchProvider>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<SearchProvider?> GetByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<SearchProvider?> GetByNameAsync(string name, CancellationToken cancellationToken = default);
    Task<SearchProvider?> GetActiveAsync(CancellationToken cancellationToken = default);
    Task AddAsync(SearchProvider provider, CancellationToken cancellationToken = default);
    void Update(SearchProvider provider);
    void Remove(SearchProvider provider);
    Task SetActiveAsync(int id, CancellationToken cancellationToken = default);
}
