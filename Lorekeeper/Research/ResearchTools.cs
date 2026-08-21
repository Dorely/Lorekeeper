using System.Text.Json;
using System.Text.Json.Nodes;
using Lorekeeper.Context;
using Lorekeeper.EntityVisuals;
using Lorekeeper.Ingest;
using Lorekeeper.Images;
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
    private readonly List<ReferenceVisualReadResult> _referenceVisuals = [];
    private readonly List<SourceVisualCandidateData> _sourceVisuals = [];
    public Guid ProjectId { get; } = projectId;
    public Guid ConversationId { get; } = conversationId;
    public Action OnMutated { get; } = onMutated;
    public OutlineToolStagingContext? Staging { get; } = staging;
    public bool VisionReady { get; } = visionReady;
    public void QueueEntityVisuals(IEnumerable<EntityVisualContextReference> values) => _entityVisuals.AddRange(values);
    public void QueueReferenceVisual(ReferenceVisualReadResult value) { if (value.DataDelivered) _referenceVisuals.Add(value); }
    public void QueueSourceVisual(SourceVisualCandidateData value) => _sourceVisuals.Add(value);
    public IReadOnlyList<EntityVisualContextReference> DrainEntityVisuals() { var result = _entityVisuals.ToList(); _entityVisuals.Clear(); return result; }
    public IReadOnlyList<SourceVisualCandidateData> DrainSourceVisuals() { var result = _sourceVisuals.ToList(); _sourceVisuals.Clear(); return result; }
    public IReadOnlyList<ReferenceVisualReadResult> DrainReferenceVisuals() { var result = _referenceVisuals.ToList(); _referenceVisuals.Clear(); return result; }
}

public sealed class ResearchTools(
    ISearchProviderService searchProviders,
    IWebIngestCandidateService candidates,
    OutlineCollaborationTools outlineTools,
    IEntityService entities,
    IEntityRelationContextService entityRelations,
    IEntityVisualExampleService entityVisualExamples,
    IProjectImageService projectImages,
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
                method: (Guid entityId, int? pageNumber = null) => ReadEntityAsync(context, entityId, pageNumber),
                name: "read_entity",
                description: "Read one explicitly paginated graph entity by id, including properties, structured wiki data, all adjacent manual and AutoMention links, bounded relation context, and canonical visual references. Full identity fields and GUIDs repeat on every page; omit pageNumber for page 1 and follow nextPageArguments. When Review edits is enabled, returns the latest staged entity and link state from this turn."),

            AIFunctionFactory.Create(
                method: (Guid entityId, int? pageNumber = null) => ListEntityLinksAsync(context, entityId, pageNumber),
                name: "list_entity_links",
                description: "List explicitly paginated graph links adjacent to an entity, including structural HasChild links and semantic story relationships. Full identity fields repeat on every page; follow nextPageArguments until complete. When Review edits is enabled, includes staged entity and link changes from this turn."),

            AIFunctionFactory.Create(
                method: (Guid pageId, string? imageUrl = null) => InspectWebImageAsync(context, pageId, imageUrl),
                name: "inspect_web_image",
                description: "Safely fetch, validate, cache, and visually inspect an image discovered on a read webpage. Use the exact image URL returned by the page read when more than one is available."),

            AIFunctionFactory.Create(
                method: (Guid candidateId, EntityVisualTarget entityTarget, ProjectImageCropRegion? crop = null, string? cropFileName = null, string? cropAltText = null) =>
                    ImportWebImageAsync(context, candidateId, entityTarget, crop, cropFileName, cropAltText),
                name: "import_web_image_as_entity_reference",
                description: "After the user confirms storage, promote an inspected web image and attach it to one entity only when it is a stable canonical appearance or design reference. If the entity occupies part of a broader image, pass a tight subject-only crop and cropAltText. Make a separate crop/import call for each entity; do not attach ordinary narrative scenes or ambiguous images."),
        };

        var outlineContext = new OutlineCollaborationContext(
            context.ProjectId, context.OnMutated, context.Staging, context.VisionReady, context.QueueEntityVisuals, context.QueueReferenceVisual);
        tools.AddRange(await outlineTools.BuildResearchSharedAsync(outlineContext));

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
            resultKind = "compactDiscovery",
            requestedLimit = count,
            totalMatches = (int?)null,
            totalMatchesKnown = false,
            returnedCount = resultPayloads.Count,
            isComplete = false,
            note = "Search-provider results are discovery previews. Use read_search_result with a returned full page id for complete paginated page text.",
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
                links = new
                {
                    totalCount = read.Links.Count,
                    returnedCount = Math.Min(read.Links.Count, 20),
                    isComplete = read.Links.Count <= 20,
                    items = read.Links.Take(20).Select(childLink => new { childLink.Url, childLink.Text }),
                },
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

    private async Task<string> ReadEntityAsync(ResearchToolContext context, Guid entityId, int? pageNumber)
    {
        if (context.Staging is not null)
        {
            await QueueEntityVisualsAsync(context, entityId);
            return await context.Staging.ReadEntityAsync(entityId, addedToContextFeed: false, EntityRelationOptions, pageNumber);
        }

        var entity = await entities.GetAsync(context.ProjectId, entityId);
        if (entity is null)
            return $"Error: entity {entityId} not found in this project.";

        var links = await entities.ListLinksAsync(context.ProjectId, entityId);
        var manualLinks = links.Where(link => !link.IsAutoLink).Select(LinkPayload).ToList();
        var autoMentionLinks = links.Where(link => link.IsAutoLink).Select(LinkPayload).ToList();
        var relationContext = await entityRelations.BuildForEntityAsync(context.ProjectId, entityId, EntityRelationOptions);
        var canonicalVisualReferences = await QueueEntityVisualsAsync(context, entityId);
        var detail = JsonSerializer.SerializeToNode(new
        {
            properties = entity.Properties,
            summary = entity.Summary,
            aliases = entity.Aliases,
            wikiSections = entity.WikiSections,
            sourceEvidence = entity.SourceEvidence,
            canonicalVisualReferences = canonicalVisualReferences.Select(VisualPayload),
            manualLinks,
            autoMentionLinks,
            relationContextPreview = RelationContextPreview(entity.Id, EntityRelationOptions, relationContext),
        }, JsonOptions);
        return AgentPayloadPaginator.SerializePage(
            AgentPayloadPaginator.EntityIdentity(entity.Id, entity.Type, entity.Name, entity.Order, entity.ParentId),
            detail,
            "read_entity",
            new JsonObject { ["entityId"] = entity.Id },
            pageNumber);
    }

    private static object RelationContextPreview(Guid entityId, EntityRelationContextOptions options, object relationContext) => new
    {
        isComplete = false,
        note = "This traversal is a bounded orientation preview. All adjacent links are included separately; use list_entity_links and read_entity to continue traversal.",
        bounds = new { options.Depth, options.MaxDirectLinks, options.MaxTraversalPaths, options.MaxLinksPerNode },
        detailReadTool = "list_entity_links",
        detailReadArguments = new { entityId, pageNumber = 1 },
        value = relationContext,
    };

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
                instruction = "Do not import until the user confirms storage and the image is suitable as a stable canonical reference. Crop to one isolated subject when the entity occupies only part of the image; ordinary narrative scenes should remain unassociated.",
            }, JsonOptions);
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    private async Task<string> ImportWebImageAsync(
        ResearchToolContext context,
        Guid candidateId,
        EntityVisualTarget entityTarget,
        ProjectImageCropRegion? crop,
        string? cropFileName,
        string? cropAltText)
    {
        var targetValidation = await entityVisualExamples.ValidateTargetsAsync(context.ProjectId, [entityTarget]);
        if (!targetValidation.IsValid)
            return $"Error: {targetValidation.Error} Use one grounded entity id.";
        if (targetValidation.Targets is not [var target])
            return "Error: one entity target is required.";
        IReadOnlyList<EntityVisualTarget> targets = [target];
        if (context.Staging is not null)
        {
            var after = new EntityVisualChange(
                "import",
                CandidateId: candidateId,
                Targets: targets,
                Crop: crop,
                CropFileName: cropFileName?.Trim() ?? string.Empty,
                CropAltText: cropAltText?.Trim() ?? string.Empty);
            return await context.Staging.StageExternalChangeAsync(
                "Import a researched image as an entity canonical reference", null, after,
                new { status = "staged", candidateId, entityTarget = target }, "EntityCanonicalReference", candidateId.ToString("N"));
        }
        var attached = new List<EntityVisualExampleView>();
        try
        {
            var sourceImage = await entityVisualExamples.PromoteCandidateAsync(context.ProjectId, candidateId);
            var referenceImage = crop is null
                ? sourceImage
                : await projectImages.CropAsync(context.ProjectId, sourceImage.Id, new ProjectImageCropRequest(
                    crop,
                    cropFileName?.Trim() ?? string.Empty,
                    cropAltText?.Trim() ?? string.Empty));
            attached.Add(await entityVisualExamples.AttachAsync(
                context.ProjectId,
                target.EntityId,
                referenceImage.Id,
                target.Label,
                EntityVisualExampleOrigin.Research,
                candidateId));
            context.OnMutated();
            return JsonSerializer.Serialize(new { status = "imported", canonicalReference = attached.Select(VisualPayload).Single() }, JsonOptions);
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    private async Task<IReadOnlyList<EntityVisualExampleView>> QueueEntityVisualsAsync(ResearchToolContext context, Guid entityId)
    {
        var examples = await entityVisualExamples.ListForEntityAsync(context.ProjectId, entityId);
        context.QueueEntityVisuals(examples.Select(EntityVisualContextService.ToReference));
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

    private async Task<string> ListEntityLinksAsync(ResearchToolContext context, Guid entityId, int? pageNumber)
    {
        if (context.Staging is not null)
            return await context.Staging.ListEntityLinksAsync(entityId, pageNumber);

        var entity = await entities.GetAsync(context.ProjectId, entityId);
        if (entity is null)
            return $"Error: entity {entityId} not found in this project.";

        var links = await entities.ListLinksAsync(context.ProjectId, entityId);
        return AgentPayloadPaginator.SerializePage(
            AgentPayloadPaginator.EntityIdentity(entity.Id, entity.Type, entity.Name, entity.Order, entity.ParentId),
            JsonSerializer.SerializeToNode(new { links = links.Select(LinkPayload) }, JsonOptions),
            "list_entity_links",
            new JsonObject { ["entityId"] = entity.Id },
            pageNumber);
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
            links = new
            {
                totalCount = read.Links.Count,
                returnedCount = Math.Min(read.Links.Count, Math.Max(0, webOptions.Value.MaxLinksReturnedToModel)),
                isComplete = read.Links.Count <= Math.Max(0, webOptions.Value.MaxLinksReturnedToModel),
                note = "This is an explicitly compact discovery list; use follow_page_links for selected returned URLs.",
                items = read.Links.Take(Math.Max(0, webOptions.Value.MaxLinksReturnedToModel)).Select(link => new { link.Url, link.Text }),
            },
            images = new
            {
                totalCount = read.Images.Count,
                returnedCount = Math.Min(read.Images.Count, 30),
                isComplete = read.Images.Count <= 30,
                note = "This is an explicitly compact image-candidate discovery list.",
                items = read.Images.Take(30).Select(image => new { image.Url, image.AltText, image.Caption }),
            },
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
        snippetPreview = candidate.Snippet,
        excerptPreview = candidate.Excerpt,
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
