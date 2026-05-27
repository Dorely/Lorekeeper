using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Lorekeeper.Context;
using Lorekeeper.Llm;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence.Repositories;
using Lorekeeper.Tokens;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Lorekeeper.Ingest;

public sealed class IngestJobProcessor(
    IIngestRepository ingest,
    ILlmProviderService providerService,
    IChatClientFactory chatClientFactory,
    ITokenCounter tokenCounter,
    IngestAgentTools tools,
    IEntityTypeService entityTypes,
    IIngestVectorIndexingService ingestVectorIndexing,
    IIngestGraphSync graphSync,
    IIngestJobNotifier notifier,
    IContextIndexingService contextIndexing,
    IGraphNodeRepository nodes,
    IGraphEdgeRepository edges,
    IOptions<AgentOptions> options,
    ILogger<IngestJobProcessor> logger)
{
    private const string _systemPrompt = """
        You are an ingestion extraction agent for Lorekeeper.

        Your job is to read the current source chunk, which may or may not be part of a larger document, and append temporary staging observations for later wiki-style canon synthesis.
        You may link to existing non-structural project entities when the source clearly refers to the same thing.
        Prefer fewer, stronger entities over duplicate nodes for titles, aliases, partial names, or alternate spellings. Entity data should be useful for later retrieval and writing; it should read like an organized wiki sheet, not extraction notes.

        Adapt what counts as an entity to the source and the project. Fiction, science fiction, and fantasy sources should preserve story and setting continuity: characters, places, cultures, factions, artifacts, magic/technology, histories, rules, recurring terms, and relationships. Nonfiction and research sources should preserve concepts, people, events, examples, methods, terms, claims, source support, and arguments. Use the project type palette first, then create broad useful non-structural types only when the source needs them.

        A useful final wiki page captures the source-grounded information the current project would need later: identity, role, status, affiliation, history, motivation, significance, setting/world-building details, factual claims, examples, and relationships. During chunk ingestion, store this only as temporary staging records. A later final review pass will synthesize those records into a single canonSource markdown property for the whole source. The graph is only a sparse support structure.

        Process rules:
        - Start from the compact touched-entity index in the prompt. It is only an identity hint.
        - Use resolve_project_entity_mentions in batches for source mentions and likely variants before creating anything. Use list_project_entity_index when you need a broader name/alias roster.
        - Treat resolver results as candidate matches for your judgment, not automatic identity decisions. A shared title, honorific, role, epithet, or semantic similarity is not enough by itself to reuse or update an entity.
        - Resolve broadly, not just exactly: use the canonical singular type plus the exact mention, base name with titles/honorifics removed, known aliases, surnames, epithets, alternate spellings, and nearby descriptive terms from the local context. For example, "Prince Kael'thas" should resolve both "Prince Kael'thas" and "Kael'thas".
        - Treat title/honorific differences, punctuation/case differences, shortened names, aliases, and obvious same-subject references as the same entity when the source context supports it.
        - Do not create an entity when the touched-entity index or resolver returns a clear same subject with matching names or aliases. Call append_ingest_entity_observation with entityId for an existing match.
        - Do not update an existing entity just because it is semantically similar to the current source chunk. Update it only when the chunk explicitly supports a fact about that same entity.
        - Use append_ingest_entity_observation as the single entity write path. Supply entityId for an existing entity, or type and name only when creating/reusing a new entity in the same call.
        - append_ingest_entity_observation never edits canonical summaries, normal wiki sections, or canonSource properties. Do not try to rewrite the entity wiki during chunk ingestion.
        - Create a new entity only when no existing project entity or same-job entity matches after variant resolution. The tool will reject duplicate names; treat that as instruction to reuse the returned existing entity.
        - Use canonical singular entity type keys from the known project entity types. Do not invent plural, lowercase, or near-duplicate categories such as "characters", "Characters", "locations", or "organisations" when an existing project type reasonably fits.
        - If a new type is needed, choose a broad stable type name. Prefer reusable categories such as Culture, Faction, Artifact, Magic, Technology, Lore, Concept, Person, Event, Example, Claim, Term, or Method over one-off labels.
        - Keep recurring entities current by appending new source observations when the source supports new facts. Do not overwrite or supersede earlier observations.
        - Observation summaries should remain concise. Each meaningful touched entity should have source-backed observation sections rather than leaving detail only in relationships.
        - For story/worldbuilding sources, prefer this section palette when supported by the text: Overview, Role in This Text, Canon / World Role, Appearances & Timeline, Traits & Motivations, Relationships, Important Events, Memorable Quotes.
        - Only create sections with source-backed content. Memorable Quotes must use exact source text; do not paraphrase invented quotes.
        - Pass tool objects and arrays directly. Do not serialize aliases or wiki sections into JSON strings; use [] for no aliases or no sections.
        - Do not provide source ids, chunk ids, page labels, locator labels, or reference metadata; the tools record provenance automatically.
        - If the source does not support a fact, skip that fact.
        - Use append_ingest_relationship_observation sparingly, and only for durable high-signal source-backed relationship facts such as membership, family, command, location, direct conflict, ownership, or major causality. It accepts only endpoints and relationship type; all prose detail belongs in entity observation sections.
        - Relationship observations are staged during chunk ingestion and promoted to durable graph edges after the final source review succeeds. Link only entities already touched by this ingest job through entity observations.
        - Data-writing tools must be called one at a time. Before each append_ingest_entity_observation, append_ingest_relationship_observation, or update_ingest_source_progress call, first stream a short plain-text note explaining what you are about to record.
        - Finish each source chunk by calling update_ingest_source_progress exactly once with a concise completed chunk summary and the rolling source synopsis. Keep optional notes to one short operational sentence and keep the rolling source synopsis around 1500 words. After this tool returns, make no more tool calls and respond with a short plain-text completion note.
        """;

    public async Task RunAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        IngestJobChunk? activeChunk = null;

        try
        {
            var job = await ingest.GetJobProcessorDetailAsync(jobId, cancellationToken)
                ?? throw new InvalidOperationException($"Ingest job {jobId} not found.");

            if (job.Status is not (IngestJobStatus.Queued or IngestJobStatus.Running or IngestJobStatus.StopRequested))
                return;

            job.Status = IngestJobStatus.Running;
            job.StartedAt ??= DateTime.UtcNow;
            job.CompletedAt = null;
            job.ErrorMessage = null;
            job.CurrentMessage = "Preparing source.";
            job.UpdatedAt = DateTime.UtcNow;
            ingest.UpdateJob(job);
            await ingest.SaveChangesAsync(cancellationToken);
            Notify(job.ProjectId, job.Id, IngestJobUpdateKind.Progress);

            var provider = await ResolveJobProviderAsync(job, cancellationToken);

            await ingestVectorIndexing.EnsureVectorFragmentsAsync(job.Source, cancellationToken: cancellationToken);
            var sourceChunks = job.Chunks.Select(chunk => chunk.SourceChunk).OrderBy(chunk => chunk.Index).ToList();
            var sourceBlocks = await ingest.ListSourceBlocksAsync(job.SourceId, cancellationToken);
            await graphSync.EnsureSourceAsync(job.Source, sourceChunks, sourceBlocks, cancellationToken);

            var chat = await chatClientFactory.CreateChatClientAsync(provider.Id, cancellationToken);
            var maxIterations = Math.Max(1, options.Value.MaxToolIterations);

            foreach (var jobChunk in job.Chunks.OrderBy(chunk => chunk.SourceChunkIndex))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var latestJob = await ingest.GetJobAsync(job.Id, cancellationToken)
                    ?? throw new InvalidOperationException($"Ingest job {job.Id} not found.");
                if (latestJob.Status == IngestJobStatus.StopRequested)
                {
                    await MarkStoppedAsync(job.Id, activeChunk);
                    return;
                }
                if (jobChunk.Status == IngestJobChunkStatus.Completed)
                    continue;

                activeChunk = jobChunk;
                await ProcessChunkAsync(job, jobChunk, chat, maxIterations, cancellationToken);
                activeChunk = null;

                job.CompletedSourceChunks = job.Chunks.Count(chunk => chunk.Status == IngestJobChunkStatus.Completed);
                var reportItems = await ingest.ListStagingRecordsAsync(job.Id, cancellationToken);
                job.CreatedEntityCount = CountDistinctEntities(reportItems);
                job.CreatedRelationshipCount = CountDistinctRelationships(reportItems);
                job.CurrentMessage = $"Completed source chunk {jobChunk.SourceChunkIndex + 1} of {job.TotalSourceChunks}.";
                job.UpdatedAt = DateTime.UtcNow;
                ingest.UpdateJob(job);
                await ingest.SaveChangesAsync(cancellationToken);
                Notify(job.ProjectId, job.Id, IngestJobUpdateKind.Progress);
            }

            if (!await ProcessFinalReviewAsync(job, chat, maxIterations, cancellationToken))
                return;

            job.Status = IngestJobStatus.Completed;
            job.CompletedSourceChunks = job.TotalSourceChunks;
            job.CurrentMessage = "Completed.";
            job.CompletedAt = DateTime.UtcNow;
            job.UpdatedAt = DateTime.UtcNow;
            ingest.UpdateJob(job);
            await ingest.SaveChangesAsync(CancellationToken.None);
            Notify(job.ProjectId, job.Id, IngestJobUpdateKind.Completed);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation(ex, "Ingest job {JobId} was stopped or canceled.", jobId);
            await MarkStoppedAsync(jobId, activeChunk);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ingest job {JobId} failed", jobId);
            await MarkFailedAsync(jobId, activeChunk, ex.Message);
        }
    }

    private async Task ProcessChunkAsync(
        IngestJob job,
        IngestJobChunk jobChunk,
        IChatClient chat,
        int maxIterations,
        CancellationToken cancellationToken)
    {
        var sourceChunk = jobChunk.SourceChunk;
        jobChunk.Status = IngestJobChunkStatus.Running;
        jobChunk.StartedAt = DateTime.UtcNow;
        jobChunk.CompletedAt = null;
        jobChunk.ErrorMessage = null;
        ResetLlmTokenCount(jobChunk);
        jobChunk.UpdatedAt = DateTime.UtcNow;
        ingest.UpdateJobChunk(jobChunk);

        job.CurrentMessage = $"Processing source chunk {jobChunk.SourceChunkIndex + 1} of {job.TotalSourceChunks}: {sourceChunk.Title}";
        job.UpdatedAt = DateTime.UtcNow;
        ingest.UpdateJob(job);
        await ingest.SaveChangesAsync(cancellationToken);
        Notify(job.ProjectId, job.Id, IngestJobUpdateKind.Progress);

        string? finalText = null;
        var mutated = false;
        var maxAttempts = Math.Max(1, options.Value.IngestMaxTransientRetries);
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var context = new IngestAgentContext(
                    job.ProjectId,
                    job.Id,
                    job.SourceId,
                    job.Source.Title,
                    job.Source.SourceKind,
                    sourceChunk.Id,
                    sourceChunk.Index,
                    sourceChunk.Title,
                    OnMutated: () => mutated = true);

                NotifyLive(job.ProjectId, job.Id, new IngestLiveTurnStarted(sourceChunk.Id, sourceChunk.Index, sourceChunk.Title, attempt, maxAttempts));
                finalText = await RunChunkConversationAsync(job, jobChunk, context, chat, maxIterations, attempt, maxAttempts, cancellationToken);
                NotifyLive(job.ProjectId, job.Id, new IngestLiveTurnCompleted(sourceChunk.Id, sourceChunk.Index, sourceChunk.Title));
                break;
            }
            catch (Exception ex) when (IsRetryableLlmFailure(ex, cancellationToken) && attempt < maxAttempts)
            {
                var delayMs = RetryDelayMs(attempt);
                logger.LogWarning(ex, "Transient ingest LLM failure for job {JobId}, source chunk {SourceChunkIndex}, attempt {Attempt}/{MaxAttempts}. Retrying in {DelayMs} ms.", job.Id, jobChunk.SourceChunkIndex, attempt, maxAttempts, delayMs);
                await ingest.AddEventAsync(new IngestJobEvent
                {
                    JobId = job.Id,
                    Level = IngestJobEventLevel.Warning,
                    EventType = "llm.retry",
                    Message = $"Retrying source chunk {jobChunk.SourceChunkIndex + 1} after transient LLM error.",
                    PayloadJson = JsonSerializer.Serialize(new
                    {
                        sourceChunkId = sourceChunk.Id,
                        sourceChunkIndex = sourceChunk.Index,
                        attempt,
                        maxAttempts,
                        delayMs,
                        errorType = ex.GetType().FullName,
                        cancellationRequested = cancellationToken.IsCancellationRequested,
                        error = ex.Message,
                    }),
                }, cancellationToken);
                await ingest.SaveChangesAsync(cancellationToken);
                Notify(job.ProjectId, job.Id, IngestJobUpdateKind.Event);
                NotifyLive(job.ProjectId, job.Id, new IngestLiveRetryScheduled(sourceChunk.Id, sourceChunk.Index, sourceChunk.Title, attempt, maxAttempts, delayMs, ex.Message));
                await Task.Delay(delayMs, cancellationToken);
            }
            catch (Exception ex) when (!IsCancellation(ex, cancellationToken))
            {
                NotifyLiveTokenCount(job.ProjectId, job.Id, jobChunk);
                NotifyLive(job.ProjectId, job.Id, new IngestLiveTurnFailed(sourceChunk.Id, sourceChunk.Index, sourceChunk.Title, ex.Message));
                throw;
            }
        }

        var sourceGraphChanged = false;
        if (string.IsNullOrWhiteSpace(sourceChunk.Summary) && !string.IsNullOrWhiteSpace(finalText))
        {
            sourceChunk.Summary = Truncate(finalText, 800);
            sourceChunk.UpdatedAt = DateTime.UtcNow;
            ingest.UpdateSourceChunk(sourceChunk);
            sourceGraphChanged = true;
        }

        var reportItems = await ingest.ListStagingRecordsAsync(job.Id, cancellationToken);
        jobChunk.Status = IngestJobChunkStatus.Completed;
        jobChunk.Summary = sourceChunk.Summary;
        jobChunk.CreatedEntityCount = reportItems.Count(item => item.SourceChunkId == sourceChunk.Id && item.Kind == IngestStagingRecordKind.Entity && item.Status == IngestStagingRecordStatus.Active);
        jobChunk.CreatedRelationshipCount = reportItems.Count(item => item.SourceChunkId == sourceChunk.Id && item.Kind == IngestStagingRecordKind.Relationship && item.Status == IngestStagingRecordStatus.Active);
        jobChunk.CompletedAt = DateTime.UtcNow;
        jobChunk.UpdatedAt = DateTime.UtcNow;
        ingest.UpdateJobChunk(jobChunk);
        await ingest.SaveChangesAsync(cancellationToken);
        Notify(job.ProjectId, job.Id, IngestJobUpdateKind.Progress);

        if (mutated || sourceGraphChanged)
        {
            var sourceChunks = await ingest.ListSourceChunksAsync(job.SourceId, cancellationToken);
            var sourceBlocks = await ingest.ListSourceBlocksAsync(job.SourceId, cancellationToken);
            await graphSync.EnsureSourceAsync(job.Source, sourceChunks, sourceBlocks, cancellationToken);
            if (sourceGraphChanged)
                await contextIndexing.ReindexIngestSourceChunkAsync(sourceChunk.Id, cancellationToken);
        }
    }

    private async Task<bool> ProcessFinalReviewAsync(
        IngestJob job,
        IChatClient chat,
        int maxIterations,
        CancellationToken cancellationToken)
    {
        var reportItems = await ingest.ListStagingRecordsAsync(job.Id, cancellationToken);
        var touchedEntities = reportItems
            .Where(item => item.Status == IngestStagingRecordStatus.Active
                && item.Kind == IngestStagingRecordKind.Entity
                && item.EntityId is not null)
            .GroupBy(item => item.EntityId!.Value)
            .Select(group => group.OrderByDescending(item => item.UpdatedAt).First())
            .OrderBy(item => item.EntityType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (touchedEntities.Count == 0)
        {
            await PromoteFinalizedRelationshipStagingRecordsAsync(job, cancellationToken);
            await MarkRemainingStagingRecordsFinalizedAsync(job.Id, cancellationToken);
            Notify(job.ProjectId, job.Id, IngestJobUpdateKind.Report);
            return true;
        }

        await ingest.AddEventAsync(new IngestJobEvent
        {
            JobId = job.Id,
            Level = IngestJobEventLevel.Info,
            EventType = "llm.final_review_started",
            Message = $"Starting final source review for {touchedEntities.Count} touched entities.",
            PayloadJson = JsonSerializer.Serialize(new { entityCount = touchedEntities.Count }),
        }, cancellationToken);
        await ingest.SaveChangesAsync(cancellationToken);
        Notify(job.ProjectId, job.Id, IngestJobUpdateKind.Event);

        var maxAttempts = Math.Max(1, options.Value.IngestMaxTransientRetries);
        for (var index = 0; index < touchedEntities.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var latestJob = await ingest.GetJobAsync(job.Id, cancellationToken)
                ?? throw new InvalidOperationException($"Ingest job {job.Id} not found.");
            if (latestJob.Status == IngestJobStatus.StopRequested)
            {
                await MarkStoppedAsync(job.Id, activeChunk: null);
                return false;
            }

            var item = touchedEntities[index];
            var entityId = item.EntityId!.Value;
            var entityTitle = string.IsNullOrWhiteSpace(item.Title) ? entityId.ToString("N") : item.Title;
            var liveTitle = $"Final review: {entityTitle}";

            job.CurrentMessage = $"Final source review {index + 1} of {touchedEntities.Count}: {entityTitle}";
            job.UpdatedAt = DateTime.UtcNow;
            ingest.UpdateJob(job);
            await ingest.SaveChangesAsync(cancellationToken);
            Notify(job.ProjectId, job.Id, IngestJobUpdateKind.Progress);

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mutated = false;
                var context = new IngestFinalReviewContext(
                    job.ProjectId,
                    job.Id,
                    job.SourceId,
                    job.Source.Title,
                    job.Source.SourceKind,
                    entityId,
                    entityTitle,
                    item.EntityType,
                    OnMutated: () => mutated = true);

                NotifyLive(job.ProjectId, job.Id, new IngestLiveTurnStarted(entityId, -1, liveTitle, attempt, maxAttempts));
                try
                {
                    _ = await RunFinalReviewConversationAsync(job, context, chat, maxIterations, attempt, maxAttempts, cancellationToken);
                    if (!mutated)
                        throw new InvalidOperationException($"Final source review for {entityTitle} completed without calling write_ingest_source_wiki_section.");
                    await MarkFinalizedEntityStagingRecordsAsync(
                        job.Id,
                        entityId,
                        cancellationToken);
                    NotifyLive(job.ProjectId, job.Id, new IngestLiveTurnCompleted(entityId, -1, liveTitle));
                    Notify(job.ProjectId, job.Id, IngestJobUpdateKind.Report);
                    break;
                }
                catch (Exception ex) when (IsRetryableLlmFailure(ex, cancellationToken) && attempt < maxAttempts)
                {
                    var delayMs = RetryDelayMs(attempt);
                    logger.LogWarning(ex, "Transient final ingest review failure for job {JobId}, entity {EntityId}, attempt {Attempt}/{MaxAttempts}. Retrying in {DelayMs} ms.", job.Id, entityId, attempt, maxAttempts, delayMs);
                    await ingest.AddEventAsync(new IngestJobEvent
                    {
                        JobId = job.Id,
                        Level = IngestJobEventLevel.Warning,
                        EventType = "llm.final_review_retry",
                        Message = $"Retrying final source review for {entityTitle} after transient LLM error.",
                        PayloadJson = JsonSerializer.Serialize(new
                        {
                            entityId,
                            entityTitle,
                            attempt,
                            maxAttempts,
                            delayMs,
                            errorType = ex.GetType().FullName,
                            cancellationRequested = cancellationToken.IsCancellationRequested,
                            error = ex.Message,
                        }),
                    }, cancellationToken);
                    await ingest.SaveChangesAsync(cancellationToken);
                    Notify(job.ProjectId, job.Id, IngestJobUpdateKind.Event);
                    NotifyLive(job.ProjectId, job.Id, new IngestLiveRetryScheduled(entityId, -1, liveTitle, attempt, maxAttempts, delayMs, ex.Message));
                    await Task.Delay(delayMs, cancellationToken);
                }
                catch (Exception ex) when (!IsCancellation(ex, cancellationToken))
                {
                    NotifyLive(job.ProjectId, job.Id, new IngestLiveTurnFailed(entityId, -1, liveTitle, ex.Message));
                    throw;
                }
            }
        }

        await PromoteFinalizedRelationshipStagingRecordsAsync(job, cancellationToken);
        await MarkRemainingStagingRecordsFinalizedAsync(job.Id, cancellationToken);
        Notify(job.ProjectId, job.Id, IngestJobUpdateKind.Report);
        await ingest.AddEventAsync(new IngestJobEvent
        {
            JobId = job.Id,
            Level = IngestJobEventLevel.Info,
            EventType = "llm.final_review_completed",
            Message = $"Completed final source review for {touchedEntities.Count} touched entities.",
            PayloadJson = JsonSerializer.Serialize(new { entityCount = touchedEntities.Count }),
        }, cancellationToken);
        await ingest.SaveChangesAsync(cancellationToken);
        Notify(job.ProjectId, job.Id, IngestJobUpdateKind.Event);
        return true;
    }

    private async Task MarkFinalizedEntityStagingRecordsAsync(
        Guid jobId,
        Guid entityId,
        CancellationToken cancellationToken)
    {
        var reportItems = await ingest.ListStagingRecordsAsync(jobId, cancellationToken);
        var changed = false;

        foreach (var item in reportItems.Where(item => item.Status == IngestStagingRecordStatus.Active))
        {
            if (item.Kind == IngestStagingRecordKind.Entity && item.EntityId == entityId)
            {
                MarkStagingRecordFinalized(item);
                changed = true;
            }
        }

        if (changed)
            await ingest.SaveChangesAsync(cancellationToken);
    }

    private async Task PromoteFinalizedRelationshipStagingRecordsAsync(IngestJob job, CancellationToken cancellationToken)
    {
        var reportItems = await ingest.ListStagingRecordsAsync(job.Id, cancellationToken);
        var relationshipItems = reportItems
            .Where(item => item.Status == IngestStagingRecordStatus.Active && item.Kind == IngestStagingRecordKind.Relationship)
            .ToList();
        if (relationshipItems.Count == 0) return;

        var affectedEntityIds = new HashSet<Guid>();
        var promoted = 0;
        var skipped = 0;

        for (var index = 0; index < relationshipItems.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = relationshipItems[index];

            job.CurrentMessage = $"Building relationship links {index + 1} of {relationshipItems.Count}: {RelationshipPromotionLabel(item)}";
            job.UpdatedAt = DateTime.UtcNow;
            ingest.UpdateJob(job);
            await ingest.SaveChangesAsync(cancellationToken);
            Notify(job.ProjectId, job.Id, IngestJobUpdateKind.Progress);

            if (!TryReadRelationshipPromotionInput(item, job, out var input, out var skipReason))
            {
                skipped++;
                logger.LogWarning("Skipping ingest relationship staging record {StagingRecordId} during finalization: {Reason}", item.Id, skipReason);
                MarkStagingRecordFailed(item, skipReason);
                ingest.UpdateStagingRecord(item);
                await ingest.SaveChangesAsync(cancellationToken);
                Notify(job.ProjectId, job.Id, IngestJobUpdateKind.Report);
                continue;
            }

            var fromNode = await nodes.FindByKeyAsync(job.ProjectId, input.FromEntityId.ToString("N"), cancellationToken);
            var toNode = await nodes.FindByKeyAsync(job.ProjectId, input.ToEntityId.ToString("N"), cancellationToken);
            if (fromNode is null || toNode is null)
            {
                skipped++;
                logger.LogWarning("Skipping ingest relationship staging record {StagingRecordId} during finalization because one or both endpoints could not be resolved.", item.Id);
                MarkStagingRecordFailed(item, "one or both endpoints could not be resolved");
                ingest.UpdateStagingRecord(item);
                await ingest.SaveChangesAsync(cancellationToken);
                Notify(job.ProjectId, job.Id, IngestJobUpdateKind.Report);
                continue;
            }

            if (!IsPromotableRelationshipNode(fromNode) || !IsPromotableRelationshipNode(toNode))
            {
                skipped++;
                logger.LogWarning("Skipping ingest relationship staging record {StagingRecordId} during finalization because one or both endpoints are structural graph nodes.", item.Id);
                MarkStagingRecordFailed(item, "one or both endpoints are structural graph nodes");
                ingest.UpdateStagingRecord(item);
                await ingest.SaveChangesAsync(cancellationToken);
                Notify(job.ProjectId, job.Id, IngestJobUpdateKind.Report);
                continue;
            }

            var edge = await edges.FindAsync(fromNode.Id, toNode.Id, input.EdgeType, cancellationToken);
            var created = edge is null;
            if (edge is null)
            {
                edge = new GraphEdge
                {
                    FromNodeId = fromNode.Id,
                    ToNodeId = toNode.Id,
                    EdgeType = input.EdgeType,
                    Properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        [IngestSourceAssertions.GraphOriginProperty] = IngestSourceAssertions.GraphOriginIngestValue,
                    },
                };
            }

            IngestWikiSheet.AddCanonSourceProvenance(
                edge.Properties,
                input.SourceId,
                input.SourceTitle,
                input.SourceKind,
                job.Id);
            edge.UpdatedAt = DateTime.UtcNow;

            if (created)
            {
                await edges.AddAsync(edge, cancellationToken);
                await edges.SaveChangesAsync(cancellationToken);
            }
            else
            {
                edges.Update(edge);
            }

            item.GraphEdgeId = edge.Id;
            item.PayloadJson = UpdateRelationshipGraphAction(
                item.PayloadJson,
                created ? IngestSourceAssertions.CreatedEdgeAction : IngestSourceAssertions.LinkedExistingEdgeAction);
            MarkStagingRecordFinalized(item);
            ingest.UpdateStagingRecord(item);

            affectedEntityIds.Add(input.FromEntityId);
            affectedEntityIds.Add(input.ToEntityId);
            promoted++;
            await ingest.SaveChangesAsync(cancellationToken);
            Notify(job.ProjectId, job.Id, IngestJobUpdateKind.Report);
        }

        if (promoted > 0 || skipped > 0)
        {
            await ingest.AddEventAsync(new IngestJobEvent
            {
                JobId = job.Id,
                Level = skipped == 0 ? IngestJobEventLevel.Info : IngestJobEventLevel.Warning,
                EventType = "relationships.promoted",
                Message = skipped == 0
                    ? $"Promoted {promoted} staged relationships to graph edges."
                    : $"Promoted {promoted} staged relationships to graph edges; skipped {skipped}.",
                PayloadJson = JsonSerializer.Serialize(new { promoted, skipped }),
            }, cancellationToken);
        }

        await ingest.SaveChangesAsync(cancellationToken);
        Notify(job.ProjectId, job.Id, IngestJobUpdateKind.Report);

        foreach (var entityId in affectedEntityIds)
            await contextIndexing.ReindexEntityAsync(job.ProjectId, entityId, cancellationToken);
    }

    private async Task MarkRemainingStagingRecordsFinalizedAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var reportItems = await ingest.ListStagingRecordsAsync(jobId, cancellationToken);
        var changed = false;
        foreach (var item in reportItems.Where(item => item.Status == IngestStagingRecordStatus.Active))
        {
            MarkStagingRecordFinalized(item);
            changed = true;
        }

        if (changed)
            await ingest.SaveChangesAsync(cancellationToken);
    }

    private async Task<string?> RunChunkConversationAsync(
        IngestJob job,
        IngestJobChunk jobChunk,
        IngestAgentContext context,
        IChatClient chat,
        int maxIterations,
        int attempt,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        var sourceChunk = jobChunk.SourceChunk;
        var aiTools = tools.Build(context);
        var chatOptions = new ChatOptions
        {
            Tools = aiTools,
            ToolMode = ChatToolMode.Auto,
        };

        var chunkPrompt = await BuildChunkPromptAsync(job, sourceChunk, attempt, maxAttempts, cancellationToken);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _systemPrompt),
            new(ChatRole.User, chunkPrompt),
        };
        var tokenTracker = new IngestChunkTokenTracker(messages);
        var toolCallCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var peakTokenCount = 0;
        var iterationsRun = 0;
        string? streamExceptionType = null;
        string? streamExceptionMessage = null;
        bool? streamCancellationRequested = null;

        void ObserveTokenCount()
        {
            NotifyTokenCountIfChanged(job, jobChunk, tokenTracker);
            if (jobChunk.LlmTokenCount is int tokenCount)
                peakTokenCount = Math.Max(peakTokenCount, tokenCount);
        }

        ObserveTokenCount();

        string? finalText = null;
        try
        {
            for (var iteration = 0; iteration < maxIterations; iteration++)
            {
                iterationsRun = iteration + 1;
                cancellationToken.ThrowIfCancellationRequested();
                var assistantText = new StringBuilder();
                var pendingCalls = new List<PendingChunkToolCall>();
                var toolTracker = new StreamingToolCallTracker();
                tokenTracker.BeginAssistantTurn();
                Exception? streamFailure = null;

                var enumerator = chat.GetStreamingResponseAsync(messages, chatOptions, cancellationToken)
                                     .GetAsyncEnumerator(cancellationToken);
                try
                {
                    while (true)
                    {
                        bool hasNext;
                        try
                        {
                            hasNext = await enumerator.MoveNextAsync();
                        }
                        catch (Exception ex)
                        {
                            streamFailure = BuildStreamFailure(ex);
                            break;
                        }

                        if (!hasNext) break;

                        try
                        {
                            var contents = enumerator.Current?.Contents;
                            if (contents is null) continue;

                            foreach (var content in contents)
                            {
                                if (content is TextContent textContent && textContent.Text is { Length: > 0 } text)
                                {
                                    assistantText.Append(text);
                                    tokenTracker.AppendAssistantText(text);
                                    NotifyLive(job.ProjectId, job.Id, new IngestLiveTextDelta(sourceChunk.Id, sourceChunk.Index, sourceChunk.Title, text));
                                    ObserveTokenCount();
                                    continue;
                                }

                                foreach (var toolUpdate in toolTracker.Process(content, assistantText.Length))
                                {
                                    switch (toolUpdate)
                                    {
                                        case StreamingToolCallStartedUpdate started:
                                            tokenTracker.StartToolCall(started.CallId, started.ToolName, started.ArgumentsJson);
                                            NotifyLive(job.ProjectId, job.Id, new IngestLiveToolCallStarted(sourceChunk.Id, sourceChunk.Index, sourceChunk.Title, started.CallId, started.ToolName, started.ArgumentsJson, started.ArgumentsComplete));
                                            ObserveTokenCount();
                                            break;

                                        case StreamingToolCallArgumentsDeltaUpdate delta:
                                            tokenTracker.AppendToolArguments(delta.CallId, delta.ArgumentsDelta);
                                            NotifyLive(job.ProjectId, job.Id, new IngestLiveToolCallArgumentsDelta(sourceChunk.Id, sourceChunk.Index, sourceChunk.Title, delta.CallId, delta.ArgumentsDelta, delta.ArgumentsComplete));
                                            ObserveTokenCount();
                                            break;

                                        case StreamingToolCallReadyUpdate ready:
                                            tokenTracker.SetToolCallArguments(ready.CallId, ready.ToolName, ready.ArgumentsJson);
                                            pendingCalls.Add(new PendingChunkToolCall(ready.Content, ready.CallId, ready.ToolName, ready.ArgumentsJson, ready.TextOffset));
                                            ObserveTokenCount();
                                            break;
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            streamFailure = BuildStreamFailure(ex);
                            break;
                        }
                    }
                }
                finally
                {
                    try
                    {
                        await enumerator.DisposeAsync();
                    }
                    catch (Exception ex) when (streamFailure is null)
                    {
                        streamFailure = BuildStreamFailure(ex);
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Ingest LLM streaming enumerator disposal failed after an earlier stream failure for job {JobId}, source chunk {SourceChunkIndex}.", job.Id, sourceChunk.Index);
                    }
                }

                if (streamFailure is not null)
                    ExceptionDispatchInfo.Capture(streamFailure).Throw();

                var assistantMessage = new ChatMessage(ChatRole.Assistant, BuildAssistantContents(assistantText.ToString(), pendingCalls));
                messages.Add(assistantMessage);
                tokenTracker.CommitAssistantMessage();
                ObserveTokenCount();

                if (pendingCalls.Count == 0)
                {
                    finalText = assistantText.ToString();
                    break;
                }

                var resultContents = new List<AIContent>();
                var writeToolCallCount = pendingCalls.Count(call => IsChunkWriteTool(call.Name));
                foreach (var pendingCall in pendingCalls)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    toolCallCounts[pendingCall.Name] = toolCallCounts.GetValueOrDefault(pendingCall.Name) + 1;
                    var stopwatch = Stopwatch.StartNew();
                    var toolResult = writeToolCallCount > 1 && IsChunkWriteTool(pendingCall.Name)
                        ? $"Error: Data-writing tools must be called one at a time. Stream a short note, then call only {pendingCall.Name}; wait for the result before calling another write tool."
                        : await InvokeToolAsync(aiTools, pendingCall.Content, job.ProjectId, job.Id, cancellationToken);
                    stopwatch.Stop();

                    var toolError = toolResult.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)
                        ? toolResult
                        : null;
                    NotifyLive(job.ProjectId, job.Id, new IngestLiveToolCallCompleted(
                        sourceChunk.Id,
                        sourceChunk.Index,
                        sourceChunk.Title,
                        pendingCall.CallId,
                        pendingCall.Name,
                        toolError is null ? toolResult : null,
                        toolError,
                        stopwatch.Elapsed.TotalMilliseconds));

                    tokenTracker.AddToolResult(pendingCall.CallId, toolResult);
                    ObserveTokenCount();
                    resultContents.Add(new FunctionResultContent(pendingCall.CallId, toolResult));
                }

                messages.Add(new ChatMessage(ChatRole.Tool, resultContents));
                tokenTracker.CommitToolMessage();
                ObserveTokenCount();
                if (iteration == maxIterations - 1)
                    throw new InvalidOperationException($"Ingest tool-call loop hit configured cap of {maxIterations} iterations without completing source chunk {sourceChunk.Index}.");
            }

            return finalText;
        }
        finally
        {
            await RecordChunkDiagnosticsAsync(
                job,
                sourceChunk,
                attempt,
                maxAttempts,
                iterationsRun,
                toolCallCounts,
                peakTokenCount,
                streamExceptionType,
                streamExceptionMessage,
                streamCancellationRequested);
        }

        Exception BuildStreamFailure(Exception exception)
        {
            streamExceptionType = exception.GetType().FullName;
            streamExceptionMessage = exception.Message;
            streamCancellationRequested = cancellationToken.IsCancellationRequested;

            if (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return new TimeoutException(
                    "Ingest LLM streaming was cancelled by the provider or timed out before the turn completed.",
                    exception);
            }

            return exception;
        }
    }

    private async Task<string?> RunFinalReviewConversationAsync(
        IngestJob job,
        IngestFinalReviewContext context,
        IChatClient chat,
        int maxIterations,
        int attempt,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        var liveSourceChunkId = context.EntityId;
        const int liveSourceChunkIndex = -1;
        var liveTitle = $"Final review: {context.EntityName}";
        var aiTools = tools.BuildFinalReview(context);
        var chatOptions = new ChatOptions
        {
            Tools = aiTools,
            ToolMode = ChatToolMode.Auto,
        };
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, BuildFinalReviewSystemPrompt()),
            new(ChatRole.User, BuildFinalReviewPrompt(job, context, attempt, maxAttempts)),
        };

        string? finalText = null;
        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var assistantText = new StringBuilder();
            var pendingCalls = new List<PendingChunkToolCall>();
            var toolTracker = new StreamingToolCallTracker();
            Exception? streamFailure = null;

            var enumerator = chat.GetStreamingResponseAsync(messages, chatOptions, cancellationToken)
                                 .GetAsyncEnumerator(cancellationToken);
            try
            {
                while (true)
                {
                    bool hasNext;
                    try
                    {
                        hasNext = await enumerator.MoveNextAsync();
                    }
                    catch (Exception ex)
                    {
                        streamFailure = BuildStreamFailure(ex);
                        break;
                    }

                    if (!hasNext) break;

                    try
                    {
                        var contents = enumerator.Current?.Contents;
                        if (contents is null) continue;

                        foreach (var content in contents)
                        {
                            if (content is TextContent textContent && textContent.Text is { Length: > 0 } text)
                            {
                                assistantText.Append(text);
                                NotifyLive(job.ProjectId, job.Id, new IngestLiveTextDelta(liveSourceChunkId, liveSourceChunkIndex, liveTitle, text));
                                continue;
                            }

                            foreach (var toolUpdate in toolTracker.Process(content, assistantText.Length))
                            {
                                switch (toolUpdate)
                                {
                                    case StreamingToolCallStartedUpdate started:
                                        NotifyLive(job.ProjectId, job.Id, new IngestLiveToolCallStarted(liveSourceChunkId, liveSourceChunkIndex, liveTitle, started.CallId, started.ToolName, started.ArgumentsJson, started.ArgumentsComplete));
                                        break;

                                    case StreamingToolCallArgumentsDeltaUpdate delta:
                                        NotifyLive(job.ProjectId, job.Id, new IngestLiveToolCallArgumentsDelta(liveSourceChunkId, liveSourceChunkIndex, liveTitle, delta.CallId, delta.ArgumentsDelta, delta.ArgumentsComplete));
                                        break;

                                    case StreamingToolCallReadyUpdate ready:
                                        pendingCalls.Add(new PendingChunkToolCall(ready.Content, ready.CallId, ready.ToolName, ready.ArgumentsJson, ready.TextOffset));
                                        break;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        streamFailure = BuildStreamFailure(ex);
                        break;
                    }
                }
            }
            finally
            {
                try
                {
                    await enumerator.DisposeAsync();
                }
                catch (Exception ex) when (streamFailure is null)
                {
                    streamFailure = BuildStreamFailure(ex);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Final ingest review streaming enumerator disposal failed after an earlier stream failure for job {JobId}, entity {EntityId}.", job.Id, context.EntityId);
                }
            }

            if (streamFailure is not null)
                ExceptionDispatchInfo.Capture(streamFailure).Throw();

            messages.Add(new ChatMessage(ChatRole.Assistant, BuildAssistantContents(assistantText.ToString(), pendingCalls)));
            if (pendingCalls.Count == 0)
            {
                finalText = assistantText.ToString();
                break;
            }

            var resultContents = new List<AIContent>();
            var writeToolCallCount = pendingCalls.Count(call => IsFinalReviewWriteTool(call.Name));
            foreach (var pendingCall in pendingCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stopwatch = Stopwatch.StartNew();
                var toolResult = writeToolCallCount > 1 && IsFinalReviewWriteTool(pendingCall.Name)
                    ? "Error: write_ingest_source_wiki_section must be called at most once in this entity review. Read observations first, stream a short note, then call the writer once."
                    : await InvokeToolAsync(aiTools, pendingCall.Content, job.ProjectId, job.Id, cancellationToken);
                stopwatch.Stop();

                var toolError = toolResult.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)
                    ? toolResult
                    : null;
                NotifyLive(job.ProjectId, job.Id, new IngestLiveToolCallCompleted(
                    liveSourceChunkId,
                    liveSourceChunkIndex,
                    liveTitle,
                    pendingCall.CallId,
                    pendingCall.Name,
                    toolError is null ? toolResult : null,
                    toolError,
                    stopwatch.Elapsed.TotalMilliseconds));

                resultContents.Add(new FunctionResultContent(pendingCall.CallId, toolResult));
            }

            messages.Add(new ChatMessage(ChatRole.Tool, resultContents));
            if (iteration == maxIterations - 1)
                throw new InvalidOperationException($"Final ingest review for entity {context.EntityName} hit configured cap of {maxIterations} iterations without producing a final response.");
        }

        return finalText;

        Exception BuildStreamFailure(Exception exception)
        {
            if (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return new TimeoutException(
                    "Final ingest review LLM streaming was cancelled by the provider or timed out before the turn completed.",
                    exception);
            }

            return exception;
        }
    }

    private static string BuildFinalReviewSystemPrompt() => """
        You are the final source review agent for Lorekeeper ingestion.

        You work on exactly one touched entity from exactly one ingested source. Your task is to synthesize the staging observations into one readable markdown wiki page for that source only.

        Rules:
        - First call read_ingest_entity_source_observations.
        - Then write exactly one markdown page with write_ingest_source_wiki_section.
        - Before calling the writer, stream a short plain-text note explaining that you are writing the source-backed canon page.
        - The property key is controlled by the tool and will be canonSource.{source-title-slug}.
        - The body should read like a compact markdown wiki page for this entity in this text: who they are in this source, where they appear, why they matter in this text, important events, relationships, traits or motivations, and memorable exact quotes when present.
        - Use only information from the source observations. Do not alter or summarize canonical project knowledge outside this source.
        - Write only useful source-backed content for this entity.
        - After the writer returns, make no more tool calls and return a short plain-text completion note.
        """;

    private static string BuildFinalReviewPrompt(
        IngestJob job,
        IngestFinalReviewContext context,
        int attempt,
        int maxAttempts) => $$"""
        Source title: {{context.SourceTitle}}
        Source kind: {{context.SourceKind}}
        Entity: {{context.EntityType}} '{{context.EntityName}}' ({{context.EntityId}})
        Review attempt: {{attempt}} of {{maxAttempts}}

        Build one source-specific markdown wiki page for this entity from this ingest source.
        Call read_ingest_entity_source_observations first. Then call write_ingest_source_wiki_section exactly once.
        Preserve all existing canonical/manual entity information by only using the writer tool.
        """;

    private async Task RecordChunkDiagnosticsAsync(
        IngestJob job,
        IngestSourceChunk sourceChunk,
        int attempt,
        int maxAttempts,
        int iterationsRun,
        IReadOnlyDictionary<string, int> toolCallCounts,
        int peakTokenCount,
        string? streamExceptionType,
        string? streamExceptionMessage,
        bool? streamCancellationRequested)
    {
        try
        {
            await ingest.AddEventAsync(new IngestJobEvent
            {
                JobId = job.Id,
                Level = IngestJobEventLevel.Debug,
                EventType = "llm.chunk_diagnostics",
                Message = $"Source chunk {sourceChunk.Index + 1} diagnostics",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    sourceChunkId = sourceChunk.Id,
                    sourceChunkIndex = sourceChunk.Index,
                    attempt,
                    maxAttempts,
                    iterationsRun,
                    totalToolCalls = toolCallCounts.Values.Sum(),
                    toolCallCounts,
                    peakModelFacingTokenCount = peakTokenCount,
                    streamExceptionType,
                    streamExceptionMessage,
                    streamCancellationRequested,
                }),
            }, CancellationToken.None);
            await ingest.SaveChangesAsync(CancellationToken.None);
            Notify(job.ProjectId, job.Id, IngestJobUpdateKind.Event);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to record ingest chunk diagnostics for job {JobId}, source chunk {SourceChunkIndex}.", job.Id, sourceChunk.Index);
        }
    }

    private void NotifyTokenCountIfChanged(IngestJob job, IngestJobChunk jobChunk, IngestChunkTokenTracker tokenTracker)
    {
        var result = tokenTracker.Count(tokenCounter, job.ModelName, job.EncodingName);
        var changed = jobChunk.LlmTokenCount != result.TokenCount
            || jobChunk.LlmTokenCountIsExact != result.IsExact
            || !string.Equals(jobChunk.LlmTokenCountMethod, result.Method, StringComparison.Ordinal)
            || !string.Equals(jobChunk.LlmTokenEncodingName, result.EncodingName, StringComparison.Ordinal);

        ApplyLlmTokenCount(jobChunk, result);
        if (changed)
            NotifyLiveTokenCount(job.ProjectId, job.Id, jobChunk);
    }

    private void NotifyLiveTokenCount(Guid projectId, Guid jobId, IngestJobChunk jobChunk)
    {
        if (jobChunk.LlmTokenCount is not int tokenCount)
            return;

        var sourceChunk = jobChunk.SourceChunk;
        NotifyLive(projectId, jobId, new IngestLiveTokenCountUpdated(
            sourceChunk.Id,
            sourceChunk.Index,
            sourceChunk.Title,
            tokenCount,
            jobChunk.LlmTokenCountIsExact ?? false,
            jobChunk.LlmTokenCountMethod ?? string.Empty,
            jobChunk.LlmTokenEncodingName));
    }

    private static void ApplyLlmTokenCount(IngestJobChunk jobChunk, TokenCountResult result)
    {
        jobChunk.LlmTokenCount = result.TokenCount;
        jobChunk.LlmTokenCountIsExact = result.IsExact;
        jobChunk.LlmTokenCountMethod = result.Method;
        jobChunk.LlmTokenEncodingName = result.EncodingName;
    }

    private static void ResetLlmTokenCount(IngestJobChunk jobChunk)
    {
        jobChunk.LlmTokenCount = null;
        jobChunk.LlmTokenCountIsExact = null;
        jobChunk.LlmTokenCountMethod = null;
        jobChunk.LlmTokenEncodingName = null;
    }

    private async Task<string> InvokeToolAsync(IList<AITool> aiTools, FunctionCallContent functionCall, Guid projectId, Guid jobId, CancellationToken cancellationToken)
    {
        var argsJson = ToolCallArguments.Serialize(functionCall.Arguments);
        var startedAt = DateTime.UtcNow;
        try
        {
            var aiFunction = aiTools.OfType<AIFunction>().FirstOrDefault(function => function.Name == functionCall.Name);
            if (aiFunction is null)
            {
                var unknownToolMessage = $"Unknown ingest tool '{functionCall.Name}'.";
                logger.LogWarning("{Message} Job {JobId}. Arguments: {ArgumentsJson}", unknownToolMessage, jobId, argsJson);
                await ingest.AddEventAsync(new IngestJobEvent
                {
                    JobId = jobId,
                    Level = IngestJobEventLevel.Warning,
                    EventType = "tool.unknown",
                    Message = functionCall.Name,
                    PayloadJson = JsonSerializer.Serialize(new { arguments = argsJson, error = unknownToolMessage, startedAt, completedAt = DateTime.UtcNow }),
                }, cancellationToken);
                await ingest.SaveChangesAsync(cancellationToken);
                Notify(projectId, jobId, IngestJobUpdateKind.Event);
                return $"Error: {unknownToolMessage}";
            }

            var result = await aiFunction.InvokeAsync(ToolCallArguments.Create(functionCall.Arguments), cancellationToken);
            var text = result?.ToString() ?? string.Empty;
            var returnedError = text.StartsWith("Error:", StringComparison.OrdinalIgnoreCase);
            if (returnedError)
                logger.LogWarning("Ingest tool {ToolName} returned an error for job {JobId}: {ToolResult}. Arguments: {ArgumentsJson}", functionCall.Name, jobId, text, argsJson);

            await ingest.AddEventAsync(new IngestJobEvent
            {
                JobId = jobId,
                Level = returnedError ? IngestJobEventLevel.Warning : IngestJobEventLevel.Debug,
                EventType = returnedError ? "tool.returned_error" : "tool.completed",
                Message = functionCall.Name,
                PayloadJson = JsonSerializer.Serialize(new { arguments = argsJson, result = text, startedAt, completedAt = DateTime.UtcNow }),
            }, cancellationToken);
            await ingest.SaveChangesAsync(cancellationToken);
            Notify(projectId, jobId, returnedError ? IngestJobUpdateKind.Event : IngestJobUpdateKind.Report);
            return text;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ingest tool {ToolName} failed for job {JobId}. Arguments: {ArgumentsJson}", functionCall.Name, jobId, argsJson);
            await ingest.AddEventAsync(new IngestJobEvent
            {
                JobId = jobId,
                Level = IngestJobEventLevel.Warning,
                EventType = "tool.failed",
                Message = functionCall.Name,
                PayloadJson = JsonSerializer.Serialize(new { arguments = argsJson, error = ex.Message, startedAt, completedAt = DateTime.UtcNow }),
            }, cancellationToken);
            await ingest.SaveChangesAsync(cancellationToken);
            Notify(projectId, jobId, IngestJobUpdateKind.Event);
            return $"Error: {ex.Message}";
        }
    }

    private async Task<LlmProvider> ResolveJobProviderAsync(IngestJob job, CancellationToken cancellationToken)
    {
        if (job.ProviderId is int providerId)
        {
            var provider = await providerService.GetByIdAsync(providerId, cancellationToken);
            if (provider is not null && await providerService.IsChatProviderWorkingAsync(provider.Id, cancellationToken))
                return provider;

            var availability = await providerService.GetDefaultChatProviderAvailabilityAsync(cancellationToken);
            var fallback = availability.Provider;
            if (!availability.IsAvailable || fallback is null)
            {
                var reason = provider is null
                    ? $"LLM provider {providerId} was not found"
                    : $"LLM provider {providerId} is not ready";
                throw new InvalidOperationException($"{reason}, and no working default LLM provider is configured. {availability.Message}");
            }

            await ingest.AddEventAsync(new IngestJobEvent
            {
                JobId = job.Id,
                Level = IngestJobEventLevel.Warning,
                EventType = "provider.fallback",
                Message = provider is null
                    ? $"Configured provider {providerId} was not found; using {fallback.Name}."
                    : $"Configured provider {provider.Name} is not ready; using {fallback.Name}.",
                PayloadJson = JsonSerializer.Serialize(new { providerId, fallbackProviderId = fallback.Id, fallback.ModelId }),
            }, cancellationToken);
            job.ProviderId = fallback.Id;
            job.ModelName = fallback.ModelId;
            job.UpdatedAt = DateTime.UtcNow;
            ingest.UpdateJob(job);
            await ingest.SaveChangesAsync(cancellationToken);
            Notify(job.ProjectId, job.Id, IngestJobUpdateKind.Event);
            return fallback;
        }

        var defaultAvailability = await providerService.GetDefaultChatProviderAvailabilityAsync(cancellationToken);
        var defaultProvider = defaultAvailability.Provider;
        if (!defaultAvailability.IsAvailable || defaultProvider is null)
            throw new InvalidOperationException(defaultAvailability.Message);

        job.ProviderId = defaultProvider.Id;
        job.ModelName = defaultProvider.ModelId;
        job.UpdatedAt = DateTime.UtcNow;
        ingest.UpdateJob(job);
        await ingest.SaveChangesAsync(cancellationToken);
        Notify(job.ProjectId, job.Id, IngestJobUpdateKind.Progress);
        return defaultProvider;
    }

    private async Task<string> BuildChunkPromptAsync(
        IngestJob job,
        IngestSourceChunk sourceChunk,
        int attempt,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        var sourceText = job.Source.SourceText;
        var start = Math.Clamp(sourceChunk.StartChar, 0, sourceText.Length);
        var end = Math.Clamp(sourceChunk.EndChar, start, sourceText.Length);
        var currentText = sourceText[start..end];
        var entityRoster = await BuildTouchedEntityIndexAsync(job.Id, sourceChunkId: null, cancellationToken);
        var currentChunkEntityRoster = await BuildTouchedEntityIndexAsync(job.Id, sourceChunk.Id, cancellationToken);
        var chunkProgress = BuildChunkProgressMap(job, sourceChunk.Index);
        var knownEntityTypes = await BuildKnownEntityTypesAsync(job.ProjectId, cancellationToken);
        var extractionProfile = BuildExtractionProfilePrompt(job);

        return $$"""
            Source title: {{job.Source.Title}}
            Source kind: {{job.Source.SourceKind}}
            Source description: {{job.Source.Description}}
            Extraction profile:
            {{extractionProfile}}

            User extraction instructions:
            {{job.Instructions}}

            Rolling source synopsis:
            {{(string.IsNullOrWhiteSpace(job.Source.Synopsis) ? "None yet." : TruncateWords(job.Source.Synopsis.Trim(), 1500))}}

            Current ingest step:
            - Processing chunk {{sourceChunk.Index + 1}} of {{job.TotalSourceChunks}}
            - Current phase: source-grounded wiki-sheet extraction
            - Retry attempt: {{attempt}} of {{maxAttempts}}

            Current source chunk:
            - Index: {{sourceChunk.Index + 1}} of {{job.TotalSourceChunks}}
            - Title: {{sourceChunk.Title}}
            - Heading path: {{sourceChunk.HeadingPath}}
            - Estimated tokens: {{sourceChunk.EstimatedTokenCount}} ({{sourceChunk.TokenCountMethod}}{{(sourceChunk.TokenCountIsExact ? ", exact" : ", estimated")}})

            Chunk progress map:
            {{chunkProgress}}

            Compact touched-entity index:
            {{entityRoster}}

            Current-chunk entities already touched by earlier attempts or partial work:
            {{currentChunkEntityRoster}}

            Known project entity types:
            {{knownEntityTypes}}

            Entity matching workflow for this chunk:
            1. Start from the compact touched-entity index above.
            2. Batch likely source mentions through resolve_project_entity_mentions before creating anything.
            3. Use the canonical singular type and multiple query variants: exact mention, base name without titles/honorifics, aliases, surnames, epithets, alternate spellings, and nearby descriptive terms.
            4. Reuse a plausible same-job or project entity instead of creating duplicate names or duplicate categories. For example, link "Prince Kael'thas" observations to an existing "Kael'thas" Character when the context points to the same person.
            5. Create only when the touched-entity index and resolver do not return a plausible same subject.

            Write useful, source-grounded staging observations. For each meaningful touched entity, call append_ingest_entity_observation once with a concise source-backed summary, source-mentioned aliases, and non-empty observation sections. Use entityId for an existing entity, or type and name only when no existing entity matches. Do not include source ids, chunk ids, page labels, locator labels, or reference metadata. These observations are temporary for this source and chunk; do not read or rewrite existing wiki sections during chunk ingestion.

            Default story/worldbuilding wiki section palette, when supported: Overview; Role in This Text; Canon / World Role; Appearances & Timeline; Traits & Motivations; Relationships; Important Events; Memorable Quotes.

            Keep relationship observations minimal. Use append_ingest_relationship_observation only for durable facts such as membership, family, command, location, direct conflict, ownership, or major causality. It accepts only fromEntityId, toEntityId, and edgeType. Put ordinary relationship nuance and narrative detail into entity observation sections.

            Good example: if a Blood Elves page says Liadrin leads the blood elf paladins and is one of the race's primary leaders, record that as role/significance/history sections on the Liadrin Character. Bad example: do not write that Liadrin was updated because the page was semantically similar or because the page did not mention her.

            Tool cadence requirement: data-writing tools must be called one at a time. Before each append_ingest_entity_observation, append_ingest_relationship_observation, or update_ingest_source_progress call, stream a short plain-text note that will be visible in the UI.

            Completion requirement: when this chunk is fully processed, call update_ingest_source_progress exactly once with a concise chunk summary, updated rolling source synopsis, and only a short operational note if needed. After that tool returns, do not call any more tools; return a short plain-text completion note.

            Current source chunk text:
            ```text
            {{currentText}}
            ```
            """;
    }

    private async Task<string> BuildTouchedEntityIndexAsync(Guid jobId, Guid? sourceChunkId, CancellationToken cancellationToken)
    {
        var reportItems = await ingest.ListStagingRecordsAsync(jobId, cancellationToken);
        var entities = reportItems
            .Where(item => item.Kind == IngestStagingRecordKind.Entity && item.Status == IngestStagingRecordStatus.Active)
            .Where(item => item.EntityId is not null)
            .Where(item => sourceChunkId is null || item.SourceChunkId == sourceChunkId)
            .GroupBy(item => item.EntityId!.Value)
            .Select(group => group.OrderByDescending(item => item.UpdatedAt).First())
            .OrderBy(item => item.EntityType)
            .ThenBy(item => item.Title)
            .Take(300)
            .Select(item =>
            {
                var aliases = ReadStagingAliases(item.AliasesJson);
                var aliasText = aliases.Count == 0 ? string.Empty : $" | aliases: {string.Join(", ", aliases.Take(8))}";
                return $"- {item.EntityId}: {item.EntityType} '{item.Title}'{aliasText}";
            })
            .ToList();

        return entities.Count == 0 ? "None yet." : string.Join("\n", entities);
    }

    private static string BuildChunkProgressMap(IngestJob job, int currentChunkIndex)
    {
        var chunks = job.Chunks
            .OrderBy(chunk => chunk.SourceChunkIndex)
            .ToList();

        var statusLines = chunks
            .Where(chunk =>
                chunk.SourceChunkIndex == 0
                || chunk.SourceChunkIndex == chunks[^1].SourceChunkIndex
                || Math.Abs(chunk.SourceChunkIndex - currentChunkIndex) <= 4
                || chunk.Status is IngestJobChunkStatus.Running or IngestJobChunkStatus.Failed or IngestJobChunkStatus.Stopped)
            .Select(chunk =>
            {
                var sourceChunk = chunk.SourceChunk;
                var title = string.IsNullOrWhiteSpace(sourceChunk.Title) ? $"Part {sourceChunk.Index + 1}" : sourceChunk.Title;
                return $"- {sourceChunk.Index + 1}. {title} | {chunk.Status}";
            })
            .ToList();

        var recentSummaries = chunks
            .Where(chunk => chunk.SourceChunkIndex < currentChunkIndex && chunk.Status == IngestJobChunkStatus.Completed)
            .OrderByDescending(chunk => chunk.SourceChunkIndex)
            .Take(5)
            .OrderBy(chunk => chunk.SourceChunkIndex)
            .Select(chunk =>
            {
                var sourceChunk = chunk.SourceChunk;
                var summary = !string.IsNullOrWhiteSpace(sourceChunk.Summary)
                    ? sourceChunk.Summary
                    : chunk.Summary;
                return string.IsNullOrWhiteSpace(summary)
                    ? string.Empty
                    : $"- {sourceChunk.Index + 1}. {Truncate(summary, 180)}";
            })
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        if (statusLines.Count == 0)
            return "No source chunks were generated.";

        var completed = chunks.Count(chunk => chunk.Status == IngestJobChunkStatus.Completed);
        var sb = new StringBuilder();
        sb.Append("Progress: ").Append(completed).Append(" completed of ").Append(chunks.Count).AppendLine(".");
        sb.AppendLine("Visible chunk statuses:");
        sb.AppendLine(string.Join("\n", statusLines));
        if (recentSummaries.Count > 0)
        {
            sb.AppendLine("Recent completed summaries:");
            sb.AppendLine(string.Join("\n", recentSummaries));
        }

        return sb.ToString().TrimEnd();
    }

    private static IReadOnlyList<string> ReadStagingAliases(string? aliasesJson)
    {
        if (string.IsNullOrWhiteSpace(aliasesJson) || !aliasesJson.TrimStart().StartsWith('['))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(aliasesJson);
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray()
                    .Select(alias => alias.ValueKind == JsonValueKind.String ? alias.GetString() : alias.GetRawText())
                    .Where(alias => !string.IsNullOrWhiteSpace(alias))
                    .Select(alias => alias!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> ReadPayloadAliases(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson) || !payloadJson.TrimStart().StartsWith('{'))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (!doc.RootElement.TryGetProperty("aliases", out var aliases) || aliases.ValueKind != JsonValueKind.Array)
                return [];

            return aliases
                .EnumerateArray()
                .Select(alias => alias.ValueKind == JsonValueKind.String ? alias.GetString() : alias.GetRawText())
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .Select(alias => alias!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<IngestWikiSectionInput> ReadStagingWikiSections(string? sectionsJson)
    {
        if (string.IsNullOrWhiteSpace(sectionsJson) || !sectionsJson.TrimStart().StartsWith('['))
            return [];

        try
        {
            return (JsonSerializer.Deserialize<List<IngestWikiSectionInput>>(sectionsJson) ?? [])
                .Where(section => section is not null
                    && (!string.IsNullOrWhiteSpace(section.Title) || !string.IsNullOrWhiteSpace(section.Body)))
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> ReadMergedStagingAliases(IngestStagingRecord item)
    {
        var aliases = ReadStagingAliases(item.AliasesJson);
        return aliases.Count > 0
            ? aliases
            : ReadPayloadAliases(item.PayloadJson);
    }

    private static string StagingRecordObservationText(IngestStagingRecord item)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(item.Summary))
            sb.AppendLine(item.Summary.Trim());
        foreach (var section in ReadStagingWikiSections(item.WikiSectionsJson))
        {
            if (!string.IsNullOrWhiteSpace(section.Title))
                sb.AppendLine(section.Title.Trim());
            if (!string.IsNullOrWhiteSpace(section.Body))
                sb.AppendLine(section.Body.Trim());
        }
        if (!string.IsNullOrWhiteSpace(item.Notes))
            sb.AppendLine(item.Notes.Trim());
        return sb.ToString().Trim();
    }

    private static string TruncateWords(string value, int maxWords)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var words = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length <= maxWords) return value.Trim();
        return string.Join(' ', words.Take(maxWords)) + "...";
    }

    private async Task<string> BuildKnownEntityTypesAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var definitions = await entityTypes.ListAsync(projectId, includeStructural: false, cancellationToken);
        var lines = definitions
            .Where(definition => !definition.IsStructural && !definition.IsChapterScoped)
            .OrderBy(definition => definition.SortOrder)
            .ThenBy(definition => definition.Type, StringComparer.OrdinalIgnoreCase)
            .Select(definition => $"- {definition.Type} (singular: {definition.SingularLabel}; plural label: {definition.PluralLabel})")
            .ToList();

        return lines.Count == 0 ? "None registered yet." : string.Join("\n", lines);
    }

    private static string BuildExtractionProfilePrompt(IngestJob job)
    {
        var profile = ReadExtractionProfile(job.Source.SourceMetadataJson);
        var profileDescription = profile switch
        {
            IngestExtractionProfile.StoryWorldbuilding => "Story / Worldbuilding. Prioritize continuity knowledge for fiction: characters, places, cultures, factions, lore, history, objects, rules, and relationships.",
            IngestExtractionProfile.ResearchNonfiction => "Research / Nonfiction. Prioritize concepts, people, historical events, examples, claims, arguments, terms, methods, and source-backed support.",
            _ => "Auto. Infer the source domain from the title, kind, metadata, user instructions, and chunk text. Reuse project-specific entity types first.",
        };

        return $"{profile}: {profileDescription}";
    }

    private static IngestExtractionProfile ReadExtractionProfile(string metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson) || !metadataJson.TrimStart().StartsWith('{'))
            return IngestExtractionProfile.Auto;

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (doc.RootElement.TryGetProperty("extractionProfile", out var direct)
                && direct.ValueKind == JsonValueKind.String
                && Enum.TryParse<IngestExtractionProfile>(direct.GetString(), out var parsed))
            {
                return parsed;
            }

            if (doc.RootElement.TryGetProperty("artifactPreprocess", out var artifact)
                && artifact.ValueKind == JsonValueKind.Object
                && artifact.TryGetProperty("extractionProfile", out var nested)
                && nested.ValueKind == JsonValueKind.String
                && Enum.TryParse<IngestExtractionProfile>(nested.GetString(), out parsed))
            {
                return parsed;
            }
        }
        catch (JsonException)
        {
            return IngestExtractionProfile.Auto;
        }

        return IngestExtractionProfile.Auto;
    }

    private async Task MarkStoppedAsync(Guid jobId, IngestJobChunk? activeChunk)
    {
        var job = await ingest.GetJobAsync(jobId, CancellationToken.None);
        if (job is null) return;

        if (activeChunk is not null)
        {
            activeChunk.Status = IngestJobChunkStatus.Stopped;
            activeChunk.UpdatedAt = DateTime.UtcNow;
            ingest.UpdateJobChunk(activeChunk);
        }

        job.Status = IngestJobStatus.Stopped;
        job.CurrentMessage = "Stopped.";
        job.CompletedAt = DateTime.UtcNow;
        job.UpdatedAt = DateTime.UtcNow;
        ingest.UpdateJob(job);
        await ingest.SaveChangesAsync(CancellationToken.None);
        if (activeChunk is not null)
            NotifyLiveTokenCount(job.ProjectId, job.Id, activeChunk);
        Notify(job.ProjectId, job.Id, IngestJobUpdateKind.Stopped);
    }

    private async Task MarkFailedAsync(Guid jobId, IngestJobChunk? activeChunk, string errorMessage)
    {
        var job = await ingest.GetJobAsync(jobId, CancellationToken.None);
        if (job is null) return;

        if (activeChunk is not null)
        {
            activeChunk.Status = IngestJobChunkStatus.Failed;
            activeChunk.ErrorMessage = errorMessage;
            activeChunk.UpdatedAt = DateTime.UtcNow;
            ingest.UpdateJobChunk(activeChunk);
        }

        job.Status = IngestJobStatus.Failed;
        job.ErrorMessage = errorMessage;
        job.CurrentMessage = "Failed.";
        job.CompletedAt = DateTime.UtcNow;
        job.UpdatedAt = DateTime.UtcNow;
        ingest.UpdateJob(job);
        await ingest.SaveChangesAsync(CancellationToken.None);
        if (activeChunk is not null)
            NotifyLiveTokenCount(job.ProjectId, job.Id, activeChunk);
        Notify(job.ProjectId, job.Id, IngestJobUpdateKind.Failed);
    }

    private void Notify(Guid projectId, Guid jobId, IngestJobUpdateKind kind) =>
        notifier.Notify(new IngestJobUpdate(projectId, jobId, kind, DateTime.UtcNow));

    private void NotifyLive(Guid projectId, Guid jobId, IngestLiveUpdate live) =>
        notifier.Notify(new IngestJobUpdate(projectId, jobId, IngestJobUpdateKind.Live, DateTime.UtcNow, live));

    private int RetryDelayMs(int failedAttempt)
    {
        var baseDelay = Math.Clamp(options.Value.IngestRetryBaseDelayMs, 100, 60_000);
        var maxDelay = Math.Clamp(options.Value.IngestRetryMaxDelayMs, baseDelay, 120_000);
        var multiplier = Math.Pow(2, Math.Max(0, failedAttempt - 1));
        return Math.Min(maxDelay, (int)Math.Round(baseDelay * multiplier));
    }

    private static bool IsRetryableLlmFailure(Exception exception, CancellationToken cancellationToken)
    {
        if (IsCancellation(exception, cancellationToken)) return false;

        if (exception is OperationCanceledException or TimeoutException)
            return true;

        if (exception is HttpIOException { HttpRequestError: HttpRequestError.ResponseEnded })
            return true;

        if (exception is HttpRequestException httpRequestException)
        {
            if (httpRequestException.StatusCode is { } statusCode)
                return IsTransientStatusCode(statusCode);
            return LooksLikeTransientProviderError(httpRequestException.Message);
        }

        return exception.InnerException is not null && IsRetryableLlmFailure(exception.InnerException, cancellationToken)
            || LooksLikeTransientProviderError(exception.Message);
    }

    private static bool IsTransientStatusCode(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.Conflict
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout
        || (int)statusCode == 425;

    private static bool LooksLikeTransientProviderError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        var normalized = message.ToLowerInvariant();
        return normalized.Contains("response ended", StringComparison.Ordinal)
            || normalized.Contains("status code 408", StringComparison.Ordinal)
            || normalized.Contains("returned 408", StringComparison.Ordinal)
            || normalized.Contains(" 408", StringComparison.Ordinal)
            || normalized.Contains("status code 409", StringComparison.Ordinal)
            || normalized.Contains("returned 409", StringComparison.Ordinal)
            || normalized.Contains(" 409", StringComparison.Ordinal)
            || normalized.Contains("status code 425", StringComparison.Ordinal)
            || normalized.Contains("returned 425", StringComparison.Ordinal)
            || normalized.Contains(" 425", StringComparison.Ordinal)
            || normalized.Contains("status code 429", StringComparison.Ordinal)
            || normalized.Contains("returned 429", StringComparison.Ordinal)
            || normalized.Contains(" 429", StringComparison.Ordinal)
            || normalized.Contains("status code 500", StringComparison.Ordinal)
            || normalized.Contains("returned 500", StringComparison.Ordinal)
            || normalized.Contains(" 500", StringComparison.Ordinal)
            || normalized.Contains("status code 502", StringComparison.Ordinal)
            || normalized.Contains("returned 502", StringComparison.Ordinal)
            || normalized.Contains(" 502", StringComparison.Ordinal)
            || normalized.Contains("status code 503", StringComparison.Ordinal)
            || normalized.Contains("returned 503", StringComparison.Ordinal)
            || normalized.Contains(" 503", StringComparison.Ordinal)
            || normalized.Contains("status code 504", StringComparison.Ordinal)
            || normalized.Contains("returned 504", StringComparison.Ordinal)
            || normalized.Contains(" 504", StringComparison.Ordinal);
    }

    private static bool IsCancellation(Exception exception, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested && ContainsOperationCanceledException(exception);

    private static bool ContainsOperationCanceledException(Exception exception) =>
        exception is OperationCanceledException
        || exception.InnerException is not null && ContainsOperationCanceledException(exception.InnerException);

    private static bool IsChunkWriteTool(string toolName) =>
        string.Equals(toolName, "append_ingest_entity_observation", StringComparison.Ordinal)
        || string.Equals(toolName, "append_ingest_relationship_observation", StringComparison.Ordinal)
        || string.Equals(toolName, "update_ingest_source_progress", StringComparison.Ordinal);

    private static bool IsFinalReviewWriteTool(string toolName) =>
        string.Equals(toolName, "write_ingest_source_wiki_section", StringComparison.Ordinal);

    private static void MarkStagingRecordFinalized(IngestStagingRecord item)
    {
        item.Status = IngestStagingRecordStatus.Finalized;
        item.FinalizedAt = DateTime.UtcNow;
        item.UpdatedAt = DateTime.UtcNow;
    }

    private static void MarkStagingRecordFailed(IngestStagingRecord item, string reason)
    {
        item.Status = IngestStagingRecordStatus.Failed;
        item.ErrorMessage = reason;
        item.UpdatedAt = DateTime.UtcNow;
    }

    private static string RelationshipPromotionLabel(IngestStagingRecord item)
    {
        var title = string.IsNullOrWhiteSpace(item.Title)
            ? $"{item.FromEntityId?.ToString("N") ?? "unknown"} -[{item.EdgeType}]-> {item.ToEntityId?.ToString("N") ?? "unknown"}"
            : item.Title;
        return Truncate(title, 120);
    }

    private static bool TryReadRelationshipPromotionInput(
        IngestStagingRecord item,
        IngestJob job,
        out RelationshipPromotionInput input,
        out string reason)
    {
        input = default!;
        reason = string.Empty;

        var from = item.FromEntityId ?? Guid.Empty;
        var to = item.ToEntityId ?? Guid.Empty;
        if ((from == Guid.Empty || to == Guid.Empty) && !TryReadRelationshipEndpoints(item.PayloadJson, out from, out to))
        {
            reason = "payload is missing relationship endpoints";
            return false;
        }

        if (from == to)
        {
            reason = "relationship points to the same entity";
            return false;
        }

        var edgeType = (item.EdgeType ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(edgeType))
        {
            reason = "relationship type is empty";
            return false;
        }

        if (string.Equals(edgeType, EntityService.HasChildEdgeType, StringComparison.OrdinalIgnoreCase))
        {
            reason = "HasChild is a managed structural relationship";
            return false;
        }

        var sourceChunkId = item.SourceChunkId
            ?? IngestSourceAssertions.ReadPayloadSourceChunkId(item.PayloadJson);
        if (sourceChunkId is null || sourceChunkId == Guid.Empty)
        {
            reason = "relationship is not tied to exactly one source chunk";
            return false;
        }

        var sourceChunkIndex = item.SourceChunkIndex ?? ReadPayloadInt(item.PayloadJson, "sourceChunkIndex");
        if (sourceChunkIndex is null)
        {
            reason = "relationship is missing source chunk index";
            return false;
        }

        input = new RelationshipPromotionInput(
            from,
            to,
            edgeType,
            IngestSourceAssertions.ReadPayloadSourceId(item.PayloadJson) ?? job.SourceId,
            ReadPayloadString(item.PayloadJson, "sourceTitle") ?? job.Source.Title,
            ReadPayloadString(item.PayloadJson, "sourceKind") ?? job.Source.SourceKind,
            sourceChunkId.Value,
            sourceChunkIndex.Value);
        return true;
    }

    private static bool IsPromotableRelationshipNode(GraphNode node) =>
        Guid.TryParseExact(node.Key, "N", out _)
        && !string.Equals(node.NodeType, EntityTypeService.ProjectNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(node.NodeType, EntityTypeService.ActNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(node.NodeType, EntityTypeService.ChapterNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(node.NodeType, EntityTypeService.ProjectFactNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(node.NodeType, EntityTypeService.SourceNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(node.NodeType, EntityTypeService.SourceChunkNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(node.NodeType, EntityTypeService.SourceBlockNodeType, StringComparison.OrdinalIgnoreCase);

    private static string UpdateRelationshipGraphAction(string payloadJson, string graphAction)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(payloadJson) && payloadJson.TrimStart().StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(payloadJson);
                foreach (var property in doc.RootElement.EnumerateObject())
                    payload[property.Name] = property.Value.Clone();
            }
            catch (JsonException) { }
        }

        payload[IngestSourceAssertions.RelationshipGraphActionProperty] = graphAction;
        return JsonSerializer.Serialize(payload);
    }

    private static bool TryReadRelationshipEndpoints(string payloadJson, out Guid from, out Guid to)
    {
        from = Guid.Empty;
        to = Guid.Empty;
        if (string.IsNullOrWhiteSpace(payloadJson) || !payloadJson.TrimStart().StartsWith('{')) return false;

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            return TryReadGuid(doc.RootElement, "fromEntityId", out from)
                && TryReadGuid(doc.RootElement, "toEntityId", out to);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadGuid(JsonElement element, string propertyName, out Guid parsed)
    {
        parsed = Guid.Empty;
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && Guid.TryParse(property.GetString(), out parsed);
    }

    private static string? ReadPayloadString(string payloadJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(payloadJson) || !payloadJson.TrimStart().StartsWith('{')) return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            return doc.RootElement.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? ReadPayloadInt(string payloadJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(payloadJson) || !payloadJson.TrimStart().StartsWith('{')) return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (!doc.RootElement.TryGetProperty(propertyName, out var property)) return null;
            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number)) return number;
            return property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out var parsed)
                ? parsed
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<AIContent> BuildAssistantContents(string text, IReadOnlyList<PendingChunkToolCall> calls)
    {
        if (calls.Count == 0) return BuildTextOnlyAssistantContents(text);

        var contents = new List<AIContent>();
        var cursor = 0;
        foreach (var item in calls
            .Select((call, index) => new { Call = call, Index = index })
            .OrderBy(item => item.Call.TextOffset)
            .ThenBy(item => item.Index))
        {
            var offset = Math.Clamp(item.Call.TextOffset, 0, text.Length);
            if (offset > cursor)
            {
                contents.Add(new TextContent(text[cursor..offset]));
                cursor = offset;
            }

            contents.Add(item.Call.Content);
        }

        if (cursor < text.Length)
            contents.Add(new TextContent(text[cursor..]));
        if (contents.Count == 0)
            contents.Add(new TextContent(string.Empty));
        return contents;
    }

    private static List<AIContent> BuildTextOnlyAssistantContents(string text)
    {
        var contents = new List<AIContent>();
        if (!string.IsNullOrEmpty(text))
            contents.Add(new TextContent(text));
        if (contents.Count == 0)
            contents.Add(new TextContent(string.Empty));
        return contents;
    }

    private static int CountDistinctEntities(IEnumerable<IngestStagingRecord> reportItems) =>
        reportItems
            .Where(item => item.Kind == IngestStagingRecordKind.Entity && item.Status == IngestStagingRecordStatus.Active)
            .Select(item => item.EntityId?.ToString("N") ?? item.GraphNodeId?.ToString() ?? item.Id.ToString("N"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

    private static int CountDistinctRelationships(IEnumerable<IngestStagingRecord> reportItems) =>
        reportItems
            .Where(item => item.Kind == IngestStagingRecordKind.Relationship && item.Status == IngestStagingRecordStatus.Active)
            .Select(item => item.GraphEdgeId?.ToString() ?? item.Id.ToString("N"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max] + "...";
    }

    private sealed class IngestChunkTokenTracker(IReadOnlyList<ChatMessage> messages)
    {
        private readonly StringBuilder _currentAssistantText = new();
        private readonly List<LiveToolCallTokenState> _currentToolCalls = [];
        private readonly List<PendingToolResultTokenState> _pendingToolResults = [];

        public void BeginAssistantTurn()
        {
            _currentAssistantText.Clear();
            _currentToolCalls.Clear();
            _pendingToolResults.Clear();
        }

        public void AppendAssistantText(string text) => _currentAssistantText.Append(text);

        public void StartToolCall(string callId, string name, string argumentsJson)
        {
            var call = FindOrAddToolCall(callId);
            call.Name = name;
            if (!string.IsNullOrEmpty(argumentsJson))
                call.SetArguments(argumentsJson);
        }

        public void AppendToolArguments(string callId, string argumentsDelta)
        {
            var call = FindOrAddToolCall(callId);
            if (!string.IsNullOrEmpty(argumentsDelta))
                call.Arguments.Append(argumentsDelta);
        }

        public void SetToolCallArguments(string callId, string name, string argumentsJson)
        {
            var call = FindOrAddToolCall(callId);
            call.Name = name;
            call.SetArguments(argumentsJson);
        }

        public void CommitAssistantMessage()
        {
            _currentAssistantText.Clear();
            _currentToolCalls.Clear();
        }

        public void AddToolResult(string callId, string result) =>
            _pendingToolResults.Add(new PendingToolResultTokenState(callId, result));

        public void CommitToolMessage() => _pendingToolResults.Clear();

        public TokenCountResult Count(ITokenCounter counter, string? modelName, string? encodingName)
        {
            var sb = new StringBuilder();
            foreach (var message in messages)
                AppendMessage(sb, message);

            AppendCurrentAssistant(sb);
            AppendPendingToolResults(sb);

            return counter.Count(sb.ToString(), new TokenCountRequest(modelName, encodingName));
        }

        private LiveToolCallTokenState FindOrAddToolCall(string callId)
        {
            var call = _currentToolCalls.FirstOrDefault(candidate => string.Equals(candidate.CallId, callId, StringComparison.Ordinal));
            if (call is not null)
                return call;

            call = new LiveToolCallTokenState(callId);
            _currentToolCalls.Add(call);
            return call;
        }

        private void AppendCurrentAssistant(StringBuilder sb)
        {
            if (_currentAssistantText.Length == 0 && _currentToolCalls.Count == 0)
                return;

            sb.AppendLine("Assistant:");
            if (_currentAssistantText.Length > 0)
                sb.AppendLine(_currentAssistantText.ToString());
            foreach (var call in _currentToolCalls)
                AppendToolCall(sb, call.CallId, call.Name, call.Arguments.ToString());
        }

        private void AppendPendingToolResults(StringBuilder sb)
        {
            if (_pendingToolResults.Count == 0)
                return;

            sb.AppendLine("Tool:");
            foreach (var result in _pendingToolResults)
                AppendToolResult(sb, result.CallId, result.Result);
        }

        private static void AppendMessage(StringBuilder sb, ChatMessage message)
        {
            sb.Append(message.Role).AppendLine(":");
            foreach (var content in message.Contents)
                AppendContent(sb, content);
        }

        private static void AppendContent(StringBuilder sb, AIContent content)
        {
            switch (content)
            {
                case TextContent textContent when !string.IsNullOrEmpty(textContent.Text):
                    sb.AppendLine(textContent.Text);
                    break;
                case FunctionCallContent functionCall:
                    AppendToolCall(
                        sb,
                        functionCall.CallId ?? string.Empty,
                        functionCall.Name,
                        functionCall.Arguments is null ? "{}" : ToolCallArguments.Serialize(functionCall.Arguments));
                    break;
                case FunctionResultContent functionResult:
                    AppendToolResult(sb, functionResult.CallId ?? string.Empty, functionResult.Result?.ToString() ?? string.Empty);
                    break;
            }
        }

        private static void AppendToolCall(StringBuilder sb, string callId, string name, string argumentsJson)
        {
            sb.Append("Tool: ").Append(name).Append(' ').AppendLine(callId);
            if (!string.IsNullOrWhiteSpace(argumentsJson) && argumentsJson != "{}")
                sb.Append("Args: ").AppendLine(argumentsJson);
        }

        private static void AppendToolResult(StringBuilder sb, string callId, string result)
        {
            sb.Append("Tool result: ").AppendLine(callId);
            if (!string.IsNullOrWhiteSpace(result))
                sb.Append("Result: ").AppendLine(result);
        }
    }

    private sealed class LiveToolCallTokenState(string callId)
    {
        public string CallId { get; } = callId;
        public string Name { get; set; } = callId;
        public StringBuilder Arguments { get; } = new();

        public void SetArguments(string argumentsJson)
        {
            Arguments.Clear();
            if (!string.IsNullOrEmpty(argumentsJson))
                Arguments.Append(argumentsJson);
        }
    }

    private sealed record PendingToolResultTokenState(string CallId, string Result);

    private sealed record RelationshipPromotionInput(
        Guid FromEntityId,
        Guid ToEntityId,
        string EdgeType,
        Guid SourceId,
        string SourceTitle,
        string SourceKind,
        Guid SourceChunkId,
        int SourceChunkIndex);

    private sealed record PendingChunkToolCall(
        FunctionCallContent Content,
        string CallId,
        string Name,
        string ArgumentsJson,
        int TextOffset);
}
