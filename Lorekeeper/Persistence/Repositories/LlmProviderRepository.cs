using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public class LlmProviderRepository(AppDbContext db) : ILlmProviderRepository
{
    public Task<List<LlmProvider>> GetAllAsync(CancellationToken cancellationToken = default) =>
        db.LlmProviders.OrderBy(p => p.Name).ToListAsync(cancellationToken);

    public Task<LlmProvider?> GetByIdAsync(int id, CancellationToken cancellationToken = default) =>
        db.LlmProviders.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public Task<LlmProvider?> GetByNameAsync(string name, CancellationToken cancellationToken = default) =>
        db.LlmProviders.FirstOrDefaultAsync(p => p.Name == name, cancellationToken);

    public async Task<LlmProvider?> GetDefaultAsync(CancellationToken cancellationToken = default) =>
        await db.LlmProviders.FirstOrDefaultAsync(p => p.IsDefault, cancellationToken)
            ?? await db.LlmProviders.FirstOrDefaultAsync(cancellationToken);

    public async Task AddAsync(LlmProvider provider, CancellationToken cancellationToken = default)
    {
        await db.LlmProviders.AddAsync(provider, cancellationToken);
    }

    public void Update(LlmProvider provider) => db.LlmProviders.Update(provider);

    public void Remove(LlmProvider provider) => db.LlmProviders.Remove(provider);

    public async Task SetDefaultAsync(int id, CancellationToken cancellationToken = default)
    {
        var all = await db.LlmProviders.ToListAsync(cancellationToken);
        foreach (var p in all)
        {
            p.IsDefault = p.Id == id;
            p.UpdatedAt = DateTime.UtcNow;
        }
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        db.SaveChangesAsync(cancellationToken);
}
