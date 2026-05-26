using Lorekeeper.Ingest;
using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public sealed class IngestRepository(AppDbContext db) : IIngestRepository
{
    public Task<List<IngestJob>> ListJobsByProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        db.IngestJobs
            .Include(job => job.Source)
            .Where(job => job.ProjectId == projectId)
            .OrderByDescending(job => job.CreatedAt)
            .ToListAsync(cancellationToken);

    public Task<List<IngestJobListItem>> ListJobSummariesByProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        db.IngestJobs
            .AsNoTracking()
            .Where(job => job.ProjectId == projectId)
            .OrderByDescending(job => job.CreatedAt)
            .Select(job => new IngestJobListItem(
                job.Id,
                job.ProjectId,
                job.SourceId,
                job.Source.Title,
                job.Source.SourceKind,
                job.Status,
                job.TotalSourceChunks,
                job.CompletedSourceChunks,
                job.CreatedEntityCount,
                job.CreatedRelationshipCount,
                job.CurrentMessage,
                job.ErrorMessage,
                job.ProviderId,
                job.ModelName,
                job.CreatedAt,
                job.UpdatedAt))
            .ToListAsync(cancellationToken);

    public Task<IngestJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        db.IngestJobs.FirstOrDefaultAsync(job => job.Id == jobId, cancellationToken);

    public Task<IngestJob?> GetJobDetailAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        db.IngestJobs
            .Include(job => job.Source)
            .Include(job => job.Chunks.OrderBy(chunk => chunk.SourceChunkIndex))
                .ThenInclude(chunk => chunk.SourceChunk)
            .Include(job => job.ReportItems.OrderBy(item => item.CreatedAt))
            .Include(job => job.Events.OrderByDescending(item => item.CreatedAt))
            .FirstOrDefaultAsync(job => job.Id == jobId, cancellationToken);

    public async Task<IngestJobDetailView?> GetJobDetailViewAsync(Guid jobId, int eventLimit = 20, CancellationToken cancellationToken = default)
    {
        var job = await db.IngestJobs
            .AsNoTracking()
            .Where(job => job.Id == jobId)
            .Select(job => new
            {
                job.Id,
                job.ProjectId,
                job.SourceId,
                SourceTitle = job.Source.Title,
                SourceKind = job.Source.SourceKind,
                SourceDescription = job.Source.Description,
                job.Instructions,
                job.Status,
                job.TotalSourceChunks,
                job.CompletedSourceChunks,
                job.CreatedEntityCount,
                job.CreatedRelationshipCount,
                job.CurrentMessage,
                job.ErrorMessage,
                job.ProviderId,
                job.ModelName,
                job.EncodingName,
                job.CreatedAt,
                job.UpdatedAt,
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (job is null) return null;

        var chunks = await db.IngestJobChunks
            .AsNoTracking()
            .Where(chunk => chunk.JobId == jobId)
            .OrderBy(chunk => chunk.SourceChunkIndex)
            .Select(chunk => new IngestJobChunkProgress(
                chunk.Id,
                chunk.SourceChunkId,
                chunk.SourceChunkIndex,
                chunk.Status,
                chunk.Summary,
                chunk.ErrorMessage,
                chunk.CreatedEntityCount,
                chunk.CreatedRelationshipCount,
                chunk.SourceChunk.Title,
                chunk.SourceChunk.HeadingPath,
                chunk.SourceChunk.StartChar,
                chunk.SourceChunk.EndChar,
                chunk.SourceChunk.EstimatedTokenCount,
                chunk.SourceChunk.TokenCountMethod,
                chunk.SourceChunk.TokenEncodingName,
                chunk.SourceChunk.TokenCountIsExact,
                chunk.SourceChunk.Summary,
                chunk.SourceChunk.AgentNotes))
            .ToListAsync(cancellationToken);

        var events = await db.IngestJobEvents
            .AsNoTracking()
            .Where(jobEvent => jobEvent.JobId == jobId && (jobEvent.Level == IngestJobEventLevel.Warning || jobEvent.Level == IngestJobEventLevel.Error))
            .OrderByDescending(jobEvent => jobEvent.CreatedAt)
            .Take(eventLimit)
            .Select(jobEvent => new IngestJobEventView(
                jobEvent.Id,
                jobEvent.Level,
                jobEvent.EventType,
                jobEvent.Message,
                jobEvent.PayloadJson,
                jobEvent.CreatedAt))
            .ToListAsync(cancellationToken);

        return new IngestJobDetailView(
            job.Id,
            job.ProjectId,
            job.SourceId,
            job.SourceTitle,
            job.SourceKind,
            job.SourceDescription,
            job.Instructions,
            job.Status,
            job.TotalSourceChunks,
            job.CompletedSourceChunks,
            job.CreatedEntityCount,
            job.CreatedRelationshipCount,
            job.CurrentMessage,
            job.ErrorMessage,
            job.ProviderId,
            job.ModelName,
            job.EncodingName,
            job.CreatedAt,
            job.UpdatedAt,
            chunks,
            events);
    }

    public Task<IngestSource?> GetSourceAsync(Guid sourceId, CancellationToken cancellationToken = default) =>
        db.IngestSources.FirstOrDefaultAsync(source => source.Id == sourceId, cancellationToken);

    public Task<IngestSourceChunk?> GetSourceChunkAsync(Guid sourceChunkId, CancellationToken cancellationToken = default) =>
        db.IngestSourceChunks.FirstOrDefaultAsync(chunk => chunk.Id == sourceChunkId, cancellationToken);

    public Task<List<IngestSource>> ListSourcesByProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        db.IngestSources
            .Where(source => source.ProjectId == projectId)
            .OrderBy(source => source.Title)
            .ThenBy(source => source.Id)
            .ToListAsync(cancellationToken);

    public Task<List<IngestSourcePage>> ListSourcePagesAsync(Guid sourceId, CancellationToken cancellationToken = default) =>
        db.IngestSourcePages
            .Where(page => page.SourceId == sourceId)
            .OrderBy(page => page.PageNumber)
            .ToListAsync(cancellationToken);

    public Task<List<IngestSourceBlock>> ListSourceBlocksAsync(Guid sourceId, CancellationToken cancellationToken = default) =>
        db.IngestSourceBlocks
            .Where(block => block.SourceId == sourceId)
            .OrderBy(block => block.Index)
            .ToListAsync(cancellationToken);

    public async Task<IngestSourceChunkExcerpt?> GetSourceChunkExcerptAsync(Guid sourceChunkId, int maxChars = 8_000, CancellationToken cancellationToken = default)
    {
        maxChars = Math.Clamp(maxChars, 1, 100_000);
        var excerpt = await db.IngestSourceChunks
            .AsNoTracking()
            .Where(chunk => chunk.Id == sourceChunkId)
            .Select(chunk => new
            {
                chunk.Id,
                Text = chunk.Source.SourceText.Substring(
                    chunk.StartChar,
                    chunk.EndChar - chunk.StartChar > maxChars ? maxChars : chunk.EndChar - chunk.StartChar),
                IsTruncated = chunk.EndChar - chunk.StartChar > maxChars,
            })
            .FirstOrDefaultAsync(cancellationToken);

        return excerpt is null
            ? null
            : new IngestSourceChunkExcerpt(excerpt.Id, excerpt.Text, excerpt.IsTruncated);
    }

    public Task<IngestReportItem?> GetReportItemAsync(Guid reportItemId, CancellationToken cancellationToken = default) =>
        db.IngestReportItems
            .Include(item => item.Job)
            .Include(item => item.SourceChunk)
            .FirstOrDefaultAsync(item => item.Id == reportItemId, cancellationToken);

    public Task<List<IngestSourceChunk>> ListSourceChunksAsync(Guid sourceId, CancellationToken cancellationToken = default) =>
        db.IngestSourceChunks
            .Where(chunk => chunk.SourceId == sourceId)
            .OrderBy(chunk => chunk.Index)
            .ToListAsync(cancellationToken);

    public Task<List<IngestVectorFragment>> ListVectorFragmentsAsync(Guid sourceId, CancellationToken cancellationToken = default) =>
        db.IngestVectorFragments
            .Where(fragment => fragment.SourceId == sourceId)
            .OrderBy(fragment => fragment.Index)
            .ToListAsync(cancellationToken);

    public Task<List<IngestReportItem>> ListReportItemsAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        db.IngestReportItems
            .Where(item => item.JobId == jobId)
            .OrderBy(item => item.CreatedAt)
            .ToListAsync(cancellationToken);

    public Task<List<IngestReportItemView>> ListReportItemViewsAsync(Guid jobId, Guid? sourceChunkId = null, CancellationToken cancellationToken = default) =>
        db.IngestReportItems
            .AsNoTracking()
            .Where(item => item.JobId == jobId && item.Status != IngestReportItemStatus.Deleted && (sourceChunkId == null || item.SourceChunkId == sourceChunkId))
            .OrderBy(item => item.CreatedAt)
            .Select(item => new IngestReportItemView(
                item.Id,
                item.SourceChunkId,
                item.Kind,
                item.Status,
                item.Title,
                item.Summary,
                item.Notes,
                item.Evidence,
                item.ResourceType,
                item.EntityId,
                item.GraphNodeId,
                item.GraphEdgeId,
                item.PayloadJson,
                item.CreatedAt,
                item.UpdatedAt))
            .ToListAsync(cancellationToken);

    public Task<List<IngestJob>> ListQueuedJobsAsync(CancellationToken cancellationToken = default) =>
        db.IngestJobs
            .Where(job => job.Status == IngestJobStatus.Queued)
            .OrderBy(job => job.CreatedAt)
            .ToListAsync(cancellationToken);

    public Task<List<IngestJob>> ListInterruptedJobsAsync(CancellationToken cancellationToken = default) =>
        db.IngestJobs
            .Where(job => job.Status == IngestJobStatus.Running || job.Status == IngestJobStatus.StopRequested)
            .OrderBy(job => job.UpdatedAt)
            .ToListAsync(cancellationToken);

    public async Task AddSourceAsync(IngestSource source, CancellationToken cancellationToken = default) =>
        await db.IngestSources.AddAsync(source, cancellationToken);

    public async Task AddSourcePageAsync(IngestSourcePage sourcePage, CancellationToken cancellationToken = default) =>
        await db.IngestSourcePages.AddAsync(sourcePage, cancellationToken);

    public async Task AddSourceBlockAsync(IngestSourceBlock sourceBlock, CancellationToken cancellationToken = default) =>
        await db.IngestSourceBlocks.AddAsync(sourceBlock, cancellationToken);

    public async Task AddJobAsync(IngestJob job, CancellationToken cancellationToken = default) =>
        await db.IngestJobs.AddAsync(job, cancellationToken);

    public async Task AddSourceChunkAsync(IngestSourceChunk sourceChunk, CancellationToken cancellationToken = default) =>
        await db.IngestSourceChunks.AddAsync(sourceChunk, cancellationToken);

    public async Task AddVectorFragmentAsync(IngestVectorFragment vectorFragment, CancellationToken cancellationToken = default) =>
        await db.IngestVectorFragments.AddAsync(vectorFragment, cancellationToken);

    public async Task AddJobChunkAsync(IngestJobChunk jobChunk, CancellationToken cancellationToken = default) =>
        await db.IngestJobChunks.AddAsync(jobChunk, cancellationToken);

    public async Task AddReportItemAsync(IngestReportItem item, CancellationToken cancellationToken = default) =>
        await db.IngestReportItems.AddAsync(item, cancellationToken);

    public async Task AddEventAsync(IngestJobEvent jobEvent, CancellationToken cancellationToken = default) =>
        await db.IngestJobEvents.AddAsync(jobEvent, cancellationToken);

    public void UpdateSource(IngestSource source) => db.IngestSources.Update(source);

    public void UpdateSourceChunk(IngestSourceChunk sourceChunk) => db.IngestSourceChunks.Update(sourceChunk);

    public void RemoveVectorFragment(IngestVectorFragment vectorFragment) => db.IngestVectorFragments.Remove(vectorFragment);

    public void UpdateJob(IngestJob job) => db.IngestJobs.Update(job);

    public void UpdateJobChunk(IngestJobChunk jobChunk) => db.IngestJobChunks.Update(jobChunk);

    public void UpdateReportItem(IngestReportItem item) => db.IngestReportItems.Update(item);

    public void RemoveSource(IngestSource source) => db.IngestSources.Remove(source);

    public void RemoveJob(IngestJob job) => db.IngestJobs.Remove(job);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        db.SaveChangesAsync(cancellationToken);
}
