using Lorekeeper.Tokens;
using Microsoft.Extensions.AI;

namespace Lorekeeper.ChatTurns;

public sealed record ChatCompactionResult(
    string CallId,
    int TokenCount,
    int MaximumInputTokens,
    string TokenCountMethod,
    int DroppedMessageCount,
    int DroppedContentCount);

public interface IChatContextCompactionService
{
    ChatCompactionResult? TryCompact(IList<ChatMessage> messages, string? modelId);
}

public static class ChatContextCompaction
{
    public const string ToolName = "Chat Compacted";
    public const string Notice = "Tool results were dropped from the active context. Do not assume you still know IDs or other details from earlier tool calls; look them up again before relying on them.";
    public const string EmptyArgumentsJson = "{}";

    private const string ToolContextMarker = "Lorekeeper.ChatTurns.ToolContext";

    public static ChatMessage MarkToolContext(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        (message.AdditionalProperties ??= new AdditionalPropertiesDictionary())[ToolContextMarker] = true;
        return message;
    }

    public static bool IsToolContext(ChatMessage message) =>
        message.AdditionalProperties is not null
        && message.AdditionalProperties.TryGetValue(ToolContextMarker, out var value)
        && value is true;
}

internal sealed class ChatContextCompactionService(
    ITokenCounter tokenCounter,
    ChatTokenLimitResolver tokenLimitResolver) : IChatContextCompactionService
{
    public ChatCompactionResult? TryCompact(IList<ChatMessage> messages, string? modelId)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var tokenCount = tokenCounter.Count(
            ChatModelHistory.FormatForTokenCount(messages),
            new TokenCountRequest(modelId));
        var maximumInputTokens = Math.Max(1, tokenLimitResolver.Resolve(modelId));
        if ((long)tokenCount.TokenCount * 100 < (long)maximumInputTokens * 90)
            return null;

        var retainedMessages = new List<ChatMessage>(messages.Count + 1);
        var droppedMessageCount = 0;
        var droppedContentCount = 0;

        foreach (var message in messages)
        {
            if (message.Role == ChatRole.Tool || ChatContextCompaction.IsToolContext(message))
            {
                droppedMessageCount++;
                droppedContentCount += message.Contents.Count;
                continue;
            }

            var retainedContents = message.Contents
                .Where(content => content is not FunctionCallContent && content is not FunctionResultContent)
                .ToList();
            var removedContents = message.Contents.Count - retainedContents.Count;
            if (removedContents > 0)
            {
                droppedContentCount += removedContents;
                message.Contents = retainedContents;
            }

            if (message.Contents.Count == 0 && retainedContents.Count == 0 && removedContents > 0)
            {
                droppedMessageCount++;
                continue;
            }

            retainedMessages.Add(message);
        }

        if (droppedContentCount == 0 && droppedMessageCount == 0)
            return null;

        messages.Clear();
        foreach (var message in retainedMessages)
            messages.Add(message);
        messages.Add(new ChatMessage(ChatRole.User, ChatContextCompaction.Notice));

        return new ChatCompactionResult(
            CallId: $"chat-compaction-{Guid.NewGuid():N}",
            TokenCount: tokenCount.TokenCount,
            MaximumInputTokens: maximumInputTokens,
            TokenCountMethod: tokenCount.Method,
            DroppedMessageCount: droppedMessageCount,
            DroppedContentCount: droppedContentCount);
    }
}
