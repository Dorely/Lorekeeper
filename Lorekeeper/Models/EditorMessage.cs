namespace Lorekeeper.Models;

public class EditorMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ConversationId { get; set; }
    public EditorConversation Conversation { get; set; } = null!;

    public int Order { get; set; }

    public EditorMessageRole Role { get; set; }

    public string Content { get; set; } = string.Empty;

    /// <summary>Versioned provider protocol, provenance, finish reason, and usage. Not display text.</summary>
    public string? ResponseMetadataJson { get; set; }

    public string ToolCallsJson { get; set; } = "[]";

    /// <summary>Displayed reasoning. Provider protocol is stored separately in ResponseMetadataJson.</summary>
    public string Reasoning { get; set; } = string.Empty;

    public string? ToolCallId { get; set; }

    public string? ToolName { get; set; }

    /// <summary>Bounded provenance trace for the automatic context used on this user turn.</summary>
    public string? ContextSnapshotJson { get; set; }

    public string ContentTargetKind { get; set; } = "Core";
    public Guid? ContentTargetEditionId { get; set; }

    public EditorMessageStatus Status { get; set; } = EditorMessageStatus.Completed;

    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<EditorMessageVisual> Visuals { get; set; } = [];
}

public enum EditorMessageRole
{
    System,
    User,
    Assistant,
    Tool,
}

public enum EditorMessageStatus
{
    Pending,
    Completed,
    Failed,
    Cancelled,
}
