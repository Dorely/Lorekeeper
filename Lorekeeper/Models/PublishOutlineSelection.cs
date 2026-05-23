namespace Lorekeeper.Models;

public class PublishOutlineSelection
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public PublishOutlineTargetKind TargetKind { get; set; }
    public Guid TargetId { get; set; }
    public bool IsIncluded { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum PublishOutlineTargetKind
{
    Act,
    Chapter,
}
