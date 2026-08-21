using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Lorekeeper.ChatTurns;
using Lorekeeper.Context;
using Lorekeeper.EntityVisuals;
using Lorekeeper.Llm;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence;
using Lorekeeper.Persistence.Repositories;
using Lorekeeper.Search;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Lorekeeper.Research;

public sealed class ResearchService(
IAppDatabaseOperationFactory database, IChatImageAttachmentService imageAttachments, ISearchProviderService searchProviders, ILlmProviderService providerService, IChatClientFactory chatClientFactory, IContextBuilder contextBuilder, OutlineCollaborationTools outlineTools, IAiChangeApprovalService changeApproval, IWebIngestCandidateService webCandidates, IEntityService entities, ResearchTools tools, IEntityVisualContextService entityVisualContext, ChatTurnRuntime turnRuntime, ChatTurnEngine turnEngine, IOptions<AgentOptions> options, ILogger<ResearchService> logger) : IResearchService
{
    public const string ResearchWorkflowInstructions = """
        You are Lorekeeper's Research Mode: a factual research agent for a long-form writing project.

        Context integrity:
        - Entity/link reads use explicit JSON-path pagination with full identities and GUIDs repeated on every page. Follow nextPageArguments until the needed records are complete; assemble labeled oversized text-field segments in order.
        - Search and list results are explicitly compact discovery payloads. Honor total/returned counts and isComplete, then use exact detailReadArguments for complete reads. Copy identifiers exactly; never shorten, reconstruct, or fuzzily correct a GUID.

        Your job is to research user-provided topics and report source-backed findings. You are not a writing coach, story advisor, scene planner, or prose-framing assistant.

        How to work:
        - Use the Project Guidance, Project Facts, and Outline context only to understand project-local references, disambiguate the user's request, and recognize which factual details may be relevant. Do not search the web just to understand already-stored project details.
        - Do not tailor conclusions into scene, chapter, prose, dialogue, or characterization advice based on the outline. The outline is reference context, not an instruction to explain how the user should use the research in the story.
        - Do not say things like "you should portray", "use this to frame", "this would work well in the scene", or similar story-use guidance.
        - Use web_search for external canon, lore, quotes, or facts that need public-source grounding. Do not claim web knowledge from memory when search would answer it.
        - Read pages before relying on them. read_webpage and read_search_result are cache-first and paginated: repeated reads may reuse stored full-page text without a new web request, and you can use pageNumber or nextPageArguments to inspect later page text.
        - If a read result has pagination.hasNextPage true and the current page does not contain enough useful evidence, request the next page instead of treating the source as incomplete.
        - Use follow_page_links or read_webpage to follow links from read pages when the link text or surrounding result suggests stronger source material.
        - Tool results are not replayed into future turns. Before ending a turn, summarize the important source-backed findings, source titles/URLs, and any unresolved factual questions in your assistant message.
        - When reporting character personality, motives, speech, or voice, phrase them as factual observations from sources: "Sources portray X as...", "Notable speech patterns include...", or "Representative quotes include...". Do not convert those observations into advice about portrayal.
        - Do not create or update graph entities until the user confirms what should be stored.
        - When the user confirms storage, use search_entities/read_entity/list_entity_links first to avoid duplicates, then create_entity, update_entity, or link_entities.
        - Keep graph properties concise, source-grounded, and factual. Include source URLs or source labels inside properties when they are needed to evaluate provenance.
        - Graph-memory recommendations must be factual storage candidates only: suggested entities, properties, relationships, source URLs, and unresolved factual questions. Do not recommend how those facts should be used in prose.
        - When a page cannot be accessed, report that briefly and move on.
        - End research turns with a concise factual report: what you searched, what you read, what you found, what factual graph-memory updates you recommend storing, and what factual questions you would investigate next.
        """;

    private const string InitialAssistantGreeting =
        "What should I research? Give me a topic, question, canon area, or character and I'll find source-backed details we can turn into graph memory.";

    private bool _mutatedSinceYield;

    public async Task<ResearchConversation> GetOrCreateAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var projects = databaseOperation.Repositories.Projects;
        var conversations = databaseOperation.Repositories.ResearchConversations;
        var existing = await conversations.GetByProjectIdAsync(projectId, cancellationToken);
        if (existing is not null) return existing;

        _ = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");

        var conversation = new ResearchConversation { ProjectId = projectId };
        await conversations.AddConversationAsync(conversation, cancellationToken);
        await conversations.AddMessageAsync(new ResearchMessage
        {
            ConversationId = conversation.Id,
            Order = 0,
            Role = ResearchMessageRole.Assistant,
            Content = InitialAssistantGreeting,
            Status = ResearchMessageStatus.Completed,
        }, cancellationToken);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        return conversation;
    }

    public async Task<IReadOnlyList<ResearchMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var conversations = databaseOperation.Repositories.ResearchConversations;
        return await conversations.LoadMessagesAsync(conversationId, cancellationToken);
    }

    public async Task<ChatProviderAvailability> GetChatProviderAvailabilityAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var conversation = await databaseOperation.Repositories.ResearchConversations
            .GetByProjectIdAsync(projectId, cancellationToken);
        var selection = await providerService.ResolveChatModelSelectionAsync(
            conversation?.SelectedProviderId,
            cancellationToken);
        return selection.IsAvailable && selection.Provider is { } provider
            ? ChatProviderAvailability.Available(provider)
            : ChatProviderAvailability.Unavailable(selection.Message, selection.Provider);
    }

    public async Task SetSelectedProviderAsync(
        Guid projectId,
        int? providerId,
        CancellationToken cancellationToken = default)
    {
        using var maintenance = turnRuntime.TryBeginMaintenance(new ChatTurnKey(projectId, ChatTurnSurface.Research));
        if (maintenance is null)
            throw new InvalidOperationException("Research Chat is still working in another window. Stop or wait for that turn before changing its model.");

        if (providerId is int)
        {
            var selection = await providerService.ResolveChatModelSelectionAsync(providerId, cancellationToken);
            if (!selection.IsAvailable || selection.Provider is null)
                throw new InvalidOperationException(selection.Message);
        }

        var normalizedProviderId = await providerService.NormalizeChatModelSelectionAsync(providerId, cancellationToken);

        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var conversation = await databaseOperation.Repositories.ResearchConversations
            .GetByProjectIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException("Research Chat is not initialized.");
        conversation.SelectedProviderId = normalizedProviderId;
        conversation.UpdatedAt = DateTime.UtcNow;
        databaseOperation.Repositories.ResearchConversations.UpdateSelectedProvider(conversation);
        await databaseOperation.SaveChangesAsync(cancellationToken);
    }
    public async Task<string> GetSystemPromptAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var projects = databaseOperation.Repositories.Projects;
        var project = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");
        return await BuildSystemPromptAsync(project, cancellationToken);
    }

    public async Task<bool> GetAiChangeApprovalEnabledAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var projects = databaseOperation.Repositories.Projects;
        var project = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");
        return project.AiChangeApprovalEnabled;
    }

    public async Task SetAiChangeApprovalEnabledAsync(Guid projectId, bool enabled, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var projects = databaseOperation.Repositories.Projects;
        var project = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");
        if (project.AiChangeApprovalEnabled == enabled) return;

        project.AiChangeApprovalEnabled = enabled;
        project.UpdatedAt = DateTime.UtcNow;
        projects.Update(project);
        await databaseOperation.SaveChangesAsync(cancellationToken);
    }

    public Task<IReadOnlyList<AiChangeBatch>> ListPendingChangesAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        changeApproval.ListPendingBatchesAsync(projectId, cancellationToken);

    public async Task<ResearchActivity> GetActivityAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var conversations = databaseOperation.Repositories.ResearchConversations;
        var conversation = await GetOrCreateAsync(projectId, cancellationToken);
        var history = await conversations.LoadMessagesAsync(conversation.Id, cancellationToken);
        var toolCalls = BuildToolCallLookup(history);
        var entityTouches = new Dictionary<Guid, EntityTouch>();

        foreach (var message in history.Where(message => message.Role == ResearchMessageRole.Tool))
            AddToolEntityTouches(entityTouches, message, toolCalls);

        var pendingBatches = (await changeApproval.ListPendingBatchesAsync(projectId, cancellationToken))
            .Where(batch => batch.ConversationKind == AiChangeConversationKind.Research
                && batch.ConversationId == conversation.Id)
            .ToList();
        foreach (var batch in pendingBatches)
        {
            foreach (var change in batch.Changes.Where(change => change.Status == AiChangeStatus.Pending))
                AddPendingEntityTouches(entityTouches, batch, change);
        }

        var entityItems = new List<ResearchEntityActivityItem>();
        foreach (var touch in entityTouches.Values.OrderByDescending(touch => touch.LastTouchedAt))
        {
            var entity = await entities.GetAsync(projectId, touch.EntityId, cancellationToken);
            if (entity is null && !touch.HasPendingChange)
                continue;

            var properties = entity?.Properties
                ?? touch.Properties
                ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var type = FirstNonEmpty(entity?.Type, touch.Type, "Entity");
            var name = FirstNonEmpty(entity?.Name, touch.Name, touch.EntityId.ToString("N"));
            entityItems.Add(new ResearchEntityActivityItem(
                touch.EntityId,
                type,
                name,
                touch.State,
                Exists: entity is not null,
                touch.HasPendingChange,
                touch.PendingBatchId,
                touch.PendingChangeId,
                touch.PendingSummary,
                FirstNonEmpty(touch.Preview, BuildPropertyPreview(properties), touch.PendingSummary, string.Empty),
                touch.LastTouchedAt,
                new Dictionary<string, string?>(properties, StringComparer.OrdinalIgnoreCase)));
        }

        var sourceItems = (await webCandidates.ListResearchAsync(projectId, cancellationToken))
            .Where(source => source.ResearchConversationId == conversation.Id
                && (source.FetchedAt is not null || source.Status == WebIngestCandidateStatus.Failed))
            .OrderByDescending(source => source.UpdatedAt)
            .Select(source => new ResearchSourceActivityItem(
                source.Id,
                source.Status,
                source.Title,
                BestUrl(source),
                source.SearchQuery,
                source.SourceProviderName,
                source.Diagnostics,
                source.FetchedAt,
                source.UpdatedAt))
            .ToList();

        return new ResearchActivity(entityItems, sourceItems);
    }

    public Task<ResearchSourceDetail?> GetSourceDetailAsync(
        Guid projectId,
        Guid sourceId,
        CancellationToken cancellationToken = default) =>
        webCandidates.GetCachedDetailAsync(projectId, sourceId, cancellationToken);

    public async Task ResetAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        using var maintenance = turnRuntime.TryBeginMaintenance(new ChatTurnKey(projectId, ChatTurnSurface.Research));
        if (maintenance is null)
            throw new InvalidOperationException("Research Chat is still working in another window. Stop or wait for that turn before resetting the conversation.");
        await imageAttachments.ClearSurfaceAsync(projectId, ChatTurnSurface.Research, cancellationToken);
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var conversations = databaseOperation.Repositories.ResearchConversations;
        var existing = await conversations.GetByProjectIdAsync(projectId, cancellationToken);
        if (existing is null) return;

        conversations.RemoveConversation(existing);
        await databaseOperation.SaveChangesAsync(cancellationToken);
    }

    public async IAsyncEnumerable<ResearchTurnUpdate> SendAsync(
        Guid projectId,
        string userText,
        IReadOnlyList<Guid> imageIds,
        int providerId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userText))
            throw new ArgumentException("Message cannot be empty.", nameof(userText));

        var conversation = await GetOrCreateAsync(projectId, cancellationToken);
        Project project;
        ChatProviderAvailability providerAvailability;
        string? preflightError = null;
        try
        {
            project = await turnEngine.ReadAsync(
                repositories => repositories.Projects,
                projects => projects.GetByIdAsync(projectId, cancellationToken),
                cancellationToken)
                ?? throw new InvalidOperationException($"Project {projectId} not found.");
            if (!await searchProviders.HasActiveProviderAsync(cancellationToken))
                throw new InvalidOperationException("No active search provider is configured.");
            var persistedSelection = await providerService.ResolveChatModelSelectionAsync(
                conversation.SelectedProviderId,
                cancellationToken);
            if (!persistedSelection.IsAvailable || persistedSelection.Provider is not { } persistedProvider)
                throw new InvalidOperationException(persistedSelection.Message);
            if (persistedProvider.Id != providerId)
                throw new InvalidOperationException(ChatModelSelectionMessages.Changed);
            providerAvailability = ChatProviderAvailability.Available(persistedProvider);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Research turn setup failed for project {ProjectId}", projectId);
            preflightError = ex.Message;
            project = null!;
            providerAvailability = null!;
        }

        if (preflightError is not null)
        {
            yield return new ResearchTurnError(preflightError, Cancelled: false);
            yield break;
        }
        var chatProvider = providerAvailability.Provider!;
        var visionReady = await providerService.IsVisionProviderWorkingAsync(chatProvider.Id, cancellationToken);
        if (imageIds.Count > 0 && !visionReady)
        {
            yield return new ResearchTurnError("The active chat provider has not passed the vision check. Run Test in Settings > Providers before sending images.", Cancelled: false);
            yield break;
        }
        await imageAttachments.ResolveAsync(projectId, imageIds, cancellationToken);

        var nextOrder = await turnEngine.ReadAsync(
            repositories => repositories.ResearchConversations,
            conversations => conversations.GetMaxOrderAsync(conversation.Id, cancellationToken),
            cancellationToken) + 1;
        var userMessage = new ResearchMessage
        {
            ConversationId = conversation.Id,
            Order = nextOrder++,
            Role = ResearchMessageRole.User,
            Content = userText.Trim(),
            Status = ResearchMessageStatus.Completed,
        };
        conversation.UpdatedAt = DateTime.UtcNow;
        await turnEngine.AddMessageAsync(repositories => repositories.ResearchConversations, userMessage, cancellationToken);
        await imageAttachments.PersistAsync(projectId, ChatTurnSurface.Research, userMessage.Id, imageIds, cancellationToken);

        IChatClient chat = null!;
        IList<AITool> aiTools = null!;
        OutlineToolStagingContext? staging = null;
        string systemPrompt = string.Empty;
        ContextAssembly? initialAssembly = null;
        ResearchToolContext? toolContext = null;
        string? setupError = null;
        try
        {
            chat = await chatClientFactory.CreateChatClientAsync(chatProvider.Id, cancellationToken);
            initialAssembly = await contextBuilder.BuildAsync(
                new ContextBuildRequest(
                    project,
                    UserMessage: userText,
                    Purpose: ContextBuildPurpose.Research,
                    OperatingRules: ResearchWorkflowInstructions
                        + "\n\n" + AssistantWorkflowInstructions.NonReplayedToolHistory
                        + "\n\n" + AssistantWorkflowInstructions.EntityVisualExamples),
                cancellationToken);
            systemPrompt = initialAssembly.Assemble();
            if (project.AiChangeApprovalEnabled)
                staging = outlineTools.CreateStagingContext(
                    projectId,
                    conversation.Id,
                    AiChangeConversationKind.Research,
                    OnToolMutated);
            toolContext = new ResearchToolContext(projectId, conversation.Id, OnToolMutated, staging, visionReady);
            aiTools = await tools.BuildAsync(toolContext, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Research turn setup failed for project {ProjectId}", projectId);
            await PersistFailedAssistantAsync(conversation.Id, nextOrder, ex.Message);
            setupError = ex.Message;
        }

        if (setupError is not null)
        {
            yield return new ResearchTurnError(setupError, Cancelled: false);
            yield break;
        }

        var chatOptions = new ChatOptions
        {
            Tools = aiTools,
            ToolMode = ChatToolMode.Auto,
        };
        var history = await turnEngine.ReadAsync(
            repositories => repositories.ResearchConversations,
            conversations => conversations.LoadMessagesAsync(conversation.Id, cancellationToken),
            cancellationToken);
        var messages = new List<ChatMessage> { new(ChatRole.System, systemPrompt) };
        if (initialAssembly is not null && toolContext is not null)
        {
            var initialVisuals = await entityVisualContext.BuildVisionMessageAsync(
                projectId, initialAssembly.Visuals, toolContext.VisionReady,
                "Canonical visual references from the initial project context. Use them for identity and appearance continuity grounding; they are not scene tags.", cancellationToken);
            if (initialVisuals is not null) messages.Add(initialVisuals);
        }
        foreach (var persistedMessage in history)
        {
            if (persistedMessage.Id == userMessage.Id && imageIds.Count > 0)
            {
                messages.Add(await imageAttachments.BuildUserMessageAsync(projectId, persistedMessage.Content, imageIds, cancellationToken: cancellationToken));
                continue;
            }
            var replay = ChatModelHistory.Project(persistedMessage.Role.ToString(), persistedMessage.Content);
            if (replay is not null) messages.Add(replay);
        }

        var maxIterations = Math.Max(1, options.Value.MaxToolIterations);
        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            var activeAssistant = new ResearchMessage
            {
                ConversationId = conversation.Id,
                Order = nextOrder++,
                Role = ResearchMessageRole.Assistant,
                Content = string.Empty,
                Status = ResearchMessageStatus.Pending,
            };
            await turnEngine.AddMessageAsync(repositories => repositories.ResearchConversations, activeAssistant, cancellationToken);

            ChatRoundCompleted? completedRound = null;
            await foreach (var update in turnEngine.StreamRoundAsync(chat, messages, chatOptions, cancellationToken))
            {
                switch (update)
                {
                    case ChatRoundTextDelta text:
                        yield return new ResearchTextDelta(text.Text);
                        break;
                    case ChatRoundToolCallStarted started:
                        yield return new ResearchToolCallStarted(started.CallId, started.ToolName, started.ArgumentsJson, started.ArgumentsComplete);
                        break;
                    case ChatRoundToolCallArgumentsDelta delta:
                        yield return new ResearchToolCallArgumentsDelta(delta.CallId, delta.ArgumentsDelta, delta.ArgumentsComplete);
                        break;
                    case ChatRoundCompleted completed:
                        completedRound = completed;
                        break;
                    case ChatRoundFailed failed:
                        activeAssistant.Content = failed.Text;
                        activeAssistant.Status = failed.Cancelled
                            ? ResearchMessageStatus.Cancelled
                            : ResearchMessageStatus.Failed;
                        activeAssistant.ErrorMessage = failed.Cancelled ? "Cancelled by user." : failed.Message;
                        await SafePersistAsync(activeAssistant);
                        yield return new ResearchTurnError(failed.Message, failed.Cancelled);
                        yield break;
                }
            }

            DrainMutated();
            if (completedRound is null)
            {
                activeAssistant.Status = ResearchMessageStatus.Failed;
                activeAssistant.ErrorMessage = "Research streaming ended without a completed round.";
                await SafePersistAsync(activeAssistant);
                yield return new ResearchTurnError(activeAssistant.ErrorMessage, Cancelled: false);
                yield break;
            }

            var textBuilder = new StringBuilder(completedRound.Text);
            var pendingCalls = completedRound.ToolCalls;

            if (pendingCalls.Count == 0)
            {
                activeAssistant.Content = textBuilder.ToString();
                activeAssistant.Status = ResearchMessageStatus.Completed;
                await SafePersistAsync(activeAssistant);
                yield return new ResearchAssistantMessageCompleted(activeAssistant.Id);
                yield break;
            }

            var manifest = pendingCalls
                .Select(ChatToolCallManifest.From)
                .ToList();
            activeAssistant.Content = textBuilder.ToString();
            activeAssistant.ToolCallsJson = JsonSerializer.Serialize(manifest);
            activeAssistant.Status = ResearchMessageStatus.Completed;
            await SafePersistAsync(activeAssistant);
            messages.Add(new ChatMessage(ChatRole.Assistant, ChatTurnEngine.BuildAssistantContents(textBuilder.ToString(), pendingCalls)));

            var resultContents = new List<AIContent>();
            foreach (var pendingCall in pendingCalls)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    yield return new ResearchTurnError("Cancelled.", Cancelled: true);
                    yield break;
                }

                var sw = Stopwatch.StartNew();
                staging?.BeginToolCall(activeAssistant.Id, pendingCall.CallId, pendingCall.Name, pendingCall.ArgumentsJson);
                var toolOutcome = await turnEngine.InvokeToolAsync(aiTools, pendingCall, cancellationToken);
                sw.Stop();

                if (toolOutcome.Cancelled)
                {
                    yield return new ResearchTurnError("Cancelled.", Cancelled: true);
                    yield break;
                }

                var toolResult = toolOutcome.Result;
                var toolError = toolOutcome.Error;

                var toolMessage = new ResearchMessage
                {
                    ConversationId = conversation.Id,
                    Order = nextOrder++,
                    Role = ResearchMessageRole.Tool,
                    Content = toolResult ?? string.Empty,
                    ToolCallId = pendingCall.CallId,
                    ToolName = pendingCall.Name,
                    Status = toolError is null ? ResearchMessageStatus.Completed : ResearchMessageStatus.Failed,
                    ErrorMessage = toolError,
                };
                await turnEngine.AddMessageAsync(repositories => repositories.ResearchConversations, toolMessage, CancellationToken.None);

                resultContents.Add(new FunctionResultContent(pendingCall.CallId, toolResult ?? string.Empty));
                if (staging is not null)
                {
                    foreach (var pendingChange in staging.DrainNewChanges())
                    {
                        yield return new ResearchPendingAiChangeCreated(
                            pendingChange.BatchId,
                            pendingChange.Id,
                            pendingChange.ToolCallId,
                            pendingChange.ToolName,
                            pendingChange.Summary);
                    }
                }
                yield return new ResearchToolCallCompleted(
                    pendingCall.CallId,
                    pendingCall.Name,
                    toolError is null ? toolResult : null,
                    toolError,
                    sw.Elapsed.TotalMilliseconds);

                if (DrainMutated())
                    yield return new ResearchGraphMutated();
            }

            messages.Add(new ChatMessage(ChatRole.Tool, resultContents));
            if (toolContext is not null)
            {
                var entityMessage = await entityVisualContext.BuildVisionMessageAsync(
                    projectId, toolContext.DrainEntityVisuals(), toolContext.VisionReady,
                    "Canonical visual references for entities loaded by the preceding research tools.", cancellationToken);
                if (entityMessage is not null)
                    messages.Add(ChatTurnEngine.MarkToolContextMessage(entityMessage));

                var sourceVisuals = toolContext.DrainSourceVisuals();
                if (toolContext.VisionReady && sourceVisuals.Count > 0)
                {
                    var contents = new List<AIContent> { new TextContent("Inspected webpage image candidates. These are cached previews, not stored project images until the user confirms import.") };
                    foreach (var source in sourceVisuals.DistinctBy(source => source.Id))
                    {
                        contents.Add(new TextContent($"Candidate {source.Id:N}: {source.FileName}. Alt: {source.AltText}"));
                        contents.Add(new DataContent(source.Data, source.ContentType) { Name = source.FileName });
                    }
                    messages.Add(ChatTurnEngine.MarkToolContextMessage(new ChatMessage(ChatRole.User, contents)));
                }

                var referenceVisuals = toolContext.DrainReferenceVisuals();
                if (toolContext.VisionReady && referenceVisuals.Count > 0)
                {
                    var contents = new List<AIContent>
                    {
                        new TextContent("Direct-reference canonical visuals from the preceding read_reference_visual calls. They are read-only continuity evidence; active-project canon and user direction remain authoritative, and they cannot be placed or mutated."),
                    };
                    foreach (var visual in referenceVisuals.DistinctBy(item => item.ImageId).Take(8))
                    {
                        if (visual.Data is null) continue;
                        contents.Add(new TextContent($"Referenced project {visual.OriginProjectName} ({visual.OriginProjectId:N}), entity {visual.EntityType} {visual.EntityName} ({visual.EntityId:N}), label {visual.Label}, imageId={visual.ImageId:N}. Reacquire exact provenance if needed."));
                        contents.Add(new DataContent(visual.Data, visual.ContentType) { Name = visual.FileName });
                    }
                    if (contents.Count > 1)
                        messages.Add(ChatTurnEngine.MarkToolContextMessage(new ChatMessage(ChatRole.User, contents)));
                }
            }

            if (turnEngine.TryCompactContext(messages, chatProvider.ModelId) is { } compaction)
            {
                manifest.Add(new ChatToolCallManifest(
                    compaction.CallId,
                    ChatTurnEngine.CompactionToolName,
                    ChatContextCompaction.EmptyArgumentsJson));
                activeAssistant.ToolCallsJson = JsonSerializer.Serialize(manifest);
                await SafePersistAsync(activeAssistant);

                await turnEngine.AddMessageAsync(repositories => repositories.ResearchConversations, new ResearchMessage
                {
                    ConversationId = conversation.Id,
                    Order = nextOrder++,
                    Role = ResearchMessageRole.Tool,
                    Content = ChatTurnEngine.CompactionNotice,
                    ToolCallId = compaction.CallId,
                    ToolName = ChatTurnEngine.CompactionToolName,
                    Status = ResearchMessageStatus.Completed,
                }, CancellationToken.None);
                yield return new ResearchToolCallStarted(
                    compaction.CallId,
                    ChatTurnEngine.CompactionToolName,
                    ChatContextCompaction.EmptyArgumentsJson,
                    ArgumentsComplete: true);
                yield return new ResearchToolCallCompleted(
                    compaction.CallId,
                    ChatTurnEngine.CompactionToolName,
                    ChatTurnEngine.CompactionNotice,
                    Error: null,
                    DurationMs: 0);
            }

            if (iteration == maxIterations - 1)
            {
                yield return new ResearchTurnError(
                    ChatTurnEngine.ToolLoopLimitError(maxIterations),
                    Cancelled: false);
                yield break;
            }
        }
    }

    private static Dictionary<string, ChatToolCallManifest> BuildToolCallLookup(IEnumerable<ResearchMessage> history)
    {
        var result = new Dictionary<string, ChatToolCallManifest>(StringComparer.Ordinal);
        foreach (var message in history.Where(message => message.Role == ResearchMessageRole.Assistant))
        {
            foreach (var call in ReadPersistedToolCalls(message.ToolCallsJson))
                result[call.CallId] = call;
        }

        return result;
    }

    private static void AddToolEntityTouches(
        IDictionary<Guid, EntityTouch> touches,
        ResearchMessage message,
        IReadOnlyDictionary<string, ChatToolCallManifest> toolCalls)
    {
        var toolName = message.ToolName ?? string.Empty;
        if (!IsEntityActivityTool(toolName)) return;

        toolCalls.TryGetValue(message.ToolCallId ?? string.Empty, out var call);
        JsonDocument? argsDoc = null;
        JsonDocument? resultDoc = null;
        try
        {
            var hasArgs = TryParseJsonObject(call?.ArgumentsJson, out argsDoc);
            var hasResult = TryParseJsonObject(message.Content, out resultDoc);
            var args = hasArgs ? argsDoc!.RootElement : default;
            var result = hasResult ? resultDoc!.RootElement : default;

            switch (toolName)
            {
                case "read_entity":
                    if (hasResult && TryAddEntityPayload(touches, result, ResearchEntityActivityState.Read, "Read entity", message.CreatedAt))
                        break;
                    if (hasArgs && TryReadGuid(args, "entityId", out var readId))
                        UpsertEntityTouch(touches, readId, string.Empty, string.Empty, ResearchEntityActivityState.Read, "Read entity", message.CreatedAt);
                    break;
                case "list_entity_links":
                    if (hasArgs && TryReadGuid(args, "entityId", out var linksId))
                        UpsertEntityTouch(touches, linksId, string.Empty, string.Empty, ResearchEntityActivityState.Read, "Listed entity links", message.CreatedAt);
                    break;
                case "create_entity":
                    if (hasResult && TryGetPropertyObject(result, "existing", out var existing))
                    {
                        TryAddEntityPayload(touches, existing, ResearchEntityActivityState.Read, "Found existing entity", message.CreatedAt);
                    }
                    else if (hasResult)
                    {
                        TryAddEntityPayload(touches, result, ResearchEntityActivityState.Created, "Created entity", message.CreatedAt);
                    }
                    break;
                case "update_entity":
                    if (hasResult && TryAddEntityPayload(touches, result, ResearchEntityActivityState.Updated, "Updated entity", message.CreatedAt))
                        break;
                    if (hasArgs && TryReadGuid(args, "entityId", out var updateId))
                        UpsertEntityTouch(touches, updateId, string.Empty, string.Empty, ResearchEntityActivityState.Updated, "Updated entity", message.CreatedAt);
                    break;
                case "link_entities":
                    if (hasResult)
                    {
                        if (TryGetPropertyObject(result, "from", out var fromEntity))
                            TryAddEntityPayload(touches, fromEntity, ResearchEntityActivityState.Linked, "Linked entity", message.CreatedAt);
                        if (TryGetPropertyObject(result, "to", out var toEntity))
                            TryAddEntityPayload(touches, toEntity, ResearchEntityActivityState.Linked, "Linked entity", message.CreatedAt);
                    }
                    if (hasArgs)
                    {
                        if (TryReadGuid(args, "fromId", out var fromId))
                            UpsertEntityTouch(touches, fromId, string.Empty, string.Empty, ResearchEntityActivityState.Linked, "Linked entity", message.CreatedAt);
                        if (TryReadGuid(args, "toId", out var toId))
                            UpsertEntityTouch(touches, toId, string.Empty, string.Empty, ResearchEntityActivityState.Linked, "Linked entity", message.CreatedAt);
                    }
                    break;
            }
        }
        finally
        {
            argsDoc?.Dispose();
            resultDoc?.Dispose();
        }
    }

    private static void AddPendingEntityTouches(
        IDictionary<Guid, EntityTouch> touches,
        AiChangeBatch batch,
        AiChange change)
    {
        if (TryReadEntityChange(change.AfterJson, out var entityChange))
        {
            var state = string.Equals(change.BeforeJson, "null", StringComparison.OrdinalIgnoreCase)
                ? ResearchEntityActivityState.PendingCreated
                : ResearchEntityActivityState.PendingUpdated;
            UpsertEntityTouch(
                touches,
                entityChange.Id,
                entityChange.Type,
                entityChange.Name,
                state,
                change.Summary,
                change.CreatedAt,
                pendingBatchId: batch.Id,
                pendingChangeId: change.Id,
                pendingSummary: change.Summary,
                properties: entityChange.Properties);
            return;
        }

        if (TryReadEntityLinkChange(change.AfterJson, out var linkChange))
        {
            UpsertEntityTouch(
                touches,
                linkChange.FromId,
                string.Empty,
                string.Empty,
                ResearchEntityActivityState.PendingLinked,
                change.Summary,
                change.CreatedAt,
                pendingBatchId: batch.Id,
                pendingChangeId: change.Id,
                pendingSummary: change.Summary);
            UpsertEntityTouch(
                touches,
                linkChange.ToId,
                string.Empty,
                string.Empty,
                ResearchEntityActivityState.PendingLinked,
                change.Summary,
                change.CreatedAt,
                pendingBatchId: batch.Id,
                pendingChangeId: change.Id,
                pendingSummary: change.Summary);
            return;
        }

        if (TryParseEntityResource(change.ResourceId, out var entityId))
        {
            UpsertEntityTouch(
                touches,
                entityId,
                string.Empty,
                string.Empty,
                ResearchEntityActivityState.PendingUpdated,
                change.Summary,
                change.CreatedAt,
                pendingBatchId: batch.Id,
                pendingChangeId: change.Id,
                pendingSummary: change.Summary);
        }
    }

    private static bool IsEntityActivityTool(string toolName) =>
        toolName is "read_entity"
            or "list_entity_links"
            or "create_entity"
            or "update_entity"
            or "link_entities";

    private static bool TryAddEntityPayload(
        IDictionary<Guid, EntityTouch> touches,
        JsonElement element,
        ResearchEntityActivityState state,
        string activity,
        DateTime touchedAt)
    {
        if (!TryReadEntityPayload(element, out var id, out var type, out var name, out var properties))
            return false;

        UpsertEntityTouch(touches, id, type, name, state, activity, touchedAt, properties: properties);
        return true;
    }

    private static bool TryReadEntityPayload(
        JsonElement element,
        out Guid id,
        out string type,
        out string name,
        out Dictionary<string, string?> properties)
    {
        id = Guid.Empty;
        type = string.Empty;
        name = string.Empty;
        properties = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (!TryReadGuid(element, "id", out id))
            return false;

        type = ReadString(element, "type");
        name = ReadString(element, "name");
        if (TryGetPropertyObject(element, "properties", out var propertyElement))
            properties = ReadProperties(propertyElement);
        else
            properties = ReadPaginatedEntityProperties(element);
        return true;
    }

    private static Dictionary<string, string?> ReadPaginatedEntityProperties(JsonElement element)
    {
        var properties = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (!TryGetPropertyIgnoreCase(element, "content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return properties;
        }

        foreach (var entry in content.EnumerateArray())
        {
            var path = ReadString(entry, "path");
            if (!TryGetPropertyIgnoreCase(entry, "value", out var value)) continue;
            if (string.Equals(path, "$[\"properties\"]", StringComparison.Ordinal)
                && value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in ReadProperties(value))
                    properties[property.Key] = property.Value;
                continue;
            }

            const string propertyPathPrefix = "$[\"properties\"][";
            if (!path.StartsWith(propertyPathPrefix, StringComparison.Ordinal) || !path.EndsWith(']')) continue;
            var encodedKey = path[propertyPathPrefix.Length..^1];
            string? key;
            try { key = JsonSerializer.Deserialize<string>(encodedKey); }
            catch (JsonException) { continue; }
            if (string.IsNullOrWhiteSpace(key)) continue;
            properties[key] = value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => value.GetString(),
                _ => value.GetRawText(),
            };
        }

        return properties;
    }

    private static void UpsertEntityTouch(
        IDictionary<Guid, EntityTouch> touches,
        Guid entityId,
        string type,
        string name,
        ResearchEntityActivityState state,
        string preview,
        DateTime touchedAt,
        Guid? pendingBatchId = null,
        Guid? pendingChangeId = null,
        string? pendingSummary = null,
        IReadOnlyDictionary<string, string?>? properties = null)
    {
        if (!touches.TryGetValue(entityId, out var touch))
        {
            touch = new EntityTouch(entityId);
            touches[entityId] = touch;
        }

        if (!string.IsNullOrWhiteSpace(type)) touch.Type = type.Trim();
        if (!string.IsNullOrWhiteSpace(name)) touch.Name = name.Trim();
        if (!string.IsNullOrWhiteSpace(preview)) touch.Preview = preview.Trim();
        if (StatePriority(state) >= StatePriority(touch.State)) touch.State = state;
        if (touchedAt > touch.LastTouchedAt) touch.LastTouchedAt = touchedAt;

        if (pendingChangeId is not null)
        {
            touch.HasPendingChange = true;
            touch.PendingBatchId = pendingBatchId;
            touch.PendingChangeId = pendingChangeId;
            touch.PendingSummary = pendingSummary;
        }

        if (properties is not null)
        {
            touch.Properties ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in properties)
                touch.Properties[property.Key] = property.Value;
        }
    }

    private static int StatePriority(ResearchEntityActivityState state) => state switch
    {
        ResearchEntityActivityState.PendingCreated => 70,
        ResearchEntityActivityState.PendingUpdated => 65,
        ResearchEntityActivityState.PendingLinked => 60,
        ResearchEntityActivityState.Created => 50,
        ResearchEntityActivityState.Updated => 40,
        ResearchEntityActivityState.Linked => 30,
        _ => 10,
    };

    private static bool TryParseJsonObject(string? json, out JsonDocument? document)
    {
        document = null;
        if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith('{')) return false;
        try
        {
            document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetPropertyObject(JsonElement element, string propertyName, out JsonElement property)
    {
        if (TryGetPropertyIgnoreCase(element, propertyName, out property)
            && property.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        property = default;
        return false;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement property)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out property))
            return true;

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in element.EnumerateObject())
            {
                if (string.Equals(item.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    property = item.Value;
                    return true;
                }
            }
        }

        property = default;
        return false;
    }

    private static bool TryReadGuid(JsonElement element, string propertyName, out Guid value)
    {
        value = Guid.Empty;
        return TryGetPropertyIgnoreCase(element, propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && Guid.TryParse(property.GetString(), out value);
    }

    private static string ReadString(JsonElement element, string propertyName) =>
        TryGetPropertyIgnoreCase(element, propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static Dictionary<string, string?> ReadProperties(JsonElement element)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            result[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Null => null,
                _ => property.Value.GetRawText(),
            };
        }

        return result;
    }

    private static bool TryReadEntityChange(string json, out OutlineEntityChange value)
    {
        value = default!;
        if (string.IsNullOrWhiteSpace(json) || string.Equals(json.Trim(), "null", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            value = JsonSerializer.Deserialize<OutlineEntityChange>(json) ?? default!;
            return value is not null && value.Id != Guid.Empty;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadEntityLinkChange(string json, out OutlineEntityLinkChange value)
    {
        value = default!;
        if (string.IsNullOrWhiteSpace(json) || string.Equals(json.Trim(), "null", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            value = JsonSerializer.Deserialize<OutlineEntityLinkChange>(json) ?? default!;
            return value is not null && value.FromId != Guid.Empty && value.ToId != Guid.Empty;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseEntityResource(string resourceId, out Guid entityId)
    {
        entityId = Guid.Empty;
        const string prefix = "Entity:";
        return resourceId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(resourceId[prefix.Length..], out entityId);
    }

    private static string BuildPropertyPreview(IReadOnlyDictionary<string, string?> properties)
    {
        foreach (var key in new[] { "summary", "description", "role", "voiceNotes", "quoteExamples", "value" })
        {
            if (properties.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value.Length <= 220 ? value : value[..220].TrimEnd() + "...";
        }

        var first = properties.OrderBy(property => property.Key, StringComparer.OrdinalIgnoreCase)
            .Select(property => property.Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return string.IsNullOrWhiteSpace(first)
            ? string.Empty
            : first.Length <= 220 ? first : first[..220].TrimEnd() + "...";
    }

    private static List<ChatToolCallManifest> ReadPersistedToolCalls(string toolCallsJson)
    {
        if (string.IsNullOrWhiteSpace(toolCallsJson) || toolCallsJson == "[]") return [];
        try { return JsonSerializer.Deserialize<List<ChatToolCallManifest>>(toolCallsJson) ?? []; }
        catch { return []; }
    }

    private static string BestUrl(WebIngestCandidateView source) =>
        FirstNonEmpty(source.CanonicalUrl, source.FinalUrl, source.Url);

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private async Task<string> BuildSystemPromptAsync(Project project, CancellationToken cancellationToken)
    {
        var assembly = await contextBuilder.BuildAsync(
            new ContextBuildRequest(
                project,
                Purpose: ContextBuildPurpose.Research,
                OperatingRules: ResearchWorkflowInstructions
                    + "\n\n" + AssistantWorkflowInstructions.NonReplayedToolHistory
                    + "\n\n" + AssistantWorkflowInstructions.EntityVisualExamples),
            cancellationToken);
        return assembly.Assemble();
    }

    private void OnToolMutated() => _mutatedSinceYield = true;

    private bool DrainMutated()
    {
        var value = _mutatedSinceYield;
        _mutatedSinceYield = false;
        return value;
    }

    private async Task PersistFailedAssistantAsync(Guid conversationId, int order, string error)
    {
        await using var databaseOperation = await database.OpenWriteAsync(default);
        databaseOperation.ShareWithNestedOperations();
        var conversations = databaseOperation.Repositories.ResearchConversations;
        try
        {
            await conversations.AddMessageAsync(new ResearchMessage
            {
                ConversationId = conversationId,
                Order = order,
                Role = ResearchMessageRole.Assistant,
                Status = ResearchMessageStatus.Failed,
                ErrorMessage = error,
            }, CancellationToken.None);
            await databaseOperation.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist Research setup failure");
        }
    }

    private async Task SafePersistAsync(ResearchMessage message)
    {
        try
        {
            await turnEngine.UpdateMessageAsync(repositories => repositories.ResearchConversations, message, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist Research message {MessageId}", message.Id);
        }
    }

    private sealed class EntityTouch(Guid entityId)
    {
        public Guid EntityId { get; } = entityId;
        public string Type { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public ResearchEntityActivityState State { get; set; } = ResearchEntityActivityState.Read;
        public bool HasPendingChange { get; set; }
        public Guid? PendingBatchId { get; set; }
        public Guid? PendingChangeId { get; set; }
        public string? PendingSummary { get; set; }
        public string Preview { get; set; } = string.Empty;
        public DateTime LastTouchedAt { get; set; } = DateTime.MinValue;
        public Dictionary<string, string?>? Properties { get; set; }
    }

}
