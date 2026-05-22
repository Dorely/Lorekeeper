using Lorekeeper.Models;

namespace Lorekeeper.Research;

public sealed record WebIngestCandidateView(
    Guid Id,
    Guid ProjectId,
    Guid? ResearchConversationId,
    Guid? IngestJobId,
    WebIngestCandidateDiscoveryKind DiscoveryKind,
    WebIngestCandidateStatus Status,
    string Url,
    string FinalUrl,
    string CanonicalUrl,
    string DisplayUrl,
    string ParentUrl,
    string Title,
    string Snippet,
    string Excerpt,
    string SourceProviderName,
    string SearchQuery,
    int? SearchRank,
    int CrawlDepth,
    string StageRationale,
    string Diagnostics,
    DateTime? FetchedAt,
    DateTime? StagedAt,
    DateTime? QueuedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record WebIngestCandidateReadResult(
    WebIngestCandidate Candidate,
    IReadOnlyList<WebPageLink> Links,
    bool FromCache = false);
