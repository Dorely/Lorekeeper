using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public sealed class SearchProviderRepository(AppDbContext db) : ISearchProviderRepository
{
    public Task<List<SearchProvider>> GetAllAsync(CancellationToken cancellationToken = default) =>
        db.SearchProviders.OrderBy(provider => provider.Name).ToListAsync(cancellationToken);

    public Task<SearchProvider?> GetByIdAsync(int id, CancellationToken cancellationToken = default) =>
        db.SearchProviders.FirstOrDefaultAsync(provider => provider.Id == id, cancellationToken);

    public Task<SearchProvider?> GetByNameAsync(string name, CancellationToken cancellationToken = default) =>
        db.SearchProviders.FirstOrDefaultAsync(provider => provider.Name == name, cancellationToken);

    public Task<SearchProvider?> GetActiveAsync(CancellationToken cancellationToken = default) =>
        db.SearchProviders.FirstOrDefaultAsync(provider => provider.IsActive, cancellationToken);

    public async Task AddAsync(SearchProvider provider, CancellationToken cancellationToken = default) =>
        await db.SearchProviders.AddAsync(provider, cancellationToken);

    public void Update(SearchProvider provider) => db.SearchProviders.Update(provider);

    public void Remove(SearchProvider provider) => db.SearchProviders.Remove(provider);

    public async Task SetActiveAsync(int id, CancellationToken cancellationToken = default)
    {
        var all = await db.SearchProviders.ToListAsync(cancellationToken);
        foreach (var provider in all)
        {
            provider.IsActive = provider.Id == id;
            provider.UpdatedAt = DateTime.UtcNow;
        }
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        db.SaveChangesAsync(cancellationToken);
}