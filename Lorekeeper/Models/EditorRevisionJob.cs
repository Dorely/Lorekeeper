namespace Lorekeeper.Models;

public class EditorRevisionJob
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public Guid ConversationId { get; set; }

    public Guid? AssistantMessageId { get; set; }

    public string ToolCallId { get; set; } = string.Empty;

    public string ArgumentsJson { get; set; } = "{}";
    public string ContentTargetKind { get; set; } = "Core";
    public Guid? ContentTargetEditionId { get; set; }

    public EditorRevisionJobStatus Status { get; set; } = EditorRevisionJobStatus.Queued;

    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    public ICollection<EditorRevisionSession> Sessions { get; set; } = [];
}

public enum EditorRevisionJobStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
}
