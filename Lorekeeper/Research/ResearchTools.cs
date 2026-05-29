using System.Text.Json;
using Lorekeeper.Context;
using Lorekeeper.Ingest;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Search;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Lorekeeper.Research;

public sealed record ResearchToolContext(
    Guid ProjectId,
    Guid ConversationId,
    Action OnMutated,
    OutlineToolStagingContext? Staging = null);

public sealed class ResearchTools(
    ISearchProviderService searchProviders,
    IWebIngestCandidateService candidates,
    OutlineCollaborationTools outlineTools,
    IEntityService entities,
    IEntityRelationContextService entityRelations,
    IWebLinkPolicy linkPolicy,
    IOptions<WebResearchOptions> webOptions)
{
    private static readonly EntityRelationContextOptions EntityRelationOptions = new()
    {
        Depth = 2,
        MaxDirectLinks = 8,
        MaxTraversalPaths = 10,
        MaxLinksPerNode = 8,
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public async Task<IList<AITool>> BuildAsync(ResearchToolContext context, CancellationToken cancellationToken = default)
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(
                method: (string query, int count = 5) => WebSearchAsync(context, query, count),
                name: "web_search",
                description: "Search the public web with the active configured search provider. Persists each result as a cached research source and returns stable page ids plus title, URL, and snippet."),

            AIFunctionFactory.Create(
                method: (Guid pageId, int? pageNumber = null) => ReadSearchResultAsync(context, pageId, pageNumber),
                name: "read_search_result",
                description:
                    "Read one paginated page of extracted text from a previously discovered search result by page id. " +
                    "Uses cached full-page text and links when available; otherwise fetches and caches the full readable page once. " +
                    "If a host is cooling down after blocked traffic, the tool returns diagnostics instead of retrying immediately. " +
                    "Omit pageNumber to read page 1; use returned nextPageArguments to continue."),

            AIFunctionFactory.Create(
                method: (string url, int? pageNumber = null) => ReadWebpageAsync(context, url, pageNumber),
                name: "read_webpage",
                description:
                    "Read one paginated page of extracted text from a specific webpage URL supplied by the user or discovered from another page's links. " +
                    "Uses cached full-page text and links when available; otherwise fetches and caches the full readable page once. " +
                    "Prefer specific article/source URLs over account, edit, history, file, category, or special pages. " +
                    "Omit pageNumber to read page 1; use returned nextPageArguments to continue."),

            AIFunctionFactory.Create(
                method: (Guid pageId, int count = 5, bool sameDomainOnly = true) => FollowPageLinksAsync(context, pageId, count, sameDomainOnly),
                name: "follow_page_links",
                description:
                    "Read selected outgoing links from an already-read page, persisting each followed link as a cached research source. " +
                    "Navigation, account, edit/history, special, file, category, and duplicate links are filtered before any fetches. " +
                    "Use this sparingly when a promising source exposes relevant wiki/article/reference links."),

            AIFunctionFactory.Create(
                method: (Guid entityId) => ReadEntityAsync(context, entityId),
                name: "read_entity",
                description: "Read one graph entity by id, including properties, structured wiki data, adjacent links, and relation context. When Review edits is enabled, returns the latest staged entity and link state from this turn."),

            AIFunctionFactory.Create(
                method: (Guid entityId) => ListEntityLinksAsync(context, entityId),
                name: "list_entity_links",
                description: "List all graph links adjacent to an entity, including structural HasChild links and semantic story relationships. When Review edits is enabled, includes staged entity and link changes from this turn."),
        };

        var allowedGraphToolNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "list_entity_types",
            "search_entities",
            "create_entity",
            "update_entity",
            "link_entities",
        };

        var outlineContext = new OutlineCollaborationContext(context.ProjectId, context.OnMutated, context.Staging);
        foreach (var tool in (await outlineTools.BuildAsync(outlineContext, cancellationToken)).OfType<AIFunction>())
        {
            if (allowedGraphToolNames.Contains(tool.Name))
                tools.Add(tool);
        }

        return tools;
    }

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

    private async Task<string> ReadSearchResultAsync(ResearchToolContext context, Guid pageId, int? pageNumber)
    {
        var read = await candidates.ReadCandidateForConversationAsync(pageId, context.ConversationId);
        return SerializeReadPayload(read, pageNumber, WebReadToolKind.SearchResult);
    }

    private async Task<string> ReadWebpageAsync(ResearchToolContext context, string url, int? pageNumber)
    {
        if (string.IsNullOrWhiteSpace(url)) return "Error: url is required.";
        var read = await candidates.ReadUrlAsync(context.ProjectId, context.ConversationId, url.Trim());
        return SerializeReadPayload(read, pageNumber, WebReadToolKind.DirectUrl);
    }

    private async Task<string> FollowPageLinksAsync(ResearchToolContext context, Guid pageId, int count, bool sameDomainOnly)
    {
        count = Math.Clamp(count, 1, Math.Max(1, webOptions.Value.MaxFollowLinksPerPage));
        var sourceRead = await candidates.ReadCandidateForConversationAsync(pageId, context.ConversationId);
        var sourceUrl = BestUrl(sourceRead.Candidate);
        var followed = new List<object>();
        var selectedLinks = linkPolicy.FilterAndPrioritizeLinks(
            sourceRead.Links,
            sourceUrl,
            sameDomainOnly,
            count,
            out var skippedCount);

        foreach (var link in selectedLinks)
        {
            if (followed.Count >= count) break;
            if (!linkPolicy.TryNormalizeHttpUrl(link.Url, out var normalizedUrl, out _)) continue;

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
                fromCache = read.FromCache,
                diagnostics = read.Candidate.Diagnostics,
                links = read.Links.Take(20).Select(childLink => new { childLink.Url, childLink.Text }),
            });
        }

        return JsonSerializer.Serialize(new
        {
            sourcePage = CandidateSummary(sourceRead.Candidate),
            sameDomainOnly,
            skippedLinks = skippedCount,
            followed,
        }, JsonOptions);
    }

    private async Task<string> ReadEntityAsync(ResearchToolContext context, Guid entityId)
    {
        if (context.Staging is not null)
            return await context.Staging.ReadEntityAsync(entityId, addedToContextFeed: false, EntityRelationOptions);

        var entity = await entities.GetAsync(context.ProjectId, entityId);
        if (entity is null)
            return $"Error: entity {entityId} not found in this project.";

        var links = await entities.ListLinksAsync(context.ProjectId, entityId);
        var relationContext = await entityRelations.BuildForEntityAsync(context.ProjectId, entityId, EntityRelationOptions);
        return JsonSerializer.Serialize(new
        {
            id = entity.Id,
            type = entity.Type,
            name = entity.Name,
            order = entity.Order,
            parentId = entity.ParentId,
            properties = entity.Properties,
            summary = entity.Summary,
            aliases = entity.Aliases,
            wikiSections = entity.WikiSections,
            canonSources = entity.CanonSources,
            links = links.Select(LinkPayload),
            relationContext,
        }, JsonOptions);
    }

    private async Task<string> ListEntityLinksAsync(ResearchToolContext context, Guid entityId)
    {
        if (context.Staging is not null)
            return await context.Staging.ListEntityLinksAsync(entityId);

        var entity = await entities.GetAsync(context.ProjectId, entityId);
        if (entity is null)
            return $"Error: entity {entityId} not found in this project.";

        var links = await entities.ListLinksAsync(context.ProjectId, entityId);
        return JsonSerializer.Serialize(links.Select(LinkPayload), JsonOptions);
    }

    private static object LinkPayload(EntityLink link) => new
    {
        edgeId = link.EdgeId,
        edgeType = link.EdgeType,
        direction = link.Direction.ToString(),
        otherEntityId = link.OtherEntityId,
        otherEntityName = link.OtherEntityName,
        otherEntityType = link.OtherEntityType,
        sortOrder = link.SortOrder,
        properties = link.Properties,
        summary = link.Summary,
        relationshipCitations = link.RelationshipCitations,
    };

    private string SerializeReadPayload(WebIngestCandidateReadResult read, int? pageNumber, WebReadToolKind toolKind)
    {
        var requestedPageNumber = pageNumber ?? 1;
        if (requestedPageNumber < 1)
            return "Error: pageNumber must be 1 or greater.";

        var pageMaxChars = EffectiveReadPageMaxChars();
        var text = read.Candidate.ExtractedText ?? string.Empty;
        var pageCount = CountTextPages(text, pageMaxChars);
        if (requestedPageNumber > pageCount)
            return $"Error: pageNumber {requestedPageNumber} is beyond the page's {pageCount} text page(s).";

        var pageStart = text.Length == 0 ? 0 : (requestedPageNumber - 1) * pageMaxChars;
        var pageText = text.Length == 0
            ? string.Empty
            : text.Substring(pageStart, Math.Min(pageMaxChars, text.Length - pageStart));

        return JsonSerializer.Serialize(new
        {
            page = CandidateSummary(read.Candidate),
            success = read.Candidate.Status != WebIngestCandidateStatus.Failed,
            fromCache = read.FromCache,
            diagnostics = read.Candidate.Diagnostics,
            pagination = new
            {
                currentPage = requestedPageNumber,
                pageCount,
                pageMaxChars,
                totalTextChars = text.Length,
                contentCharCount = pageText.Length,
                hasPreviousPage = requestedPageNumber > 1,
                previousPageNumber = requestedPageNumber > 1 ? requestedPageNumber - 1 : (int?)null,
                hasNextPage = requestedPageNumber < pageCount,
                nextPageNumber = requestedPageNumber < pageCount ? requestedPageNumber + 1 : (int?)null,
            },
            previousPageArguments = requestedPageNumber > 1
                ? PageArguments(read.Candidate, toolKind, requestedPageNumber - 1)
                : null,
            nextPageArguments = requestedPageNumber < pageCount
                ? PageArguments(read.Candidate, toolKind, requestedPageNumber + 1)
                : null,
            text = pageText,
            links = read.Links.Take(Math.Max(0, webOptions.Value.MaxLinksReturnedToModel)).Select(link => new { link.Url, link.Text }),
        }, JsonOptions);
    }

    private int EffectiveReadPageMaxChars() => Math.Max(1_000, webOptions.Value.ReadPageMaxChars);

    private static int CountTextPages(string text, int pageMaxChars) =>
        string.IsNullOrEmpty(text) ? 1 : (text.Length + pageMaxChars - 1) / pageMaxChars;

    private static object PageArguments(WebIngestCandidate candidate, WebReadToolKind toolKind, int pageNumber)
    {
        if (toolKind == WebReadToolKind.SearchResult)
            return new { pageId = candidate.Id, pageNumber };

        return new { url = BestUrl(candidate), pageNumber };
    }

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
        candidate.ContentHash,
        candidate.Diagnostics,
    };

    private static string BestUrl(WebIngestCandidate candidate) =>
        FirstNonEmpty(candidate.CanonicalUrl, candidate.FinalUrl, candidate.Url);

    private static string BestUrl(WebIngestCandidateView candidate) =>
        FirstNonEmpty(candidate.CanonicalUrl, candidate.FinalUrl, candidate.Url);

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private enum WebReadToolKind
    {
        SearchResult,
        DirectUrl,
    }
}
