using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Lorekeeper.Ingest;

public sealed class IngestJobQueue : IIngestJobQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });

    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeCancellations = new();

    public void Enqueue(Guid jobId) => _channel.Writer.TryWrite(jobId);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    public void RegisterCancellation(Guid jobId, CancellationTokenSource cancellationTokenSource) =>
        _activeCancellations[jobId] = cancellationTokenSource;

    public bool IsActive(Guid jobId) => _activeCancellations.ContainsKey(jobId);

    public bool RequestCancellation(Guid jobId)
    {
        if (!_activeCancellations.TryGetValue(jobId, out var cancellationTokenSource)) return false;
        cancellationTokenSource.Cancel();
        return true;
    }

    public void ClearCancellation(Guid jobId)
    {
        if (_activeCancellations.TryRemove(jobId, out var cancellationTokenSource))
            cancellationTokenSource.Dispose();
    }
}
