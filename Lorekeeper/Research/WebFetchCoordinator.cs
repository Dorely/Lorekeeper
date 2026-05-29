using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Options;

namespace Lorekeeper.Research;

public interface IWebFetchCoordinator
{
    Task<WebFetchLease> AcquireAsync(Uri uri, CancellationToken cancellationToken = default);
    void Record(WebFetchLease lease, HttpStatusCode? statusCode, bool success, TimeSpan? retryAfter = null);
}

public sealed class WebFetchCoordinator(IOptionsMonitor<WebResearchOptions> options) : IWebFetchCoordinator
{
    private readonly ConcurrentDictionary<string, WebFetchHostState> _hosts = new(StringComparer.OrdinalIgnoreCase);

    public async Task<WebFetchLease> AcquireAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        var hostKey = WebLinkPolicy.HostKey(uri);
        var state = _hosts.GetOrAdd(hostKey, _ => new WebFetchHostState());
        var acquired = false;

        try
        {
            await state.Semaphore.WaitAsync(cancellationToken);
            acquired = true;

            var now = DateTimeOffset.UtcNow;
            DateTimeOffset cooldownUntil;
            DateTimeOffset nextAllowedAt;
            lock (state.Gate)
            {
                cooldownUntil = state.CooldownUntil;
                nextAllowedAt = state.NextAllowedAt;
            }

            if (cooldownUntil > now)
            {
                state.Semaphore.Release();
                acquired = false;
                return WebFetchLease.Blocked(
                    hostKey,
                    $"Host '{hostKey}' is cooling down until {cooldownUntil:u} after recent blocked or failed requests.");
            }

            var delay = nextAllowedAt > now ? nextAllowedAt - now : TimeSpan.Zero;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken);

            acquired = false;
            return WebFetchLease.Acquired(hostKey, state);
        }
        catch
        {
            if (acquired)
                state.Semaphore.Release();
            throw;
        }
    }

    public void Record(WebFetchLease lease, HttpStatusCode? statusCode, bool success, TimeSpan? retryAfter = null)
    {
        if (!lease.CanFetch || lease.State is null) return;

        var webOptions = options.CurrentValue;
        var now = DateTimeOffset.UtcNow;
        var minDelayMs = Math.Clamp(webOptions.MinDelayBetweenHostRequestsMilliseconds, 0, 60_000);
        var jitterMs = Math.Clamp(webOptions.HostRequestJitterMilliseconds, 0, 60_000);
        var delayMs = minDelayMs + (jitterMs == 0 ? 0 : Random.Shared.Next(0, jitterMs + 1));
        var nextAllowedAt = now.AddMilliseconds(delayMs);

        lock (lease.State.Gate)
        {
            lease.State.NextAllowedAt = Max(lease.State.NextAllowedAt, nextAllowedAt);

            if (success)
            {
                lease.State.ConsecutiveFailures = 0;
                return;
            }

            lease.State.ConsecutiveFailures++;

            var cooldown = CooldownFor(webOptions, statusCode, lease.State.ConsecutiveFailures, retryAfter);
            if (cooldown > TimeSpan.Zero)
                lease.State.CooldownUntil = Max(lease.State.CooldownUntil, now.Add(cooldown));
        }
    }

    private static TimeSpan CooldownFor(
        WebResearchOptions options,
        HttpStatusCode? statusCode,
        int consecutiveFailures,
        TimeSpan? retryAfter)
    {
        var cooldown = retryAfter is { } delay && delay > TimeSpan.Zero
            ? ClampCooldown(delay)
            : TimeSpan.Zero;

        if (statusCode is HttpStatusCode.Forbidden or (HttpStatusCode)429)
        {
            var blockedCooldown = TimeSpan.FromSeconds(Math.Clamp(options.BlockedHostCooldownSeconds, 0, 86_400));
            cooldown = Max(cooldown, blockedCooldown);
        }
        else if (consecutiveFailures >= Math.Max(1, options.FailedHostCooldownThreshold))
        {
            var failureCooldown = TimeSpan.FromSeconds(Math.Clamp(options.RepeatedFailureCooldownSeconds, 0, 86_400));
            cooldown = Max(cooldown, failureCooldown);
        }

        return cooldown;
    }

    private static TimeSpan ClampCooldown(TimeSpan value) =>
        value > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : value;

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) =>
        left >= right ? left : right;

    private static TimeSpan Max(TimeSpan left, TimeSpan right) =>
        left >= right ? left : right;
}

public sealed class WebFetchLease : IAsyncDisposable
{
    private bool _released;

    private WebFetchLease(string hostKey, WebFetchHostState? state, bool canFetch, string diagnostics)
    {
        HostKey = hostKey;
        State = state;
        CanFetch = canFetch;
        Diagnostics = diagnostics;
    }

    public string HostKey { get; }
    public bool CanFetch { get; }
    public string Diagnostics { get; }
    internal WebFetchHostState? State { get; }

    internal static WebFetchLease Acquired(string hostKey, WebFetchHostState state) =>
        new(hostKey, state, canFetch: true, diagnostics: string.Empty);

    internal static WebFetchLease Blocked(string hostKey, string diagnostics) =>
        new(hostKey, state: null, canFetch: false, diagnostics);

    public ValueTask DisposeAsync()
    {
        if (!_released && State is not null)
        {
            State.Semaphore.Release();
            _released = true;
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class WebFetchHostState
{
    public object Gate { get; } = new();
    public SemaphoreSlim Semaphore { get; } = new(1, 1);
    public DateTimeOffset NextAllowedAt { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset CooldownUntil { get; set; } = DateTimeOffset.MinValue;
    public int ConsecutiveFailures { get; set; }
}
