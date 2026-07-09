using System.Text.Json;
using Lorekeeper.Context;
using Lorekeeper.EntityVisuals;
using Lorekeeper.Ingest;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Search;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Lorekeeper.Research;

public sealed class ResearchToolContext(
    Guid projectId,
    Guid conversationId,
    Action onMutated,
    OutlineToolStagingContext? staging = null,
    bool visionReady = false)
{
    private readonly List<EntityVisualContextReference> _entityVisuals = [];
    private readonly List<SourceVisualCandidateData> _sourceVisuals = [];
    public Guid ProjectId { get; } = projectId;
    public Guid ConversationId { get; } = conversationId;
    public Action OnMutated { get; } = onMutated;
    public OutlineToolStagingContext? Staging { get; } = staging;
    public bool VisionReady { get; } = visionReady;
    public void QueueEntityVisuals(IEnumerable<EntityVisualContextReference> values) => _entityVisuals.AddRange(values);
    public void QueueSourceVisual(SourceVisualCandidateData value) => _sourceVisuals.Add(value);
    public IReadOnlyList<EntityVisualContextReference> DrainEntityVisuals() { var result = _entityVisuals.ToList(); _entityVisuals.Clear(); return result; }
    public IReadOnlyList<SourceVisualCandidateData> DrainSourceVisuals() { var result = _sourceVisuals.ToList(); _sourceVisuals.Clear(); return result; }
}

public sealed class ResearchTools(
    ISearchProviderService searchProviders,
    IWebIngestCandidateService candidates,
    OutlineCollaborationTools outlineTools,
    IEntityService entities,
    IEntityRelationContextService entityRelations,
    IEntityVisualExampleService entityVisualExamples,
    IWebPageReader pageReader,
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

            AIFunctionFactory.Create(
                method: (Guid pageId, string? imageUrl = null) => InspectWebImageAsync(context, pageId, imageUrl),
                name: "inspect_web_image",
                description: "Safely fetch, validate, cache, and visually inspect an image discovered on a read webpage. Use the exact image URL returned by the page read when more than one is available."),

            AIFunctionFactory.Create(
                method: (Guid candidateId, EntityVisualTarget[] entityTargets) => ImportWebImageAsync(context, candidateId, entityTargets),
                name: "import_web_image_to_entities",
                description: "After the user confirms storage, promote an inspected web image into the project library and attach it to every unambiguously represented entity. Never guess ambiguous associations."),
        };

        var allowedGraphToolNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "list_entity_types",
            "list_search_sources",
            "read_project_source",
            "search_project",
            "search_entities",
            "create_entity",
            "update_entity",
            "link_entities",
            "list_entity_visual_examples",
            "attach_entity_visual_example",
            "update_entity_visual_example",
            "detach_entity_visual_example",
        };

        var outlineContext = new OutlineCollaborationContext(
            context.ProjectId, context.OnMutated, context.Staging, context.VisionReady, context.QueueEntityVisuals);
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
        var read = await TryReadCandidateForToolAsync(context, pageId);
        if (read is null)
            return $"Error: search result pageId {pageId:N} was not found in this project. Run web_search again and use a page id from the returned results.";

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
        var sourceRead = await TryReadCandidateForToolAsync(context, pageId);
        if (sourceRead is null)
            return $"Error: source pageId {pageId:N} was not found in this project. Run web_search or read_webpage again and use a page id from the returned page payload.";

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

    private async Task<WebIngestCandidateReadResult?> TryReadCandidateForToolAsync(ResearchToolContext context, Guid pageId)
    {
        var cached = await candidates.GetCachedDetailAsync(context.ProjectId, pageId);
        if (cached is null) return null;

        try
        {
            return await candidates.ReadCandidateForConversationAsync(pageId, context.ConversationId);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("was not found", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
    }

    private async Task<string> ReadEntityAsync(ResearchToolContext context, Guid entityId)
    {
        if (context.Staging is not null)
        {
            await QueueEntityVisualsAsync(context, entityId);
            return await context.Staging.ReadEntityAsync(entityId, addedToContextFeed: false, EntityRelationOptions);
        }

        var entity = await entities.GetAsync(context.ProjectId, entityId);
        if (entity is null)
            return $"Error: entity {entityId} not found in this project.";

        var links = await entities.ListLinksAsync(context.ProjectId, entityId);
        var manualLinks = links.Where(link => !link.IsAutoLink).Select(LinkPayload).ToList();
        var autoMentionLinks = links.Where(link => link.IsAutoLink).Select(LinkPayload).ToList();
        var relationContext = await entityRelations.BuildForEntityAsync(context.ProjectId, entityId, EntityRelationOptions);
        var visualExamples = await QueueEntityVisualsAsync(context, entityId);
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
            visualExamples = visualExamples.Select(VisualPayload),
            links = manualLinks.Concat(autoMentionLinks),
            manualLinks,
            autoMentionLinks,
            relationContext,
        }, JsonOptions);
    }

    private async Task<string> InspectWebImageAsync(ResearchToolContext context, Guid pageId, string? imageUrl)
    {
        var read = await TryReadCandidateForToolAsync(context, pageId);
        if (read is null) return $"Error: webpage {pageId:N} was not found in this project.";
        var image = string.IsNullOrWhiteSpace(imageUrl)
            ? read.Images.Count == 1 ? read.Images[0] : null
            : read.Images.FirstOrDefault(candidate => string.Equals(candidate.Url, imageUrl.Trim(), StringComparison.OrdinalIgnoreCase));
        if (image is null)
            return read.Images.Count == 0
                ? "Error: this page did not expose any supported image candidates."
                : $"Error: choose an exact image URL from the page's {read.Images.Count} image candidates.";

        var fetched = await pageReader.ReadImageAsync(image.Url);
        if (!fetched.Success) return $"Error: {fetched.Diagnostics}";
        try
        {
            var candidate = await entityVisualExamples.CreateCandidateAsync(new SourceVisualCandidateCreateRequest(
                context.ProjectId,
                SourceVisualCandidateKind.ResearchWeb,
                FileNameFromUrl(fetched.FinalUrl, fetched.ContentType),
                fetched.ContentType,
                fetched.Data,
                image.AltText,
                image.Caption,
                fetched.FinalUrl,
                Locator: image.Url,
                MetadataJson: JsonSerializer.Serialize(new { pageId, pageUrl = BestUrl(read.Candidate), imageUrl = image.Url, fetched.FinalUrl }),
                WebIngestCandidateId: pageId));
            var data = await entityVisualExamples.GetCandidateDataAsync(context.ProjectId, candidate.Id, maxEdge: 1024);
            if (data is not null && context.VisionReady) context.QueueSourceVisual(data);
            return JsonSerializer.Serialize(new
            {
                candidate,
                delivery = context.VisionReady ? "image bytes supplied on the next model round" : "metadata only; provider is not vision-ready",
                instruction = "Do not import until the user confirms storage and the represented entity is unambiguous.",
            }, JsonOptions);
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    private async Task<string> ImportWebImageAsync(ResearchToolContext context, Guid candidateId, EntityVisualTarget[] entityTargets)
    {
        var targets = entityTargets.Where(target => target.EntityId != Guid.Empty).DistinctBy(target => target.EntityId).ToList();
        if (targets.Count == 0) return "Error: at least one unambiguous entity target is required.";
        if (context.Staging is not null)
        {
            var after = new EntityVisualChange("import", CandidateId: candidateId, Targets: targets);
            return await context.Staging.StageExternalChangeAsync(
                "Import a researched image and attach it to entities", null, after,
                new { status = "staged", candidateId, targets }, "EntityVisualExample", candidateId.ToString("N"));
        }
        var attached = new List<EntityVisualExampleView>();
        try
        {
            foreach (var target in targets)
                attached.Add(await entityVisualExamples.PromoteAndAttachAsync(context.ProjectId, candidateId, target.EntityId, target.Label, EntityVisualExampleOrigin.Research));
            context.OnMutated();
            return JsonSerializer.Serialize(new { status = "imported", attached = attached.Select(VisualPayload) }, JsonOptions);
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    private async Task<IReadOnlyList<EntityVisualExampleView>> QueueEntityVisualsAsync(ResearchToolContext context, Guid entityId)
    {
        var examples = await entityVisualExamples.ListForEntityAsync(context.ProjectId, entityId);
        context.QueueEntityVisuals(examples.Select(example => new EntityVisualContextReference(
            example.Image.Id, example.EntityId, example.EntityType, example.EntityName, example.Label,
            example.SortOrder, example.Image.FileName, example.Image.AltText, example.Image.Prompt)));
        return examples;
    }

    private static object VisualPayload(EntityVisualExampleView example) => new
    {
        example.Id, example.EntityId, example.EntityName, example.Label, example.SortOrder,
        image = new { example.Image.Id, example.Image.FileName, example.Image.PreviewUrl, example.Image.AltText, example.Image.Prompt },
    };

    private static string FileNameFromUrl(string url, string contentType)
    {
        var name = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Path.GetFileName(uri.LocalPath) : string.Empty;
        if (!string.IsNullOrWhiteSpace(name)) return name;
        return contentType == "image/png" ? "research-image.png" : contentType == "image/webp" ? "research-image.webp" : "research-image.jpg";
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
        isAutoLink = link.IsAutoLink,
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
            images = read.Images.Take(30).Select(image => new { image.Url, image.AltText, image.Caption }),
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
