using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Lorekeeper.Manuscripts;
using Lorekeeper.Composition;
using Lorekeeper.Citations;
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
        var citations = new PublicationCitationResolver(document.Citations.Occurrences);
        AppendPublicationSections(sb, document, citations, PublicationSectionAnchor.Front, null, null);

        foreach (var section in document.Sections)
        {
            AppendPublicationSections(sb, document, citations, PublicationSectionAnchor.BeforeAct, PublishOutlineTargetKind.Act, section.ActId);
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
                AppendPublicationSections(sb, document, citations, PublicationSectionAnchor.BeforeChapter, PublishOutlineTargetKind.Chapter, chapter.Id);
                AppendGap(sb);
                if (chapter.IncludeHeading)
                    AppendHeading(sb, chapter.Title, '=');
                if (document.Profile.IncludeChapterSynopses)
                    AppendText(sb, chapter.Synopsis);
                AppendVisualText(sb, document, chapter, citations);
                AppendPublicationSections(sb, document, citations, PublicationSectionAnchor.AfterChapter, PublishOutlineTargetKind.Chapter, chapter.Id);
            }
            AppendPublicationSections(sb, document, citations, PublicationSectionAnchor.AfterAct, PublishOutlineTargetKind.Act, section.ActId);
        }

        AppendPublicationSections(sb, document, citations, PublicationSectionAnchor.Back, null, null);
        AppendCitationBackMatter(sb, document);
        return Encoding.UTF8.GetBytes(sb.ToString().TrimEnd() + Environment.NewLine);
    }

    private static void AppendPublicationSections(
        StringBuilder sb,
        PublishDocument document,
        PublicationCitationResolver citations,
        PublicationSectionAnchor anchor,
        PublishOutlineTargetKind? targetKind,
        Guid? targetId)
    {
        foreach (var item in document.PublicationSections.Where(item => item.Anchor == anchor
            && item.TargetKind == targetKind && item.TargetId == targetId).OrderBy(item => item.LocalOrder))
        {
            var notes = PublicationNotes.Create(document, new($"publication-section:{item.Id:D}", item.Title, item.Manuscript, item.DesignedPages));
            var noteNumbers = notes.Numbers;
            AppendMatterStart(sb, item.Title);
            if (item.SystemRole == PublicationSectionSystemRole.Contents)
            {
                foreach (var section in document.Sections)
                {
                    if (section.IncludeHeading) sb.AppendLine(section.Title);
                    foreach (var chapter in section.Chapters.Where(chapter => chapter.IncludeHeading))
                        sb.Append("  ").AppendLine(chapter.Title);
                }
                continue;
            }
            foreach (var block in notes.Manuscript.Content)
            {
                if (block.Type == ManuscriptBlockType.DesignedPage
                    && block.DesignedPageId is Guid designedPageId
                    && item.DesignedPages.FirstOrDefault(value => value.Id == designedPageId) is { } designedPage)
                {
                    foreach (var projected in DesignedPageSemanticProjection.Blocks(designedPage with { SemanticManuscript = notes.Placements[block.Id] }))
                        AppendText(sb, SemanticPublishFormatting.PlainTextBlock(projected, imageId => FindAsset(document, imageId), noteNumbers, citations.ForDocument($"publication-section:{item.Id:D}", [$"placement:{block.Id}"])));
                }
                else
                    AppendText(sb, SemanticPublishFormatting.PlainTextBlock(block, imageId => FindAsset(document, imageId), noteNumbers, citations.ForDocument($"publication-section:{item.Id:D}")));
            }
        }
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

    private static void AppendVisualText(
        StringBuilder sb,
        PublishDocument document,
        PublishChapterDocument chapter,
        PublicationCitationResolver citations)
    {
        var notes = PublicationNotes.Create(document, new($"chapter:{chapter.Id:D}", chapter.Title, chapter.Manuscript, chapter.DesignedPages));
        var noteNumbers = notes.Numbers;
        foreach (var block in notes.Manuscript.Content)
        {
            if (block.Type == ManuscriptBlockType.DesignedPage
                && block.DesignedPageId is Guid designedPageId
                && chapter.DesignedPages.FirstOrDefault(item => item.Id == designedPageId) is { } designedPage)
            {
                foreach (var projected in DesignedPageSemanticProjection.Blocks(designedPage with { SemanticManuscript = notes.Placements[block.Id] }))
                    AppendText(sb, SemanticPublishFormatting.PlainTextBlock(projected, imageId => FindAsset(document, imageId), noteNumbers, citations.ForDocument($"chapter:{chapter.Id:D}", [$"placement:{block.Id}"])));
                continue;
            }
            AppendText(sb, SemanticPublishFormatting.PlainTextBlock(
                block,
                imageId => FindAsset(document, imageId),
                noteNumbers,
                citations.ForDocument($"chapter:{chapter.Id:D}")));
        }
    }

    private static void AppendCitationBackMatter(StringBuilder sb, PublishDocument document)
    {
        AppendText(sb, PublicationTextBackMatter.Notes(document, markdown: false));
        if (document.Citations.Bibliography.Count == 0) return;
        AppendMatterStart(sb, document.Citations.BibliographyTitle);
        foreach (var entry in document.Citations.Bibliography)
            AppendText(sb, entry);
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
        var citations = new PublicationCitationResolver(document.Citations.Occurrences);
        var cover = document.CoverAsset;
        if (cover is not null)
            AppendImage(sb, cover, "Cover");

        AppendPublicationSections(sb, document, citations, PublicationSectionAnchor.Front, null, null);

        foreach (var section in document.Sections)
        {
            AppendPublicationSections(sb, document, citations, PublicationSectionAnchor.BeforeAct, PublishOutlineTargetKind.Act, section.ActId);
            if (section.IncludePage)
            {
                if (section.IncludeHeading)
                    sb.AppendLine().Append("## ").AppendLine(EscapeHeading(section.Title));
                if (document.Profile.IncludeActSynopses)
                    AppendBlockquote(sb, section.Synopsis);
            }

            foreach (var chapter in section.Chapters)
            {
                AppendPublicationSections(sb, document, citations, PublicationSectionAnchor.BeforeChapter, PublishOutlineTargetKind.Chapter, chapter.Id);
                sb.AppendLine();
                if (chapter.IncludeHeading)
                    sb.Append("### ").AppendLine(EscapeHeading(chapter.Title));
                if (document.Profile.IncludeChapterSynopses)
                    AppendBlockquote(sb, chapter.Synopsis);
                AppendVisualMarkdown(sb, document, chapter, citations);
                AppendPublicationSections(sb, document, citations, PublicationSectionAnchor.AfterChapter, PublishOutlineTargetKind.Chapter, chapter.Id);
            }
            AppendPublicationSections(sb, document, citations, PublicationSectionAnchor.AfterAct, PublishOutlineTargetKind.Act, section.ActId);
        }

        AppendPublicationSections(sb, document, citations, PublicationSectionAnchor.Back, null, null);
        AppendCitationBackMatter(sb, document);
        return Encoding.UTF8.GetBytes(sb.ToString().TrimEnd() + Environment.NewLine);
    }

    private static void AppendPublicationSections(
        StringBuilder sb,
        PublishDocument document,
        PublicationCitationResolver citations,
        PublicationSectionAnchor anchor,
        PublishOutlineTargetKind? targetKind,
        Guid? targetId)
    {
        foreach (var item in document.PublicationSections.Where(item => item.Anchor == anchor
            && item.TargetKind == targetKind && item.TargetId == targetId).OrderBy(item => item.LocalOrder))
        {
            sb.AppendLine().Append("## ").AppendLine(EscapeHeading(item.Title));
            var notes = PublicationNotes.Create(document, new($"publication-section:{item.Id:D}", item.Title, item.Manuscript, item.DesignedPages));
            if (item.SystemRole == PublicationSectionSystemRole.Contents)
            {
                AppendToc(sb, document);
                continue;
            }
            foreach (var block in notes.Manuscript.Content)
            {
                if (block.Type == ManuscriptBlockType.DesignedPage
                    && block.DesignedPageId is Guid designedPageId
                    && item.DesignedPages.FirstOrDefault(value => value.Id == designedPageId) is { } designedPage)
                {
                    foreach (var projected in DesignedPageSemanticProjection.Blocks(designedPage with { SemanticManuscript = notes.Placements[block.Id] }))
                        sb.AppendLine().AppendLine(SemanticPublishFormatting.MarkdownBlock(projected, imageId => FindAsset(document, imageId), citations.ForDocument($"publication-section:{item.Id:D}", [$"placement:{block.Id}"]), notes.Numbers));
                }
                else
                    sb.AppendLine().AppendLine(SemanticPublishFormatting.MarkdownBlock(block, imageId => FindAsset(document, imageId), citations.ForDocument($"publication-section:{item.Id:D}"), notes.Numbers));
            }
        }
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

    private static void AppendBlockquote(StringBuilder sb, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        sb.AppendLine();
        foreach (var line in SplitLines(text.Trim()))
            sb.Append("> ").AppendLine(EscapeInline(line));
    }

    private static void AppendImage(StringBuilder sb, PublishAssetDocument asset, string caption, string? altOverride = null)
    {
        var alt = altOverride ?? (string.IsNullOrWhiteSpace(asset.AltText) ? caption : asset.AltText);
        var dataUrl = $"data:{asset.ContentType};base64,{Convert.ToBase64String(asset.Data)}";
        sb.AppendLine().Append("![").Append(EscapeInline(alt)).Append("](").Append(dataUrl).AppendLine(")");
        if (!string.IsNullOrWhiteSpace(caption))
            sb.Append("_").Append(EscapeInline(caption)).AppendLine("_");
    }

    private static void AppendVisualMarkdown(
        StringBuilder sb,
        PublishDocument document,
        PublishChapterDocument chapter,
        PublicationCitationResolver citations)
    {
        var notes = PublicationNotes.Create(document, new($"chapter:{chapter.Id:D}", chapter.Title, chapter.Manuscript, chapter.DesignedPages));
        foreach (var manuscriptBlock in notes.Manuscript.Content)
        {
            if (manuscriptBlock.Type == ManuscriptBlockType.DesignedPage
                && manuscriptBlock.DesignedPageId is Guid designedPageId
                && chapter.DesignedPages.FirstOrDefault(item => item.Id == designedPageId) is { } designedPage)
            {
                foreach (var projected in DesignedPageSemanticProjection.Blocks(designedPage with { SemanticManuscript = notes.Placements[manuscriptBlock.Id] }))
                    sb.AppendLine().AppendLine(SemanticPublishFormatting.MarkdownBlock(projected, imageId => FindAsset(document, imageId), citations.ForDocument($"chapter:{chapter.Id:D}", [$"placement:{manuscriptBlock.Id}"]), notes.Numbers));
                continue;
            }
            sb.AppendLine().AppendLine(
                SemanticPublishFormatting.MarkdownBlock(
                    manuscriptBlock,
                    imageId => FindAsset(document, imageId),
                    citations.ForDocument($"chapter:{chapter.Id:D}"), notes.Numbers));
        }
    }

    private static void AppendCitationBackMatter(StringBuilder sb, PublishDocument document)
    {
        sb.AppendLine().AppendLine(PublicationTextBackMatter.Notes(document, markdown: true));
        if (document.Citations.Bibliography.Count == 0) return;
        sb.Append("## ").AppendLine(EscapeHeading(document.Citations.BibliographyTitle));
        foreach (var entry in document.Citations.BibliographyEntries)
            sb.AppendLine().AppendLine(SemanticPublishFormatting.CitationMarkdown(entry.Runs));
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
            .Concat(document.Sections.SelectMany(section => section.Chapters).SelectMany(chapter => chapter.DesignedPages).Select(designedPage => designedPage.SemanticManuscript))
            .Concat(document.PublicationSections.Select(item => item.Manuscript))
            .Concat(document.PublicationSections.SelectMany(item => item.DesignedPages).Select(item => item.SemanticManuscript));
        if (manuscripts.Any(manuscript => ManuscriptTraversal.EnumerateBlocks(manuscript).Any(block => block.Type == ManuscriptBlockType.Figure
            && !block.Decorative && string.IsNullOrWhiteSpace(block.AltText))))
            throw new InvalidDataException("EPUB export requires alternative text or an explicit decorative decision for every Figure.");
        var scenes = document.Sections.SelectMany(section => section.Chapters)
            .SelectMany(chapter => chapter.DesignedPages)
            .Concat(document.PublicationSections.SelectMany(section => section.DesignedPages))
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
        var citations = new PublicationCitationResolver(document.Citations.Occurrences);
        var noteBacklinks = new Dictionary<string, string>(StringComparer.Ordinal);
        if (document.Cover is not null || CoverImageHref(imageItems) is not null)
        {
            var viewport = document.Cover is { } cover
                ? SceneViewport(cover.Scene)
                : CoverViewport(document.Profile);
            items.Add(new EpubXhtmlItem(
                "cover-page",
                "cover.xhtml",
                "Cover",
                RenderXhtmlPage(document, "Cover", RenderCoverBody(document, imageItems, viewport), viewport, "fixed-layout"),
                IncludeInNavigation: false,
                SpineProperties: "rendition:layout-pre-paginated rendition:spread-none"));
        }

        var publicationSectionIndex = 0;
        AddPublicationSections(items, document, imageItems, citations, PublicationSectionAnchor.Front, null, null, ref publicationSectionIndex, noteBacklinks);

        var actIndex = 0;
        var chapterIndex = 0;
        foreach (var section in document.Sections)
        {
            AddPublicationSections(items, document, imageItems, citations, PublicationSectionAnchor.BeforeAct, PublishOutlineTargetKind.Act, section.ActId, ref publicationSectionIndex, noteBacklinks);
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
                AddPublicationSections(items, document, imageItems, citations, PublicationSectionAnchor.BeforeChapter, PublishOutlineTargetKind.Chapter, chapter.Id, ref publicationSectionIndex, noteBacklinks);
                chapterIndex++;
                var chapterId = $"chapter-{chapterIndex.ToString(CultureInfo.InvariantCulture)}";
                AddChapterItems(items, document, chapter, imageItems, citations, chapterId, noteBacklinks);
                AddPublicationSections(items, document, imageItems, citations, PublicationSectionAnchor.AfterChapter, PublishOutlineTargetKind.Chapter, chapter.Id, ref publicationSectionIndex, noteBacklinks);
            }
            AddPublicationSections(items, document, imageItems, citations, PublicationSectionAnchor.AfterAct, PublishOutlineTargetKind.Act, section.ActId, ref publicationSectionIndex, noteBacklinks);
        }

        AddPublicationSections(items, document, imageItems, citations, PublicationSectionAnchor.Back, null, null, ref publicationSectionIndex, noteBacklinks);
        AddCitationBackMatter(items, document, imageItems, citations, noteBacklinks);
        return items;
    }

    private static void AddPublicationSections(
        List<EpubXhtmlItem> items,
        PublishDocument document,
        IReadOnlyList<EpubImageItem> imageItems,
        PublicationCitationResolver citations,
        PublicationSectionAnchor anchor,
        PublishOutlineTargetKind? targetKind,
        Guid? targetId,
        ref int sectionIndex,
        Dictionary<string, string> noteBacklinks)
    {
        foreach (var section in document.PublicationSections.Where(item => item.Anchor == anchor
            && item.TargetKind == targetKind && item.TargetId == targetId).OrderBy(item => item.LocalOrder))
        {
            sectionIndex++;
            var baseId = $"publication-section-{sectionIndex.ToString(CultureInfo.InvariantCulture)}";
            var notes = PublicationNotes.Create(document, new($"publication-section:{section.Id:D}", section.Title, section.Manuscript, section.DesignedPages));
            var noteNumbers = notes.Numbers;
            const string noteFile = "endnotes.xhtml";
            var segment = new List<ManuscriptBlock>();
            var part = 0;
            var first = true;
            void Flush()
            {
                var generatedContents = first && section.SystemRole == PublicationSectionSystemRole.Contents;
                if (segment.Count == 0 && !generatedContents) return;
                var id = first ? baseId : $"{baseId}-part-{++part}";
                foreach (var noteId in SemanticPublishFormatting.ReferencedNoteIds(segment))
                    noteBacklinks[noteId] = $"{id}.xhtml#note-ref-{noteId}";
                var content = generatedContents
                    ? RenderVisibleToc(document)
                    : RenderSemanticMatterBody(section.Title,
                        SemanticPublishFormatting.HtmlBlocks(
                            segment,
                            imageId => ImageHref(imageItems, imageId),
                            noteNumbers,
                            noteFile,
                            citations.ForDocument($"publication-section:{section.Id:D}", backlink: $"{id}.xhtml"),
                            "endnotes.xhtml"));
                items.Add(new EpubXhtmlItem(id, $"{id}.xhtml", section.Title,
                    RenderXhtmlPage(document, section.Title, content), IncludeInNavigation: first));
                segment.Clear();
                first = false;
            }

            var designedIndex = 0;
            foreach (var block in notes.Manuscript.Content)
            {
                if (block.Type != ManuscriptBlockType.DesignedPage || block.DesignedPageId is not Guid designedPageId)
                {
                    segment.Add(block);
                    continue;
                }
                Flush();
                var composition = section.DesignedPages.FirstOrDefault(item => item.Id == designedPageId)
                    ?? throw new InvalidOperationException($"Designed Page '{designedPageId:N}' is missing from publication section '{section.Title}'.");
                var variant = composition.Variants.FirstOrDefault()
                    ?? throw new InvalidOperationException($"Designed Page '{composition.Name}' has no layout for EPUB export.");
                var id = first ? baseId : $"{baseId}-designed-{++designedIndex}";
                var viewport = SceneViewport(variant.Scene);
                var body = new StringBuilder();
                var placedManuscript = notes.Placements[block.Id];
                foreach (var noteId in SemanticPublishFormatting.ReferencedNoteIds(placedManuscript.Content))
                    noteBacklinks[noteId] = $"{id}.xhtml#note-ref-{noteId}";
                AppendDesignedPage(
                    body,
                    composition with { SemanticManuscript = placedManuscript },
                    imageItems,
                    citations.ForDocument($"publication-section:{section.Id:D}", [$"placement:{block.Id}"], $"{id}.xhtml"),
                    "endnotes.xhtml", noteNumbers, noteFile);
                items.Add(new EpubXhtmlItem(id, $"{id}.xhtml", composition.Name,
                    RenderXhtmlPage(document, composition.Name, body.ToString(), viewport, "fixed-layout"),
                    IncludeInNavigation: first,
                    SpineProperties: variant.Scene.Surface.Kind == CompositionSurfaceKind.FacingSpread
                        ? "rendition:layout-pre-paginated rendition:spread-none rendition:page-spread-center"
                        : "rendition:layout-pre-paginated rendition:spread-none"));
                first = false;
            }
            Flush();
        }
    }

    private static void AddChapterItems(
        List<EpubXhtmlItem> items,
        PublishDocument document,
        PublishChapterDocument chapter,
        IReadOnlyList<EpubImageItem> imageItems,
        PublicationCitationResolver citations,
        string chapterId,
        Dictionary<string, string> noteBacklinks)
    {
        var notes = PublicationNotes.Create(document, new($"chapter:{chapter.Id:D}", chapter.Title, chapter.Manuscript, chapter.DesignedPages));
        var noteNumbers = notes.Numbers;
        const string noteFile = "endnotes.xhtml";
        var segment = new List<ManuscriptBlock>();
        var part = 0;
        var firstReflow = true;
        void FlushReflow()
        {
            var hasOpeningPresentation = firstReflow
                && (chapter.IncludeHeading
                    || (document.Profile.IncludeChapterSynopses && !string.IsNullOrWhiteSpace(chapter.Synopsis)));
            if (segment.Count == 0 && !hasOpeningPresentation) return;
            var id = firstReflow ? chapterId : $"{chapterId}-part-{++part}";
            var title = firstReflow ? chapter.Title : $"{chapter.Title}, continued";
            foreach (var noteId in SemanticPublishFormatting.ReferencedNoteIds(segment))
                noteBacklinks[noteId] = $"{id}.xhtml#note-ref-{noteId}";
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
                        firstReflow,
                        noteNumbers,
                        noteFile,
                        citations.ForDocument($"chapter:{chapter.Id:D}", backlink: $"{id}.xhtml"),
                        "endnotes.xhtml")),
                IncludeInNavigation: firstReflow));
            segment.Clear();
            firstReflow = false;
        }

        var designedIndex = 0;
        foreach (var block in notes.Manuscript.Content)
        {
            if (block.Type != ManuscriptBlockType.DesignedPage
                || block.DesignedPageId is not Guid designedPageId)
            {
                segment.Add(block);
                continue;
            }
            FlushReflow();
            var composition = chapter.DesignedPages.FirstOrDefault(item => item.Id == designedPageId)
                ?? throw new InvalidOperationException($"Designed Page '{designedPageId:N}' is missing from the chapter publication document.");
            var variant = composition.Variants.FirstOrDefault()
                ?? throw new InvalidOperationException($"Designed Page '{composition.Name}' has no layout for EPUB export.");
            var id = firstReflow ? chapterId : $"{chapterId}-designed-{++designedIndex}";
            var viewport = SceneViewport(variant.Scene);
            var spread = variant.Scene.Surface.Kind == CompositionSurfaceKind.FacingSpread
                ? "rendition:layout-pre-paginated rendition:spread-none rendition:page-spread-center"
                : "rendition:layout-pre-paginated rendition:spread-none";
            var body = new StringBuilder();
            var placedManuscript = notes.Placements[block.Id];
            foreach (var noteId in SemanticPublishFormatting.ReferencedNoteIds(placedManuscript.Content))
                noteBacklinks[noteId] = $"{id}.xhtml#note-ref-{noteId}";
            AppendDesignedPage(
                body,
                composition with { SemanticManuscript = placedManuscript },
                imageItems,
                citations.ForDocument($"chapter:{chapter.Id:D}", [$"placement:{block.Id}"], $"{id}.xhtml"),
                "endnotes.xhtml", noteNumbers, noteFile);
            items.Add(new EpubXhtmlItem(
                id,
                $"{id}.xhtml",
                composition.Name,
                RenderXhtmlPage(document, composition.Name, body.ToString(), viewport, "fixed-layout"),
                IncludeInNavigation: firstReflow,
                SpineProperties: spread));
            firstReflow = false;
        }
        FlushReflow();
    }

    private static void AddCitationBackMatter(
        ICollection<EpubXhtmlItem> items,
        PublishDocument document,
        IReadOnlyList<EpubImageItem> imageItems,
        PublicationCitationResolver citations,
        IReadOnlyDictionary<string, string> noteBacklinks)
    {
        var body = new StringBuilder();
        foreach (var container in PublicationSemanticDocuments.Enumerate(document))
        {
            var notes = PublicationNotes.Create(document, container);
            var citationNotes = document.Citations.Occurrences.Where(citation => citation.Identity.TopLevelContainer == container.Id
                && citation.NoteRuns is not null).ToList();
            if (notes.Occurrences.Count == 0 && citationNotes.Count == 0) continue;
            body.Append("<section><h2>").Append(Html(container.Title)).Append("</h2>");
            foreach (var group in notes.Occurrences.GroupBy(note => note.Note.Kind))
            {
                body.Append("<section><h3>").Append(group.Key == ManuscriptNoteKind.Footnote ? "Footnotes" : "Author notes").Append("</h3><ol>");
                foreach (var note in group)
                {
                    body.Append("<li id=\"note-").Append(note.Note.Id).Append("\" value=\"").Append(note.Number).Append("\">");
                    body.Append(SemanticPublishFormatting.HtmlBlocks(note.Note.Content, imageId => ImageHref(imageItems, imageId), notes.Numbers,
                            citation: citations.ForDocument(container.Id, note.PlacementPath, "endnotes.xhtml"), citationHrefPrefix: "endnotes.xhtml"));
                    if (!noteBacklinks.TryGetValue(note.Note.Id, out var backlink))
                        throw new InvalidDataException($"Note '{note.Note.Id}' has no exported reference occurrence.");
                    body.Append("<a role=\"doc-backlink\" aria-label=\"Back to note reference\" href=\"").Append(Html(backlink)).Append("\">↩</a>");
                    body.Append("</li>");
                }
                body.Append("</ol></section>");
            }
            if (citationNotes.Count > 0)
            {
                body.Append("<section><h3>Citations</h3><ol>");
                foreach (var citation in citationNotes)
                {
                    var anchor = PublicationCitationResolver.Anchor(citation);
                    body.Append("<li id=\"citation-note-").Append(anchor).Append("\" value=\"").Append(citation.NoteNumber).Append("\">")
                        .Append(SemanticPublishFormatting.CitationHtml(citation.NoteRuns!));
                    if (citations.BacklinkFor(citation) is { } backlink)
                        body.Append(" <a role=\"doc-backlink\" aria-label=\"Back to citation\" href=\"")
                            .Append(Html(backlink)).Append("#citation-ref-").Append(anchor).Append("\">↩</a>");
                    body.Append("</li>");
                }
                body.Append("</ol></section>");
            }
            body.Append("</section>");
        }
        if (body.Length > 0)
        {
            items.Add(new EpubXhtmlItem(
                "endnotes",
                "endnotes.xhtml",
                "Endnotes",
                RenderXhtmlPage(document, "Endnotes", "<section role=\"doc-endnotes\"><h1>Endnotes</h1>" + body + "</section>")));
        }

        if (document.Citations.Bibliography.Count == 0)
            return;
        var bibliography = new StringBuilder("<section class=\"bibliography\" role=\"doc-bibliography\"><h1>")
            .Append(Html(document.Citations.BibliographyTitle)).Append("</h1>");
        foreach (var entry in document.Citations.BibliographyEntries)
            bibliography.Append("<p style=\"margin-left:0.5in;text-indent:-0.5in\">")
                .Append(SemanticPublishFormatting.CitationHtml(entry.Runs)).Append("</p>");
        bibliography.Append("</section>");
        items.Add(new EpubXhtmlItem(
            "citation-bibliography",
            "citation-bibliography.xhtml",
            document.Citations.BibliographyTitle,
            RenderXhtmlPage(document, document.Citations.BibliographyTitle, bibliography.ToString())));
    }

    private static List<EpubImageItem> BuildImageItems(PublishDocument document)
    {
        var items = new List<EpubImageItem>();
        var assets = new Dictionary<Guid, PublishAssetDocument>();
        foreach (var chapter in document.Sections.SelectMany(section => section.Chapters))
        {
            foreach (var block in ManuscriptTraversal.EnumerateBlocks(chapter.Manuscript).Where(block =>
                block.Type == ManuscriptBlockType.Figure))
            {
                if (block.ImageId is Guid imageId
                    && document.Assets.FirstOrDefault(asset => asset.Id == imageId) is { } asset)
                {
                    assets[asset.Id] = asset;
                }
            }
            foreach (var imageId in chapter.DesignedPages
                .SelectMany(composition => composition.Variants)
                .SelectMany(variant => CompositionSceneResolver.Flatten(variant.Scene))
                .Where(item => item.Kind == CompositionObjectKind.Image && item.ImageId is not null)
                .Select(item => item.ImageId!.Value))
            {
                if (document.Assets.FirstOrDefault(asset => asset.Id == imageId) is { } asset)
                    assets[asset.Id] = asset;
            }
        }
        foreach (var section in document.PublicationSections)
        {
            foreach (var block in ManuscriptTraversal.EnumerateBlocks(section.Manuscript).Where(block => block.Type == ManuscriptBlockType.Figure))
            {
                if (block.ImageId is Guid imageId
                    && document.Assets.FirstOrDefault(asset => asset.Id == imageId) is { } asset)
                    assets[asset.Id] = asset;
            }
            foreach (var imageId in section.DesignedPages.SelectMany(composition => composition.Variants)
                .SelectMany(variant => CompositionSceneResolver.Flatten(variant.Scene))
                .Where(item => item.Kind == CompositionObjectKind.Image && item.ImageId is not null)
                .Select(item => item.ImageId!.Value))
            {
                if (document.Assets.FirstOrDefault(asset => asset.Id == imageId) is { } asset)
                    assets[asset.Id] = asset;
            }
        }
        if (document.Cover is { } composedCover)
        {
            foreach (var imageId in CompositionSceneResolver.Flatten(composedCover.Scene)
                .Where(item => item.Kind == CompositionObjectKind.Image && item.ImageId is not null)
                .Select(item => item.ImageId!.Value))
            {
                if (document.Assets.FirstOrDefault(asset => asset.Id == imageId) is { } asset)
                    assets[asset.Id] = asset;
            }
        }
        foreach (var page in document.Sections.SelectMany(section => section.Chapters).SelectMany(chapter => chapter.DesignedPages)
            .Concat(document.PublicationSections.SelectMany(section => section.DesignedPages)))
        foreach (var block in ManuscriptTraversal.EnumerateBlocks(page.SemanticManuscript).Where(block => block.Type == ManuscriptBlockType.Figure))
        {
            if (block.ImageId is Guid imageId && document.Assets.FirstOrDefault(asset => asset.Id == imageId) is { } asset)
                assets[asset.Id] = asset;
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
        var textBindings = PublicationTextBindings.Bindings(
            cover.Title, cover.Subtitle, cover.Author, document.Profile.Publisher,
            document.Profile.Copyright, document.Profile.Description, document.Profile.Isbn, cover.SpineText);
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
                var text = PublicationTextBindings.Resolve(item.TextBinding, textBindings);
                sb.Append("<foreignObject x=\"").Append(x.ToString(CultureInfo.InvariantCulture)).Append("\" y=\"").Append(y.ToString(CultureInfo.InvariantCulture))
                    .Append("\" width=\"").Append(width.ToString(CultureInfo.InvariantCulture)).Append("\" height=\"").Append(height.ToString(CultureInfo.InvariantCulture))
                    .Append("\" opacity=\"").Append(item.Opacity.ToString(CultureInfo.InvariantCulture)).Append("\" transform=\"").Append(transform).Append("\"><div xmlns=\"http://www.w3.org/1999/xhtml\" style=\"box-sizing:border-box;display:flex;flex-direction:column;width:100%;height:100%;overflow:hidden;overflow-wrap:anywhere;white-space:pre-wrap;color:")
                    .Append(Html(item.FillColor)).Append(";background:").Append(Html(BackgroundCss(item.BackgroundColor, item.BackgroundOpacity)))
                    .Append(";border:").Append(item.StrokeWidthPoints.ToString(CultureInfo.InvariantCulture)).Append("px solid ").Append(Html(item.StrokeColor))
                    .Append(";font-family:").Append(Html(FontCssFamily(item.FontFamilyKey))).Append(";font-size:").Append(item.FontSizePoints.ToString(CultureInfo.InvariantCulture)).Append("px;font-weight:").Append(item.FontWeight)
                    .Append(";font-style:").Append(item.Italic ? "italic" : "normal").Append(";line-height:").Append(item.LineHeight.ToString(CultureInfo.InvariantCulture))
                    .Append(";letter-spacing:").Append(item.LetterSpacingEm.ToString(CultureInfo.InvariantCulture)).Append("em;text-align:")
                    .Append(item.TextAlignment == CompositionTextAlignment.Center ? "center" : item.TextAlignment == CompositionTextAlignment.End ? "right" : item.TextAlignment == CompositionTextAlignment.Justify ? "justify" : "left")
                    .Append(";justify-content:").Append(item.VerticalAlignment == CompositionVerticalAlignment.Center ? "center" : item.VerticalAlignment == CompositionVerticalAlignment.Bottom ? "flex-end" : "flex-start")
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
            var textBindings = PublicationTextBindings.Bindings(
                cover.Title, cover.Subtitle, cover.Author, document.Profile.Publisher,
                document.Profile.Copyright, document.Profile.Description, document.Profile.Isbn, cover.SpineText);
            foreach (var sceneItem in CompositionSceneResolver.Flatten(cover.Scene).Where(item => item.Visible && visibleLayers.Contains(item.LayerId))
                .OrderBy(item => item.ReadingOrder is null ? 1 : 0)
                .ThenBy(item => item.ReadingOrder)
                .ThenBy(item => item.ZIndex))
            {
                var item = ResolveCompositionStyle(cover.Scene, sceneItem);
                var style = FormattableString.Invariant(
                    $"left:{item.Bounds.XPercent}%;top:{item.Bounds.YPercent}%;width:{item.Bounds.WidthPercent}%;height:{item.Bounds.HeightPercent}%;opacity:{item.Opacity};transform:rotate({item.RotationDegrees}deg);z-index:{item.ZIndex};color:{item.FillColor};background:{BackgroundCss(item.BackgroundColor, item.BackgroundOpacity)};border:{item.StrokeWidthPoints}px solid {item.StrokeColor};font-family:{FontCssFamily(item.FontFamilyKey)};font-weight:{item.FontWeight};font-style:{(item.Italic ? "italic" : "normal")};font-size:{item.FontSizePoints}px;line-height:{item.LineHeight};letter-spacing:{item.LetterSpacingEm}em;text-align:{(item.TextAlignment == CompositionTextAlignment.Center ? "center" : item.TextAlignment == CompositionTextAlignment.End ? "right" : item.TextAlignment == CompositionTextAlignment.Justify ? "justify" : "left")};justify-content:{(item.VerticalAlignment == CompositionVerticalAlignment.Center ? "center" : item.VerticalAlignment == CompositionVerticalAlignment.Bottom ? "flex-end" : "flex-start")};text-shadow:{TextShadowCss(item.TextShadow)};object-fit:{ImageFitCss(item.ImageFit)};object-position:{item.CropXPercent}% {item.CropYPercent}%");
                if (item.Kind == CompositionObjectKind.Text)
                {
                    var text = PublicationTextBindings.Resolve(item.TextBinding, textBindings);
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
        sb.AppendLine("<section class=\"act-page\">");
        if (section.IncludeHeading)
            sb.Append("<h1>").Append(Html(section.Title)).AppendLine("</h1>");
        if (document.Profile.IncludeActSynopses)
            AppendTextBlocks(sb, section.Synopsis, "synopsis");
        sb.AppendLine("</section>");
        return sb.ToString();
    }

    private static string RenderChapterSegmentBody(
        PublishDocument document,
        PublishChapterDocument chapter,
        IReadOnlyList<EpubImageItem> imageItems,
        IReadOnlyList<ManuscriptBlock> blocks,
        bool includeOpening,
        IReadOnlyDictionary<string, int> noteNumbers,
        string noteFile,
        Func<string, FormattedCitationCluster?> citation,
        string citationHrefPrefix)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<article class=\"chapter-page\">");
        if (includeOpening && chapter.IncludeHeading)
            sb.Append("<h1>").Append(Html(chapter.Title)).AppendLine("</h1>");
        if (includeOpening && document.Profile.IncludeChapterSynopses)
            AppendTextBlocks(sb, chapter.Synopsis, "synopsis");
        sb.AppendLine("<div class=\"chapter-body\">");
        sb.Append(SemanticPublishFormatting.HtmlBlocks(
                blocks,
                imageId => ImageHref(imageItems, imageId),
                noteNumbers,
                noteFile,
                citation,
                citationHrefPrefix));
        sb.AppendLine("</div>");
        sb.AppendLine("</article>");
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

    private static void AppendDesignedPage(
        StringBuilder sb,
        PublishDesignedPageDocument composition,
        IReadOnlyList<EpubImageItem> imageItems,
        Func<string, FormattedCitationCluster?> citation,
        string citationHrefPrefix,
        IReadOnlyDictionary<string, int> noteNumbers,
        string noteFile)
    {
        var variant = composition.Variants.FirstOrDefault();
        if (variant is null)
            throw new InvalidOperationException($"Designed Page '{composition.Name}' has no layout for EPUB export.");
        var scene = DesignedPageService.WithDerivedTextSemanticRoles(
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
                    ? HtmlBoundCompositionRanges(
                        composition.SemanticManuscript,
                        item.ContentReferences,
                        citation,
                        citationHrefPrefix, noteNumbers, noteFile)
                    : Html(item.TextBinding);
                var (tag, epubType) = CompositionTextSemantics(item.SemanticRole);
                sb.Append('<').Append(tag).Append(" class=\"composition-object composition-text\"");
                if (epubType is not null) sb.Append(" epub:type=\"").Append(epubType).Append('"');
                if (PublicationLanguage.NormalizeOptional(item.Language) is { } language)
                    sb.Append(" lang=\"").Append(Html(language)).Append("\" xml:lang=\"").Append(Html(language)).Append('"');
                sb.Append(" style=\"").Append(style)
                    .Append(";font-family:").Append(Html(FontCssFamily(item.FontFamilyKey))).Append(";font-weight:")
                    .Append(item.FontWeight).Append(";font-style:").Append(item.Italic ? "italic" : "normal")
                    .Append(";font-size:").Append(item.FontSizePoints.ToString(CultureInfo.InvariantCulture)).Append("px;line-height:")
                    .Append(item.LineHeight.ToString(CultureInfo.InvariantCulture)).Append(";letter-spacing:")
                    .Append(item.LetterSpacingEm.ToString(CultureInfo.InvariantCulture)).Append("em;color:")
                    .Append(Html(item.FillColor)).Append(";background:").Append(Html(BackgroundCss(item.BackgroundColor, item.BackgroundOpacity)))
                    .Append(";text-align:").Append(item.TextAlignment switch { CompositionTextAlignment.Center => "center", CompositionTextAlignment.End => "right", CompositionTextAlignment.Justify => "justify", _ => "left" })
                    .Append(";justify-content:").Append(item.VerticalAlignment switch { CompositionVerticalAlignment.Center => "center", CompositionVerticalAlignment.Bottom => "flex-end", _ => "flex-start" })
                    .Append(";text-shadow:").Append(TextShadowCss(item.TextShadow))
                    .Append(";-webkit-text-stroke:").Append(item.StrokeWidthPoints.ToString(CultureInfo.InvariantCulture)).Append("px ")
                    .Append(Html(item.StrokeColor)).Append("\">").Append(text).Append("</").Append(tag).AppendLine(">");
            }
            else
            {
                var shapeStyle = item.Kind == CompositionObjectKind.Line
                    ? style + $";height:0;border-top:{item.StrokeWidthPoints.ToString(CultureInfo.InvariantCulture)}px solid {item.StrokeColor};background:transparent"
                    : style + $";background:{item.FillColor};border:{item.StrokeWidthPoints.ToString(CultureInfo.InvariantCulture)}px solid {item.StrokeColor}"
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
        IReadOnlyList<ManuscriptRangeReference> references,
        Func<string, FormattedCitationCluster?> citation,
        string citationHrefPrefix,
        IReadOnlyDictionary<string, int> noteNumbers,
        string noteFile)
    {
        var blocks = ManuscriptRangeResolver.ResolveBlocks(semantic, references);
        var sb = new StringBuilder();
        string? previousBlockId = null;
        foreach (var block in blocks)
        {
            if (previousBlockId is not null && !string.Equals(previousBlockId, block.Id, StringComparison.Ordinal))
                sb.Append("<br />");
            sb.Append(SemanticPublishFormatting.HtmlInlineContent(
                block,
                noteNumbers,
                noteFile,
                citation: citation,
                citationHrefPrefix: citationHrefPrefix));
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

    private static EpubViewport SceneViewport(CompositionScene scene) => new(
        Math.Max(1, (int)Math.Round(scene.Surface.WidthPoints)),
        Math.Max(1, (int)Math.Round(scene.Surface.HeightPoints)));

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
          font-family: "Lora", Georgia, "Times New Roman", serif;
          font-size: {{fontSize}}pt;
          line-height: {{lineHeight}};
          margin: {{marginPercent}}%;
          text-align: left;
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

        a {
          color: inherit;
          text-decoration: underline;
        }

        .chapter-body p,
        .matter-page p {
          margin: 0;
        }

        .chapter-body p[data-style-role="body"]:not([style*="margin-bottom"]):not([style*="margin-top"]),
        .matter-page p[data-style-role="body"]:not([style*="margin-bottom"]):not([style*="margin-top"]) {
          margin-bottom: 8pt;
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
          flex-direction: column;
          overflow: hidden;
          overflow-wrap: anywhere;
          white-space: pre-wrap;
        }

        .composition-text {
          padding: .25rem;
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
                : $"[data-style-role=\"{CssString(style.SemanticRole)}\" i]:not(figure), figure[data-style-role=\"{CssString(style.SemanticRole)}\" i] > figcaption, figure[data-style-role=\"{CssString(style.SemanticRole)}\" i] .figure-overlay-caption > span";
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
        if (definition.Italic is bool italic)
            declarations.Add($"font-style: {(italic ? "italic" : "normal")}");
        if (definition.SmallCaps is bool smallCaps)
            declarations.Add($"font-variant-caps: {(smallCaps ? "small-caps" : "normal")}");
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
        PublicationLanguage.Normalize(document.Profile.Language);

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

internal static class DesignedPageSemanticProjection
{
    public static IReadOnlyList<ManuscriptBlock> Blocks(PublishDesignedPageDocument composition)
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
    public static string CitationHtml(IReadOnlyList<CitationRun> runs) => string.Concat(runs.Select(run =>
        run.Italic ? $"<em>{WebUtility.HtmlEncode(run.Text)}</em>" : WebUtility.HtmlEncode(run.Text)));

    public static string CitationMarkdown(IReadOnlyList<CitationRun> runs) => string.Concat(runs.Select(run =>
        run.Italic ? $"*{EscapeMarkdown(run.Text)}*" : EscapeMarkdown(run.Text)));
    private static string ImageFitCss(FigureImageFit fit) => fit switch
    {
        FigureImageFit.Cover => "cover",
        FigureImageFit.Stretch => "fill",
        _ => "contain",
    };

    public static string PlainText(
        ManuscriptDocument manuscript,
        Func<Guid, PublishAssetDocument?> asset,
        Func<string, FormattedCitationCluster?>? citation = null)
    {
        manuscript = ManuscriptLists.Resolve(manuscript);
        var noteNumbers = NoteNumbers(manuscript);
        var blocks = new List<string>();
        foreach (var block in manuscript.Content)
            blocks.Add(PlainTextBlock(block, asset, noteNumbers, citation));
        var notes = PlainTextNotes(manuscript, asset, noteNumbers, citation);
        if (!string.IsNullOrEmpty(notes)) blocks.Add(notes);
        return string.Join(Environment.NewLine + Environment.NewLine, blocks);
    }

    internal static string PlainTextNotes(
        ManuscriptDocument manuscript,
        Func<Guid, PublishAssetDocument?> asset,
        IReadOnlyDictionary<string, int>? noteNumbers = null,
        Func<string, FormattedCitationCluster?>? citation = null)
    {
        noteNumbers ??= NoteNumbers(manuscript);
        return string.Join(Environment.NewLine, manuscript.Notes.Select(note =>
            $"[{noteNumbers[note.Id]}] {string.Join(" ", note.Content.Select(item => PlainTextBlock(item, asset, noteNumbers, citation)))}"));
    }

    internal static string PlainTextBlock(
        ManuscriptBlock block,
        Func<Guid, PublishAssetDocument?> asset,
        IReadOnlyDictionary<string, int>? noteNumbers = null,
        Func<string, FormattedCitationCluster?>? citation = null)
    {
        if (block.Type == ManuscriptBlockType.SceneBreak)
            return "***";
        if (block.Type == ManuscriptBlockType.Table)
            return string.Join("\n", block.Table!.Rows.Select(row => string.Join("\t", row.Cells.Select(cell =>
                string.Join("\n", cell.Content.Select(child => PlainTextBlock(child, asset, noteNumbers, citation)))))));
        var text = string.Concat(block.Content.Select(inline => inline.Type switch
        {
            ManuscriptInlineType.NoteReference => noteNumbers is not null && noteNumbers.TryGetValue(inline.NoteId!, out var number)
                ? $"[{number}]"
                : "[*]",
            ManuscriptInlineType.Citation => citation?.Invoke(inline.Id!)?.InlineText is { } value
                ? $"[{value}]"
                : "[citation]",
            _ => inline.Text,
        }));
        if (block.Type == ManuscriptBlockType.ListItem)
            return $"{new string(' ', (block.List?.Level ?? 0) * 4)}{ManuscriptLists.Marker(block)} {text}";
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
        Func<Guid, PublishAssetDocument?> asset,
        Func<string, FormattedCitationCluster?>? citation = null)
    {
        manuscript = ManuscriptLists.Resolve(manuscript);
        var blocks = new List<string>();
        var noteNumbers = NoteNumbers(manuscript);
        foreach (var block in manuscript.Content)
            blocks.Add(MarkdownBlock(block, asset, citation, noteNumbers));
        blocks.AddRange(manuscript.Notes.Select(note => MarkdownNote(note, noteNumbers[note.Id], asset, citation)));
        return string.Join(Environment.NewLine + Environment.NewLine, blocks);
    }

    internal static string MarkdownNote(
        ManuscriptNote note,
        int number,
        Func<Guid, PublishAssetDocument?> asset,
        Func<string, FormattedCitationCluster?>? citation = null) =>
        $"<a id=\"note-{WebUtility.HtmlEncode(note.Id)}\"></a>\n\n{number}. "
        + string.Join("\n\n", note.Content.Select(item => MarkdownBlock(item, asset, citation)))
        + $"\n\n[↩](#note-ref-{Uri.EscapeDataString(note.Id)})";

    internal static string MarkdownBlock(
        ManuscriptBlock block,
        Func<Guid, PublishAssetDocument?> asset,
        Func<string, FormattedCitationCluster?>? citation = null,
        IReadOnlyDictionary<string, int>? noteNumbers = null)
    {
        if (!ManuscriptStyleService.BuiltInParagraphRoles.Contains(block.StyleRole))
        {
            if (block.Type == ManuscriptBlockType.Table)
                return MarkdownTable(block, asset, citation, noteNumbers);
            return HtmlBlock(
                block,
                imageId => asset(imageId) is { } image
                    ? $"data:{image.ContentType};base64,{Convert.ToBase64String(image.Data)}"
                    : null,
                noteNumbers: noteNumbers, citation: citation);
        }
        var text = string.Concat(block.Content.Select(inline => MarkdownInline(inline, citation, noteNumbers)));
        return block.Type switch
        {
            ManuscriptBlockType.Heading =>
                $"{new string('#', block.HeadingLevel ?? 2)} {text}",
            ManuscriptBlockType.SceneBreak => "***",
            ManuscriptBlockType.BlockQuote => string.Join(
                Environment.NewLine,
                text.Split('\n').Select(line => $"> {line}")),
            ManuscriptBlockType.ListItem => $"{new string(' ', (block.List?.Level ?? 0) * 4)}{(block.List?.Ordered == true ? ManuscriptLists.Marker(block) : "-")} {text}",
            ManuscriptBlockType.Figure => MarkdownFigure(
                block,
                text,
                asset(block.ImageId!.Value)),
            ManuscriptBlockType.Table => MarkdownTable(block, asset, citation, noteNumbers),
            _ => text,
        };
    }

    public static string Html(
        ManuscriptDocument manuscript,
        Func<Guid, string?> imageHref,
        Func<string, FormattedCitationCluster?>? citation = null,
        string? citationHrefPrefix = null)
    {
        manuscript = ManuscriptLists.Resolve(manuscript);
        var noteNumbers = NoteNumbers(manuscript);
        var sb = new StringBuilder();
        sb.Append(HtmlBlocks(
                manuscript.Content,
                imageHref,
                noteNumbers,
                citation: citation,
                citationHrefPrefix: citationHrefPrefix));
        sb.Append(HtmlNotes(
            manuscript,
            imageHref,
            citation: citation,
            citationHrefPrefix: citationHrefPrefix));
        return sb.ToString();
    }

    internal static string HtmlNotes(
        ManuscriptDocument manuscript,
        Func<Guid, string?> imageHref,
        IReadOnlyDictionary<string, string>? backlinkHrefs = null,
        Func<string, FormattedCitationCluster?>? citation = null,
        string? citationHrefPrefix = null)
    {
        manuscript = ManuscriptLists.Resolve(manuscript);
        var noteNumbers = NoteNumbers(manuscript);
        var sb = new StringBuilder();
        foreach (var group in manuscript.Notes.GroupBy(note => note.Kind))
        {
            sb.Append("<section class=\"manuscript-notes manuscript-notes--")
                .Append(group.Key.ToString().ToLowerInvariant())
                .Append("\" role=\"doc-")
                .Append(group.Key == ManuscriptNoteKind.Footnote ? "footnotes" : "endnotes")
                .Append("\"><ol>");
            foreach (var note in group)
            {
                sb.Append("<li id=\"note-").Append(WebUtility.HtmlEncode(note.Id)).Append("\">");
                sb.Append(HtmlBlocks(
                        note.Content,
                        imageHref,
                        noteNumbers,
                        citation: citation,
                        citationHrefPrefix: citationHrefPrefix));
                var backlink = backlinkHrefs?.GetValueOrDefault(note.Id)
                    ?? $"#note-ref-{note.Id}";
                sb.Append("<a href=\"").Append(WebUtility.HtmlEncode(backlink))
                    .Append("\" role=\"doc-backlink\" aria-label=\"Back to note reference\">↩</a></li>");
            }
            sb.Append("</ol></section>");
        }
        return sb.ToString();
    }

    internal static string HtmlBlock(
        ManuscriptBlock block,
        Func<Guid, string?> imageHref,
        IReadOnlyDictionary<string, int>? noteNumbers = null,
        string? noteHrefPrefix = null,
        Func<string, FormattedCitationCluster?>? citation = null,
        string? citationHrefPrefix = null)
    {
        var content = HtmlInlineContent(block, noteNumbers, noteHrefPrefix, citation, citationHrefPrefix);
        var role = WebUtility.HtmlEncode(block.StyleRole);
        var anchor = WebUtility.HtmlEncode($"block-{block.Id}");
        var language = PublicationLanguage.NormalizeOptional(block.Language) is not { } languageTag
            ? string.Empty
            : $" lang=\"{WebUtility.HtmlEncode(languageTag)}\" xml:lang=\"{WebUtility.HtmlEncode(languageTag)}\"";
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
                HtmlListItem(block, content),
            ManuscriptBlockType.Figure => HtmlFigure(block, content, role, anchor, imageHref),
            ManuscriptBlockType.Table => HtmlTable(
                block,
                anchor,
                imageHref,
                noteNumbers,
                noteHrefPrefix,
                citation,
                citationHrefPrefix),
            _ => $"<p id=\"{anchor}\" data-style-role=\"{role}\"{language}{presentation}>{content}</p>",
        };
    }

    internal static string HtmlBlocks(
        IEnumerable<ManuscriptBlock> blocks,
        Func<Guid, string?> imageHref,
        IReadOnlyDictionary<string, int>? noteNumbers = null,
        string? noteHrefPrefix = null,
        Func<string, FormattedCitationCluster?>? citation = null,
        string? citationHrefPrefix = null)
    {
        var result = new StringBuilder();
        var lists = new Stack<(int Level, string Id, string Tag)>();
        void CloseList()
        {
            var list = lists.Pop();
            result.Append("</li></").Append(list.Tag).Append('>');
        }
        foreach (var block in blocks)
        {
            if (block.Type != ManuscriptBlockType.ListItem)
            {
                while (lists.Count > 0) CloseList();
                result.Append(HtmlBlock(block, imageHref, noteNumbers, noteHrefPrefix, citation, citationHrefPrefix));
                continue;
            }
            var level = block.List?.Level ?? 0;
            var id = block.List?.Id ?? string.Empty;
            var tag = block.List?.Ordered == true ? "ol" : "ul";
            while (lists.TryPeek(out var parent) && (parent.Level > level
                || parent.Level == level && (parent.Id != id || parent.Tag != tag))) CloseList();
            if (lists.TryPeek(out var current) && current.Level == level)
                result.Append("</li>");
            else
            {
                result.Append('<').Append(tag);
                if (tag == "ol") result.Append(" start=\"").Append(block.List!.Start ?? 1).Append('"');
                if (lists.Count == 0 && level > 0) result.Append(" style=\"margin-left:").Append(level * 2).Append("em\"");
                result.Append('>');
                lists.Push((level, id, tag));
            }
            result.Append(HtmlListEntry(block, HtmlInlineContent(block, noteNumbers, noteHrefPrefix, citation, citationHrefPrefix)));
        }
        while (lists.Count > 0) CloseList();
        return result.ToString();
    }

    private static string HtmlListEntry(ManuscriptBlock block, string content)
    {
        var value = block.List?.Ordered == true ? $" value=\"{block.List.Start ?? 1}\"" : string.Empty;
        var language = PublicationLanguage.NormalizeOptional(block.Language) is { } tag
            ? $" lang=\"{WebUtility.HtmlEncode(tag)}\" xml:lang=\"{WebUtility.HtmlEncode(tag)}\"" : string.Empty;
        return $"<li id=\"{WebUtility.HtmlEncode("block-" + block.Id)}\"{value} aria-level=\"{(block.List?.Level ?? 0) + 1}\" data-style-role=\"{WebUtility.HtmlEncode(block.StyleRole)}\"{language}{ParagraphPresentationAttribute(block.ParagraphPresentation)}>{content}";
    }

    private static string HtmlListItem(ManuscriptBlock block, string content)
    {
        var ordered = block.List?.Ordered == true;
        var tag = ordered ? "ol" : "ul";
        var start = ordered ? $" start=\"{block.List!.Start ?? 1}\"" : string.Empty;
        var level = block.List?.Level ?? 0;
        return $"<{tag}{start} style=\"margin-left:{level * 2}em\">{HtmlListEntry(block, content)}</li></{tag}>";
    }

    private static string ParagraphPresentationAttribute(ParagraphPresentation? presentation)
    {
        var declarations = ParagraphPresentationDeclarations(presentation);
        return string.IsNullOrEmpty(declarations) ? string.Empty : $" style=\"{declarations}\"";
    }

    private static string ParagraphPresentationDeclarations(ParagraphPresentation? presentation)
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
        if (presentation.Italic is bool italic) declarations.Add($"font-style:{(italic ? "italic" : "normal")}");
        if (presentation.SmallCaps is bool smallCaps) declarations.Add($"font-variant-caps:{(smallCaps ? "small-caps" : "normal")}");
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
        return string.Join(';', declarations);
    }

    internal static string HtmlInlineContent(
        ManuscriptBlock block,
        IReadOnlyDictionary<string, int>? noteNumbers = null,
        string? noteHrefPrefix = null,
        Func<string, FormattedCitationCluster?>? citation = null,
        string? citationHrefPrefix = null) =>
        string.Concat(block.Content.Select(inline => HtmlInline(
            inline,
            noteNumbers,
            noteHrefPrefix,
            citation,
            citationHrefPrefix)));

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
        var captionPresentation = ParagraphPresentationDeclarations(block.ParagraphPresentation);
        var captionStyle = string.IsNullOrEmpty(captionPresentation)
            ? string.Empty
            : $" style=\"{captionPresentation}\"";
        var caption = string.IsNullOrWhiteSpace(content)
            || presentation.CaptionPlacement == FigureCaptionPlacement.Hidden
            ? string.Empty
            : $"<figcaption{captionStyle}>{content}</figcaption>";
        var language = PublicationLanguage.NormalizeOptional(block.Language) is not { } languageTag
            ? string.Empty
            : $" lang=\"{WebUtility.HtmlEncode(languageTag)}\" xml:lang=\"{WebUtility.HtmlEncode(languageTag)}\"";
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
        var overlayCaptionStyle = string.IsNullOrEmpty(captionPresentation)
            ? "display:block"
            : $"display:block;{captionPresentation}";
        var image = $"<div class=\"figure-media\" style=\"position:relative;width:100%;height:{frameHeight};overflow:hidden\"><img src=\"{WebUtility.HtmlEncode(href)}\" alt=\"{WebUtility.HtmlEncode(block.Decorative ? string.Empty : block.AltText)}\"{decorative} style=\"{imageStyle}\" /></div>";
        var overlayCaption = presentation.CaptionPlacement == FigureCaptionPlacement.Overlay
            ? $"<figcaption class=\"figure-overlay-caption\" style=\"position:absolute;left:0;right:0;bottom:0;margin:0;background:rgba(0,0,0,.45);color:white;padding:.2em .4em\"><span style=\"{overlayCaptionStyle}\">{content}</span></figcaption>"
            : string.Empty;
        var contents = presentation.CaptionPlacement == FigureCaptionPlacement.Above
            ? caption + image
            : image + (presentation.CaptionPlacement == FigureCaptionPlacement.Overlay ? overlayCaption : caption);
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

    private static string MarkdownTable(
        ManuscriptBlock block,
        Func<Guid, PublishAssetDocument?> asset,
        Func<string, FormattedCitationCluster?>? citation = null,
        IReadOnlyDictionary<string, int>? noteNumbers = null) =>
        HtmlTable(
            block,
            WebUtility.HtmlEncode($"block-{block.Id}"),
            imageId => asset(imageId) is { } image
                ? $"data:{image.ContentType};base64,{Convert.ToBase64String(image.Data)}"
                : null,
            noteNumbers: noteNumbers, citation: citation);

    private static string HtmlTable(
        ManuscriptBlock block,
        string anchor,
        Func<Guid, string?> imageHref,
        IReadOnlyDictionary<string, int>? noteNumbers = null,
        string? noteHrefPrefix = null,
        Func<string, FormattedCitationCluster?>? citation = null,
        string? citationHrefPrefix = null)
    {
        var table = block.Table
            ?? throw new InvalidOperationException($"Table block {block.Id} has no table payload.");
        var sb = new StringBuilder($"<table id=\"{anchor}\" data-table-id=\"{WebUtility.HtmlEncode(table.Id)}\"><colgroup>");
        var totalWeight = table.ColumnWidthWeights.Sum();
        foreach (var weight in table.ColumnWidthWeights)
            sb.Append("<col style=\"width:").Append((100d * weight / totalWeight).ToString("0.####", CultureInfo.InvariantCulture)).Append("%\" />");
        sb.Append("</colgroup>");
        for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
        {
            sb.Append("<tr data-row-id=\"").Append(WebUtility.HtmlEncode(table.Rows[rowIndex].Id)).Append("\">");
            foreach (var cell in table.Rows[rowIndex].Cells)
            {
                var tag = rowIndex < table.HeaderRowCount ? "th" : "td";
                sb.Append('<').Append(tag).Append(" data-cell-id=\"").Append(WebUtility.HtmlEncode(cell.Id)).Append('"');
                if (cell.RowSpan > 1) sb.Append(" rowspan=\"").Append(cell.RowSpan).Append('"');
                if (cell.ColumnSpan > 1) sb.Append(" colspan=\"").Append(cell.ColumnSpan).Append('"');
                sb.Append('>');
                sb.Append(HtmlBlocks(
                        cell.Content,
                        imageHref,
                        noteNumbers,
                        noteHrefPrefix,
                        citation,
                        citationHrefPrefix));
                sb.Append("</").Append(tag).Append('>');
            }
            sb.Append("</tr>");
        }
        return sb.Append("</table>").ToString();
    }

    private static string MarkdownInline(
        ManuscriptInline inline,
        Func<string, FormattedCitationCluster?>? citation,
        IReadOnlyDictionary<string, int>? noteNumbers)
    {
        if (inline.Type == ManuscriptInlineType.NoteReference)
            return HtmlInline(inline, noteNumbers, null, citation, null);
        if (inline.Type == ManuscriptInlineType.Citation)
        {
            var formatted = citation?.Invoke(inline.Id!);
            return formatted?.NoteNumber is not null
                ? $"<a id=\"citation-ref-{PublicationCitationResolver.Anchor(formatted)}\"></a>[{EscapeMarkdownLiteral(formatted.InlineText)}](#citation-note-{PublicationCitationResolver.Anchor(formatted)})"
                : formatted is null ? "[citation]" : CitationMarkdown(formatted.InlineRuns);
        }
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
                ManuscriptMarkType.Language => $"<span lang=\"{WebUtility.HtmlEncode(PublicationLanguage.Normalize(mark.Value))}\">{text}</span>",
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

    private static string HtmlInline(
        ManuscriptInline inline,
        IReadOnlyDictionary<string, int>? noteNumbers,
        string? noteHrefPrefix,
        Func<string, FormattedCitationCluster?>? citation,
        string? citationHrefPrefix)
    {
        if (inline.Type == ManuscriptInlineType.NoteReference)
        {
            var id = WebUtility.HtmlEncode(inline.NoteId);
            var number = noteNumbers?.GetValueOrDefault(inline.NoteId!) ?? 0;
            var href = $"{noteHrefPrefix}#note-{id}";
            return $"<sup class=\"note-reference\"><a id=\"note-ref-{id}\" href=\"{WebUtility.HtmlEncode(href)}\" role=\"doc-noteref\">{(number == 0 ? "*" : number.ToString(CultureInfo.InvariantCulture))}</a></sup>";
        }
        if (inline.Type == ManuscriptInlineType.Citation)
        {
            var formatted = citation?.Invoke(inline.Id!);
            var citationText = formatted is null ? "[citation]" : CitationHtml(formatted.InlineRuns);
            if (formatted?.NoteNumber is not null)
            {
                var anchor = PublicationCitationResolver.Anchor(formatted);
                var href = $"{citationHrefPrefix}#citation-note-{anchor}";
                return $"<sup class=\"citation-reference\"><a id=\"citation-ref-{anchor}\" href=\"{WebUtility.HtmlEncode(href)}\" role=\"doc-noteref\">{citationText}</a></sup>";
            }
            return formatted is not null
                ? $"<cite class=\"citation-reference\">{citationText}</cite>"
                : "<cite class=\"citation-reference citation-reference--missing\">[citation]</cite>";
        }
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
                ManuscriptMarkType.Language => $"<span lang=\"{WebUtility.HtmlEncode(PublicationLanguage.Normalize(mark.Value))}\">{text}</span>",
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

    internal static IReadOnlyDictionary<string, int> NoteNumbers(ManuscriptDocument manuscript) =>
        ManuscriptTraversal.NumberNotes(manuscript)
            .ToDictionary(item => item.NoteId, item => item.Number, StringComparer.Ordinal);

    internal static IReadOnlySet<string> ReferencedNoteIds(IEnumerable<ManuscriptBlock> blocks)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        Add(blocks);
        return ids;

        void Add(IEnumerable<ManuscriptBlock> items)
        {
            foreach (var block in items)
            {
                foreach (var inline in block.Content.Where(item => item.Type == ManuscriptInlineType.NoteReference))
                    ids.Add(inline.NoteId!);
                if (block.Table is not { } table)
                    continue;
                foreach (var row in table.Rows)
                foreach (var cell in row.Cells)
                    Add(cell.Content);
            }
        }
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

internal sealed class PublicationCitationResolver
{
    private readonly IReadOnlyDictionary<string, FormattedCitationCluster> _byOccurrence;
    private readonly Dictionary<string, string> _backlinks = new(StringComparer.Ordinal);

    public PublicationCitationResolver(IEnumerable<FormattedCitationCluster> occurrences)
    {
        _byOccurrence = occurrences.ToDictionary(item => Key(item.Identity.TopLevelContainer,
            item.Identity.PlacementPath, item.Identity.CitationAtomId), StringComparer.Ordinal);
    }

    public Func<string, FormattedCitationCluster?> ForDocument(string topLevel,
        IReadOnlyList<string>? placementPath = null, string? backlink = null) =>
        atomId => Resolve(atomId, topLevel, placementPath ?? [], backlink);

    public FormattedCitationCluster Resolve(string citationAtomId, string topLevel,
        IReadOnlyList<string> placementPath, string? backlink = null)
    {
        if (!_byOccurrence.TryGetValue(Key(topLevel, placementPath, citationAtomId), out var formatted))
            throw new InvalidDataException($"Citation '{citationAtomId}' has no effective publication occurrence in '{topLevel}'.");
        if (!string.IsNullOrWhiteSpace(backlink))
            _backlinks[Anchor(formatted)] = backlink;
        return formatted;
    }

    private static string Key(string topLevel, IReadOnlyList<string> path, string atom) =>
        System.Text.Json.JsonSerializer.Serialize(new { topLevel, placements = path.Where(item => item.StartsWith("placement:", StringComparison.Ordinal)), atom });

    public string? BacklinkFor(FormattedCitationCluster formatted) =>
        _backlinks.GetValueOrDefault(Anchor(formatted));

    public static string Anchor(FormattedCitationCluster formatted)
    {
        var identity = formatted.Identity;
        var key = string.Join('|',
            identity.PublicationTarget,
            identity.TopLevelContainer,
            string.Join('/', identity.PlacementPath),
            identity.CitationAtomId);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..20].ToLowerInvariant();
    }
}
