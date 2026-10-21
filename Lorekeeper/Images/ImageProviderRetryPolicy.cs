namespace Lorekeeper.Images;

/// <summary>The retry rules every image adapter shares; adapters add their own provider-specific hints.</summary>
public static class ImageProviderRetryPolicy
{
    public static bool IsTransient(ProjectImageProviderException error) =>
        error.ErrorKind is "rate_limit" or "server_error" or "transport_error";

    /// <summary>Exponential backoff from 250 ms, capped at five seconds, for the one-based failed attempt.</summary>
    public static TimeSpan Backoff(int failedAttempt) =>
        TimeSpan.FromMilliseconds(Math.Min(5000, 250 * Math.Pow(2, Math.Max(0, failedAttempt - 1))));

    /// <summary>Retry transient errors after the provider's Retry-After, else backoff.</summary>
    public static TimeSpan? Default(Exception error, int failedAttempt)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is not ProjectImageProviderException providerError)
                continue;
            if (!IsTransient(providerError))
                return null;
            return providerError.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero
                ? retryAfter
                : Backoff(failedAttempt);
        }

        return null;
    }
}
