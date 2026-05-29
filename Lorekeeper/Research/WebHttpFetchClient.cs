using System.Net;
using Microsoft.Extensions.Options;

namespace Lorekeeper.Research;

public interface IWebHttpFetchClient
{
    Task<WebHttpFetchResult> GetAsync(Uri uri, string acceptHeader, CancellationToken cancellationToken = default);
}

public sealed record WebHttpFetchResult(
    Uri RequestedUri,
    string FinalUrl,
    HttpStatusCode? StatusCode,
    string ContentType,
    string? Charset,
    byte[] Content,
    bool Success,
    bool Retryable,
    string Diagnostics);

public sealed class WebHttpFetchClient(
    IHttpClientFactory httpClientFactory,
    IWebFetchCoordinator coordinator,
    IOptions<WebResearchOptions> options,
    ILogger<WebHttpFetchClient> logger) : IWebHttpFetchClient
{
    public async Task<WebHttpFetchResult> GetAsync(
        Uri uri,
        string acceptHeader,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await coordinator.AcquireAsync(uri, cancellationToken);
        if (!lease.CanFetch)
            return Failed(uri, uri.ToString(), null, lease.Diagnostics, retryable: false);

        var client = httpClientFactory.CreateClient(nameof(WebHttpFetchClient));
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.Value.RequestTimeoutSeconds, 5, 180));
        var recorded = false;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", options.Value.UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", acceptHeader);

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? uri.ToString();
            var retryAfter = RetryAfter(response);
            coordinator.Record(lease, response.StatusCode, response.IsSuccessStatusCode, retryAfter);
            recorded = true;

            if (!response.IsSuccessStatusCode)
            {
                return Failed(
                    uri,
                    finalUrl,
                    response.StatusCode,
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim(),
                    IsRetryableStatusCode(response.StatusCode));
            }

            if (response.Content.Headers.ContentLength is long length && length > options.Value.MaxPageBytes)
            {
                return Failed(
                    uri,
                    finalUrl,
                    response.StatusCode,
                    $"Page is larger than the configured {options.Value.MaxPageBytes:N0} byte limit.",
                    retryable: false);
            }

            var bytes = await ReadLimitedBytesAsync(response, cancellationToken);
            return new WebHttpFetchResult(
                uri,
                finalUrl,
                response.StatusCode,
                response.Content.Headers.ContentType?.MediaType ?? string.Empty,
                response.Content.Headers.ContentType?.CharSet,
                bytes,
                Success: true,
                Retryable: false,
                Diagnostics: string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            if (!recorded)
                coordinator.Record(lease, null, success: false);
            logger.LogWarning(ex, "Timed out while reading webpage {Url}", uri);
            return Failed(uri, uri.ToString(), null, "The request timed out.", retryable: true);
        }
        catch (HttpRequestException ex)
        {
            if (!recorded)
                coordinator.Record(lease, null, success: false);
            logger.LogWarning(ex, "HTTP request failed while reading webpage {Url}", uri);
            return Failed(uri, uri.ToString(), null, ex.Message, retryable: true);
        }
        catch (IOException ex)
        {
            if (!recorded)
                coordinator.Record(lease, null, success: false);
            logger.LogWarning(ex, "Network stream failed while reading webpage {Url}", uri);
            return Failed(uri, uri.ToString(), null, ex.Message, retryable: true);
        }
        catch (InvalidOperationException ex)
        {
            if (!recorded)
                coordinator.Record(lease, null, success: false);
            logger.LogWarning(ex, "Failed to read webpage {Url}", uri);
            return Failed(uri, uri.ToString(), null, ex.Message, retryable: false);
        }
        catch (Exception ex)
        {
            if (!recorded)
                coordinator.Record(lease, null, success: false);
            logger.LogWarning(ex, "Failed to read webpage {Url}", uri);
            return Failed(uri, uri.ToString(), null, ex.Message, retryable: false);
        }
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

    private static bool IsRetryableStatusCode(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code is 408 or 425 or 500 or 502 or 503 or 504;
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null) return null;
        if (retryAfter.Delta is { } delta) return delta;
        if (retryAfter.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        return null;
    }

    private static WebHttpFetchResult Failed(
        Uri requestedUri,
        string finalUrl,
        HttpStatusCode? statusCode,
        string diagnostics,
        bool retryable) =>
        new(
            requestedUri,
            finalUrl,
            statusCode,
            ContentType: string.Empty,
            Charset: null,
            Content: [],
            Success: false,
            Retryable: retryable,
            Diagnostics: diagnostics);
}
