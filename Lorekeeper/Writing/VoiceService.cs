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

public sealed class VoiceService(
IAppDatabaseOperationFactory database, IChatImageAttachmentService imageAttachments, ILlmProviderService providerService, IChatClientFactory chatClientFactory, VoiceTools tools, IContextBuilder contextBuilder, ChatTurnRuntime turnRuntime, ChatTurnEngine turnEngine, IOptions<AgentOptions> options, ILogger<VoiceService> logger) : IVoiceService
{
    public static readonly string VoiceWorkflowInstructions = """
        Develop the author's writing samples and free-form character voice profiles.
        Direct build/edit requests authorize in-scope saves; brainstorming and comparisons remain conversational until the user chooses a direction.
        Read current records and their revisions before editing. Use list_writing_samples and read_writing_sample to resolve the visible sample; never guess its identity from its title.
        Use read_current_section for the visible workspace selection. It is a turn-start snapshot, not a substitute for a fresh record before writing.
        Preserve the author's style while helping with rhythm, diction, imagery, pacing, dialogue, and POV narration.
        Profiles may discuss vocabulary, rhythm, emotional variation, distinguishing traits, internal narration, and illustrative lines. No fixed headings are required.
        Samples remain general style evidence. Profile example lines illustrate a voice, not dialogue to repeat automatically.
        Create a Character only when the user's request requires it. Mutate only the active project.
        Surface conflicts with existing canon or briefs. Keep unrelated entity properties unchanged.
        Read back changes before reporting completion and distinguish saved work from suggestions.
        """ + "\n\n" + AssistantWorkflowInstructions.NonReplayedToolHistory;

    private const string InitialAssistantGreeting =
        "Let’s develop your writing voice or a character’s dialogue and inner narration. Choose a sample or character, or tell me what you want to build.";

    public async Task<VoiceConversation> GetOrCreateAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var projects = databaseOperation.Repositories.Projects;
        var conversations = databaseOperation.Repositories.VoiceConversations;
        var existing = await conversations.GetByProjectIdAsync(projectId, cancellationToken);
        if (existing is not null) return existing;

        _ = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");

        var conversation = new VoiceConversation { ProjectId = projectId };
        await conversations.AddConversationAsync(conversation, cancellationToken);

        var greeting = new VoiceMessage
        {
            ConversationId = conversation.Id,
            Order = 0,
            Role = VoiceMessageRole.Assistant,
            Content = InitialAssistantGreeting,
            Status = VoiceMessageStatus.Completed,
        };
        await conversations.AddMessageAsync(greeting, cancellationToken);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        return conversation;
    }

    public async Task<IReadOnlyList<VoiceMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var conversations = databaseOperation.Repositories.VoiceConversations;
        return await conversations.LoadMessagesAsync(conversationId, cancellationToken);
    }

    public async Task<ChatProviderAvailability> GetChatProviderAvailabilityAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var conversation = await databaseOperation.Repositories.VoiceConversations
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
        using var maintenance = turnRuntime.TryBeginMaintenance(new ChatTurnKey(projectId, ChatTurnSurface.Voice));
        if (maintenance is null)
            throw new InvalidOperationException("Voice is still working in another window. Stop or wait for that turn before changing its model.");

        if (providerId is int)
        {
            var selection = await providerService.ResolveChatModelSelectionAsync(providerId, cancellationToken);
            if (!selection.IsAvailable || selection.Provider is null)
                throw new InvalidOperationException(selection.Message);
        }

        var normalizedProviderId = await providerService.NormalizeChatModelSelectionAsync(providerId, cancellationToken);

        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var conversation = await databaseOperation.Repositories.VoiceConversations
            .GetByProjectIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException("Voice is not initialized.");
        conversation.SelectedProviderId = normalizedProviderId;
        conversation.UpdatedAt = DateTime.UtcNow;
        databaseOperation.Repositories.VoiceConversations.UpdateSelectedProvider(conversation);
        await databaseOperation.SaveChangesAsync(cancellationToken);
    }
    public async Task ResetAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        using var maintenance = turnRuntime.TryBeginMaintenance(new ChatTurnKey(projectId, ChatTurnSurface.Voice));
        if (maintenance is null)
            throw new InvalidOperationException("Voice is still working in another window. Stop or wait for that turn before resetting the conversation.");
        await imageAttachments.ClearSurfaceAsync(projectId, ChatTurnSurface.Voice, cancellationToken);
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var conversations = databaseOperation.Repositories.VoiceConversations;
        var existing = await conversations.GetByProjectIdAsync(projectId, cancellationToken);
        if (existing is null) return;

        await conversations.ResetMessagesAsync(existing, new VoiceMessage
        {
            ConversationId = existing.Id,
            Order = 0,
            Role = VoiceMessageRole.Assistant,
            Content = InitialAssistantGreeting,
            Status = VoiceMessageStatus.Completed,
        }, cancellationToken);
        await databaseOperation.SaveChangesAsync(cancellationToken);
    }

    public async IAsyncEnumerable<VoiceTurnUpdate> SendAsync(
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
            yield return new VoiceTurnError(persistedSelection.Message, Cancelled: false);
            yield break;
        }

        if (persistedProvider.Id != providerId)
        {
            yield return new VoiceTurnError(ChatModelSelectionMessages.Changed, Cancelled: false);
            yield break;
        }

        var visionReady = await providerService.IsVisionProviderWorkingAsync(persistedProvider.Id, cancellationToken);
        if (imageIds.Count > 0 && !visionReady)
        {
            yield return new VoiceTurnError("The active chat provider has not passed the vision check. Run Test in Settings > Providers before sending images.", Cancelled: false);
            yield break;
        }
        await imageAttachments.ResolveAsync(projectId, imageIds, cancellationToken);

        var nextOrder = await turnEngine.ReadAsync(
            repositories => repositories.VoiceConversations,
            conversations => conversations.GetMaxOrderAsync(conversation.Id, cancellationToken),
            cancellationToken) + 1;

        var userMessage = new VoiceMessage
        {
            ConversationId = conversation.Id,
            Order = nextOrder++,
            Role = VoiceMessageRole.User,
            Content = userText.Trim(),
            Status = VoiceMessageStatus.Completed,
        };
        conversation.UpdatedAt = DateTime.UtcNow;
        await turnEngine.AddMessageAsync(repositories => repositories.VoiceConversations, userMessage, cancellationToken);
        await imageAttachments.PersistAsync(projectId, ChatTurnSurface.Voice, userMessage.Id, imageIds, cancellationToken);

        IChatClient chat = null!;
        IList<AITool> aiTools = null!;
        VoiceContext? toolContext = null;
        var systemPrompt = string.Empty;
        string? setupError = null;
        try
        {
            var project = await turnEngine.ReadAsync(
                repositories => repositories.Projects,
                projects => projects.GetByIdAsync(projectId, cancellationToken),
                cancellationToken)
                ?? throw new InvalidOperationException($"Project {projectId} not found.");
            systemPrompt = (await contextBuilder.BuildAsync(new ContextBuildRequest(
                project, UserMessage: userText, Purpose: ContextBuildPurpose.Voice,
                OperatingRules: VoiceWorkflowInstructions), cancellationToken)).Assemble();
            chat = await chatClientFactory.CreateChatClientAsync(persistedProvider.Id, cancellationToken);
            toolContext = new VoiceContext(project.Id, currentSampleTitle, currentSampleBody, visionReady);
            aiTools = tools.Build(toolContext);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Voice turn setup failed for project {ProjectId}", projectId);
            await PersistFailedAssistantAsync(conversation.Id, nextOrder, ex.Message);
            setupError = ex.Message;
        }

        if (setupError is not null)
        {
            yield return new VoiceTurnError(setupError, Cancelled: false);
            yield break;
        }

        var chatOptions = new ChatOptions
        {
            Tools = aiTools,
            ToolMode = ChatToolMode.Auto,
        };

        var history = await turnEngine.ReadAsync(
            repositories => repositories.VoiceConversations,
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
            var replay = ChatModelHistory.Project(persistedMessage.Role.ToString(), persistedMessage.Content, persistedMessage.ResponseMetadataJson, persistedProvider);
            if (replay is not null) messages.Add(replay);
        }

        var maxIterations = Math.Max(1, options.Value.MaxToolIterations);
        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            var activeAssistant = new VoiceMessage
            {
                ConversationId = conversation.Id,
                Order = nextOrder++,
                Role = VoiceMessageRole.Assistant,
                Content = string.Empty,
                Status = VoiceMessageStatus.Pending,
            };
            await turnEngine.AddMessageAsync(repositories => repositories.VoiceConversations, activeAssistant, cancellationToken);

            if (turnEngine.TryCompactContext(messages, persistedProvider.ModelId, persistedProvider.EffectiveMaxInputTokens) is { } compaction)
            {
                yield return new VoiceContextTrimmed(compaction);
                if (compaction.LimitExceeded)
                {
                    activeAssistant.Status = VoiceMessageStatus.Failed;
                    activeAssistant.ErrorMessage = ChatContextCompaction.LimitExceededMessage;
                    await SafePersistAsync(activeAssistant);
                    yield return new VoiceTurnError(activeAssistant.ErrorMessage, Cancelled: false);
                    yield break;
                }
            }

            ChatRoundCompleted? completedRound = null;
            await foreach (var update in turnEngine.StreamRoundAsync(chat, messages, chatOptions, cancellationToken))
            {
                switch (update)
                {
                    case ChatRoundTextDelta text:
                        yield return new VoiceTextDelta(text.Text);
                        break;
                    case ChatRoundReasoningDelta reasoning:
                        yield return new VoiceReasoningDelta(reasoning.Text);
                        break;
                    case ChatRoundToolCallStarted started:
                        yield return new VoiceToolCallStarted(started.CallId, started.ToolName, started.ArgumentsJson, started.ArgumentsComplete);
                        break;
                    case ChatRoundToolCallArgumentsDelta delta:
                        yield return new VoiceToolCallArgumentsDelta(delta.CallId, delta.ArgumentsDelta, delta.ArgumentsComplete);
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
                            ? VoiceMessageStatus.Cancelled
                            : VoiceMessageStatus.Failed;
                        activeAssistant.ErrorMessage = failed.Cancelled ? "Cancelled by user." : failed.Message;
                        await SafePersistAsync(activeAssistant);
                        yield return new VoiceTurnError(failed.Message, failed.Cancelled);
                        yield break;
                }
            }

            if (completedRound is null)
            {
                activeAssistant.Status = VoiceMessageStatus.Failed;
                activeAssistant.ErrorMessage = "Voice streaming ended without a completed round.";
                await SafePersistAsync(activeAssistant);
                yield return new VoiceTurnError(activeAssistant.ErrorMessage, Cancelled: false);
                yield break;
            }

            var textBuilder = new StringBuilder(completedRound.Text);
            var pendingCalls = completedRound.ToolCalls;
            activeAssistant.Reasoning = completedRound.Reasoning;
            activeAssistant.ResponseMetadataJson = completedRound.Metadata.Serialize();

            if (pendingCalls.Count == 0)
            {
                activeAssistant.Content = textBuilder.ToString();
                activeAssistant.Status = VoiceMessageStatus.Completed;
                await SafePersistAsync(activeAssistant);
                yield return new VoiceAssistantMessageCompleted(activeAssistant.Id);
                yield break;
            }

            var manifest = pendingCalls
                .Select(ChatToolCallManifest.From)
                .ToList();
            activeAssistant.Content = textBuilder.ToString();
            activeAssistant.ToolCallsJson = JsonSerializer.Serialize(manifest);
            activeAssistant.Status = VoiceMessageStatus.Completed;
            await SafePersistAsync(activeAssistant);

            messages.Add(new ChatMessage(ChatRole.Assistant, ChatTurnEngine.BuildAssistantContents(textBuilder.ToString(), pendingCalls, completedRound.Reasoning, completedRound.Metadata)));

            var resultContents = new List<AIContent>();
            foreach (var pendingCall in pendingCalls)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    yield return new VoiceTurnError("Cancelled.", Cancelled: true);
                    yield break;
                }

                var sw = Stopwatch.StartNew();
                var toolOutcome = await turnEngine.InvokeToolAsync(aiTools, pendingCall, cancellationToken);
                sw.Stop();

                if (toolOutcome.Cancelled)
                {
                    yield return new VoiceTurnError("Cancelled.", Cancelled: true);
                    yield break;
                }

                var toolResult = toolOutcome.Result;
                var toolError = toolOutcome.Error;

                var toolMessage = new VoiceMessage
                {
                    ConversationId = conversation.Id,
                    Order = nextOrder++,
                    Role = VoiceMessageRole.Tool,
                    Content = toolResult ?? string.Empty,
                    ToolCallId = pendingCall.CallId,
                    ToolName = pendingCall.Name,
                    Status = toolError is null ? VoiceMessageStatus.Completed : VoiceMessageStatus.Failed,
                    ErrorMessage = toolError,
                };
                await turnEngine.AddMessageAsync(repositories => repositories.VoiceConversations, toolMessage, CancellationToken.None);

                resultContents.Add(new FunctionResultContent(pendingCall.CallId, toolResult ?? string.Empty));
                yield return new VoiceToolCallCompleted(
                    pendingCall.CallId,
                    pendingCall.Name,
                    toolError is null ? toolResult : null,
                    toolError,
                    sw.Elapsed.TotalMilliseconds);
                if (toolContext!.TakeMutation()) yield return new VoiceMutated();
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


            if (iteration == maxIterations - 1)
            {
                yield return new VoiceTurnError(
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
        var conversations = databaseOperation.Repositories.VoiceConversations;
        try
        {
            var message = new VoiceMessage
            {
                ConversationId = conversationId,
                Order = order,
                Role = VoiceMessageRole.Assistant,
                Status = VoiceMessageStatus.Failed,
                ErrorMessage = error,
            };
            await conversations.AddMessageAsync(message, CancellationToken.None);
            await databaseOperation.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist Voice setup failure");
        }
    }

    private async Task SafePersistAsync(VoiceMessage message)
    {
        try
        {
            await turnEngine.UpdateMessageAsync(repositories => repositories.VoiceConversations, message, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist Voice message {MessageId}", message.Id);
        }
    }

}
