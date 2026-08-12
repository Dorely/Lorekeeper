using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Lorekeeper.ChatTurns;
using Lorekeeper.Context;
using Lorekeeper.EntityVisuals;
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
    ILogger<PublishChatService> logger,
    IEntityVisualContextService? entityVisualContext = null) : IPublishChatService
{
    internal const string WorkflowInstructions = """
        You are Lorekeeper's conversational Publish assistant. You maintain Core Book and prepare optional publication releases through the supplied tools.

        Current model:
        - Core Book is always present. It owns shared metadata, chapter inclusion, publication sections, project page setup, baseline body typography, Book Text Styles, and the reusable front cover. Chapter order always comes from the Core outline and cannot be rearranged here.
        - Paperback, EPUB ebook, and PDF ebook releases are optional products. They inherit Core Book live until a field or section is explicitly customized. ISBN is always release-specific.
        - Core Book can produce a tagged private reading PDF. It is not a publication product and has no ISBN, destination, package, or vendor-conformance claim.
        - Core Book owns PDF presentation defaults. Preserve Designed Page sizes only when the user wants full-art spreads to remain single wide PDF pages or custom Designed Pages to retain independent geometry. PDF ebook releases inherit this choice unless explicitly overridden.
        - Paperback owns destination, paper and ink, ISBN/barcode, full-wrap additions, and print PDFs. Lorekeeper manages vendor profiles and required cover bleed. EPUB uses reflow/navigation settings. PDF ebook uses page-geometry settings. Never apply controls from one product type to another.
        - A release may opt into edition-specific manuscript content. Publish can enable it, summarize differences and compatibility diagnostics, and direct the user to the target-aware Editor. Only Editor may mutate chapter manuscript text, Figures, styles in use, or chapter Designed Page layouts. Publication-section content and page layouts are edited here in Publish.
        - Profile versions, standards identifiers, bleed rules, and package internals are application-managed. Do not ask the user to choose them.

        Behavior:
        - Execute explicit instructions directly.
        - When no release is selected, read and work against Core Book. When a release is selected, read its effective values and override markers.
        - Page size, margins, baseline body typography, and Book Text Styles are editable Core Book settings. Read their current revisions before changing them; publication releases inherit them unless explicitly customized.
        - Execute direct requests proactively with safe, reversible defaults. Ask only for a genuinely material unknown such as author identity, paperback destination, an ISBN the user must supply, or ambiguous black-and-white versus color cost.
        - Recommend defaults from the Book Brief, Project Guidance, manuscript visuals, readers, and destination. Do not dump a production checklist.
        - Preserve unrelated values. Customize a release only where it differs; use ResetFields to restore live Core inheritance.
        - Create no release or ISBN unless requested. Never invent an ISBN.
        - Title and copyright are system Designed Pages: their linked copy resolves live from Core or effective release metadata while their placement and typography are edited on the page canvas. Contents is generated automatically from the effective book structure. Each other publication section has one content mode: either prose with optional flowing Figures, or Designed Page canvases. Use separate sections when both forms are needed. Sections may be placed at the front, back, or immediately before or after an act or chapter.
        - Create user-authored material such as Dedication, Epigraph, Acknowledgments, About the Author, Also By, References, image pages, or arbitrary production pages with publication-section tools. Release sections inherit Core live until customized; do not create duplicate release content when inheritance is sufficient.
        - While designing a publication-section page, read its selected variant, preview it in annotated mode, make focused changes or stage one large semantic-and-scene update, preview the result again, and finish with a clean preview. Customize an inherited release section before changing its page.

        Tool and state integrity:
        - Use tools for every publication read or mutation and honor Core or release revisions.
        - The complete ordered outline, synopses, beats, Project Guidance, Book Brief, and project facts are supplied every turn. Use list_search_sources, search_project, and paginated read_project_source for chapter bodies, research, sources, entities, facts, or other project details that are not already present. Use list_project_images and read_project_image for reusable visual assets.
        - Immediately before mutation, reread its target. After a conflict, perform one compact reread and retry only when intent remains unambiguous.
        - Use prepare_publication_files for compile, render or export, validation, and packaging. Do not attempt separate low-level orchestration.
        - Never claim a mutation, preparation, validation, package, or export succeeded unless the tool result says so.
        - Tool results are not replayed into later model turns. Use visible prose as the durable work log: narrate each meaningful publication phase and its reason, record consequential results before moving on, and end with durable decisions, exact changes, revisions, diagnostics, and unresolved questions without repeating large payloads.
        - Returned URLs require a user action. Never claim that you downloaded a file.
        - Chapters may combine semantic text, flowing Figures, and Designed Pages. A publication section is intentionally either prose with optional Figures or a designed-page canvas section. Covers remain separate front-cover or full-wrap compositions.
        - Use project page setup for authoring decisions and release geometry only for compatibility and covers. Optional generation geometry derives dimensions but never places the result; ordinary source-image shapes remain valid and are fitted non-destructively.
        - generate_project_image and edit_project_image wait for completion and always return unattached project images. Inspect the visible output, then use its ID with the focused Core/release cover tool or publication-section page tool in this same turn. Chapter manuscript and chapter Designed Page placement belongs to Editor. Never imply geometry guidance attached an image.
        - Cover artwork always remains beneath canonical title, subtitle, author, spine, and back-cover copy. Adjust the artwork crop, opacity, and framing instead of trying to raise it above cover text.
        - For existing cover design work, call preview_publication_cover_canvas in annotated mode before mutating. After placing or arranging artwork, inspect another annotated whole-cover preview and correct clipping, hierarchy, protected regions, copy legibility, and collisions. Call the clean mode before reporting completion. You may skip only the initial preview for a genuinely empty cover.
        - Cover generation targets provide an exact moderate-resolution requested raster matching the selected cover surface or frame. If the provider returns different dimensions, the project image remains usable but geometryMatched is false and the mismatch is a warning. Inspect it and deliberately regenerate or fit it; never report it as exact-geometry output.
        - A cover-canvas preview is transient visual context, not an image-library asset. If visual delivery is unavailable, report that you could not visually verify the cover instead of inferring appearance from scene JSON.
        - Require alt text or an explicit decorative decision for publication releases and preserve logical reading order. A Core reading PDF may complete with unresolved image accessibility decisions as explicit warnings; report those warnings and do not describe the copy as publication-ready.
        - Submit large cover or publication-section page payloads once to staging, then apply only the stage ID and expected revision.
        - Release format is fixed. Create another release for another product type.

        Publishing trust:
        - Distinguish Core reading-copy validation from publication-release validation. Never describe a Core reading PDF as vendor-ready or published.
        - You may prepare files and explain validation. Never claim vendor acceptance.
        - End with exact mutations, inheritance or override state, current preparation state, blockers, download actions, and remaining user actions.
        """;

    private const string InitialGreeting =
        "I can help finish the shared Core Book, add a Paperback or ebook release when you need one, and prepare the right files without making you manage production internals.";

    private static readonly HashSet<string> MutationTools =
    [
        "patch_publication_book",
        "patch_publication_book_page_setup",
        "upsert_publication_book_text_style",
        "delete_publication_book_text_style",
        "patch_publication_book_content",
        "upsert_publication_section",
        "patch_publication_section_manuscript",
        "reorder_publication_sections",
        "create_publication_section_designed_page",
        "remove_publication_section",
        "patch_publication_section_page_element",
        "add_project_image_to_publication_section_page",
        "apply_publication_section_page_workspace_stage",
        "create_publication_release",
        "patch_publication_release_overrides",
        "set_edition_specific_content",
        "patch_publication_core_cover_element",
        "place_project_image_on_core_cover",
        "add_project_image_to_core_cover",
        "apply_publication_core_cover_composition_stage",
        "customize_publication_release_cover",
        "use_core_publication_cover",
        "prepare_publication_files",
        "cancel_publication_preparation",
        "patch_publication_release_content",
        "update_publication_cover_design",
        "apply_publication_cover_composition_stage",
        "patch_publication_cover_element",
        "place_project_image_on_release_cover",
        "add_project_image_to_release_cover",
        "generate_project_image",
        "edit_project_image",
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
                    + "\n\n" + AssistantWorkflowInstructions.NonReplayedToolHistory
                    + "\n\n" + AssistantWorkflowInstructions.BookDesignCraft
                    + "\n\n" + AssistantWorkflowInstructions.PublicationDesign),
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
        PublishAssistantContext? assistantContext = null;
        string? systemPrompt = null;
        Exception? setupException = null;
        var setupCancelled = false;
        try
        {
            chat = await clients.CreateChatClientAsync(availability.Provider.Id, cancellationToken);
            assistantContext = new PublishAssistantContext(projectId, conversation.Id, cancellationToken);
            aiTools = await tools.BuildAsync(assistantContext, cancellationToken);
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
                var manifest = completedRound.ToolCalls
                    .Select(ChatToolCallManifest.From)
                    .ToList();
                activeAssistant.ToolCallsJson = JsonSerializer.Serialize(manifest);
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
                if (assistantContext is not null)
                {
                    var visuals = assistantContext.DrainVisuals();
                    if (entityVisualContext is not null)
                    {
                        var visualMessage = await entityVisualContext.BuildVisionMessageAsync(
                            projectId,
                            visuals,
                            visionReady,
                            "Project-image outputs from the preceding tools. Inspect the visible result before choosing an image ID for a separate cover tool. Publication-section Figures and Designed Pages are edited in the section workspace.",
                            cancellationToken);
                        if (visualMessage is not null)
                            messages.Add(ChatTurnEngine.MarkToolContextMessage(visualMessage));
                    }
                    var transientVisuals = assistantContext.DrainTransientVisuals();
                    if (visionReady && transientVisuals.Count > 0)
                    {
                        var contents = new List<AIContent>
                        {
                            new TextContent("Direct cover-canvas previews from the preceding tool. Inspect the complete surface before choosing further cover mutations."),
                        };
                        foreach (var visual in transientVisuals)
                        {
                            contents.Add(new TextContent($"\n{visual.Caption}; visualId={visual.Id:N}; file={visual.FileName}"));
                            contents.Add(new DataContent(visual.Data, visual.ContentType) { Name = visual.FileName });
                        }
                        messages.Add(ChatTurnEngine.MarkToolContextMessage(new ChatMessage(ChatRole.User, contents)));
                    }
                }

                if (turnEngine.TryCompactContext(messages, availability.Provider.ModelId) is { } compaction)
                {
                    manifest.Add(new ChatToolCallManifest(
                        compaction.CallId,
                        ChatTurnEngine.CompactionToolName,
                        ChatContextCompaction.EmptyArgumentsJson));
                    activeAssistant.ToolCallsJson = JsonSerializer.Serialize(manifest);
                    await SafePersistAsync(activeAssistant);

                    await turnEngine.AddMessageAsync(conversations, new PublishMessage
                    {
                        ConversationId = conversation.Id,
                        Order = nextOrder++,
                        Role = PublishMessageRole.Tool,
                        Content = ChatTurnEngine.CompactionNotice,
                        ToolCallId = compaction.CallId,
                        ToolName = ChatTurnEngine.CompactionToolName,
                        Status = PublishMessageStatus.Completed,
                    }, CancellationToken.None);
                    yield return new PublishToolCallStarted(
                        compaction.CallId,
                        ChatTurnEngine.CompactionToolName,
                        ChatContextCompaction.EmptyArgumentsJson,
                        ArgumentsComplete: true);
                    yield return new PublishToolCallCompleted(
                        compaction.CallId,
                        ChatTurnEngine.CompactionToolName,
                        ChatTurnEngine.CompactionNotice,
                        Error: null,
                        DurationMs: 0);
                }

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

        if (toolName is "generate_project_image" or "edit_project_image")
            return new PublishWorkspaceMutated(null, false, PublishWorkspaceMutationKind.ImageLibrary);
        if (toolName is "prepare_publication_files" or "cancel_publication_preparation")
        {
            var preparationReleaseId = ReadGuid(resultJson, "releaseId")
                ?? ReadGuid(argumentsJson, "releaseId");
            return new PublishWorkspaceMutated(
                preparationReleaseId,
                false,
                PublishWorkspaceMutationKind.Package);
        }
        if (toolName is "upsert_publication_section"
            or "patch_publication_section_manuscript"
            or "reorder_publication_sections"
            or "create_publication_section_designed_page"
            or "remove_publication_section")
        {
            var sectionReleaseId = ReadGuid(resultJson, "releaseId") ?? ReadGuid(argumentsJson, "releaseId");
            return new PublishWorkspaceMutated(
                sectionReleaseId,
                false,
                PublishWorkspaceMutationKind.Edition,
                null,
                toolName == "remove_publication_section" ? null : ReadGuid(resultJson, "sectionId") ?? ReadGuid(resultJson, "targetId"),
                toolName == "create_publication_section_designed_page"
                    ? ReadGuid(resultJson, "compositionId") ?? ReadGuid(resultJson, "targetId")
                    : null);
        }
        if (toolName is "patch_publication_section_page_element"
            or "add_project_image_to_publication_section_page"
            or "apply_publication_section_page_workspace_stage")
        {
            var pageReleaseId = ReadGuid(resultJson, "releaseId") ?? ReadGuid(argumentsJson, "releaseId");
            return new PublishWorkspaceMutated(
                pageReleaseId,
                false,
                PublishWorkspaceMutationKind.Edition,
                ReadGuid(resultJson, "selectId") ?? ReadGuid(argumentsJson, "targetId"),
                ReadGuid(resultJson, "sectionId"),
                ReadGuid(resultJson, "compositionId") ?? ReadGuid(argumentsJson, "compositionId"));
        }
        if (toolName is "patch_publication_book" or "patch_publication_book_page_setup"
            or "upsert_publication_book_text_style" or "delete_publication_book_text_style"
            or "patch_publication_book_content"
            or "patch_publication_core_cover_element" or "place_project_image_on_core_cover" or "add_project_image_to_core_cover"
            or "apply_publication_core_cover_composition_stage")
            return new PublishWorkspaceMutated(
                null,
                false,
                PublishWorkspaceMutationKind.Edition,
                toolName is "patch_publication_core_cover_element" or "place_project_image_on_core_cover"
                    ? ReadGuid(argumentsJson, "targetId")
                    : toolName == "add_project_image_to_core_cover"
                        ? ReadGuid(resultJson, "selectId")
                    : null);

        var selectEdition = toolName is "create_publication_release" or "customize_publication_release_cover" or "use_core_publication_cover";
        var editionId = selectEdition
            ? ReadGuid(resultJson, "id") ?? ReadGuid(resultJson, "targetId")
            : ReadGuid(argumentsJson, "releaseId") ?? ReadGuid(argumentsJson, "editionId")
                ?? (toolName == "apply_publication_cover_composition_stage"
                    ? ReadGuid(resultJson, "targetId")
                    : null);
        var selectedObjectId = toolName is "patch_publication_cover_element" or "place_project_image_on_release_cover"
            ? ReadGuid(argumentsJson, "targetId")
            : toolName == "add_project_image_to_release_cover"
                ? ReadGuid(resultJson, "selectId")
                : null;
        return editionId is { } id
            ? new PublishWorkspaceMutated(id, selectEdition, PublishWorkspaceMutationKind.Edition, selectedObjectId)
            : null;
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
