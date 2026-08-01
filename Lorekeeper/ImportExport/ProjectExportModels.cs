using System.Text.Json.Serialization;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;

namespace Lorekeeper.ImportExport;

[JsonConverter(typeof(JsonStringEnumConverter<ProjectExportKind>))]
public enum ProjectExportKind
{
    Full,
    NonStructural,
}

public sealed record ProjectExportFile(
    string FileName,
    string ContentType,
    byte[] Content);

public sealed record ProjectExportDocument
{
    public const string CurrentFormatId = "lorekeeper.project-export";
    public const int CurrentFormatVersion = 13;

    public string FormatId { get; init; } = CurrentFormatId;
    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public ProjectExportKind ExportKind { get; init; }
    public DateTime ExportedAtUtc { get; init; } = DateTime.UtcNow;
    public required ProjectExportProject Project { get; init; }
    public ProjectExportBookBrief? BookBrief { get; init; }
    public List<ProjectExportEntityType> EntityTypes { get; init; } = [];
    public List<ProjectExportImage> Images { get; init; } = [];
    public List<ProjectExportEntityVisualExample> EntityVisualExamples { get; init; } = [];
    public List<ProjectExportPublicationEdition> PublicationEditions { get; init; } = [];
    [JsonPropertyName("publishProfiles")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProjectExportLegacyPublishProfile>? LegacyPublishProfiles { get; init; }
    public List<ProjectExportManuscriptStyle> ManuscriptStyles { get; init; } = [];
    public List<ProjectExportFontFamily> FontFamilies { get; init; } = [];
    public List<ProjectExportAct> Acts { get; init; } = [];
    public List<ProjectExportChapter> Chapters { get; init; } = [];
    public List<ProjectExportNode> Nodes { get; init; } = [];
    public List<ProjectExportEdge> Edges { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

public sealed record ProjectExportProject(
    Guid Id,
    string Name,
    string Slug,
    string ProjectGuidance,
    bool IncludeCurrentChapterInContext,
    bool AiChangeApprovalEnabled)
{
    [JsonPropertyName("systemPrompt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacySystemPrompt { get; init; }

    [JsonIgnore]
    public string EffectiveProjectGuidance =>
        !string.IsNullOrWhiteSpace(ProjectGuidance) ? ProjectGuidance : LegacySystemPrompt ?? string.Empty;
}

public sealed record ProjectExportBookBrief(
    BookKind BookKind,
    string Premise,
    string Genre,
    string PrimaryThemes,
    string Purpose,
    string CreativeConstraints,
    string TargetAudience,
    int? MinimumReaderAge,
    int? MaximumReaderAge,
    string ReadingLevelGuidance,
    int? TargetWordCount,
    string PointOfView,
    string Tense,
    string VoiceAndTone,
    string LanguageLocale,
    string HouseStyle,
    bool? ReadAloudPriority,
    string AccessibilityGoals,
    string VisualDirection);

public sealed record ProjectExportEntityType(
    string Type,
    string SingularLabel,
    string PluralLabel,
    string? Color,
    string? Icon,
    bool IsStructural,
    bool IsChapterScoped,
    int SortOrder,
    Dictionary<string, object?> DefaultProperties);

public sealed record ProjectExportAct(
    Guid Id,
    string Title,
    string Synopsis,
    int Order);

public sealed record ProjectExportImage(
    Guid Id,
    string FileName,
    string ContentType,
    byte[] Data,
    string AltText,
    PublishAssetSource Source,
    string Prompt,
    string GenerationModel,
    string SourceMetadataJson,
    Guid? DerivedFromImageId,
    double? CropXPercent,
    double? CropYPercent,
    double? CropWidthPercent,
    double? CropHeightPercent,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record ProjectExportFontFamily(
    Guid Id,
    string Name,
    List<ProjectExportFontFace> Faces);

public sealed record ProjectExportFontFace(
    Guid Id,
    string SubfamilyName,
    string FileName,
    string ContentType,
    int Weight,
    bool Italic,
    byte[] Data,
    string Sha256);

public sealed record ProjectExportEntityVisualExample(
    ProjectExportNodeRef Entity,
    Guid ImageId,
    string Label,
    int SortOrder,
    EntityVisualExampleOrigin Origin,
    string SourceUrl,
    string SourceLocator);

public sealed record ProjectExportPublicationEdition(
    Guid Id,
    string Name,
    PublicationEditionFormat Format,
    PublicationVendor Vendor,
    string VendorProfileVersion,
    PublicationEditionStatus Status,
    bool IsDefault,
    long Revision,
    string TitleOverride,
    string Subtitle,
    string Author,
    string Language,
    string Publisher,
    string Copyright,
    string Isbn,
    string Description,
    bool IncludeTableOfContents,
    bool IncludeVisibleTableOfContents,
    bool IncludeActSynopses,
    bool IncludeChapterSynopses,
    bool IncludeActHeadings,
    bool IncludeChapterHeadings,
    bool NumberActs,
    bool NumberChapters,
    PublishTitlePageMode TitlePageMode,
    PrintPicturePageSpreadMode PrintPicturePageSpreadMode,
    EpubPicturePageSpreadMode EpubPicturePageSpreadMode,
    double PageWidthInches,
    double PageHeightInches,
    double PageMarginInches,
    double BodyFontSizePoints,
    double BodyLineHeight,
    Guid? SelectedCoverImageId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Guid? SelectedCoverChapterId,
    PublicationBinding Binding,
    PublicationPaper Paper,
    PublicationInk Ink,
    bool Bleed,
    List<ProjectExportEditionOutlineItem> OutlineItems,
    List<ProjectExportPublicationMatter> Matter,
    List<ProjectExportEditionStyleMapping> StyleMappings,
    List<ProjectExportPublicationImagePlacement> ImagePlacements,
    ProjectExportCoverDesign? CoverDesign);

public sealed record ProjectExportCoverDesign(
    string Title,
    string Subtitle,
    string Author,
    string SpineText,
    string BackCopy,
    string BackgroundColor,
    PublicationBarcodeMode BarcodeMode,
    double ImageFocalXPercent,
    double ImageFocalYPercent,
    long Revision);

public sealed record ProjectExportEditionOutlineItem(
    Guid Id,
    PublishOutlineTargetKind TargetKind,
    Guid TargetId,
    bool IsIncluded,
    int SortOrder);

public sealed record ProjectExportPublicationMatter(
    Guid Id,
    PublicationMatterLocation Location,
    PublicationMatterKind Kind,
    string Title,
    string ManuscriptJson,
    long Revision,
    bool IsIncluded,
    int SortOrder);

public sealed record ProjectExportEditionStyleMapping(
    Guid Id,
    Guid ManuscriptStyleDefinitionId,
    string SemanticRole,
    ManuscriptStyleProperties Override,
    long Revision);

public sealed record ProjectExportPublicationImagePlacement(
    Guid Id,
    Guid AssetId,
    PublishOutlineTargetKind TargetKind,
    Guid TargetId,
    PublicationImagePlacementKind PlacementKind,
    string Caption,
    int SortOrder);

public sealed record ProjectExportLegacyPublishProfile(
    Guid Id,
    string TitleOverride,
    string Subtitle,
    string Author,
    string Language,
    string Publisher,
    string Copyright,
    string Isbn,
    string Description,
    string Dedication,
    string Acknowledgments,
    string References,
    bool IncludeTableOfContents,
    bool IncludeVisibleTableOfContents,
    bool IncludeActSynopses,
    bool IncludeChapterSynopses,
    bool IncludeActHeadings,
    bool IncludeChapterHeadings,
    bool NumberActs,
    bool NumberChapters,
    PublishTitlePageMode TitlePageMode,
    PrintPicturePageSpreadMode PrintPicturePageSpreadMode,
    EpubPicturePageSpreadMode EpubPicturePageSpreadMode,
    double PageWidthInches,
    double PageHeightInches,
    double PageMarginInches,
    double BodyFontSizePoints,
    double BodyLineHeight,
    Guid? SelectedCoverChapterId);

public sealed record ProjectExportManuscriptStyle(
    Guid Id,
    string Name,
    ManuscriptStyleKind Kind,
    string SemanticRole,
    ManuscriptStyleProperties Definition,
    long Revision);

public sealed record ProjectExportChapter
{
    public Guid Id { get; init; }
    public Guid? ActId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string ManuscriptJson { get; init; } = string.Empty;
    public long ManuscriptRevision { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Body { get; init; }
    public string Synopsis { get; init; } = string.Empty;
    public int Order { get; init; }
    public ChapterVisualMode VisualMode { get; init; } = ChapterVisualMode.Prose;
    public ChapterPageLayoutKind PageLayoutKind { get; init; } = ChapterPageLayoutKind.SinglePortrait;
    public string PageLayoutJson { get; init; } = string.Empty;
    public string IllustrationLayoutJson { get; init; } = string.Empty;
    public List<Guid> ExplicitImageContextImageIds { get; init; } = [];
}

public sealed record ProjectExportNode(
    string NodeType,
    string Key,
    string? Label,
    Dictionary<string, object?> Properties,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record ProjectExportEdge(
    ProjectExportNodeRef From,
    ProjectExportNodeRef To,
    string EdgeType,
    Dictionary<string, object?> Properties,
    int? SortOrder,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record ProjectExportNodeRef(string NodeType, string Key)
{
    public string StableKey => $"{NodeType}/{Key}";
}
