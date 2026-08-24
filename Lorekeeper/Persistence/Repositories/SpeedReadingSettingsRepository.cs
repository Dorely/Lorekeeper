using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public sealed class SpeedReadingSettingsRepository(AppDatabaseReadOperation operation) : ISpeedReadingSettingsRepository
{
    public Task<SpeedReadingSettings?> GetAsync(CancellationToken cancellationToken = default) =>
        operation.Db.SpeedReadingSettings
            .FirstOrDefaultAsync(settings => settings.Id == SpeedReadingSettings.SingletonId, cancellationToken);

    public async Task AddAsync(SpeedReadingSettings settings, CancellationToken cancellationToken = default) =>
        await operation.Db.SpeedReadingSettings.AddAsync(settings, cancellationToken);

    public void Update(SpeedReadingSettings settings) =>
        operation.Db.MarkModified(settings);
}
