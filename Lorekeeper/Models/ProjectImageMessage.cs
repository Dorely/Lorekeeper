namespace Lorekeeper.Models;

public class ProjectImageMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ConversationId { get; set; }
    public ProjectImageConversation Conversation { get; set; } = null!;

    public int Order { get; set; }

    public ProjectImageMessageRole Role { get; set; }

    public string Content { get; set; } = string.Empty;

    public string ToolCallsJson { get; set; } = "[]";

    /// <summary>Model reasoning streamed alongside this assistant row. Echoed back within the turn; dropped from cross-turn replay.</summary>
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
