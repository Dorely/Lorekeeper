using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Lorekeeper.Context;
using Lorekeeper.Images;
using Lorekeeper.Llm;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Persistence.Repositories;
using Microsoft.Extensions.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lorekeeper.ImagesChat;

public sealed class ImagesChatService(
    IProjectRepository projects,
    IProjectImageConversationRepository conversations,
    AppDbContext db,
    IContextBuilder contextBuilder,
    ILlmProviderService providerService,
    IChatClientFactory chatClientFactory,
    IProjectImageService projectImages,
    ImagesChatTools tools,
    IOptions<AgentOptions> options,
    ILogger<ImagesChatService> logger) : IImagesChatService
{
    public const string ImagesWorkflowInstructions = """
        You are Lorekeeper's Images Chat: an image-generation and visual-layout assistant for a long-form writing project.

        Your job:
        - Help the user generate new project images, edit existing project images, and reason about where images fit in Picture Page and Illustrated Prose chapters.
        - Use project guidance, outline, facts, chapters, image metadata, and visual layout manifests before making image-prompt decisions.
        - Use rendered snapshot inspection when the user asks about the actual visible layout and the provider is vision-ready.
        - For targeted edits, use masks. User-painted masks and agent-created shape masks follow the same convention: transparent pixels are editable and opaque pixels are preserved.
        - Write generation prompts as compact image briefs with use case, asset type, subject, style/medium, composition/framing, lighting/mood, and constraints. Label reference images by role, preserve explicit edit invariants, and avoid text, logos, and watermarks unless the user explicitly asks for them.
        - For picture books, recurring characters, recurring settings, series art, or any continuity-sensitive image, look for relevant existing project images first. Use multiple referenceImageIds when useful to preserve the same character design, clothes, hair, age, proportions, palette, medium, important props, and setting traits across pages. Do not use continuity references when the user clearly asks for a redesign, variant, or style break.
        - After a successful continuity-sensitive generation, treat the saved output as a future project reference image for that character, outfit, prop, setting, or style.
        - For PicturePage image generation, read the chapter visual layout when needed and pass targetChapterId plus targetPictureImageElementId when filling an existing image frame. Omit size only when you want the tool to use the layout-native recommended size; preserve explicit user-supplied sizes. Prompt for the page or slot shape, leave quiet negative space under text boxes, and keep important subjects away from text overlays and the center gutter on spreads.
        - Queue image generation/edit jobs with generate_image or edit_image. These tools wait for completion; after a successful job, the generated images are supplied back to your model context when the provider supports vision.
        - Do not claim an image was generated or edited unless the tool returns final saved image ids.
        - Keep final responses practical: mention saved image ids/filenames, what changed, any failed outputs, and useful next steps such as placing an image in a chapter.
        """;

    private const string InitialAssistantGreeting =
        "Tell me what image you want to generate or edit, or which chapter layout you want me to inspect.";

    private bool _mutatedSinceYield;

    public async Task<ProjectImageConversation> GetOrCreateAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
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
        await conversations.SaveChangesAsync(cancellationToken);
        return conversation;
    }

    public async Task<IReadOnlyList<ProjectImageMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        await conversations.LoadMessagesAsync(conversationId, cancellationToken);

    public async Task<IReadOnlyList<ProjectImageChatAttachmentView>> ListAttachmentsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
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
        var attachment = await db.ProjectImageChatAttachments
            .FirstOrDefaultAsync(item => item.ProjectId == projectId && item.Id == attachmentId, cancellationToken);
        if (attachment is null)
            return;

        db.ProjectImageChatAttachments.Remove(attachment);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ClearAttachmentsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
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
        var project = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");
        return await BuildSystemPromptAsync(project, cancellationToken);
    }

    public async Task ResetAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var existing = await conversations.GetByProjectIdAsync(projectId, cancellationToken);
        if (existing is null) return;

        conversations.RemoveConversation(existing);
        await conversations.SaveChangesAsync(cancellationToken);
    }

    public async IAsyncEnumerable<ImagesChatTurnUpdate> SendAsync(
        Guid projectId,
        string userText,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userText))
            throw new ArgumentException("Message cannot be empty.", nameof(userText));

        var conversation = await GetOrCreateAsync(projectId, cancellationToken);
        var providerAvailability = await providerService.GetDefaultChatProviderAvailabilityAsync(cancellationToken);
        if (!providerAvailability.IsAvailable || providerAvailability.Provider is null)
        {
            yield return new ImagesChatTurnError(providerAvailability.Message, Cancelled: false);
            yield break;
        }

        var chatProvider = providerAvailability.Provider;
        var visionReady = await providerService.IsVisionProviderWorkingAsync(chatProvider.Id, cancellationToken);
        var turnAttachments = await ListAttachmentsAsync(projectId, cancellationToken);
        var nextOrder = await conversations.GetMaxOrderAsync(conversation.Id, cancellationToken) + 1;
        var userMessage = new ProjectImageMessage
        {
            ConversationId = conversation.Id,
            Order = nextOrder++,
            Role = ProjectImageMessageRole.User,
            Content = userText.Trim(),
            Status = ProjectImageMessageStatus.Completed,
        };
        await conversations.AddMessageAsync(userMessage, cancellationToken);
        conversation.UpdatedAt = DateTime.UtcNow;
        await conversations.SaveChangesAsync(cancellationToken);
        if (turnAttachments.Count > 0)
            await PersistVisualsAsync(userMessage.Id, toolCallId: null, BuildAttachmentVisuals(turnAttachments));

        IChatClient chat = null!;
        IList<AITool> aiTools = null!;
        ImagesChatToolContext toolContext = null!;
        string systemPrompt = string.Empty;
        string? setupError = null;
        try
        {
            var project = await projects.GetByIdAsync(projectId, cancellationToken)
                ?? throw new InvalidOperationException($"Project {projectId} not found.");
            systemPrompt = await BuildSystemPromptAsync(project, cancellationToken);
            chat = await chatClientFactory.CreateChatClientAsync(chatProvider.Id, cancellationToken);
            toolContext = new ImagesChatToolContext(projectId, conversation.Id, chatProvider.Id, visionReady, OnToolMutated);
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

        var history = await conversations.LoadMessagesAsync(conversation.Id, cancellationToken);
        var messages = new List<ChatMessage> { new(ChatRole.System, systemPrompt) };
        messages.AddRange(BuildModelHistory(history));
        if (turnAttachments.Count > 0)
            messages.Add(await BuildAttachedImagesMessageAsync(projectId, turnAttachments, visionReady, cancellationToken));

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
                        logger.LogError(ex, "Images chat streaming round failed");
                        streamFailed = true;
                        streamError = ex.Message;
                        break;
                    }

                    if (!hasNext) break;

                    var updatesToYield = new List<ImagesChatTurnUpdate>();
                    try
                    {
                        var contents = enumerator.Current?.Contents;
                        if (contents is null) continue;

                        foreach (var content in contents)
                        {
                            if (content is TextContent textContent && !string.IsNullOrEmpty(textContent.Text))
                            {
                                textBuilder.Append(textContent.Text);
                                updatesToYield.Add(new ImagesChatTextDelta(textContent.Text));
                            }
                            else
                            {
                                foreach (var toolUpdate in toolCallTracker.Process(content, textBuilder.Length))
                                {
                                    switch (toolUpdate)
                                    {
                                        case StreamingToolCallStartedUpdate started:
                                            updatesToYield.Add(new ImagesChatToolCallStarted(
                                                started.CallId,
                                                started.ToolName,
                                                started.ArgumentsJson,
                                                started.ArgumentsComplete));
                                            break;
                                        case StreamingToolCallArgumentsDeltaUpdate delta:
                                            updatesToYield.Add(new ImagesChatToolCallArgumentsDelta(
                                                delta.CallId,
                                                delta.ArgumentsDelta,
                                                delta.ArgumentsComplete));
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
                        logger.LogError(ex, "Images chat streaming update processing failed");
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
                    logger.LogError(ex, "Images chat streaming enumerator disposal failed");
                    streamFailed = true;
                    streamError ??= ex.Message;
                }
            }

            DrainMutated();

            if (cancelled)
            {
                activeAssistant.Content = textBuilder.ToString();
                activeAssistant.Status = ProjectImageMessageStatus.Cancelled;
                activeAssistant.ErrorMessage = "Cancelled by user.";
                await SafePersistAsync(activeAssistant);
                yield return new ImagesChatTurnError("Cancelled.", Cancelled: true);
                yield break;
            }

            if (streamFailed)
            {
                activeAssistant.Content = textBuilder.ToString();
                activeAssistant.Status = ProjectImageMessageStatus.Failed;
                activeAssistant.ErrorMessage = streamError;
                await SafePersistAsync(activeAssistant);
                yield return new ImagesChatTurnError(streamError ?? "LLM streaming failed.", Cancelled: false);
                yield break;
            }

            if (pendingCalls.Count == 0)
            {
                activeAssistant.Content = textBuilder.ToString();
                activeAssistant.Status = ProjectImageMessageStatus.Completed;
                await SafePersistAsync(activeAssistant);
                conversation.UpdatedAt = DateTime.UtcNow;
                await conversations.SaveChangesAsync(CancellationToken.None);
                yield return new ImagesChatAssistantMessageCompleted(activeAssistant.Id);
                yield break;
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
            activeAssistant.Status = ProjectImageMessageStatus.Completed;
            await SafePersistAsync(activeAssistant);

            messages.Add(new ChatMessage(ChatRole.Assistant, BuildAssistantToolCallContents(manifest)));

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
                var aiFunction = aiTools.OfType<AIFunction>().FirstOrDefault(function => function.Name == pendingCall.Name);
                var toolOutcome = aiFunction is null
                    ? new ToolInvocationOutcome($"Error: Unknown tool '{pendingCall.Name}'.", $"Unknown tool '{pendingCall.Name}'.", Cancelled: false)
                    : await InvokeToolAsync(aiFunction, pendingCall, cancellationToken);
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
                await conversations.AddMessageAsync(toolMessage, CancellationToken.None);
                await conversations.SaveChangesAsync(CancellationToken.None);
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
                messages.Add(await BuildModelOnlyImageMessageAsync(projectId, modelOnlyImagesForNextRound));

            if (iteration == maxIterations - 1)
            {
                yield return new ImagesChatTurnError($"Tool-call loop hit cap of {maxIterations} iterations without producing a final response.", Cancelled: false);
                yield break;
            }
        }
    }

    private async Task<string> BuildSystemPromptAsync(Project project, CancellationToken cancellationToken)
    {
        var assembly = await contextBuilder.BuildProjectAsync(project, ImagesWorkflowInstructions, cancellationToken);
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
        await conversations.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<ChatMessage> BuildAttachedImagesMessageAsync(
        Guid projectId,
        IReadOnlyList<ProjectImageChatAttachmentView> attachments,
        bool visionReady,
        CancellationToken cancellationToken)
    {
        var contents = new List<AIContent>
        {
            new TextContent("Images currently attached to Images Chat for this turn. Treat these as user-provided visual context. When generating or editing a continuity-related image, pass relevant attached image ids in referenceImageIds."),
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
            new TextContent("Project images returned by the previous tool call are attached as model-only visual context. Use these images when deciding whether further edits, layout actions, or future referenceImageIds are needed for visual continuity."),
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
            logger.LogWarning(ex, "Images chat tool '{Tool}' failed", pendingCall.Name);
            return new ToolInvocationOutcome($"Error: {ex.Message}", ex.Message, Cancelled: false);
        }
    }

    private async Task SafePersistAsync(ProjectImageMessage message)
    {
        try
        {
            conversations.UpdateMessage(message);
            await conversations.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist Images chat message {MessageId}", message.Id);
        }
    }

    private static IEnumerable<ChatMessage> BuildModelHistory(IEnumerable<ProjectImageMessage> history)
    {
        foreach (var message in history)
        {
            var chatMessage = ToModelHistoryMessage(message);
            if (chatMessage is not null)
                yield return chatMessage;
        }
    }

    private static ChatMessage? ToModelHistoryMessage(ProjectImageMessage message) => message.Role switch
    {
        ProjectImageMessageRole.System when !string.IsNullOrWhiteSpace(message.Content) => new ChatMessage(ChatRole.System, message.Content),
        ProjectImageMessageRole.User => new ChatMessage(ChatRole.User, message.Content),
        ProjectImageMessageRole.Assistant when !string.IsNullOrWhiteSpace(message.Content) => new ChatMessage(ChatRole.Assistant, message.Content),
        _ => null,
    };

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
