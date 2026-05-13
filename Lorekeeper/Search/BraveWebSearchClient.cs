using System.Text.Json;
using Lorekeeper.Models;

namespace Lorekeeper.Search;

public sealed class BraveWebSearchClient(IHttpClientFactory httpClientFactory) : IWebSearchClient
{
    public SearchProviderKind ProviderKind => SearchProviderKind.Brave;

    public async Task<WebSearchResponse> SearchAsync(SearchProvider provider, WebSearchRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            throw new ArgumentException("Search query is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(provider.ApiKey))
            throw new InvalidOperationException("Brave Search API key is required.");

        var count = Math.Clamp(request.Count, 1, 20);
        var url = $"https://api.search.brave.com/res/v1/web/search?q={Uri.EscapeDataString(request.Query)}&count={count}";
        var client = httpClientFactory.CreateClient(nameof(BraveWebSearchClient));
        using var message = new HttpRequestMessage(HttpMethod.Get, url);
        message.Headers.TryAddWithoutValidation("Accept", "application/json");
        message.Headers.TryAddWithoutValidation("X-Subscription-Token", provider.ApiKey);

        using var response = await client.SendAsync(message, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Brave Search failed with {(int)response.StatusCode}: {TrimForError(json)}");

        using var doc = JsonDocument.Parse(json);
        var results = new List<WebSearchResult>();
        if (doc.RootElement.TryGetProperty("web", out var web)
            && web.TryGetProperty("results", out var webResults)
            && webResults.ValueKind == JsonValueKind.Array)
        {
            var rank = 1;
            foreach (var result in webResults.EnumerateArray().Take(count))
            {
                var urlValue = ReadString(result, "url");
                if (string.IsNullOrWhiteSpace(urlValue)) continue;

                results.Add(new WebSearchResult(
                    rank,
                    ReadString(result, "title") ?? urlValue,
                    urlValue,
                    ReadString(result, "profile") ?? urlValue,
                    ReadString(result, "description") ?? string.Empty,
                    ReadString(result, "age"),
                    result.GetRawText()));
                rank++;
            }
        }

        return new WebSearchResponse(provider.ProviderKind, provider.DisplayName ?? provider.Name, request.Query, results, json);
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string TrimForError(string value) =>
        value.Length <= 600 ? value : value[..600];
}