using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Lorekeeper.Llm;
using Lorekeeper.Models;
using Lorekeeper.Persistence.Repositories;
using Lorekeeper.Search;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Lorekeeper.Research;

public sealed class ResearchService(
    IProjectRepository projects,
    IResearchConversationRepository conversations,
    ISearchProviderService searchProviders,
    ILlmProviderService providerService,
    IChatClientFactory chatClientFactory,
    ResearchTools tools,
    IOptions<AgentOptions> options,
    ILogger<ResearchService> logger) : IResearchService
{
    public const string ResearchSystemPrompt = """
        You are Lorekeeper's Research Mode for a long-form fiction project.

        Your job is to autonomously research a user-provided topic on the public web, discover useful sources, read the pages you find, follow relevant links when they look promising, and stage only pages worth ingesting into the project.

        How to work:
        - Use web_search for open-ended topics. Do not claim web knowledge from memory when search would answer it.
        - Read pages before judging them. Never stage an unread page.
        - Use follow_page_links or read_webpage to follow links from read pages when the link text or surrounding result suggests stronger source material.
        - Stage pages after reading them when they contain information likely to help project memory, canon, lore, setting details, timelines, characters, factions, places, or terminology.
        - Do not ask the user to approve individual pages before staging; staging is your research output. The user queues ingestion jobs later.
        - When a page cannot be accessed, report that briefly and move on.
        - End each turn with a concise report: what you searched, what you read, what you staged, and what you would investigate next.
        """;

    private const string InitialAssistantGreeting =
        "What should I research? Give me a topic, question, or canon area and I’ll go find useful source pages to stage.";

    public async Task<ResearchConversation> GetOrCreateAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var existing = await conversations.GetByProjectIdAsync(projectId, cancellationToken);
        if (existing is not null) return existing;

        _ = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");

        var conversation = new ResearchConversation { ProjectId = projectId };
        await conversations.AddConversationAsync(conversation, cancellationToken);
        await conversations.AddMessageAsync(new ResearchMessage
        {
            ConversationId = conversation.Id,
            Order = 0,
            Role = ResearchMessageRole.Assistant,
            Content = InitialAssistantGreeting,
            Status = ResearchMessageStatus.Completed,
        }, cancellationToken);
        await conversations.SaveChangesAsync(cancellationToken);
        return conversation;
    }

    public async Task<IReadOnlyList<ResearchMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        await conversations.LoadMessagesAsync(conversationId, cancellationToken);

    public async Task ResetAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var existing = await conversations.GetByProjectIdAsync(projectId, cancellationToken);
        if (existing is null) return;

        conversations.RemoveConversation(existing);
        await conversations.SaveChangesAsync(cancellationToken);
    }

    public async IAsyncEnumerable<ResearchTurnUpdate> SendAsync(
        Guid projectId,
        string userText,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userText))
            throw new ArgumentException("Message cannot be empty.", nameof(userText));

        var conversation = await GetOrCreateAsync(projectId, cancellationToken);
        var nextOrder = await conversations.GetMaxOrderAsync(conversation.Id, cancellationToken) + 1;
        await conversations.AddMessageAsync(new ResearchMessage
        {
            ConversationId = conversation.Id,
            Order = nextOrder++,
            Role = ResearchMessageRole.User,
            Content = userText.Trim(),
            Status = ResearchMessageStatus.Completed,
        }, cancellationToken);
        conversation.UpdatedAt = DateTime.UtcNow;
        await conversations.SaveChangesAsync(cancellationToken);

        IChatClient chat = null!;
        IList<AITool> aiTools = null!;
        string? setupError = null;
        try
        {
            _ = await projects.GetByIdAsync(projectId, cancellationToken)
                ?? throw new InvalidOperationException($"Project {projectId} not found.");
            if (!await searchProviders.HasActiveProviderAsync(cancellationToken))
                throw new InvalidOperationException("No active search provider is configured.");
            var defaultProvider = await providerService.GetDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException("No default LLM provider configured.");
            chat = await chatClientFactory.CreateChatClientAsync(defaultProvider.Id, cancellationToken);
            aiTools = tools.Build(new ResearchToolContext(projectId, conversation.Id));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Research turn setup failed for project {ProjectId}", projectId);
            await PersistFailedAssistantAsync(conversation.Id, nextOrder, ex.Message);
            setupError = ex.Message;
        }

        if (setupError is not null)
        {
            yield return new ResearchTurnError(setupError, Cancelled: false);
            yield break;
        }

        var chatOptions = new ChatOptions
        {
            Tools = aiTools,
            ToolMode = ChatToolMode.Auto,
        };
        var history = await conversations.LoadMessagesAsync(conversation.Id, cancellationToken);
        var messages = new List<ChatMessage> { new(ChatRole.System, ResearchSystemPrompt) };
        messages.AddRange(history.Select(ToChatMessage));

        var maxIterations = Math.Max(1, options.Value.MaxToolIterations);
        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            var activeAssistant = new ResearchMessage
            {
                ConversationId = conversation.Id,
                Order = nextOrder++,
                Role = ResearchMessageRole.Assistant,
                Content = string.Empty,
                Status = ResearchMessageStatus.Pending,
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
                        logger.LogError(ex, "Research streaming round failed");
                        streamFailed = true;
                        streamError = ex.Message;
                        break;
                    }

                    if (!hasNext) break;

                    foreach (var content in enumerator.Current.Contents)
                    {
                        if (content is TextContent textContent && !string.IsNullOrEmpty(textContent.Text))
                        {
                            textBuilder.Append(textContent.Text);
                            yield return new ResearchTextDelta(textContent.Text);
                        }
                        else
                        {
                            foreach (var toolUpdate in toolCallTracker.Process(content, textBuilder.Length))
                            {
                                switch (toolUpdate)
                                {
                                    case StreamingToolCallStartedUpdate started:
                                        yield return new ResearchToolCallStarted(started.CallId, started.ToolName, started.ArgumentsJson, started.ArgumentsComplete);
                                        break;
                                    case StreamingToolCallArgumentsDeltaUpdate delta:
                                        yield return new ResearchToolCallArgumentsDelta(delta.CallId, delta.ArgumentsDelta, delta.ArgumentsComplete);
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
            }
            finally
            {
                await enumerator.DisposeAsync();
            }

            if (cancelled)
            {
                activeAssistant.Content = textBuilder.ToString();
                activeAssistant.Status = ResearchMessageStatus.Cancelled;
                activeAssistant.ErrorMessage = "Cancelled by user.";
                await SafePersistAsync(activeAssistant);
                yield return new ResearchTurnError("Cancelled.", Cancelled: true);
                yield break;
            }

            if (streamFailed)
            {
                activeAssistant.Content = textBuilder.ToString();
                activeAssistant.Status = ResearchMessageStatus.Failed;
                activeAssistant.ErrorMessage = streamError;
                await SafePersistAsync(activeAssistant);
                yield return new ResearchTurnError(streamError ?? "Research streaming failed.", Cancelled: false);
                yield break;
            }

            if (pendingCalls.Count == 0)
            {
                activeAssistant.Content = textBuilder.ToString();
                activeAssistant.Status = ResearchMessageStatus.Completed;
                await SafePersistAsync(activeAssistant);
                conversation.UpdatedAt = DateTime.UtcNow;
                await conversations.SaveChangesAsync(CancellationToken.None);
                yield return new ResearchAssistantMessageCompleted(activeAssistant.Id);
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
            activeAssistant.Status = ResearchMessageStatus.Completed;
            await SafePersistAsync(activeAssistant);
            messages.Add(new ChatMessage(ChatRole.Assistant, BuildAssistantContents(textBuilder.ToString(), manifest)));

            var resultContents = new List<AIContent>();
            foreach (var pendingCall in pendingCalls)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    yield return new ResearchTurnError("Cancelled.", Cancelled: true);
                    yield break;
                }

                var sw = Stopwatch.StartNew();
                string? toolResult = null;
                string? toolError = null;
                var toolCancelled = false;
                try
                {
                    var aiFn = aiTools.OfType<AIFunction>().FirstOrDefault(function => function.Name == pendingCall.Name)
                        ?? throw new InvalidOperationException($"Unknown tool '{pendingCall.Name}'.");
                    var invokeResult = await aiFn.InvokeAsync(
                        ToolCallArguments.Create(pendingCall.Content.Arguments, pendingCall.ArgumentsJson),
                        cancellationToken);
                    toolResult = invokeResult?.ToString() ?? string.Empty;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    toolCancelled = true;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Research tool '{Tool}' failed", pendingCall.Name);
                    toolError = ex.Message;
                    toolResult = $"Error: {ex.Message}";
                }
                sw.Stop();

                if (toolCancelled)
                {
                    yield return new ResearchTurnError("Cancelled.", Cancelled: true);
                    yield break;
                }

                var toolMessage = new ResearchMessage
                {
                    ConversationId = conversation.Id,
                    Order = nextOrder++,
                    Role = ResearchMessageRole.Tool,
                    Content = toolResult ?? string.Empty,
                    ToolCallId = pendingCall.CallId,
                    ToolName = pendingCall.Name,
                    Status = toolError is null ? ResearchMessageStatus.Completed : ResearchMessageStatus.Failed,
                    ErrorMessage = toolError,
                };
                await conversations.AddMessageAsync(toolMessage, CancellationToken.None);
                await conversations.SaveChangesAsync(CancellationToken.None);

                resultContents.Add(new FunctionResultContent(pendingCall.CallId, toolResult ?? string.Empty));
                yield return new ResearchToolCallCompleted(
                    pendingCall.CallId,
                    pendingCall.Name,
                    toolError is null ? toolResult : null,
                    toolError,
                    sw.Elapsed.TotalMilliseconds);
            }

            messages.Add(new ChatMessage(ChatRole.Tool, resultContents));

            if (iteration == maxIterations - 1)
            {
                yield return new ResearchTurnError(
                    $"Research tool-call loop hit cap of {maxIterations} iterations without producing a final response.",
                    Cancelled: false);
                yield break;
            }
        }
    }

    private static ChatMessage ToChatMessage(ResearchMessage message) => message.Role switch
    {
        ResearchMessageRole.System => new ChatMessage(ChatRole.System, message.Content),
        ResearchMessageRole.User => new ChatMessage(ChatRole.User, message.Content),
        ResearchMessageRole.Assistant => BuildAssistantReplay(message),
        ResearchMessageRole.Tool => new ChatMessage(ChatRole.Tool, [new FunctionResultContent(message.ToolCallId ?? string.Empty, message.Content)]),
        _ => new ChatMessage(ChatRole.User, message.Content),
    };

    private static ChatMessage BuildAssistantReplay(ResearchMessage message)
    {
        var calls = ReadPersistedToolCalls(message.ToolCallsJson);
        var contents = calls.Count == 0
            ? BuildTextOnlyAssistantContents(message.Content)
            : BuildAssistantContents(message.Content, calls);

        return new ChatMessage(ChatRole.Assistant, contents);
    }

    private static List<AIContent> BuildTextOnlyAssistantContents(string text)
    {
        var contents = new List<AIContent>();
        if (!string.IsNullOrEmpty(text)) contents.Add(new TextContent(text));
        if (contents.Count == 0) contents.Add(new TextContent(string.Empty));
        return contents;
    }

    private static List<AIContent> BuildAssistantContents(string text, IReadOnlyList<PersistedToolCall> calls)
    {
        if (calls.Count == 0) return BuildTextOnlyAssistantContents(text);
        if (calls.Any(call => call.TextOffset is null))
        {
            var fallbackContents = BuildTextOnlyAssistantContents(text);
            foreach (var call in calls)
                fallbackContents.Add(ToFunctionCallContent(call));
            return fallbackContents;
        }

        var contents = new List<AIContent>();
        var cursor = 0;
        foreach (var item in calls.Select((call, index) => new { Call = call, Index = index })
                                  .OrderBy(item => item.Call.TextOffset!.Value)
                                  .ThenBy(item => item.Index))
        {
            var offset = Math.Clamp(item.Call.TextOffset!.Value, 0, text.Length);
            if (offset > cursor)
            {
                contents.Add(new TextContent(text[cursor..offset]));
                cursor = offset;
            }
            contents.Add(ToFunctionCallContent(item.Call));
        }

        if (cursor < text.Length)
            contents.Add(new TextContent(text[cursor..]));
        if (contents.Count == 0) contents.Add(new TextContent(string.Empty));
        return contents;
    }

    private static List<PersistedToolCall> ReadPersistedToolCalls(string toolCallsJson)
    {
        if (string.IsNullOrWhiteSpace(toolCallsJson) || toolCallsJson == "[]") return [];
        try { return JsonSerializer.Deserialize<List<PersistedToolCall>>(toolCallsJson) ?? []; }
        catch { return []; }
    }

    private static FunctionCallContent ToFunctionCallContent(PersistedToolCall call)
    {
        var args = ToolCallArguments.ParseObjectOrNull(call.ArgumentsJson);
        return new FunctionCallContent(call.CallId, call.Name, args);
    }

    private async Task PersistFailedAssistantAsync(Guid conversationId, int order, string error)
    {
        try
        {
            await conversations.AddMessageAsync(new ResearchMessage
            {
                ConversationId = conversationId,
                Order = order,
                Role = ResearchMessageRole.Assistant,
                Status = ResearchMessageStatus.Failed,
                ErrorMessage = error,
            }, CancellationToken.None);
            await conversations.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist Research setup failure");
        }
    }

    private async Task SafePersistAsync(ResearchMessage message)
    {
        try
        {
            conversations.UpdateMessage(message);
            await conversations.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist Research message {MessageId}", message.Id);
        }
    }

    private sealed record PendingToolCall(
        FunctionCallContent Content,
        string CallId,
        string Name,
        string ArgumentsJson,
        int TextOffset);

    private sealed record PersistedToolCall(string CallId, string Name, string ArgumentsJson, int? TextOffset = null);
}