namespace Lorekeeper.Models;

public class PublishProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public string TitleOverride { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Language { get; set; } = "en";
    public string Publisher { get; set; } = string.Empty;
    public string Copyright { get; set; } = string.Empty;
    public string Isbn { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Dedication { get; set; } = string.Empty;
    public string Acknowledgments { get; set; } = string.Empty;
    public string References { get; set; } = string.Empty;
    public string CoverLayoutJson { get; set; } = string.Empty;

    public bool IncludeTableOfContents { get; set; } = true;
    public bool IncludeVisibleTableOfContents { get; set; } = true;
    public bool IncludeActSynopses { get; set; }
    public bool IncludeChapterSynopses { get; set; }
    public bool IncludeActHeadings { get; set; } = true;
    public bool IncludeChapterHeadings { get; set; } = true;
    public bool NumberActs { get; set; }
    public bool NumberChapters { get; set; }

    public Guid? SelectedCoverAssetId { get; set; }
    public PublishAsset? SelectedCoverAsset { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
