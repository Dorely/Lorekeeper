using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public sealed class AppearanceSettingsRepository(AppDatabaseReadOperation operation) : IAppearanceSettingsRepository
{
    public Task<AppearanceSettings?> GetAsync(CancellationToken cancellationToken = default) =>
        operation.Db.AppearanceSettings
            .FirstOrDefaultAsync(settings => settings.Id == AppearanceSettings.SingletonId, cancellationToken);

    public async Task AddAsync(AppearanceSettings settings, CancellationToken cancellationToken = default) =>
        await operation.Db.AppearanceSettings.AddAsync(settings, cancellationToken);

    public void Update(AppearanceSettings settings) =>
        operation.Db.MarkModified(settings);
}
