namespace Lorekeeper.Models;

public class ProjectImageMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ConversationId { get; set; }
    public ProjectImageConversation Conversation { get; set; } = null!;

    public int Order { get; set; }

    public ProjectImageMessageRole Role { get; set; }

    public string Content { get; set; } = string.Empty;

    /// <summary>Versioned provider protocol, provenance, finish reason, and usage. Not display text.</summary>
    public string? ResponseMetadataJson { get; set; }

    public string ToolCallsJson { get; set; } = "[]";

    /// <summary>Displayed reasoning. Provider protocol is stored separately in ResponseMetadataJson.</summary>
    public string Reasoning { get; set; } = string.Empty;


    public string? ToolCallId { get; set; }

    public string? ToolName { get; set; }

    public ProjectImageMessageStatus Status { get; set; } = ProjectImageMessageStatus.Completed;

    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<ProjectImageMessageVisual> Visuals { get; set; } = [];
}

public enum ProjectImageMessageRole
{
    System,
    User,
    Assistant,
    Tool,
}

public enum ProjectImageMessageStatus
{
    Pending,
    Completed,
    Failed,
    Cancelled,
}
