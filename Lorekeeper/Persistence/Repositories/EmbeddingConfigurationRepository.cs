using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public sealed class EmbeddingConfigurationRepository(AppDbContext db) : IEmbeddingConfigurationRepository
{
    public Task<EmbeddingConfiguration?> GetAsync(CancellationToken cancellationToken = default) =>
        db.EmbeddingConfigurations
            .Include(configuration => configuration.Provider)
            .FirstOrDefaultAsync(configuration => configuration.Id == EmbeddingConfiguration.SingletonId, cancellationToken);

    public async Task AddAsync(EmbeddingConfiguration configuration, CancellationToken cancellationToken = default) =>
        await db.EmbeddingConfigurations.AddAsync(configuration, cancellationToken);

    public void Update(EmbeddingConfiguration configuration) =>
        db.EmbeddingConfigurations.Update(configuration);

    public void Remove(EmbeddingConfiguration configuration) =>
        db.EmbeddingConfigurations.Remove(configuration);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        db.SaveChangesAsync(cancellationToken);
}

