namespace Lorekeeper.Models;

/// <summary>
/// One row in a project's Writing Coach conversation.
/// </summary>
public class WritingCoachMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ConversationId { get; set; }
    public WritingCoachConversation Conversation { get; set; } = null!;

    public int Order { get; set; }

    public WritingCoachMessageRole Role { get; set; }

    public string Content { get; set; } = string.Empty;

    public WritingCoachMessageStatus Status { get; set; } = WritingCoachMessageStatus.Completed;

    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum WritingCoachMessageRole
{
    System,
    User,
    Assistant,
}

public enum WritingCoachMessageStatus
{
    Pending,
    Completed,
    Failed,
    Cancelled,
}