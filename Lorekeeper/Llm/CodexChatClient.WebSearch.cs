using System.Text.Json;
using System.Text.RegularExpressions;
using Lorekeeper.Diagnostics;
using Lorekeeper.Search;

namespace Lorekeeper.Llm;

/// <summary>
/// The account model's hosted Responses <c>web_search</c> tool, run as a separate request.
/// Results are only the pages the hosted search reported as sources or that the reply cited,
/// so every returned URL comes from the provider's search rather than from model text.
/// </summary>
public sealed partial class CodexChatClient
{
    private const int MaxSnippetChars = 300;

    public string DisplayName => $"OpenAI web search ({_model})";

    public async Task<WebSearchResponse> SearchAsync(WebSearchRequest request, CancellationToken cancellationToken = default)
    {
        var count = Math.Clamp(request.Count, 1, 10);
        var body = new Dictionary<string, object>
        {
            ["model"] = _model,
            ["stream"] = true,
            ["store"] = false,
            ["instructions"] = $"""
                You are the web search backend for a research tool. Use web search to find up to {count} distinct public web pages that are most relevant to the query.
                Prefer specific, authoritative article or source pages over home, search, or index pages.
                Reply with one line per page: a single factual sentence describing what that page covers, citing exactly that page.
                Do not answer the query yourself and do not mention pages you did not find with web search.
                """,
            ["input"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["role"] = "user",
                    ["content"] = new[] { new Dictionary<string, object> { ["type"] = "input_text", ["text"] = $"Query: {request.Query}" } },
                },
            },
            ["tools"] = new[] { new Dictionary<string, object> { ["type"] = "web_search" } },
            ["tool_choice"] = "auto",
            ["include"] = new[] { "web_search_call.action.sources" },
            // A search only selects pages, so it uses the lowest effort every catalog model permits.
            ["reasoning"] = new Dictionary<string, object> { ["effort"] = "low" },
        };

        using var httpRequest = CreateResponsesRequest(JsonSerializer.Serialize(body));
        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = LogRedaction.RedactJson(await response.Content.ReadAsStringAsync(cancellationToken));
            _logger.LogError("Codex web search error {StatusCode}: {Body}", (int)response.StatusCode, errorBody);
            throw new HttpRequestException($"Codex web search returned {(int)response.StatusCode}: {errorBody}");
        }

        var items = await ReadOutputItemsAsync(response, cancellationToken);
        var queries = new List<string>();
        var searchCalls = 0;
        var cited = new List<(string Url, string? Title, string Snippet)>();
        var sources = new List<(string Url, string? Title)>();
        foreach (var item in items)
        {
            switch (item.TryGetProperty("type", out var type) ? type.GetString() : null)
            {
                case "web_search_call":
                    searchCalls++;
                    if (!item.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.Object)
                        break;
                    if (action.TryGetProperty("query", out var query) && query.ValueKind == JsonValueKind.String)
                        queries.Add(query.GetString()!);
                    if (action.TryGetProperty("sources", out var actionSources) && actionSources.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var source in actionSources.EnumerateArray())
                        {
                            if (OptionalString(source, "url") is { } url)
                                sources.Add((url, OptionalString(source, "title")));
                        }
                    }
                    break;
                case "message" when item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array:
                    foreach (var part in content.EnumerateArray())
                    {
                        var text = OptionalString(part, "text") ?? string.Empty;
                        if (!part.TryGetProperty("annotations", out var annotations) || annotations.ValueKind != JsonValueKind.Array)
                            continue;
                        foreach (var annotation in annotations.EnumerateArray())
                        {
                            if (OptionalString(annotation, "type") != "url_citation" || OptionalString(annotation, "url") is not { } url)
                                continue;
                            var start = annotation.TryGetProperty("start_index", out var index) && index.TryGetInt32(out var value) ? value : 0;
                            cited.Add((url, OptionalString(annotation, "title"), CitedLine(text, start)));
                        }
                    }
                    break;
            }
        }

        if (searchCalls == 0)
            throw new InvalidOperationException("The model replied without running its built-in web search, so no results were recorded. Try the search again.");

        var results = new List<WebSearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (url, title, snippet) in cited.Concat(sources.Select(source => (source.Url, source.Title, Snippet: string.Empty))))
        {
            if (results.Count == count) break;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                continue;
            var normalized = WithoutProviderTracking(uri);
            if (!seen.Add(normalized)) continue;
            var displayUrl = uri.Host + uri.AbsolutePath.TrimEnd('/');
            results.Add(new WebSearchResult(
                results.Count + 1,
                string.IsNullOrWhiteSpace(title) ? displayUrl : title.Trim(),
                normalized,
                displayUrl,
                snippet,
                RawJson: JsonSerializer.Serialize(new { source = "openai_web_search", model = _model, queries })));
        }

        _logger.LogDebug(
            "Codex web search completed. SearchCalls={SearchCalls}, Citations={Citations}, Sources={Sources}, Results={Results}",
            searchCalls, cited.Count, sources.Count, results.Count);
        return new WebSearchResponse(DisplayName, request.Query, results, JsonSerializer.Serialize(new { queries, searchCalls }));
    }

    /// <summary>Collects completed output items, preferring the terminal response's output when it is present.</summary>
    private static async Task<IReadOnlyList<JsonElement>> ReadOutputItemsAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var streamed = new List<JsonElement>();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var data = line["data: ".Length..];
            if (data == "[DONE]") break;
            JsonElement evt;
            try { evt = JsonSerializer.Deserialize<JsonElement>(data); }
            catch (JsonException) { continue; }

            switch (OptionalString(evt, "type"))
            {
                case "response.output_item.done" when evt.TryGetProperty("item", out var item):
                    streamed.Add(item.Clone());
                    break;
                case "response.completed" or "response.done":
                    return evt.TryGetProperty("response", out var terminal)
                        && terminal.TryGetProperty("output", out var output)
                        && output.ValueKind == JsonValueKind.Array && output.GetArrayLength() > 0
                            ? output.EnumerateArray().Select(entry => entry.Clone()).ToList()
                            : streamed;
                case "response.incomplete":
                    throw new InvalidOperationException("Codex web search ended incomplete.");
                case "response.failed" or "error":
                    throw new InvalidOperationException(ExtractCodexErrorMessage(evt, "Codex web search failed."));
            }
        }

        throw new HttpRequestException("Codex web search stream ended before completion.");
    }

    private static string? OptionalString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    /// <summary>The cited URL without its fragment or the <c>utm_source=openai</c> tag the provider appends.</summary>
    private static string WithoutProviderTracking(Uri uri)
    {
        var query = string.Join('&', uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(pair => !pair.Equals("utm_source=openai", StringComparison.OrdinalIgnoreCase)));
        return new UriBuilder(uri) { Query = query, Fragment = string.Empty }.Uri.AbsoluteUri;
    }

    /// <summary>The reply line a citation belongs to, without citation markers, Markdown link syntax or list markers.</summary>
    private static string CitedLine(string text, int index)
    {
        if (text.Length == 0) return string.Empty;
        index = Math.Clamp(index, 0, text.Length - 1);
        var start = text.LastIndexOf('\n', index) + 1;
        var end = text.IndexOf('\n', index);
        var line = text[start..(end < 0 ? text.Length : end)];
        line = MarkdownLink().Replace(CitationMarker().Replace(line, string.Empty), "$1");
        line = line.TrimStart('-', '*', ' ', '\t').Trim();
        return line.Length <= MaxSnippetChars ? line : line[..MaxSnippetChars].TrimEnd() + "…";
    }

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"\s*\(\[[^\]]*\]\([^)]*\)\)")]
    private static partial Regex CitationMarker();
}
