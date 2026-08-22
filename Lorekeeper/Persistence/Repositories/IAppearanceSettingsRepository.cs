using Lorekeeper.Models;

namespace Lorekeeper.Persistence.Repositories;

public interface IAppearanceSettingsRepository
{
    Task<AppearanceSettings?> GetAsync(CancellationToken cancellationToken = default);
    Task AddAsync(AppearanceSettings settings, CancellationToken cancellationToken = default);
    void Update(AppearanceSettings settings);
}
