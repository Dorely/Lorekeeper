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

    /// <summary>
    /// Explicit chat model override for this transcript. This is intentionally
    /// a soft provider-row reference so a deleted provider remains detectable
    /// instead of silently falling back to the global default.
    /// </summary>
    public int? SelectedProviderId { get; set; }

    public ICollection<WritingCoachMessage> Messages { get; set; } = [];
}
