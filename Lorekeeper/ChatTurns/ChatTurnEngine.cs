using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lorekeeper.Llm;
using Microsoft.Extensions.AI;

namespace Lorekeeper.ChatTurns;

public abstract record ChatRoundUpdate;

public sealed record ChatRoundTextDelta(string Text) : ChatRoundUpdate;

public sealed record ChatRoundToolCallStarted(
    string CallId,
    string ToolName,
    string ArgumentsJson,
    bool ArgumentsComplete) : ChatRoundUpdate;

public sealed record ChatRoundToolCallArgumentsDelta(
    string CallId,
    string ArgumentsDelta,
    bool ArgumentsComplete) : ChatRoundUpdate;

public sealed record ChatRoundCompleted(
    string Text,
    IReadOnlyList<ChatPendingToolCall> ToolCalls) : ChatRoundUpdate;

public sealed record ChatRoundFailed(string Message, bool Cancelled, string Text) : ChatRoundUpdate;

public sealed record ChatPendingToolCall(
    FunctionCallContent Content,
    string CallId,
    string Name,
    string ArgumentsJson,
    int TextOffset);

public sealed record ChatToolCallManifest(
    string CallId,
    string Name,
    string ArgumentsJson,
    int? TextOffset = null)
{
    private static readonly HashSet<string> StagedPayloadTools = new(StringComparer.Ordinal)
    {
        "stage_page_composition",
        "stage_cover_composition",
        "stage_publication_cover_composition",
        "stage_page_composition_semantic",
        "stage_page_composition_workspace",
    };

    public static ChatToolCallManifest From(ChatPendingToolCall call) =>
        new(call.CallId, call.Name, CompactPersistedArguments(call.Name, call.ArgumentsJson), call.TextOffset);

    public static string CompactPersistedArguments(string toolName, string argumentsJson)
    {
        if (!StagedPayloadTools.Contains(toolName))
            return argumentsJson;

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;
            var summary = new Dictionary<string, object?>
            {
                ["payloadSha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(argumentsJson))),
                ["payloadOmitted"] = true,
            };
            foreach (var name in new[] { "variantId", "compositionId", "editionId", "expectedRevision" })
            {
                if (root.TryGetProperty(name, out var value))
                    summary[name] = value.Clone();
            }
            return JsonSerializer.Serialize(summary);
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(new
            {
                payloadSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(argumentsJson))),
                payloadOmitted = true,
                malformed = true,
            });
        }
    }
}

public sealed record ChatToolInvocationOutcome(string Result, string? Error, bool Cancelled);

/// <summary>
/// Shared model protocol for chat surfaces. Feature services retain only their setup,
/// persistence mapping, tool-specific progress hooks, and terminal feature behavior.
/// </summary>
public sealed class ChatTurnEngine(
    ILogger<ChatTurnEngine> logger,
    IChatContextCompactionService contextCompaction)
{
    public const string CompactionToolName = ChatContextCompaction.ToolName;
    public const string CompactionNotice = ChatContextCompaction.Notice;

    public static string ToolLoopLimitError(int maxIterations) =>
        $"Tool-call loop hit cap of {maxIterations} iterations without producing a final response.";

    public ChatCompactionResult? TryCompactContext(IList<ChatMessage> messages, string? modelId) =>
        contextCompaction.TryCompact(messages, modelId);

    public static ChatMessage MarkToolContextMessage(ChatMessage message) =>
        ChatContextCompaction.MarkToolContext(message);

    public async Task AddMessageAsync<TMessage>(
        IChatMessageStore<TMessage> store,
        TMessage message,
        CancellationToken cancellationToken)
    {
        await store.AddMessageAsync(message, cancellationToken);
        await store.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateMessageAsync<TMessage>(
        IChatMessageStore<TMessage> store,
        TMessage message,
        CancellationToken cancellationToken)
    {
        store.UpdateMessage(message);
        await store.SaveChangesAsync(cancellationToken);
    }

    public async IAsyncEnumerable<ChatRoundUpdate> StreamRoundAsync(
        IChatClient chat,
        IReadOnlyList<ChatMessage> messages,
        ChatOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var textBuilder = new StringBuilder();
        var pendingCalls = new List<ChatPendingToolCall>();
        var tracker = new StreamingToolCallTracker();
        string? failure = null;
        var cancelled = false;
        var enumerator = chat.GetStreamingResponseAsync(messages, options, cancellationToken)
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
                    logger.LogError(ex, "Chat streaming round failed");
                    failure = ex.Message;
                    break;
                }

                if (!hasNext)
                    break;

                var contents = enumerator.Current?.Contents;
                if (contents is null)
                    continue;

                var updates = new List<ChatRoundUpdate>();
                try
                {
                    foreach (var content in contents)
                    {
                        if (content is TextContent text && !string.IsNullOrEmpty(text.Text))
                        {
                            textBuilder.Append(text.Text);
                            updates.Add(new ChatRoundTextDelta(text.Text));
                            continue;
                        }

                        foreach (var toolUpdate in tracker.Process(content, textBuilder.Length))
                        {
                            switch (toolUpdate)
                            {
                                case StreamingToolCallStartedUpdate started:
                                    updates.Add(new ChatRoundToolCallStarted(
                                        started.CallId,
                                        started.ToolName,
                                        started.ArgumentsJson,
                                        started.ArgumentsComplete));
                                    break;
                                case StreamingToolCallArgumentsDeltaUpdate delta:
                                    updates.Add(new ChatRoundToolCallArgumentsDelta(
                                        delta.CallId,
                                        delta.ArgumentsDelta,
                                        delta.ArgumentsComplete));
                                    break;
                                case StreamingToolCallReadyUpdate ready:
                                    pendingCalls.Add(new ChatPendingToolCall(
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
                catch (Exception ex)
                {
                    logger.LogError(ex, "Chat streaming tool-call processing failed");
                    failure = ex.Message;
                    break;
                }

                foreach (var update in updates)
                    yield return update;
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
                logger.LogWarning(ex, "Chat streaming enumerator disposal failed");
                failure ??= ex.Message;
            }
        }

        if (cancelled)
        {
            yield return new ChatRoundFailed("Cancelled.", Cancelled: true, textBuilder.ToString());
            yield break;
        }
        if (failure is not null)
        {
            yield return new ChatRoundFailed(failure, Cancelled: false, textBuilder.ToString());
            yield break;
        }

        yield return new ChatRoundCompleted(textBuilder.ToString(), pendingCalls);
    }

    public async Task<ChatToolInvocationOutcome> InvokeToolAsync(
        IList<AITool> tools,
        ChatPendingToolCall call,
        CancellationToken cancellationToken) =>
        await InvokeToolAsync(
            tools.OfType<AIFunction>().FirstOrDefault(candidate => candidate.Name == call.Name),
            call,
            cancellationToken);

    public async Task<ChatToolInvocationOutcome> InvokeToolAsync(
        AIFunction? function,
        ChatPendingToolCall call,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolvedFunction = function
                ?? throw new InvalidOperationException($"Unknown tool '{call.Name}'.");
            var result = await resolvedFunction.InvokeAsync(
                ToolCallArguments.Create(call.Content.Arguments, call.ArgumentsJson),
                cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                return new ChatToolInvocationOutcome(string.Empty, Error: null, Cancelled: true);

            return new ChatToolInvocationOutcome(result?.ToString() ?? string.Empty, Error: null, Cancelled: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ChatToolInvocationOutcome(string.Empty, Error: null, Cancelled: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Chat tool '{Tool}' failed", call.Name);
            return new ChatToolInvocationOutcome($"Error: {ex.Message}", ex.Message, Cancelled: false);
        }
    }

    public static List<AIContent> BuildAssistantContents(
        string text,
        IReadOnlyList<ChatPendingToolCall> calls)
    {
        if (calls.Count == 0)
            return string.IsNullOrEmpty(text) ? [new TextContent(string.Empty)] : [new TextContent(text)];

        var contents = new List<AIContent>();
        var cursor = 0;
        foreach (var item in calls
            .Select((call, index) => new { Call = call, Index = index })
            .OrderBy(item => item.Call.TextOffset)
            .ThenBy(item => item.Index))
        {
            var offset = Math.Clamp(item.Call.TextOffset, 0, text.Length);
            if (offset > cursor)
            {
                contents.Add(new TextContent(text[cursor..offset]));
                cursor = offset;
            }
            contents.Add(item.Call.Content);
        }

        if (cursor < text.Length)
            contents.Add(new TextContent(text[cursor..]));
        if (contents.Count == 0)
            contents.Add(new TextContent(string.Empty));
        return contents;
    }
}
