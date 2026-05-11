using System.ClientModel;
using Lorekeeper.Models;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Lorekeeper.Llm;

public class ChatClientFactory(
    ILlmProviderService providerService,
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory) : IChatClientFactory
{
    public async Task<IChatClient> CreateChatClientAsync(int providerId, CancellationToken cancellationToken = default)
    {
        var provider = await providerService.GetByIdAsync(providerId, cancellationToken)
            ?? throw new InvalidOperationException($"Provider {providerId} not found.");

        var (apiKey, effectiveAuthType) = await ResolveConnectionAsync(provider, cancellationToken);
        return CreateChatClient(provider, apiKey, effectiveAuthType);
    }

    public async Task TestModelAsync(int providerId, CancellationToken cancellationToken = default)
    {
        var chatClient = await CreateChatClientAsync(providerId, cancellationToken);
        await TestChatClientAsync(chatClient, cancellationToken);
    }

    public async Task TestModelAsync(LlmProvider provider, CancellationToken cancellationToken = default)
    {
        var (apiKey, effectiveAuthType) = await ResolveConnectionAsync(provider, cancellationToken);
        var chatClient = CreateChatClient(provider, apiKey, effectiveAuthType);
        await TestChatClientAsync(chatClient, cancellationToken);
    }

    private async Task<(string? ApiKey, AuthType EffectiveAuthType)> ResolveConnectionAsync(
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

    private IChatClient CreateChatClient(LlmProvider provider, string? apiKey, AuthType effectiveAuthType)
    {
        // OAuth tokens are JWTs (3 dot-separated parts) → route to Codex Responses API.
        if (effectiveAuthType == AuthType.OAuth && apiKey is not null && IsJwt(apiKey))
        {
            var httpClient = httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(120);
            return new CodexChatClient(httpClient, apiKey, provider.ModelId, loggerFactory.CreateLogger<CodexChatClient>());
        }

        if (effectiveAuthType != AuthType.None && apiKey is null)
            throw new InvalidOperationException($"No valid API key or token for provider '{provider.Name}'.");

        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(provider.EndpointUrl),
            NetworkTimeout = TimeSpan.FromMinutes(10)
        };

        // Local OpenAI-compatible providers (e.g. Ollama) don't require auth; use a placeholder.
        var credential = new ApiKeyCredential(apiKey ?? "ollama");
        var client = new OpenAIClient(credential, options);
        return client.GetChatClient(provider.ModelId).AsIChatClient();
    }

    private static async Task TestChatClientAsync(IChatClient chatClient, CancellationToken cancellationToken)
    {
        var options = new ChatOptions { MaxOutputTokens = 1 };
        await chatClient.GetResponseAsync("hi", options, cancellationToken);
    }

    private static bool IsJwt(string token) =>
        !token.StartsWith("sk-") && token.Split('.').Length == 3;
}
