using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace Lorekeeper.Diagnostics;

public interface IPerformanceTraceWriter
{
    bool IsEnabled { get; }

    void Record(string metric, double durationMilliseconds, long sequence);
}

/// <summary>
/// Writes explicitly enabled, local-only editor timing diagnostics. The event
/// schema intentionally contains timing metadata only; manuscript and account
/// data never cross this boundary.
/// </summary>
public sealed class PerformanceTraceWriter : IPerformanceTraceWriter, IHostedService, IAsyncDisposable
{
    private static readonly HashSet<string> AllowedMetrics = new(StringComparer.Ordinal)
    {
        "input-to-visible-frame",
        "save-acknowledgment",
        "undo-to-visible-frame",
        "redo-to-visible-frame",
        "editor-ready-after-navigation",
    };

    private readonly string? _outputPath;
    private readonly int? _limitPerMetric;
    private readonly ConcurrentDictionary<string, int> _metricCounts = new(StringComparer.Ordinal);
    private readonly Channel<PerformanceTraceEvent> _events = Channel.CreateBounded<PerformanceTraceEvent>(
        new BoundedChannelOptions(4096)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite,
        });
    private Task? _writer;

    public PerformanceTraceWriter(IConfiguration configuration)
    {
        _outputPath = ResolveOutputPath(configuration["Diagnostics:PerformanceTracePath"]);
        _limitPerMetric = configuration.GetValue<int?>("Diagnostics:PerformanceTraceLimitPerMetric");
        if (_outputPath is not null && _limitPerMetric is <= 0)
            throw new InvalidOperationException("Diagnostics:PerformanceTraceLimitPerMetric must be greater than zero.");
    }

    public bool IsEnabled => _outputPath is not null;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled)
            return Task.CompletedTask;

        Directory.CreateDirectory(Path.GetDirectoryName(_outputPath!)!);
        _writer = WriteAsync(_outputPath!);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _events.Writer.TryComplete();
        if (_writer is not null)
            await _writer.WaitAsync(cancellationToken);
    }

    public void Record(string metric, double durationMilliseconds, long sequence)
    {
        if (!IsEnabled
            || !AllowedMetrics.Contains(metric)
            || !double.IsFinite(durationMilliseconds)
            || durationMilliseconds < 0
            || durationMilliseconds > TimeSpan.FromMinutes(5).TotalMilliseconds
            || sequence < 0)
        {
            return;
        }

        if (_limitPerMetric is { } limit
            && _metricCounts.AddOrUpdate(metric, 1, (_, count) => count + 1) > limit)
        {
            return;
        }

        _events.Writer.TryWrite(new PerformanceTraceEvent(
            metric,
            Math.Round(durationMilliseconds, 3),
            sequence,
            DateTimeOffset.UtcNow));
    }

    public async ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        if (_writer is not null)
            await _writer.ConfigureAwait(false);
    }

    private async Task WriteAsync(string outputPath)
    {
        await using var stream = new FileStream(
            outputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);
        await using var writer = new StreamWriter(stream);

        await foreach (var performanceEvent in _events.Reader.ReadAllAsync())
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(performanceEvent)).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
    }

    private static string? ResolveOutputPath(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return null;

        var fullPath = Path.GetFullPath(configuredPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Diagnostics:PerformanceTracePath must name a file.");
        for (var candidate = new DirectoryInfo(directory); candidate is not null; candidate = candidate.Parent)
        {
            if (!string.Equals(candidate.Name, ".artifacts", StringComparison.OrdinalIgnoreCase))
                continue;

            var relativePath = Path.GetRelativePath(candidate.FullName, fullPath);
            var requiredPrefix = $"performance{Path.DirectorySeparatorChar}";
            if (relativePath.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
                return fullPath;

            break;
        }

        throw new InvalidOperationException(
            "Diagnostics:PerformanceTracePath is allowed only under .artifacts/performance.");
    }

    private sealed record PerformanceTraceEvent(
        string Metric,
        double DurationMilliseconds,
        long Sequence,
        DateTimeOffset RecordedAtUtc);
}
