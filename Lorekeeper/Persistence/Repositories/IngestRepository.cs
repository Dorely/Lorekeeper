using System.Text.Json;
using Lorekeeper.Ingest;
using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public sealed class IngestRepository(AppDatabaseReadOperation operation) : IIngestRepository
{
    public Task<List<IngestJob>> ListJobsByProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        operation.Db.IngestJobs
            .Include(job => job.Source)
            .Where(job => job.ProjectId == projectId)
            .OrderByDescending(job => job.CreatedAt)
            .ToListAsync(cancellationToken);

    public Task<List<IngestJobListItem>> ListJobSummariesByProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        operation.Db.IngestJobs
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
                job.UpdatedAt,
                job.Mode))
            .ToListAsync(cancellationToken);

    public Task<IngestJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        operation.Db.IngestJobs.FirstOrDefaultAsync(job => job.Id == jobId, cancellationToken);

    public Task<IngestJob?> GetJobDetailAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        operation.Db.IngestJobs
            .Include(job => job.Source)
            .Include(job => job.Chunks.OrderBy(chunk => chunk.SourceChunkIndex))
                .ThenInclude(chunk => chunk.SourceChunk)
            .Include(job => job.ReportItems.OrderBy(item => item.CreatedAt))
            .Include(job => job.StagingRecords.OrderBy(item => item.CreatedAt))
            .Include(job => job.Events.OrderByDescending(item => item.CreatedAt))
            .AsSplitQuery()
            .FirstOrDefaultAsync(job => job.Id == jobId, cancellationToken);

    public Task<IngestJob?> GetJobProcessorDetailAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        operation.Db.IngestJobs
            .Include(job => job.Source)
            .Include(job => job.Chunks.OrderBy(chunk => chunk.SourceChunkIndex))
                .ThenInclude(chunk => chunk.SourceChunk)
            .AsSplitQuery()
            .FirstOrDefaultAsync(job => job.Id == jobId, cancellationToken);

    public Task<IngestJob?> GetJobResumeDetailAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        operation.Db.IngestJobs
            .Include(job => job.Chunks.OrderBy(chunk => chunk.SourceChunkIndex))
                .ThenInclude(chunk => chunk.SourceChunk)
            .AsSplitQuery()
            .FirstOrDefaultAsync(job => job.Id == jobId, cancellationToken);

    public async Task<IngestJobDetailView?> GetJobDetailViewAsync(Guid jobId, int eventLimit = 20, CancellationToken cancellationToken = default)
    {
        var job = await operation.Db.IngestJobs
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
                job.Mode,
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

        var chunks = await operation.Db.IngestJobChunks
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
                chunk.LlmTokenCount,
                chunk.LlmTokenCountIsExact,
                chunk.LlmTokenCountMethod,
                chunk.LlmTokenEncodingName,
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

        var events = await operation.Db.IngestJobEvents
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

        var finalizationRecords = await operation.Db.IngestStagingRecords
            .AsNoTracking()
            .Where(item => item.JobId == jobId && item.Status != IngestStagingRecordStatus.Deleted)
            .Select(item => new FinalizationStagingRecord(
                item.Id,
                item.Kind,
                item.Status,
                item.EntityId,
                item.GraphNodeId))
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
            BuildFinalizationProgress(
                job.Status,
                job.TotalSourceChunks,
                job.CompletedSourceChunks,
                job.CurrentMessage,
                finalizationRecords),
            events,
            job.Mode);
    }

    public Task<IngestSource?> GetSourceAsync(Guid sourceId, CancellationToken cancellationToken = default) =>
        operation.Db.IngestSources.FirstOrDefaultAsync(source => source.Id == sourceId, cancellationToken);

    public Task<SourceExtractionVersion?> GetActiveExtractionAsync(Guid sourceId, CancellationToken cancellationToken = default) =>
        operation.Db.SourceExtractionVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(extraction => extraction.SourceId == sourceId
                && extraction.Id == extraction.Source.ActiveExtractionVersionId
                && (extraction.Status == SourceExtractionStatus.Ready
                    || extraction.Status == SourceExtractionStatus.LegacyImmutable),
                cancellationToken);

    public Task<IngestSourceChunk?> GetSourceChunkAsync(Guid sourceChunkId, CancellationToken cancellationToken = default) =>
        operation.Db.IngestSourceChunks.FirstOrDefaultAsync(chunk =>
            chunk.Id == sourceChunkId
            && chunk.SourceExtractionVersionId == chunk.Source.ActiveExtractionVersionId,
            cancellationToken);

    public Task<List<IngestSource>> ListSourcesByProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        operation.Db.IngestSources
            .Where(source => source.ProjectId == projectId)
            .OrderBy(source => source.Title)
            .ThenBy(source => source.Id)
            .ToListAsync(cancellationToken);

    public Task<List<IngestSourcePage>> ListSourcePagesAsync(Guid sourceId, CancellationToken cancellationToken = default) =>
        operation.Db.IngestSourcePages
            .Where(page => page.SourceId == sourceId
                && page.SourceExtractionVersionId == page.Source.ActiveExtractionVersionId)
            .OrderBy(page => page.PageNumber)
            .ToListAsync(cancellationToken);

    public Task<List<IngestSourceBlock>> ListSourceBlocksAsync(Guid sourceId, CancellationToken cancellationToken = default) =>
        operation.Db.IngestSourceBlocks
            .Where(block => block.SourceId == sourceId
                && block.SourceExtractionVersionId == block.Source.ActiveExtractionVersionId)
            .OrderBy(block => block.Index)
            .ToListAsync(cancellationToken);

    public Task<IngestStagingRecord?> GetStagingRecordAsync(Guid stagingRecordId, CancellationToken cancellationToken = default) =>
        operation.Db.IngestStagingRecords
            .Include(item => item.Job)
            .Include(item => item.Source)
            .Include(item => item.SourceChunk)
            .FirstOrDefaultAsync(item => item.Id == stagingRecordId, cancellationToken);

    public async Task<IngestSourceChunkExcerpt?> GetSourceChunkExcerptAsync(Guid sourceChunkId, int maxChars = 8_000, CancellationToken cancellationToken = default)
    {
        maxChars = Math.Clamp(maxChars, 1, 100_000);
        var excerpt = await operation.Db.IngestSourceChunks
            .AsNoTracking()
            .Where(chunk => chunk.Id == sourceChunkId
                && chunk.SourceExtractionVersionId == chunk.Source.ActiveExtractionVersionId)
            .Select(chunk => new
            {
                chunk.Id,
                Text = chunk.SourceExtractionVersion.NormalizedText.Substring(
                    chunk.StartChar,
                    Math.Min(maxChars, Math.Min(chunk.EndChar, chunk.SourceExtractionVersion.NormalizedText.Length) - chunk.StartChar)),
                IsTruncated = chunk.EndChar - chunk.StartChar > maxChars,
            })
            .FirstOrDefaultAsync(cancellationToken);

        return excerpt is null
            ? null
            : new IngestSourceChunkExcerpt(excerpt.Id, excerpt.Text, excerpt.IsTruncated);
    }

    public Task<IngestReportItem?> GetReportItemAsync(Guid reportItemId, CancellationToken cancellationToken = default) =>
        operation.Db.IngestReportItems
            .Include(item => item.Job)
            .Include(item => item.SourceChunk)
            .FirstOrDefaultAsync(item => item.Id == reportItemId, cancellationToken);

    public Task<List<IngestSourceChunk>> ListSourceChunksAsync(Guid sourceId, CancellationToken cancellationToken = default) =>
        operation.Db.IngestSourceChunks
            .Where(chunk => chunk.SourceId == sourceId
                && chunk.SourceExtractionVersionId == chunk.Source.ActiveExtractionVersionId)
            .OrderBy(chunk => chunk.Index)
            .ToListAsync(cancellationToken);

    public Task<List<IngestVectorFragment>> ListVectorFragmentsAsync(Guid sourceId, CancellationToken cancellationToken = default) =>
        operation.Db.IngestVectorFragments
            .Where(fragment => fragment.SourceId == sourceId)
            .OrderBy(fragment => fragment.Index)
            .ToListAsync(cancellationToken);

    public Task<List<IngestStagingRecord>> ListStagingRecordsAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        operation.Db.IngestStagingRecords
            .Where(item => item.JobId == jobId)
            .OrderBy(item => item.CreatedAt)
            .ToListAsync(cancellationToken);

    public Task<List<IngestStagingRecord>> ListStagingRecordsBySourceAsync(Guid sourceId, CancellationToken cancellationToken = default) =>
        operation.Db.IngestStagingRecords
            .Where(item => item.SourceId == sourceId)
            .OrderBy(item => item.CreatedAt)
            .ToListAsync(cancellationToken);

    public Task<List<IngestReportItem>> ListReportItemsAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        operation.Db.IngestReportItems
            .Where(item => item.JobId == jobId)
            .OrderBy(item => item.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task<List<IngestReportItemView>> ListReportItemViewsAsync(Guid jobId, Guid? sourceChunkId = null, CancellationToken cancellationToken = default)
    {
        var query = operation.Db.IngestStagingRecords
            .AsNoTracking()
            .Where(item => item.JobId == jobId && item.Status != IngestStagingRecordStatus.Deleted);

        if (sourceChunkId is Guid selectedSourceChunkId)
            query = query.Where(item => item.SourceChunkId == selectedSourceChunkId || item.SourceChunkId == null);

        var items = await query
            .Include(item => item.SourceChunk)
            .OrderBy(item => item.CreatedAt)
            .ToListAsync(cancellationToken);

        return items
            .Where(item => sourceChunkId is null || ReportItemBelongsToChunk(item, sourceChunkId.Value))
            .Select(item => new IngestReportItemView(
                item.Id,
                item.SourceChunkId,
                item.SourceChunkIndex,
                item.SourceChunk?.Title,
                item.Kind,
                item.Status,
                item.Title,
                item.Summary,
                item.Notes,
                item.Kind == IngestStagingRecordKind.Relationship ? item.EdgeType : item.EntityType,
                item.EntityId,
                item.FromEntityId,
                item.ToEntityId,
                item.GraphNodeId,
                item.GraphEdgeId,
                item.PayloadJson,
                item.CreatedAt,
                item.UpdatedAt))
            .ToList();
    }

    public Task<List<IngestJob>> ListQueuedJobsAsync(CancellationToken cancellationToken = default) =>
        operation.Db.IngestJobs
            .Where(job => job.Status == IngestJobStatus.Queued)
            .OrderBy(job => job.CreatedAt)
            .ToListAsync(cancellationToken);

    public Task<List<IngestJob>> ListInterruptedJobsAsync(CancellationToken cancellationToken = default) =>
        operation.Db.IngestJobs
            .Include(job => job.Chunks)
            .Where(job => job.Status == IngestJobStatus.Running || job.Status == IngestJobStatus.StopRequested)
            .OrderBy(job => job.UpdatedAt)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

    public async Task AddSourceAsync(IngestSource source, CancellationToken cancellationToken = default) =>
        await operation.Db.IngestSources.AddAsync(source, cancellationToken);

    public async Task AddSourcePageAsync(IngestSourcePage sourcePage, CancellationToken cancellationToken = default) =>
        await operation.Db.IngestSourcePages.AddAsync(sourcePage, cancellationToken);

    public async Task AddSourceBlockAsync(IngestSourceBlock sourceBlock, CancellationToken cancellationToken = default) =>
        await operation.Db.IngestSourceBlocks.AddAsync(sourceBlock, cancellationToken);

    public async Task AddJobAsync(IngestJob job, CancellationToken cancellationToken = default) =>
        await operation.Db.IngestJobs.AddAsync(job, cancellationToken);

    public async Task AddSourceChunkAsync(IngestSourceChunk sourceChunk, CancellationToken cancellationToken = default) =>
        await operation.Db.IngestSourceChunks.AddAsync(sourceChunk, cancellationToken);

    public async Task AddVectorFragmentAsync(IngestVectorFragment vectorFragment, CancellationToken cancellationToken = default) =>
        await operation.Db.IngestVectorFragments.AddAsync(vectorFragment, cancellationToken);

    public async Task AddJobChunkAsync(IngestJobChunk jobChunk, CancellationToken cancellationToken = default) =>
        await operation.Db.IngestJobChunks.AddAsync(jobChunk, cancellationToken);

    public async Task AddStagingRecordAsync(IngestStagingRecord item, CancellationToken cancellationToken = default) =>
        await operation.Db.IngestStagingRecords.AddAsync(item, cancellationToken);

    public async Task AddReportItemAsync(IngestReportItem item, CancellationToken cancellationToken = default) =>
        await operation.Db.IngestReportItems.AddAsync(item, cancellationToken);

    public async Task AddEventAsync(IngestJobEvent jobEvent, CancellationToken cancellationToken = default) =>
        await operation.Db.IngestJobEvents.AddAsync(jobEvent, cancellationToken);

    public void UpdateSource(IngestSource source) => MarkModified(source);

    public void UpdateSourceChunk(IngestSourceChunk sourceChunk) => MarkModified(sourceChunk);

    public void RemoveVectorFragment(IngestVectorFragment vectorFragment) => operation.Db.MarkDeleted(vectorFragment);

    public void UpdateJob(IngestJob job) => MarkModified(job);

    public void UpdateJobChunk(IngestJobChunk jobChunk) => MarkModified(jobChunk);

    public void UpdateStagingRecord(IngestStagingRecord item) => MarkModified(item);

    public void UpdateReportItem(IngestReportItem item) => MarkModified(item);

    public void RemoveSource(IngestSource source) => operation.Db.MarkDeleted(source);

    public void RemoveJob(IngestJob job) => operation.Db.MarkDeleted(job);

    private void MarkModified<TEntity>(TEntity entity)
        where TEntity : class
    {
        var entry = operation.Db.Entry(entity);
        if (entry.State == EntityState.Detached)
            entry.State = EntityState.Modified;
    }

    private static bool ReportItemBelongsToChunk(IngestStagingRecord item, Guid sourceChunkId)
    {
        if (item.SourceChunkId == sourceChunkId) return true;
        if (item.SourceChunkId is not null) return false;

        var chunkKey = sourceChunkId.ToString("N");
        if (string.IsNullOrWhiteSpace(item.PayloadJson) || item.PayloadJson == "{}") return false;

        try
        {
            using var document = JsonDocument.Parse(item.PayloadJson);
            var root = document.RootElement;
            if (root.TryGetProperty("sourceChunkIds", out var sourceChunkIds)
                && sourceChunkIds.ValueKind == JsonValueKind.Array
                && sourceChunkIds.EnumerateArray().Any(value =>
                    string.Equals(value.GetString(), chunkKey, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (root.TryGetProperty("sourceChunkId", out var sourceChunkIdProperty)
                && sourceChunkIdProperty.ValueKind == JsonValueKind.String
                && string.Equals(sourceChunkIdProperty.GetString(), chunkKey, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        catch (JsonException) { }

        return false;
    }

    private static IngestFinalizationProgressView BuildFinalizationProgress(
        IngestJobStatus jobStatus,
        int totalSourceChunks,
        int completedSourceChunks,
        string? currentMessage,
        IReadOnlyList<FinalizationStagingRecord> records)
    {
        var entityGroups = records
            .Where(item => item.Kind == IngestStagingRecordKind.Entity)
            .GroupBy(EntityFinalizationKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => EntityFinalizationStatus(group.Select(item => item.Status)))
            .ToList();

        var entityActive = entityGroups.Count(status => status == IngestStagingRecordStatus.Active);
        var entityFailed = entityGroups.Count(status => status == IngestStagingRecordStatus.Failed);
        var entityFinalized = entityGroups.Count(status => status == IngestStagingRecordStatus.Finalized);

        var relationshipRecords = records.Where(item => item.Kind == IngestStagingRecordKind.Relationship).ToList();
        var sourceChunkNoteRecords = records.Where(item => item.Kind == IngestStagingRecordKind.SourceChunkNote).ToList();

        var allChunksCompleted = totalSourceChunks > 0 && completedSourceChunks >= totalSourceChunks;
        var phase = jobStatus switch
        {
            IngestJobStatus.Failed => IngestFinalizationPhase.Failed,
            _ when !allChunksCompleted => IngestFinalizationPhase.Pending,
            _ when entityActive > 0 => IngestFinalizationPhase.ReviewingEntities,
            _ when relationshipRecords.Any(item => item.Status == IngestStagingRecordStatus.Active)
                || sourceChunkNoteRecords.Any(item => item.Status == IngestStagingRecordStatus.Active) => IngestFinalizationPhase.BuildingRelationships,
            _ when records.Count > 0 || jobStatus == IngestJobStatus.Completed => IngestFinalizationPhase.Completed,
            _ => IngestFinalizationPhase.Pending,
        };

        return new IngestFinalizationProgressView(
            phase,
            entityGroups.Count,
            entityFinalized,
            entityActive,
            entityFailed,
            relationshipRecords.Count,
            relationshipRecords.Count(item => item.Status == IngestStagingRecordStatus.Active),
            relationshipRecords.Count(item => item.Status == IngestStagingRecordStatus.Finalized),
            relationshipRecords.Count(item => item.Status == IngestStagingRecordStatus.Failed),
            sourceChunkNoteRecords.Count,
            sourceChunkNoteRecords.Count(item => item.Status == IngestStagingRecordStatus.Active),
            sourceChunkNoteRecords.Count(item => item.Status == IngestStagingRecordStatus.Finalized),
            sourceChunkNoteRecords.Count(item => item.Status == IngestStagingRecordStatus.Failed),
            currentMessage ?? string.Empty);
    }

    private static string EntityFinalizationKey(FinalizationStagingRecord item) =>
        item.EntityId?.ToString("N") ?? item.GraphNodeId?.ToString() ?? item.Id.ToString("N");

    private static IngestStagingRecordStatus EntityFinalizationStatus(IEnumerable<IngestStagingRecordStatus> statuses)
    {
        var statusList = statuses.ToList();
        if (statusList.Any(status => status == IngestStagingRecordStatus.Active))
            return IngestStagingRecordStatus.Active;
        if (statusList.Any(status => status == IngestStagingRecordStatus.Failed))
            return IngestStagingRecordStatus.Failed;
        return IngestStagingRecordStatus.Finalized;
    }

    private sealed record FinalizationStagingRecord(
        Guid Id,
        IngestStagingRecordKind Kind,
        IngestStagingRecordStatus Status,
        Guid? EntityId,
        long? GraphNodeId);
}
