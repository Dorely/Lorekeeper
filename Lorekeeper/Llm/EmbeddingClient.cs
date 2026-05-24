using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Lorekeeper.Models;

namespace Lorekeeper.Llm;

public interface IEmbeddingClient
{
    Task<IList<float[]>> GenerateAsync(
        LlmProvider provider,
        EmbeddingApiKind apiKind,
        string modelId,
        IList<string> texts,
        CancellationToken cancellationToken = default);
}

public sealed class EmbeddingClient(
    IHttpClientFactory httpClientFactory,
    ILlmProviderService providerService,
    ILogger<EmbeddingClient> logger) : IEmbeddingClient
{
    public async Task<IList<float[]>> GenerateAsync(
        LlmProvider provider,
        EmbeddingApiKind apiKind,
        string modelId,
        IList<string> texts,
        CancellationToken cancellationToken = default)
    {
        if (texts.Count == 0) return [];
        if (string.IsNullOrWhiteSpace(modelId))
            throw new InvalidOperationException("Embedding model id is required.");

        return apiKind switch
        {
            EmbeddingApiKind.OllamaNative => await GenerateOllamaAsync(provider, modelId.Trim(), texts, cancellationToken),
            EmbeddingApiKind.OpenAICompatible => await GenerateOpenAICompatibleAsync(provider, modelId.Trim(), texts, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported embedding API kind '{apiKind}'."),
        };
    }

    private async Task<IList<float[]>> GenerateOllamaAsync(
        LlmProvider provider,
        string modelId,
        IList<string> texts,
        CancellationToken cancellationToken)
    {
        var client = await CreateClientAsync(provider, timeout: TimeSpan.FromMinutes(5), includeBearerToken: provider.AuthType != AuthType.None, cancellationToken);
        var requestUrl = $"{OllamaBaseUrl(provider.EndpointUrl)}/api/embed";
        var request = new OllamaEmbedRequest
        {
            Model = modelId,
            Input = texts.ToList(),
        };

        logger.LogDebug("Generating {Count} embedding(s) through Ollama ({Model})", texts.Count, modelId);
        var response = await client.PostAsJsonAsync(requestUrl, request, cancellationToken);
        await EnsureSuccessAsync(response, "Ollama embed", cancellationToken);

        var result = await response.Content.ReadFromJsonAsync<OllamaEmbedResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Ollama returned null response.");
        if (result.Embeddings is null || result.Embeddings.Count != texts.Count)
            throw new InvalidOperationException($"Expected {texts.Count} embedding(s), got {result.Embeddings?.Count ?? 0}.");

        return result.Embeddings;
    }

    private async Task<IList<float[]>> GenerateOpenAICompatibleAsync(
        LlmProvider provider,
        string modelId,
        IList<string> texts,
        CancellationToken cancellationToken)
    {
        var client = await CreateClientAsync(provider, timeout: TimeSpan.FromMinutes(5), includeBearerToken: provider.AuthType != AuthType.None, cancellationToken);
        var request = new OpenAIEmbeddingRequest
        {
            Model = modelId,
            Input = texts.ToList(),
        };

        logger.LogDebug("Generating {Count} embedding(s) through OpenAI-compatible API ({Model})", texts.Count, modelId);
        var response = await client.PostAsJsonAsync(EmbeddingEndpointUrl(provider.EndpointUrl), request, cancellationToken);
        await EnsureSuccessAsync(response, "OpenAI-compatible embed", cancellationToken);

        var result = await response.Content.ReadFromJsonAsync<OpenAIEmbeddingResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Embedding endpoint returned null response.");
        if (result.Data is null || result.Data.Count != texts.Count)
            throw new InvalidOperationException($"Expected {texts.Count} embedding(s), got {result.Data?.Count ?? 0}.");

        return result.Data
            .OrderBy(item => item.Index)
            .Select(item => item.Embedding)
            .ToList();
    }

    private async Task<HttpClient> CreateClientAsync(
        LlmProvider provider,
        TimeSpan timeout,
        bool includeBearerToken,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        client.Timeout = timeout;

        if (includeBearerToken)
        {
            var apiKey = await providerService.GetEffectiveApiKeyAsync(provider.Id, cancellationToken);
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException($"No valid API key or token for provider '{provider.Name}'.");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        return client;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException($"{operation} failed ({response.StatusCode}): {errorBody}", null, response.StatusCode);
    }

    private static string OllamaBaseUrl(string endpointUrl)
    {
        var trimmed = endpointUrl.Trim().TrimEnd('/');
        return trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^3]
            : trimmed;
    }

    private static string EmbeddingEndpointUrl(string endpointUrl)
    {
        var trimmed = endpointUrl.Trim().TrimEnd('/');
        return trimmed.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"{trimmed}/embeddings";
    }

    private sealed class OllamaEmbedRequest
    {
        [JsonPropertyName("model")]
        public required string Model { get; set; }

        [JsonPropertyName("input")]
        public required List<string> Input { get; set; }
    }

    private sealed class OllamaEmbedResponse
    {
        [JsonPropertyName("embeddings")]
        public List<float[]>? Embeddings { get; set; }
    }

    private sealed class OpenAIEmbeddingRequest
    {
        [JsonPropertyName("model")]
        public required string Model { get; set; }

        [JsonPropertyName("input")]
        public required List<string> Input { get; set; }
    }

    private sealed class OpenAIEmbeddingResponse
    {
        [JsonPropertyName("data")]
        public List<OpenAIEmbeddingData>? Data { get; set; }
    }

    private sealed class OpenAIEmbeddingData
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("embedding")]
        public required float[] Embedding { get; set; }
    }
}
