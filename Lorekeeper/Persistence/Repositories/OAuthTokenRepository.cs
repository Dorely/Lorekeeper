using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public class OAuthTokenRepository(AppDbContext db) : IOAuthTokenRepository
{
    public Task<OAuthToken?> GetLatestForProviderAsync(int providerId, CancellationToken cancellationToken = default) =>
        db.OAuthTokens
            .Where(t => t.ProviderId == providerId)
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<OAuthToken?> GetLatestValidForProviderAsync(int providerId, CancellationToken cancellationToken = default) =>
        db.OAuthTokens
            .Where(t => t.ProviderId == providerId && t.ExpiresAt > DateTime.UtcNow)
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task ReplaceForProviderAsync(int providerId, OAuthToken newToken, CancellationToken cancellationToken = default)
    {
        var existing = await db.OAuthTokens
            .Where(t => t.ProviderId == providerId)
            .ToListAsync(cancellationToken);
        db.OAuthTokens.RemoveRange(existing);

        newToken.ProviderId = providerId;
        await db.OAuthTokens.AddAsync(newToken, cancellationToken);
    }

    public async Task DeleteForProviderAsync(int providerId, CancellationToken cancellationToken = default)
    {
        var existing = await db.OAuthTokens
            .Where(t => t.ProviderId == providerId)
            .ToListAsync(cancellationToken);
        db.OAuthTokens.RemoveRange(existing);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        db.SaveChangesAsync(cancellationToken);
}
