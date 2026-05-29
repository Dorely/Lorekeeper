using Microsoft.Extensions.Options;

namespace Lorekeeper.Research;

public interface IWebLinkPolicy
{
    bool TryNormalizeHttpUrl(string url, out string normalizedUrl, out string hostKey);
    bool CanFetchDirectUrl(Uri uri, out string reason);
    IReadOnlyList<WebPageLink> FilterAndPrioritizeLinks(
        IEnumerable<WebPageLink> links,
        string sourceUrl,
        bool sameDomainOnly,
        int maxLinks,
        out int skippedCount);
}

public sealed class WebLinkPolicy(IOptionsMonitor<WebResearchOptions> options) : IWebLinkPolicy
{
    private static readonly HashSet<string> SkippedWikiNamespaces = new(StringComparer.OrdinalIgnoreCase)
    {
        "Special",
        "Talk",
        "User",
        "User talk",
        "File",
        "Category",
        "Help",
        "Template",
        "Module",
        "MediaWiki",
    };

    private static readonly HashSet<string> SkippedActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "delete",
        "dbquery",
        "edit",
        "history",
        "info",
        "markpatrolled",
        "protect",
        "purge",
        "rollback",
        "submit",
        "unwatch",
        "watch",
    };

    private static readonly string[] SkippedFileExtensions =
    [
        ".7z",
        ".avi",
        ".bmp",
        ".css",
        ".csv",
        ".doc",
        ".docx",
        ".gif",
        ".gz",
        ".ico",
        ".jpeg",
        ".jpg",
        ".js",
        ".json",
        ".mp3",
        ".mp4",
        ".pdf",
        ".png",
        ".rar",
        ".svg",
        ".webm",
        ".webp",
        ".xls",
        ".xlsx",
        ".zip",
    ];

    private static readonly string[] SkippedTextLabels =
    [
        "create account",
        "log in",
        "page",
        "discussion",
        "sign up to edit",
        "view source",
        "view history",
        "recent changes",
        "random article",
        "what links here",
        "related changes",
        "new page",
        "special pages",
        "page information",
        "database data",
        "cite this page",
    ];

    public bool TryNormalizeHttpUrl(string url, out string normalizedUrl, out string hostKey)
    {
        normalizedUrl = string.Empty;
        hostKey = string.Empty;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "https")) return false;

        normalizedUrl = Normalize(uri);
        hostKey = HostKey(uri);
        return true;
    }

    public bool CanFetchDirectUrl(Uri uri, out string reason)
    {
        if (uri.Scheme is not ("http" or "https"))
        {
            reason = "Only HTTP and HTTPS URLs can be read.";
            return false;
        }

        if (HasSkippedAction(uri, out reason)) return false;
        if (IsSkippedWikiNamespace(uri, out reason)) return false;
        if (HasSkippedFileExtension(uri, out reason)) return false;

        reason = string.Empty;
        return true;
    }

    public IReadOnlyList<WebPageLink> FilterAndPrioritizeLinks(
        IEnumerable<WebPageLink> links,
        string sourceUrl,
        bool sameDomainOnly,
        int maxLinks,
        out int skippedCount)
    {
        skippedCount = 0;
        var normalizedSourceHost = Uri.TryCreate(sourceUrl, UriKind.Absolute, out var sourceUri)
            ? HostKey(sourceUri)
            : string.Empty;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<PrioritizedLink>();
        var linkList = links.ToList();
        var contentStartIndex = FindLikelyContentStart(linkList);

        foreach (var pair in linkList.Select((link, index) => new { link, index }))
        {
            if (!TryNormalizeHttpUrl(pair.link.Url, out var normalizedUrl, out var hostKey)
                || sameDomainOnly && !string.Equals(hostKey, normalizedSourceHost, StringComparison.OrdinalIgnoreCase)
                || !Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri)
                || !CanFollowLink(uri, pair.link.Text, out _)
                || !seen.Add(normalizedUrl))
            {
                skippedCount++;
                continue;
            }

            candidates.Add(new PrioritizedLink(
                new WebPageLink(normalizedUrl, pair.link.Text),
                LinkPriority(uri) + (pair.index < contentStartIndex ? 1_000 : 0),
                pair.index));
        }

        var limit = Math.Clamp(maxLinks, 0, Math.Clamp(options.CurrentValue.MaxLinksReturned, 0, 500));
        return candidates
            .OrderBy(candidate => candidate.Priority)
            .ThenBy(candidate => candidate.OriginalIndex)
            .Take(limit)
            .Select(candidate => candidate.Link)
            .ToList();
    }

    private static int FindLikelyContentStart(IReadOnlyList<WebPageLink> links)
    {
        var markers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "what links here",
            "related changes",
            "new page",
            "special pages",
            "page information",
            "database data",
            "cite this page",
        };

        var markerIndex = -1;
        for (var index = 0; index < links.Count; index++)
        {
            var text = WebPageTextExtractor.NormalizeInline(links[index].Text);
            if (markers.Contains(text))
                markerIndex = index;
        }

        return markerIndex < 0 ? 0 : markerIndex + 1;
    }

    public static string HostKey(Uri uri)
    {
        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? uri.Host[4..]
            : uri.Host;
        return host.ToLowerInvariant();
    }

    public static string Normalize(Uri uri)
    {
        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        if ((builder.Scheme == "http" && builder.Port == 80) ||
            (builder.Scheme == "https" && builder.Port == 443))
        {
            builder.Port = -1;
        }

        return builder.Uri.ToString().TrimEnd('/');
    }

    private static bool CanFollowLink(Uri uri, string text, out string reason)
    {
        if (!HasUsefulPath(uri, out reason)) return false;
        if (HasSkippedText(text, out reason)) return false;
        if (HasSkippedAction(uri, out reason)) return false;
        if (IsSkippedWikiNamespace(uri, out reason)) return false;
        if (HasSkippedFileExtension(uri, out reason)) return false;

        reason = string.Empty;
        return true;
    }

    private static bool HasUsefulPath(Uri uri, out string reason)
    {
        if (string.IsNullOrWhiteSpace(uri.AbsolutePath) || uri.AbsolutePath == "/")
        {
            reason = "Home/root links are not specific source pages.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool HasSkippedText(string text, out string reason)
    {
        var normalized = WebPageTextExtractor.NormalizeInline(text).ToLowerInvariant();
        if (SkippedTextLabels.Contains(normalized))
        {
            reason = $"Navigation link text '{text}' is skipped.";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static bool HasSkippedAction(Uri uri, out string reason)
    {
        var action = QueryValue(uri.Query, "action");
        if (!string.IsNullOrWhiteSpace(action) && SkippedActions.Contains(action))
        {
            reason = $"MediaWiki action '{action}' is skipped.";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static string? QueryValue(string query, string key)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var trimmed = query[0] == '?' ? query[1..] : query;
        foreach (var part in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);
            var name = equals < 0 ? part : part[..equals];
            if (!Uri.UnescapeDataString(name).Equals(key, StringComparison.OrdinalIgnoreCase)) continue;

            var value = equals < 0 ? string.Empty : part[(equals + 1)..];
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }

        return null;
    }

    private static bool IsSkippedWikiNamespace(Uri uri, out string reason)
    {
        var title = WikiTitle(uri);
        if (string.IsNullOrWhiteSpace(title))
        {
            reason = string.Empty;
            return false;
        }

        var colonIndex = title.IndexOf(':', StringComparison.Ordinal);
        if (colonIndex <= 0)
        {
            reason = string.Empty;
            return false;
        }

        var prefix = title[..colonIndex].Replace('_', ' ').Trim();
        if (SkippedWikiNamespaces.Contains(prefix) || prefix.EndsWith(" Wiki", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"MediaWiki namespace '{prefix}' is skipped.";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static bool HasSkippedFileExtension(Uri uri, out string reason)
    {
        var path = uri.AbsolutePath;
        if (SkippedFileExtensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
        {
            reason = "Binary/static asset links are skipped.";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static int LinkPriority(Uri uri)
    {
        if (IsMediaWikiArticle(uri)) return 0;
        if (uri.AbsolutePath.Count(character => character == '/') <= 2) return 20;
        return 10;
    }

    private static bool IsMediaWikiArticle(Uri uri) =>
        !string.IsNullOrWhiteSpace(WikiTitle(uri));

    private static string WikiTitle(Uri uri)
    {
        const string wikiPrefix = "/wiki/";
        var path = uri.AbsolutePath;
        if (!path.StartsWith(wikiPrefix, StringComparison.OrdinalIgnoreCase)) return string.Empty;

        var title = path[wikiPrefix.Length..];
        return string.IsNullOrWhiteSpace(title)
            ? string.Empty
            : Uri.UnescapeDataString(title).Replace('_', ' ');
    }

    private sealed record PrioritizedLink(WebPageLink Link, int Priority, int OriginalIndex);
}
