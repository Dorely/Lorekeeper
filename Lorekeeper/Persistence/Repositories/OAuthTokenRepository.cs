using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public class OAuthTokenRepository(AppDatabaseReadOperation operation) : IOAuthTokenRepository
{
    public Task<OAuthToken?> GetLatestForAccountAsync(int accountId, CancellationToken cancellationToken = default) =>
        operation.Db.OAuthTokens
            .AsNoTracking()
            .Where(t => t.OpenAiAccountId == accountId)
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<OAuthToken?> GetLatestValidForAccountAsync(int accountId, CancellationToken cancellationToken = default) =>
        operation.Db.OAuthTokens
            .AsNoTracking()
            .Where(t => t.OpenAiAccountId == accountId && t.ExpiresAt > DateTime.UtcNow)
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task ReplaceForAccountAsync(int accountId, OAuthToken newToken, CancellationToken cancellationToken = default)
    {
        var existing = await operation.Db.OAuthTokens
            .Where(t => t.OpenAiAccountId == accountId)
            .ToListAsync(cancellationToken);
        operation.Db.OAuthTokens.RemoveRange(existing);

        newToken.OpenAiAccountId = accountId;
        await operation.Db.OAuthTokens.AddAsync(newToken, cancellationToken);
    }

    public async Task DeleteForAccountAsync(int accountId, CancellationToken cancellationToken = default)
    {
        var existing = await operation.Db.OAuthTokens
            .Where(t => t.OpenAiAccountId == accountId)
            .ToListAsync(cancellationToken);
        operation.Db.OAuthTokens.RemoveRange(existing);
    }
}
