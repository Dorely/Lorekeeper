using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using System.IO.Compression;
using System.Xml.Linq;
using Lorekeeper.Citations;
using Lorekeeper.Composition;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Publish;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Lorekeeper.Tests;

public sealed class DocxPublishFormatterTests
{
    [Fact]
    public async Task List_numbering_nesting_and_restarts_survive_semantic_and_Word_output()
    {
        ManuscriptBlock Item(string id, int level, int? start = null, bool ordered = true) => new()
        {
            Id = id, Type = ManuscriptBlockType.ListItem, StyleRole = ManuscriptStyleRoles.ListItem,
            List = new() { Id = ordered ? "sequence" : "bullets", Ordered = ordered, Level = level, Start = start },
            Content = [new() { Text = id }],
        };
        var manuscript = new ManuscriptDocument
        {
            ManuscriptId = Guid.NewGuid(),
            Content = [Item("first", 0), Item("nested-first", 1), Item("nested-second", 1),
                Item("second", 0), Item("restarted", 0, 7), Item("bullet", 1, ordered: false)],
        };
        var roundTrip = ManuscriptCodec.Deserialize(ManuscriptCodec.Serialize(manuscript));
        var numbered = ManuscriptLists.Resolve(roundTrip);
        Assert.Equal(new int?[] { 1, 1, 2, 2, 7, null }, numbered.Content.Select(block => block.List!.Start));
        Assert.Null(manuscript.Content[0].List!.Start);
        var markdown = SemanticPublishFormatting.Markdown(manuscript, _ => null);
        Assert.Contains(@"    2. nested\-second", markdown);
        Assert.Contains("7. restarted", markdown);
        Assert.Contains("    - bullet", markdown);
        var html = SemanticPublishFormatting.Html(manuscript, _ => null);
        Assert.Contains("value=\"7\"", html);
        Assert.Contains("aria-level=\"2\"", html);
        var htmlTree = XDocument.Parse("<main>" + html + "</main>");
        var nested = htmlTree.Descendants("li").Single(item => item.Attribute("id")?.Value == "block-nested-first");
        Assert.Equal("block-first", nested.Parent!.Parent!.Attribute("id")!.Value);
        var publication = new PublishDocument(Guid.NewGuid(), Guid.Empty, "Lists", "lists", DateTime.UnixEpoch,
            new("", "", "", "en", "", "", "", "", false, false, false, false, true, true, false, false, false, 6, 9, .75, 11, 1.2),
            null, [new(null, "Chapters", "", true, false, false, 0,
                [new(manuscript.ManuscriptId, null, "Lists", "", "", 0, true, manuscript, [])])], []);
        using var stream = new MemoryStream(await new DocxPublishFormatter(new UnexpectedPreviewService()).RenderAsync(publication));
        using var package = WordprocessingDocument.Open(stream, false);
        Assert.Empty(new OpenXmlValidator().Validate(package));
        var definitions = package.MainDocumentPart!.NumberingDefinitionsPart!.Numbering!;
        Assert.Contains(definitions.Descendants<W.NumberingFormat>(), format => format.Val!.Value == W.NumberFormatValues.Decimal);
        Assert.Contains(definitions.Descendants<W.NumberingFormat>(), format => format.Val!.Value == W.NumberFormatValues.Bullet);
        Assert.Contains(definitions.Descendants<W.StartOverrideNumberingValue>(), start => start.Val!.Value == 7);
        var paragraphs = package.MainDocumentPart.Document!.Descendants<W.Paragraph>()
            .Where(paragraph => paragraph.ParagraphProperties?.NumberingProperties is not null).ToList();
        Assert.Equal(6, paragraphs.Count);
        Assert.Equal([0, 1, 1, 0, 0, 1], paragraphs.Select(paragraph => paragraph.ParagraphProperties!.NumberingProperties!.NumberingLevelReference!.Val!.Value));
        var split = ManuscriptOperations.Apply(manuscript, [new SplitManuscriptBlock("restarted", 3)]).Document;
        Assert.Null(split.Content[5].List!.Start);
        Assert.Equal(8, ManuscriptLists.Resolve(split).Content[5].List!.Start);
        var high = manuscript with { Content = [Item("high", 0, 1_000_000), Item("next", 0)] };
        var projected = ManuscriptLists.Resolve(ManuscriptCodec.Deserialize(ManuscriptCodec.Serialize(high)));
        Assert.Contains("1000001. next", SemanticPublishFormatting.Markdown(projected, _ => null));
        Assert.Throws<InvalidDataException>(() => ManuscriptCodec.Serialize(manuscript with
        { Content = [manuscript.Content[0] with { List = manuscript.Content[0].List! with { Level = 9 } }] }));
    }

    [Fact]
    public async Task Export_preserves_editable_rich_content_notes_citations_and_merged_cells()
    {
        var recordId = Guid.Parse("90000000-0000-0000-0000-000000000001");
        var chapterId = Guid.NewGuid();
        var record = new CitationRecord(
            recordId,
            BibliographicRecordKind.Book,
            "Maps of Quiet Water",
            string.Empty,
            [new CitationPerson("Nguyen", "Linh")],
            [], [], new(2024), new(), string.Empty, "North Window Press", "Portland",
            string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        var citationAtomId = "citation-a";
        var citation = new CitationClusterOccurrence(
            new("core", $"chapter:{chapterId:D}", [], citationAtomId),
            new ManuscriptCitationCluster
            {
                Items = [new ManuscriptCitationItem { BibliographicRecordId = recordId, LocatorLabel = "page", LocatorValue = "42" }],
            });
        var formatted = new CitationFormatter().Format(CitationStyle.Chicago18NotesBibliography, [record], [citation]);
        var manuscript = new ManuscriptDocument
        {
            ManuscriptId = Guid.Parse("91000000-0000-0000-0000-000000000001"),
            Revision = 4,
            Content =
            [
                new ManuscriptBlock
                {
                    Id = "heading-a",
                    Type = ManuscriptBlockType.Heading,
                    StyleRole = ManuscriptStyleRoles.Heading,
                    HeadingLevel = 6,
                    Content = [new ManuscriptInline { Id = "text-heading", Text = "Editable chapter content" }],
                },
                new ManuscriptBlock
                {
                    Id = "paragraph-a",
                    Type = ManuscriptBlockType.Paragraph,
                    StyleRole = ManuscriptStyleRoles.Body,
                    Content =
                    [
                        new ManuscriptInline { Id = "text-a", Text = "Read the source" },
                        new ManuscriptInline { Id = citationAtomId, Type = ManuscriptInlineType.Citation, Citation = citation.Cluster },
                        new ManuscriptInline { Id = "footnote-ref", Type = ManuscriptInlineType.NoteReference, NoteId = "footnote-a" },
                        new ManuscriptInline { Id = "endnote-ref", Type = ManuscriptInlineType.NoteReference, NoteId = "endnote-a" },
                    ],
                },
                new ManuscriptBlock
                {
                    Id = "table-block-a",
                    Type = ManuscriptBlockType.Table,
                    StyleRole = ManuscriptStyleRoles.Body,
                    Table = new ManuscriptTable
                    {
                        Id = "table-a",
                        ColumnWidthWeights = [2, 1],
                        Rows =
                        [
                            new ManuscriptTableRow
                            {
                                Id = "row-a",
                                Cells =
                                [
                                    Cell("cell-a", "Merged", rowSpan: 2),
                                    Cell("cell-b", "First row"),
                                ],
                            },
                            new ManuscriptTableRow
                            {
                                Id = "row-b",
                                Cells = [Cell("cell-c", "Second row")],
                            },
                        ],
                    },
                },
            ],
            Notes =
            [
                Note("footnote-a", ManuscriptNoteKind.Footnote, "Editable footnote"),
                Note("endnote-a", ManuscriptNoteKind.Endnote, "Editable endnote"),
            ],
        };
        var document = new PublishDocument(
            Guid.Parse("92000000-0000-0000-0000-000000000001"),
            Guid.Parse("92000000-0000-0000-0000-000000000002"),
            "Book", "book", DateTime.UnixEpoch,
            new PublishDocumentProfile(
                string.Empty, string.Empty, "Linh Nguyen", "en", "North Window Press", string.Empty,
                string.Empty, string.Empty, false, false, false, false, true, true, false, false,
                false, 6, 9, 0.75, 11, 1.2),
            null,
            [new PublishSectionDocument(null, "Unassigned", string.Empty, true, false, false, 0,
                [new PublishChapterDocument(chapterId, null, "Chapter One", string.Empty, string.Empty, 0, true, manuscript, [])])],
            [])
        {
            CitationStyle = CitationStyle.Chicago18NotesBibliography,
            BibliographicRecords = [record],
            Citations = formatted,
        };

        var bytes = await new DocxPublishFormatter(new UnexpectedPreviewService()).RenderAsync(document);
        var imported = await new Lorekeeper.Manuscripts.Import.SemanticImportService().ReadDocxAsync(bytes);
        var importedRecord = Assert.Single(imported.Resources.Bibliography);
        Assert.NotEqual(record.Id, importedRecord.Id);
        Assert.Equal(record.Title, importedRecord.Title);
        var importedCitation = Assert.Single(ManuscriptTraversal.EnumerateCitations(imported.Document));
        Assert.Equal(importedRecord.Id, importedCitation.Cluster.Items[0].BibliographicRecordId);
        Assert.Equal("42", importedCitation.Cluster.Items[0].LocatorValue);
        Assert.Contains(imported.Document.Notes, note => ManuscriptCodec.Text(note.Content[0]).Contains("Editable footnote", StringComparison.Ordinal));
        Assert.Contains(imported.Document.Notes, note => note.Kind == ManuscriptNoteKind.Endnote
            && ManuscriptCodec.Text(note.Content[0]).Contains("Editable endnote", StringComparison.Ordinal));
        Assert.DoesNotContain(ManuscriptTraversal.EnumerateBlocks(imported.Document with { Notes = [] }), block => ManuscriptCodec.Text(block).Contains("Editable endnote", StringComparison.Ordinal));

        // HTML table cells in Markdown use the same citation destinations as prose.
        var tableManuscript = ManuscriptClone.Document(manuscript);
        var citedParagraph = tableManuscript.Content[1];
        tableManuscript.Content[2].Table!.Rows[0].Cells[0].Content.Add(citedParagraph);
        tableManuscript.Content.RemoveAt(1);
        var tableChapter = document.Sections[0].Chapters[0] with { Manuscript = tableManuscript };
        var tablePublication = document with { Sections = [document.Sections[0] with { Chapters = [tableChapter] }] };
        tablePublication = tablePublication with
        {
            Citations = new CitationFormatter().Format(document.CitationStyle, [record], PublicationCitationTraversal.Enumerate(tablePublication)),
        };
        var tableMarkdown = System.Text.Encoding.UTF8.GetString(new MarkdownPublishFormatter().Render(tablePublication));
        var citationAnchor = PublicationCitationResolver.Anchor(Assert.Single(tablePublication.Citations.Occurrences));
        Assert.Contains($"href=\"#citation-note-{citationAnchor}\"", tableMarkdown);
        Assert.Contains($"id=\"citation-note-{citationAnchor}\"", tableMarkdown);

        using var stream = new MemoryStream(bytes, writable: false);
        using var package = WordprocessingDocument.Open(stream, false);
        Assert.Empty(new OpenXmlValidator().Validate(package));
        var main = Assert.IsType<MainDocumentPart>(package.MainDocumentPart);
        var documentRoot = Assert.IsType<W.Document>(main.Document);
        var bodyText = Assert.IsType<W.Body>(documentRoot.Body).InnerText;
        Assert.Contains("Editable chapter content", bodyText);
        Assert.Contains("Merged", bodyText);
        Assert.Contains("Editable endnote", bodyText);
        Assert.Contains("Bibliography", bodyText);
        Assert.Contains("Maps of Quiet Water", bodyText);
        var footnotes = Assert.IsType<FootnotesPart>(main.FootnotesPart).Footnotes;
        Assert.Contains("Editable footnote", Assert.IsType<W.Footnotes>(footnotes).InnerText);
        Assert.DoesNotContain("Linh Nguyen", footnotes!.InnerText);
        Assert.Contains("Linh Nguyen", bodyText);
        var citationLink = Assert.Single(documentRoot.Descendants<W.Hyperlink>(),
            link => link.Anchor?.Value?.StartsWith("citation_", StringComparison.Ordinal) == true
                && !link.Anchor.Value.StartsWith("citation_ref_", StringComparison.Ordinal));
        Assert.Equal("1", citationLink.InnerText);
        Assert.Contains(documentRoot.Descendants<W.BookmarkStart>(), bookmark => bookmark.Name == citationLink.Anchor);
        Assert.True(bodyText.IndexOf("Endnotes", StringComparison.Ordinal) < bodyText.IndexOf("Bibliography", StringComparison.Ordinal));
        Assert.Contains(documentRoot.Descendants<W.Run>(), run => run.InnerText == "Maps of Quiet Water"
            && run.RunProperties?.Italic?.Val?.Value == true);
        var merges = documentRoot.Descendants<W.VerticalMerge>().ToList();
        Assert.Contains(merges, merge => merge.Val?.Value == W.MergedCellValues.Restart);
        Assert.Contains(merges, merge => merge.Val?.Value == W.MergedCellValues.Continue);
        Assert.Contains(documentRoot.Descendants<W.ParagraphStyleId>(), style => style.Val?.Value == "Heading6");

        var firstChapter = document.Sections[0].Chapters[0];
        var imageId = Guid.NewGuid();
        using var bitmap = new SkiaSharp.SKBitmap(80, 40);
        bitmap.Erase(SkiaSharp.SKColors.White);
        using var imageData = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        var secondManuscript = manuscript with
        {
            ManuscriptId = Guid.NewGuid(),
            Content = manuscript.Content.Select(block => block with
            {
                Content = block.Content.Where(inline => inline.Type != ManuscriptInlineType.Citation).ToList(),
            }).ToList(),
            Notes = manuscript.Notes.Select(note => note with
            {
                Content = [.. note.Content, new()
                {
                    Id = note.Id + "-figure", Type = ManuscriptBlockType.Figure, ImageId = imageId,
                    AltText = "A wide image retained inside a note", Content = [new() { Text = "Note image caption",
                        Marks = [new() { Type = ManuscriptMarkType.Link, Value = "https://example.invalid/note" }] }],
                }],
            }).ToList(),
        };
        var duplicateTitles = document with
        {
            Sections = [document.Sections[0] with
            {
                Chapters = [firstChapter, firstChapter with { Id = Guid.NewGuid(), Manuscript = secondManuscript }],
            }],
            Assets = [new(imageId, "wide.png", "image/png", imageData.ToArray(), "Wide image")],
        };
        var duplicateBytes = await new DocxPublishFormatter(new UnexpectedPreviewService()).RenderAsync(duplicateTitles);
        using var duplicateStream = new MemoryStream(duplicateBytes, writable: false);
        using var duplicatePackage = WordprocessingDocument.Open(duplicateStream, false);
        Assert.Empty(new OpenXmlValidator().Validate(duplicatePackage));
        var duplicateRoot = Assert.IsType<W.Document>(duplicatePackage.MainDocumentPart!.Document);
        var bookmarks = duplicateRoot.Descendants<W.BookmarkStart>().ToList();
        Assert.Equal(4, bookmarks.Count);
        Assert.Equal(4, bookmarks.Select(bookmark => bookmark.Name!.Value).Distinct().Count());
        Assert.Equal(4, bookmarks.Select(bookmark => bookmark.Id!.Value).Distinct().Count());
        Assert.All(bookmarks, bookmark => Assert.True(bookmark.Name!.Value!.Length <= 40));
        var anchors = duplicateRoot.Descendants<W.Hyperlink>()
            .Where(link => link.Anchor is not null).Select(link => link.Anchor!.Value).ToList();
        Assert.All(bookmarks, bookmark => Assert.Contains(bookmark.Name!.Value, anchors));
        var extent = Assert.Single(duplicateRoot.Descendants<DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent>());
        Assert.Equal(extent.Cx!.Value, extent.Cy!.Value * 2);
        Assert.True(extent.Cx.Value <= 4_114_800, "The image must fit the 4.5-inch text area.");
        Assert.All(duplicateRoot.Descendants<W.PageSize>(), size =>
        {
            Assert.Equal(8640U, size.Width!.Value);
            Assert.Equal(12960U, size.Height!.Value);
        });
        Assert.NotEmpty(duplicateRoot.Descendants<W.PageSize>());
        Assert.Contains("Note image caption", duplicateRoot.InnerText);
        var footnotePart = duplicatePackage.MainDocumentPart.FootnotesPart!;
        var footnoteImage = Assert.Single(footnotePart.Footnotes!.Descendants<DocumentFormat.OpenXml.Drawing.Blip>());
        Assert.IsType<ImagePart>(footnotePart.GetPartById(footnoteImage.Embed!.Value!));
        var footnoteLink = Assert.Single(footnotePart.Footnotes.Descendants<W.Hyperlink>(), link => link.Id is not null);
        Assert.Equal("https://example.invalid/note", footnotePart.HyperlinkRelationships.Single(relationship => relationship.Id == footnoteLink.Id!.Value).Uri.AbsoluteUri);
        Assert.All(footnotePart.Footnotes.Elements<W.Footnote>().Where(note => note.Id!.Value > 0), note =>
            Assert.NotEmpty(note.Elements<W.Paragraph>().First().InnerText));

        using var epubStream = new MemoryStream(new EpubPublishFormatter().Render(duplicateTitles));
        using var epub = new ZipArchive(epubStream, ZipArchiveMode.Read);
        var xhtml = epub.Entries.Where(entry => entry.FullName.EndsWith(".xhtml", StringComparison.Ordinal))
            .ToDictionary(entry => entry.FullName, entry =>
            {
                using var input = entry.Open();
                return XDocument.Load(input);
            });
        var endnotes = Assert.Single(xhtml, item => item.Key.EndsWith("/endnotes.xhtml", StringComparison.Ordinal));
        Assert.Contains("Editable endnote", endnotes.Value.ToString());
        Assert.Contains("Editable footnote", endnotes.Value.ToString());
        Assert.Contains("Linh Nguyen", endnotes.Value.ToString());
        foreach (var (path, xml) in xhtml)
        {
            var ids = xml.Descendants().Attributes("id").Select(attribute => attribute.Value).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
            foreach (var link in xml.Descendants().Attributes("href").Where(attribute => attribute.Value.Contains('#')))
            {
                var target = new Uri(new Uri("https://epub.invalid/" + path), link.Value);
                if (target.Host != "epub.invalid") continue;
                var targetFile = xhtml[target.AbsolutePath.TrimStart('/')];
                Assert.Contains(targetFile.Descendants().Attributes("id"), attribute => attribute.Value == target.Fragment[1..]);
            }
        }
        Assert.Contains(epub.Entries, entry => entry.FullName.EndsWith($"/images/{imageId:N}.png", StringComparison.Ordinal));

        var styledManuscript = manuscript with
        {
            Content = manuscript.Content.Select(block => block.Id != "paragraph-a" ? block : block with
            {
                ParagraphPresentation = new() { FontSizePoints = 13, Italic = false, LineHeight = 1.5, FirstLineIndentEm = -1, KeepWithNext = false },
                Content = block.Content.Select(inline => inline.Id != "text-a" ? inline : inline with
                {
                    Marks = [new() { Type = ManuscriptMarkType.CharacterStyle, Value = "citation-intro" }, new() { Type = ManuscriptMarkType.Language, Value = "fr" }],
                }).ToList(),
            }).ToList(),
        };
        var styledDocument = document with
        {
            NamedStyles = [
                new("Narrative", ManuscriptStyleKind.Paragraph, ManuscriptStyleRoles.Body,
                    new(FontFamilyKey: "sans", FontSizePoints: 12, Italic: true, TextAlign: "justify", KeepWithNext: true)),
                new("Citation introduction", ManuscriptStyleKind.Character, "citation-intro", new(FontWeight: 700)),
            ],
            Sections = [document.Sections[0] with { Chapters = [firstChapter with { Manuscript = styledManuscript }] }],
        };
        using var styledStream = new MemoryStream(await new DocxPublishFormatter(new UnexpectedPreviewService()).RenderAsync(styledDocument));
        using var styledPackage = WordprocessingDocument.Open(styledStream, false);
        Assert.Empty(new OpenXmlValidator().Validate(styledPackage));
        var styleDefinitions = styledPackage.MainDocumentPart!.StyleDefinitionsPart!.Styles!;
        var narrativeStyle = Assert.Single(styleDefinitions.Elements<W.Style>(), style => style.StyleName?.Val == "Narrative");
        Assert.Equal("Arial", narrativeStyle.StyleRunProperties!.RunFonts!.Ascii!.Value);
        Assert.Equal(W.JustificationValues.Both, narrativeStyle.StyleParagraphProperties!.Justification!.Val!.Value);
        var intro = Assert.Single(styledPackage.MainDocumentPart.Document!.Descendants<W.Run>(), run => run.InnerText == "Read the source");
        Assert.Equal("26", intro.RunProperties!.FontSize!.Val!.Value);
        Assert.False(intro.RunProperties.Italic!.Val!.Value);
        Assert.Equal("fr", intro.RunProperties.Languages!.Val!.Value);
        Assert.NotNull(intro.RunProperties.RunStyle);
        var styledParagraph = (W.Paragraph)intro.Parent!;
        Assert.Equal(narrativeStyle.StyleId!.Value, styledParagraph.ParagraphProperties!.ParagraphStyleId!.Val!.Value);
        Assert.Null(styledParagraph.ParagraphProperties.Justification);
        Assert.Equal("260", styledParagraph.ParagraphProperties.Indentation!.Hanging!.Value);
        Assert.Equal("360", styledParagraph.ParagraphProperties.SpacingBetweenLines!.Line!.Value);
        Assert.False(styledParagraph.ParagraphProperties.KeepNext!.Val!.Value);
    }

    private static ManuscriptTableCell Cell(string id, string text, int rowSpan = 1) => new()
    {
        Id = id,
        RowSpan = rowSpan,
        Content =
        [
            new ManuscriptBlock
            {
                Id = $"{id}-paragraph",
                Type = ManuscriptBlockType.Paragraph,
                StyleRole = ManuscriptStyleRoles.Body,
                Content = [new ManuscriptInline { Id = $"{id}-text", Text = text }],
            },
        ],
    };

    private static ManuscriptNote Note(string id, ManuscriptNoteKind kind, string text) => new()
    {
        Id = id,
        Kind = kind,
        Content =
        [
            new ManuscriptBlock
            {
                Id = $"{id}-paragraph",
                Type = ManuscriptBlockType.Paragraph,
                StyleRole = ManuscriptStyleRoles.Body,
                Content = [new ManuscriptInline { Id = $"{id}-text", Text = text }],
            },
        ],
    };

    internal sealed class UnexpectedPreviewService(Action<CompositionScene, ManuscriptDocument>? inspectArtwork = null) : ICompositionCanvasPreviewService
    {
        public Task<CompositionCanvasPreviewResult> RenderAsync(
            Guid projectId, Guid contentId, Guid variantId, CompositionCanvasPreviewMode mode,
            CancellationToken cancellationToken = default) => throw Unexpected();

        public Task<CompositionCanvasPreviewResult> RenderSceneAsync(
            Guid projectId, Guid targetId, long revision, CompositionScene scene,
            CompositionCanvasPreviewMode mode, CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? textBindings = null) => throw Unexpected();

        public Task<CompositionCanvasPreviewResult> RenderSceneAsync(
            Guid projectId, Guid targetId, long revision, CompositionScene scene, ManuscriptDocument semantic,
            CompositionCanvasPreviewMode mode, CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? textBindings = null) => throw Unexpected();

        public Task<CompositionCanvasPreviewResult> RenderSceneAtResolutionAsync(
            Guid projectId, Guid targetId, long revision, CompositionScene scene, ManuscriptDocument semantic,
            CompositionCanvasPreviewMode mode, int maximumEdge, CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? textBindings = null, string? backgroundColor = null)
        {
            if (inspectArtwork is null) throw Unexpected();
            Assert.Equal(CompositionCanvasPreviewMode.Clean, mode);
            inspectArtwork(scene, semantic);
            using var bitmap = new SkiaSharp.SKBitmap(80, 100);
            bitmap.Erase(SkiaSharp.SKColors.White);
            using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            return Task.FromResult(new CompositionCanvasPreviewResult(targetId, Guid.Empty, revision, 0, mode,
                scene.Surface.WidthPoints, scene.Surface.HeightPoints, 80, 100, scene.Objects.Count, 0, data.ToArray(), []));
        }

        private static InvalidOperationException Unexpected() =>
            new("The no-Designed-Page DOCX fixture must not request artwork.");
    }
}
