using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lorekeeper.Ingest;
using Lorekeeper.Models;
using Lorekeeper.Persistence.Repositories;
using Lorekeeper.Search;

namespace Lorekeeper.Research;

public sealed class WebIngestCandidateService(
    IWebIngestCandidateRepository candidates,
    IWebPageReader pageReader,
    IIngestService ingest) : IWebIngestCandidateService
{
    public async Task<IReadOnlyList<WebIngestCandidateView>> ListAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        (await candidates.ListByProjectAsync(projectId, cancellationToken)).Select(ToView).ToList();

    public async Task<IReadOnlyList<WebIngestCandidateView>> ListResearchAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        (await candidates.ListResearchByProjectAsync(projectId, cancellationToken)).Select(ToView).ToList();

    public async Task<IReadOnlyList<WebIngestCandidateView>> ListStagedAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        (await candidates.ListStagedByProjectAsync(projectId, cancellationToken)).Select(ToView).ToList();

    public async Task<IReadOnlyList<WebIngestCandidateView>> ListStagedAsync(Guid projectId, Guid? researchConversationId, CancellationToken cancellationToken = default) =>
        (await candidates.ListStagedByProjectAsync(projectId, researchConversationId, cancellationToken)).Select(ToView).ToList();

    public async Task<WebIngestCandidate> CreateFromSearchResultAsync(
        Guid projectId,
        Guid? conversationId,
        int? searchProviderId,
        string providerName,
        string query,
        WebSearchResult result,
        CancellationToken cancellationToken = default)
    {
        var normalizedUrl = NormalizeUrl(result.Url);
        var candidate = await candidates.FindByUrlAsync(projectId, normalizedUrl, cancellationToken);
        var isNew = candidate is null;
        if (candidate is null)
        {
            candidate = new WebIngestCandidate
            {
                ProjectId = projectId,
                ResearchConversationId = conversationId,
                SearchProviderId = searchProviderId,
                DiscoveryKind = WebIngestCandidateDiscoveryKind.SearchResult,
                Status = WebIngestCandidateStatus.Discovered,
                Url = normalizedUrl,
            };
        }

        candidate.ResearchConversationId ??= conversationId;
        candidate.SearchProviderId = searchProviderId;
        candidate.SourceProviderName = providerName;
        candidate.SearchQuery = query;
        candidate.SearchRank = result.Rank;
        candidate.Title = result.Title.Trim();
        candidate.DisplayUrl = result.DisplayUrl.Trim();
        candidate.Snippet = result.Snippet.Trim();
        candidate.Excerpt = string.IsNullOrWhiteSpace(candidate.Excerpt) ? result.Snippet.Trim() : candidate.Excerpt;
        candidate.RawMetadataJson = string.IsNullOrWhiteSpace(result.RawJson) ? "{}" : result.RawJson;
        candidate.UpdatedAt = DateTime.UtcNow;
        if (isNew)
            await candidates.AddAsync(candidate, cancellationToken);
        else
            candidates.Update(candidate);
        await candidates.SaveChangesAsync(cancellationToken);
        return candidate;
    }

    public async Task<WebIngestCandidateReadResult> ReadCandidateAsync(Guid candidateId, CancellationToken cancellationToken = default)
    {
        var candidate = await candidates.GetByIdAsync(candidateId, cancellationToken)
            ?? throw new InvalidOperationException($"Web ingest candidate {candidateId} was not found.");
        return await ReadIntoCandidateAsync(candidate, candidate.Url, cancellationToken);
    }

    public async Task<WebIngestCandidateReadResult> ReadUrlAsync(
        Guid projectId,
        Guid? conversationId,
        string url,
        WebIngestCandidateDiscoveryKind discoveryKind = WebIngestCandidateDiscoveryKind.DirectUrl,
        string? searchQuery = null,
        int? searchRank = null,
        string? parentUrl = null,
        int crawlDepth = 0,
        CancellationToken cancellationToken = default)
    {
        var normalizedUrl = NormalizeUrl(url);
        var candidate = await candidates.FindByUrlAsync(projectId, normalizedUrl, cancellationToken);
        if (candidate is null)
        {
            candidate = new WebIngestCandidate
            {
                ProjectId = projectId,
                ResearchConversationId = conversationId,
                DiscoveryKind = discoveryKind,
                Url = normalizedUrl,
                SearchQuery = searchQuery?.Trim() ?? string.Empty,
                SearchRank = searchRank,
                ParentUrl = parentUrl?.Trim() ?? string.Empty,
                CrawlDepth = crawlDepth,
            };
            await candidates.AddAsync(candidate, cancellationToken);
            await candidates.SaveChangesAsync(cancellationToken);
        }

        candidate.ResearchConversationId ??= conversationId;
        candidate.DiscoveryKind = discoveryKind;
        if (!string.IsNullOrWhiteSpace(searchQuery)) candidate.SearchQuery = searchQuery.Trim();
        candidate.SearchRank = searchRank ?? candidate.SearchRank;
        if (!string.IsNullOrWhiteSpace(parentUrl)) candidate.ParentUrl = parentUrl.Trim();
        candidate.CrawlDepth = crawlDepth;
        return await ReadIntoCandidateAsync(candidate, normalizedUrl, cancellationToken);
    }

    public async Task<WebIngestCandidate> StageAsync(Guid candidateId, string rationale, CancellationToken cancellationToken = default)
    {
        var candidate = await candidates.GetByIdAsync(candidateId, cancellationToken)
            ?? throw new InvalidOperationException($"Web ingest candidate {candidateId} was not found.");
        if (string.IsNullOrWhiteSpace(candidate.ExtractedText))
            throw new InvalidOperationException("Read the page before staging it for ingestion.");
        if (candidate.Status == WebIngestCandidateStatus.Queued)
            return candidate;

        candidate.Status = WebIngestCandidateStatus.Staged;
        candidate.StageRationale = rationale?.Trim() ?? string.Empty;
        candidate.StagedAt = DateTime.UtcNow;
        candidate.UpdatedAt = DateTime.UtcNow;
        candidates.Update(candidate);
        await candidates.SaveChangesAsync(cancellationToken);
        return candidate;
    }

    public async Task<WebIngestCandidate> UnstageAsync(Guid candidateId, CancellationToken cancellationToken = default)
    {
        var candidate = await candidates.GetByIdAsync(candidateId, cancellationToken)
            ?? throw new InvalidOperationException($"Web ingest candidate {candidateId} was not found.");
        if (candidate.Status == WebIngestCandidateStatus.Queued) return candidate;

        candidate.Status = string.IsNullOrWhiteSpace(candidate.ExtractedText)
            ? WebIngestCandidateStatus.Discovered
            : WebIngestCandidateStatus.Read;
        candidate.StageRationale = string.Empty;
        candidate.StagedAt = null;
        candidate.UpdatedAt = DateTime.UtcNow;
        candidates.Update(candidate);
        await candidates.SaveChangesAsync(cancellationToken);
        return candidate;
    }

    public async Task<IngestJob> QueueAsync(Guid candidateId, int? providerId, string? instructions, CancellationToken cancellationToken = default)
    {
        var candidate = await candidates.GetByIdAsync(candidateId, cancellationToken)
            ?? throw new InvalidOperationException($"Web ingest candidate {candidateId} was not found.");
        if (candidate.Status == WebIngestCandidateStatus.Queued && candidate.IngestJobId is not null)
        {
            var existingJob = await ingest.GetJobDetailAsync(candidate.IngestJobId.Value, cancellationToken);
            if (existingJob is not null) return existingJob;
        }
        if (string.IsNullOrWhiteSpace(candidate.ExtractedText))
            _ = await ReadIntoCandidateAsync(candidate, candidate.Url, cancellationToken);
        if (string.IsNullOrWhiteSpace(candidate.ExtractedText))
            throw new InvalidOperationException("Page text is empty and cannot be ingested.");

        var job = await ingest.CreateJobAsync(candidate.ProjectId, new IngestCreateJobRequest(
            SourceTitle(candidate),
            BuildSourceText(candidate),
            instructions?.Trim() ?? string.Empty,
            "Webpage",
            BuildDescription(candidate),
            providerId,
            SourceUrl: candidate.Url,
            FinalUrl: candidate.FinalUrl,
            CanonicalUrl: candidate.CanonicalUrl,
            FetchedAt: candidate.FetchedAt,
            ContentType: candidate.ContentType,
            SourceMetadataJson: candidate.RawMetadataJson), cancellationToken);

        candidate.Status = WebIngestCandidateStatus.Queued;
        candidate.IngestJobId = job.Id;
        candidate.QueuedAt = DateTime.UtcNow;
        candidate.UpdatedAt = DateTime.UtcNow;
        candidates.Update(candidate);
        await candidates.SaveChangesAsync(cancellationToken);
        return job;
    }

    public async Task<IngestJob> QueueBatchAsync(
        Guid projectId,
        IReadOnlyCollection<Guid> candidateIds,
        int? providerId,
        string? instructions,
        string? title = null,
        CancellationToken cancellationToken = default)
    {
        var distinctIds = candidateIds.Distinct().ToArray();
        if (distinctIds.Length == 0)
            throw new InvalidOperationException("Choose at least one webpage to queue.");

        var batchCandidates = await candidates.ListByIdsAsync(distinctIds, cancellationToken);
        if (batchCandidates.Count == 0)
            throw new InvalidOperationException("No webpage candidates were found to queue.");
        if (batchCandidates.Any(candidate => candidate.ProjectId != projectId))
            throw new InvalidOperationException("All webpage candidates must belong to the current project.");

        var alreadyQueuedJobIds = batchCandidates
            .Where(candidate => candidate.Status == WebIngestCandidateStatus.Queued && candidate.IngestJobId is not null)
            .Select(candidate => candidate.IngestJobId!.Value)
            .Distinct()
            .ToList();
        var candidatesToQueue = batchCandidates
            .Where(candidate => candidate.Status != WebIngestCandidateStatus.Queued)
            .ToList();

        if (candidatesToQueue.Count == 0 && alreadyQueuedJobIds.Count == 1)
        {
            var existingJob = await ingest.GetJobDetailAsync(alreadyQueuedJobIds[0], cancellationToken);
            if (existingJob is not null) return existingJob;
        }
        if (candidatesToQueue.Count == 0)
            throw new InvalidOperationException("The selected webpages are already queued.");

        foreach (var candidate in candidatesToQueue)
        {
            if (string.IsNullOrWhiteSpace(candidate.ExtractedText))
                _ = await ReadIntoCandidateAsync(candidate, candidate.Url, cancellationToken);
            if (string.IsNullOrWhiteSpace(candidate.ExtractedText))
                throw new InvalidOperationException($"Page '{SourceTitle(candidate)}' has no readable text and cannot be queued.");
        }

        var sourceTitle = string.IsNullOrWhiteSpace(title)
            ? BatchSourceTitle(candidatesToQueue)
            : title.Trim();
        var firstCandidate = candidatesToQueue[0];
        var job = await ingest.CreateJobAsync(projectId, new IngestCreateJobRequest(
            sourceTitle,
            BuildBatchSourceText(candidatesToQueue),
            instructions?.Trim() ?? string.Empty,
            "Webpages",
            BuildBatchDescription(candidatesToQueue),
            providerId,
            SourceUrl: firstCandidate.Url,
            FinalUrl: firstCandidate.FinalUrl,
            CanonicalUrl: firstCandidate.CanonicalUrl,
            FetchedAt: candidatesToQueue.Min(candidate => candidate.FetchedAt),
            ContentType: firstCandidate.ContentType,
            SourceMetadataJson: BuildBatchMetadataJson(candidatesToQueue)), cancellationToken);

        var now = DateTime.UtcNow;
        foreach (var candidate in candidatesToQueue)
        {
            candidate.Status = WebIngestCandidateStatus.Queued;
            candidate.IngestJobId = job.Id;
            candidate.QueuedAt = now;
            candidate.UpdatedAt = now;
            candidates.Update(candidate);
        }
        await candidates.SaveChangesAsync(cancellationToken);
        return job;
    }

    public async Task DeleteAsync(Guid candidateId, CancellationToken cancellationToken = default)
    {
        var candidate = await candidates.GetByIdAsync(candidateId, cancellationToken)
            ?? throw new InvalidOperationException($"Web ingest candidate {candidateId} was not found.");
        if (candidate.Status == WebIngestCandidateStatus.Queued)
            throw new InvalidOperationException("Queued webpages cannot be removed from the research list.");

        candidates.Remove(candidate);
        await candidates.SaveChangesAsync(cancellationToken);
    }

    private async Task<WebIngestCandidateReadResult> ReadIntoCandidateAsync(WebIngestCandidate candidate, string url, CancellationToken cancellationToken)
    {
        var result = await pageReader.ReadAsync(url, cancellationToken);
        candidate.FinalUrl = result.FinalUrl;
        candidate.CanonicalUrl = result.CanonicalUrl;
        candidate.ContentType = result.ContentType;
        candidate.Diagnostics = result.Diagnostics;
        candidate.FetchedAt = DateTime.UtcNow;
        candidate.UpdatedAt = DateTime.UtcNow;

        if (result.Success)
        {
            candidate.Title = string.IsNullOrWhiteSpace(result.Title) ? candidate.Title : result.Title;
            candidate.ExtractedText = result.Text;
            candidate.Excerpt = result.Excerpt;
            if (candidate.Status is not WebIngestCandidateStatus.Staged and not WebIngestCandidateStatus.Queued)
                candidate.Status = WebIngestCandidateStatus.Read;
        }
        else if (candidate.Status != WebIngestCandidateStatus.Queued)
        {
            candidate.Status = WebIngestCandidateStatus.Failed;
        }

        candidates.Update(candidate);
        await candidates.SaveChangesAsync(cancellationToken);
        return new WebIngestCandidateReadResult(candidate, result.Links);
    }

    private static WebIngestCandidateView ToView(WebIngestCandidate candidate) =>
        new(
            candidate.Id,
            candidate.ProjectId,
            candidate.ResearchConversationId,
            candidate.IngestJobId,
            candidate.DiscoveryKind,
            candidate.Status,
            candidate.Url,
            candidate.FinalUrl,
            candidate.CanonicalUrl,
            candidate.DisplayUrl,
            candidate.ParentUrl,
            SourceTitle(candidate),
            candidate.Snippet,
            candidate.Excerpt,
            candidate.SourceProviderName,
            candidate.SearchQuery,
            candidate.SearchRank,
            candidate.CrawlDepth,
            candidate.StageRationale,
            candidate.Diagnostics,
            candidate.FetchedAt,
            candidate.StagedAt,
            candidate.QueuedAt,
            candidate.CreatedAt,
            candidate.UpdatedAt);

    private static string BuildSourceText(WebIngestCandidate candidate)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {SourceTitle(candidate)}");
        sb.AppendLine();
        sb.AppendLine($"Source URL: {BestUrl(candidate)}");
        if (!string.IsNullOrWhiteSpace(candidate.SearchQuery)) sb.AppendLine($"Discovered by search: {candidate.SearchQuery}");
        if (!string.IsNullOrWhiteSpace(candidate.StageRationale)) sb.AppendLine($"Research staging rationale: {candidate.StageRationale}");
        sb.AppendLine();
        sb.AppendLine(candidate.ExtractedText.Trim());
        return sb.ToString();
    }

    private static string BuildBatchSourceText(IReadOnlyList<WebIngestCandidate> batchCandidates)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {BatchSourceTitle(batchCandidates)}");
        sb.AppendLine();
        sb.AppendLine($"Combined webpage ingest source containing {batchCandidates.Count} page{(batchCandidates.Count == 1 ? string.Empty : "s")}.");
        sb.AppendLine();

        foreach (var candidate in batchCandidates)
        {
            sb.AppendLine($"## {SourceTitle(candidate)}");
            sb.AppendLine();
            sb.AppendLine($"Source URL: {BestUrl(candidate)}");
            if (!string.IsNullOrWhiteSpace(candidate.SearchQuery)) sb.AppendLine($"Discovered by search: {candidate.SearchQuery}");
            if (!string.IsNullOrWhiteSpace(candidate.ParentUrl)) sb.AppendLine($"Discovered from: {candidate.ParentUrl}");
            if (!string.IsNullOrWhiteSpace(candidate.StageRationale)) sb.AppendLine($"Research staging rationale: {candidate.StageRationale}");
            sb.AppendLine();
            sb.AppendLine(candidate.ExtractedText.Trim());
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string BuildDescription(WebIngestCandidate candidate)
    {
        var parts = new List<string> { $"Webpage fetched from {BestUrl(candidate)}." };
        if (!string.IsNullOrWhiteSpace(candidate.SearchQuery)) parts.Add($"Search query: {candidate.SearchQuery}.");
        if (!string.IsNullOrWhiteSpace(candidate.StageRationale)) parts.Add($"Research rationale: {candidate.StageRationale}");
        return string.Join(" ", parts);
    }

    private static string BuildBatchDescription(IReadOnlyList<WebIngestCandidate> batchCandidates)
    {
        var firstUrl = BestUrl(batchCandidates[0]);
        return $"Combined webpage ingest source containing {batchCandidates.Count} page{(batchCandidates.Count == 1 ? string.Empty : "s")}. First page: {firstUrl}.";
    }

    private static string BuildBatchMetadataJson(IReadOnlyList<WebIngestCandidate> batchCandidates) =>
        JsonSerializer.Serialize(new
        {
            source = "webpage-batch",
            pageCount = batchCandidates.Count,
            pages = batchCandidates.Select(candidate => new
            {
                candidate.Id,
                candidate.Url,
                candidate.FinalUrl,
                candidate.CanonicalUrl,
                title = SourceTitle(candidate),
                candidate.ParentUrl,
                candidate.SearchQuery,
                candidate.SearchRank,
                candidate.CrawlDepth,
                candidate.FetchedAt,
                candidate.ContentType,
            }),
        });

    private static string BatchSourceTitle(IReadOnlyList<WebIngestCandidate> batchCandidates)
    {
        if (batchCandidates.Count == 1) return SourceTitle(batchCandidates[0]);

        var host = TryHost(BestUrl(batchCandidates[0]));
        return string.IsNullOrWhiteSpace(host)
            ? $"Webpage batch ({batchCandidates.Count} pages)"
            : $"{host} webpage batch ({batchCandidates.Count} pages)";
    }

    private static string SourceTitle(WebIngestCandidate candidate) =>
        string.IsNullOrWhiteSpace(candidate.Title) ? BestUrl(candidate) : candidate.Title.Trim();

    private static string BestUrl(WebIngestCandidate candidate) =>
        FirstNonEmpty(candidate.CanonicalUrl, candidate.FinalUrl, candidate.Url);

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string TryHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;

    private static string NormalizeUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return url.Trim();

        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        return builder.Uri.ToString().TrimEnd('/');
    }

    public static string ContentHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}