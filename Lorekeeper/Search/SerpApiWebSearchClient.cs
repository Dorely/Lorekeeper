using System.Net.Http.Json;
using System.Text.Json;
using Lorekeeper.Models;

namespace Lorekeeper.Search;

public sealed class SerpApiWebSearchClient(IHttpClientFactory httpClientFactory) : IWebSearchClient
{
    public SearchProviderKind ProviderKind => SearchProviderKind.SerpApi;

    public async Task<WebSearchResponse> SearchAsync(SearchProvider provider, WebSearchRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            throw new ArgumentException("Search query is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(provider.ApiKey))
            throw new InvalidOperationException("SerpApi API key is required.");

        var count = Math.Clamp(request.Count, 1, 20);
        var url = $"https://serpapi.com/search.json?engine=google&q={Uri.EscapeDataString(request.Query)}&num={count}&api_key={Uri.EscapeDataString(provider.ApiKey)}";
        var client = httpClientFactory.CreateClient(nameof(SerpApiWebSearchClient));
        using var response = await client.GetAsync(url, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"SerpApi search failed with {(int)response.StatusCode}: {TrimForError(json)}");

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
            throw new InvalidOperationException(error.GetString() ?? "SerpApi returned an error.");

        var results = new List<WebSearchResult>();
        if (doc.RootElement.TryGetProperty("organic_results", out var organic) && organic.ValueKind == JsonValueKind.Array)
        {
            var fallbackRank = 1;
            foreach (var result in organic.EnumerateArray().Take(count))
            {
                var link = ReadString(result, "link");
                if (string.IsNullOrWhiteSpace(link)) continue;

                results.Add(new WebSearchResult(
                    ReadInt(result, "position") ?? fallbackRank,
                    ReadString(result, "title") ?? link,
                    link,
                    ReadString(result, "displayed_link") ?? ReadString(result, "source") ?? link,
                    ReadString(result, "snippet") ?? string.Empty,
                    ReadString(result, "date"),
                    result.GetRawText()));
                fallbackRank++;
            }
        }

        return new WebSearchResponse(provider.ProviderKind, provider.DisplayName ?? provider.Name, request.Query, results, json);
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? ReadInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value)
            ? value
            : null;

    private static string TrimForError(string value) =>
        value.Length <= 600 ? value : value[..600];
}