using Lorekeeper.Manuscripts;

namespace Lorekeeper.Models;

public class PublicationImagePlacement
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid EditionId { get; set; }
    public PublicationEdition Edition { get; set; } = null!;

    public Guid AssetId { get; set; }
    public PublishAsset Asset { get; set; } = null!;

    public PublishOutlineTargetKind TargetKind { get; set; }
    public Guid TargetId { get; set; }
    public Guid? ActId { get; set; }
    public Act? Act { get; set; }
    public Guid? ChapterId { get; set; }
    public Chapter? Chapter { get; set; }
    public PublicationImagePlacementKind PlacementKind { get; set; }
    public int SortOrder { get; set; }
    public string Caption { get; set; } = string.Empty;
    public string PresentationJson { get; set; } = "{}";
    public string AltText { get; set; } = string.Empty;
    public bool Decorative { get; set; }
    public string Language { get; set; } = "en";
    public FigureAccessibilityRole AccessibilityRole { get; set; } = FigureAccessibilityRole.Figure;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum PublicationImagePlacementKind
{
    BeforeAct,
    AfterAct,
    BeforeChapter,
    ChapterOpening,
    ChapterEnding,
    AfterChapter,
}
