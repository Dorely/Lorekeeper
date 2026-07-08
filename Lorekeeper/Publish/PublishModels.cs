using System.Text.Json.Serialization;
using Lorekeeper.Models;

namespace Lorekeeper.Publish;

[JsonConverter(typeof(JsonStringEnumConverter<PublishExportFormat>))]
public enum PublishExportFormat
{
    PlainText,
    Markdown,
    Epub,
}

public sealed record PublishWorkspaceView(
    PublishProfileView Profile,
    IReadOnlyList<PublishSectionView> Sections,
    IReadOnlyList<PublishAssetView> Assets,
    IReadOnlyList<PublishImagePlacementView> Placements);

public sealed record PublishProfileView(
    Guid Id,
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
    Guid? SelectedCoverAssetId,
    PublishCoverLayoutView CoverLayout);

public sealed record PublishProfileUpdate(
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
    bool NumberChapters);

public sealed record PublishCoverLayoutView(
    IReadOnlyList<PublishCoverLayerView> Layers);

public sealed record PublishCoverLayerView(
    PublishCoverLayerKind Kind,
    bool IsVisible,
    double XPercent,
    double YPercent,
    double WidthPercent,
    double FontSizePercent,
    PublishCoverFontFamily FontFamily,
    PublishCoverTextAlign TextAlign,
    string Color,
    double Opacity,
    bool IsBold,
    bool IsItalic,
    PublishCoverShadow Shadow);

[JsonConverter(typeof(JsonStringEnumConverter<PublishCoverLayerKind>))]
public enum PublishCoverLayerKind
{
    Title,
    Subtitle,
    Author,
}

[JsonConverter(typeof(JsonStringEnumConverter<PublishCoverFontFamily>))]
public enum PublishCoverFontFamily
{
    Serif,
    Sans,
    Display,
    Monospace,
}

[JsonConverter(typeof(JsonStringEnumConverter<PublishCoverTextAlign>))]
public enum PublishCoverTextAlign
{
    Left,
    Center,
    Right,
}

[JsonConverter(typeof(JsonStringEnumConverter<PublishCoverShadow>))]
public enum PublishCoverShadow
{
    None,
    Soft,
    Strong,
    Glow,
}

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
    bool IsIncluded);

public sealed record PublishAssetView(
    Guid Id,
    string FileName,
    string ContentType,
    string PreviewDataUrl,
    string AltText,
    PublishAssetSource Source,
    string Prompt,
    string GenerationModel,
    DateTime CreatedAt,
    long SizeBytes);

public sealed record PublishImagePlacementView(
    Guid Id,
    Guid AssetId,
    string AssetFileName,
    PublishOutlineTargetKind TargetKind,
    Guid TargetId,
    string TargetTitle,
    PublishImagePlacementKind PlacementKind,
    string Caption);

public sealed record PublishAssetUpload(
    string FileName,
    string ContentType,
    byte[] Data,
    string AltText);

public sealed record PublishImageGenerationRequest(
    string Prompt,
    string Size,
    string Quality,
    string OutputFormat,
    int? OutputCompression,
    string AltText,
    IReadOnlyList<Guid> ReferenceAssetIds);

public sealed record PublishImagePlacementCreate(
    Guid AssetId,
    PublishOutlineTargetKind TargetKind,
    Guid TargetId,
    PublishImagePlacementKind PlacementKind,
    string Caption);

public sealed record PublishDocument(
    Guid ProjectId,
    string ProjectName,
    string ProjectSlug,
    DateTime ExportedAtUtc,
    PublishDocumentProfile Profile,
    PublishAssetDocument? CoverAsset,
    PublishAssetDocument? RenderedCoverAsset,
    PublishCoverLayoutView CoverLayout,
    IReadOnlyList<PublishSectionDocument> Sections,
    IReadOnlyList<PublishAssetDocument> Assets,
    IReadOnlyList<PublishImagePlacementDocument> Placements)
{
    public string DisplayTitle => string.IsNullOrWhiteSpace(Profile.TitleOverride)
        ? ProjectName
        : Profile.TitleOverride.Trim();
}

public sealed record PublishDocumentProfile(
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
    bool NumberChapters);

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
    string Body,
    string Synopsis,
    int Order,
    bool IncludeHeading,
    ChapterVisualMode VisualMode,
    double PicturePageWidthInches,
    double PicturePageHeightInches,
    bool PicturePageIsSpread,
    IllustratedProseLayout IllustrationLayout,
    PicturePageLayout PageLayout);

public sealed record PublishAssetDocument(
    Guid Id,
    string FileName,
    string ContentType,
    byte[] Data,
    string AltText);

public sealed record PublishImagePlacementDocument(
    Guid Id,
    PublishAssetDocument Asset,
    PublishOutlineTargetKind TargetKind,
    Guid TargetId,
    PublishImagePlacementKind PlacementKind,
    string Caption,
    int SortOrder);
