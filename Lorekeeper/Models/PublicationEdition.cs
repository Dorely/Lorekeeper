using System.Text.Json.Serialization;

namespace Lorekeeper.Models;

using System.ComponentModel.DataAnnotations.Schema;

[JsonConverter(typeof(JsonStringEnumConverter<PublishTitlePageMode>))]
public enum PublishTitlePageMode
{
    Automatic = 0,
    Include = 1,
    Omit = 2,
}

[JsonConverter(typeof(JsonStringEnumConverter<PublicationEditionFormat>))]
public enum PublicationEditionFormat
{
    Paperback,
    Hardcover,
    Epub,
    DigitalPdf,
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

[JsonConverter(typeof(JsonStringEnumConverter<PrintFinish>))]
public enum PrintFinish
{
    Matte,
    Gloss,
    Textured,
}

[JsonConverter(typeof(JsonStringEnumConverter<PrintCoverMode>))]
public enum PrintCoverMode
{
    Simplex,
    Duplex,
}

public class PublicationEdition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public required string Name { get; set; }
    public PublicationEditionFormat Format { get; set; } = PublicationEditionFormat.Paperback;
    public PublicationVendor Vendor { get; set; } = PublicationVendor.Generic;
    public string VendorProfileVersion { get; set; } = "generic-print-v2";
    public PublicationEditionStatus Status { get; set; } = PublicationEditionStatus.Draft;
    public long Revision { get; set; }
    public string OverrideFieldsJson { get; set; } = "[]";
    public string PublicationSectionOrderJson { get; set; } = "{}";

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
    public string PrintRegistryVersion { get; set; } = string.Empty;
    public string PrintProductKey { get; set; } = string.Empty;
    public PrintFinish PrintFinish { get; set; } = PrintFinish.Matte;
    public PrintCoverMode PrintCoverMode { get; set; } = PrintCoverMode.Simplex;
    public string GenericPrintTemplateJson { get; set; } = string.Empty;
    public bool Bleed { get; set; }
    public bool AllowDesignedPageOverrides { get; set; }
    public bool InheritsCoreCover { get; set; } = true;
    public bool EditionSpecificContentEnabled { get; set; }

    public double PageWidthInches { get; set; } = 8.5;

    public double PageHeightInches { get; set; } = 11;

    public double PageMarginInches { get; set; } = 0.75;

    [NotMapped]
    public double BodyFontSizePoints { get; set; } = 12;

    [NotMapped]
    public double BodyLineHeight { get; set; } = 1.55;

    public Guid? SelectedCoverImageId { get; set; }
    public PublishAsset? SelectedCoverImage { get; set; }

    public ICollection<PublicationEditionOutlineItem> OutlineItems { get; set; } = [];
    public ICollection<PublicationMatter> Matter { get; set; } = [];
    public ICollection<PublicationImagePlacement> ImagePlacements { get; set; } = [];
    public ICollection<PublicationEditionAuditEntry> AuditEntries { get; set; } = [];
    public ICollection<PublicationRenderJob> RenderJobs { get; set; } = [];
    public ICollection<PublicationArtifact> Artifacts { get; set; } = [];
    public ICollection<PublicationEditionChapterOverride> ChapterOverrides { get; set; } = [];
    public ICollection<PageComposition> PageCompositions { get; set; } = [];
    public ICollection<ManuscriptAnnotation> ManuscriptAnnotations { get; set; } = [];
    public ICollection<PublicationSection> PublicationSections { get; set; } = [];
    public PublicationCoverDesign? CoverDesign { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
