namespace Lorekeeper.Models;

public class WebIngestCandidate
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public Guid? ResearchConversationId { get; set; }

    public int? SearchProviderId { get; set; }
    public Guid? IngestJobId { get; set; }

    public WebIngestCandidateDiscoveryKind DiscoveryKind { get; set; } = WebIngestCandidateDiscoveryKind.DirectUrl;
    public WebIngestCandidateStatus Status { get; set; } = WebIngestCandidateStatus.Discovered;

    public required string Url { get; set; }
    public string FinalUrl { get; set; } = string.Empty;
    public string CanonicalUrl { get; set; } = string.Empty;
    public string DisplayUrl { get; set; } = string.Empty;
    public string ParentUrl { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
    public string Snippet { get; set; } = string.Empty;
    public string Excerpt { get; set; } = string.Empty;
    public string ExtractedText { get; set; } = string.Empty;
    public string CachedLinksJson { get; set; } = "[]";
    public string CachedImagesJson { get; set; } = "[]";
    public string ContentHash { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;

    public string SourceProviderName { get; set; } = string.Empty;
    public string SearchQuery { get; set; } = string.Empty;
    public int? SearchRank { get; set; }
    public int CrawlDepth { get; set; }

    public string StageRationale { get; set; } = string.Empty;
    public string Diagnostics { get; set; } = string.Empty;
    public string RawMetadataJson { get; set; } = "{}";

    public DateTime? FetchedAt { get; set; }
    public DateTime? StagedAt { get; set; }
    public DateTime? QueuedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<SourceVisualCandidate> VisualCandidates { get; set; } = [];
}

public enum WebIngestCandidateDiscoveryKind
{
    SearchResult,
    DirectUrl,
    PageLink,
}

public enum WebIngestCandidateStatus
{
    Discovered,
    Read,
    Staged,
    Queued,
    Failed,
    Skipped,
}
