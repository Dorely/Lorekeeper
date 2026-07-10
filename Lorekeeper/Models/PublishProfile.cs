using System.Text.Json.Serialization;

namespace Lorekeeper.Models;

[JsonConverter(typeof(JsonStringEnumConverter<PublishTitlePageMode>))]
public enum PublishTitlePageMode
{
    Automatic = 0,
    Include = 1,
    Omit = 2,
}

[JsonConverter(typeof(JsonStringEnumConverter<PrintPicturePageSpreadMode>))]
public enum PrintPicturePageSpreadMode
{
    WholeSpread = 0,
    SidewaysWholeSpread = 1,
    SplitLeaves = 2,
}

[JsonConverter(typeof(JsonStringEnumConverter<EpubPicturePageSpreadMode>))]
public enum EpubPicturePageSpreadMode
{
    RequestLandscape = 0,
    SidewaysPortrait = 1,
}

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
    public bool IncludeTableOfContents { get; set; } = true;
    public bool IncludeVisibleTableOfContents { get; set; } = true;
    public bool IncludeActSynopses { get; set; }
    public bool IncludeChapterSynopses { get; set; }
    public bool IncludeActHeadings { get; set; } = true;
    public bool IncludeChapterHeadings { get; set; } = true;
    public bool NumberActs { get; set; }
    public bool NumberChapters { get; set; }
    public PublishTitlePageMode TitlePageMode { get; set; } = PublishTitlePageMode.Automatic;
    public PrintPicturePageSpreadMode PrintPicturePageSpreadMode { get; set; } = PrintPicturePageSpreadMode.WholeSpread;
    public EpubPicturePageSpreadMode EpubPicturePageSpreadMode { get; set; } = EpubPicturePageSpreadMode.RequestLandscape;

    public double PageWidthInches { get; set; } = 8.5;

    public double PageHeightInches { get; set; } = 11;

    public double PageMarginInches { get; set; } = 0.75;

    public double BodyFontSizePoints { get; set; } = 12;

    public double BodyLineHeight { get; set; } = 1.55;

    public Guid? SelectedCoverChapterId { get; set; }
    public Chapter? SelectedCoverChapter { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
