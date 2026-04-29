using Lorekeeper.Models;

namespace Lorekeeper.Persistence.Repositories;

public interface ILlmProviderRepository
{
    Task<List<LlmProvider>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<LlmProvider?> GetByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<LlmProvider?> GetByNameAsync(string name, CancellationToken cancellationToken = default);
    Task<LlmProvider?> GetDefaultAsync(CancellationToken cancellationToken = default);
    Task AddAsync(LlmProvider provider, CancellationToken cancellationToken = default);
    void Update(LlmProvider provider);
    void Remove(LlmProvider provider);

    /// <summary>Atomically clears <see cref="LlmProvider.IsDefault"/> on all rows, then sets it on <paramref name="id"/>.</summary>
    Task SetDefaultAsync(int id, CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
