using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Lorekeeper.Manuscripts;
using Lorekeeper.Composition;
using Lorekeeper.Fonts;
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
            if (section.IncludeHeading)
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
        foreach (var block in chapter.Manuscript.Content)
        {
            if (block.Type == ManuscriptBlockType.DesignedPage
                && block.PageCompositionId is Guid compositionId
                && chapter.PageCompositions.FirstOrDefault(item => item.Id == compositionId) is { } composition)
            {
                foreach (var projected in DesignedPageSemanticProjection.Blocks(composition))
                    AppendText(sb, SemanticPublishFormatting.PlainTextBlock(projected, imageId => FindAsset(document, imageId)));
                continue;
            }
            AppendText(sb, SemanticPublishFormatting.PlainTextBlock(
                block,
                imageId => FindAsset(document, imageId)));
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
            AppendImage(
                sb,
                placement.Asset,
                placement.Caption,
                placement.Decorative ? string.Empty : placement.AltText);
        }
    }

    private static void AppendImage(StringBuilder sb, PublishAssetDocument asset, string caption, string? altOverride = null)
    {
        var alt = altOverride ?? (string.IsNullOrWhiteSpace(asset.AltText) ? caption : asset.AltText);
        var dataUrl = $"data:{asset.ContentType};base64,{Convert.ToBase64String(asset.Data)}";
        sb.AppendLine().Append("![").Append(EscapeInline(alt)).Append("](").Append(dataUrl).AppendLine(")");
        if (!string.IsNullOrWhiteSpace(caption))
            sb.Append("_").Append(EscapeInline(caption)).AppendLine("_");
    }

    private static void AppendVisualMarkdown(StringBuilder sb, PublishDocument document, PublishChapterDocument chapter)
    {
        foreach (var manuscriptBlock in chapter.Manuscript.Content)
        {
            if (manuscriptBlock.Type == ManuscriptBlockType.DesignedPage
                && manuscriptBlock.PageCompositionId is Guid compositionId
                && chapter.PageCompositions.FirstOrDefault(item => item.Id == compositionId) is { } composition)
            {
                foreach (var projected in DesignedPageSemanticProjection.Blocks(composition))
                    sb.AppendLine().AppendLine(SemanticPublishFormatting.MarkdownBlock(projected, imageId => FindAsset(document, imageId)));
                continue;
            }
            sb.AppendLine().AppendLine(
                SemanticPublishFormatting.MarkdownBlock(
                    manuscriptBlock,
                    imageId => FindAsset(document, imageId)));
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

public sealed class EpubPublishFormatter : IPublishExportFormatter
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public PublishExportFormat Format => PublishExportFormat.Epub;
    public string FileExtension => ".epub";
    public string ContentType => "application/epub+zip";

    public byte[] Render(PublishDocument document)
    {
        foreach (var item in document.Matter)
            PublicationMatterFormatting.EnsureUserAuthoredKind(item.Kind);
        ValidateDigitalAccessibility(document);
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
            foreach (var font in document.Fonts)
                WriteEntry(archive, $"OEBPS/{FontHref(font)}", font.Data, CompressionLevel.SmallestSize);
        }

        return stream.ToArray();
    }

    private static void ValidateDigitalAccessibility(PublishDocument document)
    {
        var manuscripts = document.Sections.SelectMany(section => section.Chapters).Select(chapter => chapter.Manuscript)
            .Concat(document.Sections.SelectMany(section => section.Chapters).SelectMany(chapter => chapter.PageCompositions).Select(composition => composition.SemanticManuscript))
            .Concat(document.Matter.Select(item => item.Manuscript));
        if (manuscripts.Any(manuscript => manuscript.Content.Any(block => block.Type == ManuscriptBlockType.Figure
            && !block.Decorative && string.IsNullOrWhiteSpace(block.AltText))))
            throw new InvalidDataException("EPUB export requires alternative text or an explicit decorative decision for every Figure.");
        if (document.Placements.Any(placement => !placement.Decorative && string.IsNullOrWhiteSpace(placement.AltText)))
            throw new InvalidDataException("EPUB export requires alternative text or an explicit decorative decision for every edition illustration.");
        var scenes = document.Sections.SelectMany(section => section.Chapters)
            .SelectMany(chapter => chapter.PageCompositions)
            .SelectMany(composition => composition.Variants)
            .Select(variant => variant.Scene)
            .Concat(document.Cover is null ? [] : [document.Cover.Scene]);
        foreach (var scene in scenes)
        {
            var visibleLayers = scene.Layers.Where(layer => layer.Visible).Select(layer => layer.Id).ToHashSet();
            var semantic = CompositionSceneResolver.Flatten(scene).Where(item => item.Visible
                && visibleLayers.Contains(item.LayerId)
                && item.SemanticRole != CompositionSemanticRole.Artifact
                && !item.Decorative).ToList();
            if (semantic.Any(item => item.Kind == CompositionObjectKind.Image
                && (item.AccessibilityDecisionPending || string.IsNullOrWhiteSpace(item.AltText))))
                throw new InvalidDataException("EPUB export requires alternative text or an explicit decorative decision for every composition image.");
            if (semantic.Any(item => item.ReadingOrder is null)
                || semantic.GroupBy(item => item.ReadingOrder).Any(group => group.Count() > 1))
                throw new InvalidDataException("EPUB export requires a complete, unique logical reading order for every composition.");
        }
        if (document.Cover is null && document.CoverAsset is { } rawCover && string.IsNullOrWhiteSpace(rawCover.AltText))
            throw new InvalidDataException("EPUB cover artwork requires alternative text.");
    }

    private List<EpubXhtmlItem> BuildXhtmlItems(PublishDocument document, IReadOnlyList<EpubImageItem> imageItems)
    {
        var items = new List<EpubXhtmlItem>();
        if (document.Cover is not null || CoverImageHref(imageItems) is not null)
        {
            var viewport = CoverViewport(document.Profile);
            items.Add(new EpubXhtmlItem(
                "cover-page",
                "cover.xhtml",
                "Cover",
                RenderXhtmlPage(document, "Cover", RenderCoverBody(document, imageItems, viewport), viewport, "fixed-layout"),
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
                AddChapterItems(items, document, chapter, imageItems, chapterId);
            }
        }

        AddMatter(items, document, imageItems, PublicationMatterLocation.Back);
        return items;
    }

    private static void AddChapterItems(
        List<EpubXhtmlItem> items,
        PublishDocument document,
        PublishChapterDocument chapter,
        IReadOnlyList<EpubImageItem> imageItems,
        string chapterId)
    {
        var segment = new List<ManuscriptBlock>();
        var part = 0;
        var firstReflow = true;
        void FlushReflow(bool final)
        {
            if (segment.Count == 0 && !firstReflow && !final) return;
            var id = firstReflow ? chapterId : $"{chapterId}-part-{++part}";
            var title = firstReflow ? chapter.Title : $"{chapter.Title}, continued";
            items.Add(new EpubXhtmlItem(
                id,
                $"{id}.xhtml",
                title,
                RenderXhtmlPage(
                    document,
                    title,
                    RenderChapterSegmentBody(
                        document,
                        chapter,
                        imageItems,
                        segment,
                        includeOpening: firstReflow,
                        includeEnding: final)),
                IncludeInNavigation: firstReflow));
            segment.Clear();
            firstReflow = false;
        }

        var designedIndex = 0;
        foreach (var block in chapter.Manuscript.Content)
        {
            if (block.Type != ManuscriptBlockType.DesignedPage
                || block.PageCompositionId is not Guid compositionId)
            {
                segment.Add(block);
                continue;
            }
            FlushReflow(final: false);
            var composition = chapter.PageCompositions.FirstOrDefault(item => item.Id == compositionId)
                ?? throw new InvalidOperationException($"Designed Page '{compositionId:N}' is missing from the chapter publication document.");
            var variant = composition.Variants.FirstOrDefault()
                ?? throw new InvalidOperationException($"Designed Page '{composition.Name}' has no layout for EPUB export.");
            var id = $"{chapterId}-designed-{++designedIndex}";
            var viewport = new EpubViewport(
                Math.Max(1, (int)Math.Round(variant.Scene.Surface.WidthPoints)),
                Math.Max(1, (int)Math.Round(variant.Scene.Surface.HeightPoints)));
            var spread = variant.Scene.Surface.Kind == CompositionSurfaceKind.FacingSpread
                ? "rendition:layout-pre-paginated rendition:spread-none rendition:page-spread-center"
                : "rendition:layout-pre-paginated rendition:spread-none";
            var body = new StringBuilder();
            AppendDesignedPage(body, composition, imageItems);
            items.Add(new EpubXhtmlItem(
                id,
                $"{id}.xhtml",
                composition.Name,
                RenderXhtmlPage(document, composition.Name, body.ToString(), viewport, "fixed-layout"),
                IncludeInNavigation: false,
                SpineProperties: spread));
        }
        FlushReflow(final: true);
    }

    private static List<EpubImageItem> BuildImageItems(PublishDocument document)
    {
        var items = new List<EpubImageItem>();
        var assets = new Dictionary<Guid, PublishAssetDocument>();
        foreach (var placement in document.Placements)
            assets[placement.Asset.Id] = placement.Asset;
        foreach (var chapter in document.Sections.SelectMany(section => section.Chapters))
        {
            foreach (var block in chapter.Manuscript.Content.Where(block =>
                block.Type == ManuscriptBlockType.Figure))
            {
                if (block.ImageId is Guid imageId
                    && document.Assets.FirstOrDefault(asset => asset.Id == imageId) is { } asset)
                {
                    assets[asset.Id] = asset;
                }
            }
            foreach (var imageId in chapter.PageCompositions
                .SelectMany(composition => composition.Variants)
                .SelectMany(variant => variant.Scene.Objects)
                .Where(item => item.Kind == CompositionObjectKind.Image && item.ImageId is not null)
                .Select(item => item.ImageId!.Value))
            {
                if (document.Assets.FirstOrDefault(asset => asset.Id == imageId) is { } asset)
                    assets[asset.Id] = asset;
            }
        }
        if (document.Cover is { } composedCover)
        {
            foreach (var imageId in composedCover.Scene.Objects
                .Where(item => item.Kind == CompositionObjectKind.Image && item.ImageId is not null)
                .Select(item => item.ImageId!.Value))
            {
                if (document.Assets.FirstOrDefault(asset => asset.Id == imageId) is { } asset)
                    assets[asset.Id] = asset;
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
        if (document.Cover is not null)
        {
            var bytes = Utf8NoBom.GetBytes(RenderComposedCoverSvg(document, items));
            var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"epub-cover:{document.EditionId:N}")).AsSpan(0, 16));
            var asset = new PublishAssetDocument(id, "cover.svg", "image/svg+xml", bytes, $"Composed cover for {document.DisplayTitle}");
            items.Insert(0, new EpubImageItem("cover-image", "images/cover.svg", asset, IsCover: true));
        }
        else if (document.CoverAsset is { } rawCover)
        {
            items.Insert(0, new EpubImageItem("cover-image", $"images/cover.{ImageExtension(rawCover.ContentType)}", rawCover, IsCover: true));
        }
        return items;
    }

    private static string RenderComposedCoverSvg(PublishDocument document, IReadOnlyList<EpubImageItem> imageItems)
    {
        var cover = document.Cover ?? throw new InvalidOperationException("A composed cover is required.");
        var scene = cover.Scene;
        var visibleLayers = scene.Layers.Where(layer => layer.Visible).Select(layer => layer.Id).ToHashSet();
        var sb = new StringBuilder();
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" viewBox=\"0 0 ")
            .Append(scene.Surface.WidthPoints.ToString(CultureInfo.InvariantCulture)).Append(' ')
            .Append(scene.Surface.HeightPoints.ToString(CultureInfo.InvariantCulture)).Append("\" role=\"img\" aria-label=\"")
            .Append(Html($"Cover of {document.DisplayTitle}")).Append("\"><rect width=\"100%\" height=\"100%\" fill=\"")
            .Append(Html(cover.BackgroundColor)).Append("\"/>");
        var fontRules = RenderFontFaceRules(document.Fonts).Replace("url('fonts/", "url('../fonts/", StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(fontRules))
            sb.Append("<style>").Append(fontRules).Append("</style>");
        foreach (var source in CompositionSceneResolver.Flatten(scene).Where(item => item.Visible && visibleLayers.Contains(item.LayerId)).OrderBy(item => item.ZIndex).ThenBy(item => item.Id))
        {
            var item = ResolveCompositionStyle(scene, source);
            var x = item.Bounds.XPercent / 100 * scene.Surface.WidthPoints;
            var y = item.Bounds.YPercent / 100 * scene.Surface.HeightPoints;
            var width = item.Bounds.WidthPercent / 100 * scene.Surface.WidthPoints;
            var height = item.Bounds.HeightPercent / 100 * scene.Surface.HeightPoints;
            var transform = $"rotate({item.RotationDegrees.ToString(CultureInfo.InvariantCulture)} {(x + width / 2).ToString(CultureInfo.InvariantCulture)} {(y + height / 2).ToString(CultureInfo.InvariantCulture)})";
            if (item.Kind == CompositionObjectKind.Image && item.ImageId is Guid imageId
                && imageItems.FirstOrDefault(candidate => !candidate.IsCover && candidate.Asset.Id == imageId) is { } image)
            {
                sb.Append("<foreignObject x=\"").Append(x.ToString(CultureInfo.InvariantCulture)).Append("\" y=\"").Append(y.ToString(CultureInfo.InvariantCulture))
                    .Append("\" width=\"").Append(width.ToString(CultureInfo.InvariantCulture)).Append("\" height=\"").Append(height.ToString(CultureInfo.InvariantCulture))
                    .Append("\" opacity=\"").Append(item.Opacity.ToString(CultureInfo.InvariantCulture)).Append("\" transform=\"").Append(transform).Append("\"><div xmlns=\"http://www.w3.org/1999/xhtml\" style=\"width:100%;height:100%;overflow:hidden\"><img alt=\"")
                    .Append(Html(item.Decorative ? string.Empty : item.AltText)).Append("\" src=\"").Append(Html(Path.GetFileName(image.Href)))
                    .Append("\" style=\"display:block;width:100%;height:100%;object-fit:").Append(ImageFitCss(item.ImageFit))
                    .Append(";object-position:").Append(item.CropXPercent.ToString(CultureInfo.InvariantCulture)).Append("% ")
                    .Append(item.CropYPercent.ToString(CultureInfo.InvariantCulture)).Append("%\"/></div></foreignObject>");
            }
            else if (item.Kind == CompositionObjectKind.Text)
            {
                var text = item.TextBinding switch { "title" => cover.Title, "subtitle" => cover.Subtitle, "author" => cover.Author, "spineText" => cover.SpineText, "backCopy" => cover.BackCopy, _ => item.TextBinding };
                sb.Append("<foreignObject x=\"").Append(x.ToString(CultureInfo.InvariantCulture)).Append("\" y=\"").Append(y.ToString(CultureInfo.InvariantCulture))
                    .Append("\" width=\"").Append(width.ToString(CultureInfo.InvariantCulture)).Append("\" height=\"").Append(height.ToString(CultureInfo.InvariantCulture))
                    .Append("\" opacity=\"").Append(item.Opacity.ToString(CultureInfo.InvariantCulture)).Append("\" transform=\"").Append(transform).Append("\"><div xmlns=\"http://www.w3.org/1999/xhtml\" style=\"box-sizing:border-box;display:flex;width:100%;height:100%;overflow:hidden;white-space:pre-wrap;color:")
                    .Append(Html(item.FillColor)).Append(";background:").Append(Html(BackgroundCss(item.BackgroundColor, item.BackgroundOpacity)))
                    .Append(";border:").Append(item.StrokeWidthPoints.ToString(CultureInfo.InvariantCulture)).Append("pt solid ").Append(Html(item.StrokeColor))
                    .Append(";font-family:").Append(Html(FontCssFamily(item.FontFamilyKey))).Append(";font-size:").Append(item.FontSizePoints.ToString(CultureInfo.InvariantCulture)).Append("pt;font-weight:").Append(item.FontWeight)
                    .Append(";font-style:").Append(item.Italic ? "italic" : "normal").Append(";line-height:").Append(item.LineHeight.ToString(CultureInfo.InvariantCulture))
                    .Append(";letter-spacing:").Append(item.LetterSpacingEm.ToString(CultureInfo.InvariantCulture)).Append("em;text-align:")
                    .Append(item.TextAlignment == CompositionTextAlignment.Center ? "center" : item.TextAlignment == CompositionTextAlignment.End ? "right" : "left")
                    .Append(";align-items:").Append(item.VerticalAlignment == CompositionVerticalAlignment.Center ? "center" : item.VerticalAlignment == CompositionVerticalAlignment.Bottom ? "flex-end" : "flex-start")
                    .Append(";text-shadow:").Append(TextShadowCss(item.TextShadow)).Append("\">").Append(Html(text)).Append("</div></foreignObject>");
            }
            else if (item.Kind == CompositionObjectKind.Ellipse)
                sb.Append("<ellipse cx=\"").Append((x + width / 2).ToString(CultureInfo.InvariantCulture)).Append("\" cy=\"").Append((y + height / 2).ToString(CultureInfo.InvariantCulture)).Append("\" rx=\"").Append((width / 2).ToString(CultureInfo.InvariantCulture)).Append("\" ry=\"").Append((height / 2).ToString(CultureInfo.InvariantCulture)).Append("\" fill=\"").Append(Html(item.FillColor)).Append("\" stroke=\"").Append(Html(item.StrokeColor)).Append("\" stroke-width=\"").Append(item.StrokeWidthPoints.ToString(CultureInfo.InvariantCulture)).Append("\" opacity=\"").Append(item.Opacity.ToString(CultureInfo.InvariantCulture)).Append("\" transform=\"").Append(transform).Append("\"/>");
            else if (item.Kind == CompositionObjectKind.Line)
                sb.Append("<line x1=\"").Append(x.ToString(CultureInfo.InvariantCulture)).Append("\" y1=\"").Append((y + height / 2).ToString(CultureInfo.InvariantCulture)).Append("\" x2=\"").Append((x + width).ToString(CultureInfo.InvariantCulture)).Append("\" y2=\"").Append((y + height / 2).ToString(CultureInfo.InvariantCulture)).Append("\" stroke=\"").Append(Html(item.StrokeColor)).Append("\" stroke-width=\"").Append(item.StrokeWidthPoints.ToString(CultureInfo.InvariantCulture)).Append("\" transform=\"").Append(transform).Append("\"/>");
            else if (item.Kind == CompositionObjectKind.Rectangle)
                sb.Append("<rect x=\"").Append(x.ToString(CultureInfo.InvariantCulture)).Append("\" y=\"").Append(y.ToString(CultureInfo.InvariantCulture)).Append("\" width=\"").Append(width.ToString(CultureInfo.InvariantCulture)).Append("\" height=\"").Append(height.ToString(CultureInfo.InvariantCulture)).Append("\" fill=\"").Append(Html(item.FillColor)).Append("\" stroke=\"").Append(Html(item.StrokeColor)).Append("\" stroke-width=\"").Append(item.StrokeWidthPoints.ToString(CultureInfo.InvariantCulture)).Append("\" opacity=\"").Append(item.Opacity.ToString(CultureInfo.InvariantCulture)).Append("\" transform=\"").Append(transform).Append("\"/>");
        }
        return sb.Append("</svg>").ToString();
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

    private static string RenderCoverBody(
        PublishDocument document,
        IReadOnlyList<EpubImageItem> imageItems,
        EpubViewport viewport)
    {
        var sb = new StringBuilder();
        sb.Append("<section class=\"fixed-page-surface composed-cover\" aria-label=\"Cover\" style=\"background:")
            .Append(Html(document.Cover?.BackgroundColor ?? "#ffffff")).AppendLine("\">");
        if (document.Cover is { } cover)
        {
            var visibleLayers = cover.Scene.Layers.Where(layer => layer.Visible).Select(layer => layer.Id).ToHashSet();
            foreach (var sceneItem in CompositionSceneResolver.Flatten(cover.Scene).Where(item => item.Visible && visibleLayers.Contains(item.LayerId))
                .OrderBy(item => item.ReadingOrder is null ? 1 : 0)
                .ThenBy(item => item.ReadingOrder)
                .ThenBy(item => item.ZIndex))
            {
                var item = ResolveCompositionStyle(cover.Scene, sceneItem);
                var style = FormattableString.Invariant(
                    $"left:{item.Bounds.XPercent}%;top:{item.Bounds.YPercent}%;width:{item.Bounds.WidthPercent}%;height:{item.Bounds.HeightPercent}%;opacity:{item.Opacity};transform:rotate({item.RotationDegrees}deg);z-index:{item.ZIndex};color:{item.FillColor};background:{BackgroundCss(item.BackgroundColor, item.BackgroundOpacity)};border:{item.StrokeWidthPoints}px solid {item.StrokeColor};font-family:{FontCssFamily(item.FontFamilyKey)};font-weight:{item.FontWeight};font-style:{(item.Italic ? "italic" : "normal")};font-size:{item.FontSizePoints}pt;line-height:{item.LineHeight};letter-spacing:{item.LetterSpacingEm}em;text-align:{(item.TextAlignment == CompositionTextAlignment.Center ? "center" : item.TextAlignment == CompositionTextAlignment.End ? "right" : "left")};align-items:{(item.VerticalAlignment == CompositionVerticalAlignment.Center ? "center" : item.VerticalAlignment == CompositionVerticalAlignment.Bottom ? "flex-end" : "flex-start")};text-shadow:{TextShadowCss(item.TextShadow)};object-fit:{ImageFitCss(item.ImageFit)};object-position:{item.CropXPercent}% {item.CropYPercent}%");
                if (item.Kind == CompositionObjectKind.Text)
                {
                    var text = item.TextBinding switch
                    {
                        "title" => cover.Title,
                        "subtitle" => cover.Subtitle,
                        "author" => cover.Author,
                        "spineText" => cover.SpineText,
                        "backCopy" => cover.BackCopy,
                        _ => item.TextBinding,
                    };
                    if (!string.IsNullOrWhiteSpace(text))
                        sb.Append("<div class=\"cover-scene-object cover-scene-text\" style=\"").Append(Html(style)).Append("\">").Append(Html(text)).AppendLine("</div>");
                }
                else if (item.Kind == CompositionObjectKind.Image && item.ImageId is Guid imageId
                    && ImageHref(imageItems, imageId) is string href)
                {
                    var alt = item.Decorative ? string.Empty : item.AltText;
                    sb.Append("<img class=\"cover-scene-object cover-scene-image\" style=\"").Append(Html(style))
                        .Append("\" src=\"").Append(Html(href)).Append("\" alt=\"").Append(Html(alt)).Append('"');
                    if (item.Decorative) sb.Append(" aria-hidden=\"true\"");
                    sb.AppendLine(" />");
                }
                else if (item.Kind is CompositionObjectKind.Rectangle or CompositionObjectKind.Ellipse or CompositionObjectKind.Line)
                {
                    var shapeStyle = item.Kind == CompositionObjectKind.Line
                        ? style + $";height:0;border:0;border-top:{item.StrokeWidthPoints.ToString(CultureInfo.InvariantCulture)}pt solid {item.StrokeColor};background:transparent"
                        : style + (item.Kind == CompositionObjectKind.Ellipse ? ";border-radius:50%" : string.Empty);
                    sb.Append("<div class=\"cover-scene-object cover-scene-shape cover-scene-")
                        .Append(item.Kind.ToString().ToLowerInvariant()).Append("\" aria-hidden=\"true\" style=\"")
                        .Append(Html(shapeStyle)).AppendLine("\"></div>");
                }
            }
        }
        else if (document.CoverAsset is { } rawCover && CoverImageHref(imageItems) is string coverHref)
        {
            var alt = string.IsNullOrWhiteSpace(rawCover.AltText) ? "Cover" : rawCover.AltText;
            sb.Append("<img class=\"fixed-page-image fixed-page-image-whole\" alt=\"").Append(Html(alt))
                .Append("\" src=\"").Append(Html(coverHref)).Append("\" width=\"")
                .Append(viewport.Width).Append("\" height=\"").Append(viewport.Height).AppendLine("\" />");
        }
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

    private static string RenderChapterSegmentBody(
        PublishDocument document,
        PublishChapterDocument chapter,
        IReadOnlyList<EpubImageItem> imageItems,
        IReadOnlyList<ManuscriptBlock> blocks,
        bool includeOpening,
        bool includeEnding)
    {
        var sb = new StringBuilder();
        if (includeOpening)
            AppendFigures(sb, document, imageItems, PublishOutlineTargetKind.Chapter, chapter.Id, PublicationImagePlacementKind.BeforeChapter);
        sb.AppendLine("<article class=\"chapter-page\">");
        if (includeOpening && chapter.IncludeHeading)
            sb.Append("<h1>").Append(Html(chapter.Title)).AppendLine("</h1>");
        if (includeOpening && document.Profile.IncludeChapterSynopses)
            AppendTextBlocks(sb, chapter.Synopsis, "synopsis");
        if (includeOpening)
            AppendFigures(sb, document, imageItems, PublishOutlineTargetKind.Chapter, chapter.Id, PublicationImagePlacementKind.ChapterOpening);
        sb.AppendLine("<div class=\"chapter-body\">");
        foreach (var block in blocks)
            sb.Append(SemanticPublishFormatting.HtmlBlock(block, imageId => ImageHref(imageItems, imageId)));
        sb.AppendLine("</div>");
        if (includeEnding)
            AppendFigures(sb, document, imageItems, PublishOutlineTargetKind.Chapter, chapter.Id, PublicationImagePlacementKind.ChapterEnding);
        sb.AppendLine("</article>");
        if (includeEnding)
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
            var presentation = placement.Presentation ?? new FigurePresentation { Placement = FigurePlacementIntent.DedicatedPage };
            var outputWidth = presentation.Placement is FigurePlacementIntent.FullWidth or FigurePlacementIntent.FullBleed
                ? 100 : Math.Clamp(presentation.WidthPercent, 1, 100);
            var figureCss = $"position:relative;width:{outputWidth.ToString(CultureInfo.InvariantCulture)}%;margin-top:{presentation.SpacingBeforePoints.ToString(CultureInfo.InvariantCulture)}pt;margin-bottom:{presentation.SpacingAfterPoints.ToString(CultureInfo.InvariantCulture)}pt;break-inside:{(presentation.KeepWithCaption ? "avoid" : "auto")};";
            if (presentation.StartOnNewPage || presentation.Placement is FigurePlacementIntent.DedicatedPage or FigurePlacementIntent.FullBleed)
                figureCss += "break-before:page;page-break-before:always;";
            if (presentation.Placement == FigurePlacementIntent.Float || presentation.TextWrap != FigureTextWrap.None)
                figureCss += presentation.TextWrap == FigureTextWrap.Start || presentation.Alignment == FigureAlignment.End ? "float:right;clear:right;" : "float:left;clear:left;";
            else
                figureCss += presentation.Alignment switch { FigureAlignment.Start => "margin-left:0;margin-right:auto;", FigureAlignment.End => "margin-left:auto;margin-right:0;", _ => "margin-left:auto;margin-right:auto;" };
            if (presentation.Placement == FigurePlacementIntent.FullBleed)
                figureCss += "width:100vw;max-width:none;margin-left:calc(50% - 50vw);";
            var objectFit = ImageFitCss(presentation.Fit);
            var frameHeight = presentation.Placement is FigurePlacementIntent.DedicatedPage or FigurePlacementIntent.FullBleed ? "75vh" : "40vh";
            var imageCss = $"display:block;width:100%;height:100%;object-fit:{objectFit};object-position:{presentation.CropXPercent.ToString(CultureInfo.InvariantCulture)}% {presentation.CropYPercent.ToString(CultureInfo.InvariantCulture)}%;";
            var alt = placement.Decorative ? string.Empty : placement.AltText;
            var caption = string.IsNullOrWhiteSpace(placement.Caption) || presentation.CaptionPlacement == FigureCaptionPlacement.Hidden
                ? string.Empty
                : $"<figcaption>{Html(placement.Caption)}</figcaption>";
            sb.Append("<figure class=\"edition-illustration caption-").Append(presentation.CaptionPlacement.ToString().ToLowerInvariant())
                .Append("\" style=\"").Append(figureCss).Append("\" lang=\"").Append(Html(placement.Language)).Append("\" xml:lang=\"")
                .Append(Html(placement.Language)).Append("\" data-accessibility-role=\"")
                .Append(Html(placement.AccessibilityRole.ToString().ToLowerInvariant())).Append("\">");
            if (presentation.CaptionPlacement == FigureCaptionPlacement.Above)
                sb.Append(caption);
            sb.Append("<div class=\"figure-media\" style=\"position:relative;width:100%;height:").Append(frameHeight).Append(";overflow:hidden\"><img alt=\"").Append(Html(alt)).Append("\" src=\"").Append(href).Append("\" style=\"").Append(imageCss).Append('"');
            if (placement.Decorative)
                sb.Append(" role=\"presentation\" aria-hidden=\"true\"");
            sb.AppendLine(" />");
            if (presentation.CaptionPlacement == FigureCaptionPlacement.Overlay)
                sb.Append("<div class=\"figure-overlay-caption\" style=\"position:absolute;left:0;right:0;bottom:0;background:rgba(0,0,0,.65);color:white;padding:.5em\">").Append(caption.Replace("<figcaption>", string.Empty, StringComparison.Ordinal).Replace("</figcaption>", string.Empty, StringComparison.Ordinal)).Append("</div>");
            sb.Append("</div>");
            if (presentation.CaptionPlacement is not FigureCaptionPlacement.Above and not FigureCaptionPlacement.Overlay)
                sb.Append(caption);
            sb.AppendLine("</figure>");
        }
    }

    private static void AppendDesignedPage(
        StringBuilder sb,
        PublishPageCompositionDocument composition,
        IReadOnlyList<EpubImageItem> imageItems)
    {
        var variant = composition.Variants.FirstOrDefault();
        if (variant is null)
            throw new InvalidOperationException($"Designed Page '{composition.Name}' has no layout for EPUB export.");
        var scene = CompositionService.WithDerivedTextSemanticRoles(
            variant.Scene,
            composition.SemanticManuscript);
        var visibleLayers = scene.Layers.Where(layer => layer.Visible).Select(layer => layer.Id).ToHashSet();
        var unplaced = ManuscriptRangeResolver.ValidateCoverage(
            composition.SemanticManuscript,
            CompositionSceneResolver.Flatten(scene).Where(item => item.Kind == CompositionObjectKind.Text
                    && item.Visible && visibleLayers.Contains(item.LayerId))
                .Select(item => item.ContentReferences));
        if (unplaced.Count > 0)
            throw new InvalidOperationException($"Designed Page '{composition.Name}' has {unplaced.Count} unplaced semantic block(s).");
        sb.Append("<section class=\"fixed-page-surface designed-page\" aria-label=\"").Append(Html(composition.Name)).Append("\" style=\"aspect-ratio:")
            .Append(scene.Surface.WidthPoints.ToString(CultureInfo.InvariantCulture)).Append('/')
            .Append(scene.Surface.HeightPoints.ToString(CultureInfo.InvariantCulture)).AppendLine("\">");
        foreach (var sceneItem in CompositionSceneResolver.Flatten(scene)
            .Where(item => item.Visible && visibleLayers.Contains(item.LayerId))
            .OrderBy(item => item.Decorative || item.SemanticRole == CompositionSemanticRole.Artifact ? int.MaxValue : item.ReadingOrder ?? int.MaxValue - 1)
            .ThenBy(item => item.ZIndex))
        {
            var item = ResolveCompositionStyle(scene, sceneItem);
            var style = $"left:{item.Bounds.XPercent.ToString(CultureInfo.InvariantCulture)}%;top:{item.Bounds.YPercent.ToString(CultureInfo.InvariantCulture)}%;width:{item.Bounds.WidthPercent.ToString(CultureInfo.InvariantCulture)}%;height:{item.Bounds.HeightPercent.ToString(CultureInfo.InvariantCulture)}%;transform:rotate({item.RotationDegrees.ToString(CultureInfo.InvariantCulture)}deg);opacity:{item.Opacity.ToString(CultureInfo.InvariantCulture)};z-index:{item.ZIndex.ToString(CultureInfo.InvariantCulture)}";
            if (item.Kind == CompositionObjectKind.Image && item.ImageId is Guid imageId)
            {
                var image = imageItems.FirstOrDefault(candidate => !candidate.IsCover && candidate.Asset.Id == imageId);
                if (image is null) continue;
                var alt = item.Decorative ? string.Empty : item.AltText;
                sb.Append("<img class=\"composition-object\" src=\"").Append(Html(image.Href))
                    .Append("\" alt=\"").Append(Html(alt)).Append('"');
                if (item.Decorative)
                    sb.Append(" role=\"presentation\" aria-hidden=\"true\"");
                sb.Append(" style=\"").Append(style)
                    .Append(";object-fit:").Append(ImageFitCss(item.ImageFit)).Append(";object-position:")
                    .Append(item.CropXPercent.ToString(CultureInfo.InvariantCulture)).Append("% ")
                    .Append(item.CropYPercent.ToString(CultureInfo.InvariantCulture)).AppendLine("%\" />");
            }
            else if (item.Kind == CompositionObjectKind.Text)
            {
                var text = string.IsNullOrWhiteSpace(item.TextBinding)
                    ? HtmlBoundCompositionRanges(composition.SemanticManuscript, item.ContentReferences)
                    : Html(item.TextBinding);
                var (tag, epubType) = CompositionTextSemantics(item.SemanticRole);
                sb.Append('<').Append(tag).Append(" class=\"composition-object composition-text\"");
                if (epubType is not null) sb.Append(" epub:type=\"").Append(epubType).Append('"');
                if (!string.IsNullOrWhiteSpace(item.Language))
                    sb.Append(" lang=\"").Append(Html(item.Language)).Append("\" xml:lang=\"").Append(Html(item.Language)).Append('"');
                sb.Append(" style=\"").Append(style)
                    .Append(";font-family:").Append(Html(FontCssFamily(item.FontFamilyKey))).Append(";font-weight:")
                    .Append(item.FontWeight).Append(";font-style:").Append(item.Italic ? "italic" : "normal")
                    .Append(";font-size:").Append(item.FontSizePoints.ToString(CultureInfo.InvariantCulture)).Append("pt;line-height:")
                    .Append(item.LineHeight.ToString(CultureInfo.InvariantCulture)).Append(";letter-spacing:")
                    .Append(item.LetterSpacingEm.ToString(CultureInfo.InvariantCulture)).Append("em;color:")
                    .Append(Html(item.FillColor)).Append(";background:").Append(Html(BackgroundCss(item.BackgroundColor, item.BackgroundOpacity)))
                    .Append(";text-align:").Append(item.TextAlignment switch { CompositionTextAlignment.Center => "center", CompositionTextAlignment.End => "right", _ => "left" })
                    .Append(";align-items:").Append(item.VerticalAlignment switch { CompositionVerticalAlignment.Center => "center", CompositionVerticalAlignment.Bottom => "flex-end", _ => "flex-start" })
                    .Append(";text-shadow:").Append(TextShadowCss(item.TextShadow))
                    .Append(";-webkit-text-stroke:").Append(item.StrokeWidthPoints.ToString(CultureInfo.InvariantCulture)).Append("pt ")
                    .Append(Html(item.StrokeColor)).Append("\">").Append(text).Append("</").Append(tag).AppendLine(">");
            }
            else
            {
                var shapeStyle = item.Kind == CompositionObjectKind.Line
                    ? style + $";height:0;border-top:{item.StrokeWidthPoints.ToString(CultureInfo.InvariantCulture)}pt solid {item.StrokeColor};background:transparent"
                    : style + $";background:{item.FillColor};border:{item.StrokeWidthPoints.ToString(CultureInfo.InvariantCulture)}pt solid {item.StrokeColor}"
                        + (item.Kind == CompositionObjectKind.Ellipse ? ";border-radius:50%" : string.Empty);
                sb.Append("<div class=\"composition-object composition-shape composition-")
                    .Append(item.Kind.ToString().ToLowerInvariant())
                    .Append("\" aria-hidden=\"true\" style=\"").Append(Html(shapeStyle)).AppendLine("\"></div>");
            }
        }
        sb.AppendLine("</section>");
    }

    private static CompositionObject ResolveCompositionStyle(CompositionScene scene, CompositionObject item)
    {
        var style = item.StyleId is Guid styleId
            ? scene.Styles.FirstOrDefault(candidate => candidate.Id == styleId)
            : null;
        return style is null ? item : item with
        {
            FontFamilyKey = style.FontFamilyKey,
            FontWeight = style.FontWeight,
            Italic = style.Italic,
            FontSizePoints = style.FontSizePoints,
            LineHeight = style.LineHeight,
            LetterSpacingEm = style.LetterSpacingEm,
            FillColor = style.FillColor,
            BackgroundColor = style.BackgroundColor,
            BackgroundOpacity = style.BackgroundOpacity,
            StrokeColor = style.StrokeColor,
            StrokeWidthPoints = style.StrokeWidthPoints,
            TextAlignment = style.TextAlignment,
            VerticalAlignment = style.VerticalAlignment,
            TextShadow = style.TextShadow,
        };
    }

    private static string HtmlBoundCompositionRanges(
        ManuscriptDocument semantic,
        IReadOnlyList<ManuscriptRangeReference> references)
    {
        var blocks = ManuscriptRangeResolver.ResolveBlocks(semantic, references);
        var sb = new StringBuilder();
        string? previousBlockId = null;
        foreach (var block in blocks)
        {
            if (previousBlockId is not null && !string.Equals(previousBlockId, block.Id, StringComparison.Ordinal))
                sb.Append("<br />");
            sb.Append(SemanticPublishFormatting.HtmlInlineContent(block));
            previousBlockId = block.Id;
        }
        return sb.ToString();
    }

    private static (string Tag, string? EpubType) CompositionTextSemantics(CompositionSemanticRole role) => role switch
    {
        CompositionSemanticRole.Heading1 => ("h1", null),
        CompositionSemanticRole.Heading2 => ("h2", null),
        CompositionSemanticRole.Heading3 => ("h3", null),
        CompositionSemanticRole.Caption => ("p", "caption"),
        CompositionSemanticRole.Credit => ("p", "credit"),
        _ => ("p", null),
    };

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

    private static EpubViewport CoverViewport(PublishDocumentProfile profile)
    {
        const int physicalPageLongEdgePixels = 2400;
        var scale = physicalPageLongEdgePixels / Math.Max(profile.PageWidthInches, profile.PageHeightInches);
        return new EpubViewport(
            Math.Max(1, (int)Math.Round(profile.PageWidthInches * scale)),
            Math.Max(1, (int)Math.Round(profile.PageHeightInches * scale)));
    }

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
        sb.AppendLine("""<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="book-id" prefix="schema: http://schema.org/">""");
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
        sb.AppendLine("    <meta property=\"schema:accessMode\">textual</meta>");
        if (imageItems.Count > 0)
            sb.AppendLine("    <meta property=\"schema:accessMode\">visual</meta>");
        sb.Append("    <meta property=\"schema:accessModeSufficient\">")
            .Append(imageItems.Count > 0 ? "textual,visual" : "textual")
            .AppendLine("</meta>");
        sb.AppendLine("    <meta property=\"schema:accessibilityFeature\">alternativeText</meta>");
        sb.AppendLine("    <meta property=\"schema:accessibilityFeature\">readingOrder</meta>");
        sb.AppendLine("    <meta property=\"schema:accessibilityFeature\">structuralNavigation</meta>");
        sb.AppendLine("    <meta property=\"schema:accessibilityFeature\">tableOfContents</meta>");
        sb.AppendLine("    <meta property=\"schema:accessibilityHazard\">none</meta>");
        sb.AppendLine("    <meta property=\"schema:accessibilitySummary\">Semantic headings, logical reading order, structural navigation, and text alternatives are included. Perform a human accessibility review before distribution.</meta>");
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
        foreach (var font in document.Fonts)
        {
            sb.Append("    <item id=\"font-").Append(font.FaceId.ToString("N", CultureInfo.InvariantCulture))
                .Append("\" href=\"").Append(FontHref(font)).Append("\" media-type=\"")
                .Append(Html(font.ContentType)).AppendLine("\" />");
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

        .composed-cover {
          position: relative;
          overflow: hidden;
        }

        .cover-scene-object {
          position: absolute;
          box-sizing: border-box;
          transform-origin: center;
        }

        .cover-scene-image {
          max-width: none;
          max-height: none;
          margin: 0;
        }

        .cover-scene-text {
          overflow: hidden;
          white-space: pre-wrap;
        }

        .cover-scene-ellipse {
          border-radius: 50%;
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

        .composition-object,
        .cover-scene-object {
          position: absolute;
          box-sizing: border-box;
          max-width: none;
          max-height: none;
          margin: 0;
        }

        .composition-text,
        .cover-scene-text {
          display: flex;
          overflow: hidden;
          white-space: pre-wrap;
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
        sb.Append(RenderFontFaceRules(document.Fonts));
        sb.Append(RenderNamedStyleRules(document.NamedStyles));
        return sb.ToString();
    }

    private static string RenderFontFaceRules(IReadOnlyList<PublishFontDocument> fonts)
    {
        var sb = new StringBuilder();
        foreach (var font in fonts.OrderBy(item => item.FamilyId).ThenBy(item => item.Weight).ThenBy(item => item.Italic).ThenBy(item => item.FaceId))
        {
            sb.Append("\n@font-face { font-family: '").Append(FontFamilyName(font.FamilyKey)).Append("'; src: url('")
                .Append(FontHref(font)).Append("'); font-weight: ").Append(font.Weight)
                .Append("; font-style: ").Append(font.Italic ? "italic" : "normal").AppendLine("; }");
        }
        return sb.ToString();
    }

    private static string FontHref(PublishFontDocument font) =>
        $"fonts/{font.FaceId:N}{Path.GetExtension(font.FileName).ToLowerInvariant()}";

    private static string FontFamilyName(string familyKey) =>
        $"lk-{new string(familyKey.Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-').ToArray())}";

    internal static string FontCssFamily(string key)
    {
        if (key.StartsWith("project:", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(key["project:".Length..], out _))
            return $"'{FontFamilyName(key)}'";
        if (PublicationBuiltInFonts.Find(key) is not null)
            return $"'{FontFamilyName(key)}'";
        throw new InvalidDataException($"Unsupported publication font family '{key}'.");
    }

    private static string BackgroundCss(string color, double opacity)
    {
        if (opacity <= 0 || string.Equals(color, "transparent", StringComparison.OrdinalIgnoreCase)) return "transparent";
        if (color.Length == 7 && color[0] == '#'
            && byte.TryParse(color.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var red)
            && byte.TryParse(color.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var green)
            && byte.TryParse(color.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var blue))
            return FormattableString.Invariant($"rgba({red},{green},{blue},{Math.Clamp(opacity, 0, 1):0.###})");
        return color;
    }

    private static string TextShadowCss(CompositionTextShadow shadow) => shadow switch
    {
        CompositionTextShadow.Soft => "0 1px 3px rgba(0,0,0,.45)",
        CompositionTextShadow.Strong => "0 2px 4px rgba(0,0,0,.75)",
        CompositionTextShadow.Glow => "0 0 5px rgba(255,255,255,.85)",
        _ => "none",
    };

    private static string ImageFitCss(FigureImageFit fit) => fit switch
    {
        FigureImageFit.Cover => "cover",
        FigureImageFit.Stretch => "fill",
        _ => "contain",
    };

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
        if (definition.FontFamilyKey is { } fontKey)
        {
            if (families.TryGetValue(fontKey, out var family))
                declarations.Add($"font-family: {family}");
            else
                declarations.Add($"font-family: {FontCssFamily(fontKey)}");
        }
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
        if (definition.LeftIndentEm is double leftIndent)
            declarations.Add($"margin-left: {leftIndent.ToString("0.###", CultureInfo.InvariantCulture)}em");
        if (definition.RightIndentEm is double rightIndent)
            declarations.Add($"margin-right: {rightIndent.ToString("0.###", CultureInfo.InvariantCulture)}em");
        if (definition.FirstLineIndentEm is double firstLineIndent)
            declarations.Add($"text-indent: {firstLineIndent.ToString("0.###", CultureInfo.InvariantCulture)}em");
        if (definition.TextAlign is { } align)
            declarations.Add($"text-align: {align.ToLowerInvariant()}");
        if (definition.KeepWithNext is true)
        {
            declarations.Add("break-after: avoid");
            declarations.Add("page-break-after: avoid");
        }
        if (definition.StartOnNewPage is true)
        {
            declarations.Add("break-before: page");
            declarations.Add("page-break-before: always");
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

    private static string? CoverImageHref(IReadOnlyList<EpubImageItem> images) =>
        images.FirstOrDefault(image => image.IsCover)?.Href;

    private static string? CoverImageId(IReadOnlyList<EpubImageItem> images) =>
        images.FirstOrDefault(image => image.IsCover)?.Id;

    private static string ImageExtension(string contentType) =>
        contentType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase)
            ? "jpg"
            : contentType.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase)
                ? "svg"
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
                $"{kind} is generated from the effective release settings and cannot be added as publication matter.");
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

internal static class DesignedPageSemanticProjection
{
    public static IReadOnlyList<ManuscriptBlock> Blocks(PublishPageCompositionDocument composition)
    {
        var variant = composition.Variants.FirstOrDefault()
            ?? throw new InvalidOperationException($"Designed Page '{composition.Name}' has no active layout variant.");
        var visibleLayers = variant.Scene.Layers.Where(layer => layer.Visible).Select(layer => layer.Id).ToHashSet();
        var textObjects = CompositionSceneResolver.Flatten(variant.Scene)
            .Where(item => item.Kind == CompositionObjectKind.Text && item.Visible
                && visibleLayers.Contains(item.LayerId)
                && !item.Decorative && item.SemanticRole != CompositionSemanticRole.Artifact)
            .OrderBy(item => item.ReadingOrder)
            .ThenBy(item => item.Id)
            .ToList();
        var unplaced = ManuscriptRangeResolver.ValidateCoverage(
            composition.SemanticManuscript,
            textObjects.Select(item => item.ContentReferences));
        if (unplaced.Count > 0)
            throw new InvalidOperationException($"Designed Page '{composition.Name}' has {unplaced.Count} unplaced semantic content block(s).");
        return textObjects.SelectMany(item => ManuscriptRangeResolver.ResolveBlocks(
            composition.SemanticManuscript,
            item.ContentReferences)).ToList();
    }
}

internal static class SemanticPublishFormatting
{
    private static string ImageFitCss(FigureImageFit fit) => fit switch
    {
        FigureImageFit.Cover => "cover",
        FigureImageFit.Stretch => "fill",
        _ => "contain",
    };

    public static string PlainText(
        ManuscriptDocument manuscript,
        Func<Guid, PublishAssetDocument?> asset)
    {
        var blocks = new List<string>();
        foreach (var block in manuscript.Content)
            blocks.Add(PlainTextBlock(block, asset));
        return string.Join(Environment.NewLine + Environment.NewLine, blocks);
    }

    internal static string PlainTextBlock(
        ManuscriptBlock block,
        Func<Guid, PublishAssetDocument?> asset)
    {
        if (block.Type == ManuscriptBlockType.SceneBreak)
            return "***";
        var text = ManuscriptCodec.Text(block);
        if (block.Type != ManuscriptBlockType.Figure)
            return text;
        var image = asset(block.ImageId!.Value);
        var label = image?.FileName ?? block.ImageId.Value.ToString("N");
        var accessibility = block.Decorative ? "decorative" : $"alt: {block.AltText}";
        return string.IsNullOrWhiteSpace(text)
            ? $"[Figure: {label}; {accessibility}]"
            : $"[Figure: {label}; {accessibility}; caption: {text}]";
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
        var content = HtmlInlineContent(block);
        var role = WebUtility.HtmlEncode(block.StyleRole);
        var anchor = $"block-{block.Id:N}";
        var language = string.IsNullOrWhiteSpace(block.Language)
            ? string.Empty
            : $" lang=\"{WebUtility.HtmlEncode(block.Language)}\" xml:lang=\"{WebUtility.HtmlEncode(block.Language)}\"";
        var presentation = ParagraphPresentationAttribute(block.ParagraphPresentation);
        return block.Type switch
        {
            ManuscriptBlockType.Heading =>
                $"<h{block.HeadingLevel ?? 2} id=\"{anchor}\" data-style-role=\"{role}\"{language}{presentation}>{content}</h{block.HeadingLevel ?? 2}>",
            ManuscriptBlockType.SceneBreak =>
                $"<hr id=\"{anchor}\" class=\"scene-break\" data-style-role=\"{role}\" />",
            ManuscriptBlockType.BlockQuote =>
                $"<blockquote id=\"{anchor}\" data-style-role=\"{role}\"{language}{presentation}>{content}</blockquote>",
            ManuscriptBlockType.ListItem =>
                $"<ul><li id=\"{anchor}\" data-style-role=\"{role}\"{language}{presentation}>{content}</li></ul>",
            ManuscriptBlockType.Figure => HtmlFigure(block, content, role, anchor, imageHref),
            _ => $"<p id=\"{anchor}\" data-style-role=\"{role}\"{language}{presentation}>{content}</p>",
        };
    }

    private static string ParagraphPresentationAttribute(ParagraphPresentation? presentation)
    {
        if (presentation is null) return string.Empty;
        var declarations = new List<string>();
        if (presentation.FontFamilyKey is { } fontFamily)
        {
            var genericFamilies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["serif"] = "Georgia, &quot;Times New Roman&quot;, serif",
                ["sans"] = "Arial, Helvetica, sans-serif",
                ["mono"] = "&quot;Courier New&quot;, Courier, monospace",
            };
            declarations.Add($"font-family:{(genericFamilies.TryGetValue(fontFamily, out var generic) ? generic : EpubPublishFormatter.FontCssFamily(fontFamily))}");
        }
        if (presentation.FontSizePoints is { } fontSize) declarations.Add($"font-size:{fontSize:R}pt");
        if (presentation.FontWeight is { } fontWeight) declarations.Add($"font-weight:{fontWeight}");
        if (presentation.Italic == true) declarations.Add("font-style:italic");
        if (presentation.SmallCaps == true) declarations.Add("font-variant-caps:small-caps");
        if (presentation.LineHeight is { } lineHeight) declarations.Add($"line-height:{lineHeight:R}");
        if (presentation.Alignment is { } alignment)
            declarations.Add($"text-align:{alignment switch { ParagraphAlignment.Start => "start", ParagraphAlignment.End => "end", ParagraphAlignment.Center => "center", _ => "justify" }}");
        if (presentation.LeftIndentEm is { } left) declarations.Add($"margin-left:{left:R}em");
        if (presentation.RightIndentEm is { } right) declarations.Add($"margin-right:{right:R}em");
        if (presentation.FirstLineIndentEm is { } first) declarations.Add($"text-indent:{first:R}em");
        if (presentation.SpacingBeforePoints is { } before) declarations.Add($"margin-top:{before:R}pt");
        if (presentation.SpacingAfterPoints is { } after) declarations.Add($"margin-bottom:{after:R}pt");
        if (presentation.StartOnNewPage == true) declarations.Add("break-before:page");
        if (presentation.KeepWithNext == true) declarations.Add("break-after:avoid");
        return declarations.Count == 0 ? string.Empty : $" style=\"{string.Join(';', declarations)}\"";
    }

    internal static string HtmlInlineContent(ManuscriptBlock block) =>
        string.Concat(block.Content.Select(HtmlInline));

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
        var presentation = block.FigurePresentation ?? new FigurePresentation();
        var caption = string.IsNullOrWhiteSpace(content)
            || presentation.CaptionPlacement == FigureCaptionPlacement.Hidden
            ? string.Empty
            : $"<figcaption>{content}</figcaption>";
        var language = string.IsNullOrWhiteSpace(block.Language)
            ? string.Empty
            : $" lang=\"{WebUtility.HtmlEncode(block.Language)}\" xml:lang=\"{WebUtility.HtmlEncode(block.Language)}\"";
        var decorative = block.Decorative ? " role=\"presentation\" aria-hidden=\"true\"" : string.Empty;
        var figureStyle = new StringBuilder();
        var outputWidth = presentation.Placement is FigurePlacementIntent.FullWidth or FigurePlacementIntent.FullBleed
            ? 100 : Math.Clamp(presentation.WidthPercent, 1, 100);
        figureStyle.Append("position:relative;width:").Append(outputWidth.ToString(CultureInfo.InvariantCulture)).Append("%;")
            .Append("margin-top:").Append(presentation.SpacingBeforePoints.ToString(CultureInfo.InvariantCulture)).Append("pt;")
            .Append("margin-bottom:").Append(presentation.SpacingAfterPoints.ToString(CultureInfo.InvariantCulture)).Append("pt;")
            .Append("break-inside:").Append(presentation.KeepWithCaption ? "avoid" : "auto").Append(';');
        if (presentation.StartOnNewPage || presentation.Placement is FigurePlacementIntent.DedicatedPage or FigurePlacementIntent.FullBleed)
            figureStyle.Append("break-before:page;page-break-before:always;clear:both;");
        if (presentation.Placement == FigurePlacementIntent.Float || presentation.TextWrap != FigureTextWrap.None)
            figureStyle.Append(presentation.TextWrap == FigureTextWrap.Start || presentation.Alignment == FigureAlignment.End ? "float:right;clear:right;" : "float:left;clear:left;");
        else
            figureStyle.Append(presentation.Alignment switch { FigureAlignment.Start => "margin-left:0;margin-right:auto;", FigureAlignment.End => "margin-left:auto;margin-right:0;", _ => "margin-left:auto;margin-right:auto;" });
        if (presentation.Placement == FigurePlacementIntent.FullBleed)
            figureStyle.Append("width:100%;max-width:100%;");
        var frameHeight = presentation.Placement is FigurePlacementIntent.DedicatedPage or FigurePlacementIntent.FullBleed ? "75vh" : "40vh";
        var imageStyle = $"display:block;width:100%;height:100%;object-fit:{ImageFitCss(presentation.Fit)};object-position:{presentation.CropXPercent.ToString(CultureInfo.InvariantCulture)}% {presentation.CropYPercent.ToString(CultureInfo.InvariantCulture)}%;";
        var image = $"<div class=\"figure-media\" style=\"position:relative;width:100%;height:{frameHeight};overflow:hidden\"><img src=\"{WebUtility.HtmlEncode(href)}\" alt=\"{WebUtility.HtmlEncode(block.Decorative ? string.Empty : block.AltText)}\"{decorative} style=\"{imageStyle}\" />{(presentation.CaptionPlacement == FigureCaptionPlacement.Overlay ? $"<div class=\"figure-overlay-caption\" style=\"position:absolute;left:0;right:0;bottom:0;background:rgba(0,0,0,.65);color:white;padding:.5em\">{content}</div>" : string.Empty)}</div>";
        var contents = presentation.CaptionPlacement == FigureCaptionPlacement.Above ? caption + image : image + (presentation.CaptionPlacement == FigureCaptionPlacement.Overlay ? string.Empty : caption);
        return $"<figure id=\"{anchor}\" data-style-role=\"{role}\" data-accessibility-role=\"{(block.AccessibilityRole ?? FigureAccessibilityRole.Figure).ToString().ToLowerInvariant()}\"{language} style=\"{figureStyle}\">{contents}</figure>";
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
