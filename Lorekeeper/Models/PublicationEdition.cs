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

[JsonConverter(typeof(JsonStringEnumConverter<PublicationEditionFormat>))]
public enum PublicationEditionFormat
{
    Paperback,
    Epub,
}

[JsonConverter(typeof(JsonStringEnumConverter<PublicationVendor>))]
public enum PublicationVendor
{
    Generic,
    AmazonKdp,
    IngramSpark,
}

[JsonConverter(typeof(JsonStringEnumConverter<PublicationEditionStatus>))]
public enum PublicationEditionStatus
{
    Draft,
    Archived,
}

[JsonConverter(typeof(JsonStringEnumConverter<PublicationBinding>))]
public enum PublicationBinding
{
    PerfectBound,
    Digital,
}

[JsonConverter(typeof(JsonStringEnumConverter<PublicationPaper>))]
public enum PublicationPaper
{
    White,
    Cream,
    Digital,
}

[JsonConverter(typeof(JsonStringEnumConverter<PublicationInk>))]
public enum PublicationInk
{
    BlackAndWhite,
    Color,
    Digital,
}

public class PublicationEdition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public required string Name { get; set; }
    public PublicationEditionFormat Format { get; set; } = PublicationEditionFormat.Paperback;
    public PublicationVendor Vendor { get; set; } = PublicationVendor.Generic;
    public string VendorProfileVersion { get; set; } = "preview-1";
    public PublicationEditionStatus Status { get; set; } = PublicationEditionStatus.Draft;
    public bool IsDefault { get; set; }
    public long Revision { get; set; }

    public string TitleOverride { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Language { get; set; } = "en";
    public string Publisher { get; set; } = string.Empty;
    public string Copyright { get; set; } = string.Empty;
    public string Isbn { get; set; } = string.Empty;
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
    public PrintPicturePageSpreadMode PrintPicturePageSpreadMode { get; set; } = PrintPicturePageSpreadMode.WholeSpread;
    public EpubPicturePageSpreadMode EpubPicturePageSpreadMode { get; set; } = EpubPicturePageSpreadMode.RequestLandscape;
    public PublicationBinding Binding { get; set; } = PublicationBinding.PerfectBound;
    public PublicationPaper Paper { get; set; } = PublicationPaper.White;
    public PublicationInk Ink { get; set; } = PublicationInk.BlackAndWhite;
    public bool Bleed { get; set; }

    public double PageWidthInches { get; set; } = 8.5;

    public double PageHeightInches { get; set; } = 11;

    public double PageMarginInches { get; set; } = 0.75;

    public double BodyFontSizePoints { get; set; } = 12;

    public double BodyLineHeight { get; set; } = 1.55;

    public Guid? SelectedCoverChapterId { get; set; }
    public Chapter? SelectedCoverChapter { get; set; }

    public ICollection<PublicationEditionOutlineItem> OutlineItems { get; set; } = [];
    public ICollection<PublicationMatter> Matter { get; set; } = [];
    public ICollection<PublicationEditionStyleMapping> StyleMappings { get; set; } = [];
    public ICollection<PublicationImagePlacement> ImagePlacements { get; set; } = [];
    public ICollection<PublicationEditionAuditEntry> AuditEntries { get; set; } = [];
    public ICollection<PublicationRenderJob> RenderJobs { get; set; } = [];
    public ICollection<PublicationArtifact> Artifacts { get; set; } = [];

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
