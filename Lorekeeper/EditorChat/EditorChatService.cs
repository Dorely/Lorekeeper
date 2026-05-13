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
    IEditorContestService contestService,
    IAiChangeApprovalService changeApproval,
    IAiChangeRepository changes,
    IOptions<AgentOptions> options,
    ILogger<EditorChatService> logger) : IEditorChatService
{
    private const int _maxToolResultCharsForModel = 12000;

    private const string _initialAssistantGreeting =
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
            Content = _initialAssistantGreeting,
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

    public Task<EditorContestSettings> GetContestSettingsAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        contestService.GetSettingsAsync(projectId, cancellationToken);

    public Task SetContestModeEnabledAsync(Guid projectId, bool enabled, CancellationToken cancellationToken = default) =>
        contestService.SetContestModeEnabledAsync(projectId, enabled, cancellationToken);

    public Task SetContestProviderAsync(Guid projectId, int slot, int? providerId, CancellationToken cancellationToken = default) =>
        contestService.SetContestProviderAsync(projectId, slot, providerId, cancellationToken);

    public async Task<IReadOnlyList<AiChangeBatch>> ListPendingChangesAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await changeApproval.ListPendingBatchesAsync(projectId, cancellationToken);

    public async Task<IReadOnlyList<ContestBatch>> ListCurrentContestBatchesAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await contestService.ListCurrentContestBatchesAsync(projectId, cancellationToken);

    public async Task<IReadOnlyList<ContestBatch>> ListContestHistoryAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await contestService.ListContestHistoryAsync(projectId, cancellationToken);

    public Task StageContestCandidateAsync(Guid candidateId, CancellationToken cancellationToken = default) =>
        contestService.StageCandidateAsync(candidateId, cancellationToken);

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
        var contestModeEnabled = false;
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
            contestModeEnabled = project.ContestModeEnabled;
            systemPrompt = assembly.Assemble(contestModeEnabled ? AssistantWorkflowInstructions.EditorContestPreparation : null);

            var defaultProvider = await providerService.GetDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException("No default LLM provider configured.");
            chat = await chatClientFactory.CreateChatClientAsync(defaultProvider.Id, cancellationToken);

            OutlineToolStagingContext? outlineStaging = null;
            EditorChatChangeStagingContext? editorStaging = null;
            if (project.AiChangeApprovalEnabled)
            {
                outlineStaging = outlineTools.CreateStagingContext(projectId, conversation.Id, AiChangeConversationKind.Editor);
                editorStaging = new EditorChatChangeStagingContext(projectId, conversation.Id, changes);
            }

            editorContext = new EditorChatContext(
                projectId,
                currentChapterId,
                OnToolMutated,
                project.AiChangeApprovalEnabled,
                outlineStaging,
                editorStaging);
            aiTools = tools.Build(editorContext, contestModeEnabled ? EditorChatToolMode.ContestPreparation : EditorChatToolMode.Normal);
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
                        else
                        {
                            foreach (var toolUpdate in toolCallTracker.Process(content, textBuilder.Length))
                            {
                                switch (toolUpdate)
                                {
                                    case StreamingToolCallStartedUpdate started:
                                        yield return new EditorChatToolCallStarted(started.CallId, started.ToolName, started.ArgumentsJson, started.ArgumentsComplete);
                                        break;
                                    case StreamingToolCallArgumentsDeltaUpdate delta:
                                        yield return new EditorChatToolCallArgumentsDelta(delta.CallId, delta.ArgumentsDelta, delta.ArgumentsComplete);
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

            var startContestIndex = pendingCalls.FindIndex(pendingCall => string.Equals(pendingCall.Name, "start_contest", StringComparison.Ordinal));
            if (startContestIndex >= 0 && startContestIndex != pendingCalls.Count - 1)
            {
                activeAssistant.Content = textBuilder.ToString();
                activeAssistant.Status = EditorMessageStatus.Failed;
                activeAssistant.ErrorMessage = "start_contest must be the final tool call in a Contest Mode turn.";
                await SafePersistAsync(activeAssistant);
                yield return new EditorChatTurnError(activeAssistant.ErrorMessage, Cancelled: false);
                yield break;
            }

            if (startContestIndex >= 0 && textBuilder.Length == 0)
            {
                const string contestNote = "I've started a contest for this chapter edit. Review the candidate responses as they arrive.";
                textBuilder.Append(contestNote);
                yield return new EditorChatTextDelta(contestNote);
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

            messages.Add(new ChatMessage(ChatRole.Assistant, BuildAssistantToolCallContents(manifest)));

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
                    var invokeResult = await aiFunction.InvokeAsync(
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

                resultContents.Add(new FunctionResultContent(
                    pendingCall.CallId,
                    BuildToolResultForModel(pendingCall.Name, toolResult ?? string.Empty)));
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

                if (toolError is null
                    && string.Equals(pendingCall.Name, "start_contest", StringComparison.Ordinal)
                    && editorContext.TryTakeContestRequest(out var contestRequest))
                {
                    var contestSnapshotToolResults = resultContents
                        .OfType<FunctionResultContent>()
                        .Where(result => result.CallId != pendingCall.CallId)
                        .Cast<AIContent>()
                        .ToList();
                    var snapshot = new ContestTurnSnapshot(BuildContestSnapshotMessages(
                        messages,
                        contestSnapshotToolResults));

                    await foreach (var contestUpdate in contestService.StartContestAsync(
                        projectId,
                        conversation.Id,
                        activeAssistant.Id,
                        contestRequest,
                        snapshot,
                        cancellationToken))
                    {
                        switch (contestUpdate)
                        {
                            case EditorContestStarted started:
                                yield return new EditorChatContestStarted(started.BatchId);
                                break;
                            case EditorContestCandidateUpdated candidateUpdated:
                                yield return new EditorChatContestCandidateUpdated(candidateUpdated.BatchId, candidateUpdated.CandidateId, candidateUpdated.Status.ToString());
                                break;
                            case EditorContestCandidateRawResponseDelta rawDelta:
                                yield return new EditorChatContestCandidateJsonDelta(rawDelta.BatchId, rawDelta.CandidateId, rawDelta.Delta, rawDelta.RawResponse);
                                break;
                            case EditorContestCompleted completed:
                                yield return new EditorChatContestCompleted(completed.BatchId, completed.Status.ToString());
                                break;
                        }
                    }

                    conversation.UpdatedAt = DateTime.UtcNow;
                    await conversations.SaveChangesAsync(CancellationToken.None);
                    yield return new EditorChatAssistantMessageCompleted(activeAssistant.Id);
                    yield break;
                }

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

    private static IReadOnlyList<ContestChatMessageSnapshot> BuildContestSnapshotMessages(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<AIContent> pendingToolResults)
    {
        var snapshot = messages
            .Select(ToContestChatMessageSnapshot)
            .Where(message => !string.IsNullOrWhiteSpace(message.Content))
            .ToList();

        if (pendingToolResults.Count > 0)
        {
            var content = FormatContestContents(pendingToolResults);
            if (!string.IsNullOrWhiteSpace(content))
                snapshot.Add(new ContestChatMessageSnapshot("Tool", content));
        }

        return snapshot;
    }

    private static ContestChatMessageSnapshot ToContestChatMessageSnapshot(ChatMessage message)
    {
        var content = FormatContestContents(message.Contents);
        if (string.IsNullOrWhiteSpace(content))
            content = message.Text ?? string.Empty;

        return new ContestChatMessageSnapshot(FormatContestRole(message.Role), content);
    }

    private static string FormatContestRole(ChatRole role)
    {
        if (role == ChatRole.System) return "System";
        if (role == ChatRole.User) return "User";
        if (role == ChatRole.Assistant) return "Assistant";
        if (role == ChatRole.Tool) return "Tool";
        return role.ToString();
    }

    private static string FormatContestContents(IEnumerable<AIContent> contents)
    {
        var sb = new StringBuilder();
        foreach (var content in contents)
        {
            switch (content)
            {
                case TextContent textContent when !string.IsNullOrEmpty(textContent.Text):
                    sb.Append(textContent.Text);
                    break;
                case FunctionCallContent functionCall:
                    if (sb.Length > 0) sb.AppendLine();
                    sb.AppendLine($"[Tool call: {functionCall.Name} ({functionCall.CallId})]");
                    sb.AppendLine("Arguments:");
                    sb.AppendLine(functionCall.Arguments is null ? "{}" : JsonSerializer.Serialize(functionCall.Arguments));
                    break;
                case FunctionResultContent functionResult:
                    if (sb.Length > 0) sb.AppendLine();
                    sb.AppendLine($"[Tool result ({functionResult.CallId})]");
                    sb.AppendLine(functionResult.Result?.ToString() ?? string.Empty);
                    break;
            }
        }

        return sb.ToString().Trim();
    }

    private static ChatMessage BuildAssistantReplay(EditorMessage message)
    {
        var calls = ReadPersistedToolCalls(message.ToolCallsJson);
        var contents = calls.Count == 0
            ? BuildTextOnlyAssistantContents(message.Content)
            : BuildAssistantToolCallContents(calls);

        return new ChatMessage(ChatRole.Assistant, contents);
    }

    private static string BuildToolResultForModel(string toolName, string result)
    {
        if (string.Equals(toolName, "edit_chapter", StringComparison.Ordinal))
        {
            const string newBodyMarker = "\n\nNew body:\n";
            var markerIndex = result.IndexOf(newBodyMarker, StringComparison.Ordinal);
            if (markerIndex >= 0)
            {
                return result[..markerIndex]
                    + "\n\nThe chapter was updated in the editor. Call read_chapter if you need to inspect the current body before responding.";
            }
        }

        if (result.Length <= _maxToolResultCharsForModel)
            return result;

        return result[.._maxToolResultCharsForModel]
            + "\n\n[Tool result truncated before returning it to the model.]";
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

    private static List<AIContent> BuildAssistantToolCallContents(IReadOnlyList<PersistedToolCall> calls) =>
        calls.Select(call => (AIContent)ToFunctionCallContent(call)).ToList();

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

    private sealed record PersistedToolCall(string CallId, string Name, string ArgumentsJson, int? TextOffset = null);
}