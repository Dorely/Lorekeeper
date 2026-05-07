namespace Lorekeeper.Models;

/// <summary>
/// Persisted, per-project transcript for the Writing Coach chat.
/// </summary>
public class WritingCoachConversation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<WritingCoachMessage> Messages { get; set; } = [];
}