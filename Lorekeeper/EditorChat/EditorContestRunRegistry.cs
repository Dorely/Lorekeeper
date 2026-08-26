namespace Lorekeeper.EditorChat;

/// <summary>
/// Keeps the process-local provider run handle independent from a request or
/// circuit scope. Contest rows remain the durable source of truth; this
/// registry only supplies cancellation and completion coordination while a
/// provider run is alive in this process.
/// </summary>
public interface IEditorContestRunRegistry
{
    EditorContestRunHandle Register(Guid batchId, CancellationToken parentCancellation);

    bool TryGet(Guid batchId, out EditorContestRunHandle? run);

    bool TryRemove(Guid batchId, EditorContestRunHandle run);
}

public sealed class EditorContestRunRegistry : IEditorContestRunRegistry
{
    private readonly Dictionary<Guid, EditorContestRunHandle> _runs = [];
    private readonly object _sync = new();

    public EditorContestRunHandle Register(Guid batchId, CancellationToken parentCancellation)
    {
        var run = new EditorContestRunHandle(batchId, parentCancellation);
        lock (_sync)
        {
            if (_runs.ContainsKey(batchId))
            {
                run.Dispose();
                throw new InvalidOperationException($"Contest batch {batchId} is already running.");
            }

            _runs.Add(batchId, run);
        }

        return run;
    }

    public bool TryGet(Guid batchId, out EditorContestRunHandle? run)
    {
        lock (_sync)
            return _runs.TryGetValue(batchId, out run);
    }

    public bool TryRemove(Guid batchId, EditorContestRunHandle run)
    {
        lock (_sync)
        {
            if (!_runs.TryGetValue(batchId, out var current) || !ReferenceEquals(current, run))
                return false;

            return _runs.Remove(batchId);
        }
    }
}

public sealed class EditorContestRunHandle(Guid batchId, CancellationToken parentCancellation) : IDisposable
{
    private readonly CancellationTokenSource _cancellation =
        CancellationTokenSource.CreateLinkedTokenSource(parentCancellation);
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    public Guid BatchId { get; } = batchId;

    public CancellationToken Token => _cancellation.Token;

    public Task Completion => _completion.Task;

    public void Cancel()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Completion/disposal won the race. The durable batch state still
            // determines whether a subsequent discard is allowed.
        }
    }

    public Task WaitForCompletionAsync(CancellationToken cancellationToken = default) =>
        cancellationToken.CanBeCanceled
            ? _completion.Task.WaitAsync(cancellationToken)
            : _completion.Task;

    public void Complete()
    {
        _completion.TrySetResult();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _cancellation.Dispose();
    }
}
