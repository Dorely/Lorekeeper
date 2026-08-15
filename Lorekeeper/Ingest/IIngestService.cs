using Lorekeeper.Models;

namespace Lorekeeper.Ingest;

public interface IIngestService
{
    Task<IReadOnlyList<IngestJob>> ListJobsAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IngestJobListItem>> ListJobSummariesAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<IngestJob?> GetJobDetailAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<IngestJobDetailView?> GetJobViewAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<IngestSource?> GetSourceAsync(Guid sourceId, CancellationToken cancellationToken = default);
    Task<IngestSourceChunk?> GetSourceChunkAsync(Guid sourceChunkId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IngestSourceChunk>> ListSourceChunksAsync(Guid sourceId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IngestReportItemView>> ListReportItemViewsAsync(Guid jobId, Guid? sourceChunkId = null, CancellationToken cancellationToken = default);
    Task<IngestSourceChunkExcerpt?> GetSourceChunkExcerptAsync(Guid sourceChunkId, int maxChars = 8_000, CancellationToken cancellationToken = default);
    Task<IngestJob> CreateJobAsync(Guid projectId, IngestCreateJobRequest request, CancellationToken cancellationToken = default);
    Task RequestStopAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task ResumeAsync(Guid jobId, IngestResumeRequest? request = null, CancellationToken cancellationToken = default);
    Task RestartAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task DeleteJobAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<IngestStagingRecord> UpdateReportItemAsync(Guid reportItemId, IngestReportItemUpdateRequest request, CancellationToken cancellationToken = default);
    Task DeleteReportItemAsync(Guid reportItemId, CancellationToken cancellationToken = default);
}

public sealed record IngestReportItemUpdateRequest(
    string Title,
    string Summary,
    string Notes,
    string? ResourceType = null);

public sealed record IngestResumeRequest(int? ProviderId = null);
