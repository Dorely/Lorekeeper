using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lorekeeper.Manuscripts.Import;
using DocumentFormat.OpenXml.Packaging;
using Docnet.Core;
using Docnet.Core.Models;
using Lorekeeper.Llm;
using Microsoft.Extensions.Options;
using SkiaSharp;
using UglyToad.PdfPig;
using VersOne.Epub;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Lorekeeper.Ingest;

public sealed partial class BookArtifactPreprocessor(
    IVisionModelClientFactory visionModels,
    ILlmProviderService providers,
    IOptions<BookArtifactIngestOptions> options) : IBookArtifactPreprocessor
{
    public async Task<BookArtifactPreprocessResult> PreprocessAsync(
        BookArtifactPreprocessRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Bytes.Length == 0)
            throw new ArgumentException("Uploaded source file is empty.", nameof(request));
        if (request.Bytes.Length > options.Value.MaxFileBytes)
            throw new InvalidOperationException($"Uploaded source file is larger than the configured limit of {options.Value.MaxFileBytes / 1024 / 1024:N0} MB.");

        var extension = Path.GetExtension(request.FileName).ToLowerInvariant();
        return extension switch
        {
            ".epub" => await PreprocessEpubAsync(request, cancellationToken),
            ".docx" => PreprocessDocx(request, cancellationToken),
            ".pdf" => await PreprocessPdfAsync(request, cancellationToken),
            ".png" or ".jpg" or ".jpeg" or ".webp" => await PreprocessImageAsync(request, cancellationToken),
            ".txt" or ".md" or ".markdown" => PreprocessPlainText(request),
            _ => throw new InvalidOperationException("Unsupported source file type. Use .txt, .md, .docx, .epub, .pdf, .png, .jpg, .jpeg, or .webp."),
        };
    }

    private static BookArtifactPreprocessResult PreprocessDocx(
        BookArtifactPreprocessRequest request,
        CancellationToken cancellationToken)
    {
        DocxPackageSafety.Validate(request.Bytes, cancellationToken, maximumInputBytes: request.Bytes.Length,
            maximumExpandedBytes: 512L * 1024 * 1024, maximumPartBytes: 64L * 1024 * 1024);
        using var package = new MemoryStream(request.Bytes, writable: false);
        using var document = WordprocessingDocument.Open(package, false, new OpenSettings
        {
            AutoSave = false,
            MaxCharactersInPart = 64L * 1024 * 1024,
        });
        var main = document.MainDocumentPart
            ?? throw new InvalidOperationException("The DOCX package has no main document part.");
        var wordDocument = main.Document
            ?? throw new InvalidOperationException("The DOCX package has no main document XML.");
        var body = wordDocument.Body
            ?? throw new InvalidOperationException("The DOCX package has no document body.");
        var output = new StringBuilder();
        var blocks = new List<IngestSourceBlockDraft>();

        foreach (var child in body.ChildElements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (child)
            {
                case W.Paragraph paragraph:
                    AppendDocxParagraph(output, blocks, paragraph, "document", cancellationToken);
                    break;
                case W.Table table:
                    AppendDocxTable(output, blocks, table, cancellationToken);
                    break;
            }
        }

        AppendDocxNotes<W.Footnote>(output, blocks, main.FootnotesPart?.Footnotes, "Footnote", cancellationToken);
        AppendDocxNotes<W.Endnote>(output, blocks, main.EndnotesPart?.Endnotes, "Endnote", cancellationToken);
        if (output.Length == 0)
            throw new InvalidOperationException("The DOCX did not contain readable text.");

        var visuals = new List<IngestVisualCandidateDraft>();
        foreach (var image in main.ImageParts.OrderBy(part => part.Uri.OriginalString, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var source = image.GetStream(FileMode.Open, FileAccess.Read);
            if (source.Length is <= 0 or > 64L * 1024 * 1024)
                continue;
            using var bytes = new MemoryStream(checked((int)source.Length));
            source.CopyTo(bytes);
            var name = Path.GetFileName(image.Uri.OriginalString);
            if (TryVisualDraft(name, image.ContentType, bytes.ToArray(), image.Uri.OriginalString, null, null, out var visual))
                visuals.Add(visual!);
        }

        return new BookArtifactPreprocessResult(
            output.ToString(),
            "Word document",
            request.ContentType ?? "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            JsonSerializer.Serialize(new
            {
                artifact = "docx",
                request.FileName,
                request.ExtractionProfile,
                sourceHash = ComputeHash(request.Bytes),
                blockCount = blocks.Count,
            }),
            Pages: [],
            Blocks: blocks,
            Visuals: DeduplicateVisuals(visuals),
            UsedVision: false,
            Diagnostics: $"Read {blocks.Count:N0} DOCX content block(s); Word pagination is not inferred.");
    }

    private static void AppendDocxParagraph(
        StringBuilder output,
        ICollection<IngestSourceBlockDraft> blocks,
        W.Paragraph paragraph,
        string locator,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = NormalizeText(paragraph.InnerText);
        if (text.Length == 0)
            return;
        if (output.Length > 0)
            output.AppendLine().AppendLine();
        var start = output.Length;
        var style = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? string.Empty;
        var headingLevel = ParseDocxHeadingLevel(style);
        if (headingLevel is { } level)
            output.Append('#', level).Append(' ');
        output.Append(text);
        var end = output.Length;
        blocks.Add(new IngestSourceBlockDraft(
            Guid.NewGuid(), null, blocks.Count,
            headingLevel is null ? "DocxParagraph" : "DocxHeading",
            headingLevel is null ? string.Empty : text,
            locator, null, start, end,
            JsonSerializer.Serialize(new { style, headingLevel })));
    }

    private static void AppendDocxTable(
        StringBuilder output,
        ICollection<IngestSourceBlockDraft> blocks,
        W.Table table,
        CancellationToken cancellationToken)
    {
        var rows = table.Elements<W.TableRow>()
            .Select(row => string.Join(" | ", row.Elements<W.TableCell>()
                .Select(cell => NormalizeText(cell.InnerText))))
            .Where(text => text.Length > 0)
            .ToArray();
        if (rows.Length == 0)
            return;
        cancellationToken.ThrowIfCancellationRequested();
        if (output.Length > 0)
            output.AppendLine().AppendLine();
        var start = output.Length;
        output.AppendJoin(Environment.NewLine, rows);
        var end = output.Length;
        blocks.Add(new IngestSourceBlockDraft(
            Guid.NewGuid(), null, blocks.Count, "DocxTable", string.Empty,
            $"table {blocks.Count + 1}", null, start, end,
            JsonSerializer.Serialize(new { rowCount = rows.Length })));
    }

    private static void AppendDocxNotes<TNote>(
        StringBuilder output,
        ICollection<IngestSourceBlockDraft> blocks,
        DocumentFormat.OpenXml.OpenXmlCompositeElement? notes,
        string kind,
        CancellationToken cancellationToken)
        where TNote : DocumentFormat.OpenXml.OpenXmlCompositeElement
    {
        if (notes is null)
            return;
        foreach (var note in notes.Elements<TNote>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = NormalizeText(note.InnerText);
            if (text.Length == 0)
                continue;
            if (output.Length > 0)
                output.AppendLine().AppendLine();
            var start = output.Length;
            output.Append('[').Append(kind).Append("] ").Append(text);
            var end = output.Length;
            blocks.Add(new IngestSourceBlockDraft(
                Guid.NewGuid(), null, blocks.Count, $"Docx{kind}", string.Empty,
                kind.ToLowerInvariant(), null, start, end, "{}"));
        }
    }

    private static int? ParseDocxHeadingLevel(string style)
    {
        if (style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(style.AsSpan("Heading".Length), out var level))
        {
            return Math.Clamp(level, 1, 6);
        }
        return null;
    }


    private async Task<BookArtifactPreprocessResult> PreprocessImageAsync(BookArtifactPreprocessRequest request, CancellationToken cancellationToken)
    {
        if (request.ProviderId is not int providerId || !await providers.IsVisionProviderWorkingAsync(providerId, cancellationToken))
            throw new InvalidOperationException("Standalone image ingestion requires a vision-ready model. Select one and run Test in Settings > Providers.");
        var contentType = ImageContentType(request.FileName, request.ContentType);
        if (!TryVisualDraft(request.FileName, contentType, request.Bytes, Path.GetFileName(request.FileName), null, null, out var visual))
            throw new InvalidOperationException("The image is invalid, unsupported, or too small to ingest as a visual source.");
        var description = NormalizeText(await visionModels.ReadImageAsync(
            providerId, request.Bytes, contentType,
            "Describe this source image factually for entity ingestion. Identify only clearly visible people, creatures, locations, objects, labels, and continuity-relevant appearance details. Note ambiguity explicitly and do not guess identities.",
            options.Value.VisionPageMaxOutputTokens, cancellationToken));
        if (string.IsNullOrWhiteSpace(description)) throw new InvalidOperationException("The vision model returned no readable description for this image.");
        var sourceText = $"# {Path.GetFileNameWithoutExtension(request.FileName)}\n\n{description}\n";
        visual = visual! with { StartChar = 0, EndChar = sourceText.Length };
        return new BookArtifactPreprocessResult(
            sourceText, "Image source", contentType,
            JsonSerializer.Serialize(new { artifact = "image", request.FileName, request.ExtractionProfile, sourceHash = ComputeHash(request.Bytes), visionProviderId = providerId }),
            Pages: [],
            Blocks: [new IngestSourceBlockDraft(Guid.NewGuid(), null, 0, "Image", Path.GetFileNameWithoutExtension(request.FileName), request.FileName, null, 0, sourceText.Length, "{}")],
            Visuals: [visual],
            UsedVision: true,
            Diagnostics: "Read standalone image with the selected vision model.");
    }

    private static BookArtifactPreprocessResult PreprocessPlainText(BookArtifactPreprocessRequest request)
    {
        var text = DecodeText(request.Bytes).Trim();
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("The selected text file did not contain readable text.");

        var sourceText = text + Environment.NewLine;
        var block = new IngestSourceBlockDraft(
            Guid.NewGuid(),
            SourcePageId: null,
            Index: 0,
            Kind: "Text",
            Title: Path.GetFileNameWithoutExtension(request.FileName),
            Locator: "text",
            PageNumber: null,
            StartChar: 0,
            EndChar: sourceText.Length,
            MetadataJson: JsonSerializer.Serialize(new { request.FileName, request.ExtractionProfile }));

        return new BookArtifactPreprocessResult(
            sourceText,
            SourceKindFromExtension(request.FileName),
            request.ContentType ?? "text/plain",
            JsonSerializer.Serialize(new
            {
                artifact = "plain_text",
                request.FileName,
                request.ExtractionProfile,
                sourceHash = ComputeHash(request.Bytes),
            }),
            Pages: [],
            Blocks: [block],
            Visuals: [],
            UsedVision: false,
            Diagnostics: "Read text file.");
    }

    private static async Task<BookArtifactPreprocessResult> PreprocessEpubAsync(
        BookArtifactPreprocessRequest request,
        CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream(request.Bytes);
        var book = await EpubReader.ReadBookAsync(stream);
        var sb = new StringBuilder();
        var blocks = new List<IngestSourceBlockDraft>();
        var visuals = new List<IngestVisualCandidateDraft>();
        var epubSections = new List<(string Html, int Start, int End, string FilePath)>();
        var index = 0;

        foreach (var file in book.ReadingOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sectionText = NormalizeText(HtmlToText(file.Content));
            if (string.IsNullOrWhiteSpace(sectionText))
                continue;

            if (sb.Length > 0) sb.AppendLine().AppendLine();
            var filePath = file.FilePath;
            var title = string.IsNullOrWhiteSpace(filePath)
                ? $"Section {index + 1}"
                : Path.GetFileNameWithoutExtension(filePath);
            var start = sb.Length;
            sb.AppendLine($"# {title}");
            sb.AppendLine();
            sb.AppendLine(sectionText);
            var end = sb.Length;
            epubSections.Add((file.Content, start, end, filePath));

            blocks.Add(new IngestSourceBlockDraft(
                Guid.NewGuid(),
                SourcePageId: null,
                Index: index,
                Kind: "EpubSection",
                Title: title,
                Locator: filePath,
                PageNumber: null,
                StartChar: start,
                EndChar: end,
                MetadataJson: JsonSerializer.Serialize(new { filePath, request.ExtractionProfile })));
            index++;
        }

        if (sb.Length == 0)
            throw new InvalidOperationException("The EPUB did not contain readable text.");

        foreach (var image in book.Content.Images.Local)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(image.FilePath);
            var matchingSection = epubSections.FirstOrDefault(section =>
                section.Html.Contains(image.FilePath, StringComparison.OrdinalIgnoreCase)
                || section.Html.Contains(fileName, StringComparison.OrdinalIgnoreCase));
            (int Start, int End)? range = matchingSection == default ? null : (matchingSection.Start, matchingSection.End);
            if (TryVisualDraft(fileName, image.ContentMimeType, image.Content, image.FilePath, null, range, out var visual))
                visuals.Add(visual!);
        }

        return new BookArtifactPreprocessResult(
            sb.ToString(),
            "EPUB book",
            request.ContentType ?? "application/epub+zip",
            JsonSerializer.Serialize(new
            {
                artifact = "epub",
                request.FileName,
                book.Title,
                request.ExtractionProfile,
                sourceHash = ComputeHash(request.Bytes),
                sectionCount = blocks.Count,
            }),
            Pages: [],
            Blocks: blocks,
            Visuals: DeduplicateVisuals(visuals),
            UsedVision: false,
            Diagnostics: $"Read {blocks.Count:N0} EPUB section(s).");
    }

    private async Task<BookArtifactPreprocessResult> PreprocessPdfAsync(
        BookArtifactPreprocessRequest request,
        CancellationToken cancellationToken)
    {
        var maxPages = Math.Clamp(request.PdfOptions.MaxPages ?? options.Value.MaxPdfPages, 1, options.Value.MaxPdfPages);
        var dpi = Math.Clamp(request.PdfOptions.VisionDpi ?? options.Value.PdfVisionDpi, 72, 300);
        var maxImagePixels = Math.Clamp(request.PdfOptions.MaxImagePixels ?? options.Value.MaxImagePixels, 250_000, 20_000_000);
        var pagesWithoutText = 0;
        var pages = new List<IngestSourcePageDraft>();
        var blocks = new List<IngestSourceBlockDraft>();
        var visuals = new List<IngestVisualCandidateDraft>();
        var sb = new StringBuilder();
        var usedVision = false;
        var visionProvider = request.ProviderId is int providerId
            ? await providers.GetByIdAsync(providerId, cancellationToken)
            : null;

        await using var stream = new MemoryStream(request.Bytes);
        using var document = PdfDocument.Open(stream);
        var documentPages = document.GetPages().Take(maxPages).ToList();
        var totalPages = document.NumberOfPages;
        if (documentPages.Count == 0)
            throw new InvalidOperationException("The PDF did not contain any readable pages.");

        foreach (var page in documentPages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageText = NormalizeText(page.Text);
            var extractionMethod = "EmbeddedText";
            var diagnostics = string.Empty;
            var imageHash = string.Empty;
            var renderSettingsJson = "{}";
            var pageWidth = (int)Math.Ceiling(page.Width);
            var pageHeight = (int)Math.Ceiling(page.Height);

            var useVision = request.PdfOptions.ForceVision;
            byte[]? renderedPageBytes = null;
            if (useVision)
            {
                if (request.ProviderId is not int visionProviderId)
                    throw new InvalidOperationException("Select a vision-ready model before ingesting a PDF page that requires image reading.");
                if (!await providers.IsVisionProviderWorkingAsync(visionProviderId, cancellationToken))
                    throw new InvalidOperationException("The selected model is not vision-ready. Run Test in Settings > Providers before ingesting this PDF.");

                var render = RenderPdfPage(request.Bytes, page.Number, page.Width, page.Height, dpi, maxImagePixels);
                renderedPageBytes = render.ImageBytes;
                imageHash = ComputeHash(render.ImageBytes);
                renderSettingsJson = JsonSerializer.Serialize(new
                {
                    dpi = render.Dpi,
                    render.Width,
                    render.Height,
                    imageFormat = "png",
                });

                pageWidth = render.Width;
                pageHeight = render.Height;
                pageText = NormalizeText(await visionModels.ReadImageAsync(
                    visionProviderId,
                    render.ImageBytes,
                    "image/png",
                    BuildPdfVisionPrompt(page.Number),
                    options.Value.VisionPageMaxOutputTokens,
                    cancellationToken));
                extractionMethod = "Vision";
                diagnostics = pageText.Length == 0 ? "Vision model returned no text." : "Read from rendered page image.";
                usedVision = true;
            }
            else
            {
                diagnostics = pageText.Length == 0
                    ? "No embedded page text. Vision reading was not selected."
                    : "Read embedded PDF text.";
            }

            if (string.IsNullOrWhiteSpace(pageText)) pagesWithoutText++;

            if (sb.Length > 0) sb.AppendLine().AppendLine();
            var start = sb.Length;
            sb.AppendLine($"[Page {page.Number}]");
            sb.AppendLine();
            sb.AppendLine(pageText);
            var end = sb.Length;
            var pageId = Guid.NewGuid();

            pages.Add(new IngestSourcePageDraft(
                pageId,
                page.Number,
                pageText,
                start,
                end,
                extractionMethod,
                pageWidth,
                pageHeight,
                imageHash,
                renderSettingsJson,
                extractionMethod == "Vision" ? request.ProviderId : null,
                extractionMethod == "Vision" ? visionProvider?.ModelId ?? string.Empty : string.Empty,
                diagnostics));

            blocks.Add(new IngestSourceBlockDraft(
                Guid.NewGuid(),
                pageId,
                blocks.Count,
                "PdfPage",
                $"Page {page.Number}",
                $"p. {page.Number}",
                page.Number,
                start,
                end,
                JsonSerializer.Serialize(new { page.Number, extractionMethod, request.ExtractionProfile })));

            var embeddedIndex = 0;
            foreach (var embedded in page.GetImages())
            {
                try
                {
                    if (!embedded.IsImageMask && embedded.TryGetPng(out var png)
                        && TryVisualDraft($"page-{page.Number}-image-{++embeddedIndex}.png", "image/png", png, $"p. {page.Number} embedded image {embeddedIndex}", page.Number, (start, end), out var visual))
                        visuals.Add(visual!);
                }
                catch
                {
                    // Unsupported/malformed embedded image data must not block text ingestion.
                }
            }
            if (renderedPageBytes is not null
                && TryVisualDraft($"page-{page.Number}-render.png", "image/png", renderedPageBytes, $"p. {page.Number} rendered page", page.Number, (start, end), out var renderedVisual))
                visuals.Add(renderedVisual! with { MetadataJson = JsonSerializer.Serialize(new { kind = "renderedPage", extractionMethod }) });
        }

        if (pagesWithoutText == documentPages.Count)
            throw new InvalidOperationException(request.PdfOptions.ForceVision
                ? "Vision returned no readable text from this PDF. The original is retained."
                : "This PDF has no embedded text in the selected page range. The original is retained; select a vision-ready model and enable PDF vision reading to index image-only pages.");

        var diagnosticsSummary = totalPages > documentPages.Count
            ? $"Read {documentPages.Count:N0} of {totalPages:N0} PDF page(s)."
            : $"Read {documentPages.Count:N0} PDF page(s).";
        if (pagesWithoutText > 0)
            diagnosticsSummary += $" {pagesWithoutText:N0} page(s) contain no readable text."
                + (usedVision ? string.Empty : " Vision reading was not selected; image-only pages are not text-indexed.");

        return new BookArtifactPreprocessResult(
            sb.ToString(),
            "PDF book",
            request.ContentType ?? "application/pdf",
            JsonSerializer.Serialize(new
            {
                artifact = "pdf",
                request.FileName,
                request.ExtractionProfile,
                sourceHash = ComputeHash(request.Bytes),
                totalPages,
                processedPages = documentPages.Count,
                usedVision,
                request.PdfOptions.ForceVision,
                dpi,
                maxImagePixels,
            }),
            pages,
            blocks,
            DeduplicateVisuals(visuals),
            usedVision,
            diagnosticsSummary);
    }

    private static PdfPageRenderResult RenderPdfPage(byte[] pdfBytes, int pageNumber, double pageWidthPoints, double pageHeightPoints, int dpi, int maxImagePixels)
    {
        var scale = dpi / 72.0;
        var width = Math.Max(1, (int)Math.Ceiling(pageWidthPoints * scale));
        var height = Math.Max(1, (int)Math.Ceiling(pageHeightPoints * scale));
        var pixels = width * height;
        if (pixels > maxImagePixels)
        {
            var downscale = Math.Sqrt(maxImagePixels / (double)pixels);
            width = Math.Max(1, (int)Math.Floor(width * downscale));
            height = Math.Max(1, (int)Math.Floor(height * downscale));
        }

        using var docReader = DocLib.Instance.GetDocReader(pdfBytes, new PageDimensions(width, height));
        using var pageReader = docReader.GetPageReader(pageNumber - 1);
        var rawBytes = pageReader.GetImage();
        var renderedWidth = pageReader.GetPageWidth();
        var renderedHeight = pageReader.GetPageHeight();

        using var bitmap = new SKBitmap(renderedWidth, renderedHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        System.Runtime.InteropServices.Marshal.Copy(rawBytes, 0, bitmap.GetPixels(), rawBytes.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 95);
        return new PdfPageRenderResult(encoded.ToArray(), renderedWidth, renderedHeight, dpi);
    }

    private static string BuildPdfVisionPrompt(int pageNumber) =>
        $"""
        Transcribe the readable text on PDF page {pageNumber}.
        Preserve reading order, headings, paragraph breaks, captions, footnotes, and page-visible labels when legible.
        Do not summarize. Do not explain. Return only the transcribed page text.
        """;

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        return Encoding.UTF8.GetString(bytes);
    }

    private static string HtmlToText(string html)
    {
        var withBreaks = BlockBreakRegex().Replace(html, "\n");
        var withoutTags = TagRegex().Replace(withBreaks, " ");
        return WebUtility.HtmlDecode(withoutTags);
    }

    private static string NormalizeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        normalized = HorizontalWhitespaceRegex().Replace(normalized, " ");
        normalized = ExcessiveBlankLinesRegex().Replace(normalized, "\n\n");
        return normalized.Trim();
    }

    private static string SourceKindFromExtension(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".md" or ".markdown" => "Markdown source",
            _ => "Text source",
        };

    private static string ImageContentType(string fileName, string? contentType)
    {
        if (contentType is "image/png" or "image/jpeg" or "image/webp") return contentType;
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "image/jpeg",
        };
    }

    private static bool TryVisualDraft(
        string fileName,
        string contentType,
        byte[] data,
        string locator,
        int? pageNumber,
        (int Start, int End)? charRange,
        out IngestVisualCandidateDraft? visual)
    {
        visual = null;
        if (contentType is not ("image/png" or "image/jpeg" or "image/webp") || data.Length == 0) return false;
        using var bitmap = SKBitmap.Decode(data);
        if (bitmap is null || bitmap.Width < 64 || bitmap.Height < 64 || (long)bitmap.Width * bitmap.Height < 16_384) return false;
        visual = new IngestVisualCandidateDraft(
            string.IsNullOrWhiteSpace(fileName) ? "source-image" : fileName,
            contentType,
            data,
            AltText: string.Empty,
            Caption: string.Empty,
            locator,
            pageNumber,
            charRange?.Start,
            charRange?.End,
            JsonSerializer.Serialize(new { bitmap.Width, bitmap.Height, sourceHash = ComputeHash(data) }));
        return true;
    }

    private static IReadOnlyList<IngestVisualCandidateDraft> DeduplicateVisuals(IEnumerable<IngestVisualCandidateDraft> visuals) =>
        visuals.GroupBy(visual => ComputeHash(visual.Data), StringComparer.Ordinal).Select(group => group.First()).ToList();

    private static string ComputeHash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [GeneratedRegex("<\\s*(br|p|div|section|article|h[1-6]|li|tr|table)\\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreakRegex();

    [GeneratedRegex("<[^>]+>", RegexOptions.IgnoreCase)]
    private static partial Regex TagRegex();

    [GeneratedRegex("[\\t \\u00A0]+")]
    private static partial Regex HorizontalWhitespaceRegex();

    [GeneratedRegex("\\n{3,}")]
    private static partial Regex ExcessiveBlankLinesRegex();

    private sealed record PdfPageRenderResult(byte[] ImageBytes, int Width, int Height, int Dpi);
}
