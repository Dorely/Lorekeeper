using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Lorekeeper.Research;

public static class WebPageTextExtractor
{
    private static readonly Regex TitleRegex = new(@"<title[^>]*>(?<text>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex CanonicalRegex = new(@"<link\s+[^>]*rel\s*=\s*['""]?canonical['""]?[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex HrefRegex = new(@"href\s*=\s*(['""])(?<href>.*?)\1", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex AnchorRegex = new(@"<a\s+[^>]*href\s*=\s*(['""])(?<href>.*?)\1[^>]*>(?<text>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ImageRegex = new(@"<img\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex AttributeRegex = new(@"(?<name>[a-zA-Z_:][-a-zA-Z0-9_:.]*)\s*=\s*(?:(['""])(?<quoted>.*?)\2|(?<bare>[^\s>]+))", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex FigureRegex = new(@"<figure\b[^>]*>(?<html>.*?)</figure>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex FigCaptionRegex = new(@"<figcaption\b[^>]*>(?<html>.*?)</figcaption>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex MainRegex = new(@"<main\b[^>]*>(?<html>.*?)</main>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ArticleRegex = new(@"<article\b[^>]*>(?<html>.*?)</article>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex MediaWikiContentRegex = new(
        @"<div\b[^>]*class\s*=\s*['""][^'""]*\bmw-parser-output\b[^'""]*['""][^>]*>(?<html>.*?)(?:<!--\s*NewPP|<div\b[^>]*class\s*=\s*['""][^'""]*\bcatlinks\b)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ScriptStyleRegex = new(@"<(script|style|noscript|template|svg|canvas)\b[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex EditSectionRegex = new(@"<span\b[^>]*class\s*=\s*['""][^'""]*\bmw-editsection\b[^'""]*['""][^>]*>.*?</span>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TagRegex = new(@"<[^>]+>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"[ \t\r\f\v]+", RegexOptions.Compiled);
    private static readonly Regex BlankLineRegex = new(@"\n{3,}", RegexOptions.Compiled);

    public static string Decode(byte[] bytes, string? charset)
    {
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try { return Encoding.GetEncoding(charset).GetString(bytes); }
            catch { }
        }

        return Encoding.UTF8.GetString(bytes);
    }

    public static bool LooksLikeHtml(string raw) =>
        raw.Contains("<html", StringComparison.OrdinalIgnoreCase)
        || raw.Contains("<body", StringComparison.OrdinalIgnoreCase)
        || raw.Contains("<p", StringComparison.OrdinalIgnoreCase);

    public static string ExtractTitle(string html)
    {
        var match = TitleRegex.Match(html);
        return match.Success ? NormalizeInline(WebUtility.HtmlDecode(match.Groups["text"].Value)) : string.Empty;
    }

    public static string ExtractCanonicalUrl(string html, string finalUrl)
    {
        var match = CanonicalRegex.Match(html);
        if (!match.Success) return string.Empty;
        var href = HrefRegex.Match(match.Value);
        if (!href.Success) return string.Empty;
        return ToAbsoluteUrl(finalUrl, WebUtility.HtmlDecode(href.Groups["href"].Value)) ?? string.Empty;
    }

    public static string ExtractMainContentHtml(string html)
    {
        var mediaWiki = MediaWikiContentRegex.Match(html);
        if (mediaWiki.Success) return mediaWiki.Groups["html"].Value;

        var main = MainRegex.Match(html);
        if (main.Success) return main.Groups["html"].Value;

        var article = ArticleRegex.Match(html);
        return article.Success ? article.Groups["html"].Value : html;
    }

    public static IReadOnlyList<WebPageLink> ExtractLinks(string html, string baseUrl)
    {
        var links = new List<WebPageLink>();
        foreach (Match match in AnchorRegex.Matches(html))
        {
            var href = WebUtility.HtmlDecode(match.Groups["href"].Value).Trim();
            var absolute = ToAbsoluteUrl(baseUrl, href);
            if (absolute is null) continue;

            var text = NormalizeInline(ExtractText(match.Groups["text"].Value));
            links.Add(new WebPageLink(absolute, string.IsNullOrWhiteSpace(text) ? absolute : Truncate(text, 180)));
        }

        return links;
    }

    public static IReadOnlyList<WebPageImage> ExtractImages(string html, string baseUrl)
    {
        var captions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match figure in FigureRegex.Matches(html))
        {
            var captionMatch = FigCaptionRegex.Match(figure.Groups["html"].Value);
            var caption = captionMatch.Success ? NormalizeInline(ExtractText(captionMatch.Groups["html"].Value)) : string.Empty;
            if (string.IsNullOrWhiteSpace(caption)) continue;
            foreach (Match image in ImageRegex.Matches(figure.Groups["html"].Value))
            {
                var attrs = ReadAttributes(image.Groups["attrs"].Value);
                var source = BestImageSource(attrs);
                var absolute = source is null ? null : ToAbsoluteUrl(baseUrl, source);
                if (absolute is not null) captions[absolute] = Truncate(caption, 500);
            }
        }

        var result = new List<WebPageImage>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in ImageRegex.Matches(html))
        {
            var attrs = ReadAttributes(match.Groups["attrs"].Value);
            var source = BestImageSource(attrs);
            var absolute = source is null ? null : ToAbsoluteUrl(baseUrl, source);
            if (absolute is null || !seen.Add(absolute)) continue;
            attrs.TryGetValue("alt", out var alt);
            attrs.TryGetValue("title", out var title);
            result.Add(new WebPageImage(
                absolute,
                NormalizeInline(WebUtility.HtmlDecode(alt ?? string.Empty)),
                captions.GetValueOrDefault(absolute, NormalizeInline(WebUtility.HtmlDecode(title ?? string.Empty)))));
        }
        return result;
    }

    private static Dictionary<string, string> ReadAttributes(string value) => AttributeRegex.Matches(value)
        .Cast<Match>()
        .GroupBy(match => match.Groups["name"].Value, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(
            group => group.Key,
            group => WebUtility.HtmlDecode(group.Last().Groups["quoted"].Success ? group.Last().Groups["quoted"].Value : group.Last().Groups["bare"].Value),
            StringComparer.OrdinalIgnoreCase);

    private static string? BestImageSource(IReadOnlyDictionary<string, string> attrs)
    {
        if (attrs.TryGetValue("srcset", out var srcset) && !string.IsNullOrWhiteSpace(srcset))
        {
            var candidate = srcset.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => value.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0])
                .LastOrDefault();
            if (!string.IsNullOrWhiteSpace(candidate)) return candidate;
        }
        foreach (var key in new[] { "src", "data-src", "data-original" })
            if (attrs.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) return value;
        return null;
    }

    public static string ExtractText(string html)
    {
        var withoutScripts = ScriptStyleRegex.Replace(html, " ");
        var withoutEditLinks = EditSectionRegex.Replace(withoutScripts, " ");
        var withBreaks = Regex.Replace(withoutEditLinks, @"</(p|div|section|article|header|footer|li|ul|ol|h[1-6]|br|tr)>\s*", "\n", RegexOptions.IgnoreCase);
        var withoutTags = TagRegex.Replace(withBreaks, " ");
        return NormalizePlainText(WebUtility.HtmlDecode(withoutTags));
    }

    public static string NormalizePlainText(string value)
    {
        var normalized = value.Replace("\r", "\n");
        normalized = WhitespaceRegex.Replace(normalized, " ");
        normalized = Regex.Replace(normalized, @" *\n *", "\n");
        normalized = BlankLineRegex.Replace(normalized, "\n\n");
        return normalized.Trim();
    }

    public static string NormalizeInline(string value) =>
        WhitespaceRegex.Replace(value.Replace('\n', ' ').Replace('\r', ' '), " ").Trim();

    public static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max].TrimEnd() + "...";

    private static string? ToAbsoluteUrl(string baseUrl, string href)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        if (href.StartsWith('#')) return null;
        if (!Uri.TryCreate(new Uri(baseUrl), href, out var uri)) return null;
        return uri.Scheme is "http" or "https" ? uri.ToString() : null;
    }
}
