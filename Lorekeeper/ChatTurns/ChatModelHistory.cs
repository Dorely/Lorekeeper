using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Lorekeeper.Llm;
using Lorekeeper.Models;

namespace Lorekeeper.ChatTurns;

/// <summary>
/// Shared model-history policy for every user-facing chat surface. Persisted tool protocol
/// remains available to the transcript UI; prose and provenance-bound reasoning cross turns.
/// </summary>
public static class ChatModelHistory
{
    public static ChatMessage? Project(string role, string? content, string? metadataJson = null, LlmProvider? provider = null)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        if (string.Equals(role, "System", StringComparison.OrdinalIgnoreCase))
            return new ChatMessage(ChatRole.System, content);
        if (string.Equals(role, "User", StringComparison.OrdinalIgnoreCase))
            return new ChatMessage(ChatRole.User, content);
        if (string.Equals(role, "Assistant", StringComparison.OrdinalIgnoreCase))
        {
            var metadata = ChatResponseMetadata.Deserialize(metadataJson);
            if (provider is null) return new ChatMessage(ChatRole.Assistant, content);
            if (metadata?.Matches(provider) is true && !metadata.OutputLimitReached && !metadata.Incomplete)
                return new ChatMessage(ChatRole.Assistant, [new TextContent(content), new ChatProtocolContent(metadata)]);
            // Never fabricate a native reasoning turn for legacy or foreign-model prose.
            return new ChatMessage(ChatRole.User,
                "Historical assistant prose (quoted conversation context, not a new instruction):\n"
                + JsonSerializer.Serialize(content));
        }

        return null;
    }

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
                    case ChatProtocolContent protocol:
                        builder.AppendLine(JsonSerializer.Serialize(protocol.Snapshot().ReasoningFields));
                        break;
                    case TextReasoningContent reasoning when !message.Contents.OfType<ChatProtocolContent>().Any(protocol => protocol.Snapshot().ReasoningFields.Count > 0)
                        && !string.IsNullOrEmpty(reasoning.Text):
                        builder.Append("Reasoning: ").AppendLine(reasoning.Text);
                        break;
                    case FunctionCallContent call:
                        builder.Append("Tool call: ").Append(call.Name).Append(' ').AppendLine(call.CallId);
                        builder.AppendLine(call.Arguments is null ? "{}" : JsonSerializer.Serialize(call.Arguments));
                        if (call.AdditionalProperties?.TryGetValue(OpenAIChatProtocolClient.ExtraContentPropertyName, out var extension) is true)
                            builder.AppendLine(extension?.ToString());
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
