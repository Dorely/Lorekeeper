namespace Lorekeeper.Models;

public class PublishMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ConversationId { get; set; }
    public PublishConversation Conversation { get; set; } = null!;

    public int Order { get; set; }
    public PublishMessageRole Role { get; set; }
    public string Content { get; set; } = string.Empty;
    public string ToolCallsJson { get; set; } = "[]";
    public string Reasoning { get; set; } = string.Empty;
    public string? ToolCallId { get; set; }
    public string? ToolName { get; set; }
    public PublishMessageStatus Status { get; set; } = PublishMessageStatus.Completed;
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<PublishMessageVisual> Visuals { get; set; } = [];
}

public enum PublishMessageRole
{
    System,
    User,
    Assistant,
    Tool,
}

public enum PublishMessageStatus
{
    Pending,
    Completed,
    Failed,
    Cancelled,
}
