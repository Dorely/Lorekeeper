using System.Text.Json.Serialization;
using Lorekeeper.ChapterVisuals;
using Lorekeeper.Models;
using Lorekeeper.Manuscripts;

namespace Lorekeeper.Publish;

[JsonConverter(typeof(JsonStringEnumConverter<PublishExportFormat>))]
public enum PublishExportFormat
{
    PlainText,
    Markdown,
    Epub,
}

public sealed record PublishWorkspaceView(
    PublicationEditionView Edition,
    IReadOnlyList<PublicationEditionSummary> Editions,
    IReadOnlyList<PublishSectionView> Sections,
    IReadOnlyList<PublishCoverCandidateView> CoverCandidates,
    IReadOnlyList<PublicationImagePlacementView> Placements,
    IReadOnlyList<PublicationMatterView> Matter,
    IReadOnlyList<PublicationEditionStyleMappingView> StyleMappings,
    string SourceFingerprint);

public sealed record PublicationEditionSummary(
    Guid Id,
    string Name,
    PublicationEditionFormat Format,
    PublicationVendor Vendor,
    PublicationEditionStatus Status,
    bool IsDefault,
    long Revision);

public sealed record PublicationEditionView(
    Guid Id,
    string Name,
    PublicationEditionFormat Format,
    PublicationVendor Vendor,
    string VendorProfileVersion,
    PublicationEditionStatus Status,
    bool IsDefault,
    long Revision,
    string ProjectName,
    string ProjectSlug,
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
    Guid? SelectedCoverChapterId,
    PublicationBinding Binding,
    PublicationPaper Paper,
    PublicationInk Ink,
    bool Bleed);

public sealed record PublicationEditionUpdate(
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
    long ExpectedRevision,
    string Name,
    PublicationEditionFormat Format,
    PublicationVendor Vendor,
    string VendorProfileVersion,
    PublicationBinding Binding,
    PublicationPaper Paper,
    PublicationInk Ink,
    bool Bleed);

public sealed record PublicationEditionCreate(
    string Name,
    PublicationEditionFormat Format,
    PublicationVendor Vendor = PublicationVendor.Generic);

public sealed record PublicationEditionCompareView(
    PublicationEditionSummary Left,
    PublicationEditionSummary Right,
    IReadOnlyList<string> Differences);

public sealed record PublicationMatterView(
    Guid Id,
    PublicationMatterLocation Location,
    PublicationMatterKind Kind,
    string Title,
    ManuscriptDocument Manuscript,
    bool IsIncluded,
    int SortOrder,
    long Revision);

public sealed record PublicationMatterInput(
    Guid? Id,
    PublicationMatterLocation Location,
    PublicationMatterKind Kind,
    string Title,
    string ManuscriptJson,
    bool IsIncluded,
    int SortOrder,
    long? ExpectedRevision = null);

public sealed record PublicationEditionStyleMappingView(
    Guid Id,
    Guid ManuscriptStyleDefinitionId,
    string StyleName,
    string SemanticRole,
    ManuscriptStyleProperties Override,
    long Revision);

public sealed record PublicationEditionStyleMappingInput(
    Guid ManuscriptStyleDefinitionId,
    ManuscriptStyleProperties Override,
    long? ExpectedRevision = null);

public sealed record PublicationEditionAuditView(
    Guid Id,
    string Action,
    string Actor,
    string BeforeHash,
    string AfterHash,
    string DetailJson,
    DateTime CreatedAt);

public sealed record PublishSectionView(
    Guid? ActId,
    string Title,
    bool IsUnassigned,
    bool IsIncluded,
    IReadOnlyList<PublishChapterView> Chapters);

public sealed record PublishChapterView(
    Guid Id,
    Guid? ActId,
    string Title,
    bool IsIncluded,
    bool IsCover,
    ChapterVisualMode VisualMode,
    ChapterPageLayoutKind PageLayoutKind);

public sealed record PublishCoverCandidateView(
    Guid Id,
    string Title,
    string OutlineLabel,
    ChapterPageLayoutKind PageLayoutKind,
    string PreviewUrl);

public sealed record PublicationImagePlacementView(
    Guid Id,
    Guid AssetId,
    string AssetFileName,
    string AssetPreviewUrl,
    PublishOutlineTargetKind TargetKind,
    Guid TargetId,
    string TargetTitle,
    PublicationImagePlacementKind PlacementKind,
    string Caption,
    int SortOrder);

public sealed record PublicationEditionOutlineItemUpdate(
    PublishOutlineTargetKind TargetKind,
    Guid TargetId,
    bool IsIncluded);

public sealed record PublicationImagePlacementCreate(
    Guid AssetId,
    PublishOutlineTargetKind TargetKind,
    Guid TargetId,
    PublicationImagePlacementKind PlacementKind,
    string Caption);

public sealed record PublicationImagePlacementUpdate(
    Guid AssetId,
    PublishOutlineTargetKind TargetKind,
    Guid TargetId,
    PublicationImagePlacementKind PlacementKind,
    string Caption);

public sealed record PublishDocument(
    Guid EditionId,
    Guid ProjectId,
    string ProjectName,
    string ProjectSlug,
    DateTime ExportedAtUtc,
    PublishDocumentProfile Profile,
    PublishAssetDocument? CoverAsset,
    IReadOnlyList<PublishSectionDocument> Sections,
    IReadOnlyList<PublishAssetDocument> Assets,
    IReadOnlyList<PublicationImagePlacementDocument> Placements)
{
    public string SourceFingerprint { get; init; } = string.Empty;
    public ChapterPageLayoutKind? CoverPageLayoutKind { get; init; }
    public IReadOnlyList<PublishManuscriptStyleDocument> NamedStyles { get; init; } = [];
    public IReadOnlyList<PublishMatterDocument> Matter { get; init; } = [];

    public string DisplayTitle => string.IsNullOrWhiteSpace(Profile.TitleOverride)
        ? ProjectName
        : Profile.TitleOverride.Trim();
}

public sealed record PublishMatterDocument(
    Guid Id,
    PublicationMatterLocation Location,
    PublicationMatterKind Kind,
    string Title,
    int SortOrder,
    ManuscriptDocument Manuscript);

public sealed record PublishManuscriptStyleDocument(
    string Name,
    ManuscriptStyleKind Kind,
    string SemanticRole,
    ManuscriptStyleProperties Definition);

public sealed record PublishDocumentProfile(
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
    bool IncludeTitlePage,
    PrintPicturePageSpreadMode PrintPicturePageSpreadMode,
    EpubPicturePageSpreadMode EpubPicturePageSpreadMode,
    double PageWidthInches,
    double PageHeightInches,
    double PageMarginInches,
    double BodyFontSizePoints,
    double BodyLineHeight);

public sealed record PublishSectionDocument(
    Guid? ActId,
    string Title,
    string Synopsis,
    bool IsUnassigned,
    bool IncludePage,
    bool IncludeHeading,
    int Order,
    IReadOnlyList<PublishChapterDocument> Chapters);

public sealed record PublishChapterDocument(
    Guid Id,
    Guid? ActId,
    string Title,
    string PlainText,
    string Synopsis,
    int Order,
    bool IncludeHeading,
    ChapterVisualMode VisualMode,
    ChapterPageLayoutKind PageLayoutKind,
    IllustratedProseLayout IllustrationLayout,
    PicturePageLayout PageLayout,
    ManuscriptDocument Manuscript)
{
    public PublishPicturePageDocument? RenderedPicturePage { get; init; }
}

public sealed record PublishPicturePageDocument(
    PublishAssetDocument Surface,
    int PhysicalPageWidthPixels,
    int PhysicalPageHeightPixels,
    int LeafCount,
    int SurfaceWidthPixels,
    int SurfaceHeightPixels,
    ChapterPicturePageSurfaceRotation Rotation,
    string AccessibleText);

public sealed record PublishAssetDocument(
    Guid Id,
    string FileName,
    string ContentType,
    byte[] Data,
    string AltText);

public sealed record PublicationImagePlacementDocument(
    Guid Id,
    PublishAssetDocument Asset,
    PublishOutlineTargetKind TargetKind,
    Guid TargetId,
    PublicationImagePlacementKind PlacementKind,
    string Caption,
    int SortOrder);
