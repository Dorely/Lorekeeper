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

        var apiKey = await providerService.GetEffectiveApiKeyAsync(providerId, cancellationToken);

        var effectiveAuthType = provider.AuthType;
        if (provider.CredentialSourceId is int sourceId)
        {
            var credSource = await providerService.GetByIdAsync(sourceId, cancellationToken);
            if (credSource is not null)
                effectiveAuthType = credSource.AuthType;
        }

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

    public async Task TestModelAsync(int providerId, CancellationToken cancellationToken = default)
    {
        var chatClient = await CreateChatClientAsync(providerId, cancellationToken);
        var options = new ChatOptions { MaxOutputTokens = 1 };
        await chatClient.GetResponseAsync("hi", options, cancellationToken);
    }

    private static bool IsJwt(string token) =>
        !token.StartsWith("sk-") && token.Split('.').Length == 3;
}
