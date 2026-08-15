using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public class OAuthTokenRepository(AppDatabaseReadOperation operation) : IOAuthTokenRepository
{
    public Task<OAuthToken?> GetLatestForProviderAsync(int providerId, CancellationToken cancellationToken = default) =>
        operation.Db.OAuthTokens
            .AsNoTracking()
            .Where(t => t.ProviderId == providerId)
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<OAuthToken?> GetLatestValidForProviderAsync(int providerId, CancellationToken cancellationToken = default) =>
        operation.Db.OAuthTokens
            .AsNoTracking()
            .Where(t => t.ProviderId == providerId && t.ExpiresAt > DateTime.UtcNow)
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task ReplaceForProviderAsync(int providerId, OAuthToken newToken, CancellationToken cancellationToken = default)
    {
        var existing = await operation.Db.OAuthTokens
            .Where(t => t.ProviderId == providerId)
            .ToListAsync(cancellationToken);
        operation.Db.OAuthTokens.RemoveRange(existing);

        newToken.ProviderId = providerId;
        await operation.Db.OAuthTokens.AddAsync(newToken, cancellationToken);
    }

    public async Task DeleteForProviderAsync(int providerId, CancellationToken cancellationToken = default)
    {
        var existing = await operation.Db.OAuthTokens
            .Where(t => t.ProviderId == providerId)
            .ToListAsync(cancellationToken);
        operation.Db.OAuthTokens.RemoveRange(existing);
    }
}
