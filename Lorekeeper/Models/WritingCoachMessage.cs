namespace Lorekeeper.Models;

/// <summary>
/// One message in a project's Writing Coach conversation. Mirrors the outline
/// chat message shape so assistant tool calls can be replayed into the LLM
/// with separate tool-result rows.
/// </summary>
public class WritingCoachMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ConversationId { get; set; }
    public WritingCoachConversation Conversation { get; set; } = null!;

    public int Order { get; set; }

    public WritingCoachMessageRole Role { get; set; }

    public string Content { get; set; } = string.Empty;

    /// <summary>Versioned provider protocol, provenance, finish reason, and usage. Not display text.</summary>
    public string? ResponseMetadataJson { get; set; }

    public string ToolCallsJson { get; set; } = "[]";

    /// <summary>Displayed reasoning. Provider protocol is stored separately in ResponseMetadataJson.</summary>
    public string Reasoning { get; set; } = string.Empty;

    public string? ToolCallId { get; set; }

    public string? ToolName { get; set; }

    public WritingCoachMessageStatus Status { get; set; } = WritingCoachMessageStatus.Completed;

    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum WritingCoachMessageRole
{
    System,
    User,
    Assistant,
    Tool,
}

public enum WritingCoachMessageStatus
{
    Pending,
    Completed,
    Failed,
    Cancelled,
}
