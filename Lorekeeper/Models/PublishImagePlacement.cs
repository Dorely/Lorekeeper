namespace Lorekeeper.Models;

public class PublishImagePlacement
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public Guid AssetId { get; set; }
    public PublishAsset Asset { get; set; } = null!;

    public PublishOutlineTargetKind TargetKind { get; set; }
    public Guid TargetId { get; set; }
    public PublishImagePlacementKind PlacementKind { get; set; }
    public int SortOrder { get; set; }
    public string Caption { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum PublishImagePlacementKind
{
    BeforeAct,
    AfterAct,
    BeforeChapter,
    ChapterOpening,
    ChapterEnding,
    AfterChapter,
}
