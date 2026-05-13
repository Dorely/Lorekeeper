using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Lorekeeper.Llm;
using Lorekeeper.Models;
using Lorekeeper.Persistence.Repositories;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Lorekeeper.Outline;

public sealed class OutlineCollaborationService(
    IProjectRepository projects,
    IOutlineConversationRepository conversations,
    ILlmProviderService providerService,
    IChatClientFactory chatClientFactory,
    OutlineCollaborationTools tools,
    IAiChangeApprovalService changeApproval,
    IOptions<AgentOptions> options,
    ILogger<OutlineCollaborationService> logger) : IOutlineCollaborationService
{
    /// <summary>
    /// System prompt that frames the assistant as a writing collaborator. Hardcoded by design:
    /// it is independent of the user's project-level <c>SystemPrompt</c> (which targets the
    /// chapter editor chat). Kept terse to leave room in the context window for the
    /// growing conversation history.
    /// </summary>
    public static readonly string CollaborationSystemPrompt = $$"""
You are a story-outline collaborator. Your job is to help the user discover
and shape their story's structure through a back-and-forth conversation.

How to work:
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
- Use ProjectFact sparingly. Project facts are for durable project-level
    guidance that has no better home in acts, chapters, beats, entities, or
    links: premise, genre, tone, scope, theme, narration rules, setting-wide
    constraints, continuity rules, or other global assumptions. Use
    properties {"key":"outline.premise","value":"..."} with the
    outline.* namespace for outline-level facts. Update existing facts
    instead of creating duplicates when list_outline shows a matching key.
- Do not create ProjectFacts for normal outline content: act/chapter plans,
    scene beats, character roles, relationship changes, location details, or
    rework notes. Store those in the relevant act/chapter synopsis, beat,
    character/location/entity property, or relationship link.
- When the user asks for a rework, update the affected story state directly
    as the new canonical version. Do not preserve the decision process in
    ProjectFacts, titles, synopses, beats, or entity properties. Synopses
    should describe the story, not the edit history.
- Bad outputs to avoid: a ProjectFact titled 'Act 2 rework: Rommath now main
    supporting character', or an act/chapter synopsis that says 'Changed so
    that now Rommath is a key character'. Instead, rewrite Act 2's synopsis,
    Rommath's role/links, The main character's role/links, and any relevant beats so
    they simply state the current story plan.
- For chapters, create or revise beats when the user's direction gives
    you enough information to do so usefully.

{{AssistantWorkflowInstructions.OutlineChat}}

Entity conventions:
- The outline spine is also represented in the graph: Project -> Act ->
    Chapter -> Event/Beat through HasChild links. Use the outline tools for
    Act and Chapter edits because those rows have stricter editor behavior.
- Use list_entity_types when you need to inspect what graph types exist.
    Use create_entity / update_entity / delete_entity for story entities.
    Pass the type as a string. Common types are:
    * 'Character' — project-scoped people. Conventional properties:
        role, description.
    * 'Location' — project-scoped places. Conventional properties:
        description.
    * 'ProjectFact' — rare project-level guidance with no better structural
        home, such as premise, genre, tone, theme, scope, or global rules.
        Conventional properties: key, value. Omit parentId; the tool
        attaches ProjectFact nodes to the Project automatically. Do not use
        ProjectFact for rework notes or ordinary act/chapter/entity details.
    * 'Event' — chapter-scoped beats. REQUIRES parentId=<chapter id>.
        Conventional properties: summary.
- Use link_entities to create relationships between entities.
    Conventional edge types:
    * 'About'      — ProjectFact -> Project/Act/Chapter/entity it broadly constrains.
    * 'SetIn'      — ProjectFact -> Location for broad setting rules.
    * 'Constrains' — ProjectFact -> Act/Chapter/Project for tone, scope, or rules.
    * 'AppearsIn' — Character -> Event (or -> Chapter via its id).
    * 'LocatedAt' — Event -> Location.
    * 'KnownTo'   — Character -> Character.
    Other edge types are allowed; prefer camel-case verbs.

When the user is exploring or undecided, propose options and wait. When
they commit to a direction, act on it without a second confirmation.
""";

    private const string InitialAssistantGreeting =
        "Let's build your outline together. To start, can you tell me what your story is about — even just a sentence or two?";

    public async Task<OutlineConversation> GetOrCreateAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
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
        await conversations.SaveChangesAsync(cancellationToken);
        return conversation;
    }

    public async Task<IReadOnlyList<OutlineMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        await conversations.LoadMessagesAsync(conversationId, cancellationToken);

    public async Task<bool> GetAiChangeApprovalEnabledAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");
        return project.AiChangeApprovalEnabled;
    }

    public async Task SetAiChangeApprovalEnabledAsync(Guid projectId, bool enabled, CancellationToken cancellationToken = default)
    {
        var project = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");
        if (project.AiChangeApprovalEnabled == enabled) return;
        project.AiChangeApprovalEnabled = enabled;
        project.UpdatedAt = DateTime.UtcNow;
        projects.Update(project);
        await projects.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AiChangeBatch>> ListPendingChangesAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await changeApproval.ListPendingBatchesAsync(projectId, cancellationToken);

    public Task ApplyAiChangeAsync(Guid changeId, CancellationToken cancellationToken = default) =>
        changeApproval.ApplyChangeAsync(changeId, cancellationToken);

    public Task RejectAiChangeAsync(Guid changeId, string? message, CancellationToken cancellationToken = default) =>
        changeApproval.RejectChangeAsync(changeId, message, cancellationToken);

    public Task ApplyAiChangeBatchAsync(Guid batchId, CancellationToken cancellationToken = default) =>
        changeApproval.ApplyBatchAsync(batchId, cancellationToken);

    public Task RejectAiChangeBatchAsync(Guid batchId, string? message, CancellationToken cancellationToken = default) =>
        changeApproval.RejectBatchAsync(batchId, message, cancellationToken);

    public async Task ResetAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var existing = await conversations.GetByProjectIdAsync(projectId, cancellationToken);
        if (existing is null) return;
        conversations.RemoveConversation(existing);
        await conversations.SaveChangesAsync(cancellationToken);
    }

    public async IAsyncEnumerable<OutlineTurnUpdate> SendAsync(
        Guid projectId,
        string userText,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userText))
            throw new ArgumentException("Message cannot be empty.", nameof(userText));

        var conversation = await GetOrCreateAsync(projectId, cancellationToken);

        var unresolvedChanges = await changeApproval.ListPendingBatchesAsync(projectId, cancellationToken);
        if (unresolvedChanges.Count > 0)
        {
            yield return new TurnError("Review the pending AI changes before sending another outline chat message.", Cancelled: false);
            yield break;
        }

        // Persist the user message immediately so it appears in history even if the LLM call fails.
        var nextOrder = await conversations.GetMaxOrderAsync(conversation.Id, cancellationToken) + 1;
        var userMsg = new OutlineMessage
        {
            ConversationId = conversation.Id,
            Order = nextOrder++,
            Role = OutlineMessageRole.User,
            Content = userText.Trim(),
            Status = OutlineMessageStatus.Completed,
        };
        await conversations.AddMessageAsync(userMsg, cancellationToken);
        conversation.UpdatedAt = DateTime.UtcNow;
        await conversations.SaveChangesAsync(cancellationToken);

        // Resolve the chat client + tools up front so any wiring failure surfaces before we start streaming.
        IChatClient chat = null!;
        IList<AITool> aiTools = null!;
        OutlineToolStagingContext? staging = null;
        string? setupError = null;
        try
        {
            var project = await projects.GetByIdAsync(projectId, cancellationToken)
                ?? throw new InvalidOperationException($"Project {projectId} not found.");
            var defaultProvider = await providerService.GetDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException("No default LLM provider configured.");
            chat = await chatClientFactory.CreateChatClientAsync(defaultProvider.Id, cancellationToken);

            if (project.AiChangeApprovalEnabled)
                staging = tools.CreateStagingContext(projectId, conversation.Id);

            // OnMutated is captured by every mutating tool; we drain it via _mutatedSinceYield.
            aiTools = tools.Build(new OutlineCollaborationContext(projectId, OnToolMutated, staging));
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
        var history = await conversations.LoadMessagesAsync(conversation.Id, cancellationToken);
        var messages = new List<ChatMessage> { new(ChatRole.System, CollaborationSystemPrompt) };
        messages.AddRange(history.Select(ToChatMessage));

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
            await conversations.AddMessageAsync(activeAssistant, cancellationToken);
            await conversations.SaveChangesAsync(cancellationToken);

            var textBuilder = new StringBuilder();
            var pendingCalls = new List<PendingToolCall>();
            var toolCallTracker = new StreamingToolCallTracker();
            var streamFailed = false;
            string? streamError = null;
            var cancelled = false;

            // Stream one round. We swallow exceptions inside the iterator so we can yield clean
            // events; the caller sees Error/cancellation as a final yielded update.
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
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        cancelled = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Outline streaming round failed");
                        streamFailed = true;
                        streamError = ex.Message;
                        break;
                    }
                    if (!hasNext) break;

                    var update = enumerator.Current;
                    foreach (var content in update.Contents)
                    {
                        if (content is TextContent tc && !string.IsNullOrEmpty(tc.Text))
                        {
                            textBuilder.Append(tc.Text);
                            yield return new TextDelta(tc.Text);
                        }
                        else
                        {
                            foreach (var toolUpdate in toolCallTracker.Process(content, textBuilder.Length))
                            {
                                switch (toolUpdate)
                                {
                                    case StreamingToolCallStartedUpdate started:
                                        yield return new ToolCallStarted(started.CallId, started.ToolName, started.ArgumentsJson, started.ArgumentsComplete);
                                        break;
                                    case StreamingToolCallArgumentsDeltaUpdate delta:
                                        yield return new ToolCallArgumentsDelta(delta.CallId, delta.ArgumentsDelta, delta.ArgumentsComplete);
                                        break;
                                    case StreamingToolCallReadyUpdate ready:
                                        pendingCalls.Add(new PendingToolCall(
                                            ready.Content,
                                            ready.CallId,
                                            ready.ToolName,
                                            ready.ArgumentsJson,
                                            ready.TextOffset));
                                        break;
                                }
                            }
                        }
                    }
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
            }

            // Drain any in-flight mutation flags from text streaming (none expected, but cheap).
            DrainMutated();

            if (cancelled)
            {
                activeAssistant.Content = textBuilder.ToString();
                activeAssistant.Status = OutlineMessageStatus.Cancelled;
                activeAssistant.ErrorMessage = "Cancelled by user.";
                await SafePersistAsync(activeAssistant);
                yield return new TurnError("Cancelled.", Cancelled: true);
                yield break;
            }

            if (streamFailed)
            {
                activeAssistant.Content = textBuilder.ToString();
                activeAssistant.Status = OutlineMessageStatus.Failed;
                activeAssistant.ErrorMessage = streamError;
                await SafePersistAsync(activeAssistant);
                yield return new TurnError(streamError ?? "LLM streaming failed.", Cancelled: false);
                yield break;
            }

            // No tool calls -> final turn.
            if (pendingCalls.Count == 0)
            {
                activeAssistant.Content = textBuilder.ToString();
                activeAssistant.Status = OutlineMessageStatus.Completed;
                await SafePersistAsync(activeAssistant);
                conversation.UpdatedAt = DateTime.UtcNow;
                await conversations.SaveChangesAsync(CancellationToken.None);
                yield return new AssistantMessageCompleted(activeAssistant.Id);
                yield break;
            }

            // Tool round: persist this assistant row with text + tool-call manifest, then invoke each.
            var manifest = pendingCalls
                .Select(pendingCall => new PersistedToolCall(
                    pendingCall.CallId,
                    pendingCall.Name,
                    pendingCall.ArgumentsJson,
                    pendingCall.TextOffset))
                .ToList();
            activeAssistant.Content = textBuilder.ToString();
            activeAssistant.ToolCallsJson = JsonSerializer.Serialize(manifest);
            activeAssistant.Status = OutlineMessageStatus.Completed;
            await SafePersistAsync(activeAssistant);

            // Append to in-memory message list as a single assistant message with tool calls,
            // matching what the model emitted (text + FunctionCallContent[]).
            messages.Add(new ChatMessage(ChatRole.Assistant, BuildAssistantContents(textBuilder.ToString(), manifest)));

            var resultContents = new List<AIContent>();
            foreach (var pendingCall in pendingCalls)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    yield return new TurnError("Cancelled.", Cancelled: true);
                    yield break;
                }

                var functionCall = pendingCall.Content;
                staging?.BeginToolCall(activeAssistant.Id, pendingCall.CallId, pendingCall.Name, pendingCall.ArgumentsJson);

                var sw = Stopwatch.StartNew();
                string? toolResult = null;
                string? toolError = null;
                var toolCancelled = false;
                try
                {
                    var aiFn = aiTools.OfType<AIFunction>().FirstOrDefault(function => function.Name == pendingCall.Name)
                        ?? throw new InvalidOperationException($"Unknown tool '{pendingCall.Name}'.");
                    var invokeResult = await aiFn.InvokeAsync(
                        ToolCallArguments.Create(functionCall.Arguments, pendingCall.ArgumentsJson),
                        cancellationToken);
                    toolResult = invokeResult?.ToString() ?? string.Empty;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    toolCancelled = true;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Outline tool '{Tool}' failed", pendingCall.Name);
                    toolError = ex.Message;
                    toolResult = $"Error: {ex.Message}";
                }
                sw.Stop();
                if (toolCancelled)
                {
                    yield return new TurnError("Cancelled.", Cancelled: true);
                    yield break;
                }

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
                await conversations.AddMessageAsync(toolMsg, CancellationToken.None);
                await conversations.SaveChangesAsync(CancellationToken.None);

                resultContents.Add(new FunctionResultContent(pendingCall.CallId, toolResult ?? string.Empty));
                if (staging is not null)
                {
                    foreach (var pendingChange in staging.DrainNewChanges())
                    {
                        yield return new PendingAiChangeCreated(
                            pendingChange.BatchId,
                            pendingChange.Id,
                            pendingChange.ToolCallId,
                            pendingChange.ToolName,
                            pendingChange.Summary);
                    }
                }
                yield return new ToolCallCompleted(pendingCall.CallId, pendingCall.Name, toolError is null ? toolResult : null, toolError, sw.Elapsed.TotalMilliseconds);

                if (DrainMutated())
                    yield return new OutlineMutated();
            }

            messages.Add(new ChatMessage(ChatRole.Tool, resultContents));

            if (iteration == maxIterations - 1)
            {
                yield return new TurnError($"Tool-call loop hit cap of {maxIterations} iterations without producing a final response.", Cancelled: false);
                yield break;
            }
        }
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
            conversations.UpdateMessage(message);
            await conversations.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist outline message {MessageId}", message.Id);
        }
    }

    // -- history → ChatMessage replay --

    private static ChatMessage ToChatMessage(OutlineMessage m) => m.Role switch
    {
        OutlineMessageRole.System => new ChatMessage(ChatRole.System, m.Content),
        OutlineMessageRole.User => new ChatMessage(ChatRole.User, m.Content),
        OutlineMessageRole.Assistant => BuildAssistantReplay(m),
        OutlineMessageRole.Tool => new ChatMessage(ChatRole.Tool, [new FunctionResultContent(m.ToolCallId ?? string.Empty, m.Content)]),
        _ => new ChatMessage(ChatRole.User, m.Content),
    };

    private static ChatMessage BuildAssistantReplay(OutlineMessage m)
    {
        var calls = ReadPersistedToolCalls(m.ToolCallsJson);
        var contents = calls.Count == 0
            ? BuildTextOnlyAssistantContents(m.Content)
            : BuildAssistantContents(m.Content, calls);

        return new ChatMessage(ChatRole.Assistant, contents);
    }

    private static List<AIContent> BuildTextOnlyAssistantContents(string text)
    {
        var contents = new List<AIContent>();
        if (!string.IsNullOrEmpty(text)) contents.Add(new TextContent(text));
        if (contents.Count == 0) contents.Add(new TextContent(string.Empty));
        return contents;
    }

    private static List<AIContent> BuildAssistantContents(string text, IReadOnlyList<PersistedToolCall> calls)
    {
        if (calls.Count == 0) return BuildTextOnlyAssistantContents(text);

        if (calls.Any(call => call.TextOffset is null))
        {
            var fallbackContents = BuildTextOnlyAssistantContents(text);
            foreach (var call in calls)
                fallbackContents.Add(ToFunctionCallContent(call));
            return fallbackContents;
        }

        var contents = new List<AIContent>();
        var cursor = 0;
        foreach (var item in calls
            .Select((call, index) => new { Call = call, Index = index })
            .OrderBy(item => item.Call.TextOffset!.Value)
            .ThenBy(item => item.Index))
        {
            var offset = Math.Clamp(item.Call.TextOffset!.Value, 0, text.Length);
            if (offset > cursor)
            {
                contents.Add(new TextContent(text[cursor..offset]));
                cursor = offset;
            }
            contents.Add(ToFunctionCallContent(item.Call));
        }

        if (cursor < text.Length)
            contents.Add(new TextContent(text[cursor..]));

        if (contents.Count == 0) contents.Add(new TextContent(string.Empty));
        return contents;
    }

    private static List<PersistedToolCall> ReadPersistedToolCalls(string toolCallsJson)
    {
        if (string.IsNullOrWhiteSpace(toolCallsJson) || toolCallsJson == "[]") return [];

        try
        {
            return JsonSerializer.Deserialize<List<PersistedToolCall>>(toolCallsJson) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static FunctionCallContent ToFunctionCallContent(PersistedToolCall call)
    {
        var args = ToolCallArguments.ParseObjectOrNull(call.ArgumentsJson);
        return new FunctionCallContent(call.CallId, call.Name, args);
    }

    private sealed record PendingToolCall(
        FunctionCallContent Content,
        string CallId,
        string Name,
        string ArgumentsJson,
        int TextOffset);

    /// <summary>JSON shape stored in <see cref="OutlineMessage.ToolCallsJson"/>.</summary>
    private sealed record PersistedToolCall(string CallId, string Name, string ArgumentsJson, int? TextOffset = null);
}
