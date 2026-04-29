using Lorekeeper.Models;
using Lorekeeper.Persistence.Repositories;

namespace Lorekeeper.Llm;

public class LlmProviderService(
    ILlmProviderRepository providers,
    IOAuthTokenRepository tokens) : ILlmProviderService
{
    public Task<List<LlmProvider>> GetAllAsync(CancellationToken cancellationToken = default) =>
        providers.GetAllAsync(cancellationToken);

    public Task<LlmProvider?> GetByIdAsync(int id, CancellationToken cancellationToken = default) =>
        providers.GetByIdAsync(id, cancellationToken);

    public Task<LlmProvider?> GetByNameAsync(string name, CancellationToken cancellationToken = default) =>
        providers.GetByNameAsync(name, cancellationToken);

    public Task<LlmProvider?> GetDefaultAsync(CancellationToken cancellationToken = default) =>
        providers.GetDefaultAsync(cancellationToken);

    public async Task<LlmProvider> CreateAsync(LlmProvider provider, CancellationToken cancellationToken = default)
    {
        provider.CreatedAt = DateTime.UtcNow;
        provider.UpdatedAt = DateTime.UtcNow;
        await providers.AddAsync(provider, cancellationToken);
        await providers.SaveChangesAsync(cancellationToken);
        return provider;
    }

    public async Task<LlmProvider> UpdateAsync(LlmProvider provider, CancellationToken cancellationToken = default)
    {
        provider.UpdatedAt = DateTime.UtcNow;
        providers.Update(provider);
        await providers.SaveChangesAsync(cancellationToken);
        return provider;
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var provider = await providers.GetByIdAsync(id, cancellationToken);
        if (provider is null) return;
        providers.Remove(provider);
        await providers.SaveChangesAsync(cancellationToken);
    }

    public async Task SetDefaultAsync(int id, CancellationToken cancellationToken = default)
    {
        await providers.SetDefaultAsync(id, cancellationToken);
        await providers.SaveChangesAsync(cancellationToken);
    }

    public async Task<string?> GetEffectiveApiKeyAsync(int providerId, CancellationToken cancellationToken = default)
    {
        var provider = await providers.GetByIdAsync(providerId, cancellationToken);
        if (provider is null) return null;

        var credentialProviderId = provider.EffectiveCredentialProviderId;
        var credentialProvider = credentialProviderId == providerId
            ? provider
            : await providers.GetByIdAsync(credentialProviderId, cancellationToken);

        if (credentialProvider is null) return null;

        if (credentialProvider.AuthType == AuthType.ApiKey)
            return credentialProvider.ApiKey;

        if (credentialProvider.AuthType == AuthType.OAuth)
        {
            var token = await tokens.GetLatestValidForProviderAsync(credentialProviderId, cancellationToken);
            return token?.AccessToken;
        }

        return null;
    }
}
