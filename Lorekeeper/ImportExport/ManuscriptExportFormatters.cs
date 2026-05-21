using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;

namespace Lorekeeper.ImportExport;

public interface IManuscriptExportFormatter
{
    ManuscriptExportFormat Format { get; }
    string FileExtension { get; }
    string ContentType { get; }
    byte[] Render(ManuscriptExportDocument manuscript);
}

public sealed class PlainTextManuscriptFormatter : IManuscriptExportFormatter
{
    public ManuscriptExportFormat Format => ManuscriptExportFormat.PlainText;
    public string FileExtension => ".txt";
    public string ContentType => "text/plain; charset=utf-8";

    public byte[] Render(ManuscriptExportDocument manuscript)
    {
        var sb = new StringBuilder();
        var includeSynopses = manuscript.Options.IncludeSynopses;

        foreach (var section in manuscript.Sections)
        {
            if (includeSynopses && !section.IsUnassigned)
            {
                AppendGap(sb);
                AppendHeading(sb, section.Title, '-');
                AppendSynopsis(sb, section.Synopsis);
            }

            foreach (var chapter in section.Chapters)
            {
                AppendGap(sb);
                AppendHeading(sb, chapter.Title, '=');
                AppendSynopsis(sb, includeSynopses ? chapter.Synopsis : string.Empty);
                sb.AppendLine();
                sb.AppendLine(chapter.Body.TrimEnd());
            }
        }

        return Utf8(sb.ToString().TrimEnd() + Environment.NewLine);
    }

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

    private static void AppendSynopsis(StringBuilder sb, string synopsis)
    {
        if (string.IsNullOrWhiteSpace(synopsis)) return;
        sb.AppendLine();
        sb.AppendLine(synopsis.Trim());
    }

    private static string CleanHeading(string heading) =>
        heading.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();

    private static byte[] Utf8(string content) => Encoding.UTF8.GetBytes(content);
}

public sealed class MarkdownManuscriptFormatter : IManuscriptExportFormatter
{
    public ManuscriptExportFormat Format => ManuscriptExportFormat.Markdown;
    public string FileExtension => ".md";
    public string ContentType => "text/markdown; charset=utf-8";

    public byte[] Render(ManuscriptExportDocument manuscript)
    {
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(EscapeHeading(manuscript.ProjectName));

        foreach (var section in manuscript.Sections)
        {
            sb.AppendLine().Append("## ").AppendLine(EscapeHeading(section.Title));
            AppendSynopsis(sb, manuscript.Options.IncludeSynopses ? section.Synopsis : string.Empty);

            foreach (var chapter in section.Chapters)
                AppendChapter(sb, chapter, manuscript.Options.IncludeSynopses);
        }

        return Encoding.UTF8.GetBytes(sb.ToString().TrimEnd() + Environment.NewLine);
    }

    private static void AppendChapter(StringBuilder sb, ManuscriptExportChapter chapter, bool includeSynopsis)
    {
        sb.AppendLine().Append("### ").AppendLine(EscapeHeading(chapter.Title));
        AppendSynopsis(sb, includeSynopsis ? chapter.Synopsis : string.Empty);
        sb.AppendLine();
        sb.AppendLine(chapter.Body.TrimEnd());
    }

    private static void AppendSynopsis(StringBuilder sb, string synopsis)
    {
        if (string.IsNullOrWhiteSpace(synopsis)) return;
        sb.AppendLine();
        foreach (var line in SplitLines(synopsis.Trim()))
            sb.Append("> ").AppendLine(line);
    }

    private static IReadOnlyList<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

    private static string EscapeHeading(string heading) =>
        heading.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
}

public sealed class EpubManuscriptFormatter : IManuscriptExportFormatter
{
    private const string Language = "en";
    private const string Mimetype = "application/epub+zip";
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public ManuscriptExportFormat Format => ManuscriptExportFormat.Epub;
    public string FileExtension => ".epub";
    public string ContentType => Mimetype;

    public byte[] Render(ManuscriptExportDocument manuscript)
    {
        var xhtmlItems = BuildXhtmlItems(manuscript);
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            WriteEntry(archive, "mimetype", Mimetype, CompressionLevel.NoCompression, Encoding.ASCII);
            WriteEntry(archive, "META-INF/container.xml", RenderContainer(), CompressionLevel.SmallestSize, Utf8NoBom);
            WriteEntry(archive, "OEBPS/styles.css", RenderStylesheet(), CompressionLevel.SmallestSize, Utf8NoBom);
            WriteEntry(archive, "OEBPS/package.opf", RenderPackage(manuscript, xhtmlItems), CompressionLevel.SmallestSize, Utf8NoBom);
            WriteEntry(archive, "OEBPS/nav.xhtml", RenderNavigation(manuscript, xhtmlItems), CompressionLevel.SmallestSize, Utf8NoBom);

            foreach (var item in xhtmlItems)
                WriteEntry(archive, $"OEBPS/{item.Href}", item.Content, CompressionLevel.SmallestSize, Utf8NoBom);
        }

        return stream.ToArray();
    }

    private static List<EpubXhtmlItem> BuildXhtmlItems(ManuscriptExportDocument manuscript)
    {
        var items = new List<EpubXhtmlItem>
        {
            new("title", "title.xhtml", manuscript.ProjectName, RenderXhtmlPage(
                manuscript.ProjectName,
                $"<section class=\"title-page\"><h1>{Html(manuscript.ProjectName)}</h1></section>")),
        };

        var actIndex = 0;
        var chapterIndex = 0;
        foreach (var section in manuscript.Sections)
        {
            actIndex++;
            var actId = section.IsUnassigned ? "section-unassigned" : $"act-{actIndex.ToString(CultureInfo.InvariantCulture)}";
            items.Add(new EpubXhtmlItem(
                actId,
                $"{actId}.xhtml",
                section.Title,
                RenderXhtmlPage(section.Title, RenderActBody(section, manuscript.Options.IncludeSynopses))));

            foreach (var chapter in section.Chapters)
            {
                chapterIndex++;
                var chapterId = $"chapter-{chapterIndex.ToString(CultureInfo.InvariantCulture)}";
                items.Add(new EpubXhtmlItem(
                    chapterId,
                    $"{chapterId}.xhtml",
                    chapter.Title,
                    RenderXhtmlPage(chapter.Title, RenderChapterBody(chapter, manuscript.Options.IncludeSynopses))));
            }
        }

        return items;
    }

    private static string RenderActBody(ManuscriptExportSection section, bool includeSynopsis)
    {
        var sb = new StringBuilder();
        sb.Append("<section class=\"act-page\"><h1>").Append(Html(section.Title)).AppendLine("</h1>");
        if (includeSynopsis)
            AppendTextBlocks(sb, section.Synopsis, "synopsis");
        sb.AppendLine("</section>");
        return sb.ToString();
    }

    private static string RenderChapterBody(ManuscriptExportChapter chapter, bool includeSynopsis)
    {
        var sb = new StringBuilder();
        sb.Append("<article class=\"chapter-page\"><h1>").Append(Html(chapter.Title)).AppendLine("</h1>");
        if (includeSynopsis)
            AppendTextBlocks(sb, chapter.Synopsis, "synopsis");
        sb.AppendLine("<div class=\"chapter-body\">");
        AppendTextBlocks(sb, chapter.Body, "prose");
        sb.AppendLine("</div></article>");
        return sb.ToString();
    }

    private static string RenderXhtmlPage(string title, string body) =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <!DOCTYPE html>
        <html xmlns="http://www.w3.org/1999/xhtml" xml:lang="{Language}" lang="{Language}">
        <head>
          <title>{Html(title)}</title>
          <link rel="stylesheet" type="text/css" href="styles.css" />
        </head>
        <body>
        {body}
        </body>
        </html>
        """;

    private static string RenderContainer() =>
        """
        <?xml version="1.0" encoding="utf-8"?>
        <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
          <rootfiles>
            <rootfile full-path="OEBPS/package.opf" media-type="application/oebps-package+xml" />
          </rootfiles>
        </container>
        """;

    private static string RenderPackage(ManuscriptExportDocument manuscript, IReadOnlyList<EpubXhtmlItem> xhtmlItems)
    {
        var modified = manuscript.ExportedAtUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.AppendLine("""<?xml version="1.0" encoding="utf-8"?>""");
        sb.AppendLine("""<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="book-id">""");
        sb.AppendLine("""  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">""");
        sb.Append("    <dc:identifier id=\"book-id\">urn:uuid:").Append(manuscript.ProjectId).AppendLine("</dc:identifier>");
        sb.Append("    <dc:title>").Append(Html(manuscript.ProjectName)).AppendLine("</dc:title>");
        sb.Append("    <dc:language>").Append(Language).AppendLine("</dc:language>");
        sb.Append("    <meta property=\"dcterms:modified\">").Append(modified).AppendLine("</meta>");
        sb.AppendLine("  </metadata>");
        sb.AppendLine("  <manifest>");
        sb.AppendLine("""    <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav" />""");
        sb.AppendLine("""    <item id="style" href="styles.css" media-type="text/css" />""");
        foreach (var item in xhtmlItems)
        {
            sb.Append("    <item id=\"").Append(item.Id).Append("\" href=\"").Append(item.Href)
                .AppendLine("\" media-type=\"application/xhtml+xml\" />");
        }
        sb.AppendLine("  </manifest>");
        sb.AppendLine("  <spine>");
        foreach (var item in xhtmlItems)
            sb.Append("    <itemref idref=\"").Append(item.Id).AppendLine("\" />");
        sb.AppendLine("  </spine>");
        sb.AppendLine("</package>");
        return sb.ToString();
    }

    private static string RenderNavigation(ManuscriptExportDocument manuscript, IReadOnlyList<EpubXhtmlItem> xhtmlItems)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""<?xml version="1.0" encoding="utf-8"?>""");
        sb.AppendLine($"""<html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops" xml:lang="{Language}" lang="{Language}">""");
        sb.AppendLine("<head>");
        sb.Append("  <title>").Append(Html(manuscript.ProjectName)).AppendLine(" - Table of Contents</title>");
        sb.AppendLine("""  <link rel="stylesheet" type="text/css" href="styles.css" />""");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("""<nav epub:type="toc" id="toc">""");
        sb.AppendLine("<h1>Table of Contents</h1>");
        sb.AppendLine("<ol>");
        foreach (var item in xhtmlItems)
        {
            sb.Append("  <li><a href=\"").Append(item.Href).Append("\">")
                .Append(Html(item.Title)).AppendLine("</a></li>");
        }
        sb.AppendLine("</ol>");
        sb.AppendLine("</nav>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    private static string RenderStylesheet() =>
        """
        body {
          color: #172033;
          font-family: Georgia, "Times New Roman", serif;
          line-height: 1.55;
          margin: 5%;
        }

        h1 {
          color: #111827;
          font-family: Arial, sans-serif;
          font-size: 1.8em;
          line-height: 1.2;
          margin: 0 0 1em;
        }

        .title-page {
          min-height: 80vh;
          display: flex;
          align-items: center;
          justify-content: center;
          text-align: center;
        }

        .title-page h1 {
          font-size: 2.4em;
        }

        .synopsis {
          color: #475467;
          font-style: italic;
          margin: 0 0 1.25em;
        }

        .chapter-body p {
          margin: 0 0 0.9em;
        }
        """;

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

    private static void WriteEntry(ZipArchive archive, string name, string content, CompressionLevel compressionLevel, Encoding encoding)
    {
        var entry = archive.CreateEntry(name, compressionLevel);
        using var stream = entry.Open();
        var bytes = encoding.GetBytes(content);
        stream.Write(bytes);
    }

    private static string Html(string value) => WebUtility.HtmlEncode(value);

    private sealed record EpubXhtmlItem(string Id, string Href, string Title, string Content);
}
