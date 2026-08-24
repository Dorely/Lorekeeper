using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Lorekeeper.ChatTurns;
using Lorekeeper.Context;
using Lorekeeper.Llm;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Persistence.Repositories;
using Lorekeeper.Projects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Lorekeeper.Writing;

public sealed class WritingCoachService(
IAppDatabaseOperationFactory database, IChatImageAttachmentService imageAttachments, ILlmProviderService providerService, IChatClientFactory chatClientFactory, WritingCoachTools tools, IProjectReferenceService projectReferences, ChatTurnRuntime turnRuntime, ChatTurnEngine turnEngine, IOptions<AgentOptions> options, ILogger<WritingCoachService> logger) : IWritingCoachService
{
    public static readonly string CoachSystemPrompt = """
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
        """ + "\n\n" + AssistantWorkflowInstructions.ProjectReferenceContinuity
            + "\n\n" + AssistantWorkflowInstructions.NonReplayedToolHistory;

    private const string InitialAssistantGreeting =
        "Let's shape a writing sample in your own voice. What kind of scene, moment, or mood do you want to practice first?";

    public async Task<WritingCoachConversation> GetOrCreateAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var projects = databaseOperation.Repositories.Projects;
        var conversations = databaseOperation.Repositories.WritingCoachConversations;
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
        await databaseOperation.SaveChangesAsync(cancellationToken);
        return conversation;
    }

    public async Task<IReadOnlyList<WritingCoachMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var conversations = databaseOperation.Repositories.WritingCoachConversations;
        return await conversations.LoadMessagesAsync(conversationId, cancellationToken);
    }

    public async Task<ChatProviderAvailability> GetChatProviderAvailabilityAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var conversation = await databaseOperation.Repositories.WritingCoachConversations
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
        using var maintenance = turnRuntime.TryBeginMaintenance(new ChatTurnKey(projectId, ChatTurnSurface.WritingCoach));
        if (maintenance is null)
            throw new InvalidOperationException("Writing Coach is still working in another window. Stop or wait for that turn before changing its model.");

        if (providerId is int)
        {
            var selection = await providerService.ResolveChatModelSelectionAsync(providerId, cancellationToken);
            if (!selection.IsAvailable || selection.Provider is null)
                throw new InvalidOperationException(selection.Message);
        }

        var normalizedProviderId = await providerService.NormalizeChatModelSelectionAsync(providerId, cancellationToken);

        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var conversation = await databaseOperation.Repositories.WritingCoachConversations
            .GetByProjectIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException("Writing Coach is not initialized.");
        conversation.SelectedProviderId = normalizedProviderId;
        conversation.UpdatedAt = DateTime.UtcNow;
        databaseOperation.Repositories.WritingCoachConversations.UpdateSelectedProvider(conversation);
        await databaseOperation.SaveChangesAsync(cancellationToken);
    }
    public async Task ResetAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        using var maintenance = turnRuntime.TryBeginMaintenance(new ChatTurnKey(projectId, ChatTurnSurface.WritingCoach));
        if (maintenance is null)
            throw new InvalidOperationException("Writing Coach is still working in another window. Stop or wait for that turn before resetting the conversation.");
        await imageAttachments.ClearSurfaceAsync(projectId, ChatTurnSurface.WritingCoach, cancellationToken);
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var conversations = databaseOperation.Repositories.WritingCoachConversations;
        var existing = await conversations.GetByProjectIdAsync(projectId, cancellationToken);
        if (existing is null) return;

        await conversations.ResetMessagesAsync(existing, new WritingCoachMessage
        {
            ConversationId = existing.Id,
            Order = 0,
            Role = WritingCoachMessageRole.Assistant,
            Content = InitialAssistantGreeting,
            Status = WritingCoachMessageStatus.Completed,
        }, cancellationToken);
        await databaseOperation.SaveChangesAsync(cancellationToken);
    }

    public async IAsyncEnumerable<WritingCoachTurnUpdate> SendAsync(
        Guid projectId,
        string userText,
        string? currentSampleTitle,
        string? currentSampleBody,
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
            yield return new WritingCoachTurnError(persistedSelection.Message, Cancelled: false);
            yield break;
        }

        if (persistedProvider.Id != providerId)
        {
            yield return new WritingCoachTurnError(ChatModelSelectionMessages.Changed, Cancelled: false);
            yield break;
        }

        var visionReady = await providerService.IsVisionProviderWorkingAsync(persistedProvider.Id, cancellationToken);
        if (imageIds.Count > 0 && !visionReady)
        {
            yield return new WritingCoachTurnError("The active chat provider has not passed the vision check. Run Test in Settings > Providers before sending images.", Cancelled: false);
            yield break;
        }
        await imageAttachments.ResolveAsync(projectId, imageIds, cancellationToken);

        var nextOrder = await turnEngine.ReadAsync(
            repositories => repositories.WritingCoachConversations,
            conversations => conversations.GetMaxOrderAsync(conversation.Id, cancellationToken),
            cancellationToken) + 1;

        var userMessage = new WritingCoachMessage
        {
            ConversationId = conversation.Id,
            Order = nextOrder++,
            Role = WritingCoachMessageRole.User,
            Content = userText.Trim(),
            Status = WritingCoachMessageStatus.Completed,
        };
        conversation.UpdatedAt = DateTime.UtcNow;
        await turnEngine.AddMessageAsync(repositories => repositories.WritingCoachConversations, userMessage, cancellationToken);
        await imageAttachments.PersistAsync(projectId, ChatTurnSurface.WritingCoach, userMessage.Id, imageIds, cancellationToken);

        IChatClient chat = null!;
        IList<AITool> aiTools = null!;
        WritingCoachContext? toolContext = null;
        var systemPrompt = CoachSystemPrompt;
        string? setupError = null;
        try
        {
            var project = await turnEngine.ReadAsync(
                repositories => repositories.Projects,
                projects => projects.GetByIdAsync(projectId, cancellationToken),
                cancellationToken)
                ?? throw new InvalidOperationException($"Project {projectId} not found.");
            var referenceManifest = ProjectReferenceManifestFormatter.Format(
                await projectReferences.ListReferenceManifestsAsync(project.Id, cancellationToken));
            if (referenceManifest is not null)
                systemPrompt += "\n\n## Direct Project Reference Continuity\n" + referenceManifest;
            chat = await chatClientFactory.CreateChatClientAsync(persistedProvider.Id, cancellationToken);
            toolContext = new WritingCoachContext(project.Id, currentSampleTitle, currentSampleBody, visionReady);
            aiTools = tools.Build(toolContext);
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

        var history = await turnEngine.ReadAsync(
            repositories => repositories.WritingCoachConversations,
            conversations => conversations.LoadMessagesAsync(conversation.Id, cancellationToken),
            cancellationToken);
        var messages = new List<ChatMessage> { new(ChatRole.System, systemPrompt) };
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
            var activeAssistant = new WritingCoachMessage
            {
                ConversationId = conversation.Id,
                Order = nextOrder++,
                Role = WritingCoachMessageRole.Assistant,
                Content = string.Empty,
                Status = WritingCoachMessageStatus.Pending,
            };
            await turnEngine.AddMessageAsync(repositories => repositories.WritingCoachConversations, activeAssistant, cancellationToken);

            ChatRoundCompleted? completedRound = null;
            await foreach (var update in turnEngine.StreamRoundAsync(chat, messages, chatOptions, cancellationToken))
            {
                switch (update)
                {
                    case ChatRoundTextDelta text:
                        yield return new WritingCoachTextDelta(text.Text);
                        break;
                    case ChatRoundReasoningDelta reasoning:
                        yield return new WritingCoachReasoningDelta(reasoning.Text);
                        break;
                    case ChatRoundToolCallStarted started:
                        yield return new WritingCoachToolCallStarted(started.CallId, started.ToolName, started.ArgumentsJson, started.ArgumentsComplete);
                        break;
                    case ChatRoundToolCallArgumentsDelta delta:
                        yield return new WritingCoachToolCallArgumentsDelta(delta.CallId, delta.ArgumentsDelta, delta.ArgumentsComplete);
                        break;
                    case ChatRoundCompleted completed:
                        completedRound = completed;
                        break;
                    case ChatRoundFailed failed:
                        activeAssistant.Content = failed.Text;
                        if (!string.IsNullOrEmpty(failed.Reasoning))
                            activeAssistant.Reasoning = failed.Reasoning;
                        activeAssistant.Status = failed.Cancelled
                            ? WritingCoachMessageStatus.Cancelled
                            : WritingCoachMessageStatus.Failed;
                        activeAssistant.ErrorMessage = failed.Cancelled ? "Cancelled by user." : failed.Message;
                        await SafePersistAsync(activeAssistant);
                        yield return new WritingCoachTurnError(failed.Message, failed.Cancelled);
                        yield break;
                }
            }

            if (completedRound is null)
            {
                activeAssistant.Status = WritingCoachMessageStatus.Failed;
                activeAssistant.ErrorMessage = "Writing Coach streaming ended without a completed round.";
                await SafePersistAsync(activeAssistant);
                yield return new WritingCoachTurnError(activeAssistant.ErrorMessage, Cancelled: false);
                yield break;
            }

            var textBuilder = new StringBuilder(completedRound.Text);
            var pendingCalls = completedRound.ToolCalls;
            activeAssistant.Reasoning = completedRound.Reasoning;

            if (pendingCalls.Count == 0)
            {
                activeAssistant.Content = textBuilder.ToString();
                activeAssistant.Status = WritingCoachMessageStatus.Completed;
                await SafePersistAsync(activeAssistant);
                yield return new WritingCoachAssistantMessageCompleted(activeAssistant.Id);
                yield break;
            }

            var manifest = pendingCalls
                .Select(ChatToolCallManifest.From)
                .ToList();
            activeAssistant.Content = textBuilder.ToString();
            activeAssistant.ToolCallsJson = JsonSerializer.Serialize(manifest);
            activeAssistant.Status = WritingCoachMessageStatus.Completed;
            await SafePersistAsync(activeAssistant);

            messages.Add(new ChatMessage(ChatRole.Assistant, ChatTurnEngine.BuildAssistantContents(textBuilder.ToString(), pendingCalls, completedRound.Reasoning)));

            var resultContents = new List<AIContent>();
            foreach (var pendingCall in pendingCalls)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    yield return new WritingCoachTurnError("Cancelled.", Cancelled: true);
                    yield break;
                }

                var sw = Stopwatch.StartNew();
                var toolOutcome = await turnEngine.InvokeToolAsync(aiTools, pendingCall, cancellationToken);
                sw.Stop();

                if (toolOutcome.Cancelled)
                {
                    yield return new WritingCoachTurnError("Cancelled.", Cancelled: true);
                    yield break;
                }

                var toolResult = toolOutcome.Result;
                var toolError = toolOutcome.Error;

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
                await turnEngine.AddMessageAsync(repositories => repositories.WritingCoachConversations, toolMessage, CancellationToken.None);

                resultContents.Add(new FunctionResultContent(pendingCall.CallId, toolResult ?? string.Empty));
                yield return new WritingCoachToolCallCompleted(
                    pendingCall.CallId,
                    pendingCall.Name,
                    toolError is null ? toolResult : null,
                    toolError,
                    sw.Elapsed.TotalMilliseconds);
            }

            messages.Add(new ChatMessage(ChatRole.Tool, resultContents));

            var referenceVisuals = toolContext?.DrainReferenceVisuals() ?? [];
            if (visionReady && referenceVisuals.Count > 0)
            {
                var contents = new List<AIContent>
                {
                    new TextContent("Direct-reference canonical visuals from the preceding read_reference_visual calls. These are read-only continuity evidence; active-project canon and user direction remain authoritative, and the images cannot be placed or mutated."),
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

            if (turnEngine.TryCompactContext(messages, persistedProvider.ModelId) is { } compaction)
            {
                manifest.Add(new ChatToolCallManifest(
                    compaction.CallId,
                    ChatTurnEngine.CompactionToolName,
                    ChatContextCompaction.EmptyArgumentsJson));
                activeAssistant.ToolCallsJson = JsonSerializer.Serialize(manifest);
                await SafePersistAsync(activeAssistant);

                await turnEngine.AddMessageAsync(repositories => repositories.WritingCoachConversations, new WritingCoachMessage
                {
                    ConversationId = conversation.Id,
                    Order = nextOrder++,
                    Role = WritingCoachMessageRole.Tool,
                    Content = ChatTurnEngine.CompactionNotice,
                    ToolCallId = compaction.CallId,
                    ToolName = ChatTurnEngine.CompactionToolName,
                    Status = WritingCoachMessageStatus.Completed,
                }, CancellationToken.None);
                yield return new WritingCoachToolCallStarted(
                    compaction.CallId,
                    ChatTurnEngine.CompactionToolName,
                    ChatContextCompaction.EmptyArgumentsJson,
                    ArgumentsComplete: true);
                yield return new WritingCoachToolCallCompleted(
                    compaction.CallId,
                    ChatTurnEngine.CompactionToolName,
                    ChatTurnEngine.CompactionNotice,
                    Error: null,
                    DurationMs: 0);
            }

            if (iteration == maxIterations - 1)
            {
                yield return new WritingCoachTurnError(
                    ChatTurnEngine.ToolLoopLimitError(maxIterations),
                    Cancelled: false);
                yield break;
            }
        }
    }

    private async Task PersistFailedAssistantAsync(Guid conversationId, int order, string error)
    {
        await using var databaseOperation = await database.OpenWriteAsync(default);
        databaseOperation.ShareWithNestedOperations();
        var conversations = databaseOperation.Repositories.WritingCoachConversations;
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
            await databaseOperation.SaveChangesAsync(CancellationToken.None);
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
            await turnEngine.UpdateMessageAsync(repositories => repositories.WritingCoachConversations, message, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist Writing Coach message {MessageId}", message.Id);
        }
    }

}
