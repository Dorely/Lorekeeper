namespace Lorekeeper.Models;

/// <summary>
/// A durable group of AI-proposed changes emitted during one assistant turn.
/// Changes remain pending until the user approves or rejects them.
/// </summary>
public class AiChangeBatch
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public AiChangeConversationKind ConversationKind { get; set; } = AiChangeConversationKind.Outline;

    public Guid ConversationId { get; set; }

    public Guid? AssistantMessageId { get; set; }

    public AiChangeBatchStatus Status { get; set; } = AiChangeBatchStatus.Pending;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }

    public ICollection<AiChange> Changes { get; set; } = [];
}

public enum AiChangeBatchStatus
{
    Pending,
    Resolved,
}

public enum AiChangeConversationKind
{
    Outline,
    Editor,
}
