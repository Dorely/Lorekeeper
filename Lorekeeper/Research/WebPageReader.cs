using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Lorekeeper.Research;

public interface IWebPageReader
{
    Task<WebPageReadResult> ReadAsync(string url, CancellationToken cancellationToken = default);
}

public sealed record WebPageReadResult(
    string Url,
    string FinalUrl,
    string Title,
    string CanonicalUrl,
    string ContentType,
    string Text,
    string Excerpt,
    IReadOnlyList<WebPageLink> Links,
    bool Success,
    string Diagnostics);

public sealed record WebPageLink(string Url, string Text);

public sealed class HttpWebPageReader(
    IHttpClientFactory httpClientFactory,
    IOptions<WebResearchOptions> options,
    ILogger<HttpWebPageReader> logger) : IWebPageReader
{
    private static readonly Regex TitleRegex = new(@"<title[^>]*>(?<text>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex CanonicalRegex = new(@"<link\s+[^>]*rel\s*=\s*['""]?canonical['""]?[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex HrefRegex = new(@"href\s*=\s*(['""])(?<href>.*?)\1", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex AnchorRegex = new(@"<a\s+[^>]*href\s*=\s*(['""])(?<href>.*?)\1[^>]*>(?<text>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ScriptStyleRegex = new(@"<(script|style|noscript|template|svg|canvas)\b[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TagRegex = new(@"<[^>]+>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"[ \t\r\f\v]+", RegexOptions.Compiled);
    private static readonly Regex BlankLineRegex = new(@"\n{3,}", RegexOptions.Compiled);

    public async Task<WebPageReadResult> ReadAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return Failed(url, url, "Only absolute HTTP or HTTPS URLs can be read.");

        if (options.Value.BlockPrivateNetworkTargets && await IsPrivateNetworkTargetAsync(uri, cancellationToken))
            return Failed(url, url, "Private, loopback, and link-local network targets are blocked.");

        var client = httpClientFactory.CreateClient(nameof(HttpWebPageReader));
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.Value.RequestTimeoutSeconds, 5, 180));
        var maxAttempts = Math.Clamp(options.Value.RetryAttempts, 0, 5) + 1;
        var retryDelay = TimeSpan.FromMilliseconds(Math.Clamp(options.Value.RetryDelayMilliseconds, 0, 30_000));

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var attemptResult = await TryReadOnceAsync(client, url, uri, cancellationToken);
            if (attemptResult.Result.Success || !attemptResult.Retryable || attempt == maxAttempts)
                return attemptResult.Retryable && !attemptResult.Result.Success
                    ? WithAttemptDiagnostics(attemptResult.Result, attempt)
                    : attemptResult.Result;

            if (retryDelay > TimeSpan.Zero)
                await Task.Delay(retryDelay + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250)), cancellationToken);
        }

        return Failed(url, uri.ToString(), "Page read failed before an HTTP request was completed.");
    }

    private async Task<WebPageReadAttemptResult> TryReadOnceAsync(
        HttpClient client,
        string url,
        Uri uri,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", options.Value.UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,text/plain;q=0.8,*/*;q=0.2");

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? uri.ToString();
            if (!response.IsSuccessStatusCode)
                return new WebPageReadAttemptResult(
                    Failed(url, finalUrl, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim()),
                    IsRetryableStatusCode(response.StatusCode));

            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!IsReadableContentType(contentType))
                return new WebPageReadAttemptResult(Failed(url, finalUrl, $"Unsupported content type '{contentType}'."), Retryable: false);

            if (response.Content.Headers.ContentLength is long length && length > options.Value.MaxPageBytes)
                return new WebPageReadAttemptResult(
                    Failed(url, finalUrl, $"Page is larger than the configured {options.Value.MaxPageBytes:N0} byte limit."),
                    Retryable: false);

            var bytes = await ReadLimitedBytesAsync(response, cancellationToken);
            var raw = Decode(bytes, response.Content.Headers.ContentType?.CharSet);
            var isHtml = contentType.Contains("html", StringComparison.OrdinalIgnoreCase) || LooksLikeHtml(raw);
            var title = isHtml ? ExtractTitle(raw) : string.Empty;
            var canonical = isHtml ? ExtractCanonicalUrl(raw, finalUrl) : string.Empty;
            var links = isHtml ? ExtractLinks(raw, finalUrl) : [];
            var text = isHtml ? ExtractText(raw) : NormalizePlainText(raw);
            var excerpt = Truncate(text, 2_000);

            return new WebPageReadAttemptResult(
                new WebPageReadResult(url, finalUrl, title, canonical, contentType, text, excerpt, links, true, string.Empty),
                Retryable: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            logger.LogWarning(ex, "Timed out while reading webpage {Url}", url);
            return new WebPageReadAttemptResult(Failed(url, uri.ToString(), "The request timed out."), Retryable: true);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "HTTP request failed while reading webpage {Url}", url);
            return new WebPageReadAttemptResult(Failed(url, uri.ToString(), ex.Message), Retryable: true);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Network stream failed while reading webpage {Url}", url);
            return new WebPageReadAttemptResult(Failed(url, uri.ToString(), ex.Message), Retryable: true);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Failed to read webpage {Url}", url);
            return new WebPageReadAttemptResult(Failed(url, uri.ToString(), ex.Message), Retryable: false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read webpage {Url}", url);
            return new WebPageReadAttemptResult(Failed(url, uri.ToString(), ex.Message), Retryable: false);
        }
    }

    private static bool IsRetryableStatusCode(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code is 403 or 408 or 425 or 429 or 500 or 502 or 503 or 504;
    }

    private static WebPageReadResult WithAttemptDiagnostics(WebPageReadResult result, int attemptCount)
    {
        var diagnostics = result.Diagnostics.Trim().TrimEnd('.');
        var attemptLabel = attemptCount == 1 ? "1 attempt" : $"{attemptCount} attempts";
        return result with
        {
            Diagnostics = string.IsNullOrWhiteSpace(diagnostics)
                ? $"Failed after {attemptLabel}."
                : $"{diagnostics} after {attemptLabel}.",
        };
    }

    private async Task<byte[]> ReadLimitedBytesAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        var maxBytes = Math.Clamp(options.Value.MaxPageBytes, 16 * 1024, 20 * 1024 * 1024);

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (output.Length + read > maxBytes)
                throw new InvalidOperationException($"Page exceeded the configured {maxBytes:N0} byte limit.");
            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private async Task<bool> IsPrivateNetworkTargetAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (uri.IsLoopback) return true;
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (IPAddress.TryParse(uri.Host, out var parsed)) return IsPrivateAddress(parsed);

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(uri.Host, cancellationToken);
            return addresses.Length == 0 || addresses.Any(IsPrivateAddress);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.Equals(IPAddress.IPv6Loopback);

        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || bytes[0] == 127
            || bytes[0] == 0
            || bytes[0] == 169 && bytes[1] == 254
            || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
            || bytes[0] == 192 && bytes[1] == 168;
    }

    private static bool IsReadableContentType(string contentType) =>
        string.IsNullOrWhiteSpace(contentType)
        || contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
        || contentType.Contains("text/plain", StringComparison.OrdinalIgnoreCase)
        || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase);

    private static string Decode(byte[] bytes, string? charset)
    {
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try { return Encoding.GetEncoding(charset).GetString(bytes); }
            catch { }
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static bool LooksLikeHtml(string raw) =>
        raw.Contains("<html", StringComparison.OrdinalIgnoreCase)
        || raw.Contains("<body", StringComparison.OrdinalIgnoreCase)
        || raw.Contains("<p", StringComparison.OrdinalIgnoreCase);

    private static string ExtractTitle(string html)
    {
        var match = TitleRegex.Match(html);
        return match.Success ? NormalizeInline(WebUtility.HtmlDecode(match.Groups["text"].Value)) : string.Empty;
    }

    private static string ExtractCanonicalUrl(string html, string finalUrl)
    {
        var match = CanonicalRegex.Match(html);
        if (!match.Success) return string.Empty;
        var href = HrefRegex.Match(match.Value);
        if (!href.Success) return string.Empty;
        return ToAbsoluteUrl(finalUrl, WebUtility.HtmlDecode(href.Groups["href"].Value)) ?? string.Empty;
    }

    private IReadOnlyList<WebPageLink> ExtractLinks(string html, string baseUrl)
    {
        var links = new List<WebPageLink>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var maxLinks = Math.Clamp(options.Value.MaxLinksReturned, 0, 500);
        foreach (Match match in AnchorRegex.Matches(html))
        {
            if (links.Count >= maxLinks) break;
            var href = WebUtility.HtmlDecode(match.Groups["href"].Value).Trim();
            var absolute = ToAbsoluteUrl(baseUrl, href);
            if (absolute is null || !seen.Add(absolute)) continue;

            var text = NormalizeInline(ExtractText(match.Groups["text"].Value));
            links.Add(new WebPageLink(absolute, string.IsNullOrWhiteSpace(text) ? absolute : Truncate(text, 180)));
        }

        return links;
    }

    private static string ExtractText(string html)
    {
        var withoutScripts = ScriptStyleRegex.Replace(html, " ");
        var withBreaks = Regex.Replace(withoutScripts, @"</(p|div|section|article|header|footer|li|ul|ol|h[1-6]|br|tr)>\s*", "\n", RegexOptions.IgnoreCase);
        var withoutTags = TagRegex.Replace(withBreaks, " ");
        return NormalizePlainText(WebUtility.HtmlDecode(withoutTags));
    }

    private static string NormalizePlainText(string value)
    {
        var normalized = value.Replace("\r", "\n");
        normalized = WhitespaceRegex.Replace(normalized, " ");
        normalized = Regex.Replace(normalized, @" *\n *", "\n");
        normalized = BlankLineRegex.Replace(normalized, "\n\n");
        return normalized.Trim();
    }

    private static string NormalizeInline(string value) =>
        WhitespaceRegex.Replace(value.Replace('\n', ' ').Replace('\r', ' '), " ").Trim();

    private static string? ToAbsoluteUrl(string baseUrl, string href)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        if (href.StartsWith('#')) return null;
        if (!Uri.TryCreate(new Uri(baseUrl), href, out var uri)) return null;
        return uri.Scheme is "http" or "https" ? uri.ToString() : null;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max].TrimEnd() + "...";

    private static WebPageReadResult Failed(string url, string finalUrl, string diagnostics) =>
        new(url, finalUrl, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, [], false, diagnostics);

    private sealed record WebPageReadAttemptResult(WebPageReadResult Result, bool Retryable);
}
