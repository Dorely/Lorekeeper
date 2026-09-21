using System.Text;
using Lorekeeper.ChatTurns;
using Lorekeeper.Tokens;
using Lorekeeper.Models;
using Lorekeeper.Llm;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace Lorekeeper.Components.Chat;

public sealed record ChatTranscriptTokenMessage(
    string Role,
    string Content,
    string ToolCallsJson = "[]",
    string? ToolCallId = null,
    string? ToolName = null,
    string? ErrorMessage = null,
    string? ProtocolReasoning = null);

public readonly record struct ChatTranscriptTokenCount(int TokenCount, bool IsExact, bool IsSettled)
{
    public static ChatTranscriptTokenCount Empty { get; } = new(0, true, true);

    public int RemainingTokens(int maximumTokens) =>
        Math.Max(0, Math.Max(1, maximumTokens) - TokenCount);

    public bool ShouldSuggestReset(int maximumTokens) =>
        IsSettled && (long)TokenCount * 2 > Math.Max(1, maximumTokens);

    public string Text(string unitLabel, int maximumTokens)
    {
        var prefix = IsExact ? string.Empty : "~";
        var resolvedMaximum = Math.Max(1, maximumTokens);
        return $"{prefix}{TokenCount:N0} / {resolvedMaximum:N0} {unitLabel} · {prefix}{RemainingTokens(resolvedMaximum):N0} left";
    }

    public string Title(string subjectLabel, int maximumTokens, string? modelId)
    {
        var resolvedMaximum = Math.Max(1, maximumTokens);
        var countLabel = IsExact
            ? $"Current {subjectLabel}"
            : $"Current {subjectLabel} is estimated";
        var modelLabel = string.IsNullOrWhiteSpace(modelId) ? "the active model" : modelId.Trim();
        var remainingLabel = IsExact
            ? $"{RemainingTokens(resolvedMaximum):N0}"
            : $"about {RemainingTokens(resolvedMaximum):N0}";
        return $"{countLabel}. Advisory maximum for {modelLabel}: {resolvedMaximum:N0} tokens; {remainingLabel} remaining.";
    }
}

public static class ChatTranscriptTokenCounter
{
    public static IEnumerable<ChatTranscriptTokenMessage> ModelReplayMessages<TMessage>(
        IEnumerable<TMessage> messages,
        Func<TMessage, string> role,
        Func<TMessage, string> content,
        Func<TMessage, string?>? metadata = null,
        LlmProvider? provider = null)
    {
        foreach (var message in messages)
        {
            var projectedRole = role(message);
            var projectedContent = content(message);
            var replay = ChatModelHistory.Project(projectedRole, projectedContent, metadata?.Invoke(message), provider);
            if (replay is not null)
                yield return new ChatTranscriptTokenMessage(replay.Role.ToString(),
                    string.Concat(replay.Contents.OfType<TextContent>().Select(text => text.Text)),
                    ProtocolReasoning: replay.Contents.OfType<ChatProtocolContent>().FirstOrDefault() is { } protocol
                        ? JsonSerializer.Serialize(protocol.Snapshot().ReasoningFields) : null);
        }
    }

    public static ChatTranscriptTokenCount Count<TMessage>(
        ITokenCounter tokenCounter,
        IEnumerable<TMessage> messages,
        Func<TMessage, ChatTranscriptTokenMessage> projectMessage,
        ChatLiveTurn? live,
        string? pendingUserText = null,
        string? systemPrompt = null)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            sb.AppendLine("System:");
            sb.AppendLine(systemPrompt);
        }

        foreach (var message in messages)
            AppendMessage(sb, projectMessage(message));

        if (!string.IsNullOrWhiteSpace(pendingUserText))
        {
            sb.AppendLine("User:");
            sb.AppendLine(pendingUserText);
        }

        ChatTranscriptHelpers.AppendLiveTurnForTokenCount(sb, live);
        var result = tokenCounter.Count(sb.ToString());
        var isSettled = live is null && string.IsNullOrWhiteSpace(pendingUserText);
        return new ChatTranscriptTokenCount(result.TokenCount, result.IsExact, isSettled);
    }

    private static void AppendMessage(StringBuilder sb, ChatTranscriptTokenMessage message)
    {
        sb.Append(message.Role).AppendLine(":");
        if (!string.IsNullOrEmpty(message.Content))
            sb.AppendLine(message.Content);
        if (!string.IsNullOrEmpty(message.ProtocolReasoning))
            sb.AppendLine(message.ProtocolReasoning);
        if (!string.IsNullOrWhiteSpace(message.ToolCallsJson) && message.ToolCallsJson != "[]")
        {
            sb.AppendLine("Tool calls:");
            sb.AppendLine(message.ToolCallsJson);
        }
        if (!string.IsNullOrWhiteSpace(message.ToolName) || !string.IsNullOrWhiteSpace(message.ToolCallId))
            sb.Append("Tool: ").Append(message.ToolName).Append(' ').AppendLine(message.ToolCallId);
        if (!string.IsNullOrWhiteSpace(message.ErrorMessage))
            sb.Append("Error: ").AppendLine(message.ErrorMessage);
    }
}
