using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Lorekeeper.ChatTurns;
using Lorekeeper.Chapters;
using Lorekeeper.Context;
using Lorekeeper.EntityVisuals;
using Lorekeeper.Images;
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
    IChatImageAttachmentService imageAttachments,
    IContextBuilder contextBuilder,
    IProjectImageService projectImages,
    IEntityVisualContextService entityVisualContext,
    IProjectImageGenerationRuntime imageRuntime,
    ILlmProviderService providerService,
    IChatClientFactory chatClientFactory,
    EditorChatTools tools,
    OutlineCollaborationTools outlineTools,
    IEditorContestService contestService,
    IEditorRevisionJobNotifier revisionJobNotifier,
    IEditorRevisionAgentService revisionAgents,
    IAiChangeApprovalService changeApproval,
    IAiChangeRepository changes,
    IServiceScopeFactory scopeFactory,
    ChatTurnEngine turnEngine,
    IOptions<AgentOptions> options,
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
        await conversations.LoadTranscriptMessagesAsync(conversationId, cancellationToken);

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
        await imageAttachments.ClearSurfaceAsync(projectId, ChatTurnSurface.Editor, cancellationToken);
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
        IReadOnlyList<Guid> imageIds,
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

        var visionReady = await providerService.IsVisionProviderWorkingAsync(providerAvailability.Provider.Id, cancellationToken);
        if (imageIds.Count > 0 && !visionReady)
        {
            yield return new EditorChatTurnError("The active chat provider has not passed Test Vision. Run Test Vision in Settings > Providers before sending images.", Cancelled: false);
            yield break;
        }
        await imageAttachments.ResolveAsync(projectId, imageIds, cancellationToken);

        var nextOrder = await conversations.GetMaxOrderAsync(conversation.Id, cancellationToken) + 1;
        var userMessage = new EditorMessage
        {
            ConversationId = conversation.Id,
            Order = nextOrder++,
            Role = EditorMessageRole.User,
            Content = userText.Trim(),
            Status = EditorMessageStatus.Completed,
        };
        conversation.UpdatedAt = DateTime.UtcNow;
        await turnEngine.AddMessageAsync(conversations, userMessage, cancellationToken);
        await imageAttachments.PersistAsync(projectId, ChatTurnSurface.Editor, userMessage.Id, imageIds, cancellationToken);

        IChatClient chat = null!;
        IList<AITool> aiTools = null!;
        EditorChatContext editorContext = null!;
        Chapter? currentChapter = null;
        var contestModeEnabled = false;
        IReadOnlyList<EntityVisualContextReference> initialEntityVisuals = [];
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

            var assembly = await contextBuilder.BuildAsync(
                new ContextBuildRequest(project, currentChapter, userText, ContextBuildPurpose.Editor),
                cancellationToken);
            initialEntityVisuals = assembly.Visuals;
            contestModeEnabled = project.ContestModeEnabled;
            systemPrompt = assembly.Assemble();
            userMessage.ContextSnapshotJson = assembly.SnapshotJson();
            await turnEngine.UpdateMessageAsync(conversations, userMessage, cancellationToken);

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
                providerAvailability.Provider.Id,
                visionReady,
                OnToolMutated,
                project.AiChangeApprovalEnabled,
                autoPinReadEntities: !contestModeEnabled,
                outlineStaging,
                editorStaging,
                cancellationToken);
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
        if (await entityVisualContext.BuildVisionMessageAsync(
            projectId,
            initialEntityVisuals,
            visionReady,
            "Canonical entity references and explicit-image visual context follow. Entity mappings are stable appearance/design references, not scene tags: inspect each label, association origin, image source, purpose, and visible content before using it.",
            cancellationToken) is { } entityVisualMessage)
        {
            messages.Add(entityVisualMessage);
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
            var activeAssistant = new EditorMessage
            {
                ConversationId = conversation.Id,
                Order = nextOrder++,
                Role = EditorMessageRole.Assistant,
                Content = string.Empty,
                Status = EditorMessageStatus.Pending,
            };
            await turnEngine.AddMessageAsync(conversations, activeAssistant, cancellationToken);

            ChatRoundCompleted? completedRound = null;
            await foreach (var update in turnEngine.StreamRoundAsync(chat, messages, chatOptions, cancellationToken))
            {
                switch (update)
                {
                    case ChatRoundTextDelta text:
                        yield return new EditorChatTextDelta(text.Text);
                        break;
                    case ChatRoundToolCallStarted started:
                        yield return new EditorChatToolCallStarted(started.CallId, started.ToolName, started.ArgumentsJson, started.ArgumentsComplete);
                        break;
                    case ChatRoundToolCallArgumentsDelta delta:
                        yield return new EditorChatToolCallArgumentsDelta(delta.CallId, delta.ArgumentsDelta, delta.ArgumentsComplete);
                        break;
                    case ChatRoundCompleted completed:
                        completedRound = completed;
                        break;
                    case ChatRoundFailed failed:
                        activeAssistant.Content = failed.Text;
                        activeAssistant.Status = failed.Cancelled
                            ? EditorMessageStatus.Cancelled
                            : EditorMessageStatus.Failed;
                        activeAssistant.ErrorMessage = failed.Cancelled ? "Cancelled by user." : failed.Message;
                        await SafePersistAsync(activeAssistant);
                        yield return new EditorChatTurnError(failed.Message, failed.Cancelled);
                        yield break;
                }
            }

            DrainMutated();
            if (completedRound is null)
            {
                activeAssistant.Status = EditorMessageStatus.Failed;
                activeAssistant.ErrorMessage = "Editor chat streaming ended without a completed round.";
                await SafePersistAsync(activeAssistant);
                yield return new EditorChatTurnError(activeAssistant.ErrorMessage, Cancelled: false);
                yield break;
            }

            var textBuilder = new StringBuilder(completedRound.Text);
            var pendingCalls = completedRound.ToolCalls.ToList();

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
                .Select(ChatToolCallManifest.From)
                .ToList();
            activeAssistant.Content = textBuilder.ToString();
            activeAssistant.ToolCallsJson = JsonSerializer.Serialize(manifest);
            activeAssistant.Status = EditorMessageStatus.Completed;
            await SafePersistAsync(activeAssistant);

            messages.Add(new ChatMessage(
                ChatRole.Assistant,
                ChatTurnEngine.BuildAssistantContents(textBuilder.ToString(), pendingCalls)));

            var resultContents = new List<AIContent>();
            var modelOnlyImagesForNextRound = new List<EditorChatModelImageAttachment>();
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
                ChatToolInvocationOutcome toolOutcome;
                if (aiFunction is null)
                {
                    var message = $"Unknown tool '{pendingCall.Name}'.";
                    logger.LogWarning("Editor chat tool '{Tool}' failed: {Message}", pendingCall.Name, message);
                    toolOutcome = new ChatToolInvocationOutcome($"Error: {message}", message, Cancelled: false);
                }
                else if (IsRevisionAgentsTool(pendingCall.Name))
                {
                    Guid? lastRevisionJobId = null;
                    using var updateCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    await using var subscription = revisionJobNotifier.Subscribe(projectId);
                    await using var updateEnumerator = subscription
                        .ReadAllAsync(updateCancellation.Token)
                        .GetAsyncEnumerator(updateCancellation.Token);
                    var updateTask = updateEnumerator.MoveNextAsync().AsTask();
                    var invokeTask = turnEngine.InvokeToolAsync(aiFunction, pendingCall, cancellationToken);
                    try
                    {
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
                            catch (OperationCanceledException) when (updateCancellation.IsCancellationRequested)
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
                                if (ShouldRefreshForCompletedRevisionSession(editorContext, revisionUpdate))
                                    yield return new EditorChatMutated();
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
                                if (ShouldRefreshForCompletedRevisionSession(editorContext, revisionUpdate))
                                    yield return new EditorChatMutated();
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
                    finally
                    {
                        await CompleteRevisionUpdateReadAsync(updateCancellation, updateTask);
                    }
                }
                else
                {
                    string? lastProgressKey = null;
                    var invokeTask = turnEngine.InvokeToolAsync(aiFunction, pendingCall, cancellationToken);

                    while (!invokeTask.IsCompleted)
                    {
                        if (await TryBuildImageGenerationJobUpdateAsync(
                                editorContext,
                                projectId,
                                pendingCall.CallId,
                                cancellationToken) is { } imageUpdate)
                        {
                            var progressKey = ImageProgressKey(imageUpdate);
                            if (!string.Equals(progressKey, lastProgressKey, StringComparison.Ordinal))
                            {
                                lastProgressKey = progressKey;
                                yield return imageUpdate;
                            }
                        }

                        await Task.WhenAny(invokeTask, Task.Delay(250, cancellationToken));
                    }

                    toolOutcome = await invokeTask;
                    if (await TryBuildImageGenerationJobUpdateAsync(
                            editorContext,
                            projectId,
                            pendingCall.CallId,
                            CancellationToken.None) is { } finalImageUpdate)
                    {
                        var progressKey = ImageProgressKey(finalImageUpdate);
                        if (!string.Equals(progressKey, lastProgressKey, StringComparison.Ordinal))
                            yield return finalImageUpdate;
                    }
                }
                stopwatch.Stop();

                if (toolOutcome.Cancelled)
                {
                    yield return new EditorChatTurnError("Cancelled.", Cancelled: true);
                    yield break;
                }

                var toolResult = toolOutcome.Result;
                var toolError = toolOutcome.Error;
                var visuals = editorContext.DrainVisuals(pendingCall.CallId);
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
                await turnEngine.AddMessageAsync(conversations, toolMessage, CancellationToken.None);
                await PersistVisualsAsync(toolMessage.Id, pendingCall.CallId, visuals);

                resultContents.Add(new FunctionResultContent(
                    pendingCall.CallId,
                    toolResult ?? string.Empty));
                var modelImages = editorContext.DrainModelOnlyImages();
                if (modelImages.Count > 0)
                    modelOnlyImagesForNextRound.AddRange(modelImages);
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

                yield return new EditorChatToolCallCompleted(
                    pendingCall.CallId,
                    pendingCall.Name,
                    toolError is null ? toolResult : null,
                    toolError,
                    stopwatch.Elapsed.TotalMilliseconds,
                    visuals);

                var workspaceMutation = toolError is null
                    ? TryWorkspaceMutation(pendingCall.Name, pendingCall.ArgumentsJson, toolResult)
                    : null;
                if (workspaceMutation is not null)
                    yield return workspaceMutation;

                if (toolError is null
                    && string.Equals(pendingCall.Name, "start_contest", StringComparison.Ordinal)
                    && editorContext.TryTakeContestRequest(out var contestRequest))
                {
                    var contestSnapshotToolResults = resultContents
                        .OfType<FunctionResultContent>()
                        .Where(result => result.CallId != pendingCall.CallId)
                        .Cast<AIContent>()
                        .ToList();
                    var snapshot = new ContestTurnSnapshot(
                        BuildContestSnapshotMessages(messages, contestSnapshotToolResults),
                        initialEntityVisuals);

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

                if (DrainMutated() && workspaceMutation is null)
                    yield return new EditorChatMutated();
            }

            messages.Add(new ChatMessage(ChatRole.Tool, resultContents));
            if (modelOnlyImagesForNextRound.Count > 0)
                messages.Add(await BuildModelOnlyImageMessageAsync(projectId, modelOnlyImagesForNextRound));

            if (iteration == maxIterations - 1)
            {
                yield return new EditorChatTurnError(ChatTurnEngine.ToolLoopLimitError(maxIterations), Cancelled: false);
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
            await turnEngine.UpdateMessageAsync(conversations, message, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist editor chat message {MessageId}", message.Id);
        }
    }

    private async Task PersistVisualsAsync(
        Guid messageId,
        string? toolCallId,
        IReadOnlyList<EditorChatVisualAttachment> visuals)
    {
        if (visuals.Count == 0)
            return;

        await conversations.AddMessageVisualsAsync(visuals.Select((visual, index) => new EditorMessageVisual
        {
            Id = visual.Id,
            MessageId = messageId,
            SortOrder = index,
            ToolCallId = visual.ToolCallId ?? toolCallId,
            Title = visual.Title,
            Caption = visual.Caption,
            SourceKind = visual.SourceKind,
            SourceRefId = visual.SourceRefId,
            ContentType = visual.ContentType,
            FileName = visual.FileName,
            Width = visual.Width,
            Height = visual.Height,
            Data = visual.Data,
        }), CancellationToken.None);
        await conversations.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<ChatMessage> BuildModelOnlyImageMessageAsync(
        Guid projectId,
        IReadOnlyList<EditorChatModelImageAttachment> images)
    {
        var contents = new List<AIContent>
        {
            new TextContent("Images returned by the previous Editor Chat tool call are attached as model-only visual context. They may be project images or freshly rendered chapter snapshots. Use them when deciding whether further edits or layout actions are needed."),
        };

        var seen = new HashSet<Guid>();
        foreach (var image in images)
        {
            if (!seen.Add(image.Id))
                continue;

            byte[] data;
            string contentType;
            string fileName;
            if (image.ProjectImageId is { } projectImageId)
            {
                var projectImage = await projectImages.GetDataAsync(projectId, projectImageId, cancellationToken: CancellationToken.None);
                if (projectImage is null)
                    continue;
                data = projectImage.Data;
                contentType = projectImage.ContentType;
                fileName = projectImage.FileName;
            }
            else if (image.Data is { Length: > 0 })
            {
                data = image.Data;
                contentType = image.ContentType;
                fileName = image.FileName;
            }
            else
            {
                continue;
            }

            contents.Add(new TextContent($"\nImage {image.Id:N}: {image.FileName}"));
            contents.Add(new DataContent(data, contentType)
            {
                Name = fileName,
            });
        }

        return new ChatMessage(ChatRole.User, contents);
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

    private async Task<EditorChatImageGenerationJobUpdated?> TryBuildImageGenerationJobUpdateAsync(
        EditorChatContext editorContext,
        Guid projectId,
        string toolCallId,
        CancellationToken cancellationToken)
    {
        if (editorContext.CurrentImageGenerationJobId is not { } jobId)
            return null;

        var runtimeJob = imageRuntime.GetSnapshot().Jobs
            .FirstOrDefault(job => job.ProjectId == projectId && job.JobId == jobId);
        if (runtimeJob is not null)
            return BuildImageGenerationJobUpdate(toolCallId, runtimeJob);

        await using var scope = scopeFactory.CreateAsyncScope();
        var scopedImageJobs = scope.ServiceProvider.GetRequiredService<IProjectImageJobService>();
        var persisted = await scopedImageJobs.GetJobAsync(projectId, jobId, cancellationToken);
        return persisted is null ? null : BuildImageGenerationJobUpdate(toolCallId, persisted);
    }

    private static EditorChatImageGenerationJobUpdated BuildImageGenerationJobUpdate(
        string toolCallId,
        ProjectImageGenerationJobRuntimeView job)
    {
        var outputs = job.Outputs
            .OrderBy(output => output.OutputIndex)
            .Select(output => new EditorChatImageGenerationOutputProgress(
                output.OutputIndex,
                output.Status.ToString(),
                output.Message,
                string.IsNullOrWhiteSpace(output.Error) ? null : output.Error,
                output.Attempt,
                output.PartialImageDataUrl))
            .ToList();

        return BuildImageGenerationJobUpdate(
            toolCallId,
            job.JobId,
            job.IsRunning ? "Running" : StatusFromOutputs(outputs),
            outputs);
    }

    private static EditorChatImageGenerationJobUpdated BuildImageGenerationJobUpdate(
        string toolCallId,
        ProjectImageJobView job)
    {
        var outputs = job.OutputStates
            .OrderBy(output => output.OutputIndex)
            .Select(output => new EditorChatImageGenerationOutputProgress(
                output.OutputIndex,
                output.Status.ToString(),
                output.Message,
                string.IsNullOrWhiteSpace(output.Error) ? null : output.Error,
                output.Attempt,
                PartialImageDataUrl: null))
            .ToList();

        return BuildImageGenerationJobUpdate(
            toolCallId,
            job.Id,
            job.Status.ToString(),
            outputs);
    }

    private static EditorChatImageGenerationJobUpdated BuildImageGenerationJobUpdate(
        string toolCallId,
        Guid jobId,
        string status,
        IReadOnlyList<EditorChatImageGenerationOutputProgress> outputs)
    {
        var total = outputs.Count;
        return new EditorChatImageGenerationJobUpdated(
            toolCallId,
            jobId,
            status,
            DateTime.UtcNow,
            total,
            CountOutputs(outputs, ProjectImageOutputStatus.Queued.ToString()),
            CountOutputs(outputs, ProjectImageOutputStatus.Running.ToString(), ProjectImageOutputStatus.Generating.ToString()),
            CountOutputs(outputs, ProjectImageOutputStatus.Succeeded.ToString()),
            CountOutputs(outputs, ProjectImageOutputStatus.Failed.ToString()),
            CountOutputs(outputs, ProjectImageOutputStatus.Cancelled.ToString()),
            outputs,
            outputs.LastOrDefault(output => !string.IsNullOrWhiteSpace(output.PartialImageDataUrl))?.PartialImageDataUrl);
    }

    private static string ImageProgressKey(EditorChatImageGenerationJobUpdated progress)
    {
        var sb = new StringBuilder()
            .Append(progress.JobId).Append('|')
            .Append(progress.Status).Append('|')
            .Append(progress.TotalCount).Append('|')
            .Append(progress.QueuedCount).Append('|')
            .Append(progress.RunningCount).Append('|')
            .Append(progress.CompletedCount).Append('|')
            .Append(progress.FailedCount).Append('|')
            .Append(progress.CancelledCount).Append('|')
            .Append(StableStringHash(progress.LatestPartialImageDataUrl));

        foreach (var output in progress.Outputs)
        {
            sb.Append('|')
                .Append(output.OutputIndex).Append(':')
                .Append(output.Status).Append(':')
                .Append(output.Attempt).Append(':')
                .Append(output.Message).Append(':')
                .Append(output.ErrorMessage).Append(':')
                .Append(StableStringHash(output.PartialImageDataUrl));
        }

        return sb.ToString();
    }

    private static string StatusFromOutputs(IReadOnlyList<EditorChatImageGenerationOutputProgress> outputs)
    {
        if (outputs.Count == 0)
            return "Running";
        if (outputs.All(output => output.Status == ProjectImageOutputStatus.Succeeded.ToString()))
            return ProjectImageGenerationJobStatus.Succeeded.ToString();
        if (outputs.Any(output => output.Status == ProjectImageOutputStatus.Succeeded.ToString()))
            return ProjectImageGenerationJobStatus.CompletedWithErrors.ToString();
        if (outputs.Any(output => output.Status == ProjectImageOutputStatus.Failed.ToString()))
            return ProjectImageGenerationJobStatus.Failed.ToString();
        if (outputs.Any(output => output.Status == ProjectImageOutputStatus.Cancelled.ToString()))
            return ProjectImageGenerationJobStatus.Cancelled.ToString();
        if (outputs.Any(output => output.Status == ProjectImageOutputStatus.Running.ToString()
            || output.Status == ProjectImageOutputStatus.Generating.ToString()))
        {
            return ProjectImageGenerationJobStatus.Running.ToString();
        }

        return ProjectImageGenerationJobStatus.Queued.ToString();
    }

    private static int CountOutputs(IReadOnlyList<EditorChatImageGenerationOutputProgress> outputs, params string[] statuses) =>
        outputs.Count(output => statuses.Contains(output.Status, StringComparer.Ordinal));

    private static int StableStringHash(string? value) =>
        string.IsNullOrEmpty(value) ? 0 : StringComparer.Ordinal.GetHashCode(value);

    private static bool IsRevisionAgentsTool(string toolName) =>
        string.Equals(toolName, "start_revision_agents", StringComparison.Ordinal);

    internal static async Task CompleteRevisionUpdateReadAsync(
        CancellationTokenSource updateCancellation,
        Task<bool> updateTask)
    {
        await updateCancellation.CancelAsync();
        try
        {
            await updateTask;
        }
        catch (OperationCanceledException) when (updateCancellation.IsCancellationRequested)
        {
        }
    }

    internal static EditorWorkspaceMutated? TryWorkspaceMutation(
        string toolName,
        string argumentsJson,
        string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
            return null;
        try
        {
            using var resultDocument = JsonDocument.Parse(resultJson);
            var root = resultDocument.RootElement;
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
                return null;
            if (toolName is "generate_project_image" or "edit_project_image" or "wait_project_image_job")
            {
                return root.TryGetProperty("outputImageIds", out var outputs)
                    && outputs.ValueKind == JsonValueKind.Array
                    && outputs.GetArrayLength() > 0
                        ? new EditorWorkspaceMutated(EditorWorkspaceMutationKind.ImageLibrary)
                        : null;
            }
            if (!root.TryGetProperty("mutation", out var mutation) || mutation.ValueKind != JsonValueKind.Object)
                return null;

            var kind = ReadString(mutation, "kind");
            var id = ReadGuid(mutation, "id");
            var revision = ReadLong(mutation, "revision") ?? ReadLong(root, "revision");
            var changedIds = ReadStrings(root, "changedIds");
            using var argumentsDocument = ParseJson(argumentsJson);
            var arguments = argumentsDocument.RootElement;

            if (string.Equals(kind, "manuscript", StringComparison.OrdinalIgnoreCase))
                return new EditorWorkspaceMutated(EditorWorkspaceMutationKind.Manuscript, ChapterId: id, Revision: revision, ChangedIds: changedIds);
            if (string.Equals(kind, "projectPageSetup", StringComparison.OrdinalIgnoreCase))
                return new EditorWorkspaceMutated(EditorWorkspaceMutationKind.ProjectPageSetup, Revision: revision, ChangedIds: changedIds);
            if (string.Equals(kind, "pageComposition", StringComparison.OrdinalIgnoreCase))
            {
                var variantId = ReadGuid(root, "variantId") ?? ReadGuid(arguments, "variantId");
                var selectedObjectId = toolName.Contains("patch", StringComparison.Ordinal)
                    ? ReadGuid(arguments, "targetId")
                    : null;
                return new EditorWorkspaceMutated(
                    EditorWorkspaceMutationKind.PageComposition,
                    CompositionId: id,
                    VariantId: variantId,
                    Revision: revision,
                    ChangedIds: changedIds,
                    SelectedObjectId: selectedObjectId);
            }
            if (string.Equals(kind, "pageCompositionVariant", StringComparison.OrdinalIgnoreCase))
                return new EditorWorkspaceMutated(
                    EditorWorkspaceMutationKind.PageComposition,
                    VariantId: id,
                    Revision: revision,
                    ChangedIds: changedIds,
                    SelectedObjectId: ReadGuid(mutation, "selectId") ?? ReadGuid(root, "selectId") ?? ReadGuid(arguments, "targetId"));
            return new EditorWorkspaceMutated(EditorWorkspaceMutationKind.Other, Revision: revision, ChangedIds: changedIds);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonDocument ParseJson(string json)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json); }
        catch (JsonException) { return JsonDocument.Parse("{}"); }
    }

    private static Guid? ReadGuid(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
        && Guid.TryParse(property.GetString(), out var value)
            ? value
            : null;

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static long? ReadLong(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out var value)
            ? value
            : null;

    private static IReadOnlyList<string> ReadStrings(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Array
            ? property.EnumerateArray().Select(item => item.ToString()).ToList()
            : [];

    private static bool ShouldRefreshForCompletedRevisionSession(
        EditorChatContext editorContext,
        EditorChatRevisionJobUpdated update) =>
        !editorContext.ReviewEdits
        && update.Kind == EditorRevisionJobUpdateKind.SessionCompleted
        && update.SessionId is { } sessionId
        && update.Progress.Sessions.Any(session =>
            session.SessionId == sessionId
            && session.Status == EditorRevisionSessionStatus.Completed);

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

}
