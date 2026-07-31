using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using Lorekeeper.ChapterVisuals;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;

namespace Lorekeeper.Publish;

public sealed class PlainTextPublishFormatter : IPublishExportFormatter
{
    public PublishExportFormat Format => PublishExportFormat.PlainText;
    public string FileExtension => ".txt";
    public string ContentType => "text/plain; charset=utf-8";

    public byte[] Render(PublishDocument document)
    {
        var sb = new StringBuilder();
        AppendCenteredTitle(sb, document);
        AppendMetadata(sb, document);
        AppendMatter(sb, document, PublicationMatterLocation.Front);

        if (document.Profile.IncludeTableOfContents)
            AppendPlainToc(sb, document);

        foreach (var section in document.Sections)
        {
            if (section.IncludePage)
            {
                AppendGap(sb);
                if (section.IncludeHeading)
                    AppendHeading(sb, section.Title, '-');
                if (document.Profile.IncludeActSynopses)
                    AppendText(sb, section.Synopsis);
            }

            foreach (var chapter in section.Chapters)
            {
                AppendGap(sb);
                if (chapter.IncludeHeading)
                    AppendHeading(sb, chapter.Title, '=');
                if (document.Profile.IncludeChapterSynopses)
                    AppendText(sb, chapter.Synopsis);
                AppendVisualText(sb, document, chapter);
            }
        }

        AppendMatter(sb, document, PublicationMatterLocation.Back);
        return Encoding.UTF8.GetBytes(sb.ToString().TrimEnd() + Environment.NewLine);
    }

    private static void AppendCenteredTitle(StringBuilder sb, PublishDocument document)
    {
        AppendHeading(sb, document.DisplayTitle, '=');
        if (!string.IsNullOrWhiteSpace(document.Profile.Subtitle))
            sb.AppendLine(document.Profile.Subtitle.Trim());
        if (!string.IsNullOrWhiteSpace(document.Profile.Author))
            sb.AppendLine().Append("by ").AppendLine(document.Profile.Author.Trim());
    }

    private static void AppendMetadata(StringBuilder sb, PublishDocument document)
    {
        var lines = new[]
        {
            ("Publisher", document.Profile.Publisher),
            ("Copyright", document.Profile.Copyright),
            ("ISBN", document.Profile.Isbn),
            ("Language", document.Profile.Language),
            ("Description", document.Profile.Description),
        };

        foreach (var (label, value) in lines)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            sb.Append(label).Append(": ").AppendLine(value.Trim());
        }
    }

    private static void AppendPlainToc(StringBuilder sb, PublishDocument document)
    {
        AppendMatterStart(sb, "Table of Contents");
        foreach (var section in document.Sections)
        {
            if (section.IncludePage)
                sb.AppendLine(section.Title);
            foreach (var chapter in section.Chapters)
                sb.Append("  ").AppendLine(chapter.Title);
        }
    }

    private static void AppendMatter(StringBuilder sb, string title, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        AppendMatterStart(sb, title);
        AppendText(sb, text);
    }

    private static void AppendMatter(
        StringBuilder sb,
        PublishDocument document,
        PublicationMatterLocation location)
    {
        foreach (var item in document.Matter
            .Where(item => item.Location == location)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Id))
        {
            AppendMatter(
                sb,
                PublicationMatterFormatting.Title(item),
                SemanticPublishFormatting.PlainText(
                    item.Manuscript,
                    imageId => document.Assets.FirstOrDefault(asset => asset.Id == imageId)));
        }
    }

    private static void AppendMatterStart(StringBuilder sb, string title)
    {
        AppendGap(sb);
        AppendHeading(sb, title, '-');
    }

    private static void AppendText(StringBuilder sb, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        sb.AppendLine();
        sb.AppendLine(text.Trim());
    }

    private static void AppendVisualText(StringBuilder sb, PublishDocument document, PublishChapterDocument chapter)
    {
        if (chapter.VisualMode == ChapterVisualMode.PicturePage)
        {
            AppendText(sb, chapter.PlainText);
            foreach (var image in chapter.PageLayout.Images.OrderBy(image => image.ZIndex))
            {
                if (FindAsset(document, image.ImageId) is { } asset)
                    sb.AppendLine().Append("[Picture page image: ").Append(asset.FileName).AppendLine("]");
            }
            return;
        }

        AppendText(
            sb,
            SemanticPublishFormatting.PlainText(
                chapter.Manuscript,
                imageId => FindAsset(document, imageId)));
        foreach (var image in chapter.IllustrationLayout.Images.OrderBy(image => image.SortOrder))
        {
            if (FindAsset(document, image.ImageId) is { } asset)
                sb.AppendLine().Append("[Illustration: ").Append(asset.FileName).AppendLine(string.IsNullOrWhiteSpace(image.Caption) ? "]" : $" - {image.Caption}]");
        }
    }

    private static PublishAssetDocument? FindAsset(PublishDocument document, Guid imageId) =>
        document.Assets.FirstOrDefault(asset => asset.Id == imageId);

    private static void AppendGap(StringBuilder sb)
    {
        if (sb.Length > 0)
            sb.AppendLine().AppendLine();
    }

    private static void AppendHeading(StringBuilder sb, string heading, char underline)
    {
        var title = CleanHeading(heading);
        sb.AppendLine(title);
        sb.AppendLine(new string(underline, Math.Max(3, title.Length)));
    }

    private static string CleanHeading(string heading) =>
        heading.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
}

public sealed class MarkdownPublishFormatter : IPublishExportFormatter
{
    public PublishExportFormat Format => PublishExportFormat.Markdown;
    public string FileExtension => ".md";
    public string ContentType => "text/markdown; charset=utf-8";

    public byte[] Render(PublishDocument document)
    {
        var sb = new StringBuilder();
        var cover = document.CoverAsset;
        if (cover is not null)
            AppendImage(sb, cover, "Cover");

        sb.Append("# ").AppendLine(EscapeHeading(document.DisplayTitle));
        if (!string.IsNullOrWhiteSpace(document.Profile.Subtitle))
            sb.AppendLine().Append("## ").AppendLine(EscapeHeading(document.Profile.Subtitle));
        if (!string.IsNullOrWhiteSpace(document.Profile.Author))
            sb.AppendLine().Append("_by ").Append(EscapeInline(document.Profile.Author)).AppendLine("_");
        AppendMetadata(sb, document);
        AppendMatter(sb, document, PublicationMatterLocation.Front);

        if (document.Profile.IncludeTableOfContents)
            AppendToc(sb, document);

        foreach (var section in document.Sections)
        {
            AppendPlacements(sb, document, PublishOutlineTargetKind.Act, section.ActId, PublicationImagePlacementKind.BeforeAct);
            if (section.IncludePage)
            {
                if (section.IncludeHeading)
                    sb.AppendLine().Append("## ").AppendLine(EscapeHeading(section.Title));
                if (document.Profile.IncludeActSynopses)
                    AppendBlockquote(sb, section.Synopsis);
            }

            AppendPlacements(sb, document, PublishOutlineTargetKind.Act, section.ActId, PublicationImagePlacementKind.AfterAct);

            foreach (var chapter in section.Chapters)
            {
                AppendPlacements(sb, document, PublishOutlineTargetKind.Chapter, chapter.Id, PublicationImagePlacementKind.BeforeChapter);
                sb.AppendLine();
                if (chapter.IncludeHeading)
                    sb.Append("### ").AppendLine(EscapeHeading(chapter.Title));
                if (document.Profile.IncludeChapterSynopses)
                    AppendBlockquote(sb, chapter.Synopsis);
                AppendPlacements(sb, document, PublishOutlineTargetKind.Chapter, chapter.Id, PublicationImagePlacementKind.ChapterOpening);
                AppendVisualMarkdown(sb, document, chapter);
                AppendPlacements(sb, document, PublishOutlineTargetKind.Chapter, chapter.Id, PublicationImagePlacementKind.ChapterEnding);
                AppendPlacements(sb, document, PublishOutlineTargetKind.Chapter, chapter.Id, PublicationImagePlacementKind.AfterChapter);
            }
        }

        AppendMatter(sb, document, PublicationMatterLocation.Back);
        return Encoding.UTF8.GetBytes(sb.ToString().TrimEnd() + Environment.NewLine);
    }

    private static void AppendMetadata(StringBuilder sb, PublishDocument document)
    {
        var lines = new[]
        {
            ("Publisher", document.Profile.Publisher),
            ("Copyright", document.Profile.Copyright),
            ("ISBN", document.Profile.Isbn),
            ("Language", document.Profile.Language),
            ("Description", document.Profile.Description),
        };

        foreach (var (label, value) in lines)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            sb.AppendLine().Append("**").Append(label).Append(":** ").AppendLine(EscapeInline(value));
        }
    }

    private static void AppendToc(StringBuilder sb, PublishDocument document)
    {
        sb.AppendLine().AppendLine("## Table of Contents");
        foreach (var section in document.Sections)
        {
            if (section.IncludeHeading)
                sb.Append("- [").Append(EscapeInline(section.Title)).Append("](#").Append(Anchor(section.Title)).AppendLine(")");
            foreach (var chapter in section.Chapters)
            {
                if (chapter.IncludeHeading)
                    sb.Append("  - [").Append(EscapeInline(chapter.Title)).Append("](#").Append(Anchor(chapter.Title)).AppendLine(")");
            }
        }
    }

    private static void AppendMatter(StringBuilder sb, string title, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        sb.AppendLine().Append("## ").AppendLine(EscapeHeading(title)).AppendLine();
        foreach (var line in SplitLines(text.TrimEnd()))
            sb.AppendLine(line);
    }

    private static void AppendMatter(
        StringBuilder sb,
        PublishDocument document,
        PublicationMatterLocation location)
    {
        foreach (var item in document.Matter
            .Where(item => item.Location == location)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Id))
        {
            AppendMatter(
                sb,
                PublicationMatterFormatting.Title(item),
                SemanticPublishFormatting.Markdown(
                    item.Manuscript,
                    imageId => document.Assets.FirstOrDefault(asset => asset.Id == imageId)));
        }
    }

    private static void AppendBlockquote(StringBuilder sb, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        sb.AppendLine();
        foreach (var line in SplitLines(text.Trim()))
            sb.Append("> ").AppendLine(EscapeInline(line));
    }

    private static void AppendPlacements(
        StringBuilder sb,
        PublishDocument document,
        PublishOutlineTargetKind targetKind,
        Guid? targetId,
        PublicationImagePlacementKind placementKind)
    {
        if (targetId is null) return;
        foreach (var placement in document.Placements.Where(placement =>
            placement.TargetKind == targetKind
            && placement.TargetId == targetId
            && placement.PlacementKind == placementKind).OrderBy(placement => placement.SortOrder))
        {
            AppendImage(sb, placement.Asset, placement.Caption);
        }
    }

    private static void AppendImage(StringBuilder sb, PublishAssetDocument asset, string caption)
    {
        var alt = string.IsNullOrWhiteSpace(asset.AltText) ? caption : asset.AltText;
        var dataUrl = $"data:{asset.ContentType};base64,{Convert.ToBase64String(asset.Data)}";
        sb.AppendLine().Append("![").Append(EscapeInline(alt)).Append("](").Append(dataUrl).AppendLine(")");
        if (!string.IsNullOrWhiteSpace(caption))
            sb.Append("_").Append(EscapeInline(caption)).AppendLine("_");
    }

    private static void AppendVisualMarkdown(StringBuilder sb, PublishDocument document, PublishChapterDocument chapter)
    {
        if (chapter.VisualMode == ChapterVisualMode.PicturePage)
        {
            sb.AppendLine();
            sb.AppendLine("> Picture page");
            foreach (var text in chapter.PageLayout.TextElements.OrderBy(text => text.ReadingOrder))
                sb.AppendLine().AppendLine(EscapeInline(text.Text.TrimEnd()));
            foreach (var image in chapter.PageLayout.Images.OrderBy(image => image.ZIndex))
            {
                if (FindAsset(document, image.ImageId) is { } asset)
                    AppendImage(sb, asset, string.Empty);
            }
            return;
        }

        var illustrationsByBlock = chapter.IllustrationLayout.Images
            .GroupBy(block => (block.BlockId, block.AnchorPosition))
            .ToDictionary(group => group.Key, group => group.OrderBy(block => block.SortOrder).ToList());
        foreach (var manuscriptBlock in chapter.Manuscript.Content)
        {
            AppendIllustrationBlocks(
                sb,
                document,
                illustrationsByBlock,
                manuscriptBlock.Id,
                ChapterImageAnchorPosition.BeforeParagraph);
            sb.AppendLine().AppendLine(
                SemanticPublishFormatting.MarkdownBlock(
                    manuscriptBlock,
                    imageId => FindAsset(document, imageId)));
            AppendIllustrationBlocks(
                sb,
                document,
                illustrationsByBlock,
                manuscriptBlock.Id,
                ChapterImageAnchorPosition.AfterParagraph);
        }
    }

    private static void AppendIllustrationBlocks(
        StringBuilder sb,
        PublishDocument document,
        IReadOnlyDictionary<(string BlockId, ChapterImageAnchorPosition Position), List<IllustratedProseImageBlock>> blocksByBlock,
        string blockId,
        ChapterImageAnchorPosition position)
    {
        if (!blocksByBlock.TryGetValue((blockId, position), out var blocks)) return;
        foreach (var block in blocks)
        {
            if (FindAsset(document, block.ImageId) is { } asset)
                AppendImage(sb, asset, block.Caption);
        }
    }

    private static IReadOnlyList<string> SplitMarkdownParagraphs(string text)
    {
        var paragraphs = new List<string>();
        var current = new StringBuilder();
        foreach (var line in SplitLines(text))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                Flush();
                continue;
            }

            if (current.Length > 0) current.AppendLine();
            current.Append(line.TrimEnd());
        }

        Flush();
        return paragraphs;

        void Flush()
        {
            if (current.Length == 0) return;
            paragraphs.Add(current.ToString());
            current.Clear();
        }
    }

    private static PublishAssetDocument? FindAsset(PublishDocument document, Guid imageId) =>
        document.Assets.FirstOrDefault(asset => asset.Id == imageId);

    private static string Anchor(string value)
    {
        var chars = value.Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();
        return string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    private static IReadOnlyList<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

    private static string EscapeHeading(string heading) =>
        EscapeInline(
            heading.Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal));

    private static string EscapeInline(string value) =>
        SemanticPublishFormatting.EscapeMarkdownLiteral(value).Trim();

}

public sealed class EpubPublishFormatter(IPageGeometryService pageGeometry) : IPublishExportFormatter
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public PublishExportFormat Format => PublishExportFormat.Epub;
    public string FileExtension => ".epub";
    public string ContentType => "application/epub+zip";

    public byte[] Render(PublishDocument document)
    {
        foreach (var item in document.Matter)
            PublicationMatterFormatting.EnsureUserAuthoredKind(item.Kind);
        var imageItems = BuildImageItems(document);
        var xhtmlItems = BuildXhtmlItems(document, imageItems);
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            WriteEntry(archive, "mimetype", ContentType, CompressionLevel.NoCompression, Encoding.ASCII);
            WriteEntry(archive, "META-INF/container.xml", RenderContainer(), CompressionLevel.SmallestSize, Utf8NoBom);
            WriteEntry(archive, "OEBPS/styles.css", RenderStylesheet(document), CompressionLevel.SmallestSize, Utf8NoBom);
            WriteEntry(archive, "OEBPS/package.opf", RenderPackage(document, xhtmlItems, imageItems), CompressionLevel.SmallestSize, Utf8NoBom);
            WriteEntry(archive, "OEBPS/nav.xhtml", RenderNavigation(document, xhtmlItems), CompressionLevel.SmallestSize, Utf8NoBom);

            foreach (var item in xhtmlItems)
                WriteEntry(archive, $"OEBPS/{item.Href}", item.Content, CompressionLevel.SmallestSize, Utf8NoBom);
            foreach (var item in imageItems)
                WriteEntry(archive, $"OEBPS/{item.Href}", item.Asset.Data, CompressionLevel.SmallestSize);
        }

        return stream.ToArray();
    }

    private List<EpubXhtmlItem> BuildXhtmlItems(PublishDocument document, IReadOnlyList<EpubImageItem> imageItems)
    {
        var items = new List<EpubXhtmlItem>();
        if (CoverImageHref(imageItems) is string coverHref)
        {
            var viewport = CoverViewport(PageGeometry(document.Profile, document.CoverPageLayoutKind));
            items.Add(new EpubXhtmlItem(
                "cover-page",
                "cover.xhtml",
                "Cover",
                RenderXhtmlPage(document, "Cover", RenderCoverBody(document, coverHref, viewport), viewport, "fixed-layout"),
                IncludeInNavigation: false,
                SpineProperties: "rendition:layout-pre-paginated rendition:spread-none"));
        }

        if (document.Profile.IncludeTitlePage)
            items.Add(new EpubXhtmlItem("title", "title.xhtml", document.DisplayTitle, RenderXhtmlPage(document, document.DisplayTitle, RenderTitleBody(document))));
        if (!string.IsNullOrWhiteSpace(document.Profile.Description))
        {
            items.Add(new EpubXhtmlItem(
                "description",
                "description.xhtml",
                "Description",
                RenderXhtmlPage(document, "Description", RenderMatterBody("Description", document.Profile.Description)),
                IncludeInNavigation: false));
        }
        AddMatter(items, document, imageItems, PublicationMatterLocation.Front);
        if (document.Profile.IncludeTableOfContents && document.Profile.IncludeVisibleTableOfContents)
            items.Add(new EpubXhtmlItem("toc-page", "toc.xhtml", "Table of Contents", RenderXhtmlPage(document, "Table of Contents", RenderVisibleToc(document))));

        var actIndex = 0;
        var chapterIndex = 0;
        foreach (var section in document.Sections)
        {
            actIndex++;
            if (section.IncludePage)
            {
                var actId = section.IsUnassigned ? "section-unassigned" : $"act-{actIndex.ToString(CultureInfo.InvariantCulture)}";
                items.Add(new EpubXhtmlItem(
                    actId,
                    $"{actId}.xhtml",
                    section.Title,
                    RenderXhtmlPage(document, section.Title, RenderActBody(document, section, imageItems))));
            }

            foreach (var chapter in section.Chapters)
            {
                chapterIndex++;
                var chapterId = $"chapter-{chapterIndex.ToString(CultureInfo.InvariantCulture)}";
                if (chapter.VisualMode == ChapterVisualMode.PicturePage)
                {
                    AddPicturePageItems(items, document, chapterId, chapter, imageItems);
                    continue;
                }

                items.Add(new EpubXhtmlItem(
                    chapterId,
                    $"{chapterId}.xhtml",
                    chapter.Title,
                    RenderXhtmlPage(document, chapter.Title, RenderChapterBody(document, chapter, imageItems))));
            }
        }

        AddMatter(items, document, imageItems, PublicationMatterLocation.Back);
        return items;
    }

    private static List<EpubImageItem> BuildImageItems(PublishDocument document)
    {
        var items = new List<EpubImageItem>();
        var picturePages = new List<(Guid ChapterId, PublishAssetDocument Surface)>();
        var cover = document.CoverAsset;
        if (cover is not null)
            items.Add(new EpubImageItem("cover-image", $"images/cover.{ImageExtension(cover.ContentType)}", cover, IsCover: true));

        var assets = new Dictionary<Guid, PublishAssetDocument>();
        foreach (var placement in document.Placements)
            assets[placement.Asset.Id] = placement.Asset;
        foreach (var chapter in document.Sections.SelectMany(section => section.Chapters))
        {
            if (chapter.VisualMode == ChapterVisualMode.PicturePage)
            {
                if (chapter.RenderedPicturePage is { } picturePage)
                    picturePages.Add((chapter.Id, picturePage.Surface));
                continue;
            }

            foreach (var block in chapter.IllustrationLayout.Images)
            {
                if (document.Assets.FirstOrDefault(asset => asset.Id == block.ImageId) is { } asset)
                    assets[asset.Id] = asset;
            }
            foreach (var block in chapter.Manuscript.Content.Where(block =>
                block.Type == ManuscriptBlockType.Figure))
            {
                if (block.ImageId is Guid imageId
                    && document.Assets.FirstOrDefault(asset => asset.Id == imageId) is { } asset)
                {
                    assets[asset.Id] = asset;
                }
            }
        }
        foreach (var block in document.Matter.SelectMany(item => item.Manuscript.Content))
        {
            if (block.Type == ManuscriptBlockType.Figure
                && document.Assets.FirstOrDefault(asset => asset.Id == block.ImageId) is { } asset)
            {
                assets[asset.Id] = asset;
            }
        }

        items.AddRange(assets.Values
            .Select(asset => new EpubImageItem($"img-{asset.Id:N}", $"images/{asset.Id:N}.{ImageExtension(asset.ContentType)}", asset, IsCover: false)));
        items.AddRange(picturePages.Select(picturePage => new EpubImageItem(
            PicturePageImageId(picturePage.ChapterId),
            $"images/picture-page-{picturePage.ChapterId:N}.{ImageExtension(picturePage.Surface.ContentType)}",
            picturePage.Surface,
            IsCover: false)));
        return items;
    }

    private static void AddPicturePageItems(
        List<EpubXhtmlItem> items,
        PublishDocument document,
        string chapterId,
        PublishChapterDocument chapter,
        IReadOnlyList<EpubImageItem> imageItems)
    {
        var picturePage = chapter.RenderedPicturePage
            ?? throw new InvalidOperationException($"Picture Page chapter '{chapter.Title}' does not have a rendered publishing surface.");
        var expectedLeafCount = IsDoubleLayout(chapter.PageLayoutKind) ? 2 : 1;
        if (picturePage.LeafCount != expectedLeafCount)
        {
            throw new InvalidOperationException(
                $"Picture Page chapter '{chapter.Title}' rendered {picturePage.LeafCount} leaves, but {chapter.PageLayoutKind} requires {expectedLeafCount}.");
        }
        if (picturePage.PhysicalPageWidthPixels <= 0 || picturePage.PhysicalPageHeightPixels <= 0)
            throw new InvalidOperationException($"Picture Page chapter '{chapter.Title}' has invalid rendered page dimensions.");

        var surfaceHref = PicturePageImageHref(imageItems, chapter.Id)
            ?? throw new InvalidOperationException($"Picture Page chapter '{chapter.Title}' is missing its rendered surface asset.");

        AddPicturePageCompanion(
            items,
            document,
            chapter,
            imageItems,
            $"{chapterId}-before",
            [PublicationImagePlacementKind.BeforeChapter, PublicationImagePlacementKind.ChapterOpening]);

        if (picturePage.SurfaceWidthPixels <= 0 || picturePage.SurfaceHeightPixels <= 0)
            throw new InvalidOperationException($"Picture Page chapter '{chapter.Title}' has invalid rendered surface dimensions.");
        var expectedRotation = document.Profile.EpubPicturePageSpreadMode == EpubPicturePageSpreadMode.SidewaysPortrait
            && picturePage.LeafCount == 2
                ? ChapterPicturePageSurfaceRotation.Clockwise90
                : ChapterPicturePageSurfaceRotation.None;
        if (picturePage.Rotation != expectedRotation)
        {
            throw new InvalidOperationException(
                $"Picture Page chapter '{chapter.Title}' rendered with {picturePage.Rotation}, but the EPUB profile requires {expectedRotation}.");
        }

        var viewport = new EpubViewport(picturePage.SurfaceWidthPixels, picturePage.SurfaceHeightPixels);
        items.Add(new EpubXhtmlItem(
            chapterId,
            $"{chapterId}.xhtml",
            chapter.Title,
            RenderXhtmlPage(
                document,
                chapter.Title,
                RenderPicturePageBody(chapter, picturePage, surfaceHref),
                viewport,
                "fixed-layout"),
            SpineProperties: PicturePageSpineProperties(document.Profile.EpubPicturePageSpreadMode, picturePage.LeafCount)));

        AddPicturePageCompanion(
            items,
            document,
            chapter,
            imageItems,
            $"{chapterId}-after",
            [PublicationImagePlacementKind.ChapterEnding, PublicationImagePlacementKind.AfterChapter]);
    }

    private static void AddPicturePageCompanion(
        List<EpubXhtmlItem> items,
        PublishDocument document,
        PublishChapterDocument chapter,
        IReadOnlyList<EpubImageItem> imageItems,
        string id,
        IReadOnlyList<PublicationImagePlacementKind> placementKinds)
    {
        var figures = new StringBuilder();
        foreach (var placementKind in placementKinds)
        {
            AppendFigures(
                figures,
                document,
                imageItems,
                PublishOutlineTargetKind.Chapter,
                chapter.Id,
                placementKind);
        }

        if (figures.Length == 0) return;

        var body = $"<section class=\"picture-page-companion\">{Environment.NewLine}{figures}</section>";
        items.Add(new EpubXhtmlItem(
            id,
            $"{id}.xhtml",
            chapter.Title,
            RenderXhtmlPage(document, chapter.Title, body),
            IncludeInNavigation: false));
    }

    private static void AddMatter(
        List<EpubXhtmlItem> items,
        PublishDocument document,
        IReadOnlyList<EpubImageItem> imageItems,
        PublicationMatterLocation location)
    {
        foreach (var item in document.Matter
            .Where(item => item.Location == location)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Id))
        {
            var id = $"matter-{item.Id:N}";
            var title = PublicationMatterFormatting.Title(item);
            var content = SemanticPublishFormatting.Html(
                item.Manuscript,
                imageId => ImageHref(imageItems, imageId));
            items.Add(new EpubXhtmlItem(
                id,
                $"{id}.xhtml",
                title,
                RenderXhtmlPage(document, title, RenderSemanticMatterBody(title, content))));
        }
    }

    private static string RenderTitleBody(PublishDocument document)
    {
        var sb = new StringBuilder();
        sb.Append("<section class=\"title-page\"><h1>").Append(Html(document.DisplayTitle)).AppendLine("</h1>");
        if (!string.IsNullOrWhiteSpace(document.Profile.Subtitle))
            sb.Append("<p class=\"subtitle\">").Append(Html(document.Profile.Subtitle)).AppendLine("</p>");
        if (!string.IsNullOrWhiteSpace(document.Profile.Author))
            sb.Append("<p class=\"byline\">by ").Append(Html(document.Profile.Author)).AppendLine("</p>");
        if (!string.IsNullOrWhiteSpace(document.Profile.Publisher))
            sb.Append("<p class=\"publisher\">").Append(Html(document.Profile.Publisher)).AppendLine("</p>");
        sb.AppendLine("</section>");
        return sb.ToString();
    }

    private static string RenderCoverBody(PublishDocument document, string coverHref, EpubViewport viewport)
    {
        var cover = document.CoverAsset;
        if (cover is null) return string.Empty;

        var alt = string.IsNullOrWhiteSpace(cover.AltText) ? "Cover" : cover.AltText;
        var sb = new StringBuilder();
        sb.AppendLine("<section class=\"fixed-page-surface\">");
        sb.Append("<img class=\"fixed-page-image fixed-page-image-whole\" alt=\"").Append(Html(alt))
            .Append("\" src=\"").Append(Html(coverHref)).Append("\" width=\"")
            .Append(viewport.Width).Append("\" height=\"").Append(viewport.Height).AppendLine("\" />");
        sb.AppendLine("</section>");
        return sb.ToString();
    }

    private static string RenderPicturePageBody(
        PublishChapterDocument chapter,
        PublishPicturePageDocument picturePage,
        string surfaceHref)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<section class=\"fixed-page-surface\">");
        sb.Append("<img class=\"fixed-page-image fixed-page-image-whole\" alt=\"\" aria-hidden=\"true\" src=\"")
            .Append(Html(surfaceHref)).Append("\" width=\"").Append(picturePage.SurfaceWidthPixels)
            .Append("\" height=\"").Append(picturePage.SurfaceHeightPixels).AppendLine("\" />");
        sb.AppendLine("<div class=\"fixed-page-accessible\">");
        sb.Append("<h1>").Append(Html(chapter.Title)).AppendLine("</h1>");
        AppendTextBlocks(sb, picturePage.AccessibleText, "picture-page-transcript");
        sb.AppendLine("</div>");
        sb.AppendLine("</section>");
        return sb.ToString();
    }

    private static string RenderVisibleToc(PublishDocument document)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""<nav epub:type="toc" id="visible-toc">""");
        sb.AppendLine("<h1>Table of Contents</h1><ol>");
        foreach (var link in TocLinks(document))
            sb.Append("<li><a href=\"").Append(link.Href).Append("\">").Append(Html(link.Title)).AppendLine("</a></li>");
        sb.AppendLine("</ol></nav>");
        return sb.ToString();
    }

    private static string RenderActBody(PublishDocument document, PublishSectionDocument section, IReadOnlyList<EpubImageItem> imageItems)
    {
        var sb = new StringBuilder();
        AppendFigures(sb, document, imageItems, PublishOutlineTargetKind.Act, section.ActId, PublicationImagePlacementKind.BeforeAct);
        sb.AppendLine("<section class=\"act-page\">");
        if (section.IncludeHeading)
            sb.Append("<h1>").Append(Html(section.Title)).AppendLine("</h1>");
        if (document.Profile.IncludeActSynopses)
            AppendTextBlocks(sb, section.Synopsis, "synopsis");
        sb.AppendLine("</section>");
        AppendFigures(sb, document, imageItems, PublishOutlineTargetKind.Act, section.ActId, PublicationImagePlacementKind.AfterAct);
        return sb.ToString();
    }

    private static string RenderChapterBody(PublishDocument document, PublishChapterDocument chapter, IReadOnlyList<EpubImageItem> imageItems)
    {
        var sb = new StringBuilder();
        AppendFigures(sb, document, imageItems, PublishOutlineTargetKind.Chapter, chapter.Id, PublicationImagePlacementKind.BeforeChapter);
        sb.AppendLine("<article class=\"chapter-page\">");
        if (chapter.IncludeHeading)
            sb.Append("<h1>").Append(Html(chapter.Title)).AppendLine("</h1>");
        if (document.Profile.IncludeChapterSynopses)
            AppendTextBlocks(sb, chapter.Synopsis, "synopsis");
        AppendFigures(sb, document, imageItems, PublishOutlineTargetKind.Chapter, chapter.Id, PublicationImagePlacementKind.ChapterOpening);
        AppendVisualChapterBody(sb, chapter, imageItems);
        AppendFigures(sb, document, imageItems, PublishOutlineTargetKind.Chapter, chapter.Id, PublicationImagePlacementKind.ChapterEnding);
        sb.AppendLine("</article>");
        AppendFigures(sb, document, imageItems, PublishOutlineTargetKind.Chapter, chapter.Id, PublicationImagePlacementKind.AfterChapter);
        return sb.ToString();
    }

    private static string RenderMatterBody(string title, string text)
    {
        var sb = new StringBuilder();
        sb.Append("<section class=\"matter-page\"><h1>").Append(Html(title)).AppendLine("</h1>");
        AppendTextBlocks(sb, text, "prose");
        sb.AppendLine("</section>");
        return sb.ToString();
    }

    private static string RenderSemanticMatterBody(string title, string content) =>
        $"<section class=\"matter-page\"><h1>{Html(title)}</h1>{content}</section>";

    private static void AppendFigures(
        StringBuilder sb,
        PublishDocument document,
        IReadOnlyList<EpubImageItem> imageItems,
        PublishOutlineTargetKind targetKind,
        Guid? targetId,
        PublicationImagePlacementKind placementKind)
    {
        if (targetId is null) return;
        foreach (var placement in document.Placements.Where(placement =>
            placement.TargetKind == targetKind
            && placement.TargetId == targetId
            && placement.PlacementKind == placementKind).OrderBy(placement => placement.SortOrder))
        {
            var href = ImageHref(imageItems, placement.Asset.Id);
            if (href is null) continue;
            var alt = string.IsNullOrWhiteSpace(placement.Asset.AltText) ? placement.Caption : placement.Asset.AltText;
            sb.Append("<figure><img alt=\"").Append(Html(alt)).Append("\" src=\"").Append(href).AppendLine("\" />");
            if (!string.IsNullOrWhiteSpace(placement.Caption))
                sb.Append("<figcaption>").Append(Html(placement.Caption)).AppendLine("</figcaption>");
            sb.AppendLine("</figure>");
        }
    }

    private static void AppendVisualChapterBody(StringBuilder sb, PublishChapterDocument chapter, IReadOnlyList<EpubImageItem> imageItems)
    {
        sb.AppendLine("<div class=\"chapter-body\">");
        var illustrationsByBlock = chapter.IllustrationLayout.Images
            .GroupBy(block => (block.BlockId, block.AnchorPosition))
            .ToDictionary(group => group.Key, group => group.OrderBy(block => block.SortOrder).ToList());
        foreach (var manuscriptBlock in chapter.Manuscript.Content)
        {
            AppendIllustrationFigures(
                sb,
                imageItems,
                illustrationsByBlock,
                manuscriptBlock.Id,
                ChapterImageAnchorPosition.BeforeParagraph);
            sb.Append(SemanticPublishFormatting.HtmlBlock(
                manuscriptBlock,
                imageId => ImageHref(imageItems, imageId)));
            AppendIllustrationFigures(
                sb,
                imageItems,
                illustrationsByBlock,
                manuscriptBlock.Id,
                ChapterImageAnchorPosition.AfterParagraph);
        }

        sb.AppendLine("</div>");
    }

    private static void AppendIllustrationFigures(
        StringBuilder sb,
        IReadOnlyList<EpubImageItem> imageItems,
        IReadOnlyDictionary<(string BlockId, ChapterImageAnchorPosition Position), List<IllustratedProseImageBlock>> blocksByBlock,
        string blockId,
        ChapterImageAnchorPosition position)
    {
        if (!blocksByBlock.TryGetValue((blockId, position), out var blocks)) return;
        foreach (var block in blocks)
            AppendAssetFigure(sb, imageItems, block.ImageId, block.Caption, block.AltTextOverride);
    }

    private static void AppendAssetFigure(
        StringBuilder sb,
        IReadOnlyList<EpubImageItem> imageItems,
        Guid imageId,
        string caption,
        string altTextOverride)
    {
        var image = imageItems.FirstOrDefault(candidate => !candidate.IsCover && candidate.Asset.Id == imageId);
        if (image is null) return;
        var alt = string.IsNullOrWhiteSpace(altTextOverride) ? image.Asset.AltText : altTextOverride;

        sb.Append("<figure class=\"figure\"><img src=\"").Append(Html(image.Href))
            .Append("\" alt=\"").Append(Html(alt)).AppendLine("\" />");
        if (!string.IsNullOrWhiteSpace(caption))
            sb.Append("<figcaption>").Append(Html(caption)).AppendLine("</figcaption>");
        sb.AppendLine("</figure>");
    }

    private static void AppendParagraph(StringBuilder sb, IReadOnlyList<string> paragraph, string cssClass)
    {
        sb.Append("<p class=\"").Append(cssClass).Append("\">");
        for (var i = 0; i < paragraph.Count; i++)
        {
            if (i > 0) sb.Append("<br />");
            sb.Append(Html(paragraph[i]));
        }
        sb.AppendLine("</p>");
    }

    private static string PicturePageSpineProperties(EpubPicturePageSpreadMode mode, int leafCount) => leafCount == 1
        ? "rendition:layout-pre-paginated rendition:spread-none"
        : mode switch
    {
        EpubPicturePageSpreadMode.SidewaysPortrait =>
            "rendition:layout-pre-paginated rendition:orientation-portrait rendition:spread-none",
        _ => "rendition:layout-pre-paginated rendition:orientation-landscape rendition:spread-none",
    };

    private BookPageGeometry PageGeometry(PublishDocumentProfile profile, ChapterPageLayoutKind? layoutKind) =>
        pageGeometry.Calculate(
            profile.PageWidthInches,
            profile.PageHeightInches,
            profile.PageMarginInches,
            profile.BodyFontSizePoints,
            profile.BodyLineHeight,
            layoutKind ?? ChapterPageLayoutKind.SinglePortrait);

    private static EpubViewport CoverViewport(BookPageGeometry geometry)
    {
        const int physicalPageLongEdgePixels = 2400;
        var scale = physicalPageLongEdgePixels / Math.Max(geometry.PageWidthInches, geometry.PageHeightInches);
        var pageWidth = Math.Max(1, (int)Math.Round(geometry.PageWidthInches * scale));
        var pageHeight = Math.Max(1, (int)Math.Round(geometry.PageHeightInches * scale));
        return new EpubViewport(pageWidth * (geometry.IsDouble ? 2 : 1), pageHeight);
    }

    private static bool IsDoubleLayout(ChapterPageLayoutKind layoutKind) =>
        layoutKind is ChapterPageLayoutKind.DoublePortrait or ChapterPageLayoutKind.DoubleLandscape;

    private static string RenderXhtmlPage(
        PublishDocument document,
        string title,
        string body,
        EpubViewport? viewport = null,
        string bodyClass = "")
    {
        var viewportMeta = viewport is null
            ? string.Empty
            : $"  <meta name=\"viewport\" content=\"width={viewport.Width}, height={viewport.Height}\" />{Environment.NewLine}";
        var bodyClassAttribute = string.IsNullOrWhiteSpace(bodyClass)
            ? string.Empty
            : $" class=\"{Html(bodyClass)}\"";
        return $"""
        <?xml version="1.0" encoding="utf-8"?>
        <!DOCTYPE html>
        <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops" xml:lang="{Html(Language(document))}" lang="{Html(Language(document))}">
        <head>
          <title>{Html(title)}</title>
        {viewportMeta}  <link rel="stylesheet" type="text/css" href="styles.css" />
        </head>
        <body{bodyClassAttribute}>
        {body}
        </body>
        </html>
        """;
    }

    private static string RenderContainer() =>
        """
        <?xml version="1.0" encoding="utf-8"?>
        <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
          <rootfiles>
            <rootfile full-path="OEBPS/package.opf" media-type="application/oebps-package+xml" />
          </rootfiles>
        </container>
        """;

    private static string RenderPackage(PublishDocument document, IReadOnlyList<EpubXhtmlItem> xhtmlItems, IReadOnlyList<EpubImageItem> imageItems)
    {
        var modified = document.ExportedAtUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.AppendLine("""<?xml version="1.0" encoding="utf-8"?>""");
        sb.AppendLine("""<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="book-id">""");
        sb.AppendLine("""  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">""");
        if (string.IsNullOrWhiteSpace(document.Profile.Isbn))
        {
            sb.Append("    <dc:identifier id=\"book-id\">urn:uuid:").Append(document.EditionId).AppendLine("</dc:identifier>");
        }
        else
        {
            sb.Append("    <dc:identifier id=\"book-id\">urn:isbn:").Append(Html(document.Profile.Isbn)).AppendLine("</dc:identifier>");
            sb.Append("    <dc:identifier id=\"edition-id\">urn:uuid:").Append(document.EditionId).AppendLine("</dc:identifier>");
        }
        sb.Append("    <dc:title>").Append(Html(document.DisplayTitle)).AppendLine("</dc:title>");
        if (!string.IsNullOrWhiteSpace(document.Profile.Author))
            sb.Append("    <dc:creator>").Append(Html(document.Profile.Author)).AppendLine("</dc:creator>");
        if (!string.IsNullOrWhiteSpace(document.Profile.Publisher))
            sb.Append("    <dc:publisher>").Append(Html(document.Profile.Publisher)).AppendLine("</dc:publisher>");
        if (!string.IsNullOrWhiteSpace(document.Profile.Description))
            sb.Append("    <dc:description>").Append(Html(document.Profile.Description)).AppendLine("</dc:description>");
        if (!string.IsNullOrWhiteSpace(document.Profile.Copyright))
            sb.Append("    <dc:rights>").Append(Html(document.Profile.Copyright)).AppendLine("</dc:rights>");
        sb.Append("    <dc:language>").Append(Html(Language(document))).AppendLine("</dc:language>");
        sb.Append("    <meta property=\"dcterms:modified\">").Append(modified).AppendLine("</meta>");
        sb.AppendLine("    <meta property=\"rendition:layout\">reflowable</meta>");
        if (CoverImageId(imageItems) is string coverImageId)
            sb.Append("    <meta name=\"cover\" content=\"").Append(coverImageId).AppendLine("\" />");
        sb.AppendLine("  </metadata>");
        sb.AppendLine("  <manifest>");
        sb.AppendLine("""    <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav" />""");
        sb.AppendLine("""    <item id="style" href="styles.css" media-type="text/css" />""");
        foreach (var image in imageItems)
        {
            sb.Append("    <item id=\"").Append(image.Id).Append("\" href=\"").Append(image.Href)
                .Append("\" media-type=\"").Append(image.Asset.ContentType).Append("\"");
            if (image.IsCover)
                sb.Append(" properties=\"cover-image\"");
            sb.AppendLine(" />");
        }
        foreach (var item in xhtmlItems)
        {
            sb.Append("    <item id=\"").Append(item.Id).Append("\" href=\"").Append(item.Href)
                .AppendLine("\" media-type=\"application/xhtml+xml\" />");
        }
        sb.AppendLine("  </manifest>");
        sb.AppendLine("  <spine page-progression-direction=\"ltr\">");
        foreach (var item in xhtmlItems)
        {
            sb.Append("    <itemref idref=\"").Append(item.Id).Append('"');
            if (!string.IsNullOrWhiteSpace(item.SpineProperties))
                sb.Append(" properties=\"").Append(item.SpineProperties).Append('"');
            sb.AppendLine(" />");
        }
        sb.AppendLine("  </spine>");
        sb.AppendLine("</package>");
        return sb.ToString();
    }

    private static string RenderNavigation(PublishDocument document, IReadOnlyList<EpubXhtmlItem> xhtmlItems)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""<?xml version="1.0" encoding="utf-8"?>""");
        sb.AppendLine($"""<html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops" xml:lang="{Html(Language(document))}" lang="{Html(Language(document))}">""");
        sb.AppendLine("<head>");
        sb.Append("  <title>").Append(Html(document.DisplayTitle)).AppendLine(" - Table of Contents</title>");
        sb.AppendLine("""  <link rel="stylesheet" type="text/css" href="styles.css" />""");
        sb.AppendLine("</head><body>");
        sb.AppendLine("""<nav epub:type="toc" id="toc">""");
        sb.AppendLine("<h1>Table of Contents</h1><ol>");
        foreach (var item in xhtmlItems.Where(item => item.IncludeInNavigation))
            sb.Append("<li><a href=\"").Append(item.Href).Append("\">").Append(Html(item.Title)).AppendLine("</a></li>");
        sb.AppendLine("</ol></nav>");
        sb.AppendLine("""<nav epub:type="landmarks" id="landmarks">""");
        sb.AppendLine("<h2>Landmarks</h2><ol>");
        if (xhtmlItems.FirstOrDefault(item => item.Id == "cover-page") is { } cover)
            sb.Append("<li><a epub:type=\"cover\" href=\"").Append(cover.Href).AppendLine("\">Cover</a></li>");
        if (xhtmlItems.FirstOrDefault(item => item.Id == "title") is { } title)
            sb.Append("<li><a epub:type=\"titlepage\" href=\"").Append(title.Href).AppendLine("\">Title Page</a></li>");
        var body = xhtmlItems.FirstOrDefault(item =>
                item.Id.StartsWith("chapter-", StringComparison.Ordinal)
                || item.Id.StartsWith("act-", StringComparison.Ordinal)
                || item.Id.StartsWith("section-", StringComparison.Ordinal))
            ?? xhtmlItems.FirstOrDefault(item => item.IncludeInNavigation);
        if (body is not null)
            sb.Append("<li><a epub:type=\"bodymatter\" href=\"").Append(body.Href).AppendLine("\">Start Reading</a></li>");
        sb.AppendLine("</ol></nav>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static IEnumerable<(string Title, string Href)> TocLinks(PublishDocument document)
    {
        var actIndex = 0;
        var chapterIndex = 0;
        foreach (var section in document.Sections)
        {
            actIndex++;
            var actHref = section.IsUnassigned ? "section-unassigned.xhtml" : $"act-{actIndex.ToString(CultureInfo.InvariantCulture)}.xhtml";
            if (section.IncludePage)
                yield return (section.Title, actHref);
            foreach (var chapter in section.Chapters)
            {
                chapterIndex++;
                yield return (chapter.Title, $"chapter-{chapterIndex.ToString(CultureInfo.InvariantCulture)}.xhtml");
            }
        }
    }

    private static IReadOnlyList<PublicationImagePlacementDocument> Placements(PublishDocument document, PublishOutlineTargetKind kind, Guid? targetId) =>
        targetId is null
            ? []
            : document.Placements.Where(placement => placement.TargetKind == kind && placement.TargetId == targetId).ToList();

    private static void AppendTextBlocks(StringBuilder sb, string text, string cssClass)
    {
        foreach (var paragraph in SplitParagraphs(text))
        {
            sb.Append("<p class=\"").Append(cssClass).Append("\">");
            for (var i = 0; i < paragraph.Count; i++)
            {
                if (i > 0) sb.Append("<br />");
                sb.Append(Html(paragraph[i]));
            }
            sb.AppendLine("</p>");
        }
    }

    private static IReadOnlyList<IReadOnlyList<string>> SplitParagraphs(string text)
    {
        var paragraphs = new List<IReadOnlyList<string>>();
        var current = new List<string>();
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                Flush();
                continue;
            }

            current.Add(line.TrimEnd());
        }

        Flush();
        return paragraphs;

        void Flush()
        {
            if (current.Count == 0) return;
            paragraphs.Add(current.ToList());
            current.Clear();
        }
    }

    internal static string RenderStylesheet(PublishDocument document)
    {
        var profile = document.Profile;
        var semanticInlineRules = RenderSemanticInlineRules();
        var fontSize = profile.BodyFontSizePoints.ToString("0.###", CultureInfo.InvariantCulture);
        var lineHeight = profile.BodyLineHeight.ToString("0.###", CultureInfo.InvariantCulture);
        var marginPercent = Math.Clamp(
            profile.PageMarginInches / Math.Max(0.001, profile.PageWidthInches) * 100,
            0,
            20).ToString("0.###", CultureInfo.InvariantCulture);
        var css = $$"""
        body {
          color: #172033;
          font-family: Georgia, "Times New Roman", serif;
          font-size: {{fontSize}}pt;
          line-height: {{lineHeight}};
          margin: {{marginPercent}}%;
        }

        h1 {
          color: #111827;
          font-family: Arial, sans-serif;
          font-size: 1.8em;
          line-height: 1.2;
          margin: 0 0 1em;
        }

        .cover-page,
        .title-page,
        .act-page {
          min-height: 80vh;
          display: flex;
          flex-direction: column;
          align-items: center;
          justify-content: center;
          text-align: center;
        }

        .cover-page img,
        figure img {
          display: block;
          max-width: 100%;
          max-height: 90vh;
          margin: 0 auto;
        }

        .title-page h1 {
          font-size: 2.4em;
        }

        .subtitle,
        .byline,
        .publisher,
        figcaption {
          color: #475467;
          font-family: Arial, sans-serif;
          margin: 0.35em 0;
          text-align: center;
        }

        .synopsis {
          color: #475467;
          font-style: italic;
          margin: 0 0 1.25em;
        }

        {{semanticInlineRules}}

        .chapter-body p,
        .matter-page p {
          margin: 0 0 0.9em;
        }

        body.fixed-layout {
          margin: 0;
          padding: 0;
          width: 100%;
          height: 100%;
          overflow: hidden;
        }

        .fixed-page-surface {
          position: relative;
          width: 100%;
          height: 100%;
          margin: 0;
          padding: 0;
          overflow: hidden;
        }

        .fixed-page-image {
          display: block;
          position: absolute;
          top: 0;
          height: 100%;
          max-width: none;
          max-height: none;
          margin: 0;
        }

        .fixed-page-image-whole {
          left: 0;
          width: 100%;
        }

        .fixed-page-accessible {
          position: absolute;
          width: 1px;
          height: 1px;
          padding: 0;
          margin: -1px;
          overflow: hidden;
          clip: rect(0, 0, 0, 0);
          clip-path: inset(50%);
          white-space: normal;
          border: 0;
        }
        """;
        var sb = new StringBuilder(css);
        sb.Append(RenderNamedStyleRules(document.NamedStyles));
        return sb.ToString();
    }

    internal static string RenderSemanticInlineRules() =>
        ".small-caps { font-variant-caps: small-caps; }";

    internal static string RenderNamedStyleRules(
        IReadOnlyList<PublishManuscriptStyleDocument> styles)
    {
        var sb = new StringBuilder();
        foreach (var style in styles)
        {
            var selector = style.Kind == ManuscriptStyleKind.Character
                ? $"[data-character-style=\"{CssString(style.SemanticRole)}\" i]"
                : $"[data-style-role=\"{CssString(style.SemanticRole)}\" i]";
            var declarations = StyleDeclarations(style.Definition);
            if (declarations.Count == 0)
                continue;
            sb.AppendLine().Append(selector).AppendLine(" {");
            foreach (var declaration in declarations)
                sb.Append("  ").Append(declaration).AppendLine(";");
            sb.AppendLine("}");
        }
        return sb.ToString();
    }

    private static IReadOnlyList<string> StyleDeclarations(ManuscriptStyleProperties definition)
    {
        var declarations = new List<string>();
        var families = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["serif"] = "Georgia, \"Times New Roman\", serif",
            ["sans"] = "Arial, Helvetica, sans-serif",
            ["mono"] = "\"Courier New\", Courier, monospace",
        };
        if (definition.FontFamilyKey is { } fontKey && families.TryGetValue(fontKey, out var family))
            declarations.Add($"font-family: {family}");
        if (definition.FontSizePoints is double fontSize)
            declarations.Add($"font-size: {fontSize.ToString("0.###", CultureInfo.InvariantCulture)}pt");
        if (definition.FontWeight is int fontWeight)
            declarations.Add($"font-weight: {fontWeight}");
        if (definition.Italic is true)
            declarations.Add("font-style: italic");
        if (definition.SmallCaps is true)
            declarations.Add("font-variant-caps: small-caps");
        if (definition.LineHeight is double styleLineHeight)
            declarations.Add($"line-height: {styleLineHeight.ToString("0.###", CultureInfo.InvariantCulture)}");
        if (definition.SpaceBeforePoints is double before)
            declarations.Add($"margin-top: {before.ToString("0.###", CultureInfo.InvariantCulture)}pt");
        if (definition.SpaceAfterPoints is double after)
            declarations.Add($"margin-bottom: {after.ToString("0.###", CultureInfo.InvariantCulture)}pt");
        if (definition.TextAlign is { } align)
            declarations.Add($"text-align: {align.ToLowerInvariant()}");
        if (definition.KeepWithNext is true)
        {
            declarations.Add("break-after: avoid");
            declarations.Add("page-break-after: avoid");
        }
        return declarations;
    }

    private static string CssString(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '"':
                    sb.Append("\\\"");
                    break;
                case '<':
                case '>':
                case '&':
                    sb.Append('\\').Append(((int)character).ToString("x", CultureInfo.InvariantCulture)).Append(' ');
                    break;
                case '\r':
                case '\n':
                case '\f':
                    sb.Append('\\').Append(((int)character).ToString("x", CultureInfo.InvariantCulture)).Append(' ');
                    break;
                default:
                    sb.Append(character);
                    break;
            }
        }
        return sb.ToString();
    }

    private static void WriteEntry(ZipArchive archive, string name, string content, CompressionLevel compressionLevel, Encoding encoding)
    {
        var entry = archive.CreateEntry(name, compressionLevel);
        using var stream = entry.Open();
        stream.Write(encoding.GetBytes(content));
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] content, CompressionLevel compressionLevel)
    {
        var entry = archive.CreateEntry(name, compressionLevel);
        using var stream = entry.Open();
        stream.Write(content);
    }

    private static string? ImageHref(IReadOnlyList<EpubImageItem> images, Guid assetId) =>
        images.FirstOrDefault(image => !image.IsCover && image.Asset.Id == assetId)?.Href;

    private static string? PicturePageImageHref(IReadOnlyList<EpubImageItem> images, Guid chapterId) =>
        images.FirstOrDefault(image => image.Id == PicturePageImageId(chapterId))?.Href;

    private static string PicturePageImageId(Guid chapterId) => $"picture-page-{chapterId:N}";

    private static string? CoverImageHref(IReadOnlyList<EpubImageItem> images) =>
        images.FirstOrDefault(image => image.IsCover)?.Href;

    private static string? CoverImageId(IReadOnlyList<EpubImageItem> images) =>
        images.FirstOrDefault(image => image.IsCover)?.Id;

    private static string ImageExtension(string contentType) =>
        contentType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase)
            ? "jpg"
            : contentType.Equals("image/webp", StringComparison.OrdinalIgnoreCase)
                ? "webp"
                : "png";

    private static string Language(PublishDocument document) =>
        string.IsNullOrWhiteSpace(document.Profile.Language) ? "en" : document.Profile.Language.Trim();

    private static string Html(string value) => WebUtility.HtmlEncode(value);

    private sealed record EpubXhtmlItem(
        string Id,
        string Href,
        string Title,
        string Content,
        bool IncludeInNavigation = true,
        string SpineProperties = "");

    private sealed record EpubViewport(int Width, int Height);
    private sealed record EpubImageItem(string Id, string Href, PublishAssetDocument Asset, bool IsCover);
}

internal static class PublicationMatterFormatting
{
    public static bool IsGeneratedPageKind(PublicationMatterKind kind) =>
        kind is PublicationMatterKind.TitlePage
            or PublicationMatterKind.Copyright
            or PublicationMatterKind.Contents;

    public static void EnsureUserAuthoredKind(PublicationMatterKind kind)
    {
        if (IsGeneratedPageKind(kind))
        {
            throw new InvalidOperationException(
                $"{kind} is generated from edition settings and cannot be added as publication matter.");
        }
    }

    public static string Title(PublishMatterDocument item)
    {
        if (!string.IsNullOrWhiteSpace(item.Title))
            return item.Title.Trim();
        return item.Kind switch
        {
            PublicationMatterKind.TitlePage => "Title Page",
            PublicationMatterKind.Copyright => "Copyright",
            PublicationMatterKind.Dedication => "Dedication",
            PublicationMatterKind.Epigraph => "Epigraph",
            PublicationMatterKind.Contents => "Contents",
            PublicationMatterKind.Acknowledgments => "Acknowledgments",
            PublicationMatterKind.AboutAuthor => "About the Author",
            PublicationMatterKind.AlsoBy => "Also By",
            PublicationMatterKind.References => "References",
            _ => "Additional Matter",
        };
    }
}

internal static class SemanticPublishFormatting
{
    public static string PlainText(
        ManuscriptDocument manuscript,
        Func<Guid, PublishAssetDocument?> asset)
    {
        var blocks = new List<string>();
        foreach (var block in manuscript.Content)
        {
            if (block.Type == ManuscriptBlockType.SceneBreak)
            {
                blocks.Add("***");
                continue;
            }
            var text = ManuscriptCodec.Text(block);
            if (block.Type == ManuscriptBlockType.Figure)
            {
                var image = asset(block.ImageId!.Value);
                var label = image?.FileName ?? block.ImageId.Value.ToString("N");
                blocks.Add(string.IsNullOrWhiteSpace(text)
                    ? $"[Figure: {label}; alt: {block.AltText}]"
                    : $"[Figure: {label}; alt: {block.AltText}; caption: {text}]");
                continue;
            }
            blocks.Add(text);
        }
        return string.Join(Environment.NewLine + Environment.NewLine, blocks);
    }

    public static string Markdown(
        ManuscriptDocument manuscript,
        Func<Guid, PublishAssetDocument?> asset)
    {
        var blocks = new List<string>();
        foreach (var block in manuscript.Content)
            blocks.Add(MarkdownBlock(block, asset));
        return string.Join(Environment.NewLine + Environment.NewLine, blocks);
    }

    internal static string MarkdownBlock(
        ManuscriptBlock block,
        Func<Guid, PublishAssetDocument?> asset)
    {
        if (!ManuscriptStyleService.BuiltInParagraphRoles.Contains(block.StyleRole))
        {
            return HtmlBlock(
                block,
                imageId => asset(imageId) is { } image
                    ? $"data:{image.ContentType};base64,{Convert.ToBase64String(image.Data)}"
                    : null);
        }
        var text = string.Concat(block.Content.Select(MarkdownInline));
        return block.Type switch
        {
            ManuscriptBlockType.Heading =>
                $"{new string('#', block.HeadingLevel ?? 2)} {text}",
            ManuscriptBlockType.SceneBreak => "***",
            ManuscriptBlockType.BlockQuote => string.Join(
                Environment.NewLine,
                text.Split('\n').Select(line => $"> {line}")),
            ManuscriptBlockType.ListItem => $"- {text}",
            ManuscriptBlockType.Figure => MarkdownFigure(
                block,
                text,
                asset(block.ImageId!.Value)),
            _ => text,
        };
    }

    public static string Html(
        ManuscriptDocument manuscript,
        Func<Guid, string?> imageHref)
    {
        var sb = new StringBuilder();
        foreach (var block in manuscript.Content)
            sb.Append(HtmlBlock(block, imageHref));
        return sb.ToString();
    }

    internal static string HtmlBlock(
        ManuscriptBlock block,
        Func<Guid, string?> imageHref)
    {
        var content = string.Concat(block.Content.Select(HtmlInline));
        var role = WebUtility.HtmlEncode(block.StyleRole);
        var anchor = $"block-{block.Id:N}";
        return block.Type switch
        {
            ManuscriptBlockType.Heading =>
                $"<h{block.HeadingLevel ?? 2} id=\"{anchor}\" data-style-role=\"{role}\">{content}</h{block.HeadingLevel ?? 2}>",
            ManuscriptBlockType.SceneBreak =>
                $"<hr id=\"{anchor}\" class=\"scene-break\" data-style-role=\"{role}\" />",
            ManuscriptBlockType.BlockQuote =>
                $"<blockquote id=\"{anchor}\" data-style-role=\"{role}\">{content}</blockquote>",
            ManuscriptBlockType.ListItem =>
                $"<ul><li id=\"{anchor}\" data-style-role=\"{role}\">{content}</li></ul>",
            ManuscriptBlockType.Figure => HtmlFigure(block, content, role, anchor, imageHref),
            _ => $"<p id=\"{anchor}\" data-style-role=\"{role}\">{content}</p>",
        };
    }

    private static string HtmlFigure(
        ManuscriptBlock block,
        string content,
        string role,
        string anchor,
        Func<Guid, string?> imageHref)
    {
        var href = imageHref(block.ImageId!.Value)
            ?? throw new InvalidOperationException(
                $"Figure image {block.ImageId:N} is missing from the publication.");
        var caption = string.IsNullOrWhiteSpace(content)
            ? string.Empty
            : $"<figcaption>{content}</figcaption>";
        return $"<figure id=\"{anchor}\" data-style-role=\"{role}\"><img src=\"{WebUtility.HtmlEncode(href)}\" "
            + $"alt=\"{WebUtility.HtmlEncode(block.AltText)}\" />{caption}</figure>";
    }

    private static string MarkdownFigure(
        ManuscriptBlock block,
        string caption,
        PublishAssetDocument? asset)
    {
        if (asset is null)
            throw new InvalidOperationException($"Figure image {block.ImageId:N} is missing from the publication.");
        var dataUrl = $"data:{asset.ContentType};base64,{Convert.ToBase64String(asset.Data)}";
        var markdown = $"![{EscapeMarkdown(block.AltText ?? string.Empty)}]({dataUrl})";
        return string.IsNullOrWhiteSpace(caption)
            ? markdown
            : $"{markdown}{Environment.NewLine}_{caption}_";
    }

    private static string MarkdownInline(ManuscriptInline inline)
    {
        var hasCode = inline.Marks.Any(mark => mark.Type == ManuscriptMarkType.Code);
        var text = hasCode
            ? $"<code>{EncodeHtmlInlineText(inline.Text)}</code>"
            : EscapeMarkdown(inline.Text);
        foreach (var mark in inline.Marks
            .Where(mark => mark.Type != ManuscriptMarkType.Code)
            .OrderBy(mark => mark.Type))
        {
            text = mark.Type switch
            {
                ManuscriptMarkType.Emphasis => $"*{text}*",
                ManuscriptMarkType.Strong => $"**{text}**",
                ManuscriptMarkType.Underline => $"<u>{text}</u>",
                ManuscriptMarkType.Strikethrough => $"~~{text}~~",
                ManuscriptMarkType.Link =>
                    $"<a href=\"{WebUtility.HtmlEncode(mark.Value!)}\">{text}</a>",
                ManuscriptMarkType.Language => $"<span lang=\"{WebUtility.HtmlEncode(mark.Value!)}\">{text}</span>",
                ManuscriptMarkType.SmallCaps => $"<span class=\"small-caps\">{text}</span>",
                ManuscriptMarkType.Superscript => $"<sup>{text}</sup>",
                ManuscriptMarkType.Subscript => $"<sub>{text}</sub>",
                ManuscriptMarkType.CharacterStyle =>
                    $"<span data-character-style=\"{WebUtility.HtmlEncode(mark.Value!)}\">{text}</span>",
                _ => text,
            };
        }
        return text;
    }

    private static string HtmlInline(ManuscriptInline inline)
    {
        var text = EncodeHtmlInlineText(inline.Text);
        foreach (var mark in inline.Marks)
        {
            text = mark.Type switch
            {
                ManuscriptMarkType.Emphasis => $"<em>{text}</em>",
                ManuscriptMarkType.Strong => $"<strong>{text}</strong>",
                ManuscriptMarkType.Underline => $"<u>{text}</u>",
                ManuscriptMarkType.Strikethrough => $"<s>{text}</s>",
                ManuscriptMarkType.Code => $"<code>{text}</code>",
                ManuscriptMarkType.Link => $"<a href=\"{WebUtility.HtmlEncode(mark.Value)}\">{text}</a>",
                ManuscriptMarkType.Language => $"<span lang=\"{WebUtility.HtmlEncode(mark.Value)}\">{text}</span>",
                ManuscriptMarkType.SmallCaps => $"<span class=\"small-caps\">{text}</span>",
                ManuscriptMarkType.Superscript => $"<sup>{text}</sup>",
                ManuscriptMarkType.Subscript => $"<sub>{text}</sub>",
                ManuscriptMarkType.CharacterStyle =>
                    $"<span data-character-style=\"{WebUtility.HtmlEncode(mark.Value)}\">{text}</span>",
                _ => text,
            };
        }
        return text;
    }

    internal static string EscapeMarkdownLiteral(string value)
    {
        const string punctuation = "\\`*_{}[]<>()#+-.!|~^$%\"'/,:=?@";
        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var sb = new StringBuilder(normalized.Length);
        var lines = normalized.Split('\n');
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            if (lineIndex > 0)
                sb.Append("<br />").AppendLine();
            foreach (var character in lines[lineIndex])
            {
                if (character == '&')
                {
                    sb.Append("&amp;");
                    continue;
                }
                if (character == '<')
                {
                    sb.Append("&lt;");
                    continue;
                }
                if (character == '>')
                {
                    sb.Append("&gt;");
                    continue;
                }
                if (punctuation.Contains(character))
                    sb.Append('\\');
                sb.Append(character);
            }
        }
        return sb.ToString();
    }

    private static string EncodeHtmlInlineText(string value) =>
        WebUtility.HtmlEncode(
            value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'))
            .Replace("\n", "<br />", StringComparison.Ordinal);

    private static string EscapeMarkdown(string value) =>
        EscapeMarkdownLiteral(value);
}
