namespace Lorekeeper.Models;

public class EditorMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ConversationId { get; set; }
    public EditorConversation Conversation { get; set; } = null!;

    public int Order { get; set; }

    public EditorMessageRole Role { get; set; }

    public string Content { get; set; } = string.Empty;

    public string ToolCallsJson { get; set; } = "[]";

    public string? ToolCallId { get; set; }

    public string? ToolName { get; set; }

    public EditorMessageStatus Status { get; set; } = EditorMessageStatus.Completed;

    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
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