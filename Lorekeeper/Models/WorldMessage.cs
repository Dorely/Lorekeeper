namespace Lorekeeper.Models;

public class WorldMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ConversationId { get; set; }
    public WorldConversation Conversation { get; set; } = null!;

    public int Order { get; set; }

    public WorldMessageRole Role { get; set; }

    public string Content { get; set; } = string.Empty;

    /// <summary>Versioned provider protocol, provenance, finish reason, and usage. Not display text.</summary>
    public string? ResponseMetadataJson { get; set; }

    public string ToolCallsJson { get; set; } = "[]";

    /// <summary>Displayed reasoning. Provider protocol is stored separately in ResponseMetadataJson.</summary>
    public string Reasoning { get; set; } = string.Empty;


    public string? ToolCallId { get; set; }

    public string? ToolName { get; set; }

    public WorldMessageStatus Status { get; set; } = WorldMessageStatus.Completed;

    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum WorldMessageRole
{
    System,
    User,
    Assistant,
    Tool,
}

public enum WorldMessageStatus
{
    Pending,
    Completed,
    Failed,
    Cancelled,
}
