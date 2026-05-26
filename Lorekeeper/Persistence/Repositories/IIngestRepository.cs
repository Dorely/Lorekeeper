using Lorekeeper.Ingest;
using Lorekeeper.Models;

namespace Lorekeeper.Persistence.Repositories;

public interface IIngestRepository
{
    Task<List<IngestJob>> ListJobsByProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<List<IngestJobListItem>> ListJobSummariesByProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<IngestJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<IngestJob?> GetJobDetailAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<IngestJobDetailView?> GetJobDetailViewAsync(Guid jobId, int eventLimit = 20, CancellationToken cancellationToken = default);
    Task<IngestSource?> GetSourceAsync(Guid sourceId, CancellationToken cancellationToken = default);
    Task<IngestSourceChunk?> GetSourceChunkAsync(Guid sourceChunkId, CancellationToken cancellationToken = default);
    Task<IngestSourceChunkExcerpt?> GetSourceChunkExcerptAsync(Guid sourceChunkId, int maxChars = 8_000, CancellationToken cancellationToken = default);
    Task<List<IngestSource>> ListSourcesByProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<List<IngestSourcePage>> ListSourcePagesAsync(Guid sourceId, CancellationToken cancellationToken = default);
    Task<List<IngestSourceBlock>> ListSourceBlocksAsync(Guid sourceId, CancellationToken cancellationToken = default);
    Task<IngestReportItem?> GetReportItemAsync(Guid reportItemId, CancellationToken cancellationToken = default);
    Task<List<IngestSourceChunk>> ListSourceChunksAsync(Guid sourceId, CancellationToken cancellationToken = default);
    Task<List<IngestVectorFragment>> ListVectorFragmentsAsync(Guid sourceId, CancellationToken cancellationToken = default);
    Task<List<IngestReportItem>> ListReportItemsAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<List<IngestReportItemView>> ListReportItemViewsAsync(Guid jobId, Guid? sourceChunkId = null, CancellationToken cancellationToken = default);
    Task<List<IngestJob>> ListQueuedJobsAsync(CancellationToken cancellationToken = default);
    Task<List<IngestJob>> ListInterruptedJobsAsync(CancellationToken cancellationToken = default);
    Task AddSourceAsync(IngestSource source, CancellationToken cancellationToken = default);
    Task AddSourcePageAsync(IngestSourcePage sourcePage, CancellationToken cancellationToken = default);
    Task AddSourceBlockAsync(IngestSourceBlock sourceBlock, CancellationToken cancellationToken = default);
    Task AddJobAsync(IngestJob job, CancellationToken cancellationToken = default);
    Task AddSourceChunkAsync(IngestSourceChunk sourceChunk, CancellationToken cancellationToken = default);
    Task AddVectorFragmentAsync(IngestVectorFragment vectorFragment, CancellationToken cancellationToken = default);
    Task AddJobChunkAsync(IngestJobChunk jobChunk, CancellationToken cancellationToken = default);
    Task AddReportItemAsync(IngestReportItem item, CancellationToken cancellationToken = default);
    Task AddEventAsync(IngestJobEvent jobEvent, CancellationToken cancellationToken = default);
    void UpdateSource(IngestSource source);
    void UpdateSourceChunk(IngestSourceChunk sourceChunk);
    void RemoveVectorFragment(IngestVectorFragment vectorFragment);
    void UpdateJob(IngestJob job);
    void UpdateJobChunk(IngestJobChunk jobChunk);
    void UpdateReportItem(IngestReportItem item);
    void RemoveSource(IngestSource source);
    void RemoveJob(IngestJob job);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
