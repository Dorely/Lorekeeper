namespace Lorekeeper.Models;

public class ResearchMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ConversationId { get; set; }
    public ResearchConversation Conversation { get; set; } = null!;

    public int Order { get; set; }

    public ResearchMessageRole Role { get; set; }

    public string Content { get; set; } = string.Empty;

    public string ToolCallsJson { get; set; } = "[]";

    /// <summary>Model reasoning streamed alongside this assistant row. Echoed back within the turn; dropped from cross-turn replay.</summary>
    public string Reasoning { get; set; } = string.Empty;


    public string? ToolCallId { get; set; }

    public string? ToolName { get; set; }

    public ResearchMessageStatus Status { get; set; } = ResearchMessageStatus.Completed;

    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum ResearchMessageRole
{
    System,
    User,
    Assistant,
    Tool,
}

public enum ResearchMessageStatus
{
    Pending,
    Completed,
    Failed,
    Cancelled,
}