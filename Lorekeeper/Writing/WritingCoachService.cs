using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Lorekeeper.Llm;
using Lorekeeper.Models;
using Lorekeeper.Persistence.Repositories;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Lorekeeper.Writing;

public sealed class WritingCoachService(
    IProjectRepository projects,
    IWritingCoachConversationRepository conversations,
    ILlmProviderService providerService,
    IChatClientFactory chatClientFactory,
    WritingCoachTools tools,
    IOptions<AgentOptions> options,
    ILogger<WritingCoachService> logger) : IWritingCoachService
{
    public const string CoachSystemPrompt = """
        You are a Writing Coach for a long-form fiction project. Your job is to help
        the writer produce writing samples in their own style and words so future AI
        drafting can better imitate their voice.

        How to work:
        - You are a partner, not an oracle. Ask questions, propose options, and
          surface craft trade-offs. Do not take over the prose.
        - At the beginning of every user turn, call read_current_section before you
          answer. Treat its result as the latest current draft. Do not ask the user to
          paste the current section unless the tool result says it is unavailable.
        - Call list_project_facts when project-level context would change your advice,
          especially for premise, tone, setting, character, canon, style constraints,
          or other established truths.
        - Your tools are read-only. You cannot edit, save, rename, delete, or otherwise
          change stored samples or project facts from this chat.
        - Do not claim you changed the draft or stored anything.
        - Avoid taking over the prose. When the user asks for examples, keep them short
          and frame them as options the writer can adapt.
        - Keep replies concise and practical. Prefer one next step over a broad lecture.
        - Pay attention to sentence rhythm, diction, point of view, imagery, pacing,
          and emotional texture. Help the writer make those choices intentional.
        """;

    private const string InitialAssistantGreeting =
        "Let's shape a writing sample in your own voice. What kind of scene, moment, or mood do you want to practice first?";

    public async Task<WritingCoachConversation> GetOrCreateAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var existing = await conversations.GetByProjectIdAsync(projectId, cancellationToken);
        if (existing is not null) return existing;

        _ = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");

        var conversation = new WritingCoachConversation { ProjectId = projectId };
        await conversations.AddConversationAsync(conversation, cancellationToken);

        var greeting = new WritingCoachMessage
        {
            ConversationId = conversation.Id,
            Order = 0,
            Role = WritingCoachMessageRole.Assistant,
            Content = InitialAssistantGreeting,
            Status = WritingCoachMessageStatus.Completed,
        };
        await conversations.AddMessageAsync(greeting, cancellationToken);
        await conversations.SaveChangesAsync(cancellationToken);
        return conversation;
    }

    public async Task<IReadOnlyList<WritingCoachMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        await conversations.LoadMessagesAsync(conversationId, cancellationToken);

    public async Task ResetAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var existing = await conversations.GetByProjectIdAsync(projectId, cancellationToken);
        if (existing is null) return;

        conversations.RemoveConversation(existing);
        await conversations.SaveChangesAsync(cancellationToken);
    }

    public async IAsyncEnumerable<WritingCoachTurnUpdate> SendAsync(
        Guid projectId,
        string userText,
        string? currentSampleTitle,
        string? currentSampleBody,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userText))
            throw new ArgumentException("Message cannot be empty.", nameof(userText));

        var conversation = await GetOrCreateAsync(projectId, cancellationToken);
        var nextOrder = await conversations.GetMaxOrderAsync(conversation.Id, cancellationToken) + 1;

        var userMessage = new WritingCoachMessage
        {
            ConversationId = conversation.Id,
            Order = nextOrder++,
            Role = WritingCoachMessageRole.User,
            Content = userText.Trim(),
            Status = WritingCoachMessageStatus.Completed,
        };
        await conversations.AddMessageAsync(userMessage, cancellationToken);
        conversation.UpdatedAt = DateTime.UtcNow;
        await conversations.SaveChangesAsync(cancellationToken);

        IChatClient chat = null!;
        IList<AITool> aiTools = null!;
        string? setupError = null;
        try
        {
            var project = await projects.GetByIdAsync(projectId, cancellationToken)
                ?? throw new InvalidOperationException($"Project {projectId} not found.");
            var defaultProvider = await providerService.GetDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException("No default LLM provider configured.");
            chat = await chatClientFactory.CreateChatClientAsync(defaultProvider.Id, cancellationToken);
            aiTools = tools.Build(new WritingCoachContext(project.Id, currentSampleTitle, currentSampleBody));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Writing Coach turn setup failed for project {ProjectId}", projectId);
            await PersistFailedAssistantAsync(conversation.Id, nextOrder, ex.Message);
            setupError = ex.Message;
        }

        if (setupError is not null)
        {
            yield return new WritingCoachTurnError(setupError, Cancelled: false);
            yield break;
        }

        var chatOptions = new ChatOptions
        {
            Tools = aiTools,
            ToolMode = ChatToolMode.Auto,
        };

        var history = await conversations.LoadMessagesAsync(conversation.Id, cancellationToken);
        var messages = new List<ChatMessage> { new(ChatRole.System, CoachSystemPrompt) };
        messages.AddRange(history.Select(ToChatMessage));

        var maxIterations = Math.Max(1, options.Value.MaxToolIterations);
        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            var activeAssistant = new WritingCoachMessage
            {
                ConversationId = conversation.Id,
                Order = nextOrder++,
                Role = WritingCoachMessageRole.Assistant,
                Content = string.Empty,
                Status = WritingCoachMessageStatus.Pending,
            };
            await conversations.AddMessageAsync(activeAssistant, cancellationToken);
            await conversations.SaveChangesAsync(cancellationToken);

            var textBuilder = new StringBuilder();
            var pendingCalls = new List<PendingToolCall>();
            var toolCallTracker = new StreamingToolCallTracker();
            var streamFailed = false;
            string? streamError = null;
            var cancelled = false;

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
                        logger.LogError(ex, "Writing Coach streaming round failed");
                        streamFailed = true;
                        streamError = ex.Message;
                        break;
                    }

                    if (!hasNext) break;

                    foreach (var content in enumerator.Current.Contents)
                    {
                        if (content is TextContent textContent && !string.IsNullOrEmpty(textContent.Text))
                        {
                            textBuilder.Append(textContent.Text);
                            yield return new WritingCoachTextDelta(textContent.Text);
                        }
                        else
                        {
                            foreach (var toolUpdate in toolCallTracker.Process(content, textBuilder.Length))
                            {
                                switch (toolUpdate)
                                {
                                    case StreamingToolCallStartedUpdate started:
                                        yield return new WritingCoachToolCallStarted(started.CallId, started.ToolName, started.ArgumentsJson, started.ArgumentsComplete);
                                        break;
                                    case StreamingToolCallArgumentsDeltaUpdate delta:
                                        yield return new WritingCoachToolCallArgumentsDelta(delta.CallId, delta.ArgumentsDelta, delta.ArgumentsComplete);
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

            if (cancelled)
            {
                activeAssistant.Content = textBuilder.ToString();
                activeAssistant.Status = WritingCoachMessageStatus.Cancelled;
                activeAssistant.ErrorMessage = "Cancelled by user.";
                await SafePersistAsync(activeAssistant);
                yield return new WritingCoachTurnError("Cancelled.", Cancelled: true);
                yield break;
            }

            if (streamFailed)
            {
                activeAssistant.Content = textBuilder.ToString();
                activeAssistant.Status = WritingCoachMessageStatus.Failed;
                activeAssistant.ErrorMessage = streamError;
                await SafePersistAsync(activeAssistant);
                yield return new WritingCoachTurnError(streamError ?? "Writing Coach streaming failed.", Cancelled: false);
                yield break;
            }

            if (pendingCalls.Count == 0)
            {
                activeAssistant.Content = textBuilder.ToString();
                activeAssistant.Status = WritingCoachMessageStatus.Completed;
                await SafePersistAsync(activeAssistant);
                conversation.UpdatedAt = DateTime.UtcNow;
                await conversations.SaveChangesAsync(CancellationToken.None);
                yield return new WritingCoachAssistantMessageCompleted(activeAssistant.Id);
                yield break;
            }

            var manifest = pendingCalls
                .Select(pendingCall => new PersistedToolCall(
                    pendingCall.CallId,
                    pendingCall.Name,
                    pendingCall.ArgumentsJson,
                    pendingCall.TextOffset))
                .ToList();
            activeAssistant.Content = textBuilder.ToString();
            activeAssistant.ToolCallsJson = JsonSerializer.Serialize(manifest);
            activeAssistant.Status = WritingCoachMessageStatus.Completed;
            await SafePersistAsync(activeAssistant);

            messages.Add(new ChatMessage(ChatRole.Assistant, BuildAssistantContents(textBuilder.ToString(), manifest)));

            var resultContents = new List<AIContent>();
            foreach (var pendingCall in pendingCalls)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    yield return new WritingCoachTurnError("Cancelled.", Cancelled: true);
                    yield break;
                }

                var sw = Stopwatch.StartNew();
                string? toolResult = null;
                string? toolError = null;
                var toolCancelled = false;
                try
                {
                    var aiFn = aiTools.OfType<AIFunction>().FirstOrDefault(function => function.Name == pendingCall.Name)
                        ?? throw new InvalidOperationException($"Unknown tool '{pendingCall.Name}'.");
                    var invokeResult = await aiFn.InvokeAsync(
                        ToolCallArguments.Create(pendingCall.Content.Arguments, pendingCall.ArgumentsJson),
                        cancellationToken);
                    toolResult = invokeResult?.ToString() ?? string.Empty;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    toolCancelled = true;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Writing Coach tool '{Tool}' failed", pendingCall.Name);
                    toolError = ex.Message;
                    toolResult = $"Error: {ex.Message}";
                }
                sw.Stop();

                if (toolCancelled)
                {
                    yield return new WritingCoachTurnError("Cancelled.", Cancelled: true);
                    yield break;
                }

                var toolMessage = new WritingCoachMessage
                {
                    ConversationId = conversation.Id,
                    Order = nextOrder++,
                    Role = WritingCoachMessageRole.Tool,
                    Content = toolResult ?? string.Empty,
                    ToolCallId = pendingCall.CallId,
                    ToolName = pendingCall.Name,
                    Status = toolError is null ? WritingCoachMessageStatus.Completed : WritingCoachMessageStatus.Failed,
                    ErrorMessage = toolError,
                };
                await conversations.AddMessageAsync(toolMessage, CancellationToken.None);
                await conversations.SaveChangesAsync(CancellationToken.None);

                resultContents.Add(new FunctionResultContent(pendingCall.CallId, toolResult ?? string.Empty));
                yield return new WritingCoachToolCallCompleted(
                    pendingCall.CallId,
                    pendingCall.Name,
                    toolError is null ? toolResult : null,
                    toolError,
                    sw.Elapsed.TotalMilliseconds);
            }

            messages.Add(new ChatMessage(ChatRole.Tool, resultContents));

            if (iteration == maxIterations - 1)
            {
                yield return new WritingCoachTurnError(
                    $"Writing Coach tool-call loop hit cap of {maxIterations} iterations without producing a final response.",
                    Cancelled: false);
                yield break;
            }
        }
    }

    private static ChatMessage ToChatMessage(WritingCoachMessage message) => message.Role switch
    {
        WritingCoachMessageRole.System => new ChatMessage(ChatRole.System, message.Content),
        WritingCoachMessageRole.User => new ChatMessage(ChatRole.User, message.Content),
        WritingCoachMessageRole.Assistant => BuildAssistantReplay(message),
        WritingCoachMessageRole.Tool => new ChatMessage(ChatRole.Tool, [new FunctionResultContent(message.ToolCallId ?? string.Empty, message.Content)]),
        _ => new ChatMessage(ChatRole.User, message.Content),
    };

    private static ChatMessage BuildAssistantReplay(WritingCoachMessage message)
    {
        var calls = ReadPersistedToolCalls(message.ToolCallsJson);
        var contents = calls.Count == 0
            ? BuildTextOnlyAssistantContents(message.Content)
            : BuildAssistantContents(message.Content, calls);

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

    private async Task PersistFailedAssistantAsync(Guid conversationId, int order, string error)
    {
        try
        {
            var message = new WritingCoachMessage
            {
                ConversationId = conversationId,
                Order = order,
                Role = WritingCoachMessageRole.Assistant,
                Status = WritingCoachMessageStatus.Failed,
                ErrorMessage = error,
            };
            await conversations.AddMessageAsync(message, CancellationToken.None);
            await conversations.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist Writing Coach setup failure");
        }
    }

    private async Task SafePersistAsync(WritingCoachMessage message)
    {
        try
        {
            conversations.UpdateMessage(message);
            await conversations.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist Writing Coach message {MessageId}", message.Id);
        }
    }

    private sealed record PendingToolCall(
        FunctionCallContent Content,
        string CallId,
        string Name,
        string ArgumentsJson,
        int TextOffset);

    private sealed record PersistedToolCall(string CallId, string Name, string ArgumentsJson, int? TextOffset = null);
}