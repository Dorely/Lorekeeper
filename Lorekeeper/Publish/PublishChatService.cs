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
        You are Lorekeeper's conversational Publish assistant. You maintain Core Book and prepare optional publication releases through the supplied tools.

        Current model:
        - Core Book is always present. It owns shared metadata, content order and inclusion, matter, design defaults, opening and ending images, and the reusable front cover.
        - Paperback, EPUB ebook, and PDF ebook releases are optional products. They inherit Core Book live until a field or section is explicitly customized. ISBN is always release-specific.
        - Core Book can produce a tagged private reading PDF. It is not a publication product and has no ISBN, destination, package, vendor-conformance, or proof claim.
        - Paperback owns destination, paper and ink, ISBN/barcode, full-wrap additions, print PDFs, and physical proof. Lorekeeper manages vendor profiles and required cover bleed. EPUB uses reflow/navigation settings. PDF ebook uses page-geometry settings. Never apply controls from one product type to another.
        - Profile versions, standards identifiers, bleed rules, and package internals are application-managed. Do not ask the user to choose them.

        Behavior:
        - Execute explicit instructions directly.
        - When no release is selected, read and work against Core Book. When a release is selected, read its effective values and override markers.
        - Execute direct requests proactively with safe, reversible defaults. Ask only for a genuinely material unknown such as author identity, paperback destination, an ISBN the user must supply, or ambiguous black-and-white versus color cost.
        - Recommend defaults from the Book Brief, Project Guidance, manuscript visuals, readers, and destination. Do not dump a production checklist.
        - Preserve unrelated values. Customize a release only where it differs; use ResetFields to restore live Core inheritance.
        - Create no release or ISBN unless requested. Never invent an ISBN.

        Tool and state integrity:
        - Use tools for every publication read or mutation and honor Core or release revisions.
        - Immediately before mutation, reread its target. After a conflict, perform one compact reread and retry only when intent remains unambiguous.
        - Use prepare_publication_files for compile, render or export, validation, and packaging. Do not attempt separate low-level orchestration.
        - Never claim a mutation, preparation, validation, package, or export succeeded unless the tool result says so.
        - Tool results are not replayed into later model turns. Summarize durable decisions, exact changes, revisions, diagnostics, and unresolved questions without repeating large payloads.
        - Returned URLs require a user action. Never claim that you downloaded a file.
        - Chapters contain semantic text, flowing Figures, and Designed Pages. Distinguish those from release-only placements, print full-wrap covers, and digital front covers.
        - Use project page setup for authoring decisions and release geometry only for compatibility and covers. Target-bound generation derives dimensions; ordinary source-image shapes remain valid and are fitted non-destructively.
        - Require alt text or an explicit decorative decision and preserve logical reading order.
        - Submit large composition payloads once to staging, then apply only the stage ID and expected revision.
        - Release format is fixed. Create another release for another product type.

        Publishing trust:
        - Distinguish Core reading-copy validation from publication-release validation. Never describe a Core reading PDF as vendor-ready or published.
        - You may prepare files, explain validation, and guide proof inspection. You cannot approve a digital or physical proof or claim vendor acceptance.
        - End with exact mutations, inheritance or override state, current preparation state, blockers, download actions, and remaining user actions.
        """;

    private const string InitialGreeting =
        "I can help finish the shared Core Book, add a Paperback or ebook release when you need one, and prepare the right files without making you manage production internals.";

    private static readonly HashSet<string> MutationTools =
    [
        "patch_publication_book",
        "patch_publication_book_content",
        "upsert_publication_book_matter",
        "delete_publication_book_matter",
        "add_publication_book_placement",
        "update_publication_book_placement",
        "reorder_publication_book_placements",
        "delete_publication_book_placement",
        "create_publication_release",
        "patch_publication_release_overrides",
        "patch_publication_core_cover_element",
        "apply_publication_core_cover_composition_stage",
        "customize_publication_release_cover",
        "use_core_publication_cover",
        "prepare_publication_files",
        "cancel_publication_preparation",
        "patch_publication_release_content",
        "reorder_publication_release_content",
        "upsert_publication_release_matter",
        "delete_publication_release_matter",
        "upsert_publication_release_style_override",
        "delete_publication_release_style_override",
        "add_publication_release_placement",
        "update_publication_release_placement",
        "reorder_publication_release_placements",
        "delete_publication_release_placement",
        "update_publication_cover_design",
        "get_or_create_publication_composition_variant",
        "patch_publication_composition_element",
        "apply_publication_cover_composition_stage",
        "patch_publication_cover_element",
        "apply_publication_composition_stage",
        "apply_publication_composition_semantic_stage",
        "apply_publication_composition_workspace_stage",
        "generate_publication_layout_image",
        "generate_publication_image",
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
            ? $"The Publish workspace currently has publication release {editionId:D} selected. Read that release before acting."
            : "The Publish workspace currently targets Core Book. Read Core Book before acting; do not assume a publication release is required.";
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
            aiTools = await tools.BuildAsync(new PublishAssistantContext(projectId, conversation.Id, cancellationToken), cancellationToken);
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
                activeAssistant.ToolCallsJson = JsonSerializer.Serialize(
                    completedRound.ToolCalls.Select(ChatToolCallManifest.From));
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
        if (!ResultSucceeded(resultJson))
            return null;

        if (toolName == "generate_publication_image")
            return new PublishWorkspaceMutated(null, false, PublishWorkspaceMutationKind.ImageLibrary);
        if (toolName is "patch_publication_book" or "patch_publication_book_content"
            or "upsert_publication_book_matter" or "delete_publication_book_matter"
            or "add_publication_book_placement" or "update_publication_book_placement"
            or "reorder_publication_book_placements" or "delete_publication_book_placement"
            or "patch_publication_core_cover_element" or "apply_publication_core_cover_composition_stage")
            return new PublishWorkspaceMutated(null, false, PublishWorkspaceMutationKind.Edition);

        var selectEdition = toolName is "create_publication_release" or "customize_publication_release_cover" or "use_core_publication_cover";
        var kind = toolName switch
        {
            "prepare_publication_files" or "cancel_publication_preparation" => PublishWorkspaceMutationKind.Package,
            _ => PublishWorkspaceMutationKind.Edition,
        };
        var editionId = selectEdition
            ? ReadGuid(resultJson, "id") ?? ReadGuid(resultJson, "targetId")
            : ReadGuid(argumentsJson, "releaseId") ?? ReadGuid(argumentsJson, "editionId")
                ?? (toolName == "apply_publication_cover_composition_stage"
                    ? ReadGuid(resultJson, "targetId")
                    : null);
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

    private static bool ResultSucceeded(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return !document.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.False;
        }
        catch (JsonException)
        {
            return false;
        }
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
