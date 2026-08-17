using Lorekeeper.Models;

namespace Lorekeeper.Llm;

/// <summary>
/// Resolves the effective API key (or OAuth access token) and auth type for a
/// provider, following <see cref="LlmProvider.CredentialSourceId"/> for child
/// model rows. Shared by the chat, vision, and model-catalog clients.
/// </summary>
internal static class LlmConnectionResolver
{
    public static async Task<(string? ApiKey, AuthType EffectiveAuthType)> ResolveAsync(
        ILlmProviderService providerService,
        LlmProvider provider,
        CancellationToken cancellationToken)
    {
        if (provider.CredentialSourceId is int sourceId)
        {
            var credentialSource = await providerService.GetByIdAsync(sourceId, cancellationToken)
                ?? throw new InvalidOperationException($"Credential source provider {sourceId} not found.");

            var sourceApiKey = await providerService.GetEffectiveApiKeyAsync(sourceId, cancellationToken);
            return (sourceApiKey, credentialSource.AuthType);
        }

        if (provider.Id != 0)
            return (await providerService.GetEffectiveApiKeyAsync(provider.Id, cancellationToken), provider.AuthType);

        return (provider.ApiKey, provider.AuthType);
    }
}
