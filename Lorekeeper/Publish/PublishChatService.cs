using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Lorekeeper.Authoring;
using Lorekeeper.ChatTurns;
using Lorekeeper.Composition;
using Lorekeeper.Context;
using Lorekeeper.EntityVisuals;
using Lorekeeper.Llm;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Persistence.Repositories;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Lorekeeper.Publish;

public interface IPublishChatService
{
    Task<PublishConversation> GetOrCreateAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PublishMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task<ChatProviderAvailability> GetChatProviderAvailabilityAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task SetSelectedProviderAsync(Guid projectId, int? providerId, CancellationToken cancellationToken = default);
    Task<string> GetSystemPromptAsync(Guid projectId, Guid? selectedEditionId, PublishAssistantWorkspaceContext? workspaceContext = null, CancellationToken cancellationToken = default);
    IAsyncEnumerable<PublishTurnUpdate> SendAsync(
        Guid projectId,
        Guid? selectedEditionId,
        PublishAssistantWorkspaceContext? workspaceContext,
        string userText,
        IReadOnlyList<Guid> imageIds,
        int providerId,
        CancellationToken cancellationToken = default);
    Task ResetAsync(Guid projectId, CancellationToken cancellationToken = default);
}

public sealed class PublishChatService(
IAppDatabaseOperationFactory database, IChatImageAttachmentService imageAttachments, ILlmProviderService providers, IChatClientFactory clients, IContextBuilder contextBuilder, IPublishAssistantTools tools, IPublicationActorContext actorContext, ChatTurnRuntime turnRuntime, ChatTurnEngine turnEngine, IAuthoringMutationContextAccessor authoringMutationContext, IOptions<AgentOptions> options, ILogger<PublishChatService> logger, IEntityVisualContextService? entityVisualContext = null, ICompositionService? compositions = null) : IPublishChatService
{
    internal const string WorkflowInstructions = """
        You are Lorekeeper's conversational Publish assistant. You maintain Core Book and prepare optional publication releases through the supplied tools.

        Current model:
        - Core Book is always present. It owns shared metadata, chapter inclusion, publication sections, project page setup, baseline body typography, Book Text Styles, and the reusable front cover. Chapter order always comes from the Core outline and cannot be rearranged here.
        - Paperback, hardcover, EPUB ebook, and PDF ebook releases are optional products. They inherit Core Book live until a field or section is explicitly customized. ISBN is always release-specific.
        - Core Book can produce a tagged private reading PDF. It is not a publication product and has no ISBN, destination, package, or vendor-conformance claim.
        - Core Book owns PDF presentation defaults. Preserve Designed Page sizes only when the user wants full-art spreads to remain single wide PDF pages or custom Designed Pages to retain independent geometry. PDF ebook releases inherit this choice unless explicitly overridden.
        - Paperback and hardcover releases select an exact offline, versioned print product. The product fixes vendor, binding construction, trim availability, print process, exact paper stock and basis weight/GSM, cover material, finish choices, cover printing mode, page range, PDF rules, and required artifacts. Lorekeeper calculates spine and cover geometry from that exact product; never substitute a generic caliper or invent an unlisted combination. EPUB uses reflow/navigation settings. PDF ebook uses page-geometry settings. Never apply controls from one product type to another.
        - A release may opt into edition-specific manuscript content. Publish can enable it, summarize differences and compatibility diagnostics, and direct the user to the target-aware Editor. Only Editor may mutate chapter manuscript text, Figures, styles in use, or chapter Designed Page layouts. Publication-section content and page layouts are edited here in Publish.
        - Profile versions, standards identifiers, bleed rules, and package internals are application-managed. Do not ask the user to choose them.

        Behavior:
        - Execute explicit instructions directly.
        - When no release is selected, read and work against Core Book. When a release is selected, read its effective values and override markers.
        - Page size, margins, baseline body typography, and Book Text Styles are editable Core Book settings. Read their current revisions before changing them; publication releases inherit them unless explicitly customized.
        - Execute direct requests proactively with safe, reversible defaults. Ask only for a genuinely material unknown such as author identity, print destination, an ISBN the user must supply, or ambiguous black-and-white versus color cost. Explain meaningful cost and quality tradeoffs among construction, exact paper weight, color process, finish, duplex cover printing, case laminate, cloth, and jackets.
        - Recommend defaults from the Book Brief, Project Guidance, manuscript visuals, readers, and destination. Do not dump a production checklist.
        - Preserve unrelated values. Customize a release only where it differs; use ResetFields to restore live Core inheritance.
        - Create no release or ISBN unless requested. Never invent an ISBN.
        - Title and copyright are system Designed Pages: their linked copy resolves live from Core or effective release metadata while their placement and typography are edited on the page canvas. Contents is generated automatically from the effective book structure. Each other publication section has one content mode: either prose with optional flowing Figures, or Designed Page canvases. Use separate sections when both forms are needed. Sections may be placed at the front, back, or immediately before or after an act or chapter.
        - Publication-section inclusion, order, and single-page start side are authored choices. Preserve them unless the user asks to change them. A section can start on the next available, right/recto, or left/verso page; a true two-page facing spread necessarily starts verso. Distinguish vendor blockers from optional design guidance: KDP does not require a copyright page, and KDP front-matter order/side guidance is advisory; Ingram does not mandate title/copyright presence or order. Explain recommendations without silently applying them.
        - To edit a title, copyright, or other designed publication section: start from the protected visible-workspace context when that page is already open; otherwise list and read sections in the active target. The section read returns pageCanvases with exact composition and active-variant revisions. Call get_or_create_publication_section_page_variant before editing; it safely materializes and remaps an inherited release section when required. Then read and preview the returned variant, mutate it with focused page tools, and preview again. An omitted section remains editable; do not change its inclusion merely to design it. Never upsert unchanged section metadata merely to find or unlock its canvas.
        - Create user-authored material such as Dedication, Epigraph, Acknowledgments, About the Author, Also By, References, image pages, or arbitrary production pages with publication-section tools. Release sections inherit Core live until customized; do not create duplicate release content when inheritance is sufficient.
        - upsert_publication_section creates an empty section or changes section metadata only. It never accepts manuscript JSON and never replaces content. After creating a prose section, read its returned ID and revision, then add the requested copy with focused patch_publication_section_manuscript operations in the same turn. Do not copy the bounded blocks returned by read_publication_section into an upsert payload.
        - Publication-section prose operations use the canonical names InsertBlock, ReplaceBlockText, DeleteBlock, MoveBlock, SplitBlock, MergeBlocks, SetBlockType, SetBlockStyle, SetInlineMark, and SetParagraphPresentation. Use SetBlockStyle to apply a saved Book Text Style. To insert and directly format a new block in one patch, assign the InsertBlock a stable blockId and follow it with SetParagraphPresentation for that ID.
        - While designing a publication-section page, resolve and read its selected variant, preview it in annotated mode, make focused changes or stage one large semantic-and-scene update, preview the result again, validate the active Core/release target, and finish with a clean preview. Use fill_publication_section_page_image_canvas for full-page art and place_project_image_in_publication_section_page_frame to replace an existing frame without reconstructing the scene.
        - A final title or copyright preview must visibly contain every required non-empty linked field. An empty linked frame is unresolved state, not a harmless preview limitation and not successful completion. Reread the effective metadata and resolved page after changing Book details; if the required copy remains absent, report the tool failure and do not claim the page is complete.
        - Read list_publication_book_fonts before choosing a font-family key for a section page, cover, or Book Text Style. Use only returned family keys and available face weights/styles; never invent a font name from appearance alone.

        Tool and state integrity:
        - Use tools for every publication read or mutation and honor Core or release revisions.
        - The complete ordered outline, synopses, beats, Project Guidance, Book Brief, and project facts are supplied every turn. Use list_search_sources, search_project, and paginated read_project_source for chapter bodies, research, sources, entities, facts, or other project details that are not already present. Use list_project_images and read_project_image for reusable visual assets.
        - Immediately before mutation, reread its target. After a conflict, perform one compact reread and retry only when intent remains unambiguous.
        - Before preparing print files, read the selected print product and calculated geometry. Use prepare_publication_files for the interior layout pass, exact spine/cover resolution, rendering, validation, and packaging. Do not attempt separate low-level orchestration.
        - Never claim a mutation, preparation, validation, package, or export succeeded unless the tool result says so.
        - Tool results are not replayed into later model turns. Use visible prose as the durable work log: narrate each meaningful publication phase and its reason, record consequential results before moving on, and end with durable decisions, exact changes, revisions, diagnostics, and unresolved questions without repeating large payloads.
        - Returned URLs require a user action. Never claim that you downloaded a file.
        - Chapters may combine semantic text, flowing Figures, and Designed Pages. A publication section is intentionally either prose with optional Figures or a designed-page canvas section. Covers remain separate front-cover or full-wrap compositions.
        - Use project page setup for authoring decisions and release geometry only for compatibility and covers. Optional generation geometry derives dimensions but never places the result; ordinary source-image shapes remain valid and are fitted non-destructively.
        - generate_project_image and edit_project_image wait for completion and always return unattached project images. Inspect the visible output, then use its ID with the focused Core/release cover tool or publication-section page tool in this same turn. Chapter manuscript and chapter Designed Page placement belongs to Editor. Never imply geometry guidance attached an image.
        - Cover artwork always remains beneath canonical title, subtitle, author, spine, and back-cover copy. Adjust the artwork crop, opacity, and framing instead of trying to raise it above cover text.
        - For existing cover design work, call preview_publication_cover_canvas in annotated mode before mutating. After placing or arranging artwork, inspect another annotated whole-cover preview and correct clipping, hierarchy, protected regions, copy legibility, and collisions. Call the clean mode before reporting completion. You may skip only the initial preview for a genuinely empty cover.
        - Cover generation targets provide an exact target aspect and a moderate-resolution requested raster for the selected cover surface or frame. A different provider raster remains compatible when aspectMatched=true; rasterMatched is informational. Warn or suggest regeneration only for LAYOUT_IMAGE_ASPECT_MISMATCH. Do not add a publication-DPI caveat merely because a proportional output used different pixel dimensions.
        - Treat perfect-bound outside and inside, case wrap, dust jacket, and Digital Cloth setup as distinct product surfaces. Read, mutate, and visually preview the exact surface being edited; never overwrite a reviewed surface while working on another. For duplex paperbacks, page one is outside and page two is inside, with the inside-spine no-ink region kept clear. For jacketed case products, case and jacket are independent required designs.
        - A cover-canvas preview is a visible chat attachment and model visual context when vision is available; it is not an image-library asset. If model visual delivery is unavailable, leave the preview visible for the user and report that you could not visually verify the cover instead of inferring appearance from scene JSON.
        - Require alt text or an explicit decorative decision for publication releases and preserve logical reading order. A Core reading PDF may complete with unresolved image accessibility decisions as explicit warnings; report those warnings and do not describe the copy as publication-ready.
        - Submit large cover or publication-section page payloads once to staging, then apply only the stage ID and expected revision.
        - Release format is fixed. Create another release for another product type.

        Publishing trust:
        - Distinguish Core reading-copy validation from publication-release validation. Never describe a Core reading PDF as vendor-ready or published.
        - Preview prepared EPUB artifacts with read_epub_artifact_preview when navigation order or extracted location text matters. Lorekeeper preparation performs the app's declared structural checks, while the in-app reader visually inspects the immutable result. Neither establishes behavior in every third-party reader; report that boundary without promising a separate future validator.
        - A successful print preparation means “Lorekeeper validated for profile version …”. You may prepare files and explain validation, but only a real vendor upload can establish acceptance.
        - End with exact mutations, inheritance or override state, current preparation state, blockers, download actions, and remaining user actions.
        """;

    private const string InitialGreeting =
        "I can help shape the complete publication—from metadata and front/back matter through book design, product selection, preflight, and prepared files—while keeping vendor requirements and editorial conventions distinct.";

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
        "get_or_create_publication_section_page_variant",
        "fill_publication_section_page_image_canvas",
        "place_project_image_in_publication_section_page_frame",
        "apply_publication_section_page_composition_stage",
        "apply_publication_section_page_semantic_stage",
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
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var projects = databaseOperation.Repositories.Projects;
        var conversations = databaseOperation.Repositories.PublishConversations;
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
        await databaseOperation.SaveChangesAsync(cancellationToken);
        return conversation;
    }

    public async Task<IReadOnlyList<PublishMessage>> LoadMessagesAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var conversations = databaseOperation.Repositories.PublishConversations;
        return await conversations.LoadMessagesAsync(conversationId, cancellationToken);
    }

    public async Task<ChatProviderAvailability> GetChatProviderAvailabilityAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var conversation = await databaseOperation.Repositories.PublishConversations
            .GetByProjectIdAsync(projectId, cancellationToken);
        var selection = await providers.ResolveChatModelSelectionAsync(
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
        using var maintenance = turnRuntime.TryBeginMaintenance(new ChatTurnKey(projectId, ChatTurnSurface.Publish));
        if (maintenance is null)
            throw new InvalidOperationException("Publish Assistant is still working in another window. Stop or wait for that turn before changing its model.");

        if (providerId is int)
        {
            var selection = await providers.ResolveChatModelSelectionAsync(providerId, cancellationToken);
            if (!selection.IsAvailable || selection.Provider is null)
                throw new InvalidOperationException(selection.Message);
        }

        var normalizedProviderId = await providers.NormalizeChatModelSelectionAsync(providerId, cancellationToken);
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var conversation = await databaseOperation.Repositories.PublishConversations
            .GetByProjectIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException("Publish Assistant is not initialized.");
        conversation.SelectedProviderId = normalizedProviderId;
        conversation.UpdatedAt = DateTime.UtcNow;
        databaseOperation.Repositories.PublishConversations.UpdateSelectedProvider(conversation);
        await databaseOperation.SaveChangesAsync(cancellationToken);
    }

    public async Task<string> GetSystemPromptAsync(
        Guid projectId,
        Guid? selectedEditionId,
        PublishAssistantWorkspaceContext? workspaceContext = null,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var projects = databaseOperation.Repositories.Projects;
        var project = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");
        var selection = selectedEditionId is { } editionId
            ? $"The Publish workspace currently has publication release {editionId:D} selected. Treat that release as the active target: pass this release ID to Core/release tools and preserve inherited values. Do not switch to Core or pass a null release ID unless the user explicitly asks for a shared Core change. Read the selected release before acting."
            : "The Publish workspace currently targets Core Book. Read Core Book before acting; do not assume a publication release is required.";
        var visibleWorkspace = await DescribeVisibleWorkspaceAsync(projectId, selectedEditionId, workspaceContext, cancellationToken);
        var assembly = await contextBuilder.BuildAsync(
            new ContextBuildRequest(
                project,
                Purpose: ContextBuildPurpose.Publish,
                OperatingRules: WorkflowInstructions
                    + "\n\n" + selection
                    + "\n\n" + visibleWorkspace
                    + "\n\n" + AssistantWorkflowInstructions.NonReplayedToolHistory
                    + "\n\n" + AssistantWorkflowInstructions.AgentManuscriptProjection
                    + "\n\n" + AssistantWorkflowInstructions.PublicationContentCraft
                    + "\n\n" + AssistantWorkflowInstructions.ImageGeneration
                    + "\n\n" + AssistantWorkflowInstructions.ImageSpaceDiscipline
                    + "\n\n" + AssistantWorkflowInstructions.BookDesignCraft
                    + "\n\n" + AssistantWorkflowInstructions.CompositionDesign
                    + "\n\n" + AssistantWorkflowInstructions.PublicationDesign),
            cancellationToken);
        return assembly.Assemble();
    }

    private async Task<string> DescribeVisibleWorkspaceAsync(
        Guid projectId,
        Guid? selectedEditionId,
        PublishAssistantWorkspaceContext? workspaceContext,
        CancellationToken cancellationToken)
    {
        if (workspaceContext is null)
            return "Visible Publish workspace: no more specific design surface is open. Work against the selected Core Book or release overview.";

        var details = new List<string>
        {
            $"Visible Publish workspace: {workspaceContext.TargetLabel}.",
            $"Surface: {workspaceContext.Surface}.",
        };
        if (workspaceContext.SectionId is Guid sectionId)
            details.Add($"Publication section ID: {sectionId:D}.");
        if (!string.IsNullOrWhiteSpace(workspaceContext.SectionTitle))
            details.Add($"Publication section title: {workspaceContext.SectionTitle}.");
        if (workspaceContext.CompositionId is Guid compositionId)
        {
            details.Add($"Visible page composition ID: {compositionId:D}.");
            var composition = compositions is null
                ? null
                : await compositions.GetAsync(projectId, compositionId, cancellationToken);
            if (composition is not null)
            {
                details.Add($"Composition revision: {composition.Revision}.");
                var visibleVariant = workspaceContext.VariantId is Guid visibleVariantId
                    ? composition.Variants.FirstOrDefault(item => item.Id == visibleVariantId)
                    : selectedEditionId is Guid editionId && composition.EditionId == editionId
                        ? (await compositions!.ListVariantsAsync(projectId, compositionId, editionId, cancellationToken)).FirstOrDefault()
                        : composition.ActiveAuthoringVariantId is Guid activeId
                            ? composition.Variants.FirstOrDefault(item => item.Id == activeId)
                            : composition.Variants.OrderByDescending(item => item.UpdatedAt).FirstOrDefault();
                if (visibleVariant is not null)
                {
                    details.Add($"Visible geometry variant ID: {visibleVariant.Id:D}.");
                    details.Add($"Variant revision: {workspaceContext.VariantRevision ?? visibleVariant.Revision}; geometry key: {visibleVariant.GeometryKey}.");
                }
            }
        }
        if (workspaceContext.SelectedObjectId is Guid objectId)
            details.Add($"Selected canvas object ID: {objectId:D}.");
        details.Add(selectedEditionId is null
            ? "This visible surface belongs to Core Book."
            : $"This visible surface belongs to selected release {selectedEditionId:D}.");
        details.Add("Treat this protected UI context as the user's current focus. Do not make them identify the open section, canvas, or target again. Reread mutable state before changing it.");
        return string.Join(' ', details);
    }

    public async Task ResetAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        using var maintenance = turnRuntime.TryBeginMaintenance(
            new ChatTurnKey(projectId, ChatTurnSurface.Publish));
        if (maintenance is null)
            throw new InvalidOperationException("Publish Assistant is still working in another window. Stop or wait for that turn before resetting the conversation.");
        await imageAttachments.ClearSurfaceAsync(projectId, ChatTurnSurface.Publish, cancellationToken);
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var conversations = databaseOperation.Repositories.PublishConversations;
        var existing = await conversations.GetByProjectIdAsync(projectId, cancellationToken);
        if (existing is null)
            return;
        await conversations.ResetMessagesAsync(existing, new PublishMessage
        {
            ConversationId = existing.Id,
            Order = 0,
            Role = PublishMessageRole.Assistant,
            Content = InitialGreeting,
        }, cancellationToken);
        await databaseOperation.SaveChangesAsync(cancellationToken);
    }

    public async IAsyncEnumerable<PublishTurnUpdate> SendAsync(
        Guid projectId,
        Guid? selectedEditionId,
        PublishAssistantWorkspaceContext? workspaceContext,
        string userText,
        IReadOnlyList<Guid> imageIds,
        int providerId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userText))
            throw new ArgumentException("Message cannot be empty.", nameof(userText));

        var conversation = await GetOrCreateAsync(projectId, cancellationToken);
        var persistedSelection = await providers.ResolveChatModelSelectionAsync(
            conversation.SelectedProviderId,
            cancellationToken);
        if (!persistedSelection.IsAvailable || persistedSelection.Provider is not { } persistedProvider)
        {
            yield return new PublishTurnError(persistedSelection.Message, Cancelled: false);
            yield break;
        }

        if (persistedProvider.Id != providerId)
        {
            yield return new PublishTurnError(ChatModelSelectionMessages.Changed, Cancelled: false);
            yield break;
        }

        var visionReady = await providers.IsVisionProviderWorkingAsync(persistedProvider.Id, cancellationToken);
        if (imageIds.Count > 0 && !visionReady)
        {
            yield return new PublishTurnError(
                "The active chat provider has not passed the vision check. Run Test in Settings > Providers before sending images.",
                Cancelled: false);
            yield break;
        }
        await imageAttachments.ResolveAsync(projectId, imageIds, cancellationToken);

        var nextOrder = await turnEngine.ReadAsync(
            repositories => repositories.PublishConversations,
            conversations => conversations.GetMaxOrderAsync(conversation.Id, cancellationToken),
            cancellationToken) + 1;
        var userMessage = new PublishMessage
        {
            ConversationId = conversation.Id,
            Order = nextOrder++,
            Role = PublishMessageRole.User,
            Content = userText.Trim(),
        };
        conversation.UpdatedAt = DateTime.UtcNow;
        await turnEngine.AddMessageAsync(repositories => repositories.PublishConversations, userMessage, cancellationToken);
        await imageAttachments.PersistAsync(
            projectId,
            ChatTurnSurface.Publish,
            userMessage.Id,
            imageIds,
            cancellationToken);
        using var authoringTurn = authoringMutationContext.BeginAssistantTurn(
            userMessage.Id,
            BuildAssistantActionLabel(userText));

        IChatClient? chat = null;
        IList<AITool>? aiTools = null;
        PublishAssistantContext? assistantContext = null;
        string? systemPrompt = null;
        Exception? setupException = null;
        var setupCancelled = false;
        try
        {
            chat = await clients.CreateChatClientAsync(persistedProvider.Id, cancellationToken);
            assistantContext = new PublishAssistantContext(projectId, conversation.Id, selectedEditionId, workspaceContext, visionReady, cancellationToken);
            aiTools = await tools.BuildAsync(assistantContext, cancellationToken);
            systemPrompt = await GetSystemPromptAsync(projectId, selectedEditionId, workspaceContext, cancellationToken);
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
        var persistedMessages = await turnEngine.ReadAsync(
            repositories => repositories.PublishConversations,
            conversations => conversations.LoadMessagesAsync(conversation.Id, cancellationToken),
            cancellationToken);
        foreach (var persisted in persistedMessages)
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
                await turnEngine.AddMessageAsync(repositories => repositories.PublishConversations, activeAssistant, cancellationToken);

                ChatRoundCompleted? completedRound = null;
                await foreach (var update in turnEngine.StreamRoundAsync(readyChat, messages, chatOptions, cancellationToken))
                {
                    switch (update)
                    {
                        case ChatRoundTextDelta text:
                            yield return new PublishTextDelta(text.Text);
                            break;
                        case ChatRoundReasoningDelta reasoning:
                            yield return new PublishReasoningDelta(reasoning.Text);
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
                            if (!string.IsNullOrEmpty(failed.Reasoning))
                                activeAssistant.Reasoning = failed.Reasoning;
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

                activeAssistant.Reasoning = completedRound.Reasoning;

                if (completedRound.ToolCalls.Count == 0)
                {
                    activeAssistant.Content = completedRound.Text;
                    activeAssistant.Status = PublishMessageStatus.Completed;
                    await SafePersistAsync(activeAssistant);
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
                    ChatTurnEngine.BuildAssistantContents(completedRound.Text, completedRound.ToolCalls, completedRound.Reasoning)));

                var resultContents = new List<AIContent>();
                var roundTransientVisuals = new List<PublishAssistantTransientVisual>();
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
                    await turnEngine.AddMessageAsync(repositories => repositories.PublishConversations, toolMessage, CancellationToken.None);
                    var transientVisuals = assistantContext?.DrainTransientVisuals() ?? [];
                    roundTransientVisuals.AddRange(transientVisuals);
                    var visualAttachments = transientVisuals
                        .Select(visual => ToVisualAttachment(projectId, pendingCall.CallId, visual))
                        .ToList();
                    if (visualAttachments.Count > 0)
                    {
                        await turnEngine.WriteAsync(
                            repositories => repositories.PublishConversations,
                            conversations => conversations.AddMessageVisualsAsync(
                                visualAttachments.Select((visual, index) => new PublishMessageVisual
                                {
                                    Id = visual.Id,
                                    MessageId = toolMessage.Id,
                                    SortOrder = index,
                                    ToolCallId = visual.ToolCallId,
                                    Title = visual.Title,
                                    Caption = visual.Caption,
                                    SourceKind = visual.SourceKind,
                                    SourceRefId = visual.SourceRefId,
                                    ContentType = visual.ContentType,
                                    FileName = visual.FileName,
                                    Width = visual.Width,
                                    Height = visual.Height,
                                    Data = visual.Data ?? [],
                                }),
                                CancellationToken.None),
                            CancellationToken.None);
                    }
                    resultContents.Add(new FunctionResultContent(pendingCall.CallId, outcome.Result));
                    yield return new PublishToolCallCompleted(
                        pendingCall.CallId,
                        pendingCall.Name,
                        outcome.Error is null ? outcome.Result : null,
                        outcome.Error,
                        stopwatch.Elapsed.TotalMilliseconds,
                        visualAttachments);

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
                    if (visionReady && roundTransientVisuals.Count > 0)
                    {
                        var contents = new List<AIContent>
                        {
                            new TextContent("Direct Publish canvas previews from the preceding tool. Inspect the complete cover or publication-section surface before choosing further design mutations."),
                        };
                        foreach (var visual in roundTransientVisuals)
                        {
                            contents.Add(new TextContent($"\n{visual.Caption}; visualId={visual.Id:N}; file={visual.FileName}"));
                            contents.Add(new DataContent(visual.Data, visual.ContentType) { Name = visual.FileName });
                        }
                        messages.Add(ChatTurnEngine.MarkToolContextMessage(new ChatMessage(ChatRole.User, contents)));
                    }
                }

                if (turnEngine.TryCompactContext(messages, persistedProvider.ModelId) is { } compaction)
                {
                    yield return new PublishContextTrimmed(compaction);
                    if (compaction.LimitExceeded)
                    {
                        activeAssistant.Status = PublishMessageStatus.Failed;
                        activeAssistant.ErrorMessage = ChatContextCompaction.LimitExceededMessage;
                        await SafePersistAsync(activeAssistant);
                        yield return new PublishTurnError(activeAssistant.ErrorMessage, Cancelled: false);
                        yield break;
                    }
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

    private static string BuildAssistantActionLabel(string userText)
    {
        var compact = string.Join(' ', userText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (compact.Length > 120)
            compact = compact[..117] + "...";
        return $"Assistant: Publish — {compact}";
    }

    private static PublishChatVisualAttachment ToVisualAttachment(
        Guid projectId,
        string toolCallId,
        PublishAssistantTransientVisual visual)
    {
        var contentUrl = $"/projects/{projectId:N}/publish-chat-visuals/{visual.Id:N}/content";
        return new PublishChatVisualAttachment(
            visual.Id,
            visual.Title,
            visual.Caption,
            $"{contentUrl}?maxEdge=640",
            contentUrl,
            visual.Width,
            visual.Height,
            toolCallId,
            visual.SourceKind,
            visual.SourceRefId,
            visual.ContentType,
            visual.FileName,
            visual.Data);
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
            or "get_or_create_publication_section_page_variant"
            or "fill_publication_section_page_image_canvas"
            or "place_project_image_in_publication_section_page_frame"
            or "add_project_image_to_publication_section_page"
            or "apply_publication_section_page_composition_stage"
            or "apply_publication_section_page_semantic_stage"
            or "apply_publication_section_page_workspace_stage")
        {
            var pageReleaseId = ReadGuid(resultJson, "releaseId") ?? ReadGuid(argumentsJson, "releaseId");
            return new PublishWorkspaceMutated(
                pageReleaseId,
                false,
                PublishWorkspaceMutationKind.Edition,
                ReadGuid(resultJson, "selectId") ?? ReadGuid(argumentsJson, "targetId"),
                ReadGuid(resultJson, "sectionId"),
                ReadGuid(resultJson, "compositionId") ?? ReadGuid(argumentsJson, "compositionId"),
                ReadGuid(resultJson, "variantId")
                    ?? (toolName == "get_or_create_publication_section_page_variant"
                        ? ReadGuid(resultJson, "targetId")
                        : ReadGuid(argumentsJson, "variantId")));
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
        await using var databaseOperation = await database.OpenWriteAsync(default);
        databaseOperation.ShareWithNestedOperations();
        var conversations = databaseOperation.Repositories.PublishConversations;
        await conversations.AddMessageAsync(new PublishMessage
        {
            ConversationId = conversationId,
            Order = order,
            Role = PublishMessageRole.Assistant,
            Status = status,
            ErrorMessage = error,
        }, CancellationToken.None);
        await databaseOperation.SaveChangesAsync(CancellationToken.None);
    }

    private async Task SafePersistAsync(PublishMessage message)
    {
        try
        {
            await turnEngine.UpdateMessageAsync(repositories => repositories.PublishConversations, message, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to persist Publish chat message {MessageId}", message.Id);
        }
    }
}
