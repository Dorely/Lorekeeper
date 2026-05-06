using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lorekeeper.Knowledge;
using Lorekeeper.Llm;
using Lorekeeper.Models;
using Lorekeeper.Persistence.Repositories;

namespace Lorekeeper.Ingest;

public sealed class IngestService(
    IIngestRepository ingest,
    IProjectRepository projects,
    IIngestSourceStructureBuilder structureBuilder,
    IIngestGraphSync graphSync,
    IIngestJobQueue queue,
    ILlmProviderService providers,
    IIngestJobNotifier notifier,
    IGraphStore graphStore,
    IGraphNodeRepository nodes,
    IGraphEdgeRepository edges,
    IVectorStore vectors,
    ILogger<IngestService> logger) : IIngestService
{
    public async Task<IReadOnlyList<IngestJob>> ListJobsAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await ingest.ListJobsByProjectAsync(projectId, cancellationToken);

    public async Task<IReadOnlyList<IngestJobListItem>> ListJobSummariesAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await ingest.ListJobSummariesByProjectAsync(projectId, cancellationToken);

    public Task<IngestJob?> GetJobDetailAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        ingest.GetJobDetailAsync(jobId, cancellationToken);

    public Task<IngestJobDetailView?> GetJobViewAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        ingest.GetJobDetailViewAsync(jobId, cancellationToken: cancellationToken);

    public async Task<IReadOnlyList<IngestReportItemView>> ListReportItemViewsAsync(Guid jobId, Guid? sourceChunkId = null, CancellationToken cancellationToken = default) =>
        await ingest.ListReportItemViewsAsync(jobId, sourceChunkId, cancellationToken);

    public Task<IngestSourceChunkExcerpt?> GetSourceChunkExcerptAsync(Guid sourceChunkId, int maxChars = 8_000, CancellationToken cancellationToken = default) =>
        ingest.GetSourceChunkExcerptAsync(sourceChunkId, maxChars, cancellationToken);

    public async Task<IngestJob> CreateJobAsync(Guid projectId, IngestCreateJobRequest request, CancellationToken cancellationToken = default)
    {
        var project = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");

        var title = (request.Title ?? string.Empty).Trim();
        if (title.Length == 0) throw new ArgumentException("Source title is required.", nameof(request));

        var sourceText = request.SourceText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sourceText)) throw new ArgumentException("Source text is required.", nameof(request));

        var instructions = (request.UserInstructions ?? string.Empty).Trim();

        var provider = await ResolveProviderAsync(request.ProviderId, requireProvider: true, cancellationToken);

        var source = new IngestSource
        {
            ProjectId = projectId,
            Title = title,
            SourceKind = request.SourceKind?.Trim() ?? string.Empty,
            Description = request.Description?.Trim() ?? string.Empty,
            UserInstructions = instructions,
            SourceText = sourceText,
            SourceHash = ComputeHash(sourceText),
            VectorIndexState = VectorIndexState.Stale,
        };

        await ingest.AddSourceAsync(source, cancellationToken);

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
        await ingest.SaveChangesAsync(cancellationToken);
        await graphSync.EnsureSourceAsync(source, sourceChunks, cancellationToken);
        queue.Enqueue(job.Id);
        Notify(job.ProjectId, job.Id, IngestJobUpdateKind.Created);
        return job;
    }

    public async Task RequestStopAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
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
            job.Status = IngestJobStatus.StopRequested;
            job.CurrentMessage = "Stop requested.";
            queue.RequestCancellation(job.Id);
        }

        job.UpdatedAt = DateTime.UtcNow;
        ingest.UpdateJob(job);
        await ingest.SaveChangesAsync(cancellationToken);
        Notify(job.ProjectId, job.Id, job.Status == IngestJobStatus.Stopped ? IngestJobUpdateKind.Stopped : IngestJobUpdateKind.Progress);
    }

    public async Task ResumeAsync(Guid jobId, IngestResumeRequest? request = null, CancellationToken cancellationToken = default)
    {
        var job = await ingest.GetJobDetailAsync(jobId, cancellationToken)
            ?? throw new InvalidOperationException($"Ingest job {jobId} not found.");

        if (job.Status is not (IngestJobStatus.Stopped or IngestJobStatus.Failed)) return;

        if (request is not null)
        {
            var provider = await ResolveProviderAsync(request.ProviderId, requireProvider: true, cancellationToken);
            job.ProviderId = provider?.Id;
            job.ModelName = provider?.ModelId;
        }

        foreach (var jobChunk in job.Chunks.Where(chunk => chunk.Status is IngestJobChunkStatus.Running or IngestJobChunkStatus.Stopped or IngestJobChunkStatus.Failed))
        {
            jobChunk.Status = IngestJobChunkStatus.Pending;
            jobChunk.ErrorMessage = null;
            jobChunk.UpdatedAt = DateTime.UtcNow;
            ingest.UpdateJobChunk(jobChunk);
        }

        job.Status = IngestJobStatus.Queued;
        job.ErrorMessage = null;
        job.CurrentMessage = "Queued for resume.";
        job.CompletedAt = null;
        job.UpdatedAt = DateTime.UtcNow;
        ingest.UpdateJob(job);
        await ingest.SaveChangesAsync(cancellationToken);
        queue.Enqueue(job.Id);
        Notify(job.ProjectId, job.Id, IngestJobUpdateKind.Queued);
    }

    public async Task RestartAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await ingest.GetJobDetailAsync(jobId, cancellationToken)
            ?? throw new InvalidOperationException($"Ingest job {jobId} not found.");

        queue.RequestCancellation(job.Id);

        await RemoveReportGraphItemsAsync(job.ReportItems, job.ProjectId, job.SourceId, cancellationToken);

        foreach (var item in job.ReportItems.Where(item => item.Status != IngestReportItemStatus.Deleted))
        {
            item.Status = IngestReportItemStatus.Deleted;
            item.DeletedAt = DateTime.UtcNow;
            item.UpdatedAt = DateTime.UtcNow;
            ingest.UpdateReportItem(item);
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
        await ingest.SaveChangesAsync(cancellationToken);
        queue.Enqueue(job.Id);
        Notify(job.ProjectId, job.Id, IngestJobUpdateKind.Queued);
    }

    public async Task DeleteJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await ingest.GetJobDetailAsync(jobId, cancellationToken);
        if (job is null) return;

        var projectId = job.ProjectId;
        queue.RequestCancellation(job.Id);
        await RemoveReportGraphItemsAsync(job.ReportItems, job.ProjectId, job.SourceId, cancellationToken);
        try
        {
            await vectors.DeleteBySourceAsync("ingest_source", job.Source.VectorSourceId, Project.ScopeKey(job.ProjectId), cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete vector fragments for ingest source {SourceId}", job.SourceId);
        }

        try
        {
            await graphSync.RemoveSourceAsync(job.ProjectId, job.SourceId, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete graph nodes for ingest source {SourceId}", job.SourceId);
        }

        ingest.RemoveSource(job.Source);
        await ingest.SaveChangesAsync(cancellationToken);
        Notify(projectId, job.Id, IngestJobUpdateKind.Deleted);
    }

    public async Task<IngestReportItem> UpdateReportItemAsync(Guid reportItemId, IngestReportItemUpdateRequest request, CancellationToken cancellationToken = default)
    {
        var item = await ingest.GetReportItemAsync(reportItemId, cancellationToken)
            ?? throw new InvalidOperationException($"Ingest report item {reportItemId} not found.");
        if (item.Status == IngestReportItemStatus.Deleted)
            throw new InvalidOperationException("Deleted report items cannot be edited.");

        var title = (request.Title ?? string.Empty).Trim();
        if (title.Length == 0) throw new ArgumentException("Report item title is required.", nameof(request));

        var summary = request.Summary?.Trim() ?? string.Empty;
        var notes = request.Notes?.Trim() ?? string.Empty;
        var evidence = request.Evidence?.Trim() ?? string.Empty;
        var resourceType = item.Kind == IngestReportItemKind.Relationship && !string.IsNullOrWhiteSpace(request.ResourceType)
            ? request.ResourceType.Trim()
            : item.ResourceType;

        item.Title = title;
        item.Summary = summary;
        item.Notes = notes;
        item.Evidence = evidence;
        item.ResourceType = resourceType;
        item.UpdatedAt = DateTime.UtcNow;

        await SyncReportEditToGraphAsync(item, cancellationToken);
        ingest.UpdateReportItem(item);
        await ingest.SaveChangesAsync(cancellationToken);
        Notify(item.Job.ProjectId, item.JobId, IngestJobUpdateKind.Report);
        return item;
    }

    public async Task DeleteReportItemAsync(Guid reportItemId, CancellationToken cancellationToken = default)
    {
        var item = await ingest.GetReportItemAsync(reportItemId, cancellationToken)
            ?? throw new InvalidOperationException($"Ingest report item {reportItemId} not found.");
        if (item.Status == IngestReportItemStatus.Deleted) return;

        await RemoveSingleReportGraphItemAsync(item, cancellationToken);
        MarkReportItemDeleted(item);
        ingest.UpdateReportItem(item);
        await ingest.SaveChangesAsync(cancellationToken);
        await RefreshJobCountsAsync(item.JobId, cancellationToken);
        await ingest.SaveChangesAsync(cancellationToken);
        Notify(item.Job.ProjectId, item.JobId, IngestJobUpdateKind.Report);
    }

    private async Task RemoveReportGraphItemsAsync(IEnumerable<IngestReportItem> reportItems, Guid projectId, Guid fallbackSourceId, CancellationToken cancellationToken)
    {
        var activeItems = reportItems.Where(item => item.Status != IngestReportItemStatus.Deleted).ToList();
        foreach (var item in activeItems.Where(item => item.Kind == IngestReportItemKind.Relationship))
        {
            await RemoveRelationshipGraphEdgeAsync(item, fallbackSourceId, cancellationToken);
        }

        foreach (var item in activeItems.Where(item => item.Kind == IngestReportItemKind.Entity))
        {
            await RemoveEntityGraphContributionAsync(item, projectId, fallbackSourceId, cancellationToken);
        }
    }

    private async Task SyncReportEditToGraphAsync(IngestReportItem item, CancellationToken cancellationToken)
    {
        switch (item.Kind)
        {
            case IngestReportItemKind.Entity:
                var node = await FindReportNodeAsync(item, item.Job.ProjectId, cancellationToken);
                if (node is null) return;

                if (ResolveReportSourceId(item, item.Job.SourceId) is not Guid entitySourceId) return;
                IngestSourceAssertions.UpsertEntityAssertion(
                    node.Properties,
                    BuildReportAssertionInput(item, entitySourceId, item.Summary, item.Evidence, item.Notes, replaceExistingText: true));
                node.UpdatedAt = DateTime.UtcNow;
                nodes.Update(node);
                break;

            case IngestReportItemKind.Relationship:
                if (item.GraphEdgeId is null) return;
                var edge = await edges.GetByIdAsync(item.GraphEdgeId.Value, cancellationToken);
                if (edge is null) return;

                if (ResolveReportSourceId(item, item.Job.SourceId) is not Guid relationshipSourceId) return;
                IngestSourceAssertions.UpsertRelationshipAssertion(
                    edge.Properties,
                    BuildReportAssertionInput(item, relationshipSourceId, item.Summary, item.Evidence, item.Notes, replaceExistingText: true));
                edge.UpdatedAt = DateTime.UtcNow;
                edges.Update(edge);
                break;

            case IngestReportItemKind.SourceChunkNote:
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

    private async Task RemoveSingleReportGraphItemAsync(IngestReportItem item, CancellationToken cancellationToken)
    {
        switch (item.Kind)
        {
            case IngestReportItemKind.Entity:
                if (item.EntityId is not null)
                {
                    var reportItems = await ingest.ListReportItemsAsync(item.JobId, cancellationToken);
                    foreach (var relationship in reportItems.Where(candidate =>
                        candidate.Status != IngestReportItemStatus.Deleted
                        && candidate.Kind == IngestReportItemKind.Relationship
                        && IsRelationshipConnectedTo(candidate, item.EntityId.Value)))
                    {
                        await RemoveRelationshipGraphEdgeAsync(relationship, item.Job.SourceId, cancellationToken);
                        MarkReportItemDeleted(relationship);
                        ingest.UpdateReportItem(relationship);
                    }
                }

                await RemoveEntityGraphContributionAsync(item, item.Job.ProjectId, item.Job.SourceId, cancellationToken);
                break;

            case IngestReportItemKind.Relationship:
                await RemoveRelationshipGraphEdgeAsync(item, item.Job.SourceId, cancellationToken);
                break;

            case IngestReportItemKind.SourceChunkNote:
                if (item.SourceChunkId is null) break;
                var sourceChunk = await ingest.GetSourceChunkAsync(item.SourceChunkId.Value, cancellationToken);
                if (sourceChunk is null) break;

                sourceChunk.Summary = string.Empty;
                sourceChunk.AgentNotes = string.Empty;
                sourceChunk.UpdatedAt = DateTime.UtcNow;
                ingest.UpdateSourceChunk(sourceChunk);
                break;
        }
    }

    private async Task RefreshJobCountsAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await ingest.GetJobAsync(jobId, cancellationToken);
        if (job is null) return;

        var reportItems = await ingest.ListReportItemsAsync(jobId, cancellationToken);
        job.CreatedEntityCount = reportItems.Count(item => item.Kind == IngestReportItemKind.Entity && item.Status == IngestReportItemStatus.Active);
        job.CreatedRelationshipCount = reportItems.Count(item => item.Kind == IngestReportItemKind.Relationship && item.Status == IngestReportItemStatus.Active);
        job.UpdatedAt = DateTime.UtcNow;
        ingest.UpdateJob(job);
    }

    private async Task RemoveRelationshipGraphEdgeAsync(IngestReportItem item, Guid fallbackSourceId, CancellationToken cancellationToken)
    {
        if (item.GraphEdgeId is null) return;
        try
        {
            var edge = await edges.GetByIdAsync(item.GraphEdgeId.Value, cancellationToken);
            if (edge is null) return;

            var sourceId = ResolveReportSourceId(item, fallbackSourceId);
            if (sourceId is not null)
                IngestSourceAssertions.RemoveRelationshipSource(edge.Properties, sourceId.Value);

            var graphAction = IngestSourceAssertions.ReadRelationshipGraphAction(item.PayloadJson);
            if (CanRemoveGraphEdgeAfterSourceSubtraction(edge, graphAction))
            {
                await graphStore.RemoveEdgeAsync(edge.Id, cancellationToken);
            }
            else
            {
                edge.UpdatedAt = DateTime.UtcNow;
                edges.Update(edge);
                await edges.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to remove graph edge {GraphEdgeId} for ingest report item {ReportItemId}", item.GraphEdgeId, item.Id);
        }
    }

    private async Task RemoveEntityGraphContributionAsync(IngestReportItem item, Guid projectId, Guid fallbackSourceId, CancellationToken cancellationToken)
    {
        try
        {
            var node = await FindReportNodeAsync(item, projectId, cancellationToken);
            if (node is null) return;

            var sourceId = ResolveReportSourceId(item, fallbackSourceId);
            if (sourceId is not null)
            {
                IngestSourceAssertions.RemoveEntitySource(node.Properties, sourceId.Value);
                await RemoveExtractedFromEdgesForSourceAsync(node, sourceId.Value, cancellationToken);
            }

            var graphAction = IngestSourceAssertions.ReadEntityGraphAction(item.PayloadJson);
            if (await CanRemoveGraphNodeAfterSourceSubtractionAsync(node, graphAction, cancellationToken))
            {
                await graphStore.RemoveNodeAsync(node.Id, cancellationToken);
            }
            else
            {
                node.UpdatedAt = DateTime.UtcNow;
                nodes.Update(node);
                await nodes.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to remove graph node {GraphNodeId} for ingest report item {ReportItemId}", item.GraphNodeId, item.Id);
        }
    }

    private async Task RemoveExtractedFromEdgesForSourceAsync(GraphNode node, Guid sourceId, CancellationToken cancellationToken)
    {
        var extractedFromEdges = await edges.GetAdjacentAsync(
            node.Id,
            EdgeDirection.Outgoing,
            [IngestGraphSync.ExtractedFromEdgeType],
            maxResults: null,
            cancellationToken);

        foreach (var edge in extractedFromEdges)
        {
            if (await EdgeTargetsSourceAsync(edge, sourceId, cancellationToken))
                await graphStore.RemoveEdgeAsync(edge.Id, cancellationToken);
        }
    }

    private async Task<bool> EdgeTargetsSourceAsync(GraphEdge edge, Guid sourceId, CancellationToken cancellationToken)
    {
        var sourceKey = sourceId.ToString("N");
        if (edge.Properties.TryGetValue("sourceId", out var edgeSourceId)
            && string.Equals(edgeSourceId?.ToString(), sourceKey, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var target = await nodes.GetByIdAsync(edge.ToNodeId, cancellationToken);
        return target is not null
            && target.Properties.TryGetValue("sourceId", out var targetSourceId)
            && string.Equals(targetSourceId?.ToString(), sourceKey, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> CanRemoveGraphNodeAfterSourceSubtractionAsync(GraphNode node, string? graphAction, CancellationToken cancellationToken)
    {
        if (!CanRemovePotentiallyIngestCreatedObject(node.Properties, graphAction, IngestSourceAssertions.CreatedEntityAction))
            return false;
        if (IngestSourceAssertions.CountEntitySources(node.Properties) > 0)
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
        return !HasCanonicalProperties(edge.Properties);
    }

    private static bool CanRemovePotentiallyIngestCreatedObject(
        IReadOnlyDictionary<string, object?> properties,
        string? graphAction,
        string createdAction) =>
        IngestSourceAssertions.IsIngestCreatedGraphObject(properties)
        || string.Equals(graphAction, createdAction, StringComparison.Ordinal)
        || string.IsNullOrWhiteSpace(graphAction);

    private static bool HasCanonicalProperties(IReadOnlyDictionary<string, object?> properties) =>
        properties.Keys.Any(key => !IsInternalProperty(key)
            && !IngestSourceAssertions.IsProtectedProperty(key)
            && !IngestSourceAssertions.IsLegacyIngestProperty(key));

    private static bool IsInternalProperty(string key) =>
        string.Equals(key, "sourceType", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "sourceId", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "structural", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "order", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("vectorIndex", StringComparison.OrdinalIgnoreCase);

    private static Guid? ResolveReportSourceId(IngestReportItem item, Guid fallbackSourceId) =>
        IngestSourceAssertions.ReadPayloadSourceId(item.PayloadJson) ?? fallbackSourceId;

    private static IngestAssertionInput BuildReportAssertionInput(
        IngestReportItem item,
        Guid sourceId,
        string? summary,
        string? evidence,
        string? notes,
        bool replaceExistingText)
    {
        var sourceChunkId = item.SourceChunkId ?? Guid.Empty;
        var sourceChunkIndex = ReadPayloadSourceChunkIndex(item.PayloadJson);
        return new IngestAssertionInput(
            item.JobId,
            sourceId,
            ReadPayloadString(item.PayloadJson, "sourceTitle") ?? item.Job.Source?.Title ?? string.Empty,
            ReadPayloadString(item.PayloadJson, "sourceKind") ?? item.Job.Source?.SourceKind ?? string.Empty,
            sourceChunkId,
            sourceChunkIndex,
            summary,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
            [],
            evidence,
            notes,
            replaceExistingText);
    }

    private static int ReadPayloadSourceChunkIndex(string payloadJson)
    {
        if (!payloadJson.TrimStart().StartsWith('{')) return 0;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.TryGetProperty("latestSeenSourceChunkIndex", out var latest)
                && latest.ValueKind == JsonValueKind.Number
                && latest.TryGetInt32(out var latestIndex))
            {
                return latestIndex;
            }
            if (doc.RootElement.TryGetProperty("sourceChunkIndex", out var chunkIndex)
                && chunkIndex.ValueKind == JsonValueKind.Number
                && chunkIndex.TryGetInt32(out var parsed))
            {
                return parsed;
            }
        }
        catch (JsonException) { }
        return 0;
    }

    private static string? ReadPayloadString(string payloadJson, string propertyName) =>
        IngestSourceAssertions.TryReadPayloadString(payloadJson, propertyName, out var value) ? value : null;

    private async Task<GraphNode?> FindReportNodeAsync(IngestReportItem item, Guid projectId, CancellationToken cancellationToken)
    {
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

    private static bool MatchesEndpoint(JsonElement element, string propertyName, Guid entityId) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
        && Guid.TryParse(property.GetString(), out var parsed)
        && parsed == entityId;

    private static string ComputeHash(string text)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private async Task<LlmProvider?> ResolveProviderAsync(int? providerId, bool requireProvider, CancellationToken cancellationToken)
    {
        if (providerId is int id)
        {
            return await providers.GetByIdAsync(id, cancellationToken)
                ?? throw new InvalidOperationException($"LLM provider {id} was not found.");
        }

        var provider = await providers.GetDefaultAsync(cancellationToken);
        return provider is null && requireProvider
            ? throw new InvalidOperationException("No LLM providers are configured.")
            : provider;
    }

    private void Notify(Guid projectId, Guid jobId, IngestJobUpdateKind kind) =>
        notifier.Notify(new IngestJobUpdate(projectId, jobId, kind, DateTime.UtcNow));
}