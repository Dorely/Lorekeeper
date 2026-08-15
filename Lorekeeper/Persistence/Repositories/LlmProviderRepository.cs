using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public class LlmProviderRepository(AppDatabaseReadOperation operation) : ILlmProviderRepository
{
    public Task<List<LlmProvider>> GetAllAsync(CancellationToken cancellationToken = default) =>
        operation.Db.LlmProviders.OrderBy(p => p.Name).ToListAsync(cancellationToken);

    public Task<LlmProvider?> GetByIdAsync(int id, CancellationToken cancellationToken = default) =>
        operation.Db.LlmProviders.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public Task<LlmProvider?> GetByNameAsync(string name, CancellationToken cancellationToken = default) =>
        operation.Db.LlmProviders.FirstOrDefaultAsync(p => p.Name == name, cancellationToken);

    public async Task<LlmProvider?> GetDefaultAsync(CancellationToken cancellationToken = default) =>
        await operation.Db.LlmProviders.FirstOrDefaultAsync(p => p.IsDefault, cancellationToken)
            ?? await operation.Db.LlmProviders.FirstOrDefaultAsync(cancellationToken);

    public async Task AddAsync(LlmProvider provider, CancellationToken cancellationToken = default)
    {
        await operation.Db.LlmProviders.AddAsync(provider, cancellationToken);
    }

    public void Update(LlmProvider provider) => operation.Db.MarkModified(provider);

    public void Remove(LlmProvider provider) => operation.Db.MarkDeleted(provider);

    public async Task SetDefaultAsync(int id, CancellationToken cancellationToken = default)
    {
        var all = await operation.Db.LlmProviders.ToListAsync(cancellationToken);
        foreach (var p in all)
        {
            p.IsDefault = p.Id == id;
            p.UpdatedAt = DateTime.UtcNow;
        }
    }
}
