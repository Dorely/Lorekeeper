namespace Lorekeeper.Models;

/// <summary>
/// One message in an <see cref="OutlineConversation"/>. Roles map directly to
/// <c>Microsoft.Extensions.AI.ChatRole</c> so the persisted history can be replayed
/// into the LLM verbatim. Tool calls emitted by an assistant turn are serialized into
/// <see cref="ToolCallsJson"/>; a separate <see cref="OutlineMessageRole.Tool"/> row is
/// written for each tool result.
/// </summary>
public class OutlineMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ConversationId { get; set; }
    public OutlineConversation Conversation { get; set; } = null!;

    /// <summary>Monotonically increasing within a conversation. Used for stable replay ordering.</summary>
    public int Order { get; set; }

    public OutlineMessageRole Role { get; set; }

    /// <summary>Plain-text content. For an assistant row this is the streamed text; for a user row, the user's input; for a tool row, the JSON-serialized tool result (or the error string).</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Versioned provider protocol, provenance, finish reason, and usage. Not display text.</summary>
    public string? ResponseMetadataJson { get; set; }

    /// <summary>JSON array of <c>{ callId, name, argumentsJson }</c> for assistant rows that emitted tool calls. Empty array otherwise.</summary>
    public string ToolCallsJson { get; set; } = "[]";

    /// <summary>Displayed reasoning. Provider protocol is stored separately in ResponseMetadataJson.</summary>
    public string Reasoning { get; set; } = string.Empty;

    /// <summary>Set on <see cref="OutlineMessageRole.Tool"/> rows; matches the assistant's emitted call id.</summary>
    public string? ToolCallId { get; set; }

    /// <summary>Set on <see cref="OutlineMessageRole.Tool"/> rows; the tool function name.</summary>
    public string? ToolName { get; set; }

    /// <summary>Lifecycle state for assistant rows; ignored for other roles.</summary>
    public OutlineMessageStatus Status { get; set; } = OutlineMessageStatus.Completed;

    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum OutlineMessageRole
{
    System,
    User,
    Assistant,
    Tool,
}

public enum OutlineMessageStatus
{
    Pending,
    Completed,
    Failed,
    Cancelled,
}
