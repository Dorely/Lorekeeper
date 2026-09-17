using Lorekeeper.Models;

namespace Lorekeeper.Persistence.Repositories;

public interface IOAuthTokenRepository
{
    Task<OAuthToken?> GetLatestForAccountAsync(int accountId, CancellationToken cancellationToken = default);
    Task<OAuthToken?> GetLatestValidForAccountAsync(int accountId, CancellationToken cancellationToken = default);

    /// <summary>Removes any existing tokens for the account and adds the new one.</summary>
    Task ReplaceForAccountAsync(int accountId, OAuthToken newToken, CancellationToken cancellationToken = default);

    Task DeleteForAccountAsync(int accountId, CancellationToken cancellationToken = default);
}
