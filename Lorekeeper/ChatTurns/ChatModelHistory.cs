using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Lorekeeper.ChatTurns;

/// <summary>
/// Shared model-history policy for every user-facing chat surface. Persisted tool protocol
/// remains available to the transcript UI, but only durable conversational text crosses turns.
/// </summary>
public static class ChatModelHistory
{
    public static IEnumerable<ChatMessage> Build<TMessage>(
        IEnumerable<TMessage> messages,
        Func<TMessage, string> role,
        Func<TMessage, string> content)
    {
        foreach (var message in messages)
        {
            var projected = Project(role(message), content(message));
            if (projected is not null)
                yield return projected;
        }
    }

    public static ChatMessage? Project(string role, string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        if (string.Equals(role, "System", StringComparison.OrdinalIgnoreCase))
            return new ChatMessage(ChatRole.System, content);
        if (string.Equals(role, "User", StringComparison.OrdinalIgnoreCase))
            return new ChatMessage(ChatRole.User, content);
        if (string.Equals(role, "Assistant", StringComparison.OrdinalIgnoreCase))
            return new ChatMessage(ChatRole.Assistant, content);

        return null;
    }

    public static bool IsReplayedText(string role, string? content) =>
        Project(role, content) is not null;

    public static string FormatForTokenCount(IEnumerable<ChatMessage> messages)
    {
        var builder = new StringBuilder();
        foreach (var message in messages)
        {
            builder.Append(RoleLabel(message.Role)).AppendLine(":");
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextContent text when !string.IsNullOrEmpty(text.Text):
                        builder.AppendLine(text.Text);
                        break;
                    case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text):
                        builder.Append("Reasoning: ").AppendLine(reasoning.Text);
                        break;
                    case FunctionCallContent call:
                        builder.Append("Tool call: ").Append(call.Name).Append(' ').AppendLine(call.CallId);
                        builder.AppendLine(call.Arguments is null ? "{}" : JsonSerializer.Serialize(call.Arguments));
                        break;
                    case FunctionResultContent result:
                        builder.Append("Tool result: ").AppendLine(result.CallId);
                        builder.AppendLine(result.Result?.ToString() ?? string.Empty);
                        break;
                    case DataContent data:
                        builder.Append("Binary context: ").Append(data.Name).Append(' ')
                            .Append(data.MediaType).Append(' ').Append(data.Data.Length).AppendLine(" bytes");
                        break;
                }
            }
        }

        return builder.ToString();
    }

    private static string RoleLabel(ChatRole role)
    {
        if (role == ChatRole.System) return "System";
        if (role == ChatRole.User) return "User";
        if (role == ChatRole.Assistant) return "Assistant";
        if (role == ChatRole.Tool) return "Tool";
        return role.ToString();
    }
}
