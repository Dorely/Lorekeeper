using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public interface IImageProviderRepository
{
    Task<List<ImageProvider>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<ImageProvider?> GetByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<ImageProvider?> GetByNameAsync(string name, CancellationToken cancellationToken = default);
    Task<ImageProviderModel?> GetModelAsync(int modelId, CancellationToken cancellationToken = default);
    Task AddAsync(ImageProvider provider, CancellationToken cancellationToken = default);
    void Update(ImageProvider provider);
    void Remove(ImageProvider provider);
    Task AddModelAsync(ImageProviderModel model, CancellationToken cancellationToken = default);
    void UpdateModel(ImageProviderModel model);
    void RemoveModel(ImageProviderModel model);

    /// <summary>Stages <paramref name="id"/> as the only default provider.</summary>
    Task SetDefaultAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Stages <paramref name="modelId"/> as the only default model of its provider.</summary>
    Task SetDefaultModelAsync(int providerId, int modelId, CancellationToken cancellationToken = default);
}

public sealed class ImageProviderRepository(AppDatabaseReadOperation operation) : IImageProviderRepository
{
    public Task<List<ImageProvider>> GetAllAsync(CancellationToken cancellationToken = default) =>
        operation.Db.ImageProviders
            .Include(provider => provider.Models.OrderBy(model => model.SortOrder).ThenBy(model => model.ModelId))
            .OrderByDescending(provider => provider.IsDefault)
            .ThenBy(provider => provider.Name)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

    public Task<ImageProvider?> GetByIdAsync(int id, CancellationToken cancellationToken = default) =>
        operation.Db.ImageProviders
            .Include(provider => provider.Models.OrderBy(model => model.SortOrder).ThenBy(model => model.ModelId))
            .FirstOrDefaultAsync(provider => provider.Id == id, cancellationToken);

    public Task<ImageProvider?> GetByNameAsync(string name, CancellationToken cancellationToken = default) =>
        operation.Db.ImageProviders.FirstOrDefaultAsync(provider => provider.Name == name, cancellationToken);

    public Task<ImageProviderModel?> GetModelAsync(int modelId, CancellationToken cancellationToken = default) =>
        operation.Db.ImageProviderModels.FirstOrDefaultAsync(model => model.Id == modelId, cancellationToken);

    public async Task AddAsync(ImageProvider provider, CancellationToken cancellationToken = default) =>
        await operation.Db.ImageProviders.AddAsync(provider, cancellationToken);

    public void Update(ImageProvider provider) => operation.Db.MarkModified(provider);

    public void Remove(ImageProvider provider) => operation.Db.MarkDeleted(provider);

    public async Task AddModelAsync(ImageProviderModel model, CancellationToken cancellationToken = default) =>
        await operation.Db.ImageProviderModels.AddAsync(model, cancellationToken);

    public void UpdateModel(ImageProviderModel model) => operation.Db.MarkModified(model);

    public void RemoveModel(ImageProviderModel model) => operation.Db.MarkDeleted(model);

    public async Task SetDefaultAsync(int id, CancellationToken cancellationToken = default)
    {
        var all = await operation.Db.ImageProviders.ToListAsync(cancellationToken);
        foreach (var provider in all)
        {
            var isDefault = provider.Id == id;
            if (provider.IsDefault == isDefault)
                continue;
            provider.IsDefault = isDefault;
            provider.UpdatedAt = DateTime.UtcNow;
        }
    }

    public async Task SetDefaultModelAsync(int providerId, int modelId, CancellationToken cancellationToken = default)
    {
        var models = await operation.Db.ImageProviderModels
            .Where(model => model.ImageProviderId == providerId)
            .ToListAsync(cancellationToken);
        foreach (var model in models)
        {
            var isDefault = model.Id == modelId;
            if (model.IsDefault == isDefault)
                continue;
            model.IsDefault = isDefault;
            model.UpdatedAt = DateTime.UtcNow;
        }
    }
}
