namespace Lorekeeper.Models;

public class PublicationEditionOutlineItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid EditionId { get; set; }
    public PublicationEdition Edition { get; set; } = null!;

    public PublishOutlineTargetKind TargetKind { get; set; }
    public Guid TargetId { get; set; }
    public Guid? ActId { get; set; }
    public Act? Act { get; set; }
    public Guid? ChapterId { get; set; }
    public Chapter? Chapter { get; set; }
    public bool IsIncluded { get; set; } = true;
    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum PublishOutlineTargetKind
{
    Act,
    Chapter,
}
