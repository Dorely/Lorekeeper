using System.Text.Json;
using Lorekeeper.Models;
using Lorekeeper.Search;
using Microsoft.Extensions.AI;

namespace Lorekeeper.Research;

public sealed record ResearchToolContext(Guid ProjectId, Guid ConversationId);

public sealed class ResearchTools(
    ISearchProviderService searchProviders,
    IWebIngestCandidateService candidates)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public IList<AITool> Build(ResearchToolContext context) =>
    [
        AIFunctionFactory.Create(
            method: (string query, int count) => WebSearchAsync(context, query, count),
            name: "web_search",
            description: "Search the public web with the active configured search provider. Persists each result as a page candidate and returns stable page ids plus title, URL, and snippet."),

        AIFunctionFactory.Create(
            method: (Guid pageId) => ReadSearchResultAsync(pageId),
            name: "read_search_result",
            description: "Read and extract text from a previously discovered search result by page id. Returns excerpt and outgoing links that can be followed with read_webpage."),

        AIFunctionFactory.Create(
            method: (string url) => ReadWebpageAsync(context, url),
            name: "read_webpage",
            description: "Read and extract text from a specific webpage URL supplied by the user or discovered from another page's links. Returns a stable page id, excerpt, diagnostics, and outgoing links."),

        AIFunctionFactory.Create(
            method: (Guid pageId, int count, bool sameDomainOnly) => FollowPageLinksAsync(context, pageId, count, sameDomainOnly),
            name: "follow_page_links",
            description: "Read outgoing links from an already-read page, persisting each followed link as a page candidate. Use this when a promising source exposes relevant wiki/article/reference links."),

        AIFunctionFactory.Create(
            method: (Guid pageId, string rationale) => StagePageAsync(pageId, rationale),
            name: "stage_page_for_ingestion",
            description: "Stage a page for ingestion after it has been read and judged useful for the project. Include a concise rationale for why it belongs in project memory."),

        AIFunctionFactory.Create(
            method: () => ListStagedPagesAsync(context),
            name: "list_staged_pages",
            description: "List pages currently staged for ingestion in this project."),
    ];

    private async Task<string> WebSearchAsync(ResearchToolContext context, string query, int count)
    {
        if (string.IsNullOrWhiteSpace(query)) return "Error: query is required.";
        count = Math.Clamp(count, 1, 10);
        var active = await searchProviders.GetActiveAsync()
            ?? throw new InvalidOperationException("No active search provider is configured.");
        var response = await searchProviders.SearchAsync(new WebSearchRequest(query.Trim(), count));
        var resultPayloads = new List<object>();
        foreach (var result in response.Results)
        {
            var candidate = await candidates.CreateFromSearchResultAsync(
                context.ProjectId,
                context.ConversationId,
                active.Id,
                response.ProviderName,
                response.Query,
                result);
            resultPayloads.Add(CandidateSummary(candidate));
        }

        return JsonSerializer.Serialize(new
        {
            provider = response.ProviderName,
            response.Query,
            results = resultPayloads,
        }, JsonOptions);
    }

    private async Task<string> ReadSearchResultAsync(Guid pageId)
    {
        var read = await candidates.ReadCandidateAsync(pageId);
        return JsonSerializer.Serialize(ReadPayload(read), JsonOptions);
    }

    private async Task<string> ReadWebpageAsync(ResearchToolContext context, string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "Error: url is required.";
        var read = await candidates.ReadUrlAsync(context.ProjectId, context.ConversationId, url.Trim());
        return JsonSerializer.Serialize(ReadPayload(read), JsonOptions);
    }

    private async Task<string> FollowPageLinksAsync(ResearchToolContext context, Guid pageId, int count, bool sameDomainOnly)
    {
        count = Math.Clamp(count, 1, 12);
        var sourceRead = await candidates.ReadCandidateAsync(pageId);
        var sourceUrl = BestUrl(sourceRead.Candidate);
        var sourceHost = HostKey(sourceUrl);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var followed = new List<object>();

        foreach (var link in sourceRead.Links)
        {
            if (followed.Count >= count) break;
            if (!TryNormalizeHttpUrl(link.Url, out var normalizedUrl, out var linkHost)) continue;
            if (sameDomainOnly && !string.Equals(sourceHost, linkHost, StringComparison.OrdinalIgnoreCase)) continue;
            if (!seen.Add(normalizedUrl)) continue;

            var read = await candidates.ReadUrlAsync(
                context.ProjectId,
                context.ConversationId,
                normalizedUrl,
                WebIngestCandidateDiscoveryKind.PageLink,
                parentUrl: sourceUrl,
                crawlDepth: sourceRead.Candidate.CrawlDepth + 1);
            followed.Add(new
            {
                link = new { url = normalizedUrl, link.Text },
                page = CandidateSummary(read.Candidate),
                success = read.Candidate.Status != WebIngestCandidateStatus.Failed,
                diagnostics = read.Candidate.Diagnostics,
                links = read.Links.Take(20).Select(childLink => new { childLink.Url, childLink.Text }),
            });
        }

        return JsonSerializer.Serialize(new
        {
            sourcePage = CandidateSummary(sourceRead.Candidate),
            sameDomainOnly,
            followed,
        }, JsonOptions);
    }

    private async Task<string> StagePageAsync(Guid pageId, string rationale)
    {
        var candidate = await candidates.StageAsync(pageId, rationale);
        return JsonSerializer.Serialize(new
        {
            staged = true,
            page = CandidateSummary(candidate),
        }, JsonOptions);
    }

    private async Task<string> ListStagedPagesAsync(ResearchToolContext context)
    {
        var staged = await candidates.ListStagedAsync(context.ProjectId, context.ConversationId);
        return JsonSerializer.Serialize(staged.Select(candidate => new
        {
            candidate.Id,
            candidate.Title,
            url = BestUrl(candidate),
            candidate.StageRationale,
            candidate.Excerpt,
        }), JsonOptions);
    }

    private static object ReadPayload(WebIngestCandidateReadResult read) => new
    {
        page = CandidateSummary(read.Candidate),
        success = read.Candidate.Status != WebIngestCandidateStatus.Failed,
        diagnostics = read.Candidate.Diagnostics,
        links = read.Links.Take(40).Select(link => new { link.Url, link.Text }),
    };

    private static object CandidateSummary(WebIngestCandidate candidate) => new
    {
        candidate.Id,
        candidate.Status,
        candidate.Title,
        url = BestUrl(candidate),
        candidate.DisplayUrl,
        candidate.SearchQuery,
        candidate.SearchRank,
        candidate.Snippet,
        candidate.Excerpt,
        candidate.Diagnostics,
    };

    private static string BestUrl(WebIngestCandidate candidate) =>
        FirstNonEmpty(candidate.CanonicalUrl, candidate.FinalUrl, candidate.Url);

    private static string BestUrl(WebIngestCandidateView candidate) =>
        FirstNonEmpty(candidate.CanonicalUrl, candidate.FinalUrl, candidate.Url);

    private static string HostKey(string url) =>
        TryNormalizeHttpUrl(url, out _, out var hostKey) ? hostKey : string.Empty;

    private static bool TryNormalizeHttpUrl(string url, out string normalizedUrl, out string hostKey)
    {
        normalizedUrl = string.Empty;
        hostKey = string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "https")) return false;

        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        if ((builder.Scheme == "http" && builder.Port == 80) ||
            (builder.Scheme == "https" && builder.Port == 443))
        {
            builder.Port = -1;
        }

        normalizedUrl = builder.Uri.AbsoluteUri;
        hostKey = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? uri.Host[4..].ToLowerInvariant()
            : uri.Host.ToLowerInvariant();
        return true;
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
}