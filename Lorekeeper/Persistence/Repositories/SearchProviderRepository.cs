using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public sealed class SearchProviderRepository(AppDatabaseReadOperation operation) : ISearchProviderRepository
{
    public Task<List<SearchProvider>> GetAllAsync(CancellationToken cancellationToken = default) =>
        operation.Db.SearchProviders.OrderBy(provider => provider.Name).ToListAsync(cancellationToken);

    public Task<SearchProvider?> GetByIdAsync(int id, CancellationToken cancellationToken = default) =>
        operation.Db.SearchProviders.FirstOrDefaultAsync(provider => provider.Id == id, cancellationToken);

    public Task<SearchProvider?> GetByNameAsync(string name, CancellationToken cancellationToken = default) =>
        operation.Db.SearchProviders.FirstOrDefaultAsync(provider => provider.Name == name, cancellationToken);

    public Task<SearchProvider?> GetActiveAsync(CancellationToken cancellationToken = default) =>
        operation.Db.SearchProviders.FirstOrDefaultAsync(provider => provider.IsActive, cancellationToken);

    public async Task AddAsync(SearchProvider provider, CancellationToken cancellationToken = default) =>
        await operation.Db.SearchProviders.AddAsync(provider, cancellationToken);

    public void Update(SearchProvider provider) => operation.Db.MarkModified(provider);

    public void Remove(SearchProvider provider) => operation.Db.MarkDeleted(provider);

    public async Task SetActiveAsync(int id, CancellationToken cancellationToken = default)
    {
        var all = await operation.Db.SearchProviders.ToListAsync(cancellationToken);
        foreach (var provider in all)
        {
            provider.IsActive = provider.Id == id;
            provider.UpdatedAt = DateTime.UtcNow;
        }
    }
}
