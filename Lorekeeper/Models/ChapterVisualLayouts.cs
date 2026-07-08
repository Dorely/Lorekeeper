using Lorekeeper.Publish;

namespace Lorekeeper.Models;

public sealed record IllustratedProseLayout(
    IReadOnlyList<IllustratedProseImageBlock> Images);

public sealed record IllustratedProseImageBlock(
    Guid Id,
    Guid ImageId,
    ChapterImageAnchorPosition AnchorPosition,
    int ParagraphIndex,
    string ParagraphHash,
    double WidthPercent,
    ChapterImageAlignment Alignment,
    string Caption,
    string AltTextOverride,
    int SortOrder,
    bool StartOnNewPage);

public sealed record PicturePageLayout(
    IReadOnlyList<PicturePageImageElement> Images,
    IReadOnlyList<PicturePageTextElement> TextElements);

public sealed record PicturePageImageElement(
    Guid Id,
    Guid ImageId,
    double XPercent,
    double YPercent,
    double WidthPercent,
    double HeightPercent,
    ChapterImageFit Fit,
    double Opacity,
    int ZIndex,
    string AltTextOverride);

public sealed record PicturePageTextElement(
    Guid Id,
    string Text,
    double XPercent,
    double YPercent,
    double WidthPercent,
    double HeightPercent,
    int ZIndex,
    int ReadingOrder,
    PublishCoverFontFamily FontFamily,
    double FontSizePercent,
    double LineHeight,
    string Color,
    string BackgroundColor,
    double BackgroundOpacity,
    PublishCoverTextAlign TextAlign,
    ChapterTextVerticalAlign VerticalAlign,
    PublishCoverShadow Shadow);
