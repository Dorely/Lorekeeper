using Lorekeeper.Manuscripts;

namespace Lorekeeper.Models;

public enum PublicationEditionOverrideField
{
    Title,
    Subtitle,
    Author,
    Language,
    Publisher,
    Copyright,
    Description,
    IncludeTableOfContents,
    IncludeVisibleTableOfContents,
    IncludeActSynopses,
    IncludeChapterSynopses,
    IncludeActHeadings,
    IncludeChapterHeadings,
    NumberActs,
    NumberChapters,
    TitlePageMode,
    PageWidthInches,
    PageHeightInches,
    PageMarginInches,
    BodyFontSizePoints,
    BodyLineHeight,
}

public class PublicationBook
{
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public long Revision { get; set; } = 1;

    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Language { get; set; } = "en";
    public string Publisher { get; set; } = string.Empty;
    public string Copyright { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    public bool IncludeTableOfContents { get; set; } = true;
    public bool IncludeVisibleTableOfContents { get; set; } = true;
    public bool IncludeActSynopses { get; set; }
    public bool IncludeChapterSynopses { get; set; }
    public bool IncludeActHeadings { get; set; } = true;
    public bool IncludeChapterHeadings { get; set; } = true;
    public bool NumberActs { get; set; }
    public bool NumberChapters { get; set; }
    public PublishTitlePageMode TitlePageMode { get; set; } = PublishTitlePageMode.Automatic;

    public ICollection<PublicationBookOutlineItem> OutlineItems { get; set; } = [];
    public ICollection<PublicationBookMatter> Matter { get; set; } = [];
    public ICollection<PublicationBookImagePlacement> ImagePlacements { get; set; } = [];
    public PublicationBookCoverDesign? CoverDesign { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class PublicationBookOutlineItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public PublicationBook Book { get; set; } = null!;
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

public class PublicationBookMatter
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public PublicationBook Book { get; set; } = null!;
    public PublicationMatterLocation Location { get; set; }
    public PublicationMatterKind Kind { get; set; }
    public required string Title { get; set; }
    public string ManuscriptJson { get; set; } = string.Empty;
    public long Revision { get; set; }
    public bool IsIncluded { get; set; } = true;
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class PublicationBookImagePlacement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public PublicationBook Book { get; set; } = null!;
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

public class PublicationBookCoverDesign
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public PublicationBook Book { get; set; } = null!;
    public string BackgroundColor { get; set; } = "#5c7ca5";
    public string CompositionSceneJson { get; set; } = string.Empty;
    public long Revision { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
