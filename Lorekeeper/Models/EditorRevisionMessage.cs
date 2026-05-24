namespace Lorekeeper.Models;

public class EditorRevisionMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SessionId { get; set; }
    public EditorRevisionSession Session { get; set; } = null!;

    public int Order { get; set; }

    public EditorRevisionMessageRole Role { get; set; }

    public string Content { get; set; } = string.Empty;

    public string ToolCallsJson { get; set; } = "[]";

    public string? ToolCallId { get; set; }

    public string? ToolName { get; set; }

    public EditorRevisionMessageStatus Status { get; set; } = EditorRevisionMessageStatus.Completed;

    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum EditorRevisionMessageRole
{
    System,
    User,
    Assistant,
    Tool,
}

public enum EditorRevisionMessageStatus
{
    Pending,
    Completed,
    Failed,
    Cancelled,
}
