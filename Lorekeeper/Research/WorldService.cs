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

public sealed class WorldService(
 IAppDatabaseOperationFactory database, IChatImageAttachmentService imageAttachments, ILlmProviderService providerService, IChatClientFactory chatClientFactory, IContextBuilder contextBuilder, IWebIngestCandidateService webCandidates, IEntityService entities, WorldTools tools, IEntityVisualContextService entityVisualContext, ChatTurnRuntime turnRuntime, ChatTurnEngine turnEngine, IOptions<AgentOptions> options, ILogger<WorldService> logger) : IWorldService
{
    public const string WorldWorkflowInstructions = """
        You are Lorekeeper's World assistant, an adaptive world-development and research partner for any book type.
        Help invent coherent fictional settings, investigate real subjects, and develop durable context appropriate to the user's book.
        Maintain the free-form World Brief as the project's concise synthesis of established world or research context. Its name remains World Brief for every book type.
        For fiction, capture established setting, history, cultures, systems, and constraints. For nonfiction, capture research scope, supported findings, terminology, uncertainties, and background. No fixed headings are required.
        Direct build/edit requests authorize saves. Brainstorming and comparison remain conversational until a direction is chosen. Distinguish fictional invention, sourced fact, inference, and unresolved questions.
        Read the current World Brief and revision before updating it. Keep detailed canon in entities, facts, and relationships and source material in Sources. Do not duplicate whole sources in the brief or automatically promote sources to canon.
        Search existing entities before creating them. Preserve useful provenance and surface conflicts with the Book Brief, World Brief, or established canon.
        Use web_search when external evidence is needed, then read pages before relying on them. Cached reads are paginated; follow continuations as needed.
        Missing web search configuration does not block creative work or local project research. Explain an unavailable external capability without fabricating evidence.
        Preserve the guarded fetching, source promotion, and explicit visual-reference approval rules of the web tools.
        Read back saved changes. Close with a self-contained account of established decisions, saved changes, sources, and unresolved questions.
        """ + "\n\n" + AssistantWorkflowInstructions.NonReplayedToolHistory;

    private const string InitialAssistantGreeting =
        "What would you like to develop or research? I can build your world, investigate a subject, and maintain the project’s World Brief.";

    private bool _mutatedSinceYield;

    public async Task<WorldConversation> GetOrCreateAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var projects = databaseOperation.Repositories.Projects;
        var conversations = databaseOperation.Repositories.WorldConversations;
        var existing = await conversations.GetByProjectIdAsync(projectId, cancellationToken);
        if (existing is not null) return existing;

        _ = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");

        var conversation = new WorldConversation { ProjectId = projectId };
        await conversations.AddConversationAsync(conversation, cancellationToken);
        await conversations.AddMessageAsync(new WorldMessage
        {
            ConversationId = conversation.Id,
            Order = 0,
            Role = WorldMessageRole.Assistant,
            Content = InitialAssistantGreeting,
            Status = WorldMessageStatus.Completed,
        }, cancellationToken);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        return conversation;
    }

    public async Task<IReadOnlyList<WorldMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var conversations = databaseOperation.Repositories.WorldConversations;
        return await conversations.LoadMessagesAsync(conversationId, cancellationToken);
    }

    public async Task<ChatProviderAvailability> GetChatProviderAvailabilityAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var conversation = await databaseOperation.Repositories.WorldConversations
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
        using var maintenance = turnRuntime.TryBeginMaintenance(new ChatTurnKey(projectId, ChatTurnSurface.World));
        if (maintenance is null)
            throw new InvalidOperationException("World is still working in another window. Stop or wait for that turn before changing its model.");

        if (providerId is int)
        {
            var selection = await providerService.ResolveChatModelSelectionAsync(providerId, cancellationToken);
            if (!selection.IsAvailable || selection.Provider is null)
                throw new InvalidOperationException(selection.Message);
        }

        var normalizedProviderId = await providerService.NormalizeChatModelSelectionAsync(providerId, cancellationToken);

        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var conversation = await databaseOperation.Repositories.WorldConversations
            .GetByProjectIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException("World is not initialized.");
        conversation.SelectedProviderId = normalizedProviderId;
        conversation.UpdatedAt = DateTime.UtcNow;
        databaseOperation.Repositories.WorldConversations.UpdateSelectedProvider(conversation);
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

    public async Task<ResearchActivity> GetActivityAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var conversations = databaseOperation.Repositories.WorldConversations;
        var conversation = await GetOrCreateAsync(projectId, cancellationToken);
        var history = await conversations.LoadMessagesAsync(conversation.Id, cancellationToken);
        var toolCalls = BuildToolCallLookup(history);
        var entityTouches = new Dictionary<Guid, EntityTouch>();

        foreach (var message in history.Where(message => message.Role == WorldMessageRole.Tool))
            AddToolEntityTouches(entityTouches, message, toolCalls);

        var entityItems = new List<ResearchEntityActivityItem>();
        foreach (var touch in entityTouches.Values.OrderByDescending(touch => touch.LastTouchedAt))
        {
            var entity = await entities.GetAsync(projectId, touch.EntityId, cancellationToken);
            if (entity is null)
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
                FirstNonEmpty(touch.Preview, BuildPropertyPreview(properties), string.Empty),
                touch.LastTouchedAt,
                new Dictionary<string, string?>(properties, StringComparer.OrdinalIgnoreCase)));
        }

        var sourceItems = (await webCandidates.ListResearchAsync(projectId, cancellationToken))
            .Where(source => source.WorldConversationId == conversation.Id
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
        using var maintenance = turnRuntime.TryBeginMaintenance(new ChatTurnKey(projectId, ChatTurnSurface.World));
        if (maintenance is null)
            throw new InvalidOperationException("World is still working in another window. Stop or wait for that turn before resetting the conversation.");
        await imageAttachments.ClearSurfaceAsync(projectId, ChatTurnSurface.World, cancellationToken);
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var conversations = databaseOperation.Repositories.WorldConversations;
        var existing = await conversations.GetByProjectIdAsync(projectId, cancellationToken);
        if (existing is null) return;

        await conversations.ResetMessagesAsync(existing, new WorldMessage
        {
            ConversationId = existing.Id,
            Order = 0,
            Role = WorldMessageRole.Assistant,
            Content = InitialAssistantGreeting,
            Status = WorldMessageStatus.Completed,
        }, cancellationToken);
        await databaseOperation.SaveChangesAsync(cancellationToken);
    }

    public async IAsyncEnumerable<WorldTurnUpdate> SendAsync(
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
            logger.LogError(ex, "World turn setup failed for project {ProjectId}", projectId);
            preflightError = ex.Message;
            project = null!;
            providerAvailability = null!;
        }

        if (preflightError is not null)
        {
            yield return new WorldTurnError(preflightError, Cancelled: false);
            yield break;
        }
        var chatProvider = providerAvailability.Provider!;
        var visionReady = await providerService.IsVisionProviderWorkingAsync(chatProvider.Id, cancellationToken);
        if (imageIds.Count > 0 && !visionReady)
        {
            yield return new WorldTurnError("The active chat provider has not passed the vision check. Run Test in Settings > Providers before sending images.", Cancelled: false);
            yield break;
        }
        await imageAttachments.ResolveAsync(projectId, imageIds, cancellationToken);

        var nextOrder = await turnEngine.ReadAsync(
            repositories => repositories.WorldConversations,
            conversations => conversations.GetMaxOrderAsync(conversation.Id, cancellationToken),
            cancellationToken) + 1;
        var userMessage = new WorldMessage
        {
            ConversationId = conversation.Id,
            Order = nextOrder++,
            Role = WorldMessageRole.User,
            Content = userText.Trim(),
            Status = WorldMessageStatus.Completed,
        };
        conversation.UpdatedAt = DateTime.UtcNow;
        await turnEngine.AddMessageAsync(repositories => repositories.WorldConversations, userMessage, cancellationToken);
        await imageAttachments.PersistAsync(projectId, ChatTurnSurface.World, userMessage.Id, imageIds, cancellationToken);

        IChatClient chat = null!;
        IList<AITool> aiTools = null!;
        string systemPrompt = string.Empty;
        ContextAssembly? initialAssembly = null;
        WorldToolContext? toolContext = null;
        string? setupError = null;
        try
        {
            chat = await chatClientFactory.CreateChatClientAsync(chatProvider.Id, cancellationToken);
            initialAssembly = await contextBuilder.BuildAsync(
                new ContextBuildRequest(
                    project,
                    UserMessage: userText,
                    Purpose: ContextBuildPurpose.World,
                    OperatingRules: WorldWorkflowInstructions
                        + "\n\n" + AssistantWorkflowInstructions.NonReplayedToolHistory
                        + "\n\n" + AssistantWorkflowInstructions.EntityVisualExamples),
                cancellationToken);
            systemPrompt = initialAssembly.Assemble();
            // World tools always mutate the live project. Review derives pending work from
            // the version-history baseline rather than a per-turn staging overlay.
            toolContext = new WorldToolContext(projectId, conversation.Id, OnToolMutated, visionReady: visionReady,
                modelWebSearch: chat.GetService<IModelWebSearch>());
            aiTools = await tools.BuildAsync(toolContext, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "World turn setup failed for project {ProjectId}", projectId);
            await PersistFailedAssistantAsync(conversation.Id, nextOrder, ex.Message);
            setupError = ex.Message;
        }

        if (setupError is not null)
        {
            yield return new WorldTurnError(setupError, Cancelled: false);
            yield break;
        }

        var chatOptions = new ChatOptions
        {
            Tools = aiTools,
            ToolMode = ChatToolMode.Auto,
        };
        var history = await turnEngine.ReadAsync(
            repositories => repositories.WorldConversations,
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
            var replay = ChatModelHistory.Project(persistedMessage.Role.ToString(), persistedMessage.Content, persistedMessage.ResponseMetadataJson, chatProvider);
            if (replay is not null) messages.Add(replay);
        }

        var maxIterations = Math.Max(1, options.Value.MaxToolIterations);
        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            var activeAssistant = new WorldMessage
            {
                ConversationId = conversation.Id,
                Order = nextOrder++,
                Role = WorldMessageRole.Assistant,
                Content = string.Empty,
                Status = WorldMessageStatus.Pending,
            };
            await turnEngine.AddMessageAsync(repositories => repositories.WorldConversations, activeAssistant, cancellationToken);

            if (turnEngine.TryCompactContext(messages, chatProvider.ModelId, chatProvider.EffectiveMaxInputTokens) is { } compaction)
            {
                yield return new WorldContextTrimmed(compaction);
                if (compaction.LimitExceeded)
                {
                    activeAssistant.Status = WorldMessageStatus.Failed;
                    activeAssistant.ErrorMessage = ChatContextCompaction.LimitExceededMessage;
                    await SafePersistAsync(activeAssistant);
                    yield return new WorldTurnError(activeAssistant.ErrorMessage, Cancelled: false);
                    yield break;
                }
            }

            ChatRoundCompleted? completedRound = null;
            await foreach (var update in turnEngine.StreamRoundAsync(chat, messages, chatOptions, cancellationToken))
            {
                switch (update)
                {
                    case ChatRoundTextDelta text:
                        yield return new WorldTextDelta(text.Text);
                        break;
                    case ChatRoundReasoningDelta reasoning:
                        yield return new WorldReasoningDelta(reasoning.Text);
                        break;
                    case ChatRoundToolCallStarted started:
                        yield return new WorldToolCallStarted(started.CallId, started.ToolName, started.ArgumentsJson, started.ArgumentsComplete);
                        break;
                    case ChatRoundToolCallArgumentsDelta delta:
                        yield return new WorldToolCallArgumentsDelta(delta.CallId, delta.ArgumentsDelta, delta.ArgumentsComplete);
                        break;
                    case ChatRoundCompleted completed:
                        completedRound = completed;
                        break;
                    case ChatRoundFailed failed:
                        activeAssistant.Content = failed.Text;
                        activeAssistant.ResponseMetadataJson = failed.Metadata?.Serialize();
                        if (!string.IsNullOrEmpty(failed.Reasoning))
                            activeAssistant.Reasoning = failed.Reasoning;
                        activeAssistant.Status = failed.Cancelled
                            ? WorldMessageStatus.Cancelled
                            : WorldMessageStatus.Failed;
                        activeAssistant.ErrorMessage = failed.Cancelled ? "Cancelled by user." : failed.Message;
                        await SafePersistAsync(activeAssistant);
                        yield return new WorldTurnError(failed.Message, failed.Cancelled);
                        yield break;
                }
            }

            DrainMutated();
            if (completedRound is null)
            {
                activeAssistant.Status = WorldMessageStatus.Failed;
                activeAssistant.ErrorMessage = "World streaming ended without a completed round.";
                await SafePersistAsync(activeAssistant);
                yield return new WorldTurnError(activeAssistant.ErrorMessage, Cancelled: false);
                yield break;
            }

            var textBuilder = new StringBuilder(completedRound.Text);
            var pendingCalls = completedRound.ToolCalls;
            activeAssistant.Reasoning = completedRound.Reasoning;
            activeAssistant.ResponseMetadataJson = completedRound.Metadata.Serialize();

            if (pendingCalls.Count == 0)
            {
                activeAssistant.Content = textBuilder.ToString();
                activeAssistant.Status = WorldMessageStatus.Completed;
                await SafePersistAsync(activeAssistant);
                yield return new WorldAssistantMessageCompleted(activeAssistant.Id);
                yield break;
            }

            var manifest = pendingCalls
                .Select(ChatToolCallManifest.From)
                .ToList();
            activeAssistant.Content = textBuilder.ToString();
            activeAssistant.ToolCallsJson = JsonSerializer.Serialize(manifest);
            activeAssistant.Status = WorldMessageStatus.Completed;
            await SafePersistAsync(activeAssistant);
            messages.Add(new ChatMessage(ChatRole.Assistant, ChatTurnEngine.BuildAssistantContents(textBuilder.ToString(), pendingCalls, completedRound.Reasoning, completedRound.Metadata)));

            var resultContents = new List<AIContent>();
            foreach (var pendingCall in pendingCalls)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    yield return new WorldTurnError("Cancelled.", Cancelled: true);
                    yield break;
                }

                var sw = Stopwatch.StartNew();
                var toolOutcome = await turnEngine.InvokeToolAsync(aiTools, pendingCall, cancellationToken);
                sw.Stop();

                if (toolOutcome.Cancelled)
                {
                    yield return new WorldTurnError("Cancelled.", Cancelled: true);
                    yield break;
                }

                var toolResult = toolOutcome.Result;
                var toolError = toolOutcome.Error;

                var toolMessage = new WorldMessage
                {
                    ConversationId = conversation.Id,
                    Order = nextOrder++,
                    Role = WorldMessageRole.Tool,
                    Content = toolResult ?? string.Empty,
                    ToolCallId = pendingCall.CallId,
                    ToolName = pendingCall.Name,
                    Status = toolError is null ? WorldMessageStatus.Completed : WorldMessageStatus.Failed,
                    ErrorMessage = toolError,
                };
                await turnEngine.AddMessageAsync(repositories => repositories.WorldConversations, toolMessage, CancellationToken.None);

                resultContents.Add(new FunctionResultContent(pendingCall.CallId, toolResult ?? string.Empty));
                yield return new WorldToolCallCompleted(
                    pendingCall.CallId,
                    pendingCall.Name,
                    toolError is null ? toolResult : null,
                    toolError,
                    sw.Elapsed.TotalMilliseconds);

                if (DrainMutated())
                    yield return new WorldMutated();
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


            if (iteration == maxIterations - 1)
            {
                yield return new WorldTurnError(
                    ChatTurnEngine.ToolLoopLimitError(maxIterations),
                    Cancelled: false);
                yield break;
            }
        }
    }

    private static Dictionary<string, ChatToolCallManifest> BuildToolCallLookup(IEnumerable<WorldMessage> history)
    {
        var result = new Dictionary<string, ChatToolCallManifest>(StringComparer.Ordinal);
        foreach (var message in history.Where(message => message.Role == WorldMessageRole.Assistant))
        {
            foreach (var call in ReadPersistedToolCalls(message.ToolCallsJson))
                result[call.CallId] = call;
        }

        return result;
    }

    private static void AddToolEntityTouches(
        IDictionary<Guid, EntityTouch> touches,
        WorldMessage message,
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

        if (properties is not null)
        {
            touch.Properties ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in properties)
                touch.Properties[property.Key] = property.Value;
        }
    }

    private static int StatePriority(ResearchEntityActivityState state) => state switch
    {
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
                Purpose: ContextBuildPurpose.World,
                OperatingRules: WorldWorkflowInstructions
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
        var conversations = databaseOperation.Repositories.WorldConversations;
        try
        {
            await conversations.AddMessageAsync(new WorldMessage
            {
                ConversationId = conversationId,
                Order = order,
                Role = WorldMessageRole.Assistant,
                Status = WorldMessageStatus.Failed,
                ErrorMessage = error,
            }, CancellationToken.None);
            await databaseOperation.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist World setup failure");
        }
    }

    private async Task SafePersistAsync(WorldMessage message)
    {
        try
        {
            await turnEngine.UpdateMessageAsync(repositories => repositories.WorldConversations, message, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist World message {MessageId}", message.Id);
        }
    }

    private sealed class EntityTouch(Guid entityId)
    {
        public Guid EntityId { get; } = entityId;
        public string Type { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public ResearchEntityActivityState State { get; set; } = ResearchEntityActivityState.Read;
        public string Preview { get; set; } = string.Empty;
        public DateTime LastTouchedAt { get; set; } = DateTime.MinValue;
        public Dictionary<string, string?>? Properties { get; set; }
    }

}
