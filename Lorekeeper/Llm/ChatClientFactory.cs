using System.ClientModel;
using System.ClientModel.Primitives;
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
    // One shared connection pool for all OpenAI-compatible clients; the handler unwraps
    // gateway envelopes (see OpenAICompatEnvelopeHandler) and adds no per-provider state.
    private static readonly HttpClient EnvelopeHttpClient = new(new OpenAICompatEnvelopeHandler())
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    public async Task<IChatClient> CreateChatClientAsync(int providerId, CancellationToken cancellationToken = default)
    {
        var provider = await providerService.GetByIdAsync(providerId, cancellationToken)
            ?? throw new InvalidOperationException($"Provider {providerId} not found.");

        var (apiKey, effectiveAuthType) = await LlmConnectionResolver.ResolveAsync(providerService, provider, cancellationToken);
        return CreateChatClient(provider, apiKey, effectiveAuthType);
    }

    public async Task TestModelAsync(int providerId, CancellationToken cancellationToken = default)
    {
        var chatClient = await CreateChatClientAsync(providerId, cancellationToken);
        await TestChatClientAsync(chatClient, cancellationToken);
    }

    public async Task TestModelAsync(LlmProvider provider, CancellationToken cancellationToken = default)
    {
        var (apiKey, effectiveAuthType) = await LlmConnectionResolver.ResolveAsync(providerService, provider, cancellationToken);
        var chatClient = CreateChatClient(provider, apiKey, effectiveAuthType);
        await TestChatClientAsync(chatClient, cancellationToken);
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
        // Gateways like Cline wrap non-streaming completions in a {"data": {...}, "success": true}
        // envelope; the shared handler unwraps it before the SDK parses the payload. Standard
        // providers are unaffected, and streaming responses pass through untouched.
        options.Transport = new HttpClientPipelineTransport(EnvelopeHttpClient);

        // Local OpenAI-compatible providers (e.g. Ollama) don't require auth; use a placeholder.
        var credential = new ApiKeyCredential(apiKey ?? "ollama");
        var client = new OpenAIClient(credential, options);
        IChatClient chatClient = new OpenAIChatToolMetadataClient(
            client.GetChatClient(provider.ModelId).AsIChatClient());
        return ConfigureReasoningEffort(chatClient, provider.ReasoningEffort);
    }

    private static async Task TestChatClientAsync(IChatClient chatClient, CancellationToken cancellationToken)
    {
        try
        {
            // No ChatOptions: several OpenAI-compatible providers reject trivially
            // small token budgets or extra parameters, so the probe sends plain
            // input and lets provider defaults apply.
            await chatClient.GetResponseAsync("Reply with exactly: ok", cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(NormalizeChatTestError(ex), ex);
        }
    }

    private static string NormalizeChatTestError(Exception ex) =>
        ex is ClientResultException requestFailure && requestFailure.Status > 0
            ? $"Chat test failed (HTTP {requestFailure.Status}): {LlmErrorNormalizer.Truncate(requestFailure.Message)}"
            : $"Chat test failed: {LlmErrorNormalizer.Truncate(ex.Message)}";

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
