using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Lorekeeper.Images;

namespace Lorekeeper.Research;

public interface IWebPageReader
{
    Task<WebPageReadResult> ReadAsync(string url, CancellationToken cancellationToken = default);
    Task<WebImageReadResult> ReadImageAsync(string url, CancellationToken cancellationToken = default);
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
    IReadOnlyList<WebPageImage> Images,
    bool Success,
    string Diagnostics,
    int? StatusCode = null,
    string SourceKind = "");

public sealed record WebPageLink(string Url, string Text);
public sealed record WebPageImage(string Url, string AltText, string Caption);
public sealed record WebImageReadResult(string Url, string FinalUrl, string ContentType, byte[] Data, bool Success, string Diagnostics);

public sealed class HttpWebPageReader(
    IWebHttpFetchClient fetchClient,
    IEnumerable<IWebPageSourceReader> sourceReaders,
    IWebLinkPolicy linkPolicy,
    IWebRobotsPolicy robots,
    IOptions<WebResearchOptions> options) : IWebPageReader
{
    public async Task<WebImageReadResult> ReadImageAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return new(url, url, string.Empty, [], false, "Only absolute HTTP or HTTPS image URLs can be read.");
        var rejection = await ValidateTargetAsync(uri, cancellationToken);
        if (rejection is not null) return new(url, url, string.Empty, [], false, rejection);

        var result = await fetchClient.GetAsync(uri, "image/png,image/jpeg,image/webp;q=0.9,*/*;q=0.1", cancellationToken);
        if (!result.Success) return new(url, result.FinalUrl, result.ContentType, [], false, result.Diagnostics);
        if (!Uri.TryCreate(result.FinalUrl, UriKind.Absolute, out var finalUri))
            return new(url, result.FinalUrl, result.ContentType, [], false, "The redirected image URL is invalid.");
        var finalRejection = await ValidateTargetAsync(finalUri, cancellationToken);
        if (finalRejection is not null)
            return new(url, result.FinalUrl, result.ContentType, [], false, finalRejection);
        try
        {
            var normalized = ProjectImageBinary.Normalize(result.Content, result.ContentType, Path.GetFileName(finalUri.LocalPath));
            return new(url, result.FinalUrl, normalized.ContentType, normalized.Data, true, string.Empty);
        }
        catch (Exception ex)
        {
            return new(url, result.FinalUrl, result.ContentType, [], false, ex.Message);
        }
    }

    public async Task<WebPageReadResult> ReadAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return WebPageReadResultFactory.Failed(url, url, "Only absolute HTTP or HTTPS URLs can be read.");

        if (!linkPolicy.CanFetchDirectUrl(uri, out var rejectedReason))
            return WebPageReadResultFactory.Failed(url, WebLinkPolicy.Normalize(uri), rejectedReason);

        if (options.Value.BlockPrivateNetworkTargets && await IsPrivateNetworkTargetAsync(uri, cancellationToken))
            return WebPageReadResultFactory.Failed(url, url, "Private, loopback, and link-local network targets are blocked.");

        var robotDecision = await robots.IsAllowedAsync(uri, cancellationToken);
        if (!robotDecision.Allowed)
            return WebPageReadResultFactory.Failed(url, WebLinkPolicy.Normalize(uri), robotDecision.Diagnostics);

        foreach (var sourceReader in sourceReaders)
        {
            if (!sourceReader.CanRead(uri)) continue;

            var sourceResult = await sourceReader.ReadAsync(url, uri, cancellationToken);
            if (sourceResult is null) continue;
            if (sourceResult.Result.Success || !sourceResult.AllowFallback)
                return sourceResult.Result;
        }

        var clientRetryAttempts = Math.Clamp(options.Value.RetryAttempts, 0, 5);
        var maxAttempts = clientRetryAttempts + 1;
        var retryDelay = TimeSpan.FromMilliseconds(Math.Clamp(options.Value.RetryDelayMilliseconds, 0, 30_000));

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var attemptResult = await ReadGenericOnceAsync(url, uri, cancellationToken);
            if (attemptResult.Result.Success || !attemptResult.Retryable || attempt == maxAttempts)
                return attemptResult.Retryable && !attemptResult.Result.Success
                    ? WithAttemptDiagnostics(attemptResult.Result, attempt)
                    : attemptResult.Result;

            if (retryDelay > TimeSpan.Zero)
                await Task.Delay(retryDelay + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250)), cancellationToken);
        }

        return WebPageReadResultFactory.Failed(url, uri.ToString(), "Page read failed before an HTTP request was completed.");
    }

    private async Task<WebPageReadAttemptResult> ReadGenericOnceAsync(
        string requestedUrl,
        Uri uri,
        CancellationToken cancellationToken)
    {
        var result = await fetchClient.GetAsync(uri, "text/html,application/xhtml+xml,text/plain;q=0.8,*/*;q=0.2", cancellationToken);
        if (!result.Success)
        {
            return new WebPageReadAttemptResult(
                WebPageReadResultFactory.Failed(
                    requestedUrl,
                    result.FinalUrl,
                    result.Diagnostics,
                    result.StatusCode is null ? null : (int)result.StatusCode,
                    sourceKind: "http"),
                result.Retryable);
        }

        if (!IsReadableContentType(result.ContentType))
        {
            return new WebPageReadAttemptResult(
                WebPageReadResultFactory.Failed(
                    requestedUrl,
                    result.FinalUrl,
                    $"Unsupported content type '{result.ContentType}'.",
                    result.StatusCode is null ? null : (int)result.StatusCode,
                    sourceKind: "http"),
                Retryable: false);
        }

        var raw = WebPageTextExtractor.Decode(result.Content, result.Charset);
        var isHtml = result.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase)
            || WebPageTextExtractor.LooksLikeHtml(raw);
        var title = isHtml ? WebPageTextExtractor.ExtractTitle(raw) : string.Empty;
        var canonical = isHtml ? WebPageTextExtractor.ExtractCanonicalUrl(raw, result.FinalUrl) : string.Empty;
        var contentHtml = isHtml ? WebPageTextExtractor.ExtractMainContentHtml(raw) : string.Empty;
        var text = isHtml ? WebPageTextExtractor.ExtractText(contentHtml) : WebPageTextExtractor.NormalizePlainText(raw);
        var rawLinks = isHtml ? WebPageTextExtractor.ExtractLinks(contentHtml, result.FinalUrl) : [];
        var images = isHtml ? WebPageTextExtractor.ExtractImages(contentHtml, result.FinalUrl) : [];
        var links = linkPolicy.FilterAndPrioritizeLinks(
            rawLinks,
            result.FinalUrl,
            sameDomainOnly: false,
            options.Value.MaxLinksReturned,
            out _);
        var excerpt = WebPageTextExtractor.Truncate(text, 2_000);

        return new WebPageReadAttemptResult(
            new WebPageReadResult(
                requestedUrl,
                result.FinalUrl,
                title,
                canonical,
                result.ContentType,
                text,
                excerpt,
                links,
                images,
                Success: true,
                Diagnostics: string.Empty,
                StatusCode: result.StatusCode is null ? null : (int)result.StatusCode,
                SourceKind: "http"),
            Retryable: false);
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

    private async Task<string?> ValidateTargetAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!linkPolicy.CanFetchDirectUrl(uri, out var rejectedReason)) return rejectedReason;
        if (options.Value.BlockPrivateNetworkTargets && await IsPrivateNetworkTargetAsync(uri, cancellationToken))
            return "Private, loopback, and link-local network targets are blocked.";
        var robotDecision = await robots.IsAllowedAsync(uri, cancellationToken);
        return robotDecision.Allowed ? null : robotDecision.Diagnostics;
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

    private sealed record WebPageReadAttemptResult(WebPageReadResult Result, bool Retryable);
}

public static class WebPageReadResultFactory
{
    public static WebPageReadResult Failed(
        string url,
        string finalUrl,
        string diagnostics,
        int? statusCode = null,
        string sourceKind = "") =>
        new(
            url,
            finalUrl,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            [],
            [],
            Success: false,
            diagnostics,
            statusCode,
            sourceKind);
}
