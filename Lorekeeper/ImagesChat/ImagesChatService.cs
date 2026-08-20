using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Lorekeeper.ChatTurns;
using Lorekeeper.Context;
using Lorekeeper.EntityVisuals;
using Lorekeeper.Images;
using Lorekeeper.Llm;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Lorekeeper.ImagesChat;

public sealed class ImagesChatService(
    IChatImageAttachmentService imageAttachments, IAppDatabaseOperationFactory database, IContextBuilder contextBuilder, ILlmProviderService providerService, IChatClientFactory chatClientFactory, IProjectImageService projectImages, IEntityVisualContextService entityVisualContext, ImagesChatTools tools, ChatTurnRuntime turnRuntime, ChatTurnEngine turnEngine, IOptions<AgentOptions> options, ILogger<ImagesChatService> logger) : IImagesChatService
{
    public const string ImagesWorkflowInstructions = """
        You are Lorekeeper's Images assistant: the concept-art and visual-canon workspace for a long-form writing project.

        Context integrity:
        - Entity/link reads use explicit JSON-path pagination with full identities and GUIDs repeated on every page. Follow nextPageArguments until the needed records are complete; assemble labeled oversized text-field segments in order.
        - Search and list results are explicitly compact discovery payloads. Honor total/returned counts and isComplete, then use exact detailReadArguments for complete reads. Copy identifiers exactly; never shorten, reconstruct, or fuzzily correct a GUID.

        Your job:
        - Help the user discover and define a coherent art style, create concept art, and establish canonical appearances for characters, locations, creatures, props, costumes, and other entities.
        - Use Project Guidance, the Book Brief, outline, facts, chapters, entities, canonical references, and image metadata as read-only grounding before making visual decisions.
        - Work only in the project image library, entity canonical references, and the approved Book Brief Visual Direction. Do not modify manuscript content, Figures, Designed Pages, page setup, covers, publication sections, or chapter context.
        - When a request belongs to page illustration or composition, direct the user to Editor. When it belongs to publication sections or covers, direct the user to Publish. You may still create reusable concept art when that is the actual request, but never imply that it was placed in the book.
        - Generate or edit unattached project images with generate_project_image or edit_project_image. These tools wait for completion; after a successful job, the generated images are supplied back to your model context when the provider supports vision.
        - Inspect completed outputs before describing them as successful. Attach an image to an entity only when the user has approved it as a stable canonical reference; exploratory art remains unattached.
        - Save Visual Direction only after explicit user approval. Read its exact current value immediately before saving and preserve it when the user is still exploring.
        - Reconnect with read_project_image_job or wait_project_image_job when a prior generation/edit job must be resumed; never replay its prompt. Cancel with cancel_project_image_job when requested or when a wait times out.
        - Do not claim an image was generated or edited unless the tool returns final saved image ids.
        - Keep final responses practical: record the approved style decisions, saved Visual Direction, canonical associations, image ids or filenames, rejected variants, failed outputs, and the next genuinely optional visual-development step.
        """;

    private const string InitialAssistantGreeting =
        "Let’s define the visual language of your book. I can help explore art styles and establish canonical designs for characters, locations, and other story elements.";

    private bool _mutatedSinceYield;

    public async Task<ProjectImageConversation> GetOrCreateAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var projects = databaseOperation.Repositories.Projects;
        var conversations = databaseOperation.Repositories.ProjectImageConversations;
        var existing = await conversations.GetByProjectIdAsync(projectId, cancellationToken);
        if (existing is not null) return existing;

        _ = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");

        var conversation = new ProjectImageConversation { ProjectId = projectId };
        await conversations.AddConversationAsync(conversation, cancellationToken);
        await conversations.AddMessageAsync(new ProjectImageMessage
        {
            ConversationId = conversation.Id,
            Order = 0,
            Role = ProjectImageMessageRole.Assistant,
            Content = InitialAssistantGreeting,
            Status = ProjectImageMessageStatus.Completed,
        }, cancellationToken);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        return conversation;
    }

    public async Task<IReadOnlyList<ProjectImageMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var conversations = databaseOperation.Repositories.ProjectImageConversations;
        return await conversations.LoadMessagesAsync(conversationId, cancellationToken);
    }

    public async Task<ChatProviderAvailability> GetChatProviderAvailabilityAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var conversation = await databaseOperation.Repositories.ProjectImageConversations
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
        using var maintenance = turnRuntime.TryBeginMaintenance(new ChatTurnKey(projectId, ChatTurnSurface.Images));
        if (maintenance is null)
            throw new InvalidOperationException("Images Chat is still working in another window. Stop or wait for that turn before changing its model.");

        if (providerId is int)
        {
            var selection = await providerService.ResolveChatModelSelectionAsync(providerId, cancellationToken);
            if (!selection.IsAvailable || selection.Provider is null)
                throw new InvalidOperationException(selection.Message);
        }

        var normalizedProviderId = await providerService.NormalizeChatModelSelectionAsync(providerId, cancellationToken);
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var conversation = await databaseOperation.Repositories.ProjectImageConversations
            .GetByProjectIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException("Images Chat is not initialized.");
        conversation.SelectedProviderId = normalizedProviderId;
        conversation.UpdatedAt = DateTime.UtcNow;
        databaseOperation.Repositories.ProjectImageConversations.UpdateSelectedProvider(conversation);
        await databaseOperation.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ProjectImageChatAttachmentView>> ListAttachmentsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var attachments = await db.ProjectImageChatAttachments
            .AsNoTracking()
            .Include(attachment => attachment.Image)
            .Where(attachment => attachment.ProjectId == projectId)
            .OrderBy(attachment => attachment.SortOrder)
            .ToListAsync(cancellationToken);

        return attachments.Select(ToAttachmentView).ToList();
    }

    public async Task<ProjectImageChatAttachmentView> AttachImageAsync(Guid projectId, Guid imageId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var projects = databaseOperation.Repositories.Projects;
        _ = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");

        var image = await db.PublishAssets
            .FirstOrDefaultAsync(asset => asset.ProjectId == projectId && asset.Id == imageId, cancellationToken)
            ?? throw new InvalidOperationException($"Image {imageId} was not found in this project.");

        var existing = await db.ProjectImageChatAttachments
            .Include(attachment => attachment.Image)
            .FirstOrDefaultAsync(
                attachment => attachment.ProjectId == projectId && attachment.ImageId == imageId,
                cancellationToken);
        if (existing is not null)
            return ToAttachmentView(existing);

        var nextOrder = await db.ProjectImageChatAttachments
            .Where(attachment => attachment.ProjectId == projectId)
            .Select(attachment => (int?)attachment.SortOrder)
            .MaxAsync(cancellationToken) ?? -1;

        var now = DateTime.UtcNow;
        var attachment = new ProjectImageChatAttachment
        {
            ProjectId = projectId,
            ImageId = imageId,
            Image = image,
            Label = string.IsNullOrWhiteSpace(image.AltText) ? image.FileName : image.AltText.Trim(),
            SortOrder = nextOrder + 1,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await db.ProjectImageChatAttachments.AddAsync(attachment, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return ToAttachmentView(attachment);
    }

    public async Task RemoveAttachmentAsync(Guid projectId, Guid attachmentId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var attachment = await db.ProjectImageChatAttachments
            .FirstOrDefaultAsync(item => item.ProjectId == projectId && item.Id == attachmentId, cancellationToken);
        if (attachment is null)
            return;

        db.ProjectImageChatAttachments.Remove(attachment);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ClearAttachmentsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var attachments = await db.ProjectImageChatAttachments
            .Where(attachment => attachment.ProjectId == projectId)
            .ToListAsync(cancellationToken);
        if (attachments.Count == 0)
            return;

        db.ProjectImageChatAttachments.RemoveRange(attachments);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<string> GetSystemPromptAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var projects = databaseOperation.Repositories.Projects;
        var project = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");
        return await BuildSystemPromptAsync(project, cancellationToken);
    }

    public async Task ResetAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        using var maintenance = turnRuntime.TryBeginMaintenance(new ChatTurnKey(projectId, ChatTurnSurface.Images));
        if (maintenance is null)
            throw new InvalidOperationException("Images Chat is still working in another window. Stop or wait for that turn before resetting the conversation.");
        await imageAttachments.ClearSurfaceAsync(projectId, ChatTurnSurface.Images, cancellationToken);
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var conversations = databaseOperation.Repositories.ProjectImageConversations;
        var existing = await conversations.GetByProjectIdAsync(projectId, cancellationToken);
        if (existing is null) return;

        conversations.RemoveConversation(existing);
        await databaseOperation.SaveChangesAsync(cancellationToken);
    }

    public async IAsyncEnumerable<ImagesChatTurnUpdate> SendAsync(
        Guid projectId,
        string userText,
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
            yield return new ImagesChatTurnError(persistedSelection.Message, Cancelled: false);
            yield break;
        }

        if (persistedProvider.Id != providerId)
        {
            yield return new ImagesChatTurnError(ChatModelSelectionMessages.Changed, Cancelled: false);
            yield break;
        }

        var chatProvider = persistedProvider;
        var visionReady = await providerService.IsVisionProviderWorkingAsync(chatProvider.Id, cancellationToken);
        if (imageIds.Count > 0 && !visionReady)
        {
            yield return new ImagesChatTurnError("The active chat provider has not passed the vision check. Run Test in Settings > Providers before sending images.", Cancelled: false);
            yield break;
        }
        await imageAttachments.ResolveAsync(projectId, imageIds, cancellationToken);
        var turnAttachments = await ListAttachmentsAsync(projectId, cancellationToken);
        var allTurnImageIds = imageIds.Concat(turnAttachments.Select(attachment => attachment.ImageId)).Distinct().ToList();
        var nextOrder = await turnEngine.ReadAsync(
            repositories => repositories.ProjectImageConversations,
            conversations => conversations.GetMaxOrderAsync(conversation.Id, cancellationToken),
            cancellationToken) + 1;
        var userMessage = new ProjectImageMessage
        {
            ConversationId = conversation.Id,
            Order = nextOrder++,
            Role = ProjectImageMessageRole.User,
            Content = userText.Trim(),
            Status = ProjectImageMessageStatus.Completed,
        };
        conversation.UpdatedAt = DateTime.UtcNow;
        await turnEngine.AddMessageAsync(repositories => repositories.ProjectImageConversations, userMessage, cancellationToken);
        await imageAttachments.PersistAsync(projectId, ChatTurnSurface.Images, userMessage.Id, imageIds, cancellationToken);
        var additionalContextAttachments = turnAttachments.Where(attachment => !imageIds.Contains(attachment.ImageId)).ToList();
        if (additionalContextAttachments.Count > 0)
            await PersistVisualsAsync(userMessage.Id, toolCallId: null, BuildAttachmentVisuals(additionalContextAttachments));

        IChatClient chat = null!;
        IList<AITool> aiTools = null!;
        ImagesChatToolContext toolContext = null!;
        string systemPrompt = string.Empty;
        ContextAssembly? initialAssembly = null;
        string? setupError = null;
        try
        {
            var project = await turnEngine.ReadAsync(
                repositories => repositories.Projects,
                projects => projects.GetByIdAsync(projectId, cancellationToken),
                cancellationToken)
                ?? throw new InvalidOperationException($"Project {projectId} not found.");
            initialAssembly = await contextBuilder.BuildAsync(
                new ContextBuildRequest(
                    project,
                    UserMessage: userText,
                    Purpose: ContextBuildPurpose.Images,
                    OperatingRules: ImagesWorkflowInstructions + "\n\n" + AssistantWorkflowInstructions.VisualCreationWorkflow),
                cancellationToken);
            systemPrompt = initialAssembly.Assemble();
            chat = await chatClientFactory.CreateChatClientAsync(chatProvider.Id, cancellationToken);
            toolContext = new ImagesChatToolContext(projectId, conversation.Id, chatProvider.Id, visionReady, OnToolMutated, cancellationToken);
            aiTools = await tools.BuildAsync(toolContext, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Images chat turn setup failed for project {ProjectId}", projectId);
            setupError = ex.Message;
        }

        if (setupError is not null)
        {
            yield return new ImagesChatTurnError(setupError, Cancelled: false);
            yield break;
        }

        var chatOptions = new ChatOptions
        {
            Tools = aiTools,
            ToolMode = ChatToolMode.Auto,
        };

        var history = await turnEngine.ReadAsync(
            repositories => repositories.ProjectImageConversations,
            conversations => conversations.LoadMessagesAsync(conversation.Id, cancellationToken),
            cancellationToken);
        var messages = new List<ChatMessage> { new(ChatRole.System, systemPrompt) };
        if (initialAssembly is not null)
        {
            var visualMessage = await entityVisualContext.BuildVisionMessageAsync(
                projectId, initialAssembly.Visuals, visionReady,
                "Canonical entity references and explicit-image visual context from the project follow. Entity mappings are stable appearance/design references, not scene tags; inspect each label, association origin, image source, purpose, and visible content before using it.", cancellationToken);
            if (visualMessage is not null) messages.Add(visualMessage);
        }
        foreach (var persistedMessage in history)
        {
            if (persistedMessage.Id == userMessage.Id && allTurnImageIds.Count > 0)
            {
                if (visionReady)
                {
                    messages.Add(await imageAttachments.BuildUserMessageAsync(
                        projectId,
                        persistedMessage.Content,
                        allTurnImageIds,
                        "Current-turn attached images follow. Treat them as user-provided visual context. When generating or editing a continuity-related image, pass only relevant attached image ids in referenceImageIds.",
                        cancellationToken));
                }
                else
                {
                    messages.Add(await BuildUserMessageWithAttachmentsAsync(projectId, persistedMessage.Content, turnAttachments, visionReady, cancellationToken));
                }
                continue;
            }

            var modelMessage = ChatModelHistory.Project(
                persistedMessage.Role.ToString(),
                persistedMessage.Content);
            if (modelMessage is not null)
                messages.Add(modelMessage);
        }

        var maxIterations = Math.Max(1, options.Value.MaxToolIterations);
        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            var activeAssistant = new ProjectImageMessage
            {
                ConversationId = conversation.Id,
                Order = nextOrder++,
                Role = ProjectImageMessageRole.Assistant,
                Content = string.Empty,
                Status = ProjectImageMessageStatus.Pending,
            };
            await turnEngine.AddMessageAsync(repositories => repositories.ProjectImageConversations, activeAssistant, cancellationToken);

            ChatRoundCompleted? completedRound = null;
            await foreach (var update in turnEngine.StreamRoundAsync(chat, messages, chatOptions, cancellationToken))
            {
                switch (update)
                {
                    case ChatRoundTextDelta text:
                        yield return new ImagesChatTextDelta(text.Text);
                        break;
                    case ChatRoundToolCallStarted started:
                        yield return new ImagesChatToolCallStarted(started.CallId, started.ToolName, started.ArgumentsJson, started.ArgumentsComplete);
                        break;
                    case ChatRoundToolCallArgumentsDelta delta:
                        yield return new ImagesChatToolCallArgumentsDelta(delta.CallId, delta.ArgumentsDelta, delta.ArgumentsComplete);
                        break;
                    case ChatRoundCompleted completed:
                        completedRound = completed;
                        break;
                    case ChatRoundFailed failed:
                        activeAssistant.Content = failed.Text;
                        activeAssistant.Status = failed.Cancelled
                            ? ProjectImageMessageStatus.Cancelled
                            : ProjectImageMessageStatus.Failed;
                        activeAssistant.ErrorMessage = failed.Cancelled ? "Cancelled by user." : failed.Message;
                        await SafePersistAsync(activeAssistant);
                        yield return new ImagesChatTurnError(failed.Message, failed.Cancelled);
                        yield break;
                }
            }

            DrainMutated();
            if (completedRound is null)
            {
                activeAssistant.Status = ProjectImageMessageStatus.Failed;
                activeAssistant.ErrorMessage = "Images chat streaming ended without a completed round.";
                await SafePersistAsync(activeAssistant);
                yield return new ImagesChatTurnError(activeAssistant.ErrorMessage, Cancelled: false);
                yield break;
            }

            var textBuilder = new StringBuilder(completedRound.Text);
            var pendingCalls = completedRound.ToolCalls;

            if (pendingCalls.Count == 0)
            {
                activeAssistant.Content = textBuilder.ToString();
                activeAssistant.Status = ProjectImageMessageStatus.Completed;
                await SafePersistAsync(activeAssistant);
                yield return new ImagesChatAssistantMessageCompleted(activeAssistant.Id);
                yield break;
            }

            var manifest = pendingCalls
                .Select(ChatToolCallManifest.From)
                .ToList();
            activeAssistant.Content = textBuilder.ToString();
            activeAssistant.ToolCallsJson = JsonSerializer.Serialize(manifest);
            activeAssistant.Status = ProjectImageMessageStatus.Completed;
            await SafePersistAsync(activeAssistant);

            messages.Add(new ChatMessage(
                ChatRole.Assistant,
                ChatTurnEngine.BuildAssistantContents(textBuilder.ToString(), pendingCalls)));

            var resultContents = new List<AIContent>();
            var modelOnlyImagesForNextRound = new List<ProjectImageView>();
            foreach (var pendingCall in pendingCalls)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    yield return new ImagesChatTurnError("Cancelled.", Cancelled: true);
                    yield break;
                }

                toolContext.BeginToolCall(pendingCall.CallId, pendingCall.Name, pendingCall.ArgumentsJson);
                var stopwatch = Stopwatch.StartNew();
                var toolOutcome = await turnEngine.InvokeToolAsync(aiTools, pendingCall, cancellationToken);
                stopwatch.Stop();

                if (toolOutcome.Cancelled)
                {
                    yield return new ImagesChatTurnError("Cancelled.", Cancelled: true);
                    yield break;
                }

                var toolResult = toolOutcome.Result;
                var toolError = toolOutcome.Error;
                var visuals = toolContext.DrainVisuals(pendingCall.CallId);
                var toolMessage = new ProjectImageMessage
                {
                    ConversationId = conversation.Id,
                    Order = nextOrder++,
                    Role = ProjectImageMessageRole.Tool,
                    Content = toolResult ?? string.Empty,
                    ToolCallId = pendingCall.CallId,
                    ToolName = pendingCall.Name,
                    Status = toolError is null ? ProjectImageMessageStatus.Completed : ProjectImageMessageStatus.Failed,
                    ErrorMessage = toolError,
                };
                await turnEngine.AddMessageAsync(repositories => repositories.ProjectImageConversations, toolMessage, CancellationToken.None);
                await PersistVisualsAsync(toolMessage.Id, pendingCall.CallId, visuals);

                resultContents.Add(new FunctionResultContent(pendingCall.CallId, toolResult ?? string.Empty));
                var modelImages = toolContext.DrainModelOnlyImages();
                if (modelImages.Count > 0)
                    modelOnlyImagesForNextRound.AddRange(modelImages);

                yield return new ImagesChatToolCallCompleted(
                    pendingCall.CallId,
                    pendingCall.Name,
                    toolError is null ? toolResult : null,
                    toolError,
                    stopwatch.Elapsed.TotalMilliseconds,
                    visuals);

                if (DrainMutated())
                    yield return new ImagesChatMutated();
            }

            messages.Add(new ChatMessage(ChatRole.Tool, resultContents));
            if (modelOnlyImagesForNextRound.Count > 0)
                messages.Add(ChatTurnEngine.MarkToolContextMessage(
                    await BuildModelOnlyImageMessageAsync(projectId, modelOnlyImagesForNextRound)));

            if (turnEngine.TryCompactContext(messages, chatProvider.ModelId) is { } compaction)
            {
                manifest.Add(new ChatToolCallManifest(
                    compaction.CallId,
                    ChatTurnEngine.CompactionToolName,
                    ChatContextCompaction.EmptyArgumentsJson));
                activeAssistant.ToolCallsJson = JsonSerializer.Serialize(manifest);
                await SafePersistAsync(activeAssistant);

                await turnEngine.AddMessageAsync(repositories => repositories.ProjectImageConversations, new ProjectImageMessage
                {
                    ConversationId = conversation.Id,
                    Order = nextOrder++,
                    Role = ProjectImageMessageRole.Tool,
                    Content = ChatTurnEngine.CompactionNotice,
                    ToolCallId = compaction.CallId,
                    ToolName = ChatTurnEngine.CompactionToolName,
                    Status = ProjectImageMessageStatus.Completed,
                }, CancellationToken.None);
                yield return new ImagesChatToolCallStarted(
                    compaction.CallId,
                    ChatTurnEngine.CompactionToolName,
                    ChatContextCompaction.EmptyArgumentsJson,
                    ArgumentsComplete: true);
                yield return new ImagesChatToolCallCompleted(
                    compaction.CallId,
                    ChatTurnEngine.CompactionToolName,
                    ChatTurnEngine.CompactionNotice,
                    Error: null,
                    DurationMs: 0,
                    Visuals: []);
            }

            if (iteration == maxIterations - 1)
            {
                yield return new ImagesChatTurnError(ChatTurnEngine.ToolLoopLimitError(maxIterations), Cancelled: false);
                yield break;
            }
        }
    }

    private async Task<string> BuildSystemPromptAsync(Project project, CancellationToken cancellationToken)
    {
        var assembly = await contextBuilder.BuildAsync(
            new ContextBuildRequest(
                project,
                Purpose: ContextBuildPurpose.Images,
                OperatingRules: ImagesWorkflowInstructions + "\n\n" + AssistantWorkflowInstructions.VisualCreationWorkflow),
            cancellationToken);
        return assembly.Assemble();
    }

    private static IReadOnlyList<ImagesChatVisualAttachment> BuildAttachmentVisuals(
        IReadOnlyList<ProjectImageChatAttachmentView> attachments) =>
        attachments
            .Select(attachment => new ImagesChatVisualAttachment(
                Guid.NewGuid(),
                attachment.Label,
                "Attached to Images Chat context.",
                attachment.PreviewImageUrl,
                attachment.FullImageUrl,
                Width: null,
                Height: null,
                ToolCallId: null,
                SourceKind: "projectImage",
                SourceRefId: attachment.ImageId,
                ContentType: attachment.ContentType,
                FileName: attachment.FileName))
            .ToList();

    private static ProjectImageChatAttachmentView ToAttachmentView(ProjectImageChatAttachment attachment) =>
        new(
            attachment.Id,
            attachment.ImageId,
            attachment.Label,
            attachment.Image.FileName,
            attachment.Image.AltText,
            attachment.Image.ContentType,
            $"/projects/{attachment.ProjectId:N}/images/{attachment.ImageId:N}/content?maxEdge=160",
            $"/projects/{attachment.ProjectId:N}/images/{attachment.ImageId:N}/content",
            attachment.SortOrder,
            attachment.CreatedAt,
            attachment.UpdatedAt);

    private void OnToolMutated() => _mutatedSinceYield = true;

    private bool DrainMutated()
    {
        if (!_mutatedSinceYield) return false;
        _mutatedSinceYield = false;
        return true;
    }

    private async Task PersistVisualsAsync(Guid messageId, string? toolCallId, IReadOnlyList<ImagesChatVisualAttachment> visuals)
    {
        await using var databaseOperation = await database.OpenWriteAsync(default);
        databaseOperation.ShareWithNestedOperations();
        var conversations = databaseOperation.Repositories.ProjectImageConversations;
        if (visuals.Count == 0)
            return;

        await conversations.AddMessageVisualsAsync(visuals.Select((visual, index) => new ProjectImageMessageVisual
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
        await databaseOperation.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<ChatMessage> BuildUserMessageWithAttachmentsAsync(
        Guid projectId,
        string userText,
        IReadOnlyList<ProjectImageChatAttachmentView> attachments,
        bool visionReady,
        CancellationToken cancellationToken)
    {
        var contents = new List<AIContent>
        {
            new TextContent(userText),
            new TextContent("\nCurrent-turn attached images follow. Treat them as user-provided visual context. When generating or editing a continuity-related image, pass only relevant attached image ids in referenceImageIds."),
        };

        foreach (var attachment in attachments)
        {
            contents.Add(new TextContent($"\nAttached image {attachment.ImageId:N}: {attachment.Label} ({attachment.FileName})"));
            if (!visionReady)
                continue;

            var data = await projectImages.GetDataAsync(projectId, attachment.ImageId, cancellationToken: cancellationToken);
            if (data is null)
                continue;

            contents.Add(new DataContent(data.Data, data.ContentType)
            {
                Name = data.FileName,
            });
        }

        if (!visionReady)
            contents.Add(new TextContent("\nThe active chat provider is not vision-ready, so only attachment metadata is available."));

        return new ChatMessage(ChatRole.User, contents);
    }

    private async Task<ChatMessage> BuildModelOnlyImageMessageAsync(Guid projectId, IReadOnlyList<ProjectImageView> images)
    {
        var contents = new List<AIContent>
        {
            new TextContent("Project images returned by the previous tool call are attached as model-only visual context. Inspect them when deciding on further concept-art edits, canonical promotion, or future referenceImageIds for visual continuity."),
        };

        foreach (var image in images.DistinctBy(image => image.Id))
        {
            var data = await projectImages.GetDataAsync(projectId, image.Id, cancellationToken: CancellationToken.None);
            if (data is null) continue;

            contents.Add(new TextContent($"\nImage {image.Id:N}: {image.FileName}"));
            contents.Add(new DataContent(data.Data, data.ContentType)
            {
                Name = data.FileName,
            });
        }

        return new ChatMessage(ChatRole.User, contents);
    }

    private async Task SafePersistAsync(ProjectImageMessage message)
    {
        try
        {
            await turnEngine.UpdateMessageAsync(repositories => repositories.ProjectImageConversations, message, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist Images chat message {MessageId}", message.Id);
        }
    }

}
