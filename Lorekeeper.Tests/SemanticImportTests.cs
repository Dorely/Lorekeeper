using System.IO.Compression;
using System.Text;
using Lorekeeper.Authoring;
using Lorekeeper.Manuscripts;
using Lorekeeper.Manuscripts.Import;

namespace Lorekeeper.Tests;

public sealed class SemanticImportTests
{
    [Fact]
    public void ManuscriptEnumsKeepTheClientHashVocabularyAtTheInteropBoundary()
    {
        var position = new ManuscriptPosition(Guid.NewGuid(), ["document"], "body", 0, ManuscriptPositionAffinity.After);
        var wire = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            position,
            note = new ManuscriptNote { Id = "note", Kind = ManuscriptNoteKind.Footnote },
            figure = new FigurePresentation { Placement = FigurePlacementIntent.Inline },
        }, ManuscriptCodec.JsonOptions);
        Assert.Equal("after", wire.GetProperty("position").GetProperty("affinity").GetString());
        Assert.Equal("footnote", wire.GetProperty("note").GetProperty("kind").GetString());
        Assert.Equal("inline", wire.GetProperty("figure").GetProperty("placement").GetString());
    }
    [Fact]
    public async Task DocxPreservesFinalTextStylesListsMergedTableNotesAndCitationMetadata()
    {
        var fragment = await new SemanticImportService().ReadDocxAsync(Package("""
            <w:p><w:pPr><w:pStyle w:val="Heading2"/></w:pPr><w:r><w:t>Heading</w:t></w:r></w:p>
            <w:p><w:del><w:r><w:delText>Deleted</w:delText></w:r></w:del><w:ins><w:r><w:rPr><w:b/></w:rPr><w:t>Final text</w:t></w:r></w:ins>
            <w:r><w:footnoteReference w:id="1"/></w:r><w:fldSimple w:instr=" CITATION Example \p 42 "><w:r><w:t>(Example, 2024)</w:t></w:r></w:fldSimple></w:p>
            <w:p><w:pPr><w:numPr><w:ilvl w:val="1"/><w:numId w:val="1"/></w:numPr></w:pPr><w:r><w:t>Nested numbered item</w:t></w:r></w:p>
            <w:tbl><w:tblGrid><w:gridCol w:w="1000"/><w:gridCol w:w="2000"/></w:tblGrid>
            <w:tr><w:tc><w:tcPr><w:vMerge w:val="restart"/></w:tcPr><w:p><w:r><w:t>Merged</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Right</w:t></w:r></w:p></w:tc></w:tr>
            <w:tr><w:tc><w:tcPr><w:vMerge/></w:tcPr><w:p/></w:tc><w:tc><w:p><w:r><w:t>Bottom</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
            """, rich: true));
        Assert.Equal(2, fragment.Document.Content[0].HeadingLevel);
        Assert.Contains(fragment.Document.Content[1].Content, run => run.Text == "Final text" && run.Marks.Any(mark => mark.Type == ManuscriptMarkType.Strong));
        Assert.DoesNotContain("Deleted", ManuscriptCodec.Serialize(fragment.Document));
        var list = fragment.Document.Content[2].List!;
        Assert.True(list.Ordered); Assert.Equal(1, list.Level); Assert.Equal(3, list.Start);
        var table = fragment.Document.Content[3].Table!;
        Assert.Equal([1000, 2000], table.ColumnWidthWeights); Assert.Equal(2, table.Rows[0].Cells[0].RowSpan);
        Assert.Equal("Note content", ManuscriptCodec.Text(Assert.Single(fragment.Document.Notes).Content[0]));
        var record = Assert.Single(fragment.Resources.Bibliography);
        Assert.Equal("Example book", record.Title); Assert.Equal(2024, record.Issued.Year);
        var citation = Assert.Single(ManuscriptTraversal.EnumerateCitations(fragment.Document));
        Assert.Equal(record.Id, citation.Cluster.Items[0].BibliographicRecordId); Assert.Equal("42", citation.Cluster.Items[0].LocatorValue);
        Assert.Contains(fragment.Report, message => message.Contains("revision history", StringComparison.Ordinal));
        Assert.NotEmpty(fragment.Resources.Styles);
    }

    [Fact]
    public async Task UnresolvedWordCitationRetainsItsDisplayAndActionableReport()
    {
        var result = await new SemanticImportService().ReadDocxAsync(Package("""
            <w:p><w:fldSimple w:instr=" CITATION Missing "><w:r><w:t>(Missing, p. 12)</w:t></w:r></w:fldSimple></w:p>
            """));
        Assert.Equal("(Missing, p. 12)", ManuscriptCodec.Text(result.Document.Content[0]));
        Assert.Empty(result.Resources.Bibliography);
        Assert.Contains(result.Report, message => message.Contains("Bibliography", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClipboardKeepsCellFormattingAndReportsUnresolvedConditionalCitation()
    {
        var result = await new SemanticImportService().ReadWordHtmlAsync("""
            <html><body><p style="line-height:150%"><!--[if supportFields] CITATION Missing -->(Missing, 2024)</p>
            <table><tr><td style="width:144pt"><h2>Styled cell</h2></td><td style="width:72pt"><p>Second</p></td></tr></table></body></html>
            """);
        Assert.Equal("(Missing, 2024)", ManuscriptCodec.Text(result.Document.Content[0]));
        Assert.Contains(result.Report, message => message.Contains("Bibliography", StringComparison.Ordinal));
        Assert.Contains(result.Resources.Styles, style => style.Definition.LineHeight == 1.5);
        var table = result.Document.Content[1].Table!;
        Assert.Equal([2880, 1440], table.ColumnWidthWeights);
        Assert.Equal(ManuscriptBlockType.Paragraph, table.Rows[0].Cells[0].Content[0].Type);
    }

    [Theory]
    [InlineData("../escape.xml", "<x/>")]
    [InlineData("word/unused.dll", "executable")]
    [InlineData("word/activeX/control.xml", "<x/>")]
    [InlineData("word/unused.xml", "<!DOCTYPE x [<!ENTITY evil 'unsafe'>]><x>&evil;</x>")]
    public async Task UnsafePackageIsRejectedEvenWhenThePartIsUnused(string path, string content)
    {
        var bytes = Package("<w:p><w:r><w:t>Safe text</w:t></w:r></w:p>", extra: new(path, content));
        await Assert.ThrowsAnyAsync<Exception>(() => new SemanticImportService().ReadDocxAsync(bytes));
    }

    [Fact]
    public async Task RemoteRelationshipsAndDanglingNoteOwnershipFailClosed()
    {
        var remote = Package("<w:p/>", extra: new("word/_rels/unsafe.xml.rels", """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="remote" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="https://example.invalid/image.png" TargetMode="External"/></Relationships>
            """));
        await Assert.ThrowsAsync<InvalidDataException>(() => new SemanticImportService().ReadDocxAsync(remote));
        await Assert.ThrowsAsync<InvalidDataException>(() => new SemanticImportService().ReadDocxAsync(Package("<w:p><w:r><w:footnoteReference w:id=\"7\"/></w:r></w:p>")));
    }

    [Fact]
    public async Task WordHtmlPreservesFormattingNestedListsTableSpansAndNotes()
    {
        var result = await new SemanticImportService().ReadWordHtmlAsync("""
            <html><head><style>p.MsoNormal {text-align:center;margin-left:12pt} @list l0:level1 {mso-level-number-format:bullet}</style></head><body>
            <p class="MsoNormal"><b>Bold</b> <i>italic</i><a href="#_ftn1">1</a></p>
            <p style="mso-list:l0 level1 lfo1"><span style="mso-list:Ignore">•</span>Bullet</p>
            <ol start="4"><li>Outer<ul><li>Inner</li></ul></li></ol>
            <table><tr><td rowspan="2">Merged</td><td>Right</td></tr><tr><td>Bottom</td></tr></table>
            <div style="mso-element:footnote-list"><div id="_ftn1" style="mso-element:footnote"><p><a href="#_ftnref1">1</a>Note text</p></div></div>
            </body></html>
            """);
        Assert.Equal(5, result.Document.Content.Count);
        Assert.Contains(result.Document.Content[0].Content, run => run.Text == "Bold" && run.Marks.Any(mark => mark.Type == ManuscriptMarkType.Strong));
        Assert.False(result.Document.Content[1].List!.Ordered);
        Assert.Equal("Bullet", ManuscriptCodec.Text(result.Document.Content[1]));
        Assert.Equal(4, result.Document.Content[2].List!.Start);
        Assert.Equal(1, result.Document.Content[3].List!.Level);
        Assert.Equal(2, result.Document.Content[4].Table!.Rows[0].Cells[0].RowSpan);
        Assert.Equal("Note text", ManuscriptCodec.Text(Assert.Single(result.Document.Notes).Content[0]));
    }

    [Theory]
    [InlineData("<p class=MsoNormal>Text<script>alert(1)</script></p>")]
    [InlineData("<p class=MsoNormal><img src='https://example.invalid/picture.png'></p>")]
    [InlineData("<p class=MsoNormal><a href='#_ftn1'>1</a></p>")]
    public async Task UnsafeClipboardAndMissingNotesAreRejected(string html) =>
        await Assert.ThrowsAsync<InvalidDataException>(() => new SemanticImportService().ReadWordHtmlAsync(html));

    [Fact]
    public async Task ClipboardCitationMetadataUsesTheSameWordRecordAndFieldConversion()
    {
        var fragment = await new SemanticImportService().ReadWordHtmlAsync("""
            <html><body><xml><b:Sources><b:Source><b:Tag>Source</b:Tag><b:SourceType>Book</b:SourceType><b:Title>Clipboard book</b:Title><b:Year>2024</b:Year></b:Source></b:Sources></xml>
            <p class=MsoNormal><span style='mso-field-code:" CITATION Source \p 7 "'>(Source, 7)</span></p></body></html>
            """);
        var citation = Assert.Single(ManuscriptTraversal.EnumerateCitations(fragment.Document));
        Assert.Equal("Clipboard book", Assert.Single(fragment.Resources.Bibliography).Title);
        Assert.Equal("7", citation.Cluster.Items[0].LocatorValue);
    }

    [Fact]
    public void InsertionBesideCitationKeepsEveryOriginalAtomAndHasCanonicalUndo()
    {
        var source = CitationPreservationTests.Document(Guid.NewGuid(), null);
        var fragment = new ManuscriptDocument { ManuscriptId = Guid.NewGuid(), Content = [new() { Id = "imported", Content = [new() { Text = "Inserted" }] }] };
        var position = new ManuscriptPosition(source.ManuscriptId, ["document"], "body-citation", 0, ManuscriptPositionAffinity.After);
        var operation = new AuthoringOperationV1(0, "insertSemanticFragment", SecondBlockId: "tail", RichDocument: fragment,
            Position: position, ExpectedDocumentFingerprint: AuthoringBatchReducer.Fingerprint(source));
        var applied = AuthoringBatchReducer.Apply(source, [operation]);
        Assert.Equal(3, ManuscriptTraversal.EnumerateCitations(applied.Document).Count);
        Assert.Single(applied.Document.Notes);
        Assert.Equal("Inserted", ManuscriptCodec.Text(applied.Document.Content[1]));
        var undone = AuthoringBatchReducer.Apply(applied.Document, applied.CanonicalInverse, true);
        Assert.True(ManuscriptCodec.ContentEquals(source, undone.Document));
        Assert.Throws<InvalidDataException>(() => SemanticImportInsertion.Insert(source, position with { Offset = 1 }, fragment, "tail"));
    }

    private static byte[] Package(string body, bool rich = false, KeyValuePair<string, string>? extra = null)
    {
        const string w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        const string relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = """
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
                """ + (rich ? """
                <Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/><Override PartName="/word/numbering.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml"/><Override PartName="/word/footnotes.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.footnotes+xml"/>
                """ : "") + "</Types>",
            ["_rels/.rels"] = $"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"main\" Type=\"{relationships}officeDocument\" Target=\"word/document.xml\"/></Relationships>",
            ["word/document.xml"] = $"<w:document xmlns:w=\"{w}\"><w:body>{body}</w:body></w:document>",
        };
        if (rich)
        {
            parts["word/_rels/document.xml.rels"] = $"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + string.Join("", new[] { "styles", "numbering", "footnotes", "customXml" }.Select(kind => $"<Relationship Id=\"{kind}\" Type=\"{relationships}{kind}\" Target=\"{kind}.xml\"/>")) + "</Relationships>";
            parts["word/styles.xml"] = $"<w:styles xmlns:w=\"{w}\"><w:style w:type=\"paragraph\" w:styleId=\"Heading2\"><w:name w:val=\"Heading 2\"/><w:pPr><w:outlineLvl w:val=\"1\"/></w:pPr><w:rPr><w:i/></w:rPr></w:style></w:styles>";
            parts["word/numbering.xml"] = $"<w:numbering xmlns:w=\"{w}\"><w:abstractNum w:abstractNumId=\"1\"><w:lvl w:ilvl=\"1\"><w:start w:val=\"3\"/><w:numFmt w:val=\"decimal\"/></w:lvl></w:abstractNum><w:num w:numId=\"1\"><w:abstractNumId w:val=\"1\"/></w:num></w:numbering>";
            parts["word/footnotes.xml"] = $"<w:footnotes xmlns:w=\"{w}\"><w:footnote w:id=\"1\"><w:p><w:r><w:t>Note content</w:t></w:r></w:p></w:footnote></w:footnotes>";
            parts["word/customXml.xml"] = """
                <b:Sources xmlns:b="http://schemas.openxmlformats.org/officeDocument/2006/bibliography"><b:Source><b:Tag>Example</b:Tag><b:SourceType>Book</b:SourceType><b:Title>Example book</b:Title><b:Year>2024</b:Year><b:Author><b:Author><b:NameList><b:Person><b:Last>Writer</b:Last><b:First>A.</b:First></b:Person></b:NameList></b:Author></b:Author></b:Source></b:Sources>
                """;
        }
        if (extra is { } item) parts[item.Key] = item.Value;
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var part in parts)
            {
                using var writer = new StreamWriter(zip.CreateEntry(part.Key).Open(), new UTF8Encoding(false));
                writer.Write(part.Value);
            }
        return stream.ToArray();
    }
}
