using System.Text;
using Lorekeeper.ChatTurns;
using Lorekeeper.Tokens;

namespace Lorekeeper.Components.Chat;

public sealed record ChatTranscriptTokenMessage(
    string Role,
    string Content,
    string ToolCallsJson = "[]",
    string? ToolCallId = null,
    string? ToolName = null,
    string? ErrorMessage = null);

public readonly record struct ChatTranscriptTokenCount(int TokenCount, bool IsExact)
{
    public static ChatTranscriptTokenCount Empty { get; } = new(0, true);

    public string Text(string unitLabel) =>
        $"{(IsExact ? string.Empty : "~")}{TokenCount:N0} {unitLabel}";

    public string Title(string subjectLabel) => IsExact
        ? $"Current {subjectLabel}"
        : $"Current {subjectLabel} is estimated";
}

public static class ChatTranscriptTokenCounter
{
    public static IEnumerable<ChatTranscriptTokenMessage> ModelReplayMessages<TMessage>(
        IEnumerable<TMessage> messages,
        Func<TMessage, string> role,
        Func<TMessage, string> content)
    {
        foreach (var message in messages)
        {
            var projectedRole = role(message);
            var projectedContent = content(message);
            if (ChatModelHistory.IsReplayedText(projectedRole, projectedContent))
                yield return new ChatTranscriptTokenMessage(projectedRole, projectedContent);
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
        return new ChatTranscriptTokenCount(result.TokenCount, result.IsExact);
    }

    private static void AppendMessage(StringBuilder sb, ChatTranscriptTokenMessage message)
    {
        sb.Append(message.Role).AppendLine(":");
        if (!string.IsNullOrEmpty(message.Content))
            sb.AppendLine(message.Content);
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
