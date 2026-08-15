using Lorekeeper.Models;

namespace Lorekeeper.Persistence.Repositories;

public interface IOAuthTokenRepository
{
    Task<OAuthToken?> GetLatestForProviderAsync(int providerId, CancellationToken cancellationToken = default);
    Task<OAuthToken?> GetLatestValidForProviderAsync(int providerId, CancellationToken cancellationToken = default);

    /// <summary>Removes any existing tokens for the provider and adds the new one.</summary>
    Task ReplaceForProviderAsync(int providerId, OAuthToken newToken, CancellationToken cancellationToken = default);

    Task DeleteForProviderAsync(int providerId, CancellationToken cancellationToken = default);
}
