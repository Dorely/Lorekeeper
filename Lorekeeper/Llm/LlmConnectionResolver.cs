using Lorekeeper.Models;

namespace Lorekeeper.Llm;

/// <summary>
/// Resolves the effective API key (or OAuth access token) and auth type for a
/// provider, following <see cref="LlmProvider.CredentialSourceId"/> for child
/// model rows. Shared by the chat, vision, and model-catalog clients.
/// </summary>
internal static class LlmConnectionResolver
{
    public static async Task<LlmConnectionAccess> ResolveAsync(
        ILlmProviderService providerService,
        LlmProvider provider,
        CancellationToken cancellationToken)
    {
        if (provider.OpenAiAccountId is not null)
        {
            var access = await providerService.GetOpenAiAccountAccessAsync(
                provider.OpenAiAccountId.Value,
                cancellationToken);
            return new LlmConnectionAccess(access?.AccessToken, AuthType.OAuth, access?.ExternalAccountId);
        }

        if (provider.CredentialSourceId is int sourceId)
        {
            var credentialSource = await providerService.GetByIdAsync(sourceId, cancellationToken)
                ?? throw new InvalidOperationException($"Credential source provider {sourceId} not found.");

            var sourceApiKey = await providerService.GetEffectiveApiKeyAsync(sourceId, cancellationToken);
            return new LlmConnectionAccess(sourceApiKey, credentialSource.AuthType, null);
        }

        if (provider.Id != 0)
        {
            return new LlmConnectionAccess(
                await providerService.GetEffectiveApiKeyAsync(provider.Id, cancellationToken),
                provider.AuthType,
                null);
        }

        return new LlmConnectionAccess(provider.ApiKey, provider.AuthType, null);
    }
}

internal sealed record LlmConnectionAccess(
    string? ApiKey,
    AuthType EffectiveAuthType,
    string? ExternalAccountId);
