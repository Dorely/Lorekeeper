using Lorekeeper.Ingest;
using Lorekeeper.Models;
using Lorekeeper.Search;

namespace Lorekeeper.Research;

public interface IWebIngestCandidateService
{
    Task<IReadOnlyList<WebIngestCandidateView>> ListAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WebIngestCandidateView>> ListResearchAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WebIngestCandidateView>> ListStagedAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WebIngestCandidateView>> ListStagedAsync(Guid projectId, Guid? researchConversationId, CancellationToken cancellationToken = default);
    Task<WebIngestCandidate> CreateFromSearchResultAsync(
        Guid projectId,
        Guid? conversationId,
        int? searchProviderId,
        string providerName,
        string query,
        WebSearchResult result,
        CancellationToken cancellationToken = default);
    Task<WebIngestCandidateReadResult> ReadCandidateAsync(Guid candidateId, CancellationToken cancellationToken = default);
    Task<WebIngestCandidateReadResult> ReadUrlAsync(
        Guid projectId,
        Guid? conversationId,
        string url,
        WebIngestCandidateDiscoveryKind discoveryKind = WebIngestCandidateDiscoveryKind.DirectUrl,
        string? searchQuery = null,
        int? searchRank = null,
        string? parentUrl = null,
        int crawlDepth = 0,
        CancellationToken cancellationToken = default);
    Task<WebIngestCandidate> StageAsync(Guid candidateId, string rationale, CancellationToken cancellationToken = default);
    Task<WebIngestCandidate> UnstageAsync(Guid candidateId, CancellationToken cancellationToken = default);
    Task<IngestJob> QueueAsync(Guid candidateId, int? providerId, string? instructions, CancellationToken cancellationToken = default);
    Task<IngestJob> QueueBatchAsync(Guid projectId, IReadOnlyCollection<Guid> candidateIds, int? providerId, string? instructions, string? title = null, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid candidateId, CancellationToken cancellationToken = default);
}