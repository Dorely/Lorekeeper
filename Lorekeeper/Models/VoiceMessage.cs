namespace Lorekeeper.Models;

/// <summary>
/// One message in a project's Voice conversation. Mirrors the outline
/// chat message shape so assistant tool calls can be replayed into the LLM
/// with separate tool-result rows.
/// </summary>
public class VoiceMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ConversationId { get; set; }
    public VoiceConversation Conversation { get; set; } = null!;

    public int Order { get; set; }

    public VoiceMessageRole Role { get; set; }

    public string Content { get; set; } = string.Empty;

    /// <summary>Versioned provider protocol, provenance, finish reason, and usage. Not display text.</summary>
    public string? ResponseMetadataJson { get; set; }

    public string ToolCallsJson { get; set; } = "[]";

    /// <summary>Displayed reasoning. Provider protocol is stored separately in ResponseMetadataJson.</summary>
    public string Reasoning { get; set; } = string.Empty;

    public string? ToolCallId { get; set; }

    public string? ToolName { get; set; }

    public VoiceMessageStatus Status { get; set; } = VoiceMessageStatus.Completed;

    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum VoiceMessageRole
{
    System,
    User,
    Assistant,
    Tool,
}

public enum VoiceMessageStatus
{
    Pending,
    Completed,
    Failed,
    Cancelled,
}
