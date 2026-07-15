using System.ClientModel;
using Microsoft.Extensions.Options;
using Lorekeeper.Models;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

namespace Lorekeeper.Llm;

public class ChatClientFactory(
    ILlmProviderService providerService,
    IHttpClientFactory httpClientFactory,
    IOptions<AgentOptions> agentOptions,
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
            var timeoutSeconds = Math.Clamp(agentOptions.Value.CodexRequestTimeoutSeconds, 1, 3600);
            httpClient.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            return new CodexChatClient(
                httpClient,
                apiKey,
                provider.ModelId,
                provider.ReasoningEffort,
                loggerFactory.CreateLogger<CodexChatClient>());
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
        var chatClient = client.GetChatClient(provider.ModelId).AsIChatClient();
        return ConfigureReasoningEffort(chatClient, provider.ReasoningEffort);
    }

    private static async Task TestChatClientAsync(IChatClient chatClient, CancellationToken cancellationToken)
    {
        var options = new ChatOptions { MaxOutputTokens = 32 };
        await chatClient.GetResponseAsync("hi", options, cancellationToken);
    }

    private static IChatClient ConfigureReasoningEffort(
        IChatClient chatClient,
        LlmReasoningEffort? reasoningEffort)
    {
        if (reasoningEffort is null)
            return chatClient;

        var wireValue = reasoningEffort.Value.ToWireValue();
        return chatClient
            .AsBuilder()
            .ConfigureOptions(options => ConfigureOpenAiReasoningEffort(options, wireValue))
            .Build();
    }

    private static void ConfigureOpenAiReasoningEffort(ChatOptions options, string wireValue)
    {
        var existingFactory = options.RawRepresentationFactory;
        options.RawRepresentationFactory = client =>
        {
            var existing = existingFactory?.Invoke(client);
            if (existing is not null and not ChatCompletionOptions)
                return existing;

            var openAiOptions = existing as ChatCompletionOptions ?? new ChatCompletionOptions();
#pragma warning disable OPENAI001 // ReasoningEffortLevel is experimental in the pinned OpenAI SDK.
            openAiOptions.ReasoningEffortLevel = new ChatReasoningEffortLevel(wireValue);
#pragma warning restore OPENAI001
            return openAiOptions;
        };
    }

    private static bool IsJwt(string token) =>
        !token.StartsWith("sk-") && token.Split('.').Length == 3;
}
