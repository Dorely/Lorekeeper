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
    private static readonly object SharedHttpClientLock = new();
    private static HttpClient? _sharedHttpClient;
    private static TimeSpan _sharedHttpClientTimeout;

    private static HttpClient SharedHttpClient(TimeSpan timeout)
    {
        // HttpClient.Timeout is effectively immutable after the first request, so a
        // shared instance is created once per distinct configured timeout.
        if (_sharedHttpClient is { } client && _sharedHttpClientTimeout == timeout)
            return client;

        lock (SharedHttpClientLock)
        {
            if (_sharedHttpClient is { } current && _sharedHttpClientTimeout == timeout)
                return current;

            var replacement = buildHttpClient(timeout);
            _sharedHttpClient = replacement;
            _sharedHttpClientTimeout = timeout;
            return replacement;
        }
    }

    private static HttpClient buildHttpClient(TimeSpan timeout) =>
        new(new OpenAICompatEnvelopeHandler())
        {
            Timeout = timeout
        };

    public async Task<IChatClient> CreateChatClientAsync(int providerId, CancellationToken cancellationToken = default)
    {
        var provider = await providerService.GetByIdAsync(providerId, cancellationToken)
            ?? throw new InvalidOperationException($"Provider {providerId} not found.");

        var access = await LlmConnectionResolver.ResolveAsync(providerService, provider, cancellationToken);
        return CreateChatClient(provider, access);
    }

    public async Task TestModelAsync(int providerId, CancellationToken cancellationToken = default)
    {
        var chatClient = await CreateChatClientAsync(providerId, cancellationToken);
        await TestChatClientAsync(chatClient, cancellationToken);
    }

    public async Task TestModelAsync(LlmProvider provider, CancellationToken cancellationToken = default)
    {
        var access = await LlmConnectionResolver.ResolveAsync(providerService, provider, cancellationToken);
        var chatClient = CreateChatClient(provider, access);
        await TestChatClientAsync(chatClient, cancellationToken);
    }

    private IChatClient CreateChatClient(LlmProvider provider, LlmConnectionAccess access)
    {
        if (provider.OpenAiAccountId is not null
            && access.ApiKey is not null
            && access.ExternalAccountId is not null)
        {
            var httpClient = httpClientFactory.CreateClient();
            var timeoutSeconds = Math.Clamp(agentOptions.Value.CodexRequestTimeoutSeconds, 1, 3600);
            httpClient.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            return new CodexChatClient(
                httpClient,
                access.ApiKey,
                access.ExternalAccountId,
                provider.ModelId,
                provider.EffectiveReasoningEffort,
                loggerFactory.CreateLogger<CodexChatClient>());
        }

        if (access.EffectiveAuthType != AuthType.None && access.ApiKey is null)
            throw new InvalidOperationException($"No valid API key or token for provider '{provider.Name}'.");

        var chatTimeoutSeconds = Math.Clamp(agentOptions.Value.ChatRequestTimeoutSeconds, 60, 3600);
        var requestTimeout = TimeSpan.FromSeconds(chatTimeoutSeconds);

        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(provider.EndpointUrl),
            NetworkTimeout = requestTimeout
        };

        var wireField = LlmWireCompatResolver.ResolveMaxTokensField(
            provider.EndpointUrl,
            provider.MaxTokensField);
        if (wireField == WireMaxTokensField.Legacy)
        {
            // Legacy-field endpoints rewrite max_completion_tokens → max_tokens in the
            // request body while sharing the same pooled SocketsHttpHandler underneath.
            options.Transport = new HttpClientPipelineTransport(
                new HttpClient(LegacyFieldHandler())
                {
                    Timeout = requestTimeout
                });
        }
        else
        {
            // Gateways like Cline wrap non-streaming completions in a
            // {"data": {...}, "success": true} envelope; the shared handler unwraps it
            // before the SDK parses the payload. Streaming responses pass through.
            options.Transport = new HttpClientPipelineTransport(SharedHttpClient(requestTimeout));
        }

        // Local OpenAI-compatible providers (e.g. Ollama) don't require auth; use a placeholder.
        var credential = new ApiKeyCredential(access.ApiKey ?? "ollama");
        var client = new OpenAIClient(credential, options);
        IChatClient chatClient = new OpenAIChatToolMetadataClient(
            client.GetChatClient(provider.ModelId).AsIChatClient());

        var pipeline = chatClient.AsBuilder();
        if (provider.MaxOutputTokens is > 0 and var budget)
            pipeline.ConfigureOptions(options => options.MaxOutputTokens = budget);
        return ConfigureReasoningEffort(pipeline.Build(), provider.EffectiveReasoningEffort);
    }

    private static HttpMessageHandler LegacyFieldHandler() =>
        new OpenAiLegacyMaxTokensFieldHandler(
            new OpenAICompatEnvelopeHandler(
                new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) }));

    private static async Task TestChatClientAsync(IChatClient chatClient, CancellationToken cancellationToken)
    {
        try
        {
            // Exercise the configured client, including explicit user overrides.
            // Unset generation settings remain provider-owned defaults.
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

}
