using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Lorekeeper.ChapterVisuals;
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
    IChapterVisualService chapterVisuals,
    ILlmProviderService providerService,
    IChatClientFactory chatClientFactory,
    IEmbeddingService embeddings,
    EditorChatTools tools,
    OutlineCollaborationTools outlineTools,
    IEditorContestService contestService,
    IEditorRevisionJobNotifier revisionJobNotifier,
    IEditorRevisionAgentService revisionAgents,
    IAiChangeApprovalService changeApproval,
    IAiChangeRepository changes,
    IServiceScopeFactory scopeFactory,
    IOptions<AgentOptions> options,
    IOptions<EditorChatOptions> editorOptions,
    ILogger<EditorChatService> logger) : IEditorChatService
{
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

    public Task ResolveContestCandidateLineAsync(
        Guid projectId,
        Guid chapterId,
        ContestCandidateReviewLineResolution request,
        CancellationToken cancellationToken = default) =>
        contestService.ResolveCandidateLineAsync(projectId, chapterId, request, cancellationToken);

    public Task KeepContestCandidateAsync(Guid candidateId, CancellationToken cancellationToken = default) =>
        contestService.KeepCandidateAsync(candidateId, cancellationToken);

    public Task FinishContestBatchAsync(Guid batchId, CancellationToken cancellationToken = default) =>
        contestService.FinishContestBatchAsync(batchId, cancellationToken);

    public async Task ResetAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await contestService.DiscardInactiveContestBatchesAsync(projectId, cancellationToken);
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

        var currentContestBatches = await contestService.ListCurrentContestBatchesAsync(projectId, cancellationToken);
        if (currentContestBatches.Any(batch => batch.Status == ContestBatchStatus.Running))
        {
            yield return new EditorChatTurnError("Wait for the running contest to finish before sending another editor chat message.", Cancelled: false);
            yield break;
        }
        if (currentContestBatches.Any(batch => batch.Status == ContestBatchStatus.Completed))
        {
            yield return new EditorChatTurnError("Finish the pending Contest Mode review before sending another editor chat message.", Cancelled: false);
            yield break;
        }
        await contestService.DiscardInactiveContestBatchesAsync(projectId, cancellationToken);

        var providerAvailability = await providerService.GetDefaultChatProviderAvailabilityAsync(cancellationToken);
        if (!providerAvailability.IsAvailable || providerAvailability.Provider is null)
        {
            yield return new EditorChatTurnError(providerAvailability.Message, Cancelled: false);
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
        Chapter? currentChapter = null;
        var contestModeEnabled = false;
        string systemPrompt = string.Empty;
        string? setupError = null;
        try
        {
            var project = await projects.GetByIdAsync(projectId, cancellationToken)
                ?? throw new InvalidOperationException($"Project {projectId} not found.");

            if (currentChapterId is { } chapterId)
            {
                currentChapter = await chapters.GetAsync(chapterId, cancellationToken);
                if (currentChapter is null || currentChapter.ProjectId != projectId)
                    throw new InvalidOperationException($"Chapter {chapterId} not found in this project.");
            }

            var assembly = await contextBuilder.BuildAsync(project, currentChapter, cancellationToken);
            contestModeEnabled = project.ContestModeEnabled;
            var vectorSearchAvailable = await embeddings.IsAvailableAsync(cancellationToken);
            systemPrompt = assembly.Assemble(contestModeEnabled
                ? AssistantWorkflowInstructions.EditorContestPreparation
                : AssistantWorkflowInstructions.EditorChatFor(vectorSearchAvailable));

            chat = await chatClientFactory.CreateChatClientAsync(providerAvailability.Provider.Id, cancellationToken);

            OutlineToolStagingContext? outlineStaging = null;
            EditorChatChangeStagingContext? editorStaging = null;
            if (project.AiChangeApprovalEnabled)
            {
                outlineStaging = outlineTools.CreateStagingContext(
                    projectId,
                    conversation.Id,
                    AiChangeConversationKind.Editor,
                    OnToolMutated);
                editorStaging = new EditorChatChangeStagingContext(projectId, conversation.Id, changes);
            }

            editorContext = new EditorChatContext(
                projectId,
                conversation.Id,
                currentChapterId,
                OnToolMutated,
                project.AiChangeApprovalEnabled,
                autoPinReadEntities: !contestModeEnabled,
                outlineStaging,
                editorStaging);
            aiTools = await tools.BuildAsync(editorContext, contestModeEnabled ? EditorChatToolMode.ContestPreparation : EditorChatToolMode.Normal, cancellationToken);
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
        messages.AddRange(BuildModelHistory(history));
        await AddAutomaticVisualSnapshotsAsync(messages, currentChapter, providerAvailability.Provider, cancellationToken);

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

                    var updatesToYield = new List<EditorChatTurnUpdate>();
                    try
                    {
                        var contents = enumerator.Current?.Contents;
                        if (contents is null) continue;

                        foreach (var content in contents)
                        {
                            if (content is TextContent textContent && !string.IsNullOrEmpty(textContent.Text))
                            {
                                textBuilder.Append(textContent.Text);
                                updatesToYield.Add(new EditorChatTextDelta(textContent.Text));
                            }
                            else
                            {
                                foreach (var toolUpdate in toolCallTracker.Process(content, textBuilder.Length))
                                {
                                    switch (toolUpdate)
                                    {
                                        case StreamingToolCallStartedUpdate started:
                                            updatesToYield.Add(new EditorChatToolCallStarted(started.CallId, started.ToolName, started.ArgumentsJson, started.ArgumentsComplete));
                                            break;
                                        case StreamingToolCallArgumentsDeltaUpdate delta:
                                            updatesToYield.Add(new EditorChatToolCallArgumentsDelta(delta.CallId, delta.ArgumentsDelta, delta.ArgumentsComplete));
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
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Editor chat streaming update processing failed");
                        streamFailed = true;
                        streamError = ex.Message;
                        break;
                    }

                    foreach (var updateToYield in updatesToYield)
                        yield return updateToYield;
                }
            }
            finally
            {
                try
                {
                    await enumerator.DisposeAsync();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Editor chat streaming enumerator disposal failed");
                    streamFailed = true;
                    streamError ??= ex.Message;
                }
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
                var aiFunction = aiTools.OfType<AIFunction>().FirstOrDefault(function => function.Name == pendingCall.Name);
                ToolInvocationOutcome toolOutcome;
                if (aiFunction is null)
                {
                    var message = $"Unknown tool '{pendingCall.Name}'.";
                    logger.LogWarning("Editor chat tool '{Tool}' failed: {Message}", pendingCall.Name, message);
                    toolOutcome = new ToolInvocationOutcome($"Error: {message}", message, Cancelled: false);
                }
                else if (IsRevisionAgentsTool(pendingCall.Name))
                {
                    Guid? lastRevisionJobId = null;
                    await using var subscription = revisionJobNotifier.Subscribe(projectId);
                    await using var updateEnumerator = subscription.ReadAllAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
                    var updateTask = updateEnumerator.MoveNextAsync().AsTask();
                    var invokeTask = InvokeToolAsync(aiFunction, pendingCall, cancellationToken);

                    while (!invokeTask.IsCompleted)
                    {
                        var completed = await Task.WhenAny(invokeTask, updateTask);
                        if (completed == invokeTask)
                            break;

                        bool hasUpdate;
                        try
                        {
                            hasUpdate = await updateTask;
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        if (!hasUpdate)
                            break;

                        if (await TryBuildRevisionJobUpdateAsync(
                                updateEnumerator.Current,
                                projectId,
                                conversation.Id,
                                pendingCall.CallId,
                                cancellationToken) is { } revisionUpdate)
                        {
                            lastRevisionJobId = revisionUpdate.JobId;
                            yield return revisionUpdate;
                        }

                        updateTask = updateEnumerator.MoveNextAsync().AsTask();
                    }

                    while (updateTask.IsCompletedSuccessfully && updateTask.Result)
                    {
                        if (await TryBuildRevisionJobUpdateAsync(
                                updateEnumerator.Current,
                                projectId,
                                conversation.Id,
                                pendingCall.CallId,
                                cancellationToken) is { } revisionUpdate)
                        {
                            lastRevisionJobId = revisionUpdate.JobId;
                            yield return revisionUpdate;
                        }

                        updateTask = updateEnumerator.MoveNextAsync().AsTask();
                    }

                    toolOutcome = await invokeTask;
                    var finalJobId = TryReadRevisionJobId(toolOutcome.Result) ?? lastRevisionJobId;
                    if (finalJobId is { } revisionJobId)
                    {
                        var finalJob = await revisionAgents.GetJobAsync(revisionJobId, CancellationToken.None);
                        if (finalJob is not null)
                        {
                            var progress = EditorRevisionAgentService.ToProgress(finalJob);
                            yield return new EditorChatRevisionJobUpdated(
                                pendingCall.CallId,
                                revisionJobId,
                                SessionId: null,
                                RevisionUpdateKindForStatus(progress.Status),
                                DateTime.UtcNow,
                                progress);
                        }
                    }
                }
                else
                {
                    toolOutcome = await InvokeToolAsync(aiFunction, pendingCall, cancellationToken);
                }
                stopwatch.Stop();

                if (toolOutcome.Cancelled)
                {
                    yield return new EditorChatTurnError("Cancelled.", Cancelled: true);
                    yield break;
                }

                var toolResult = toolOutcome.Result;
                var toolError = toolOutcome.Error;
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
                    BuildToolResultForModel(pendingCall.Name, toolResult ?? string.Empty, EffectiveMaxToolResultCharsForModel())));
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

    private static IEnumerable<ChatMessage> BuildModelHistory(IEnumerable<EditorMessage> history)
    {
        foreach (var message in history)
        {
            var chatMessage = ToModelHistoryMessage(message);
            if (chatMessage is not null)
                yield return chatMessage;
        }
    }

    private static ChatMessage? ToModelHistoryMessage(EditorMessage message) => message.Role switch
    {
        EditorMessageRole.System when !string.IsNullOrWhiteSpace(message.Content) => new ChatMessage(ChatRole.System, message.Content),
        EditorMessageRole.User => new ChatMessage(ChatRole.User, message.Content),
        EditorMessageRole.Assistant when !string.IsNullOrWhiteSpace(message.Content) => new ChatMessage(ChatRole.Assistant, message.Content),
        _ => null,
    };

    private async Task AddAutomaticVisualSnapshotsAsync(
        List<ChatMessage> messages,
        Chapter? currentChapter,
        LlmProvider? provider,
        CancellationToken cancellationToken)
    {
        if (currentChapter is null || provider is null || !CodexProvider.IsCodex(provider))
            return;
        if (!await providerService.IsVisionProviderWorkingAsync(provider.Id, cancellationToken))
            return;

        IReadOnlyList<ChapterVisualSnapshot> snapshots;
        try
        {
            snapshots = await chapterVisuals.RenderSnapshotsAsync(currentChapter.Id, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to render automatic chapter visual snapshots for chapter {ChapterId}", currentChapter.Id);
            return;
        }

        if (snapshots.Count == 0)
            return;

        var contents = new List<AIContent>
        {
            new TextContent(
                "Automatic visual context for the current chapter follows. These are rendered paginated snapshots of the chapter layout, provided with the text visual manifest already included in system context."),
        };
        foreach (var snapshot in snapshots)
        {
            contents.Add(new TextContent($"\nPage {snapshot.PageNumber}: {snapshot.FileName}"));
            contents.Add(new DataContent(snapshot.Data, snapshot.ContentType)
            {
                Name = snapshot.FileName,
            });
        }

        messages.Add(new ChatMessage(ChatRole.User, contents));
    }

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

    private int EffectiveMaxToolResultCharsForModel() => Math.Max(1000, editorOptions.Value.MaxToolResultCharsForModel);

    private static string BuildToolResultForModel(string toolName, string result, int maxToolResultCharsForModel)
    {
        if (string.Equals(toolName, "start_revision_agents", StringComparison.Ordinal))
            return result;

        if (result.Length <= maxToolResultCharsForModel)
            return result;

        var message = string.Equals(toolName, "read_chapter", StringComparison.Ordinal)
            ? "[Tool result exceeded the model-facing limit after pagination. Request a specific page from the returned pagination metadata.]"
            : "[Tool result truncated before returning it to the model.]";

        return result[..maxToolResultCharsForModel]
            + "\n\n" + message;
    }

    private async Task<ToolInvocationOutcome> InvokeToolAsync(
        AIFunction aiFunction,
        PendingToolCall pendingCall,
        CancellationToken cancellationToken)
    {
        try
        {
            var invokeResult = await aiFunction.InvokeAsync(
                ToolCallArguments.Create(pendingCall.Content.Arguments, pendingCall.ArgumentsJson),
                cancellationToken);
            return new ToolInvocationOutcome(invokeResult?.ToString() ?? string.Empty, Error: null, Cancelled: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ToolInvocationOutcome(string.Empty, Error: null, Cancelled: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Editor chat tool '{Tool}' failed", pendingCall.Name);
            return new ToolInvocationOutcome($"Error: {ex.Message}", ex.Message, Cancelled: false);
        }
    }

    private async Task<EditorChatRevisionJobUpdated?> TryBuildRevisionJobUpdateAsync(
        EditorRevisionJobUpdate update,
        Guid projectId,
        Guid conversationId,
        string toolCallId,
        CancellationToken cancellationToken)
    {
        if (update.ProjectId != projectId
            || update.ConversationId != conversationId
            || !string.Equals(update.ToolCallId, toolCallId, StringComparison.Ordinal))
        {
            return null;
        }

        var progress = await ReadRevisionProgressInFreshScopeAsync(update.JobId, cancellationToken);
        return progress is null
            ? null
            : new EditorChatRevisionJobUpdated(
                toolCallId,
                update.JobId,
                update.SessionId,
                update.Kind,
                update.CreatedAtUtc,
                progress);
    }

    private async Task<EditorRevisionJobProgress?> ReadRevisionProgressInFreshScopeAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var scopedRevisionAgents = scope.ServiceProvider.GetRequiredService<IEditorRevisionAgentService>();
        var job = await scopedRevisionAgents.GetJobAsync(jobId, cancellationToken);
        return job is null ? null : EditorRevisionAgentService.ToProgress(job);
    }

    private static bool IsRevisionAgentsTool(string toolName) =>
        string.Equals(toolName, "start_revision_agents", StringComparison.Ordinal);

    private static EditorRevisionJobUpdateKind RevisionUpdateKindForStatus(EditorRevisionJobStatus status) => status switch
    {
        EditorRevisionJobStatus.Completed => EditorRevisionJobUpdateKind.Completed,
        EditorRevisionJobStatus.Failed => EditorRevisionJobUpdateKind.Failed,
        EditorRevisionJobStatus.Cancelled => EditorRevisionJobUpdateKind.Cancelled,
        _ => EditorRevisionJobUpdateKind.Progress,
    };

    private static Guid? TryReadRevisionJobId(string? result)
    {
        if (string.IsNullOrWhiteSpace(result)) return null;
        try
        {
            using var document = JsonDocument.Parse(result);
            if (document.RootElement.TryGetProperty("jobId", out var jobId)
                && Guid.TryParse(jobId.GetString(), out var parsed))
            {
                return parsed;
            }
        }
        catch (JsonException) { }

        return null;
    }

    private static List<AIContent> BuildAssistantToolCallContents(IReadOnlyList<PersistedToolCall> calls) =>
        calls.Select(call => (AIContent)ToFunctionCallContent(call)).ToList();

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

    private sealed record ToolInvocationOutcome(string Result, string? Error, bool Cancelled);

    private sealed record PersistedToolCall(string CallId, string Name, string ArgumentsJson, int? TextOffset = null);
}
