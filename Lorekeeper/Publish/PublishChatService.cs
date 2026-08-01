using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Lorekeeper.ChatTurns;
using Lorekeeper.Context;
using Lorekeeper.Llm;
using Lorekeeper.Models;
using Lorekeeper.Persistence.Repositories;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Lorekeeper.Publish;

public interface IPublishChatService
{
    Task<PublishConversation> GetOrCreateAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PublishMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task<string> GetSystemPromptAsync(Guid projectId, Guid? selectedEditionId, CancellationToken cancellationToken = default);
    IAsyncEnumerable<PublishTurnUpdate> SendAsync(
        Guid projectId,
        Guid? selectedEditionId,
        string userText,
        IReadOnlyList<Guid> imageIds,
        CancellationToken cancellationToken = default);
    Task ResetAsync(Guid projectId, CancellationToken cancellationToken = default);
}

public sealed class PublishChatService(
    IProjectRepository projects,
    IPublishConversationRepository conversations,
    IChatImageAttachmentService imageAttachments,
    ILlmProviderService providers,
    IChatClientFactory clients,
    IContextBuilder contextBuilder,
    IPublishAssistantTools tools,
    IPublicationActorContext actorContext,
    ChatTurnRuntime turnRuntime,
    ChatTurnEngine turnEngine,
    IOptions<AgentOptions> options,
    ILogger<PublishChatService> logger) : IPublishChatService
{
    internal const string WorkflowInstructions = """
        You are Lorekeeper's conversational Publish assistant. You help the author make and carry out edition-production decisions through the supplied tools.

        Collaboration:
        - Begin by reading the currently selected edition when one is supplied. If no edition exists, help the user choose the first edition's format, vendor, and name, then create it only after those material choices are clear.
        - Ask focused questions only when format, vendor, audience, distribution, metadata, trim, typography, content, matter, imagery, or cover choices are materially undecided. Ask one small related group at a time instead of presenting a giant configuration checklist.
        - Explain meaningful tradeoffs in plain language and recommend a sensible default grounded in the Book Brief, Project Guidance, selected vendor, and intended readers.
        - Execute explicit instructions directly. For exploratory, underspecified, or consequential choices, discuss the options and obtain agreement before mutating.
        - Preserve unrelated settings. Never silently replace the user's established production choices.

        Tool and state integrity:
        - Use tools for every publication read or mutation. Copy stable IDs exactly and honor expected revisions.
        - Immediately before a mutation, make sure the relevant edition read is current. If a revision conflict occurs, reread, preserve the user's intent, and retry only when the intended change is still unambiguous.
        - Never claim a mutation, render, preflight, package, or export succeeded unless the tool result says it did.
        - Tool results are not replayed into later model turns. Summarize durable decisions, exact changes, important diagnostics, resulting revisions, and unresolved questions in the assistant response.
        - Generated files are saved only when the user opens a returned download URL. Never claim that you downloaded a file for them.
        - Archived editions are read-only. Recommend cloning when the user wants to change one.

        Publishing trust:
        - Clearly label current press output and packages as Preview. Do not claim PDF/X conformance, vendor acceptance, accessibility certification, or print readiness beyond the evidence returned by preflight.
        - You may run and explain preflight, request or cancel renders, build packages, and guide proof inspection. You cannot approve a digital or physical proof; only the user-facing proof controls may record that human attestation.
        - End each turn with a concise account of the exact settings or artifacts changed, remaining diagnostics, current render/package state, what remains blocked, and every user action still required.
        """;

    private const string InitialGreeting =
        "What are you publishing? I can help choose an edition, work through the production options with you, configure it, generate Preview files, and explain anything that still blocks export.";

    private static readonly HashSet<string> MutationTools =
    [
        "create_publication_edition",
        "clone_publication_edition",
        "update_publication_edition",
        "set_default_publication_edition",
        "archive_publication_edition",
        "set_publication_cover_image",
        "set_publication_content",
        "reorder_publication_content",
        "upsert_publication_matter",
        "delete_publication_matter",
        "upsert_publication_style_mapping",
        "delete_publication_style_mapping",
        "add_publication_image_placement",
        "update_publication_image_placement",
        "reorder_publication_image_placements",
        "delete_publication_image_placement",
        "request_publication_render",
        "cancel_publication_render",
        "update_publication_cover_design",
        "preflight_publication_edition",
        "build_publication_package",
    ];

    public async Task<PublishConversation> GetOrCreateAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var existing = await conversations.GetByProjectIdAsync(projectId, cancellationToken);
        if (existing is not null)
            return existing;

        _ = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");
        var conversation = new PublishConversation { ProjectId = projectId };
        await conversations.AddConversationAsync(conversation, cancellationToken);
        await conversations.AddMessageAsync(new PublishMessage
        {
            ConversationId = conversation.Id,
            Order = 0,
            Role = PublishMessageRole.Assistant,
            Content = InitialGreeting,
        }, cancellationToken);
        await conversations.SaveChangesAsync(cancellationToken);
        return conversation;
    }

    public async Task<IReadOnlyList<PublishMessage>> LoadMessagesAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default) =>
        await conversations.LoadMessagesAsync(conversationId, cancellationToken);

    public async Task<string> GetSystemPromptAsync(
        Guid projectId,
        Guid? selectedEditionId,
        CancellationToken cancellationToken = default)
    {
        var project = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");
        var selection = selectedEditionId is { } editionId
            ? $"The Publish workspace currently has edition {editionId:D} selected. Treat it as the default target, but verify it with tools before relying on its state."
            : "The Publish workspace has no selected edition. List editions before assuming whether one exists.";
        var assembly = await contextBuilder.BuildAsync(
            new ContextBuildRequest(
                project,
                Purpose: ContextBuildPurpose.Publish,
                OperatingRules: WorkflowInstructions
                    + "\n\n" + selection
                    + "\n\n" + AssistantWorkflowInstructions.NonReplayedToolHistory),
            cancellationToken);
        return assembly.Assemble();
    }

    public async Task ResetAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        using var maintenance = turnRuntime.TryBeginMaintenance(
            new ChatTurnKey(projectId, ChatTurnSurface.Publish));
        if (maintenance is null)
            throw new InvalidOperationException("Publish Assistant is still working in another window. Stop or wait for that turn before resetting the conversation.");
        await imageAttachments.ClearSurfaceAsync(projectId, ChatTurnSurface.Publish, cancellationToken);
        var existing = await conversations.GetByProjectIdAsync(projectId, cancellationToken);
        if (existing is null)
            return;
        conversations.RemoveConversation(existing);
        await conversations.SaveChangesAsync(cancellationToken);
    }

    public async IAsyncEnumerable<PublishTurnUpdate> SendAsync(
        Guid projectId,
        Guid? selectedEditionId,
        string userText,
        IReadOnlyList<Guid> imageIds,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userText))
            throw new ArgumentException("Message cannot be empty.", nameof(userText));

        var conversation = await GetOrCreateAsync(projectId, cancellationToken);
        var availability = await providers.GetDefaultChatProviderAvailabilityAsync(cancellationToken);
        if (!availability.IsAvailable || availability.Provider is null)
        {
            yield return new PublishTurnError(availability.Message, Cancelled: false);
            yield break;
        }

        var visionReady = await providers.IsVisionProviderWorkingAsync(availability.Provider.Id, cancellationToken);
        if (imageIds.Count > 0 && !visionReady)
        {
            yield return new PublishTurnError(
                "The active chat provider has not passed Test Vision. Run Test Vision in Settings > Providers before sending images.",
                Cancelled: false);
            yield break;
        }
        await imageAttachments.ResolveAsync(projectId, imageIds, cancellationToken);

        var nextOrder = await conversations.GetMaxOrderAsync(conversation.Id, cancellationToken) + 1;
        var userMessage = new PublishMessage
        {
            ConversationId = conversation.Id,
            Order = nextOrder++,
            Role = PublishMessageRole.User,
            Content = userText.Trim(),
        };
        conversation.UpdatedAt = DateTime.UtcNow;
        await turnEngine.AddMessageAsync(conversations, userMessage, cancellationToken);
        await imageAttachments.PersistAsync(
            projectId,
            ChatTurnSurface.Publish,
            userMessage.Id,
            imageIds,
            cancellationToken);

        IChatClient? chat = null;
        IList<AITool>? aiTools = null;
        string? systemPrompt = null;
        Exception? setupException = null;
        var setupCancelled = false;
        try
        {
            chat = await clients.CreateChatClientAsync(availability.Provider.Id, cancellationToken);
            aiTools = await tools.BuildAsync(new PublishAssistantContext(projectId), cancellationToken);
            systemPrompt = await GetSystemPromptAsync(projectId, selectedEditionId, cancellationToken);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            setupException = exception;
            setupCancelled = true;
            await PersistTerminalAssistantAsync(
                conversation.Id,
                nextOrder,
                PublishMessageStatus.Cancelled,
                "Cancelled by user.");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Publish chat setup failed for project {ProjectId}", projectId);
            await PersistTerminalAssistantAsync(
                conversation.Id,
                nextOrder,
                PublishMessageStatus.Failed,
                exception.Message);
            setupException = exception;
        }
        if (setupException is not null)
        {
            yield return new PublishTurnError(
                setupCancelled ? "Cancelled." : setupException.Message,
                setupCancelled);
            yield break;
        }
        var readyChat = chat!;
        var readyTools = aiTools!;

        var messages = new List<ChatMessage> { new(ChatRole.System, systemPrompt!) };
        foreach (var persisted in await conversations.LoadMessagesAsync(conversation.Id, cancellationToken))
        {
            if (persisted.Id == userMessage.Id && imageIds.Count > 0)
            {
                messages.Add(await imageAttachments.BuildUserMessageAsync(
                    projectId,
                    persisted.Content,
                    imageIds,
                    cancellationToken: cancellationToken));
                continue;
            }
            var replay = ChatModelHistory.Project(persisted.Role.ToString(), persisted.Content);
            if (replay is not null)
                messages.Add(replay);
        }

        var chatOptions = new ChatOptions { Tools = readyTools, ToolMode = ChatToolMode.Auto };
        var priorActor = actorContext.Actor;
        actorContext.Actor = "assistant";
        try
        {
            var maxIterations = Math.Max(1, options.Value.MaxToolIterations);
            for (var iteration = 0; iteration < maxIterations; iteration++)
            {
                var activeAssistant = new PublishMessage
                {
                    ConversationId = conversation.Id,
                    Order = nextOrder++,
                    Role = PublishMessageRole.Assistant,
                    Status = PublishMessageStatus.Pending,
                };
                await turnEngine.AddMessageAsync(conversations, activeAssistant, cancellationToken);

                ChatRoundCompleted? completedRound = null;
                await foreach (var update in turnEngine.StreamRoundAsync(readyChat, messages, chatOptions, cancellationToken))
                {
                    switch (update)
                    {
                        case ChatRoundTextDelta text:
                            yield return new PublishTextDelta(text.Text);
                            break;
                        case ChatRoundToolCallStarted started:
                            yield return new PublishToolCallStarted(
                                started.CallId,
                                started.ToolName,
                                started.ArgumentsJson,
                                started.ArgumentsComplete);
                            break;
                        case ChatRoundToolCallArgumentsDelta arguments:
                            yield return new PublishToolCallArgumentsDelta(
                                arguments.CallId,
                                arguments.ArgumentsDelta,
                                arguments.ArgumentsComplete);
                            break;
                        case ChatRoundCompleted completed:
                            completedRound = completed;
                            break;
                        case ChatRoundFailed failed:
                            activeAssistant.Content = failed.Text;
                            activeAssistant.Status = failed.Cancelled
                                ? PublishMessageStatus.Cancelled
                                : PublishMessageStatus.Failed;
                            activeAssistant.ErrorMessage = failed.Cancelled ? "Cancelled by user." : failed.Message;
                            await SafePersistAsync(activeAssistant);
                            yield return new PublishTurnError(failed.Message, failed.Cancelled);
                            yield break;
                    }
                }

                if (completedRound is null)
                {
                    activeAssistant.Status = PublishMessageStatus.Failed;
                    activeAssistant.ErrorMessage = "Publish chat streaming ended without a completed round.";
                    await SafePersistAsync(activeAssistant);
                    yield return new PublishTurnError(activeAssistant.ErrorMessage, Cancelled: false);
                    yield break;
                }

                if (completedRound.ToolCalls.Count == 0)
                {
                    activeAssistant.Content = completedRound.Text;
                    activeAssistant.Status = PublishMessageStatus.Completed;
                    await SafePersistAsync(activeAssistant);
                    conversation.UpdatedAt = DateTime.UtcNow;
                    await conversations.SaveChangesAsync(CancellationToken.None);
                    yield return new PublishAssistantMessageCompleted(activeAssistant.Id);
                    yield break;
                }

                activeAssistant.Content = completedRound.Text;
                activeAssistant.ToolCallsJson = JsonSerializer.Serialize(completedRound.ToolCalls.Select(call =>
                    new ChatToolCallManifest(call.CallId, call.Name, call.ArgumentsJson, call.TextOffset)));
                activeAssistant.Status = PublishMessageStatus.Completed;
                await SafePersistAsync(activeAssistant);
                messages.Add(new ChatMessage(
                    ChatRole.Assistant,
                    ChatTurnEngine.BuildAssistantContents(completedRound.Text, completedRound.ToolCalls)));

                var resultContents = new List<AIContent>();
                foreach (var pendingCall in completedRound.ToolCalls)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        activeAssistant.Status = PublishMessageStatus.Cancelled;
                        activeAssistant.ErrorMessage = "Cancelled by user.";
                        await SafePersistAsync(activeAssistant);
                        yield return new PublishTurnError("Cancelled.", Cancelled: true);
                        yield break;
                    }

                    var stopwatch = Stopwatch.StartNew();
                    var outcome = await turnEngine.InvokeToolAsync(readyTools, pendingCall, cancellationToken);
                    stopwatch.Stop();
                    if (outcome.Cancelled)
                    {
                        activeAssistant.Status = PublishMessageStatus.Cancelled;
                        activeAssistant.ErrorMessage = "Cancelled by user.";
                        await SafePersistAsync(activeAssistant);
                        yield return new PublishTurnError("Cancelled.", Cancelled: true);
                        yield break;
                    }

                    var toolMessage = new PublishMessage
                    {
                        ConversationId = conversation.Id,
                        Order = nextOrder++,
                        Role = PublishMessageRole.Tool,
                        Content = outcome.Result,
                        ToolCallId = pendingCall.CallId,
                        ToolName = pendingCall.Name,
                        Status = outcome.Error is null ? PublishMessageStatus.Completed : PublishMessageStatus.Failed,
                        ErrorMessage = outcome.Error,
                    };
                    await turnEngine.AddMessageAsync(conversations, toolMessage, CancellationToken.None);
                    resultContents.Add(new FunctionResultContent(pendingCall.CallId, outcome.Result));
                    yield return new PublishToolCallCompleted(
                        pendingCall.CallId,
                        pendingCall.Name,
                        outcome.Error is null ? outcome.Result : null,
                        outcome.Error,
                        stopwatch.Elapsed.TotalMilliseconds);

                    if (outcome.Error is null
                        && TryMutationNotice(pendingCall.Name, pendingCall.ArgumentsJson, outcome.Result) is { } mutation)
                    {
                        yield return mutation;
                    }
                }
                messages.Add(new ChatMessage(ChatRole.Tool, resultContents));

                if (iteration == maxIterations - 1)
                {
                    activeAssistant.Status = PublishMessageStatus.Failed;
                    activeAssistant.ErrorMessage = ChatTurnEngine.ToolLoopLimitError(maxIterations);
                    await SafePersistAsync(activeAssistant);
                    yield return new PublishTurnError(
                        activeAssistant.ErrorMessage,
                        Cancelled: false);
                    yield break;
                }
            }
        }
        finally
        {
            actorContext.Actor = priorActor;
        }
    }

    internal static PublishWorkspaceMutated? TryMutationNotice(
        string toolName,
        string argumentsJson,
        string resultJson)
    {
        if (!MutationTools.Contains(toolName))
            return null;

        var selectEdition = toolName is "create_publication_edition" or "clone_publication_edition";
        var kind = toolName switch
        {
            "request_publication_render" or "cancel_publication_render" => PublishWorkspaceMutationKind.Render,
            "preflight_publication_edition" => PublishWorkspaceMutationKind.Preflight,
            "build_publication_package" => PublishWorkspaceMutationKind.Package,
            _ => PublishWorkspaceMutationKind.Edition,
        };
        var editionId = selectEdition
            ? ReadGuid(resultJson, "id")
            : ReadGuid(argumentsJson, "editionId");
        return editionId is { } id ? new PublishWorkspaceMutated(id, selectEdition, kind) : null;
    }

    private static Guid? ReadGuid(string json, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String
                    && Guid.TryParse(property.Value.GetString(), out var value))
                {
                    return value;
                }
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private async Task PersistTerminalAssistantAsync(
        Guid conversationId,
        int order,
        PublishMessageStatus status,
        string error)
    {
        await conversations.AddMessageAsync(new PublishMessage
        {
            ConversationId = conversationId,
            Order = order,
            Role = PublishMessageRole.Assistant,
            Status = status,
            ErrorMessage = error,
        }, CancellationToken.None);
        await conversations.SaveChangesAsync(CancellationToken.None);
    }

    private async Task SafePersistAsync(PublishMessage message)
    {
        try
        {
            await turnEngine.UpdateMessageAsync(conversations, message, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to persist Publish chat message {MessageId}", message.Id);
        }
    }
}
