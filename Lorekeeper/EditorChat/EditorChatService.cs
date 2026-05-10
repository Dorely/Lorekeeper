using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Lorekeeper.Chapters;
using Lorekeeper.Context;
using Lorekeeper.Llm;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence.Repositories;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Lorekeeper.EditorChat;

public sealed class EditorChatService(
    IProjectRepository projects,
    IChapterService chapters,
    IEditorConversationRepository conversations,
    IContextBuilder contextBuilder,
    ILlmProviderService providerService,
    IChatClientFactory chatClientFactory,
    EditorChatTools tools,
    OutlineCollaborationTools outlineTools,
    IAiChangeApprovalService changeApproval,
    IAiChangeRepository changes,
    IOptions<AgentOptions> options,
    ILogger<EditorChatService> logger) : IEditorChatService
{
    private const string InitialAssistantGreeting =
        "I'm ready to work on the draft with you. Tell me what you want to shape, revise, or check in the current chapter.";

    public async Task<EditorConversation> GetOrCreateAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var existing = await conversations.GetByProjectIdAsync(projectId, cancellationToken);
        if (existing is not null) return existing;

        _ = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");

        var conversation = new EditorConversation { ProjectId = projectId };
        await conversations.AddConversationAsync(conversation, cancellationToken);

        await conversations.AddMessageAsync(new EditorMessage
        {
            ConversationId = conversation.Id,
            Order = 0,
            Role = EditorMessageRole.Assistant,
            Content = InitialAssistantGreeting,
            Status = EditorMessageStatus.Completed,
        }, cancellationToken);
        await conversations.SaveChangesAsync(cancellationToken);
        return conversation;
    }

    public async Task<IReadOnlyList<EditorMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
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

    public async Task ResetAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var existing = await conversations.GetByProjectIdAsync(projectId, cancellationToken);
        if (existing is null) return;
        conversations.RemoveConversation(existing);
        await conversations.SaveChangesAsync(cancellationToken);
    }

    public async IAsyncEnumerable<EditorChatTurnUpdate> SendAsync(
        Guid projectId,
        Guid? currentChapterId,
        string userText,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userText))
            throw new ArgumentException("Message cannot be empty.", nameof(userText));

        var conversation = await GetOrCreateAsync(projectId, cancellationToken);

        var unresolvedChanges = await changeApproval.ListPendingBatchesAsync(projectId, cancellationToken);
        if (unresolvedChanges.Count > 0)
        {
            yield return new EditorChatTurnError("Review the pending AI changes before sending another editor chat message.", Cancelled: false);
            yield break;
        }

        var nextOrder = await conversations.GetMaxOrderAsync(conversation.Id, cancellationToken) + 1;
        var userMessage = new EditorMessage
        {
            ConversationId = conversation.Id,
            Order = nextOrder++,
            Role = EditorMessageRole.User,
            Content = userText.Trim(),
            Status = EditorMessageStatus.Completed,
        };
        await conversations.AddMessageAsync(userMessage, cancellationToken);
        conversation.UpdatedAt = DateTime.UtcNow;
        await conversations.SaveChangesAsync(cancellationToken);

        IChatClient chat = null!;
        IList<AITool> aiTools = null!;
        EditorChatContext editorContext = null!;
        string systemPrompt = string.Empty;
        string? setupError = null;
        try
        {
            var project = await projects.GetByIdAsync(projectId, cancellationToken)
                ?? throw new InvalidOperationException($"Project {projectId} not found.");

            Chapter? currentChapter = null;
            if (currentChapterId is { } chapterId)
            {
                currentChapter = await chapters.GetAsync(chapterId, cancellationToken);
                if (currentChapter is null || currentChapter.ProjectId != projectId)
                    throw new InvalidOperationException($"Chapter {chapterId} not found in this project.");
            }

            var assembly = await contextBuilder.BuildAsync(project, currentChapter, cancellationToken);
            systemPrompt = assembly.Assemble();

            var defaultProvider = await providerService.GetDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException("No default LLM provider configured.");
            chat = await chatClientFactory.CreateChatClientAsync(defaultProvider.Id, cancellationToken);

            OutlineToolStagingContext? outlineStaging = null;
            EditorChatChangeStagingContext? editorStaging = null;
            if (project.AiChangeApprovalEnabled)
            {
                outlineStaging = outlineTools.CreateStagingContext(projectId, conversation.Id);
                editorStaging = new EditorChatChangeStagingContext(projectId, conversation.Id, changes);
            }

            editorContext = new EditorChatContext(
                projectId,
                currentChapterId,
                OnToolMutated,
                project.AiChangeApprovalEnabled,
                outlineStaging,
                editorStaging);
            aiTools = tools.Build(editorContext);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Editor chat turn setup failed for project {ProjectId}", projectId);
            setupError = ex.Message;
        }
        if (setupError is not null)
        {
            yield return new EditorChatTurnError(setupError, Cancelled: false);
            yield break;
        }

        var chatOptions = new ChatOptions
        {
            Tools = aiTools,
            ToolMode = ChatToolMode.Auto,
        };

        var history = await conversations.LoadMessagesAsync(conversation.Id, cancellationToken);
        var messages = new List<ChatMessage> { new(ChatRole.System, systemPrompt) };
        messages.AddRange(history.Select(ToChatMessage));

        var maxIterations = Math.Max(1, options.Value.MaxToolIterations);
        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            var activeAssistant = new EditorMessage
            {
                ConversationId = conversation.Id,
                Order = nextOrder++,
                Role = EditorMessageRole.Assistant,
                Content = string.Empty,
                Status = EditorMessageStatus.Pending,
            };
            await conversations.AddMessageAsync(activeAssistant, cancellationToken);
            await conversations.SaveChangesAsync(cancellationToken);

            var textBuilder = new StringBuilder();
            var pendingCalls = new List<PendingToolCall>();
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
                        logger.LogError(ex, "Editor chat streaming round failed");
                        streamFailed = true;
                        streamError = ex.Message;
                        break;
                    }

                    if (!hasNext) break;

                    var update = enumerator.Current;
                    foreach (var content in update.Contents)
                    {
                        if (content is TextContent textContent && !string.IsNullOrEmpty(textContent.Text))
                        {
                            textBuilder.Append(textContent.Text);
                            yield return new EditorChatTextDelta(textContent.Text);
                        }
                        else if (content is FunctionCallContent functionCall)
                        {
                            var callId = functionCall.CallId ?? functionCall.Name;
                            var argumentsJson = functionCall.Arguments is null
                                ? "{}"
                                : JsonSerializer.Serialize(functionCall.Arguments);
                            pendingCalls.Add(new PendingToolCall(
                                functionCall,
                                callId,
                                functionCall.Name,
                                argumentsJson,
                                textBuilder.Length));
                            yield return new EditorChatToolCallStarted(callId, functionCall.Name, argumentsJson);
                        }
                    }
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
            }

            DrainMutated();

            if (cancelled)
            {
                activeAssistant.Content = textBuilder.ToString();
                activeAssistant.Status = EditorMessageStatus.Cancelled;
                activeAssistant.ErrorMessage = "Cancelled by user.";
                await SafePersistAsync(activeAssistant);
                yield return new EditorChatTurnError("Cancelled.", Cancelled: true);
                yield break;
            }

            if (streamFailed)
            {
                activeAssistant.Content = textBuilder.ToString();
                activeAssistant.Status = EditorMessageStatus.Failed;
                activeAssistant.ErrorMessage = streamError;
                await SafePersistAsync(activeAssistant);
                yield return new EditorChatTurnError(streamError ?? "LLM streaming failed.", Cancelled: false);
                yield break;
            }

            if (pendingCalls.Count == 0)
            {
                activeAssistant.Content = textBuilder.ToString();
                activeAssistant.Status = EditorMessageStatus.Completed;
                await SafePersistAsync(activeAssistant);
                conversation.UpdatedAt = DateTime.UtcNow;
                await conversations.SaveChangesAsync(CancellationToken.None);
                yield return new EditorChatAssistantMessageCompleted(activeAssistant.Id);
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
            activeAssistant.Status = EditorMessageStatus.Completed;
            await SafePersistAsync(activeAssistant);

            messages.Add(new ChatMessage(ChatRole.Assistant, BuildAssistantContents(textBuilder.ToString(), manifest)));

            var resultContents = new List<AIContent>();
            foreach (var pendingCall in pendingCalls)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    yield return new EditorChatTurnError("Cancelled.", Cancelled: true);
                    yield break;
                }

                editorContext.BeginToolCall(activeAssistant.Id, pendingCall.CallId, pendingCall.Name, pendingCall.ArgumentsJson);

                var stopwatch = Stopwatch.StartNew();
                string? toolResult = null;
                string? toolError = null;
                var toolCancelled = false;
                try
                {
                    var aiFunction = aiTools.OfType<AIFunction>().FirstOrDefault(function => function.Name == pendingCall.Name)
                        ?? throw new InvalidOperationException($"Unknown tool '{pendingCall.Name}'.");
                    var argsDict = pendingCall.Content.Arguments ?? new Dictionary<string, object?>();
                    var invokeResult = await aiFunction.InvokeAsync(new AIFunctionArguments(argsDict), cancellationToken);
                    toolResult = invokeResult?.ToString() ?? string.Empty;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    toolCancelled = true;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Editor chat tool '{Tool}' failed", pendingCall.Name);
                    toolError = ex.Message;
                    toolResult = $"Error: {ex.Message}";
                }
                stopwatch.Stop();

                if (toolCancelled)
                {
                    yield return new EditorChatTurnError("Cancelled.", Cancelled: true);
                    yield break;
                }

                var toolMessage = new EditorMessage
                {
                    ConversationId = conversation.Id,
                    Order = nextOrder++,
                    Role = EditorMessageRole.Tool,
                    Content = toolResult ?? string.Empty,
                    ToolCallId = pendingCall.CallId,
                    ToolName = pendingCall.Name,
                    Status = toolError is null ? EditorMessageStatus.Completed : EditorMessageStatus.Failed,
                    ErrorMessage = toolError,
                };
                await conversations.AddMessageAsync(toolMessage, CancellationToken.None);
                await conversations.SaveChangesAsync(CancellationToken.None);

                resultContents.Add(new FunctionResultContent(pendingCall.CallId, toolResult ?? string.Empty));
                foreach (var pendingChange in editorContext.OutlineStaging?.DrainNewChanges() ?? [])
                {
                    yield return new EditorChatPendingAiChangeCreated(
                        pendingChange.BatchId,
                        pendingChange.Id,
                        pendingChange.ToolCallId,
                        pendingChange.ToolName,
                        pendingChange.Summary);
                }
                foreach (var pendingChange in editorContext.EditorStaging?.DrainNewChanges() ?? [])
                {
                    yield return new EditorChatPendingAiChangeCreated(
                        pendingChange.BatchId,
                        pendingChange.Id,
                        pendingChange.ToolCallId,
                        pendingChange.ToolName,
                        pendingChange.Summary);
                }

                yield return new EditorChatToolCallCompleted(pendingCall.CallId, pendingCall.Name, toolError is null ? toolResult : null, toolError, stopwatch.Elapsed.TotalMilliseconds);

                if (DrainMutated())
                    yield return new EditorChatMutated();
            }

            messages.Add(new ChatMessage(ChatRole.Tool, resultContents));

            if (iteration == maxIterations - 1)
            {
                yield return new EditorChatTurnError($"Tool-call loop hit cap of {maxIterations} iterations without producing a final response.", Cancelled: false);
                yield break;
            }
        }
    }

    private bool _mutatedSinceYield;
    private void OnToolMutated() => _mutatedSinceYield = true;
    private bool DrainMutated()
    {
        if (!_mutatedSinceYield) return false;
        _mutatedSinceYield = false;
        return true;
    }

    private async Task SafePersistAsync(EditorMessage message)
    {
        try
        {
            conversations.UpdateMessage(message);
            await conversations.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist editor chat message {MessageId}", message.Id);
        }
    }

    private static ChatMessage ToChatMessage(EditorMessage message) => message.Role switch
    {
        EditorMessageRole.System => new ChatMessage(ChatRole.System, message.Content),
        EditorMessageRole.User => new ChatMessage(ChatRole.User, message.Content),
        EditorMessageRole.Assistant => BuildAssistantReplay(message),
        EditorMessageRole.Tool => new ChatMessage(ChatRole.Tool, [new FunctionResultContent(message.ToolCallId ?? string.Empty, message.Content)]),
        _ => new ChatMessage(ChatRole.User, message.Content),
    };

    private static ChatMessage BuildAssistantReplay(EditorMessage message)
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
        IDictionary<string, object?>? args = null;
        if (!string.IsNullOrWhiteSpace(call.ArgumentsJson) && call.ArgumentsJson != "{}")
        {
            try { args = JsonSerializer.Deserialize<Dictionary<string, object?>>(call.ArgumentsJson); }
            catch { args = new Dictionary<string, object?> { ["raw"] = call.ArgumentsJson }; }
        }

        return new FunctionCallContent(call.CallId, call.Name, args);
    }

    private sealed record PendingToolCall(
        FunctionCallContent Content,
        string CallId,
        string Name,
        string ArgumentsJson,
        int TextOffset);

    private sealed record PersistedToolCall(string CallId, string Name, string ArgumentsJson, int? TextOffset = null);
}