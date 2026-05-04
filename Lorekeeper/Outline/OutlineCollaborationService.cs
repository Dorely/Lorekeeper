using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Lorekeeper.AiConsole;
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
    IOptions<AiConsoleOptions> options,
    ILogger<OutlineCollaborationService> logger) : IOutlineCollaborationService
{
    /// <summary>
    /// System prompt that frames the assistant as a writing collaborator. Hardcoded by design:
    /// it is independent of the user's project-level <c>SystemPrompt</c> (which targets the
    /// chapter-editing console). Kept terse to leave room in the context window for the
    /// growing conversation history.
    /// </summary>
    public const string CollaborationSystemPrompt = """
        You are a story-outline collaborator. Your job is to help the user discover
        and shape their story's structure through a back-and-forth conversation.

        How to work:
        - You are a partner, not an oracle. Ask questions, propose options, and
          surface trade-offs. Do not dump a full outline up front.
        - Do not write the outline as prose in chat. The outline lives in the
          tools (acts, chapters, beats, entities, project metadata). Chat is for
          thinking together.
        - Call list_outline early in the conversation, and again after major
          changes, to stay synced with the current state. The result includes
          a beatCount per chapter so you know which chapters already have beats.
        - Keep replies short. No headings, no bullet lists unless the user asked
          for them, no emojis. Plain conversational prose.

        When to use tools (be aggressive):
        - CREATING new things: just do it. If the user gives you a premise,
          capture it with set_project_metadata immediately. If they describe a
          new act, chapter, character, location, beat, or relationship, create
          it with the appropriate tool right away — don't ask first. Then
          mention what you did and ask what's next.
        - Persist key facts the user tells you (premise, tone, scope, main
          characters, core conflict, setting) via set_project_metadata under
          the 'outline.' namespace (outline.premise, outline.tone, outline.scope,
          outline.conflict, outline.setting). Do this as soon as the user shares
          the information, without asking.
        - Whenever the user names a character or place in passing, create the
          corresponding Character or Location entity proactively, using
          create_entity. Do not ask for permission for these proactive creates.
        - For new chapters, suggest 3–5 beats by default but wait for
          confirmation before bulk-creating beats on a chapter that already has
          some — that's an edit-shaped operation.
        - EDITING or DELETING existing acts, chapters, beats, entities, or
          metadata: confirm with the user first. Read back what you intend to
          change before calling update_*, delete_*, reorder_*, or link_entities
          with overwrite-shaped intent.

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
            * 'Event' — chapter-scoped beats. REQUIRES parentId=<chapter id>.
              Conventional properties: summary.
        - Use link_entities to create relationships between entities.
          Conventional edge types:
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
        string? setupError = null;
        try
        {
            var defaultProvider = await providerService.GetDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException("No default LLM provider configured.");
            chat = await chatClientFactory.CreateChatClientAsync(defaultProvider.Id, cancellationToken);

            // OnMutated is captured by every mutating tool; we drain it via _mutatedSinceYield.
            aiTools = tools.Build(new OutlineCollaborationContext(projectId, OnToolMutated));
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
            var pendingCalls = new List<FunctionCallContent>();
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
                        else if (content is FunctionCallContent fc)
                        {
                            pendingCalls.Add(fc);
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
                .Select(c => new PersistedToolCall(
                    c.CallId ?? c.Name,
                    c.Name,
                    c.Arguments is null ? "{}" : JsonSerializer.Serialize(c.Arguments)))
                .ToList();
            activeAssistant.Content = textBuilder.ToString();
            activeAssistant.ToolCallsJson = JsonSerializer.Serialize(manifest);
            activeAssistant.Status = OutlineMessageStatus.Completed;
            await SafePersistAsync(activeAssistant);

            // Append to in-memory message list as a single assistant message with tool calls,
            // matching what the model emitted (text + FunctionCallContent[]).
            var assistantContents = new List<AIContent>();
            if (textBuilder.Length > 0) assistantContents.Add(new TextContent(textBuilder.ToString()));
            foreach (var c in pendingCalls) assistantContents.Add(c);
            messages.Add(new ChatMessage(ChatRole.Assistant, assistantContents));

            var resultContents = new List<AIContent>();
            foreach (var fc in pendingCalls)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    yield return new TurnError("Cancelled.", Cancelled: true);
                    yield break;
                }

                var argsJson = fc.Arguments is null ? "{}" : JsonSerializer.Serialize(fc.Arguments);
                var callId = fc.CallId ?? fc.Name;
                yield return new ToolCallStarted(callId, fc.Name, argsJson);

                var sw = Stopwatch.StartNew();
                string? toolResult = null;
                string? toolError = null;
                var toolCancelled = false;
                try
                {
                    var aiFn = aiTools.OfType<AIFunction>().FirstOrDefault(f => f.Name == fc.Name)
                        ?? throw new InvalidOperationException($"Unknown tool '{fc.Name}'.");
                    var argsDict = fc.Arguments ?? new Dictionary<string, object?>();
                    var invokeResult = await aiFn.InvokeAsync(new AIFunctionArguments(argsDict), cancellationToken);
                    toolResult = invokeResult?.ToString() ?? string.Empty;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    toolCancelled = true;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Outline tool '{Tool}' failed", fc.Name);
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
                    ToolCallId = callId,
                    ToolName = fc.Name,
                    Status = toolError is null ? OutlineMessageStatus.Completed : OutlineMessageStatus.Failed,
                    ErrorMessage = toolError,
                };
                await conversations.AddMessageAsync(toolMsg, CancellationToken.None);
                await conversations.SaveChangesAsync(CancellationToken.None);

                resultContents.Add(new FunctionResultContent(callId, toolResult ?? string.Empty));
                yield return new ToolCallCompleted(callId, fc.Name, toolError is null ? toolResult : null, toolError, sw.Elapsed.TotalMilliseconds);

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
        var contents = new List<AIContent>();
        if (!string.IsNullOrEmpty(m.Content)) contents.Add(new TextContent(m.Content));

        if (!string.IsNullOrWhiteSpace(m.ToolCallsJson) && m.ToolCallsJson != "[]")
        {
            try
            {
                var calls = JsonSerializer.Deserialize<List<PersistedToolCall>>(m.ToolCallsJson);
                if (calls is not null)
                {
                    foreach (var c in calls)
                    {
                        IDictionary<string, object?>? args = null;
                        if (!string.IsNullOrWhiteSpace(c.ArgumentsJson) && c.ArgumentsJson != "{}")
                        {
                            try { args = JsonSerializer.Deserialize<Dictionary<string, object?>>(c.ArgumentsJson); }
                            catch { args = new Dictionary<string, object?> { ["raw"] = c.ArgumentsJson }; }
                        }
                        contents.Add(new FunctionCallContent(c.CallId, c.Name, args));
                    }
                }
            }
            catch
            {
                // Corrupt manifest — best-effort, skip.
            }
        }
        if (contents.Count == 0) contents.Add(new TextContent(string.Empty));
        return new ChatMessage(ChatRole.Assistant, contents);
    }

    /// <summary>JSON shape stored in <see cref="OutlineMessage.ToolCallsJson"/>.</summary>
    private sealed record PersistedToolCall(string CallId, string Name, string ArgumentsJson);
}
