using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public sealed class EmbeddingConfigurationRepository(AppDatabaseReadOperation operation) : IEmbeddingConfigurationRepository
{
    public Task<EmbeddingConfiguration?> GetAsync(CancellationToken cancellationToken = default) =>
        operation.Db.EmbeddingConfigurations
            .Include(configuration => configuration.Provider)
            .FirstOrDefaultAsync(configuration => configuration.Id == EmbeddingConfiguration.SingletonId, cancellationToken);

    public async Task AddAsync(EmbeddingConfiguration configuration, CancellationToken cancellationToken = default) =>
        await operation.Db.EmbeddingConfigurations.AddAsync(configuration, cancellationToken);

    public void Update(EmbeddingConfiguration configuration) =>
        operation.Db.MarkModified(configuration);

    public void Remove(EmbeddingConfiguration configuration) =>
        operation.Db.MarkDeleted(configuration);
}

