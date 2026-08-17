using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lorekeeper.Context;
using Lorekeeper.EntityVisuals;
using Lorekeeper.Knowledge;
using Lorekeeper.Llm;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Persistence.Repositories;

namespace Lorekeeper.Ingest;

public sealed class IngestService(
IAppDatabaseOperationFactory database, IIngestSourceStructureBuilder structureBuilder, IBookArtifactPreprocessor artifactPreprocessor, IEntityVisualExampleService entityVisualExamples, IIngestGraphSync graphSync, IIngestJobQueue queue, ILlmProviderService providers, IIngestJobNotifier notifier, IIngestGraphCleanup graphCleanup, IGraphStore graphStore, IVectorStore vectors, IContextIndexingService contextIndexing, ILogger<IngestService> logger) : IIngestService
{
    public async Task<IReadOnlyList<IngestJob>> ListJobsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var ingest = databaseOperation.Repositories.Ingest;
        await RecoverInactiveRunningJobsAsync(projectId, jobId: null, cancellationToken);
        return await ingest.ListJobsByProjectAsync(projectId, cancellationToken);
    }

    public async Task<IReadOnlyList<IngestJobListItem>> ListJobSummariesAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var ingest = databaseOperation.Repositories.Ingest;
        await RecoverInactiveRunningJobsAsync(projectId, jobId: null, cancellationToken);
        return await ingest.ListJobSummariesByProjectAsync(projectId, cancellationToken);
    }

    public async Task<IngestJob?> GetJobDetailAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var ingest = databaseOperation.Repositories.Ingest;
        await RecoverInactiveRunningJobsAsync(projectId: null, jobId, cancellationToken);
        return await ingest.GetJobDetailAsync(jobId, cancellationToken);
    }

    public async Task<IngestJobDetailView?> GetJobViewAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var ingest = databaseOperation.Repositories.Ingest;
        await RecoverInactiveRunningJobsAsync(projectId: null, jobId, cancellationToken);
        return await ingest.GetJobDetailViewAsync(jobId, cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<IngestReportItemView>> ListReportItemViewsAsync(Guid jobId, Guid? sourceChunkId = null, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var ingest = databaseOperation.Repositories.Ingest;
        return await ingest.ListReportItemViewsAsync(jobId, sourceChunkId, cancellationToken);
    }

    public async Task<IngestSource?> GetSourceAsync(Guid sourceId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        return await databaseOperation.Repositories.Ingest.GetSourceAsync(sourceId, cancellationToken);
    }

    public async Task<IngestSourceChunk?> GetSourceChunkAsync(Guid sourceChunkId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        return await databaseOperation.Repositories.Ingest.GetSourceChunkAsync(sourceChunkId, cancellationToken);
    }

    public async Task<IReadOnlyList<IngestSourceChunk>> ListSourceChunksAsync(
        Guid sourceId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        return await databaseOperation.Repositories.Ingest.ListSourceChunksAsync(sourceId, cancellationToken);
    }

    public async Task<IngestSourceChunkExcerpt?> GetSourceChunkExcerptAsync(Guid sourceChunkId, int maxChars = 8_000, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var ingest = databaseOperation.Repositories.Ingest;
        return await ingest.GetSourceChunkExcerptAsync(sourceChunkId, maxChars, cancellationToken);
    }
    public async Task<IngestJob> CreateJobAsync(Guid projectId, IngestCreateJobRequest request, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var ingest = databaseOperation.Repositories.Ingest;
        var projects = databaseOperation.Repositories.Projects;
        var project = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");

        var title = (request.Title ?? string.Empty).Trim();
        if (title.Length == 0) throw new ArgumentException("Source title is required.", nameof(request));

        var sourceText = request.SourceText ?? string.Empty;
        var sourceKind = request.SourceKind?.Trim() ?? string.Empty;
        var contentType = request.ContentType?.Trim() ?? string.Empty;
        var sourceMetadataJson = string.IsNullOrWhiteSpace(request.SourceMetadataJson) ? "{}" : request.SourceMetadataJson.Trim();
        IReadOnlyList<IngestSourcePageDraft> pageDrafts = [];
        IReadOnlyList<IngestSourceBlockDraft> blockDrafts = [];
        IReadOnlyList<IngestVisualCandidateDraft> visualDrafts = [];

        var editedTextIsAuthoritative = request.ArtifactBytes is { Length: > 0 }
            && IsEditableTextArtifact(request.ArtifactFileName)
            && !string.IsNullOrWhiteSpace(sourceText);
        if (request.ArtifactBytes is { Length: > 0 } artifactBytes && !editedTextIsAuthoritative)
        {
            var preprocessed = await artifactPreprocessor.PreprocessAsync(new BookArtifactPreprocessRequest(
                request.ArtifactFileName ?? title,
                request.ArtifactContentType,
                artifactBytes,
                request.ProviderId,
                request.ExtractionProfile,
                request.PdfOptions ?? new PdfArtifactIngestOptions()), cancellationToken);

            sourceText = preprocessed.SourceText;
            pageDrafts = preprocessed.Pages;
            blockDrafts = preprocessed.Blocks;
            visualDrafts = preprocessed.Visuals;
            if (string.IsNullOrWhiteSpace(sourceKind))
                sourceKind = preprocessed.SourceKind;
            if (string.IsNullOrWhiteSpace(contentType))
                contentType = preprocessed.ContentType;
            sourceMetadataJson = MergeSourceMetadataJson(sourceMetadataJson, preprocessed);
        }
        else
        {
            sourceMetadataJson = EnsureExtractionProfileMetadata(sourceMetadataJson, request.ExtractionProfile);
        }

        if (string.IsNullOrWhiteSpace(sourceText)) throw new ArgumentException("Source text is required.", nameof(request));

        var instructions = (request.UserInstructions ?? string.Empty).Trim();

        var provider = await ResolveProviderAsync(request.ProviderId, requireProvider: true, cancellationToken);

        var source = new IngestSource
        {
            ProjectId = projectId,
            Title = title,
            SourceKind = sourceKind,
            Description = request.Description?.Trim() ?? string.Empty,
            UserInstructions = instructions,
            SourceText = sourceText,
            SourceHash = ComputeHash(sourceText),
            SourceUrl = request.SourceUrl?.Trim() ?? string.Empty,
            FinalUrl = request.FinalUrl?.Trim() ?? string.Empty,
            CanonicalUrl = request.CanonicalUrl?.Trim() ?? string.Empty,
            FetchedAt = request.FetchedAt,
            ContentType = contentType,
            SourceMetadataJson = sourceMetadataJson,
            VectorIndexState = VectorIndexState.Stale,
        };

        await ingest.AddSourceAsync(source, cancellationToken);

        foreach (var visual in visualDrafts)
        {
            await entityVisualExamples.CreateCandidateAsync(new SourceVisualCandidateCreateRequest(
                projectId,
                SourceVisualCandidateKind.IngestArtifact,
                visual.FileName,
                visual.ContentType,
                visual.Data,
                visual.AltText,
                visual.Caption,
                SourceUrl: request.SourceUrl ?? string.Empty,
                Locator: visual.Locator,
                MetadataJson: visual.MetadataJson,
                IngestSourceId: source.Id,
                StartChar: visual.StartChar,
                EndChar: visual.EndChar), cancellationToken);
        }

        foreach (var pageDraft in pageDrafts)
        {
            await ingest.AddSourcePageAsync(new IngestSourcePage
            {
                Id = pageDraft.Id,
                SourceId = source.Id,
                PageNumber = pageDraft.PageNumber,
                Text = pageDraft.Text,
                StartChar = pageDraft.StartChar,
                EndChar = pageDraft.EndChar,
                ExtractionMethod = pageDraft.ExtractionMethod,
                Width = pageDraft.Width,
                Height = pageDraft.Height,
                ImageHash = pageDraft.ImageHash,
                RenderSettingsJson = pageDraft.RenderSettingsJson,
                VisionProviderId = pageDraft.VisionProviderId,
                VisionModelName = pageDraft.VisionModelName,
                Diagnostics = pageDraft.Diagnostics,
            }, cancellationToken);
        }

        foreach (var blockDraft in blockDrafts)
        {
            await ingest.AddSourceBlockAsync(new IngestSourceBlock
            {
                Id = blockDraft.Id,
                SourceId = source.Id,
                SourcePageId = blockDraft.SourcePageId,
                Index = blockDraft.Index,
                Kind = blockDraft.Kind,
                Title = blockDraft.Title,
                Locator = blockDraft.Locator,
                PageNumber = blockDraft.PageNumber,
                StartChar = blockDraft.StartChar,
                EndChar = blockDraft.EndChar,
                MetadataJson = blockDraft.MetadataJson,
            }, cancellationToken);
        }

        var drafts = structureBuilder.Build(new IngestSourceStructureRequest(
            sourceText,
            provider?.ModelId,
            request.EncodingName,
            request.SourceTextTargetTokens));

        var sourceChunks = drafts.Select(draft => new IngestSourceChunk
        {
            SourceId = source.Id,
            Index = draft.Index,
            Title = draft.Title,
            HeadingPath = draft.HeadingPath,
            StartChar = draft.StartChar,
            EndChar = draft.EndChar,
            EstimatedTokenCount = draft.TokenCount.TokenCount,
            TokenCountMethod = draft.TokenCount.Method,
            TokenEncodingName = draft.TokenCount.EncodingName,
            TokenCountIsExact = draft.TokenCount.IsExact,
        }).ToList();

        if (sourceChunks.Count == 0)
            throw new InvalidOperationException("Source text could not be split into source chunks.");

        foreach (var sourceChunk in sourceChunks)
            await ingest.AddSourceChunkAsync(sourceChunk, cancellationToken);

        var job = new IngestJob
        {
            ProjectId = projectId,
            SourceId = source.Id,
            Instructions = instructions,
            Status = IngestJobStatus.Queued,
            TotalSourceChunks = sourceChunks.Count,
            ProviderId = provider?.Id,
            ModelName = provider?.ModelId,
            EncodingName = request.EncodingName?.Trim(),
            CurrentMessage = "Queued.",
        };
        await ingest.AddJobAsync(job, cancellationToken);

        foreach (var sourceChunk in sourceChunks)
        {
            await ingest.AddJobChunkAsync(new IngestJobChunk
            {
                JobId = job.Id,
                SourceChunkId = sourceChunk.Id,
                SourceChunkIndex = sourceChunk.Index,
                Status = IngestJobChunkStatus.Pending,
            }, cancellationToken);
        }

        project.UpdatedAt = DateTime.UtcNow;
        projects.Update(project);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        await graphSync.EnsureSourceAsync(source, sourceChunks, blockDrafts.Select(draft => new IngestSourceBlock
        {
            Id = draft.Id,
            SourceId = source.Id,
            SourcePageId = draft.SourcePageId,
            Index = draft.Index,
            Kind = draft.Kind,
            Title = draft.Title,
            Locator = draft.Locator,
            PageNumber = draft.PageNumber,
            StartChar = draft.StartChar,
            EndChar = draft.EndChar,
            MetadataJson = draft.MetadataJson,
        }).ToList(), cancellationToken);
        await contextIndexing.ReindexIngestSourceAsync(source.Id, cancellationToken);
        queue.Enqueue(job.Id);
        Notify(job.ProjectId, job.Id, IngestJobUpdateKind.Created);
        return job;
    }

    private static bool IsEditableTextArtifact(string? fileName) =>
        Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant() is ".txt" or ".md" or ".markdown";

    public async Task RequestStopAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var ingest = databaseOperation.Repositories.Ingest;
        var job = await ingest.GetJobAsync(jobId, cancellationToken)
            ?? throw new InvalidOperationException($"Ingest job {jobId} not found.");

        if (job.Status == IngestJobStatus.Queued)
        {
            job.Status = IngestJobStatus.Stopped;
            job.CurrentMessage = "Stopped before starting.";
            job.CompletedAt = DateTime.UtcNow;
        }
        else if (job.Status == IngestJobStatus.Running)
        {
            if (!queue.RequestCancellation(job.Id))
            {
                await RecoverInactiveRunningJobsAsync(job.ProjectId, job.Id, cancellationToken);
                return;
            }

            job.Status = IngestJobStatus.StopRequested;
            job.CurrentMessage = "Stop requested.";
        }

        job.UpdatedAt = DateTime.UtcNow;
        ingest.UpdateJob(job);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        Notify(job.ProjectId, job.Id, job.Status == IngestJobStatus.Stopped ? IngestJobUpdateKind.Stopped : IngestJobUpdateKind.Progress);
    }

    public async Task ResumeAsync(Guid jobId, IngestResumeRequest? request = null, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var ingest = databaseOperation.Repositories.Ingest;
        var job = await ingest.GetJobResumeDetailAsync(jobId, cancellationToken)
            ?? throw new InvalidOperationException($"Ingest job {jobId} not found.");

        if (job.Status is not (IngestJobStatus.Stopped or IngestJobStatus.Failed)) return;

        if (request is not null)
        {
            var provider = await ResolveProviderAsync(request.ProviderId, requireProvider: true, cancellationToken);
            job.ProviderId = provider?.Id;
            job.ModelName = provider?.ModelId;
        }
        else
        {
            await EnsureProviderAvailableForQueuedJobAsync(job.ProviderId, cancellationToken);
        }

        var chunksToReset = job.Chunks
            .Where(chunk => chunk.Status is IngestJobChunkStatus.Running or IngestJobChunkStatus.Stopped or IngestJobChunkStatus.Failed)
            .ToList();
        if (chunksToReset.Count == 0)
        {
            chunksToReset = job.Chunks
                .Where(chunk => chunk.Status != IngestJobChunkStatus.Completed)
                .OrderBy(chunk => chunk.SourceChunkIndex)
                .Take(1)
                .ToList();
        }

        foreach (var jobChunk in chunksToReset)
        {
            jobChunk.SourceChunk.Summary = string.Empty;
            jobChunk.SourceChunk.AgentNotes = string.Empty;
            jobChunk.SourceChunk.UpdatedAt = DateTime.UtcNow;
            ingest.UpdateSourceChunk(jobChunk.SourceChunk);

            jobChunk.Status = IngestJobChunkStatus.Pending;
            jobChunk.Summary = string.Empty;
            jobChunk.ErrorMessage = null;
            jobChunk.CreatedEntityCount = 0;
            jobChunk.CreatedRelationshipCount = 0;
            jobChunk.LlmTokenCount = null;
            jobChunk.LlmTokenCountIsExact = null;
            jobChunk.LlmTokenCountMethod = null;
            jobChunk.LlmTokenEncodingName = null;
            jobChunk.StartedAt = null;
            jobChunk.CompletedAt = null;
            jobChunk.UpdatedAt = DateTime.UtcNow;
            ingest.UpdateJobChunk(jobChunk);
        }

        job.Status = IngestJobStatus.Queued;
        job.ErrorMessage = null;
        job.CompletedSourceChunks = job.Chunks.Count(chunk => chunk.Status == IngestJobChunkStatus.Completed);
        job.CurrentMessage = "Queued for resume.";
        job.CompletedAt = null;
        job.UpdatedAt = DateTime.UtcNow;
        ingest.UpdateJob(job);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        queue.Enqueue(job.Id);
        Notify(job.ProjectId, job.Id, IngestJobUpdateKind.Queued);
    }

    public async Task RestartAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var ingest = databaseOperation.Repositories.Ingest;
        var job = await ingest.GetJobDetailAsync(jobId, cancellationToken)
            ?? throw new InvalidOperationException($"Ingest job {jobId} not found.");

        await EnsureProviderAvailableForQueuedJobAsync(job.ProviderId, cancellationToken);

        queue.RequestCancellation(job.Id);

        await entityVisualExamples.RemoveIngestOwnedAsync(job.ProjectId, job.SourceId, deleteCandidates: false, cancellationToken: cancellationToken);

        var cleanup = await graphCleanup.RemoveSourceGraphContributionsAsync(job.ProjectId, job.SourceId, job.StagingRecords, cancellationToken);
        await ApplyGraphCleanupContextUpdatesAsync(job.ProjectId, cleanup, cancellationToken);

        foreach (var item in job.StagingRecords.Where(item => item.Status != IngestStagingRecordStatus.Deleted))
        {
            item.Status = IngestStagingRecordStatus.Deleted;
            item.DeletedAt = DateTime.UtcNow;
            item.UpdatedAt = DateTime.UtcNow;
            ingest.UpdateStagingRecord(item);
        }

        foreach (var jobChunk in job.Chunks)
        {
            jobChunk.SourceChunk.Summary = string.Empty;
            jobChunk.SourceChunk.AgentNotes = string.Empty;
            jobChunk.SourceChunk.UpdatedAt = DateTime.UtcNow;
            ingest.UpdateSourceChunk(jobChunk.SourceChunk);

            jobChunk.Status = IngestJobChunkStatus.Pending;
            jobChunk.Summary = string.Empty;
            jobChunk.ErrorMessage = null;
            jobChunk.CreatedEntityCount = 0;
            jobChunk.CreatedRelationshipCount = 0;
            jobChunk.LlmTokenCount = null;
            jobChunk.LlmTokenCountIsExact = null;
            jobChunk.LlmTokenCountMethod = null;
            jobChunk.LlmTokenEncodingName = null;
            jobChunk.StartedAt = null;
            jobChunk.CompletedAt = null;
            jobChunk.UpdatedAt = DateTime.UtcNow;
            ingest.UpdateJobChunk(jobChunk);
        }

        job.Status = IngestJobStatus.Queued;
        job.CompletedSourceChunks = 0;
        job.CreatedEntityCount = 0;
        job.CreatedRelationshipCount = 0;
        job.ErrorMessage = null;
        job.CurrentMessage = "Queued for restart.";
        job.StartedAt = null;
        job.CompletedAt = null;
        job.UpdatedAt = DateTime.UtcNow;
        ingest.UpdateJob(job);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        await contextIndexing.ReindexIngestSourceAsync(job.SourceId, cancellationToken);
        queue.Enqueue(job.Id);
        Notify(job.ProjectId, job.Id, IngestJobUpdateKind.Queued);
    }

    public async Task DeleteJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var ingest = databaseOperation.Repositories.Ingest;
        var job = await ingest.GetJobDetailAsync(jobId, cancellationToken);
        if (job is null) return;

        var projectId = job.ProjectId;
        queue.RequestCancellation(job.Id);
        await entityVisualExamples.RemoveIngestOwnedAsync(job.ProjectId, job.SourceId, deleteCandidates: true, cancellationToken: cancellationToken);
        var cleanup = await graphCleanup.RemoveSourceGraphContributionsAsync(job.ProjectId, job.SourceId, job.StagingRecords, cancellationToken);
        await ApplyGraphCleanupContextUpdatesAsync(job.ProjectId, cleanup, cancellationToken);
        await vectors.DeleteBySourceAsync("ingest_source", job.Source.VectorSourceId, Project.ScopeKey(job.ProjectId), cancellationToken);
        await contextIndexing.DeleteIngestSourceAsync(job.ProjectId, job.SourceId, cancellationToken);
        await graphSync.RemoveSourceAsync(job.ProjectId, job.SourceId, cancellationToken);

        ingest.RemoveSource(job.Source);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        Notify(projectId, job.Id, IngestJobUpdateKind.Deleted);
    }

    private async Task ApplyGraphCleanupContextUpdatesAsync(Guid projectId, IngestGraphCleanupResult cleanup, CancellationToken cancellationToken)
    {
        var deletedEntityIds = cleanup.EntityIdsToDelete.ToHashSet();
        foreach (var entityId in deletedEntityIds)
            await contextIndexing.DeleteEntityAsync(projectId, entityId, cancellationToken);

        foreach (var entityId in cleanup.EntityIdsToReindex.Where(entityId => !deletedEntityIds.Contains(entityId)).Distinct())
            await contextIndexing.ReindexEntityAsync(projectId, entityId, cancellationToken);
    }

    private async Task RecoverInactiveRunningJobsAsync(Guid? projectId, Guid? jobId, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var ingest = databaseOperation.Repositories.Ingest;
        var interrupted = await ingest.ListInterruptedJobsAsync(cancellationToken);
        var repairedJobs = new List<(Guid ProjectId, Guid JobId)>();

        foreach (var job in interrupted)
        {
            if (projectId is Guid requestedProjectId && job.ProjectId != requestedProjectId) continue;
            if (jobId is Guid requestedJobId && job.Id != requestedJobId) continue;
            if (queue.IsActive(job.Id)) continue;

            MarkInactiveRunningJobStopped(job);
            ingest.UpdateJob(job);

            foreach (var chunk in job.Chunks.Where(chunk => chunk.Status == IngestJobChunkStatus.Running))
            {
                chunk.Status = IngestJobChunkStatus.Stopped;
                chunk.ErrorMessage = "Stopped because no active ingest worker was running for this job.";
                chunk.CompletedAt = DateTime.UtcNow;
                chunk.UpdatedAt = DateTime.UtcNow;
                ingest.UpdateJobChunk(chunk);
            }

            await ingest.AddEventAsync(new IngestJobEvent
            {
                JobId = job.Id,
                Level = IngestJobEventLevel.Warning,
                EventType = "job.inactive_running_recovered",
                Message = "The database marked this ingest job as running, but this app instance no longer had an active worker for it, so the job was moved to Stopped and can be resumed.",
            }, cancellationToken);

            logger.LogWarning("Recovered inactive running ingest job {JobId}; job can now be resumed.", job.Id);
            repairedJobs.Add((job.ProjectId, job.Id));
        }

        if (repairedJobs.Count == 0) return;

        await databaseOperation.SaveChangesAsync(cancellationToken);
        foreach (var repairedJob in repairedJobs)
            Notify(repairedJob.ProjectId, repairedJob.JobId, IngestJobUpdateKind.Stopped);
    }

    private static void MarkInactiveRunningJobStopped(IngestJob job)
    {
        job.Status = IngestJobStatus.Stopped;
        job.CurrentMessage = "Stopped because no active ingest worker was running.";
        job.ErrorMessage = null;
        job.CompletedSourceChunks = job.Chunks.Count(chunk => chunk.Status == IngestJobChunkStatus.Completed);
        job.CompletedAt = DateTime.UtcNow;
        job.UpdatedAt = DateTime.UtcNow;
    }

    public async Task<IngestStagingRecord> UpdateReportItemAsync(Guid reportItemId, IngestReportItemUpdateRequest request, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var ingest = databaseOperation.Repositories.Ingest;
        var item = await ingest.GetStagingRecordAsync(reportItemId, cancellationToken)
            ?? throw new InvalidOperationException($"Ingest staging record {reportItemId} not found.");
        if (item.Status != IngestStagingRecordStatus.Active)
            throw new InvalidOperationException("Only active ingest staging records can be edited.");

        var title = (request.Title ?? string.Empty).Trim();
        if (title.Length == 0) throw new ArgumentException("Staging record title is required.", nameof(request));

        var summary = request.Summary?.Trim() ?? string.Empty;
        var notes = request.Notes?.Trim() ?? string.Empty;
        var resourceType = item.Kind == IngestStagingRecordKind.Relationship && !string.IsNullOrWhiteSpace(request.ResourceType)
            ? request.ResourceType.Trim()
            : item.Kind == IngestStagingRecordKind.Relationship ? item.EdgeType : item.EntityType;

        item.Title = title;
        item.Summary = summary;
        item.Notes = notes;
        if (item.Kind == IngestStagingRecordKind.Relationship)
            item.EdgeType = resourceType;
        else
            item.EntityType = resourceType;
        item.UpdatedAt = DateTime.UtcNow;

        await SyncStagingEditAsync(item, cancellationToken);
        ingest.UpdateStagingRecord(item);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        await ReindexStagingRecordContextAsync(item, cancellationToken);
        Notify(item.Job.ProjectId, item.JobId, IngestJobUpdateKind.Report);
        return item;
    }

    public async Task DeleteReportItemAsync(Guid reportItemId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var ingest = databaseOperation.Repositories.Ingest;
        var item = await ingest.GetStagingRecordAsync(reportItemId, cancellationToken)
            ?? throw new InvalidOperationException($"Ingest staging record {reportItemId} not found.");
        if (item.Status == IngestStagingRecordStatus.Deleted) return;
        if (item.Status != IngestStagingRecordStatus.Active)
            throw new InvalidOperationException("Only active ingest staging records can be deleted.");

        var affectedEntityIds = await RemoveSingleStagingGraphItemAsync(item, cancellationToken);
        MarkStagingRecordDeleted(item);
        ingest.UpdateStagingRecord(item);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        await RefreshJobCountsAsync(item.JobId, cancellationToken);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        await ReindexStagingRecordContextAsync(item, cancellationToken);
        foreach (var entityId in affectedEntityIds)
            await contextIndexing.ReindexEntityAsync(item.Job.ProjectId, entityId, cancellationToken);
        Notify(item.Job.ProjectId, item.JobId, IngestJobUpdateKind.Report);
    }

    private async Task ReindexStagingRecordContextAsync(IngestStagingRecord item, CancellationToken cancellationToken)
    {
        switch (item.Kind)
        {
            case IngestStagingRecordKind.Entity when item.EntityId is Guid entityId:
                await contextIndexing.ReindexEntityAsync(item.Job.ProjectId, entityId, cancellationToken);
                break;

            case IngestStagingRecordKind.Relationship:
                foreach (var entityId in ReadRelationshipEndpointIds(item))
                    await contextIndexing.ReindexEntityAsync(item.Job.ProjectId, entityId, cancellationToken);
                break;

            case IngestStagingRecordKind.SourceChunkNote when item.SourceChunkId is Guid sourceChunkId:
                await contextIndexing.ReindexIngestSourceChunkAsync(sourceChunkId, cancellationToken);
                break;
        }
    }

    private async Task SyncStagingEditAsync(IngestStagingRecord item, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var ingest = databaseOperation.Repositories.Ingest;
        switch (item.Kind)
        {
            case IngestStagingRecordKind.Entity:
                break;

            case IngestStagingRecordKind.Relationship:
                break;

            case IngestStagingRecordKind.SourceChunkNote:
                if (item.SourceChunkId is null) return;
                var sourceChunk = await ingest.GetSourceChunkAsync(item.SourceChunkId.Value, cancellationToken);
                if (sourceChunk is null) return;

                sourceChunk.Summary = item.Summary;
                sourceChunk.AgentNotes = item.Notes;
                sourceChunk.UpdatedAt = DateTime.UtcNow;
                ingest.UpdateSourceChunk(sourceChunk);
                break;
        }
    }

    private async Task<IReadOnlyCollection<Guid>> RemoveSingleStagingGraphItemAsync(IngestStagingRecord item, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var ingest = databaseOperation.Repositories.Ingest;
        var affectedEntityIds = new HashSet<Guid>();
        switch (item.Kind)
        {
            case IngestStagingRecordKind.Entity:
                if (item.EntityId is Guid reportEntityId)
                    affectedEntityIds.Add(reportEntityId);
                await RemoveEntityShellIfOrphanedAsync(item, item.Job.ProjectId, cancellationToken);
                break;

            case IngestStagingRecordKind.Relationship:
                foreach (var endpointEntityId in ReadRelationshipEndpointIds(item))
                    affectedEntityIds.Add(endpointEntityId);
                await RemoveStagingRelationshipEdgeIfOrphanedAsync(item, cancellationToken);
                break;

            case IngestStagingRecordKind.SourceChunkNote:
                if (item.SourceChunkId is null) break;
                var sourceChunk = await ingest.GetSourceChunkAsync(item.SourceChunkId.Value, cancellationToken);
                if (sourceChunk is null) break;

                sourceChunk.Summary = string.Empty;
                sourceChunk.AgentNotes = string.Empty;
                sourceChunk.UpdatedAt = DateTime.UtcNow;
                ingest.UpdateSourceChunk(sourceChunk);
                break;
        }

        return affectedEntityIds;
    }

    private async Task RefreshJobCountsAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var ingest = databaseOperation.Repositories.Ingest;
        var job = await ingest.GetJobAsync(jobId, cancellationToken);
        if (job is null) return;

        var reportItems = await ingest.ListStagingRecordsAsync(jobId, cancellationToken);
        RefreshJobCounts(job, reportItems);
        job.UpdatedAt = DateTime.UtcNow;
        ingest.UpdateJob(job);
    }

    private static void RefreshJobCounts(IngestJob job, IEnumerable<IngestStagingRecord> reportItems)
    {
        var activeItems = reportItems
            .Where(item => item.Status == IngestStagingRecordStatus.Active)
            .ToList();

        job.CreatedEntityCount = activeItems
            .Where(item => item.Kind == IngestStagingRecordKind.Entity)
            .Select(item => item.EntityId?.ToString("N") ?? item.GraphNodeId?.ToString() ?? item.Id.ToString("N"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        job.CreatedRelationshipCount = activeItems
            .Where(item => item.Kind == IngestStagingRecordKind.Relationship)
            .Select(item => item.GraphEdgeId?.ToString() ?? item.Id.ToString("N"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    private async Task RemoveEntityShellIfOrphanedAsync(IngestStagingRecord item, Guid projectId, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var edges = databaseOperation.Repositories.GraphEdges;
        var node = await FindStagingNodeAsync(item, projectId, cancellationToken);
        if (node is null || item.EntityId is not Guid entityId) return;

        var graphAction = IngestSourceAssertions.ReadEntityGraphAction(item.PayloadJson);
        if (!CanRemovePotentiallyIngestCreatedObject(node.Properties, graphAction, IngestSourceAssertions.CreatedEntityAction))
            return;

        if (await HasActiveStagingReferencesForEntityAsync(item.JobId, entityId, item.Id, cancellationToken))
            return;

        if (IngestWikiSheet.HasCanonSources(node.Properties) || HasCanonicalProperties(node.Properties))
            return;

        var adjacent = await edges.GetAdjacentAsync(node.Id, EdgeDirection.Both, edgeTypes: null, maxResults: null, cancellationToken);
        if (adjacent.Count == 0)
            await graphStore.RemoveNodeAsync(node.Id, cancellationToken);
    }

    private async Task RemoveStagingRelationshipEdgeIfOrphanedAsync(IngestStagingRecord item, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var edges = databaseOperation.Repositories.GraphEdges;
        if (item.GraphEdgeId is null) return;

        var edge = await edges.GetByIdAsync(item.GraphEdgeId.Value, cancellationToken);
        if (edge is null) return;

        var graphAction = IngestSourceAssertions.ReadRelationshipGraphAction(item.PayloadJson);
        if (!CanRemovePotentiallyIngestCreatedObject(edge.Properties, graphAction, IngestSourceAssertions.CreatedEdgeAction))
            return;
        if (IngestWikiSheet.HasCanonSources(edge.Properties) || HasCanonicalProperties(edge.Properties))
            return;

        await graphStore.RemoveEdgeAsync(edge.Id, cancellationToken);
    }

    private async Task<GraphNode?> FindStagingNodeAsync(IngestStagingRecord item, Guid projectId, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var nodes = databaseOperation.Repositories.GraphNodes;
        if (item.GraphNodeId is not null)
        {
            var node = await nodes.GetByIdAsync(item.GraphNodeId.Value, cancellationToken);
            if (node is not null) return node;
        }

        return item.EntityId is null || string.IsNullOrWhiteSpace(item.EntityType)
            ? null
            : await nodes.FindAsync(projectId, item.EntityType, item.EntityId.Value.ToString("N"), cancellationToken);
    }

    private async Task<bool> HasActiveStagingReferencesForEntityAsync(
        Guid jobId,
        Guid entityId,
        Guid excludedStagingRecordId,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var ingest = databaseOperation.Repositories.Ingest;
        var records = await ingest.ListStagingRecordsAsync(jobId, cancellationToken);
        return records.Any(item =>
            item.Id != excludedStagingRecordId
            && item.Status == IngestStagingRecordStatus.Active
            && ((item.Kind == IngestStagingRecordKind.Entity && item.EntityId == entityId)
                || (item.Kind == IngestStagingRecordKind.Relationship && IsRelationshipConnectedTo(item, entityId))));
    }

    private static IReadOnlyCollection<Guid> ReadRelationshipEndpointIds(IngestStagingRecord item)
    {
        var ids = new HashSet<Guid>();
        if (item.FromEntityId is Guid from && from != Guid.Empty)
            ids.Add(from);
        if (item.ToEntityId is Guid to && to != Guid.Empty)
            ids.Add(to);
        foreach (var id in ReadRelationshipEndpointIds(item.PayloadJson))
            ids.Add(id);
        return ids;
    }

    private static void MarkStagingRecordDeleted(IngestStagingRecord item)
    {
        item.Status = IngestStagingRecordStatus.Deleted;
        item.DeletedAt = DateTime.UtcNow;
        item.UpdatedAt = DateTime.UtcNow;
    }

    private async Task RemoveRelationshipGraphEdgeAsync(IngestReportItem item, Guid fallbackSourceId, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var edges = databaseOperation.Repositories.GraphEdges;
        _ = ResolveRequiredReportSourceChunkId(item);
        if (item.GraphEdgeId is null) return;

        try
        {
            var edge = await edges.GetByIdAsync(item.GraphEdgeId.Value, cancellationToken);
            if (edge is null) return;

            var sourceId = ResolveReportSourceId(item, fallbackSourceId);
            var sourceChunkId = ResolveRequiredReportSourceChunkId(item);
            IngestSourceAssertions.RemoveRelationshipChunk(edge.Properties, sourceId, sourceChunkId);
            var removeSourceWikiSection = IngestSourceAssertions.CountRelationshipSourceChunks(edge.Properties, sourceId) == 0;
            IngestWikiSheet.RemoveSourceChunkCitations(edge.Properties, sourceId, sourceChunkId, removeSourceWikiSection);

            var graphAction = IngestSourceAssertions.ReadRelationshipGraphAction(item.PayloadJson);
            if (CanRemoveGraphEdgeAfterSourceSubtraction(edge, graphAction))
            {
                await graphStore.RemoveEdgeAsync(edge.Id, cancellationToken);
            }
            else
            {
                edge.UpdatedAt = DateTime.UtcNow;
                edges.Update(edge);
                await databaseOperation.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to remove graph edge {GraphEdgeId} for ingest report item {ReportItemId}", item.GraphEdgeId, item.Id);
            throw;
        }
    }

    private async Task RemoveEntityGraphContributionAsync(IngestReportItem item, Guid projectId, Guid fallbackSourceId, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var nodes = databaseOperation.Repositories.GraphNodes;
        try
        {
            var node = await FindReportNodeAsync(item, projectId, cancellationToken);
            if (node is null) return;

            var sourceId = ResolveReportSourceId(item, fallbackSourceId);
            var sourceChunkId = ResolveRequiredReportSourceChunkId(item);
            IngestSourceAssertions.RemoveEntityChunk(node.Properties, sourceId, sourceChunkId);
            var removeSourceWikiSection = IngestSourceAssertions.CountEntitySourceChunks(node.Properties, sourceId) == 0;
            IngestWikiSheet.RemoveSourceChunkCitations(node.Properties, sourceId, sourceChunkId, removeSourceWikiSection);
            await RemoveExtractedFromEdgeForChunkAsync(node, sourceId, sourceChunkId, cancellationToken);

            var graphAction = IngestSourceAssertions.ReadEntityGraphAction(item.PayloadJson);
            if (item.EntityId is Guid entityId
                && await HasActiveReportReferencesForEntityAsync(item.JobId, entityId, item.Id, cancellationToken))
            {
                node.UpdatedAt = DateTime.UtcNow;
                nodes.Update(node);
                await databaseOperation.SaveChangesAsync(cancellationToken);
            }
            else if (await CanRemoveGraphNodeAfterSourceSubtractionAsync(node, graphAction, cancellationToken))
            {
                await graphStore.RemoveNodeAsync(node.Id, cancellationToken);
            }
            else
            {
                node.UpdatedAt = DateTime.UtcNow;
                nodes.Update(node);
                await databaseOperation.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to remove graph node {GraphNodeId} for ingest report item {ReportItemId}", item.GraphNodeId, item.Id);
            throw;
        }
    }

    private async Task RemoveExtractedFromEdgeForChunkAsync(GraphNode node, Guid sourceId, Guid sourceChunkId, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var edges = databaseOperation.Repositories.GraphEdges;
        var extractedFromEdges = await edges.GetAdjacentAsync(
            node.Id,
            EdgeDirection.Outgoing,
            [IngestGraphSync.ExtractedFromEdgeType],
            maxResults: null,
            cancellationToken);

        foreach (var edge in extractedFromEdges)
        {
            if (await EdgeTargetsSourceChunkAsync(edge, sourceId, sourceChunkId, cancellationToken))
                await graphStore.RemoveEdgeAsync(edge.Id, cancellationToken);
        }
    }

    private async Task<bool> EdgeTargetsSourceChunkAsync(GraphEdge edge, Guid sourceId, Guid sourceChunkId, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var nodes = databaseOperation.Repositories.GraphNodes;
        var sourceKey = sourceId.ToString("N");
        var sourceChunkKey = sourceChunkId.ToString("N");
        if (edge.Properties.TryGetValue("sourceChunkId", out var edgeSourceChunkId)
            && string.Equals(edgeSourceChunkId?.ToString(), sourceChunkKey, StringComparison.OrdinalIgnoreCase)
            && (!edge.Properties.TryGetValue("sourceId", out var edgeSourceId)
                || string.Equals(edgeSourceId?.ToString(), sourceKey, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var target = await nodes.GetByIdAsync(edge.ToNodeId, cancellationToken);
        return target is not null
            && string.Equals(target.NodeType, IngestGraphSync.SourceChunkNodeType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(target.Key, sourceChunkKey, StringComparison.OrdinalIgnoreCase)
            && (!target.Properties.TryGetValue("sourceId", out var targetSourceId)
                || string.Equals(targetSourceId?.ToString(), sourceKey, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<bool> CanRemoveGraphNodeAfterSourceSubtractionAsync(GraphNode node, string? graphAction, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var edges = databaseOperation.Repositories.GraphEdges;
        if (!CanRemovePotentiallyIngestCreatedObject(node.Properties, graphAction, IngestSourceAssertions.CreatedEntityAction))
            return false;
        if (IngestSourceAssertions.CountEntitySources(node.Properties) > 0)
            return false;
        if (IngestWikiSheet.HasCitations(node.Properties))
            return false;
        if (HasCanonicalProperties(node.Properties))
            return false;

        var adjacent = await edges.GetAdjacentAsync(node.Id, EdgeDirection.Both, edgeTypes: null, maxResults: null, cancellationToken);
        return adjacent.Count == 0;
    }

    private static bool CanRemoveGraphEdgeAfterSourceSubtraction(GraphEdge edge, string? graphAction)
    {
        if (!CanRemovePotentiallyIngestCreatedObject(edge.Properties, graphAction, IngestSourceAssertions.CreatedEdgeAction))
            return false;
        if (IngestSourceAssertions.CountRelationshipSources(edge.Properties) > 0)
            return false;
        if (IngestWikiSheet.HasCitations(edge.Properties))
            return false;
        return !HasCanonicalProperties(edge.Properties);
    }

    private static bool CanRemovePotentiallyIngestCreatedObject(
        IReadOnlyDictionary<string, object?> properties,
        string? graphAction,
        string createdAction) =>
        IngestSourceAssertions.IsIngestCreatedGraphObject(properties)
        || string.Equals(graphAction, createdAction, StringComparison.Ordinal);

    private static bool HasCanonicalProperties(IReadOnlyDictionary<string, object?> properties) =>
        properties.Keys.Any(key => !IsInternalProperty(key)
            && !IngestSourceAssertions.IsProtectedProperty(key)
            && !IngestSourceAssertions.IsLegacyIngestProperty(key)
            && !IngestWikiSheet.IsWikiStorageProperty(key));

    private static bool IsInternalProperty(string key) =>
        string.Equals(key, "sourceType", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "sourceId", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "sourceChunkId", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "sourceChunkIndex", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "sourceBlockId", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "sourcePageId", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "sourceGraphTargetType", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "structural", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "order", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("vectorIndex", StringComparison.OrdinalIgnoreCase);

    private static Guid ResolveReportSourceId(IngestReportItem item, Guid fallbackSourceId) =>
        IngestSourceAssertions.ReadPayloadSourceId(item.PayloadJson) ?? fallbackSourceId;

    private static Guid ResolveRequiredReportSourceChunkId(IngestReportItem item) =>
        NormalizeReportSourceChunkId(item.SourceChunkId)
        ?? IngestSourceAssertions.ReadPayloadSourceChunkId(item.PayloadJson)
        ?? throw new InvalidOperationException($"Cannot delete ingest report item {item.Id} safely because it is not tied to exactly one source chunk.");

    private static Guid? NormalizeReportSourceChunkId(Guid? sourceChunkId) =>
        sourceChunkId is Guid id && id != Guid.Empty ? id : null;

    private async Task<GraphNode?> FindReportNodeAsync(IngestReportItem item, Guid projectId, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var nodes = databaseOperation.Repositories.GraphNodes;
        if (item.GraphNodeId is not null)
        {
            var node = await nodes.GetByIdAsync(item.GraphNodeId.Value, cancellationToken);
            if (node is not null) return node;
        }

        return item.EntityId is null || string.IsNullOrWhiteSpace(item.ResourceType)
            ? null
            : await nodes.FindAsync(projectId, item.ResourceType, item.EntityId.Value.ToString("N"), cancellationToken);
    }

    private static void MarkReportItemDeleted(IngestReportItem item)
    {
        item.Status = IngestReportItemStatus.Deleted;
        item.DeletedAt = DateTime.UtcNow;
        item.UpdatedAt = DateTime.UtcNow;
    }

    private async Task<bool> HasActiveReportReferencesForEntityAsync(
        Guid jobId,
        Guid entityId,
        Guid excludedReportItemId,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var ingest = databaseOperation.Repositories.Ingest;
        var reportItems = await ingest.ListReportItemsAsync(jobId, cancellationToken);
        return reportItems.Any(item =>
            item.Id != excludedReportItemId
            && item.Status != IngestReportItemStatus.Deleted
            && ((item.Kind == IngestReportItemKind.Entity && item.EntityId == entityId)
                || (item.Kind == IngestReportItemKind.Relationship && IsRelationshipConnectedTo(item, entityId))));
    }

    private bool IsRelationshipConnectedTo(IngestReportItem relationship, Guid entityId)
    {
        try
        {
            if (!relationship.PayloadJson.TrimStart().StartsWith('{')) return false;
            using var doc = JsonDocument.Parse(relationship.PayloadJson);
            return MatchesEndpoint(doc.RootElement, "fromEntityId", entityId)
                || MatchesEndpoint(doc.RootElement, "toEntityId", entityId);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Invalid relationship payload JSON for ingest report item {ReportItemId}", relationship.Id);
            return false;
        }
    }

    private bool IsRelationshipConnectedTo(IngestStagingRecord relationship, Guid entityId)
    {
        if (relationship.FromEntityId == entityId || relationship.ToEntityId == entityId)
            return true;

        try
        {
            if (!relationship.PayloadJson.TrimStart().StartsWith('{')) return false;
            using var doc = JsonDocument.Parse(relationship.PayloadJson);
            return MatchesEndpoint(doc.RootElement, "fromEntityId", entityId)
                || MatchesEndpoint(doc.RootElement, "toEntityId", entityId);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Invalid relationship payload JSON for ingest staging record {StagingRecordId}", relationship.Id);
            return false;
        }
    }

    private static bool MatchesEndpoint(JsonElement element, string propertyName, Guid entityId) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
        && Guid.TryParse(property.GetString(), out var parsed)
        && parsed == entityId;

    private static IReadOnlyList<Guid> ReadRelationshipEndpointIds(string payloadJson)
    {
        if (!payloadJson.TrimStart().StartsWith('{')) return [];
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var ids = new List<Guid>(2);
            AddEndpointId(doc.RootElement, "fromEntityId", ids);
            AddEndpointId(doc.RootElement, "toEntityId", ids);
            return ids;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void AddEndpointId(JsonElement element, string propertyName, ICollection<Guid> ids)
    {
        if (element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && Guid.TryParse(property.GetString(), out var parsed))
        {
            ids.Add(parsed);
        }
    }

    private static string MergeSourceMetadataJson(string baseMetadataJson, BookArtifactPreprocessResult preprocessed)
    {
        var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(baseMetadataJson) && baseMetadataJson.TrimStart().StartsWith('{'))
        {
            try
            {
                using var baseDoc = JsonDocument.Parse(baseMetadataJson);
                foreach (var property in baseDoc.RootElement.EnumerateObject())
                    metadata[property.Name] = JsonElementToObject(property.Value);
            }
            catch (JsonException)
            {
                metadata["rawMetadata"] = baseMetadataJson;
            }
        }

        metadata["artifactPreprocess"] = JsonSerializer.Deserialize<object>(preprocessed.SourceMetadataJson);
        metadata["artifactDiagnostics"] = preprocessed.Diagnostics;
        metadata["artifactUsedVision"] = preprocessed.UsedVision;
        metadata["sourceBlockCount"] = preprocessed.Blocks.Count;
        metadata["sourcePageCount"] = preprocessed.Pages.Count;
        return JsonSerializer.Serialize(metadata);
    }

    private static string EnsureExtractionProfileMetadata(string baseMetadataJson, IngestExtractionProfile extractionProfile)
    {
        var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(baseMetadataJson) && baseMetadataJson.TrimStart().StartsWith('{'))
        {
            try
            {
                using var baseDoc = JsonDocument.Parse(baseMetadataJson);
                foreach (var property in baseDoc.RootElement.EnumerateObject())
                    metadata[property.Name] = JsonElementToObject(property.Value);
            }
            catch (JsonException)
            {
                metadata["rawMetadata"] = baseMetadataJson;
            }
        }

        metadata["extractionProfile"] = extractionProfile.ToString();
        return JsonSerializer.Serialize(metadata);
    }

    private static object? JsonElementToObject(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when element.TryGetDouble(out var doubleValue) => doubleValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => JsonSerializer.Deserialize<object>(element.GetRawText()),
        };

    private static string ComputeHash(string text)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private async Task<LlmProvider?> ResolveProviderAsync(int? providerId, bool requireProvider, CancellationToken cancellationToken)
    {
        if (providerId is int id)
        {
            var provider = await providers.GetByIdAsync(id, cancellationToken)
                ?? throw new InvalidOperationException($"LLM provider {id} was not found.");
            if (!await providers.IsChatProviderWorkingAsync(id, cancellationToken))
                throw new InvalidOperationException("Run Test successfully before using this provider for ingest.");

            return provider;
        }

        var availability = await providers.GetDefaultChatProviderAvailabilityAsync(cancellationToken);
        if (availability.IsAvailable && availability.Provider is not null)
            return availability.Provider;

        return requireProvider
            ? throw new InvalidOperationException(availability.Message)
            : null;
    }

    private async Task EnsureProviderAvailableForQueuedJobAsync(int? providerId, CancellationToken cancellationToken)
    {
        if (providerId is int id && await providers.IsChatProviderWorkingAsync(id, cancellationToken))
            return;

        var availability = await providers.GetDefaultChatProviderAvailabilityAsync(cancellationToken);
        if (availability.IsAvailable && availability.Provider is not null)
            return;

        throw new InvalidOperationException(availability.Message);
    }

    private void Notify(Guid projectId, Guid jobId, IngestJobUpdateKind kind) =>
        notifier.Notify(new IngestJobUpdate(projectId, jobId, kind, DateTime.UtcNow));
}
