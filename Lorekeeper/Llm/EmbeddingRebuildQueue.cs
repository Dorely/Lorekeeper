using System.Threading.Channels;

namespace Lorekeeper.Llm;

public sealed record EmbeddingRebuildRequest(int ConfigurationId, DateTime RequestedAtUtc, long CoordinatorVersion = 0);

public interface IEmbeddingRebuildQueue
{
    void Enqueue(EmbeddingRebuildRequest request);
    ValueTask<EmbeddingRebuildRequest> DequeueAsync(CancellationToken cancellationToken);
    EmbeddingRebuildRun BeginRun(EmbeddingRebuildRequest request, CancellationToken hostCancellationToken);
    void CompleteRun(EmbeddingRebuildRun run);
    Task CancelActiveAndClearPendingAsync(CancellationToken cancellationToken = default);
}

public sealed class EmbeddingRebuildQueue : IEmbeddingRebuildQueue
{
    private readonly Channel<EmbeddingRebuildRequest> _channel = Channel.CreateUnbounded<EmbeddingRebuildRequest>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly object _gate = new();
    private long _version;
    private EmbeddingRebuildRun? _activeRun;

    public void Enqueue(EmbeddingRebuildRequest request)
    {
        lock (_gate)
        {
            request = request with { CoordinatorVersion = _version };
        }

        _channel.Writer.TryWrite(request);
    }

    public ValueTask<EmbeddingRebuildRequest> DequeueAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAsync(cancellationToken);

    public EmbeddingRebuildRun BeginRun(EmbeddingRebuildRequest request, CancellationToken hostCancellationToken)
    {
        lock (_gate)
        {
            if (request.CoordinatorVersion != _version)
                return EmbeddingRebuildRun.Skipped(request);

            if (_activeRun is not null)
                return EmbeddingRebuildRun.Skipped(request);

            var run = EmbeddingRebuildRun.Active(request, hostCancellationToken);
            _activeRun = run;
            return run;
        }
    }

    public void CompleteRun(EmbeddingRebuildRun run)
    {
        if (run.IsSkipped)
            return;

        lock (_gate)
        {
            if (ReferenceEquals(_activeRun, run))
                _activeRun = null;
        }

        run.MarkCompleted();
    }

    public async Task CancelActiveAndClearPendingAsync(CancellationToken cancellationToken = default)
    {
        EmbeddingRebuildRun? activeRun;
        lock (_gate)
        {
            _version++;
            while (_channel.Reader.TryRead(out _))
            {
            }

            activeRun = _activeRun;
            activeRun?.Cancel();
        }

        if (activeRun is not null)
            await activeRun.Completion.WaitAsync(cancellationToken);
    }
}

public sealed class EmbeddingRebuildRun : IDisposable
{
    private readonly CancellationTokenSource? _cancellation;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private EmbeddingRebuildRun(
        EmbeddingRebuildRequest request,
        CancellationTokenSource? cancellation,
        bool isSkipped)
    {
        Request = request;
        _cancellation = cancellation;
        IsSkipped = isSkipped;
        if (isSkipped)
            _completion.SetResult();
    }

    public EmbeddingRebuildRequest Request { get; }
    public bool IsSkipped { get; }
    public CancellationToken CancellationToken => _cancellation?.Token ?? new CancellationToken(canceled: true);
    public Task Completion => _completion.Task;

    internal static EmbeddingRebuildRun Active(EmbeddingRebuildRequest request, CancellationToken hostCancellationToken) =>
        new(request, CancellationTokenSource.CreateLinkedTokenSource(hostCancellationToken), isSkipped: false);

    internal static EmbeddingRebuildRun Skipped(EmbeddingRebuildRequest request) =>
        new(request, cancellation: null, isSkipped: true);

    internal void Cancel() => _cancellation?.Cancel();

    internal void MarkCompleted() => _completion.TrySetResult();

    public void Dispose() => _cancellation?.Dispose();
}
