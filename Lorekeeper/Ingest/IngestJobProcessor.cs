using System.Text;
using System.Text.Json;
using Lorekeeper.Context;
using Lorekeeper.Knowledge;
using Lorekeeper.Llm;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence.Repositories;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Lorekeeper.Ingest;

public sealed class IngestJobProcessor(
    IIngestRepository ingest,
    ILlmProviderService providerService,
    IChatClientFactory chatClientFactory,
    IngestAgentTools tools,
    IEntityTypeService entityTypes,
    IVectorStore vectors,
    IEmbeddingService embeddings,
    ITextChunker chunker,
    IIngestGraphSync graphSync,
    IIngestJobNotifier notifier,
    IContextIndexingService contextIndexing,
    IOptions<AgentOptions> options,
    ILogger<IngestJobProcessor> logger)
{
    private const string _systemPrompt = """
        You are an ingestion extraction agent for Lorekeeper.

        Your job is to read the current source chunk, which may or may not be part of a larger document, and record source-scoped observations on the project graph.
        You may link to existing non-structural project entities when the source clearly refers to the same thing.
        Do not rewrite canonical project entity properties. Extracted facts, aliases, evidence, and notes belong in source-scoped assertions recorded by the ingest tools.
        Prefer fewer, stronger story entities over duplicate nodes for titles, aliases, partial names, or alternate spellings. Entity data should be useful for later retrieval and writing; it does not need to be highly normalized or split into many tiny fields.

        Process rules:
        - Call list_job_entities before creating or linking entities, and compare each source mention against the same-job roster first.
        - Before every create_ingest_entity call, call search_project_entities for the source mention and its likely variants.
        - Treat search_project_entities results as candidate matches for your judgment, not automatic identity decisions. A shared title, honorific, role, or epithet such as queen, prince, lord, commander, or similar is not enough by itself to reuse an entity.
        - Search broadly, not just exactly: use the canonical singular type plus the exact mention, base name with titles/honorifics removed, known aliases, surnames, epithets, alternate spellings, and descriptive terms from the local context. For example, "Prince Kael'thas" should search both "Prince Kael'thas" and "Kael'thas".
        - Treat title/honorific differences, punctuation/case differences, shortened names, aliases, and obvious same-subject references as the same entity when the source context supports it.
        - Do not create an entity when list_job_entities or search_project_entities returns a clear same subject with matching names, aliases, or source-grounded identity details. Use update_ingest_entity or record_existing_entity_observation instead.
        - Use record_existing_entity_observation when a source mention matches an existing project entity.
        - Use update_ingest_entity when a source mention matches an entity already touched by this ingest job.
        - Create a new entity only when no existing project entity or same-job entity matches after variant searches. The tool will reject duplicate names; treat that as instruction to reuse the returned/existing entity.
        - Use canonical singular entity type keys from the known project entity types. Do not invent plural, lowercase, or near-duplicate categories such as "characters", "Characters", "locations", or "organisations" when Character, Location, or Organization/Faction-style categories are available.
        - Keep recurring source observations current. If a character appears again later with new history, status, aliases, relationships, or role details, update the source assertion for the existing entity.
        - Use the properties object for practical, readable observations such as summary, description, role, status, affiliation, history, motivation, or significance. Avoid empty schema-filling; prefer concise natural-language values that will help a writer understand and retrieve the entity later.
        - Pass tool objects and arrays directly. Do not serialize properties or aliases into JSON strings; use {} for no properties and [] for no aliases.
        - Use evidence from the current source chunk. Do not invent facts.
        - Link only entities already touched by this ingest job using link_ingest_entities. If an endpoint is an existing project entity, record an observation on it first.
        - Finish each source chunk by calling record_source_chunk_notes with a concise summary.
        """;

    public async Task RunAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        IngestJobChunk? activeChunk = null;

        try
        {
            var job = await ingest.GetJobDetailAsync(jobId, cancellationToken)
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

            await EnsureVectorFragmentsAsync(job.Source, cancellationToken);
            var sourceChunks = job.Chunks.Select(chunk => chunk.SourceChunk).OrderBy(chunk => chunk.Index).ToList();
            await graphSync.EnsureSourceAsync(job.Source, sourceChunks, cancellationToken);

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
                job.CreatedEntityCount = reportItems.Count(item => item.Kind == IngestReportItemKind.Entity && item.Status == IngestReportItemStatus.Active);
                job.CreatedRelationshipCount = reportItems.Count(item => item.Kind == IngestReportItemKind.Relationship && item.Status == IngestReportItemStatus.Active);
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
        jobChunk.UpdatedAt = DateTime.UtcNow;
        ingest.UpdateJobChunk(jobChunk);

        job.CurrentMessage = $"Processing source chunk {jobChunk.SourceChunkIndex + 1} of {job.TotalSourceChunks}: {sourceChunk.Title}";
        job.UpdatedAt = DateTime.UtcNow;
        ingest.UpdateJob(job);
        await ingest.SaveChangesAsync(cancellationToken);
        Notify(job.ProjectId, job.Id, IngestJobUpdateKind.Progress);

        var mutated = false;
        var sourceGraphChanged = false;
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
        var aiTools = tools.Build(context);
        var chatOptions = new ChatOptions
        {
            Tools = aiTools,
            ToolMode = ChatToolMode.Auto,
        };

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _systemPrompt),
            new(ChatRole.User, await BuildChunkPromptAsync(job, sourceChunk, cancellationToken)),
        };

        string? finalText = null;
        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await chat.GetResponseAsync(messages, chatOptions, cancellationToken);
            var assistantMessage = response.Messages.LastOrDefault()
                ?? new ChatMessage(ChatRole.Assistant, response.Text ?? string.Empty);
            messages.Add(assistantMessage);

            var functionCalls = assistantMessage.Contents.OfType<FunctionCallContent>().ToList();
            if (functionCalls.Count == 0)
            {
                finalText = assistantMessage.Text;
                break;
            }

            var resultContents = new List<AIContent>();
            foreach (var functionCall in functionCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var toolResult = await InvokeToolAsync(aiTools, functionCall, job.ProjectId, job.Id, cancellationToken);
                resultContents.Add(new FunctionResultContent(functionCall.CallId ?? functionCall.Name, toolResult));
            }

            messages.Add(new ChatMessage(ChatRole.Tool, resultContents));
            if (iteration == maxIterations - 1)
                throw new InvalidOperationException($"Ingest tool-call loop hit configured cap of {maxIterations} iterations without completing source chunk {sourceChunk.Index}.");
        }

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
            await graphSync.EnsureSourceAsync(job.Source, sourceChunks, cancellationToken);
            if (sourceGraphChanged)
                await contextIndexing.ReindexIngestSourceChunkAsync(sourceChunk.Id, cancellationToken);
        }
    }

    private async Task<string> InvokeToolAsync(IList<AITool> aiTools, FunctionCallContent functionCall, Guid projectId, Guid jobId, CancellationToken cancellationToken)
    {
        var argsJson = functionCall.Arguments is null ? "{}" : JsonSerializer.Serialize(functionCall.Arguments);
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

            var result = await aiFunction.InvokeAsync(new AIFunctionArguments(functionCall.Arguments ?? new Dictionary<string, object?>()), cancellationToken);
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
            if (provider is not null) return provider;

            var fallback = await providerService.GetDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException($"LLM provider {providerId} was not found, and no default LLM provider is configured.");
            await ingest.AddEventAsync(new IngestJobEvent
            {
                JobId = job.Id,
                Level = IngestJobEventLevel.Warning,
                EventType = "provider.fallback",
                Message = $"Configured provider {providerId} was not found; using {fallback.Name}.",
                PayloadJson = JsonSerializer.Serialize(new { missingProviderId = providerId, fallbackProviderId = fallback.Id, fallback.ModelId }),
            }, cancellationToken);
            job.ProviderId = fallback.Id;
            job.ModelName = fallback.ModelId;
            job.UpdatedAt = DateTime.UtcNow;
            ingest.UpdateJob(job);
            await ingest.SaveChangesAsync(cancellationToken);
            Notify(job.ProjectId, job.Id, IngestJobUpdateKind.Event);
            return fallback;
        }

        var defaultProvider = await providerService.GetDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("No default LLM provider configured.");
        job.ProviderId = defaultProvider.Id;
        job.ModelName = defaultProvider.ModelId;
        job.UpdatedAt = DateTime.UtcNow;
        ingest.UpdateJob(job);
        await ingest.SaveChangesAsync(cancellationToken);
        Notify(job.ProjectId, job.Id, IngestJobUpdateKind.Progress);
        return defaultProvider;
    }

    private async Task<string> BuildChunkPromptAsync(IngestJob job, IngestSourceChunk sourceChunk, CancellationToken cancellationToken)
    {
        var sourceText = job.Source.SourceText;
        var start = Math.Clamp(sourceChunk.StartChar, 0, sourceText.Length);
        var end = Math.Clamp(sourceChunk.EndChar, start, sourceText.Length);
        var currentText = sourceText[start..end];
        var entityRoster = await BuildEntityRosterAsync(job.Id, cancellationToken);
        var previousSummaries = await BuildPreviousSummariesAsync(job, sourceChunk.Index, cancellationToken);
        var knownEntityTypes = await BuildKnownEntityTypesAsync(job.ProjectId, cancellationToken);

        return $$"""
            Source title: {{job.Source.Title}}
            Source kind: {{job.Source.SourceKind}}
            Source description: {{job.Source.Description}}

            User extraction instructions:
            {{job.Instructions}}

            Current source chunk:
            - Index: {{sourceChunk.Index + 1}} of {{job.TotalSourceChunks}}
            - Title: {{sourceChunk.Title}}
            - Heading path: {{sourceChunk.HeadingPath}}
            - Estimated tokens: {{sourceChunk.EstimatedTokenCount}} ({{sourceChunk.TokenCountMethod}}{{(sourceChunk.TokenCountIsExact ? ", exact" : ", estimated")}})

            Previous source chunk summaries:
            {{previousSummaries}}

            Entities already touched by this ingest job:
            {{entityRoster}}

            Known project entity types:
            {{knownEntityTypes}}

            Entity matching workflow for this chunk:
            1. Start from the same-job roster above.
            2. For each source mention that may be an entity, search existing project entities before creating anything.
            3. Use the canonical singular type and multiple query variants: exact mention, base name without titles/honorifics, aliases, surnames, epithets, alternate spellings, and nearby descriptive terms.
            4. Reuse a plausible same-job or project entity instead of creating duplicate names or duplicate categories. For example, link "Prince Kael'thas" observations to an existing "Kael'thas" Character when the context points to the same person.
            5. Create only when the roster and project searches do not return a plausible same subject.

            Write useful observations. The properties object may use broad natural-language fields such as summary, description, role, status, affiliation, history, motivation, or significance; it does not need to be highly structured when a readable note is more useful.

            Current source chunk text:
            ```text
            {{currentText}}
            ```
            """;
    }

    private async Task<string> BuildEntityRosterAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var reportItems = await ingest.ListReportItemsAsync(jobId, cancellationToken);
        var entities = reportItems
            .Where(item => item.Kind == IngestReportItemKind.Entity && item.Status == IngestReportItemStatus.Active)
            .OrderBy(item => item.ResourceType)
            .ThenBy(item => item.Title)
            .Take(200)
            .Select(item => $"- {item.EntityId}: {item.ResourceType} '{item.Title}' | {Truncate(item.Summary, 180)} | notes: {Truncate(item.Notes, 180)}")
            .ToList();

        return entities.Count == 0 ? "None yet." : string.Join("\n", entities);
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

    private async Task<string> BuildPreviousSummariesAsync(IngestJob job, int currentIndex, CancellationToken cancellationToken)
    {
        var sourceChunks = await ingest.ListSourceChunksAsync(job.SourceId, cancellationToken);
        var summaries = sourceChunks
            .Where(chunk => chunk.Index < currentIndex && !string.IsNullOrWhiteSpace(chunk.Summary))
            .OrderByDescending(chunk => chunk.Index)
            .Take(8)
            .OrderBy(chunk => chunk.Index)
            .Select(chunk => $"- {chunk.Index + 1}. {chunk.Title}: {Truncate(chunk.Summary, 300)}")
            .ToList();

        return summaries.Count == 0 ? "None yet." : string.Join("\n", summaries);
    }

    private async Task EnsureVectorFragmentsAsync(IngestSource source, CancellationToken cancellationToken)
    {
        var existingFragments = await ingest.ListVectorFragmentsAsync(source.Id, cancellationToken);
        if (source.VectorIndexState == VectorIndexState.UpToDate && existingFragments.Count > 0)
            return;

        try
        {
            await vectors.DeleteBySourceAsync("ingest_source", source.VectorSourceId, Project.ScopeKey(source.ProjectId), cancellationToken);
            foreach (var fragment in existingFragments)
                ingest.RemoveVectorFragment(fragment);
            await ingest.SaveChangesAsync(cancellationToken);

            var chunks = chunker.Chunk(source.SourceText);
            if (chunks.Count > 0)
            {
                var contents = chunks.Select(item => item.Content).ToList();
                var embeddingVectors = await embeddings.GenerateEmbeddingsAsync(contents, cancellationToken);
                var cursor = 0;
                for (var index = 0; index < chunks.Count; index++)
                {
                    var text = chunks[index].Content;
                    var start = FindFragmentStart(source.SourceText, text, cursor);
                    var end = Math.Min(source.SourceText.Length, start + text.Length);
                    cursor = Math.Min(source.SourceText.Length, Math.Max(start + 1, end - 200));
                    var metadata = $"Source {source.Title} - Vector fragment {index + 1}/{chunks.Count}";
                    var rowId = await vectors.StoreAsync(
                        content: text,
                        embedding: embeddingVectors[index],
                        sourceType: "ingest_source",
                        scopeKey: Project.ScopeKey(source.ProjectId),
                        sourceId: source.VectorSourceId,
                        metadata: metadata,
                        chunkIndex: index,
                        cancellationToken: cancellationToken);

                    await ingest.AddVectorFragmentAsync(new IngestVectorFragment
                    {
                        SourceId = source.Id,
                        Index = index,
                        VectorRowId = rowId,
                        StartChar = start,
                        EndChar = end,
                        Metadata = metadata,
                    }, cancellationToken);
                }
            }

            source.VectorIndexState = VectorIndexState.UpToDate;
            source.VectorIndexedAt = DateTime.UtcNow;
            source.VectorIndexError = null;
            source.UpdatedAt = DateTime.UtcNow;
            ingest.UpdateSource(source);
            await ingest.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            source.VectorIndexState = VectorIndexState.Failed;
            source.VectorIndexError = ex.Message;
            source.UpdatedAt = DateTime.UtcNow;
            ingest.UpdateSource(source);
            await ingest.SaveChangesAsync(CancellationToken.None);
            throw;
        }
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
        Notify(job.ProjectId, job.Id, IngestJobUpdateKind.Failed);
    }

    private void Notify(Guid projectId, Guid jobId, IngestJobUpdateKind kind) =>
        notifier.Notify(new IngestJobUpdate(projectId, jobId, kind, DateTime.UtcNow));

    private static int FindFragmentStart(string sourceText, string fragmentText, int cursor)
    {
        var searchStart = Math.Max(0, cursor - 500);
        var found = sourceText.IndexOf(fragmentText, searchStart, StringComparison.Ordinal);
        return found < 0 ? Math.Clamp(cursor, 0, sourceText.Length) : found;
    }

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max] + "...";
    }
}