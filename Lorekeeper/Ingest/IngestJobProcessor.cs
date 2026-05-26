using System.Diagnostics;
using System.Net;
using System.Net.Http;
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
    IOptions<AgentOptions> options,
    ILogger<IngestJobProcessor> logger)
{
    private const string _systemPrompt = """
        You are an ingestion extraction agent for Lorekeeper.

        Your job is to read the current source chunk, which may or may not be part of a larger document, and build durable wiki-style entity sheets on the project graph.
        You may link to existing non-structural project entities when the source clearly refers to the same thing.
        Prefer fewer, stronger entities over duplicate nodes for titles, aliases, partial names, or alternate spellings. Entity data should be useful for later retrieval and writing; it should read like an organized wiki sheet, not extraction notes.

        Adapt what counts as an entity to the source and the project. Fiction, science fiction, and fantasy sources should preserve story and setting continuity: characters, places, cultures, factions, artifacts, magic/technology, histories, rules, recurring terms, and relationships. Nonfiction and research sources should preserve concepts, people, events, examples, methods, terms, claims, evidence, and arguments. Use the project type palette first, then create broad useful non-structural types only when the source needs them.

        A useful wiki sheet captures the source-grounded information the current project would need later: identity, role, status, affiliation, history, motivation, significance, setting/world-building details, factual claims, examples, relationships, and evidence. Store this as summary, aliases, and ordered wiki sections with compact citations.

        Process rules:
        - Start from the compact touched-entity index in the prompt. It is only an identity hint; read a full sheet before updating it.
        - Before every create_ingest_entity call, call search_project_entities for the source mention and its likely variants.
        - Treat search_project_entities results as candidate matches for your judgment, not automatic identity decisions. A shared title, honorific, role, epithet, or semantic similarity is not enough by itself to reuse or update an entity.
        - Search broadly, not just exactly: use the canonical singular type plus the exact mention, base name with titles/honorifics removed, known aliases, surnames, epithets, alternate spellings, and descriptive terms from the local context. For example, "Prince Kael'thas" should search both "Prince Kael'thas" and "Kael'thas".
        - Treat title/honorific differences, punctuation/case differences, shortened names, aliases, and obvious same-subject references as the same entity when the source context supports it.
        - Do not create an entity when the touched-entity index or search_project_entities returns a clear same subject with matching names, aliases, or source-grounded identity details. Use read_entity_sheet and update_ingest_entity_sheet instead.
        - Do not update an existing entity just because it is semantically similar to the current source chunk. Update it only when the chunk explicitly supports a fact about that same entity.
        - Use read_entity_sheet before updating any existing or already-touched entity.
        - Use update_ingest_entity_sheet when a source mention matches an existing project entity or a same-job entity.
        - Create a new entity only when no existing project entity or same-job entity matches after variant searches. The tool will reject duplicate names; treat that as instruction to reuse the returned/existing entity.
        - Use canonical singular entity type keys from the known project entity types. Do not invent plural, lowercase, or near-duplicate categories such as "characters", "Characters", "locations", or "organisations" when an existing project type reasonably fits.
        - If a new type is needed, choose a broad stable type name. Prefer reusable categories such as Culture, Faction, Artifact, Magic, Technology, Lore, Concept, Person, Event, Example, Claim, Term, or Method over one-off labels.
        - Keep recurring sheets current. If a character, place, concept, faction, or other entity appears again with new information, rewrite the relevant sheet sections so the page remains organized and current.
        - The summary field is required and should remain concise. Wiki sections can be larger, but they should be structured for later reading and retrieval.
        - Pass tool objects and arrays directly. Do not serialize aliases, wiki sections, or citations into JSON strings; use [] for no aliases or no sections.
        - Use compact citations from the current source chunk for non-trivial facts. Citations may include sourceBlockId, pageNumber, locator, and a short quote or close summary snippet. Do not invent facts.
        - Never record statements such as "the page did not mention this", "not enough information", "semantically similar", or any other rationale for why an unsupported update was attempted. If the source does not support a fact, skip that fact.
        - Link only entities already touched by this ingest job using link_ingest_entities. If an endpoint is an existing project entity, update its sheet first.
        - Finish each source chunk by calling update_ingest_source_progress exactly once with the completed chunk summary and the rolling source synopsis. Keep the rolling source synopsis around 1500 words.
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
                var reportItems = await ingest.ListReportItemsAsync(job.Id, cancellationToken);
                job.CreatedEntityCount = CountDistinctEntities(reportItems);
                job.CreatedRelationshipCount = CountDistinctRelationships(reportItems);
                job.CurrentMessage = $"Completed source chunk {jobChunk.SourceChunkIndex + 1} of {job.TotalSourceChunks}.";
                job.UpdatedAt = DateTime.UtcNow;
                ingest.UpdateJob(job);
                await ingest.SaveChangesAsync(cancellationToken);
                Notify(job.ProjectId, job.Id, IngestJobUpdateKind.Progress);
            }

            job.Status = IngestJobStatus.Completed;
            job.CompletedSourceChunks = job.TotalSourceChunks;
            job.CurrentMessage = "Completed.";
            job.CompletedAt = DateTime.UtcNow;
            job.UpdatedAt = DateTime.UtcNow;
            ingest.UpdateJob(job);
            await ingest.SaveChangesAsync(CancellationToken.None);
            Notify(job.ProjectId, job.Id, IngestJobUpdateKind.Completed);
        }
        catch (OperationCanceledException ex)
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
            catch (Exception ex) when (IsRetryableLlmFailure(ex) && attempt < maxAttempts)
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
                        error = ex.Message,
                    }),
                }, cancellationToken);
                await ingest.SaveChangesAsync(cancellationToken);
                Notify(job.ProjectId, job.Id, IngestJobUpdateKind.Event);
                NotifyLive(job.ProjectId, job.Id, new IngestLiveRetryScheduled(sourceChunk.Id, sourceChunk.Index, sourceChunk.Title, attempt, maxAttempts, delayMs, ex.Message));
                await Task.Delay(delayMs, cancellationToken);
            }
            catch (Exception ex) when (!IsCancellation(ex))
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

        var reportItems = await ingest.ListReportItemsAsync(job.Id, cancellationToken);
        jobChunk.Status = IngestJobChunkStatus.Completed;
        jobChunk.Summary = sourceChunk.Summary;
        jobChunk.CreatedEntityCount = reportItems.Count(item => item.SourceChunkId == sourceChunk.Id && item.Kind == IngestReportItemKind.Entity && item.Status == IngestReportItemStatus.Active);
        jobChunk.CreatedRelationshipCount = reportItems.Count(item => item.SourceChunkId == sourceChunk.Id && item.Kind == IngestReportItemKind.Relationship && item.Status == IngestReportItemStatus.Active);
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
        NotifyTokenCountIfChanged(job, jobChunk, tokenTracker);

        string? finalText = null;
        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var assistantText = new StringBuilder();
            var pendingCalls = new List<PendingChunkToolCall>();
            var toolTracker = new StreamingToolCallTracker();
            tokenTracker.BeginAssistantTurn();

            await foreach (var update in chat.GetStreamingResponseAsync(messages, chatOptions, cancellationToken))
            {
                foreach (var content in update.Contents)
                {
                    if (content is TextContent textContent && textContent.Text is { Length: > 0 } text)
                    {
                        assistantText.Append(text);
                        tokenTracker.AppendAssistantText(text);
                        NotifyLive(job.ProjectId, job.Id, new IngestLiveTextDelta(sourceChunk.Id, sourceChunk.Index, sourceChunk.Title, text));
                        NotifyTokenCountIfChanged(job, jobChunk, tokenTracker);
                        continue;
                    }

                    foreach (var toolUpdate in toolTracker.Process(content, assistantText.Length))
                    {
                        switch (toolUpdate)
                        {
                            case StreamingToolCallStartedUpdate started:
                                tokenTracker.StartToolCall(started.CallId, started.ToolName, started.ArgumentsJson);
                                NotifyLive(job.ProjectId, job.Id, new IngestLiveToolCallStarted(sourceChunk.Id, sourceChunk.Index, sourceChunk.Title, started.CallId, started.ToolName, started.ArgumentsJson, started.ArgumentsComplete));
                                NotifyTokenCountIfChanged(job, jobChunk, tokenTracker);
                                break;

                            case StreamingToolCallArgumentsDeltaUpdate delta:
                                tokenTracker.AppendToolArguments(delta.CallId, delta.ArgumentsDelta);
                                NotifyLive(job.ProjectId, job.Id, new IngestLiveToolCallArgumentsDelta(sourceChunk.Id, sourceChunk.Index, sourceChunk.Title, delta.CallId, delta.ArgumentsDelta, delta.ArgumentsComplete));
                                NotifyTokenCountIfChanged(job, jobChunk, tokenTracker);
                                break;

                            case StreamingToolCallReadyUpdate ready:
                                tokenTracker.SetToolCallArguments(ready.CallId, ready.ToolName, ready.ArgumentsJson);
                                pendingCalls.Add(new PendingChunkToolCall(ready.Content, ready.CallId, ready.ToolName, ready.ArgumentsJson));
                                NotifyTokenCountIfChanged(job, jobChunk, tokenTracker);
                                break;
                        }
                    }
                }
            }

            var assistantContents = new List<AIContent>();
            if (assistantText.Length > 0)
                assistantContents.Add(new TextContent(assistantText.ToString()));
            assistantContents.AddRange(pendingCalls.Select(call => (AIContent)call.Content));
            var assistantMessage = new ChatMessage(ChatRole.Assistant, assistantContents);
            messages.Add(assistantMessage);
            tokenTracker.CommitAssistantMessage();
            NotifyTokenCountIfChanged(job, jobChunk, tokenTracker);

            if (pendingCalls.Count == 0)
            {
                finalText = assistantText.ToString();
                break;
            }

            var resultContents = new List<AIContent>();
            foreach (var pendingCall in pendingCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stopwatch = Stopwatch.StartNew();
                var toolResult = await InvokeToolAsync(aiTools, pendingCall.Content, job.ProjectId, job.Id, cancellationToken);
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
                NotifyTokenCountIfChanged(job, jobChunk, tokenTracker);
                resultContents.Add(new FunctionResultContent(pendingCall.CallId, toolResult));
            }

            messages.Add(new ChatMessage(ChatRole.Tool, resultContents));
            tokenTracker.CommitToolMessage();
            NotifyTokenCountIfChanged(job, jobChunk, tokenTracker);
            if (iteration == maxIterations - 1)
                throw new InvalidOperationException($"Ingest tool-call loop hit configured cap of {maxIterations} iterations without completing source chunk {sourceChunk.Index}.");
        }

        return finalText;
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
        var entityRoster = await BuildTouchedEntityIndexAsync(job.Id, cancellationToken);
        var chunkProgress = BuildChunkProgressMap(job);
        var knownEntityTypes = await BuildKnownEntityTypesAsync(job.ProjectId, cancellationToken);
        var extractionProfile = BuildExtractionProfilePrompt(job);
        var sourceLocators = await BuildSourceLocatorsAsync(job.SourceId, sourceChunk, cancellationToken);

        return $$"""
            Source title: {{job.Source.Title}}
            Source kind: {{job.Source.SourceKind}}
            Source description: {{job.Source.Description}}
            Extraction profile:
            {{extractionProfile}}

            User extraction instructions:
            {{job.Instructions}}

            Rolling source synopsis:
            {{(string.IsNullOrWhiteSpace(job.Source.Synopsis) ? "None yet." : job.Source.Synopsis.Trim())}}

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

            Known project entity types:
            {{knownEntityTypes}}

            Source locators overlapping this chunk:
            {{sourceLocators}}

            Entity matching workflow for this chunk:
            1. Start from the compact touched-entity index above.
            2. For each source mention that may be an entity, search existing project entities before creating anything.
            3. Use the canonical singular type and multiple query variants: exact mention, base name without titles/honorifics, aliases, surnames, epithets, alternate spellings, and nearby descriptive terms.
            4. Reuse a plausible same-job or project entity instead of creating duplicate names or duplicate categories. For example, link "Prince Kael'thas" observations to an existing "Kael'thas" Character when the context points to the same person.
            5. Create only when the touched-entity index and project searches do not return a plausible same subject.

            Write useful, source-grounded wiki sheets. For each touched entity, provide a required concise summary, source-mentioned aliases, and complete revised wiki sections with citations. If an entity already exists, call read_entity_sheet, integrate the new information into the existing sections, and call update_ingest_entity_sheet with the complete revised sheet. When evidence comes from a listed source locator, include sourceBlockId/pageNumber/locator and a short snippet in the citation.

            Good example: if a Blood Elves page says Liadrin leads the blood elf paladins and is one of the race's primary leaders, record that as role/significance/history fields on the Liadrin Character with evidence from the page. Bad example: do not write that Liadrin was updated because the page was semantically similar or because the page did not mention her.

            Current source chunk text:
            ```text
            {{currentText}}
            ```
            """;
    }

    private async Task<string> BuildTouchedEntityIndexAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var reportItems = await ingest.ListReportItemsAsync(jobId, cancellationToken);
        var entities = reportItems
            .Where(item => item.Kind == IngestReportItemKind.Entity && item.Status == IngestReportItemStatus.Active)
            .Where(item => item.EntityId is not null)
            .GroupBy(item => item.EntityId!.Value)
            .Select(group => group.OrderByDescending(item => item.UpdatedAt).First())
            .OrderBy(item => item.ResourceType)
            .ThenBy(item => item.Title)
            .Take(300)
            .Select(item => $"- {item.EntityId}: {item.ResourceType} '{item.Title}' | {Truncate(item.Summary, 220)}")
            .ToList();

        return entities.Count == 0 ? "None yet." : string.Join("\n", entities);
    }

    private static string BuildChunkProgressMap(IngestJob job)
    {
        var lines = job.Chunks
            .OrderBy(chunk => chunk.SourceChunkIndex)
            .Select(chunk =>
            {
                var sourceChunk = chunk.SourceChunk;
                var title = string.IsNullOrWhiteSpace(sourceChunk.Title) ? $"Part {sourceChunk.Index + 1}" : sourceChunk.Title;
                var summary = !string.IsNullOrWhiteSpace(sourceChunk.Summary)
                    ? Truncate(sourceChunk.Summary, 220)
                    : !string.IsNullOrWhiteSpace(chunk.Summary)
                        ? Truncate(chunk.Summary, 220)
                        : string.Empty;
                var suffix = string.IsNullOrWhiteSpace(summary) ? string.Empty : $" | {summary}";
                return $"- {sourceChunk.Index + 1}. {title} | {chunk.Status}{suffix}";
            })
            .ToList();

        return lines.Count == 0 ? "No source chunks were generated." : string.Join("\n", lines);
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

    private async Task<string> BuildSourceLocatorsAsync(Guid sourceId, IngestSourceChunk sourceChunk, CancellationToken cancellationToken)
    {
        var blocks = await ingest.ListSourceBlocksAsync(sourceId, cancellationToken);
        var overlapping = blocks
            .Where(block => block.EndChar > sourceChunk.StartChar && block.StartChar < sourceChunk.EndChar)
            .OrderBy(block => block.Index)
            .Take(24)
            .Select(block =>
            {
                var label = string.IsNullOrWhiteSpace(block.Locator) ? block.Title : block.Locator;
                var page = block.PageNumber is int pageNumber ? $" page {pageNumber};" : string.Empty;
                return $"- {block.Id:N}: {block.Kind} '{label}' ({page} chars {block.StartChar}-{block.EndChar})";
            })
            .ToList();

        return overlapping.Count == 0 ? "No page or section locators were recorded for this chunk." : string.Join("\n", overlapping);
    }

    private static string BuildExtractionProfilePrompt(IngestJob job)
    {
        var profile = ReadExtractionProfile(job.Source.SourceMetadataJson);
        var profileDescription = profile switch
        {
            IngestExtractionProfile.StoryWorldbuilding => "Story / Worldbuilding. Prioritize continuity knowledge for fiction: characters, places, cultures, factions, lore, history, objects, rules, and relationships.",
            IngestExtractionProfile.ResearchNonfiction => "Research / Nonfiction. Prioritize concepts, people, historical events, examples, claims, arguments, terms, methods, and source-backed evidence.",
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

    private static bool IsRetryableLlmFailure(Exception exception)
    {
        if (IsCancellation(exception)) return false;

        if (exception is HttpIOException { HttpRequestError: HttpRequestError.ResponseEnded })
            return true;

        if (exception is HttpRequestException httpRequestException)
        {
            if (httpRequestException.StatusCode is { } statusCode)
                return IsTransientStatusCode(statusCode);
            return LooksLikeTransientProviderError(httpRequestException.Message);
        }

        return exception.InnerException is not null && IsRetryableLlmFailure(exception.InnerException)
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

    private static bool IsCancellation(Exception exception) =>
        exception is OperationCanceledException;

    private static int CountDistinctEntities(IEnumerable<IngestReportItem> reportItems) =>
        reportItems
            .Where(item => item.Kind == IngestReportItemKind.Entity && item.Status == IngestReportItemStatus.Active)
            .Select(item => item.EntityId?.ToString("N") ?? item.GraphNodeId?.ToString() ?? item.Id.ToString("N"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

    private static int CountDistinctRelationships(IEnumerable<IngestReportItem> reportItems) =>
        reportItems
            .Where(item => item.Kind == IngestReportItemKind.Relationship && item.Status == IngestReportItemStatus.Active)
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

    private sealed record PendingChunkToolCall(
        FunctionCallContent Content,
        string CallId,
        string Name,
        string ArgumentsJson);
}
