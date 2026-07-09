using Lorekeeper.Models;

namespace Lorekeeper.Research;

public sealed record ResearchActivity(
    IReadOnlyList<ResearchEntityActivityItem> Entities,
    IReadOnlyList<ResearchSourceActivityItem> Sources);

public sealed record ResearchEntityActivityItem(
    Guid EntityId,
    string Type,
    string Name,
    ResearchEntityActivityState State,
    bool Exists,
    bool HasPendingChange,
    Guid? PendingBatchId,
    Guid? PendingChangeId,
    string? PendingSummary,
    string Preview,
    DateTime LastTouchedAt,
    IReadOnlyDictionary<string, string?> Properties);

public enum ResearchEntityActivityState
{
    Read,
    Created,
    Updated,
    Linked,
    PendingCreated,
    PendingUpdated,
    PendingLinked,
}

public sealed record ResearchSourceActivityItem(
    Guid Id,
    WebIngestCandidateStatus Status,
    string Title,
    string Url,
    string SearchQuery,
    string SourceProviderName,
    string Diagnostics,
    DateTime? FetchedAt,
    DateTime LastTouchedAt);

public sealed record ResearchSourceDetail(
    Guid Id,
    WebIngestCandidateStatus Status,
    string Title,
    string Url,
    string FinalUrl,
    string CanonicalUrl,
    string DisplayUrl,
    string SearchQuery,
    string SourceProviderName,
    string ContentType,
    string ContentHash,
    string Excerpt,
    string Diagnostics,
    DateTime? FetchedAt,
    DateTime UpdatedAt,
    string ExtractedText,
    IReadOnlyList<ResearchSourceLink> Links,
    IReadOnlyList<WebPageImage> DiscoveredImages,
    IReadOnlyList<EntityVisuals.SourceVisualCandidateView> Visuals);

public sealed record ResearchSourceLink(string Url, string Text);
