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
    IReadOnlyList<PublishCoverCandidateView> CoverCandidates,
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
    Guid? SelectedCoverChapterId);

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

public sealed record PublishImagePlacementView(
    Guid Id,
    Guid AssetId,
    string AssetFileName,
    string AssetPreviewUrl,
    PublishOutlineTargetKind TargetKind,
    Guid TargetId,
    string TargetTitle,
    PublishImagePlacementKind PlacementKind,
    string Caption,
    int SortOrder);

public sealed record PublishOutlineSelectionUpdate(
    PublishOutlineTargetKind TargetKind,
    Guid TargetId,
    bool IsIncluded);

public sealed record PublishImagePlacementCreate(
    Guid AssetId,
    PublishOutlineTargetKind TargetKind,
    Guid TargetId,
    PublishImagePlacementKind PlacementKind,
    string Caption);

public sealed record PublishImagePlacementUpdate(
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
    IReadOnlyList<PublishSectionDocument> Sections,
    IReadOnlyList<PublishAssetDocument> Assets,
    IReadOnlyList<PublishImagePlacementDocument> Placements)
{
    public ChapterPageLayoutKind? CoverPageLayoutKind { get; init; }

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
    ChapterPageLayoutKind PageLayoutKind,
    IllustratedProseLayout IllustrationLayout,
    PicturePageLayout PageLayout)
{
    public PublishPicturePageDocument? RenderedPicturePage { get; init; }
}

public sealed record PublishPicturePageDocument(
    PublishAssetDocument Surface,
    int PageWidthPixels,
    int PageHeightPixels,
    int LeafCount,
    string AccessibleText);

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
