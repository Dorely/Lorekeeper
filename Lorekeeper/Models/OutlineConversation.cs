namespace Lorekeeper.Models;

/// <summary>
/// Persisted, per-project transcript of the Outline collaboration chat. One row per project
/// (enforced by a unique index on <see cref="ProjectId"/>); messages are stored in
/// <see cref="OutlineMessage"/> and ordered by <see cref="OutlineMessage.Order"/>.
/// </summary>
public class OutlineConversation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<OutlineMessage> Messages { get; set; } = [];
}
