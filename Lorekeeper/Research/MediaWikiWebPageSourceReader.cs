using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Lorekeeper.Research;

public interface IWebPageSourceReader
{
    bool CanRead(Uri uri);
    Task<WebPageSourceReadResult?> ReadAsync(string requestedUrl, Uri uri, CancellationToken cancellationToken = default);
}

public sealed record WebPageSourceReadResult(WebPageReadResult Result, bool Retryable, bool AllowFallback);

public sealed class MediaWikiWebPageSourceReader(
    IWebHttpFetchClient fetchClient,
    IWebLinkPolicy linkPolicy,
    IWebRobotsPolicy robots,
    IOptions<WebResearchOptions> options,
    ILogger<MediaWikiWebPageSourceReader> logger) : IWebPageSourceReader
{
    private const string SourceKind = "mediawiki-api";

    public bool CanRead(Uri uri) =>
        TryGetWikiTitle(uri, out _) && linkPolicy.CanFetchDirectUrl(uri, out _);

    public async Task<WebPageSourceReadResult?> ReadAsync(
        string requestedUrl,
        Uri uri,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetWikiTitle(uri, out var title))
            return null;

        var extract = await ReadExtractApiAsync(requestedUrl, uri, title, cancellationToken);
        if (extract is { Result.Success: true }) return extract;
        if (extract is { AllowFallback: false }) return extract;

        var parse = await ReadParseApiAsync(requestedUrl, uri, title, cancellationToken);
        if (parse is { Result.Success: true }) return parse;

        return parse ?? extract;
    }

    private async Task<WebPageSourceReadResult> ReadExtractApiAsync(
        string requestedUrl,
        Uri pageUri,
        string title,
        CancellationToken cancellationToken)
    {
        var apiUri = BuildApiUri(pageUri, new Dictionary<string, string>
        {
            ["action"] = "query",
            ["titles"] = title,
            ["redirects"] = "1",
            ["prop"] = "extracts|links",
            ["plnamespace"] = "0",
            ["pllimit"] = Math.Clamp(options.Value.MaxLinksReturned, 1, 500).ToString(),
            ["explaintext"] = "1",
            ["format"] = "json",
            ["formatversion"] = "2",
        });

        var robotDecision = await robots.IsAllowedAsync(apiUri, cancellationToken);
        if (!robotDecision.Allowed)
            return Failure(requestedUrl, pageUri.ToString(), robotDecision.Diagnostics, retryable: false, allowFallback: true);

        var result = await fetchClient.GetAsync(apiUri, "application/json", cancellationToken);
        if (!result.Success)
            return Failure(requestedUrl, result.FinalUrl, result.Diagnostics, result.Retryable, allowFallback: !IsCooldown(result));

        try
        {
            var json = WebPageTextExtractor.Decode(result.Content, result.Charset);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("query", out var query)
                || !query.TryGetProperty("pages", out var pages)
                || pages.ValueKind != JsonValueKind.Array
                || pages.GetArrayLength() == 0)
            {
                return Failure(requestedUrl, pageUri.ToString(), "MediaWiki API did not return a page.", retryable: false, allowFallback: true);
            }

            var page = pages[0];
            if (page.TryGetProperty("missing", out _))
                return Failure(requestedUrl, pageUri.ToString(), "MediaWiki page was not found.", retryable: false, allowFallback: false);

            var finalTitle = ReadString(page, "title") ?? title;
            var text = WebPageTextExtractor.NormalizePlainText(ReadString(page, "extract") ?? string.Empty);
            if (string.IsNullOrWhiteSpace(text))
                return Failure(requestedUrl, pageUri.ToString(), "MediaWiki extract API returned no readable text.", retryable: false, allowFallback: true);

            var finalUrl = BuildArticleUrl(pageUri, finalTitle);
            var rawLinks = ReadQueryLinks(page, pageUri);
            var links = linkPolicy.FilterAndPrioritizeLinks(
                rawLinks,
                finalUrl,
                sameDomainOnly: false,
                options.Value.MaxLinksReturned,
                out _);

            return Success(requestedUrl, finalUrl, finalTitle, text, links, result.StatusCode is null ? null : (int)result.StatusCode);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse MediaWiki extract API response for {Url}", pageUri);
            return Failure(requestedUrl, pageUri.ToString(), "MediaWiki API returned invalid JSON.", retryable: false, allowFallback: true);
        }
    }

    private async Task<WebPageSourceReadResult> ReadParseApiAsync(
        string requestedUrl,
        Uri pageUri,
        string title,
        CancellationToken cancellationToken)
    {
        var apiUri = BuildApiUri(pageUri, new Dictionary<string, string>
        {
            ["action"] = "parse",
            ["page"] = title,
            ["redirects"] = "1",
            ["prop"] = "text|links|displaytitle",
            ["format"] = "json",
            ["formatversion"] = "2",
        });

        var robotDecision = await robots.IsAllowedAsync(apiUri, cancellationToken);
        if (!robotDecision.Allowed)
            return Failure(requestedUrl, pageUri.ToString(), robotDecision.Diagnostics, retryable: false, allowFallback: true);

        var result = await fetchClient.GetAsync(apiUri, "application/json", cancellationToken);
        if (!result.Success)
            return Failure(requestedUrl, result.FinalUrl, result.Diagnostics, result.Retryable, allowFallback: !IsCooldown(result));

        try
        {
            var json = WebPageTextExtractor.Decode(result.Content, result.Charset);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                var message = ReadString(error, "info") ?? "MediaWiki parse API returned an error.";
                return Failure(requestedUrl, pageUri.ToString(), message, retryable: false, allowFallback: true);
            }

            if (!doc.RootElement.TryGetProperty("parse", out var parse))
                return Failure(requestedUrl, pageUri.ToString(), "MediaWiki parse API did not return parsed content.", retryable: false, allowFallback: true);

            var finalTitle = ReadString(parse, "title") ?? title;
            var html = ReadString(parse, "text") ?? string.Empty;
            var text = WebPageTextExtractor.ExtractText(WebPageTextExtractor.ExtractMainContentHtml(html));
            if (string.IsNullOrWhiteSpace(text))
                return Failure(requestedUrl, pageUri.ToString(), "MediaWiki parse API returned no readable text.", retryable: false, allowFallback: true);

            var finalUrl = BuildArticleUrl(pageUri, finalTitle);
            var rawLinks = ReadParseLinks(parse, pageUri);
            var links = linkPolicy.FilterAndPrioritizeLinks(
                rawLinks,
                finalUrl,
                sameDomainOnly: false,
                options.Value.MaxLinksReturned,
                out _);

            return Success(requestedUrl, finalUrl, finalTitle, text, links, result.StatusCode is null ? null : (int)result.StatusCode);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse MediaWiki parse API response for {Url}", pageUri);
            return Failure(requestedUrl, pageUri.ToString(), "MediaWiki API returned invalid JSON.", retryable: false, allowFallback: true);
        }
    }

    private static IReadOnlyList<WebPageLink> ReadQueryLinks(JsonElement page, Uri pageUri)
    {
        if (!page.TryGetProperty("links", out var linksElement) || linksElement.ValueKind != JsonValueKind.Array)
            return [];

        return linksElement
            .EnumerateArray()
            .Where(link => ReadInt(link, "ns") == 0)
            .Select(link => ReadString(link, "title"))
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Select(title => new WebPageLink(BuildArticleUrl(pageUri, title!), title!))
            .ToList();
    }

    private static IReadOnlyList<WebPageLink> ReadParseLinks(JsonElement parse, Uri pageUri)
    {
        if (!parse.TryGetProperty("links", out var linksElement) || linksElement.ValueKind != JsonValueKind.Array)
            return [];

        return linksElement
            .EnumerateArray()
            .Where(link => ReadInt(link, "ns") == 0)
            .Select(link => ReadString(link, "title"))
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Select(title => new WebPageLink(BuildArticleUrl(pageUri, title!), title!))
            .ToList();
    }

    private static WebPageSourceReadResult Success(
        string requestedUrl,
        string finalUrl,
        string title,
        string text,
        IReadOnlyList<WebPageLink> links,
        int? statusCode)
    {
        var excerpt = WebPageTextExtractor.Truncate(text, 2_000);
        return new WebPageSourceReadResult(
            new WebPageReadResult(
                requestedUrl,
                finalUrl,
                title,
                finalUrl,
                "application/json",
                text,
                excerpt,
                links,
                Success: true,
                Diagnostics: string.Empty,
                StatusCode: statusCode,
                SourceKind: SourceKind),
            Retryable: false,
            AllowFallback: false);
    }

    private static WebPageSourceReadResult Failure(
        string requestedUrl,
        string finalUrl,
        string diagnostics,
        bool retryable,
        bool allowFallback) =>
        new(
            WebPageReadResultFactory.Failed(
                requestedUrl,
                finalUrl,
                diagnostics,
                sourceKind: SourceKind),
            retryable,
            allowFallback);

    private static bool IsCooldown(WebHttpFetchResult result) =>
        result.Diagnostics.Contains("cooling down", StringComparison.OrdinalIgnoreCase);

    private static Uri BuildApiUri(Uri pageUri, IReadOnlyDictionary<string, string> query)
    {
        var builder = new UriBuilder(pageUri.Scheme, pageUri.Host, pageUri.IsDefaultPort ? -1 : pageUri.Port, "api.php")
        {
            Query = string.Join("&", query.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")),
        };
        return builder.Uri;
    }

    private static string BuildArticleUrl(Uri pageUri, string title)
    {
        var escaped = Uri.EscapeDataString(title.Replace(' ', '_')).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);
        var builder = new UriBuilder(pageUri.Scheme, pageUri.Host, pageUri.IsDefaultPort ? -1 : pageUri.Port, $"wiki/{escaped}");
        return WebLinkPolicy.Normalize(builder.Uri);
    }

    private static bool TryGetWikiTitle(Uri uri, out string title)
    {
        title = string.Empty;
        const string wikiPrefix = "/wiki/";
        if (!uri.AbsolutePath.StartsWith(wikiPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(uri.Query)) return false;

        title = Uri.UnescapeDataString(uri.AbsolutePath[wikiPrefix.Length..]).Replace('_', ' ').Trim();
        return !string.IsNullOrWhiteSpace(title);
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? ReadInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value)
            ? value
            : null;
}
