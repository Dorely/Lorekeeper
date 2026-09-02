using System.Net.Http.Headers;
using System.Text.Json;
using Lorekeeper.Diagnostics;
using Lorekeeper.Models;

namespace Lorekeeper.Llm;

public sealed class ModelCatalogService(
    ILlmProviderService providerService,
    IHttpClientFactory httpClientFactory,
    ILogger<ModelCatalogService> logger) : IModelCatalogService
{
    public async Task<IReadOnlyList<LlmDiscoveredModel>> ListChatModelsAsync(LlmProvider provider, CancellationToken cancellationToken = default)
    {
        var endpoint = CodexProvider.IsCodex(provider)
            ? CodexProvider.PlatformModelsEndpoint
            : BuildModelsEndpointUrl(provider.EndpointUrl);
        return await QueryModelsAsync(provider, endpoint, ParseOpenAiChatModels, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListEmbeddingModelsAsync(LlmProvider provider, EmbeddingApiKind apiKind, CancellationToken cancellationToken = default)
    {
        if (apiKind == EmbeddingApiKind.OllamaNative)
        {
            return await QueryModelsAsync(
                provider,
                $"{OllamaRootUrl(provider.EndpointUrl)}/api/tags",
                ParseOllamaModelIds,
                cancellationToken);
        }

        var endpoint = CodexProvider.IsCodex(provider)
            ? CodexProvider.PlatformModelsEndpoint
            : BuildModelsEndpointUrl(provider.EndpointUrl);
        return await QueryModelsAsync(provider, endpoint, ParseOpenAiModelIds, cancellationToken);
    }

    private async Task<IReadOnlyList<T>> QueryModelsAsync<T>(
        LlmProvider provider,
        string endpoint,
        Func<JsonElement, IReadOnlyList<T>> parse,
        CancellationToken cancellationToken)
    {
        var (apiKey, effectiveAuthType) = await LlmConnectionResolver.ResolveAsync(providerService, provider, cancellationToken);
        // Don't block discovery when AuthType is ApiKey but no key has been entered yet.
        // CommandCode's GET /v1/models succeeds without auth and many gateways return an
        // empty or public list anonymously; authenticated gateways will return 401/403
        // which is surfaced below as an HttpRequestException. This lets Settings >
        // Providers "List models" work prior to API-key configuration.

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        if (effectiveAuthType != AuthType.None && !string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            if (CodexProvider.IsCodex(provider))
            {
                request.Headers.TryAddWithoutValidation("chatgpt-account-id", CodexProvider.ExtractAccountId(apiKey));
                request.Headers.TryAddWithoutValidation("User-Agent", "Lorekeeper");
            }
        }

        var httpClient = httpClientFactory.CreateClient();
        httpClient.Timeout = TimeSpan.FromSeconds(30);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Model catalog request to {Endpoint} for provider {Provider} failed: {Status} {Body}",
                endpoint, provider.Name, (int)response.StatusCode, LogRedaction.RedactJson(responseBody));
            throw new HttpRequestException(
                LlmErrorNormalizer.SummarizeHttpError("Model list request", (int)response.StatusCode, LogRedaction.RedactJson(responseBody)));
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            return parse(document.RootElement);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "The provider returned a model list that could not be parsed; enter the model ID manually.", ex);
        }
    }

    private static IReadOnlyList<LlmDiscoveredModel> ParseOpenAiChatModels(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        return data.EnumerateArray()
            .Where(item => item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            .Select(item => new LlmDiscoveredModel(
                Id: item.GetProperty("id").GetString() ?? string.Empty,
                ContextLengthTokens: ReadContextLengthTokens(item)))
            .Where(model => !string.IsNullOrWhiteSpace(model.Id))
            .DistinctBy(model => model.Id, StringComparer.Ordinal)
            .OrderBy(model => model.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static long? ReadContextLengthTokens(JsonElement item)
    {
        foreach (var propertyName in ContextLengthPropertyNames)
        {
            if (item.TryGetProperty(propertyName, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out var tokens)
                && tokens > 0)
            {
                return tokens;
            }
        }

        return null;
    }

    private static readonly string[] ContextLengthPropertyNames = ["context_length", "context_window"];

    private static IReadOnlyList<string> ParseOpenAiModelIds(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        return data.EnumerateArray()
            .Where(item => item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            .Select(item => item.GetProperty("id").GetString())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<string> ParseOllamaModelIds(JsonElement root)
    {
        if (!root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            return [];

        return models.EnumerateArray()
            .Where(item => item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            .Select(item => item.GetProperty("name").GetString())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    private static string BuildModelsEndpointUrl(string endpointUrl)
    {
        var trimmed = endpointUrl.Trim().TrimEnd('/');
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^"/chat/completions".Length];
        if (trimmed.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
            return trimmed;

        return trimmed + "/models";
    }

    private static string OllamaRootUrl(string endpointUrl)
    {
        var trimmed = endpointUrl.Trim().TrimEnd('/');
        return trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^3]
            : trimmed;
    }
}
