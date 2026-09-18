using Lorekeeper.Citations;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Publish;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace Lorekeeper.Tests;

public sealed class CitationOccurrenceTests
{
    [Fact]
    public void Designed_page_ranges_preserve_atoms_once_and_report_unplaced_references()
    {
        ManuscriptInline Reference(string id) => new()
        {
            Id = id, Type = ManuscriptInlineType.NoteReference, NoteId = id + "-note",
        };
        var document = new ManuscriptDocument
        {
            Content = [new() { Id = "body", Content =
                [Reference("leading"), new() { Text = "ab" }, Reference("boundary"),
                    new() { Text = "cd" }, Reference("trailing")] },
                new() { Id = "atoms", Content = [Reference("only")] }],
        };
        var first = new ManuscriptRangeReference("body", 0, 2);
        var second = new ManuscriptRangeReference("body", 2, 4);
        var atoms = new ManuscriptRangeReference("atoms");
        var resolved = ManuscriptRangeResolver.ResolveBlocks(document, [first, second, atoms]);
        Assert.Equal(["leading"], resolved[0].Content.Where(inline => inline.Type != ManuscriptInlineType.Text).Select(inline => inline.Id));
        Assert.Equal(["boundary", "trailing"], resolved[1].Content.Where(inline => inline.Type != ManuscriptInlineType.Text).Select(inline => inline.Id));
        Assert.Equal("only", Assert.Single(resolved[2].Content).Id);
        Assert.Empty(ManuscriptRangeResolver.ValidateCoverage(document, [[first, second, atoms]]));
        Assert.Equal(["atoms"], ManuscriptRangeResolver.ValidateCoverage(document, [[first, second]]));
        Assert.Throws<InvalidDataException>(() => ManuscriptRangeResolver.ValidateCoverage(document, [[atoms, atoms]]));
    }

    [Fact]
    public async Task Repeated_pages_and_note_citations_resolve_by_identity_in_any_render_order()
    {
        var record = new CitationRecord(Guid.NewGuid(), BibliographicRecordKind.Book, "Shared source", "",
            [new("River", "Ada")], [], [], new(2024), new(), "", "Press", "", "", "", "", "", "", "", "", "");
        ManuscriptInline Citation(string id) => new()
        {
            Id = id, Type = ManuscriptInlineType.Citation,
            Citation = new() { Items = [new() { BibliographicRecordId = record.Id }] },
        };
        var pageId = Guid.NewGuid();
        var layerId = Guid.NewGuid();
        var scene = new CompositionScene
        {
            Layers = [new(layerId, "Text", 0)],
            Objects = [new() { Id = Guid.NewGuid(), LayerId = layerId, Kind = CompositionObjectKind.Text,
                SemanticRole = CompositionSemanticRole.Paragraph, ReadingOrder = 0, ContentReferences = [new("body")] }],
        };
        var page = new PublishDesignedPageDocument(pageId, "Repeated page", new()
        {
            ManuscriptId = Guid.NewGuid(),
            Content = [new() { Id = "body", Content =
                [new() { Id = "reference", Type = ManuscriptInlineType.NoteReference, NoteId = "note" }, Citation("body-citation")] }],
            Notes = [new() { Id = "note", Content = [new() { Id = "note-body", Content = [Citation("note-citation")] }] }],
        }, "Shared page description", 0, [new(Guid.NewGuid(), "fixture", scene, 0)]);
        PublishChapterDocument Chapter()
        {
            var id = Guid.NewGuid();
            return new(id, null, "Duplicate title", "", "", 0, true, new()
            {
                ManuscriptId = id,
                Content = [new() { Id = "first", Type = ManuscriptBlockType.DesignedPage, DesignedPageId = pageId },
                    new() { Id = "second", Type = ManuscriptBlockType.DesignedPage, DesignedPageId = pageId }],
            }, [page]);
        }
        var first = Chapter();
        var second = Chapter();
        var publication = new PublishDocument(Guid.NewGuid(), Guid.Empty, "Book", "book", DateTime.UnixEpoch,
            new("", "", "", "en", "", "", "", "", false, false, false, false, true, true, false, false, false, 6, 9, .75, 11, 1.2),
            null, [new(null, "Chapters", "", true, false, false, 0, [first, second])], []);
        var occurrences = PublicationCitationTraversal.Enumerate(publication);
        var formatted = new CitationFormatter().Format(CitationStyle.Chicago18NotesBibliography, [record], occurrences);
        var resolver = new PublicationCitationResolver(formatted.Occurrences);

        Assert.Equal(8, occurrences.Count);
        Assert.Equal([1, 2, 3, 4, 1, 2, 3, 4], formatted.Occurrences.Select(item => item.NoteNumber));
        Assert.Equal("note-citation", occurrences[0].Identity.CitationAtomId);
        Assert.Contains("note:note", occurrences[0].Identity.PlacementPath);
        Assert.Equal(8, formatted.Occurrences.Select(PublicationCitationResolver.Anchor).Distinct().Count());
        Assert.Single(formatted.BibliographyEntries);
        var secondPlacement = resolver.Resolve("note-citation", $"chapter:{first.Id:D}", ["placement:second"], "second.xhtml");
        var firstPlacement = resolver.Resolve("note-citation", $"chapter:{first.Id:D}", ["placement:first"], "first.xhtml");
        Assert.Equal(3, secondPlacement.NoteNumber);
        Assert.Equal(1, firstPlacement.NoteNumber);
        Assert.Same(secondPlacement, resolver.Resolve("note-citation", $"chapter:{first.Id:D}", ["placement:second"]));
        Assert.Equal("second.xhtml", resolver.BacklinkFor(secondPlacement));
        Assert.Equal("first.xhtml", resolver.BacklinkFor(firstPlacement));
        Assert.Throws<InvalidDataException>(() => resolver.Resolve("note-citation", $"chapter:{first.Id:D}", []));
        publication = publication with { Citations = formatted };
        var noteProjections = PublicationSemanticDocuments.Enumerate(publication)
            .Select(container => PublicationNotes.Create(publication, container)).ToList();
        Assert.Equal([1, 2, 1, 2], noteProjections.SelectMany(projection => projection.Occurrences).Select(note => note.Number));
        var noteIds = noteProjections.SelectMany(projection => projection.Occurrences).Select(note => note.Note.Id).ToList();
        Assert.Equal(4, noteIds.Distinct().Count());
        Assert.Equal("note", page.SemanticManuscript.Notes[0].Id);
        foreach (var projection in noteProjections)
        foreach (var placement in projection.Placements.Values)
        {
            var reference = placement.Content[0].Content[0];
            Assert.Equal(placement.Notes[0].Id, reference.NoteId);
            Assert.Contains(reference.NoteId!, projection.Numbers.Keys);
        }
        var markdownNotes = PublicationTextBackMatter.Notes(publication, markdown: true);
        Assert.StartsWith("## Endnotes", markdownNotes);
        Assert.All(noteIds, id => Assert.Contains($"id=\"note-{id}\"", markdownNotes));
        Assert.Equal(1, markdownNotes.Split("## Endnotes", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain($"chapter:{first.Id:D}", markdownNotes);
        Assert.Contains("Duplicate title", markdownNotes);
        Assert.All(formatted.Occurrences, citation => Assert.Contains($"citation-note-{PublicationCitationResolver.Anchor(citation)}", markdownNotes));
        var markdown = Encoding.UTF8.GetString(new MarkdownPublishFormatter().Render(publication));
        Assert.All(noteIds, id =>
        {
            Assert.Contains($"href=\"#note-{id}\"", markdown);
            Assert.Contains($"id=\"note-{id}\"", markdown);
            Assert.Contains($"[↩](#note-ref-{id})", markdown);
        });
        Assert.DoesNotContain("[^", markdown);
        foreach (var projection in noteProjections)
        foreach (var occurrence in projection.Occurrences)
            Assert.Contains($"href=\"#note-{occurrence.Note.Id}\" role=\"doc-noteref\">{occurrence.Number}</a>", markdown);
        var plain = Encoding.UTF8.GetString(new PlainTextPublishFormatter().Render(publication));
        Assert.True(plain.IndexOf("Endnotes", StringComparison.Ordinal) < plain.IndexOf("Bibliography", StringComparison.Ordinal));
        using var epubStream = new MemoryStream(new EpubPublishFormatter().Render(publication));
        using var epub = new ZipArchive(epubStream, ZipArchiveMode.Read);
        var files = epub.Entries.Where(entry => entry.FullName.EndsWith(".xhtml", StringComparison.Ordinal)).ToDictionary(entry => entry.FullName, entry =>
        {
            using var input = entry.Open();
            return XDocument.Load(input);
        });
        foreach (var (path, xml) in files)
        {
            var ids = xml.Descendants().Attributes("id").Select(attribute => attribute.Value).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
            foreach (var link in xml.Descendants().Attributes("href").Where(attribute => attribute.Value.Contains('#')))
            {
                var target = new Uri(new Uri("https://epub.invalid/" + path), link.Value);
                Assert.Contains(files[target.AbsolutePath.TrimStart('/')].Descendants().Attributes("id"),
                    attribute => attribute.Value == target.Fragment[1..]);
            }
        }
        var artworkText = new List<string>();
        var previews = new DocxPublishFormatterTests.UnexpectedPreviewService((artworkScene, semantic) =>
        {
            var bound = ManuscriptRangeResolver.ResolveBlocks(semantic, artworkScene.Objects[0].ContentReferences);
            Assert.All(bound.SelectMany(block => block.Content), inline => Assert.Equal(ManuscriptInlineType.Text, inline.Type));
            artworkText.Add(string.Concat(bound.Select(ManuscriptCodec.Text)));
        });
        using var docxStream = new MemoryStream(await new DocxPublishFormatter(previews).RenderAsync(publication));
        using var docx = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(docxStream, false);
        Assert.Empty(new DocumentFormat.OpenXml.Validation.OpenXmlValidator().Validate(docx));
        Assert.Equal(4, artworkText.Count);
        Assert.NotEqual(artworkText[0], artworkText[1]);
        Assert.Equal(artworkText[0], artworkText[2]);
        var main = docx.MainDocumentPart!;
        var roots = new DocumentFormat.OpenXml.OpenXmlElement[] { main.Document!, main.FootnotesPart!.Footnotes! };
        var bookmarks = roots.SelectMany(root => root.Descendants<DocumentFormat.OpenXml.Wordprocessing.BookmarkStart>()).ToList();
        Assert.Equal(bookmarks.Count, bookmarks.Select(bookmark => bookmark.Id!.Value).Distinct().Count());
        Assert.Equal(bookmarks.Count, bookmarks.Select(bookmark => bookmark.Name!.Value).Distinct().Count());
        foreach (var anchor in roots.SelectMany(root => root.Descendants<DocumentFormat.OpenXml.Wordprocessing.Hyperlink>())
            .Where(link => link.Anchor is not null))
            Assert.Contains(bookmarks, bookmark => bookmark.Name!.Value == anchor.Anchor!.Value);
        Assert.Equal(4, main.Document!.Descendants<DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties>()
            .Count(properties => properties.Description == "Shared page description"));
        Assert.Equal(4, main.Document.Descendants<DocumentFormat.OpenXml.Wordprocessing.FootnoteReference>().Count());
    }

    [Fact]
    public void Table_note_references_use_the_document_owned_note_path_in_press_payloads()
    {
        var record = new CitationRecord(Guid.NewGuid(), BibliographicRecordKind.Book, "Source", "",
            [new("River", "Ada")], [], [], new(2024), new(), "", "Press", "", "", "", "", "", "", "", "", "");
        var chapterId = Guid.NewGuid();
        var manuscript = new ManuscriptDocument
        {
            ManuscriptId = chapterId,
            Content = [new() { Id = "table-block", Type = ManuscriptBlockType.Table,
                Table = new() { Id = "table", ColumnWidthWeights = [1], Rows = [new() { Id = "row", Cells =
                    [new() { Id = "cell", Content = [new() { Id = "paragraph", Content =
                        [new() { Id = "reference", Type = ManuscriptInlineType.NoteReference, NoteId = "note" }] }] }] }] } }],
            Notes = [new() { Id = "note", Content = [new() { Id = "note-paragraph", Content =
                [new() { Id = "citation", Type = ManuscriptInlineType.Citation, Citation = new() { Items = [new() { BibliographicRecordId = record.Id }] } }] }] }],
        };
        var publication = new PublishDocument(Guid.Empty, Guid.NewGuid(), "Book", "book", DateTime.UnixEpoch,
            new("", "", "", "en", "", "", "", "", false, false, false, false, true, true, false, false, false, 6, 9, .75, 11, 1.2),
            null, [new(null, "Chapters", "", true, false, false, 0, [new(chapterId, null, "Chapter", "", "", 0, true, manuscript, [])])], []);
        var occurrence = Assert.Single(PublicationCitationTraversal.Enumerate(publication));
        Assert.Equal(["note:note"], occurrence.Identity.PlacementPath);
        var formatted = new CitationFormatter().Format(CitationStyle.APA7, [record], [occurrence]);
        var lookup = formatted.Occurrences.ToDictionary(item => PublicationSemanticPayload.CitationPayloadKey(
            item.Identity.TopLevelContainer, item.Identity.PlacementPath, item.Identity.CitationAtomId));
        var payload = PublicationSemanticPayload.NotePayload(manuscript.Notes[0], $"chapter:{chapterId:D}", ["note:note"], lookup);
        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        Assert.Contains("River, 2024", json);
    }

    [Fact]
    public void Semantic_citation_markup_escapes_content_and_retains_emphasis()
    {
        CitationRun[] runs = [new("A < B "), new("Book & title", true), new(".")];
        Assert.Equal("A &lt; B <em>Book &amp; title</em>.", SemanticPublishFormatting.CitationHtml(runs));
        Assert.Contains("*Book &amp; title*", SemanticPublishFormatting.CitationMarkdown(runs));
    }
}
