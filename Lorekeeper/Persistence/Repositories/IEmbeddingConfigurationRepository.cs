using Lorekeeper.Models;

namespace Lorekeeper.Persistence.Repositories;

public interface IEmbeddingConfigurationRepository
{
    Task<EmbeddingConfiguration?> GetAsync(CancellationToken cancellationToken = default);
    Task AddAsync(EmbeddingConfiguration configuration, CancellationToken cancellationToken = default);
    void Update(EmbeddingConfiguration configuration);
    void Remove(EmbeddingConfiguration configuration);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}

