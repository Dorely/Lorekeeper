using System.IO.Compression;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Lorekeeper.Ingest;
using Microsoft.Extensions.Options;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Lorekeeper.Tests;

public sealed class BookArtifactDocxPreprocessorTests
{
    [Fact]
    public async Task DocxExtractionPreservesHeadingsAndTableTextWithoutInferringPagination()
    {
        var result = await CreateSubject().PreprocessAsync(new BookArtifactPreprocessRequest(
            "fixture.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            CreateDocument(),
            ProviderId: null,
            IngestExtractionProfile.Auto,
            new PdfArtifactIngestOptions()));

        Assert.Equal("Word document", result.SourceKind);
        Assert.Empty(result.Pages);
        Assert.Contains("# Chapter One", result.SourceText, StringComparison.Ordinal);
        Assert.Contains("Alpha | Beta", result.SourceText, StringComparison.Ordinal);
        Assert.Contains(result.Blocks, block => block.Kind == "DocxHeading" && block.Title == "Chapter One");
        Assert.Contains(result.Blocks, block => block.Kind == "DocxTable");
        Assert.Contains("Word pagination is not inferred", result.Diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocxExtractionRejectsEmbeddedObjectsBeforeOpeningThePackage()
    {
        var bytes = CreateDocument();
        using var package = new MemoryStream();
        package.Write(bytes);
        package.Position = 0;
        using (var archive = new ZipArchive(package, ZipArchiveMode.Update, leaveOpen: true))
        {
            using var payload = archive.CreateEntry("word/embeddings/object.bin").Open();
            payload.WriteByte(1);
        }
        bytes = package.ToArray();

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            CreateSubject().PreprocessAsync(new BookArtifactPreprocessRequest(
                "unsafe.docx",
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                bytes,
                ProviderId: null,
                IngestExtractionProfile.Auto,
                new PdfArtifactIngestOptions())));

        Assert.Contains("executable or embedded object", error.Message, StringComparison.Ordinal);
    }

    private static BookArtifactPreprocessor CreateSubject() => new(
        visionModels: null!,
        providers: null!,
        Options.Create(new BookArtifactIngestOptions()));

    private static byte[] CreateDocument()
    {
        using var stream = new MemoryStream();
        using (var package = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, autoSave: true))
        {
            var main = package.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body(
                new W.Paragraph(
                    new W.ParagraphProperties(new W.ParagraphStyleId { Val = "Heading1" }),
                    new W.Run(new W.Text("Chapter One"))),
                new W.Paragraph(new W.Run(new W.Text("Opening paragraph."))),
                new W.Table(
                    new W.TableRow(
                        new W.TableCell(new W.Paragraph(new W.Run(new W.Text("Alpha")))),
                        new W.TableCell(new W.Paragraph(new W.Run(new W.Text("Beta"))))))));
        }

        return stream.ToArray();
    }
}
