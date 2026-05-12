namespace Lorekeeper.Models;

public class ContestBatch
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public Guid ConversationId { get; set; }

    public Guid? AssistantMessageId { get; set; }

    public Guid ChapterId { get; set; }

    public string ChapterTitle { get; set; } = string.Empty;

    public string OriginalChapterBody { get; set; } = string.Empty;

    public string ContextSnapshotJson { get; set; } = "{}";

    public ContestBatchStatus Status { get; set; } = ContestBatchStatus.Running;

    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    public ICollection<ContestCandidate> Candidates { get; set; } = [];
}

public enum ContestBatchStatus
{
    Running,
    Completed,
    Staged,
    Failed,
    Cancelled,
}
