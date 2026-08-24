using Lorekeeper.Models;

namespace Lorekeeper.Persistence.Repositories;

public interface ISpeedReadingSettingsRepository
{
    Task<SpeedReadingSettings?> GetAsync(CancellationToken cancellationToken = default);
    Task AddAsync(SpeedReadingSettings settings, CancellationToken cancellationToken = default);
    void Update(SpeedReadingSettings settings);
}
