using Lorekeeper.Models;

namespace Lorekeeper.Search;

public sealed record WebSearchRequest(string Query, int Count = 10);

public sealed record WebSearchResponse(
    SearchProviderKind ProviderKind,
    string ProviderName,
    string Query,
    IReadOnlyList<WebSearchResult> Results,
    string RawJson);

public sealed record WebSearchResult(
    int Rank,
    string Title,
    string Url,
    string DisplayUrl,
    string Snippet,
    string? PublishedDate = null,
    string RawJson = "{}");

public sealed record SearchProviderTestResult(bool Success, string Message);