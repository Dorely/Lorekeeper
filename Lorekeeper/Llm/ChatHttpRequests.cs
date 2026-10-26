namespace Lorekeeper.Llm;

/// <summary>Bounded transport recovery before a native chat response stream is exposed.</summary>
internal static class ChatHttpRequests
{
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, Func<HttpRequestMessage> createRequest,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = createRequest();
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (HttpRequestException) when (attempt < 2 && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt + 1), cancellationToken);
                continue;
            }
            if (attempt >= 2 || (int)response.StatusCode is not (408 or 429 or 500 or 502 or 503 or 504)) return response;
            var retryAfter = response.Headers.RetryAfter?.Delta
                ?? response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow
                ?? TimeSpan.FromSeconds(attempt + 1);
            response.Dispose();
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(retryAfter.TotalSeconds, 1, 30)), cancellationToken);
        }
    }
}
