using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Lorekeeper.Context;
using Lorekeeper.Images;
using Lorekeeper.Llm;
using Lorekeeper.Models;
using Lorekeeper.Persistence.Repositories;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Lorekeeper.ImagesChat;

public sealed class ImagesChatService(
    IProjectRepository projects,
    IProjectImageConversationRepository conversations,
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
        var nextOrder = await conversations.GetMaxOrderAsync(conversation.Id, cancellationToken) + 1;
        await conversations.AddMessageAsync(new ProjectImageMessage
        {
            ConversationId = conversation.Id,
            Order = nextOrder++,
            Role = ProjectImageMessageRole.User,
            Content = userText.Trim(),
            Status = ProjectImageMessageStatus.Completed,
        }, cancellationToken);
        conversation.UpdatedAt = DateTime.UtcNow;
        await conversations.SaveChangesAsync(cancellationToken);

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

    private void OnToolMutated() => _mutatedSinceYield = true;

    private bool DrainMutated()
    {
        if (!_mutatedSinceYield) return false;
        _mutatedSinceYield = false;
        return true;
    }

    private async Task PersistVisualsAsync(Guid messageId, string toolCallId, IReadOnlyList<ImagesChatVisualAttachment> visuals)
    {
        if (visuals.Count == 0)
            return;

        await conversations.AddMessageVisualsAsync(visuals.Select((visual, index) => new ProjectImageMessageVisual
        {
            MessageId = messageId,
            SortOrder = index,
            ToolCallId = toolCallId,
            Title = visual.Title,
            Caption = visual.Caption,
            SourceKind = visual.SourceKind,
            SourceRefId = visual.SourceRefId,
            ContentType = visual.ContentType,
            FileName = visual.FileName,
            Width = visual.Width,
            Height = visual.Height,
        }), CancellationToken.None);
        await conversations.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<ChatMessage> BuildModelOnlyImageMessageAsync(Guid projectId, IReadOnlyList<ProjectImageView> images)
    {
        var contents = new List<AIContent>
        {
            new TextContent("Generated image outputs from the previous tool call are attached for visual context. Use these images when deciding whether further edits are needed."),
        };

        foreach (var image in images)
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
