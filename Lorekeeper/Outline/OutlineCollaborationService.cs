using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Lorekeeper.ChatTurns;
using Lorekeeper.Context;
using Lorekeeper.EntityVisuals;
using Lorekeeper.Llm;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Projects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Lorekeeper.Outline;

public sealed class OutlineCollaborationService(
 IAppDatabaseOperationFactory database, IChatImageAttachmentService imageAttachments, ILlmProviderService providerService, IChatClientFactory chatClientFactory, OutlineCollaborationTools tools, IEntityVisualContextService entityVisualContext, IBookBriefService bookBriefs, ISystemPromptComposer systemPrompts, IOutlineWorkingContextBuilder workingContext, IProjectReferenceService projectReferences, ChatTurnRuntime turnRuntime, ChatTurnEngine turnEngine, IOptions<AgentOptions> options, ILogger<OutlineCollaborationService> logger) : IOutlineCollaborationService
{
    /// <summary>
    /// Code-owned operating rules composed with the professional charter, Project Guidance,
    /// and Book Brief into the actual system-role message for every turn.
    /// </summary>
    public static readonly string CollaborationOperatingRules = $$"""
Outline collaboration rules:
- You are a partner, not an oracle. Ask questions, propose options, and
    surface trade-offs. Do not dump a full outline up front.
- Do not write the outline as prose in chat. The outline lives in the
    tools (acts, chapters, beats, entities, links, and only occasional
    project facts). Chat is for thinking together.
- Keep replies short. No headings, no bullet lists unless the user asked
    for them, no emojis. Plain conversational prose.
- Narrate tool work briefly. Before a tool call, say what you are checking
    or changing in one short clause. After tool results, briefly say what
    changed or what still needs the user's input. Do not expose raw JSON
    unless the user asks.

When to use tools:
- CREATING new things: if the user describes a new act, chapter,
    character, location, beat, or relationship clearly enough to persist,
    create or link it with the appropriate tool.
- EDITING, DELETING, REORDERING, or LINKING existing outline items and
    entities: once you have enough information to infer the user's intent,
    make the change with the appropriate tool. Ask only when the target or
    desired outcome is genuinely ambiguous.
- The Book Brief is the canonical home for book kind, premise, genre, themes,
    purpose, audience, reader ages/level, target length, POV, tense, voice/tone,
    language/locale, house style, read-aloud priority, accessibility goals,
    visual direction, and non-negotiable creative constraints. Use
    update_book_brief whenever the user commits to one of these directions.
    Book Brief updates apply directly even when Review edits is enabled.
- The Book Brief also owns canonical-source selection. Change that selection
    only when the user explicitly asks; use update_book_brief_canon_sources
    with the complete desired source-id list.
- The system context includes the complete current outline, chapter entity
    attachments, a compact inventory for every entity category, and an
    ingested-source inventory. Do not claim these are absent without reading it.
- Selected canonical sources are authoritative grounding. Unselected ingested
    sources are useful evidence, not canon. Search and read them when useful,
    but do not silently promote their claims to canon.
- If source material conflicts with an explicit user instruction, the current
    Book Brief, or the current outline, preserve the user/Book Brief/outline
    direction and report the conflict instead of silently resolving it.
- Treat missing premise, book kind, audience, purpose, genre, and relevant
    narrative choices as early outlining priorities. Ask one or two focused
    questions at a time. Do not block a concrete outline request because
    optional fields remain unspecified. Revisit the brief when outline changes
    alter audience, format, premise, POV, tone, length, or visual strategy.
- Do not create new outline.* ProjectFacts for fields owned by the Book Brief.
    Existing overlapping facts remain evidence until you write a clearly
    equivalent brief field, then remove the redundant fact. Keep ambiguous or
    genuinely canonical world/continuity constraints as facts.
- Use ProjectFact sparingly for durable canon or global constraints with no
    better home in the Book Brief, acts, chapters, beats, entities, or links.
- Do not create ProjectFacts for normal outline content: act/chapter plans,
    scene beats, character roles, relationship changes, location details, or
    rework notes. Store those in the relevant act/chapter synopsis, beat,
    character/location/entity property, or relationship link.
- Every persisted outline field must read as present canonical truth, as though
    the current version is the only draft. This applies to ProjectFacts, titles,
    synopses, beats, entity properties, and relationship descriptions. Chat may
    discuss the revision process; saved outline content must not.
- When the user asks for a rework, replace the affected story state directly.
    Never preserve or imply comparison with an earlier draft through editorial
    framing or revision-relative words such as 'still', 'now', 'continues to',
    'remains', 'no longer', 'rather than', 'instead of', 'retains', 'unchanged',
    'as before', 'reworked to', or 'changed so that'. Those words are permitted
    only when they express an actual in-story timeline or contrast that matters
    to the canon. If removing the comparison leaves the story meaning intact,
    remove it.
- Rewrite the complete affected sentence or field so it stands alone, then
    audit the other summaries and entity data touched by the same rework for
    leftover draft-relative language. For example, replace 'These characters
    still distrust Rommath' with 'These characters distrust Rommath' when the
    continuity exists only against an earlier draft.
- Bad outputs to avoid: a ProjectFact titled 'Act 2 rework: Rommath now main
    supporting character', or an act/chapter synopsis that says 'Rommath
    remains a supporting character rather than a rival'. Instead, rewrite Act
    2's synopsis, Rommath's role/links, the main character's role/links, and any
    relevant beats so they simply state the current story plan.
- For chapters, create or revise beats when the user's direction gives
    you enough information to do so usefully.

{{AssistantWorkflowInstructions.OutlineChat}}

{{AssistantWorkflowInstructions.CanonicalAppearanceImages}}

{{AssistantWorkflowInstructions.ImageSpaceDiscipline}}

{{AssistantWorkflowInstructions.NonReplayedToolHistory}}

{{AssistantWorkflowInstructions.EntityVisualExamples}}

Entity conventions:
- The outline spine is also represented in the graph: Project -> Act ->
    Chapter -> Event/Beat through HasChild links. Use the outline tools for
    Act and Chapter edits because those rows have stricter editor behavior.
- Use list_entity_types when you need to inspect what graph types exist.
    Use list_entities for concise paginated browsing, search_entities for
    focused discovery, and read_entity for the complete record including its
    origin and source evidence. Use create_entity / update_entity /
    delete_entity for story entities.
    Pass the type as a string. Common types are:
    * 'Character' — project-scoped people. Conventional properties:
        role, description.
    * 'Location' — project-scoped places. Conventional properties:
        description.
    * 'ProjectFact' — rare canonical or global constraint with no better
        home. Book kind, premise, genre, themes, purpose, audience, length,
        POV, tense, voice/tone, language, house style, read-aloud priority,
        accessibility, visual direction, and creative constraints belong in
        the Book Brief instead. Conventional fact properties: key, value.
        Omit parentId; the tool attaches it to the Project automatically.
    * 'Event' — chapter-scoped beats. REQUIRES parentId=<chapter id>.
        Conventional properties: summary.
- Create durable entities for every canonical person, named or materially
    recurring creature, place, object/artifact, vehicle, organization/faction,
    culture, species, system, concept, ritual, and historical event the outline
    must track. Do not create entities for generic scenery, incidental mentions,
    transient actions, or unnamed one-off objects with no continuity value.
- Use link_entities to create relationships between entities.
    Conventional edge types:
    * 'About'      — ProjectFact -> Project/Act/Chapter/entity it broadly constrains.
    * 'SetIn'      — ProjectFact -> Location for broad setting rules.
    * 'Constrains' — ProjectFact -> Act/Chapter/Project for tone, scope, or rules.
    * 'RelevantTo' — any canonical entity -> Chapter. This preferred chapter
        association is what Editor uses for automatic context.
    * 'AppearsIn' — Character -> Event/beat only.
    * 'LocatedAt' — Event -> Location.
    * 'KnownTo'   — Character -> Character.
    Other edge types are allowed; prefer camel-case verbs.
- Prefer chapter-level RelevantTo links over beat-level links for context-bearing
    associations. Beat links are optional detail and never a substitute for the
    chapter link. Every canonical entity that appears in, affects, constrains,
    or otherwise matters to a chapter must have a RelevantTo link to it, even
    if the entity is not physically present in the scene.

When the user is exploring or undecided, propose options and wait. When
they commit to a direction, act on it without a second confirmation.
""";

    private const string InitialAssistantGreeting =
        "Let's build your outline together. To start, can you tell me what your story is about — even just a sentence or two?";

    public async Task<OutlineConversation> GetOrCreateAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var projects = databaseOperation.Repositories.Projects;
        var conversations = databaseOperation.Repositories.OutlineConversations;
        var existing = await conversations.GetByProjectIdAsync(projectId, cancellationToken);
        if (existing is not null) return existing;

        _ = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");

        var conversation = new OutlineConversation { ProjectId = projectId };
        await conversations.AddConversationAsync(conversation, cancellationToken);

        var greeting = new OutlineMessage
        {
            ConversationId = conversation.Id,
            Order = 0,
            Role = OutlineMessageRole.Assistant,
            Content = InitialAssistantGreeting,
            Status = OutlineMessageStatus.Completed,
        };
        await conversations.AddMessageAsync(greeting, cancellationToken);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        return conversation;
    }

    public async Task<IReadOnlyList<OutlineMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var conversations = databaseOperation.Repositories.OutlineConversations;
        return await conversations.LoadMessagesAsync(conversationId, cancellationToken);
    }

    public async Task<ChatProviderAvailability> GetChatProviderAvailabilityAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var conversation = await databaseOperation.Repositories.OutlineConversations
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
        using var maintenance = turnRuntime.TryBeginMaintenance(new ChatTurnKey(projectId, ChatTurnSurface.Outline));
        if (maintenance is null)
            throw new InvalidOperationException("Outline Chat is still working in another window. Stop or wait for that turn before changing its model.");

        if (providerId is int)
        {
            var selection = await providerService.ResolveChatModelSelectionAsync(providerId, cancellationToken);
            if (!selection.IsAvailable || selection.Provider is null)
                throw new InvalidOperationException(selection.Message);
        }

        var normalizedProviderId = await providerService.NormalizeChatModelSelectionAsync(providerId, cancellationToken);

        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var conversation = await databaseOperation.Repositories.OutlineConversations
            .GetByProjectIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException("Outline Chat is not initialized.");
        conversation.SelectedProviderId = normalizedProviderId;
        conversation.UpdatedAt = DateTime.UtcNow;
        databaseOperation.Repositories.OutlineConversations.UpdateSelectedProvider(conversation);
        await databaseOperation.SaveChangesAsync(cancellationToken);
    }
    public async Task<string> GetSystemPromptAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var projects = databaseOperation.Repositories.Projects;
        var project = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");
        return await ComposeSystemPromptAsync(project, cancellationToken);
    }

    public async Task ResetAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        using var maintenance = turnRuntime.TryBeginMaintenance(new ChatTurnKey(projectId, ChatTurnSurface.Outline));
        if (maintenance is null)
            throw new InvalidOperationException("Outline Chat is still working in another window. Stop or wait for that turn before resetting the conversation.");
        await imageAttachments.ClearSurfaceAsync(projectId, ChatTurnSurface.Outline, cancellationToken);
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var conversations = databaseOperation.Repositories.OutlineConversations;
        var existing = await conversations.GetByProjectIdAsync(projectId, cancellationToken);
        if (existing is null) return;
        await conversations.ResetMessagesAsync(existing, new OutlineMessage
        {
            ConversationId = existing.Id,
            Order = 0,
            Role = OutlineMessageRole.Assistant,
            Content = InitialAssistantGreeting,
            Status = OutlineMessageStatus.Completed,
        }, cancellationToken);
        await databaseOperation.SaveChangesAsync(cancellationToken);
    }

    public async IAsyncEnumerable<OutlineTurnUpdate> SendAsync(
        Guid projectId,
        string userText,
        IReadOnlyList<Guid> imageIds,
        int providerId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userText))
            throw new ArgumentException("Message cannot be empty.", nameof(userText));

        var conversation = await GetOrCreateAsync(projectId, cancellationToken);

        var persistedSelection = await providerService.ResolveChatModelSelectionAsync(
            conversation.SelectedProviderId,
            cancellationToken);
        if (!persistedSelection.IsAvailable || persistedSelection.Provider is not { } persistedProvider)
        {
            yield return new TurnError(persistedSelection.Message, Cancelled: false);
            yield break;
        }

        if (persistedProvider.Id != providerId)
        {
            yield return new TurnError(ChatModelSelectionMessages.Changed, Cancelled: false);
            yield break;
        }

        var visionReady = await providerService.IsVisionProviderWorkingAsync(persistedProvider.Id, cancellationToken);
        if (imageIds.Count > 0 && !visionReady)
        {
            yield return new TurnError("The active chat provider has not passed the vision check. Run Test in Settings > Providers before sending images.", Cancelled: false);
            yield break;
        }
        await imageAttachments.ResolveAsync(projectId, imageIds, cancellationToken);

        // Persist the user message immediately so it appears in history even if the LLM call fails.
        var nextOrder = await turnEngine.ReadAsync(
            repositories => repositories.OutlineConversations,
            conversations => conversations.GetMaxOrderAsync(conversation.Id, cancellationToken),
            cancellationToken) + 1;
        var userMsg = new OutlineMessage
        {
            ConversationId = conversation.Id,
            Order = nextOrder++,
            Role = OutlineMessageRole.User,
            Content = userText.Trim(),
            Status = OutlineMessageStatus.Completed,
        };
        conversation.UpdatedAt = DateTime.UtcNow;
        await turnEngine.AddMessageAsync(repositories => repositories.OutlineConversations, userMsg, cancellationToken);
        await imageAttachments.PersistAsync(projectId, ChatTurnSurface.Outline, userMsg.Id, imageIds, cancellationToken);

        // Resolve the chat client + tools up front so any wiring failure surfaces before we start streaming.
        IChatClient chat = null!;
        IList<AITool> aiTools = null!;
        OutlineCollaborationContext? toolContext = null;
        var systemPrompt = string.Empty;
        string? setupError = null;
        try
        {
            var project = await turnEngine.ReadAsync(
                repositories => repositories.Projects,
                projects => projects.GetByIdAsync(projectId, cancellationToken),
                cancellationToken)
                ?? throw new InvalidOperationException($"Project {projectId} not found.");
            systemPrompt = await ComposeSystemPromptAsync(project, cancellationToken);
            chat = await chatClientFactory.CreateChatClientAsync(persistedProvider.Id, cancellationToken);

            // All outline tool mutations are applied immediately to the live project. Review
            // derives pending work from the version-history baseline rather than a staging overlay.
            toolContext = new OutlineCollaborationContext(projectId, OnToolMutated, visionReady: visionReady);
            aiTools = await tools.BuildAsync(toolContext, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Outline turn setup failed for project {ProjectId}", projectId);
            setupError = ex.Message;
        }
        if (setupError is not null)
        {
            yield return new TurnError(setupError, Cancelled: false);
            yield break;
        }

        var chatOptions = new ChatOptions
        {
            Tools = aiTools,
            ToolMode = ChatToolMode.Auto,
        };

        // Build the running message list from persisted history (already includes the user msg above).
        var history = await turnEngine.ReadAsync(
            repositories => repositories.OutlineConversations,
            conversations => conversations.LoadMessagesAsync(conversation.Id, cancellationToken),
            cancellationToken);
        var messages = new List<ChatMessage> { new(ChatRole.System, systemPrompt) };
        foreach (var persistedMessage in history)
        {
            if (persistedMessage.Id == userMsg.Id && imageIds.Count > 0)
            {
                messages.Add(await imageAttachments.BuildUserMessageAsync(projectId, persistedMessage.Content, imageIds, cancellationToken: cancellationToken));
                continue;
            }
            var replay = ChatModelHistory.Project(persistedMessage.Role.ToString(), persistedMessage.Content);
            if (replay is not null) messages.Add(replay);
        }

        var maxIterations = Math.Max(1, options.Value.MaxToolIterations);
        OutlineMessage? activeAssistant = null;

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            // Pre-create the assistant row so failures still leave a trace.
            activeAssistant = new OutlineMessage
            {
                ConversationId = conversation.Id,
                Order = nextOrder++,
                Role = OutlineMessageRole.Assistant,
                Content = string.Empty,
                Status = OutlineMessageStatus.Pending,
            };
            await turnEngine.AddMessageAsync(repositories => repositories.OutlineConversations, activeAssistant, cancellationToken);

            ChatRoundCompleted? completedRound = null;
            await foreach (var update in turnEngine.StreamRoundAsync(chat, messages, chatOptions, cancellationToken))
            {
                switch (update)
                {
                    case ChatRoundTextDelta text:
                        yield return new TextDelta(text.Text);
                        break;
                    case ChatRoundReasoningDelta reasoning:
                        yield return new ReasoningDelta(reasoning.Text);
                        break;
                    case ChatRoundToolCallStarted started:
                        yield return new ToolCallStarted(started.CallId, started.ToolName, started.ArgumentsJson, started.ArgumentsComplete);
                        break;
                    case ChatRoundToolCallArgumentsDelta delta:
                        yield return new ToolCallArgumentsDelta(delta.CallId, delta.ArgumentsDelta, delta.ArgumentsComplete);
                        break;
                    case ChatRoundCompleted completed:
                        completedRound = completed;
                        break;
                    case ChatRoundFailed failed:
                        activeAssistant.Content = failed.Text;
                        if (!string.IsNullOrEmpty(failed.Reasoning))
                            activeAssistant.Reasoning = failed.Reasoning;
                        activeAssistant.Status = failed.Cancelled
                            ? OutlineMessageStatus.Cancelled
                            : OutlineMessageStatus.Failed;
                        activeAssistant.ErrorMessage = failed.Cancelled ? "Cancelled by user." : failed.Message;
                        await SafePersistAsync(activeAssistant);
                        yield return new TurnError(failed.Message, failed.Cancelled);
                        yield break;
                }
            }

            DrainMutated();
            if (completedRound is null)
            {
                activeAssistant.Status = OutlineMessageStatus.Failed;
                activeAssistant.ErrorMessage = "Outline streaming ended without a completed round.";
                await SafePersistAsync(activeAssistant);
                yield return new TurnError(activeAssistant.ErrorMessage, Cancelled: false);
                yield break;
            }

            var textBuilder = new StringBuilder(completedRound.Text);
            var pendingCalls = completedRound.ToolCalls;
            activeAssistant.Reasoning = completedRound.Reasoning;

            // No tool calls -> final turn.
            if (pendingCalls.Count == 0)
            {
                activeAssistant.Content = textBuilder.ToString();
                activeAssistant.Status = OutlineMessageStatus.Completed;
                await SafePersistAsync(activeAssistant);
                yield return new AssistantMessageCompleted(activeAssistant.Id);
                yield break;
            }

            // Tool round: persist this assistant row with text + tool-call manifest, then invoke each.
            var manifest = pendingCalls
                .Select(ChatToolCallManifest.From)
                .ToList();
            activeAssistant.Content = textBuilder.ToString();
            activeAssistant.ToolCallsJson = JsonSerializer.Serialize(manifest);
            activeAssistant.Status = OutlineMessageStatus.Completed;
            await SafePersistAsync(activeAssistant);

            // Append to in-memory message list as a single assistant message with tool calls,
            // matching what the model emitted (text + FunctionCallContent[]).
            messages.Add(new ChatMessage(ChatRole.Assistant, ChatTurnEngine.BuildAssistantContents(textBuilder.ToString(), pendingCalls, completedRound.Reasoning)));

            var resultContents = new List<AIContent>();
            foreach (var pendingCall in pendingCalls)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    yield return new TurnError("Cancelled.", Cancelled: true);
                    yield break;
                }

                var sw = Stopwatch.StartNew();
                var toolOutcome = await turnEngine.InvokeToolAsync(aiTools, pendingCall, cancellationToken);
                sw.Stop();
                if (toolOutcome.Cancelled)
                {
                    yield return new TurnError("Cancelled.", Cancelled: true);
                    yield break;
                }

                var toolResult = toolOutcome.Result;
                var toolError = toolOutcome.Error;

                // Persist a Tool row.
                var toolMsg = new OutlineMessage
                {
                    ConversationId = conversation.Id,
                    Order = nextOrder++,
                    Role = OutlineMessageRole.Tool,
                    Content = toolResult ?? string.Empty,
                    ToolCallId = pendingCall.CallId,
                    ToolName = pendingCall.Name,
                    Status = toolError is null ? OutlineMessageStatus.Completed : OutlineMessageStatus.Failed,
                    ErrorMessage = toolError,
                };
                await turnEngine.AddMessageAsync(repositories => repositories.OutlineConversations, toolMsg, CancellationToken.None);

                resultContents.Add(new FunctionResultContent(pendingCall.CallId, toolResult ?? string.Empty));
                yield return new ToolCallCompleted(pendingCall.CallId, pendingCall.Name, toolError is null ? toolResult : null, toolError, sw.Elapsed.TotalMilliseconds);

                if (DrainMutated())
                    yield return new OutlineMutated();
            }

            messages.Add(new ChatMessage(ChatRole.Tool, resultContents));
            if (toolContext is not null)
            {
                var visualMessage = await entityVisualContext.BuildVisionMessageAsync(
                    projectId,
                    toolContext.DrainVisuals(),
                    toolContext.VisionReady,
                    "Canonical visual references for entities loaded by the preceding tools. Use them for stable identity/design grounding; do not treat ordinary scenes as entity mappings.",
                    cancellationToken);
                if (visualMessage is not null)
                    messages.Add(ChatTurnEngine.MarkToolContextMessage(visualMessage));

                var referenceVisuals = toolContext.DrainReferenceVisuals();
                if (toolContext.VisionReady && referenceVisuals.Count > 0)
                {
                    var contents = new List<AIContent>
                    {
                        new TextContent("Direct-reference canonical visuals from the preceding read_reference_visual calls. These are read-only continuity evidence; active-project canon and user direction remain authoritative, and these images cannot be placed or mutated."),
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

            if (turnEngine.TryCompactContext(messages, persistedProvider.ModelId, persistedProvider.MaxInputTokens) is { } compaction)
            {
                yield return new ContextTrimmed(compaction);
                if (compaction.LimitExceeded)
                {
                    activeAssistant.Status = OutlineMessageStatus.Failed;
                    activeAssistant.ErrorMessage = ChatContextCompaction.LimitExceededMessage;
                    await SafePersistAsync(activeAssistant);
                    yield return new TurnError(activeAssistant.ErrorMessage, Cancelled: false);
                    yield break;
                }
            }

            if (iteration == maxIterations - 1)
            {
                yield return new TurnError(ChatTurnEngine.ToolLoopLimitError(maxIterations), Cancelled: false);
                yield break;
            }
        }
    }

    private async Task<string> ComposeSystemPromptAsync(Project project, CancellationToken cancellationToken)
    {
        var brief = await bookBriefs.GetOrCreateAsync(project.Id, cancellationToken);
        var context = await workingContext.BuildAsync(project.Id, cancellationToken);
        var prompt = systemPrompts.Compose(new(
            project,
            brief,
            SystemPromptAgentRole.Outline,
            CollaborationOperatingRules,
            WorkingContext: context)).Prompt;
        var referenceManifest = ProjectReferenceManifestFormatter.Format(
            await projectReferences.ListReferenceManifestsAsync(project.Id, cancellationToken));
        return referenceManifest is null
            ? prompt
            : prompt + "\n\n## Direct Project Reference Continuity\n" + referenceManifest;
    }

    // -- mutation flag (drained between yields so the UI can refresh the tree) --

    private bool _mutatedSinceYield;
    private void OnToolMutated() => _mutatedSinceYield = true;
    private bool DrainMutated()
    {
        if (!_mutatedSinceYield) return false;
        _mutatedSinceYield = false;
        return true;
    }

    private async Task SafePersistAsync(OutlineMessage message)
    {
        try
        {
            await turnEngine.UpdateMessageAsync(repositories => repositories.OutlineConversations, message, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist outline message {MessageId}", message.Id);
        }
    }

    // -- history → ChatMessage replay --

}
