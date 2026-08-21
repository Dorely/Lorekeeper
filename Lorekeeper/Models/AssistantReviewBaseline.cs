namespace Lorekeeper.Models;

/// <summary>
/// The durable before-state retained for the latest successful assistant
/// manuscript mutation. This is deliberately independent from in-process
/// Undo/Redo history.
/// </summary>
public sealed class AssistantReviewBaseline
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public Guid ChapterId { get; set; }
    public Chapter Chapter { get; set; } = null!;
    public Lorekeeper.Manuscripts.EditorContentTargetKind TargetKind { get; set; }
    public Guid? EditionId { get; set; }
    public PublicationEdition? Edition { get; set; }
    public string TargetKey { get; set; } = string.Empty;
    public string BeforeManuscriptJson { get; set; } = string.Empty;
    public string BeforeHash { get; set; } = string.Empty;
    public Guid? AssistantTurnId { get; set; }
    public string ActionLabel { get; set; } = string.Empty;
    public DateTime CapturedAt { get; set; } = DateTime.UtcNow;
}
