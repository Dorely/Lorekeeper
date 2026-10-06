namespace Lorekeeper.Context;

public enum VectorIndexWorkKind
{
    ChapterBody,
    EditionChapter,
    ContextChapter,
    ContextAct,
    ContextEntity,
    ContextProjectProfile,
}

public interface IVectorIndexWorkCoordinator
{
    IVectorIndexWorkDeferral BeginDeferral();

    Task QueueOrRunAsync(
        VectorIndexWorkKind kind,
        string resourceKey,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken = default);
}

public interface IVectorIndexWorkDeferral : IAsyncDisposable
{
    Task FlushAsync(CancellationToken cancellationToken = default);
}

public sealed class VectorIndexWorkCoordinator(ILogger<VectorIndexWorkCoordinator> logger) : IVectorIndexWorkCoordinator
{
    private readonly Dictionary<VectorIndexWorkKey, VectorIndexWorkItem> _pending = [];
    private int _deferralDepth;
    private bool _flushing;

    public IVectorIndexWorkDeferral BeginDeferral()
    {
        _deferralDepth++;
        return new Deferral(this);
    }

    public async Task QueueOrRunAsync(
        VectorIndexWorkKind kind,
        string resourceKey,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceKey);
        ArgumentNullException.ThrowIfNull(work);

        if (_deferralDepth == 0 || _flushing)
        {
            await work(cancellationToken);
            return;
        }

        var normalizedKey = resourceKey.Trim();
        var key = new VectorIndexWorkKey(kind, normalizedKey);
        if (kind == VectorIndexWorkKind.ChapterBody)
            _pending.Remove(new VectorIndexWorkKey(VectorIndexWorkKind.ContextChapter, normalizedKey));
        else if (kind == VectorIndexWorkKind.ContextChapter
            && _pending.ContainsKey(new VectorIndexWorkKey(VectorIndexWorkKind.ChapterBody, normalizedKey)))
        {
            return;
        }

        _pending[key] = new VectorIndexWorkItem(key, work);
    }

    private async Task CompleteDeferralAsync(bool flush, CancellationToken cancellationToken)
    {
        if (_deferralDepth <= 0) return;

        _deferralDepth--;
        if (_deferralDepth > 0) return;

        if (flush)
        {
            await FlushQueuedWorkAsync(cancellationToken);
            return;
        }

        if (_pending.Count > 0)
        {
            logger.LogWarning("Discarding {Count} deferred vector index work item(s) because the deferral scope was disposed without flushing.", _pending.Count);
            _pending.Clear();
        }
    }

    private async Task FlushQueuedWorkAsync(CancellationToken cancellationToken)
    {
        while (_pending.Count > 0)
        {
            var items = _pending
                .Values
                .OrderBy(item => WorkPriority(item.Key.Kind))
                .ThenBy(item => item.Key.ResourceKey, StringComparer.Ordinal)
                .ToList();
            _pending.Clear();

            _flushing = true;
            try
            {
                foreach (var item in items)
                {
                    try
                    {
                        await item.Work(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(
                            ex,
                            "Deferred vector index work failed for {WorkKind}/{ResourceKey}.",
                            item.Key.Kind,
                            item.Key.ResourceKey);
                    }
                }
            }
            finally
            {
                _flushing = false;
            }
        }
    }

    private static int WorkPriority(VectorIndexWorkKind kind) => kind switch
    {
        VectorIndexWorkKind.ChapterBody => 0,
        VectorIndexWorkKind.EditionChapter => 0,
        VectorIndexWorkKind.ContextChapter => 1,
        VectorIndexWorkKind.ContextAct => 2,
        VectorIndexWorkKind.ContextEntity => 2,
        VectorIndexWorkKind.ContextProjectProfile => 2,
        _ => 10,
    };

    private readonly record struct VectorIndexWorkKey(VectorIndexWorkKind Kind, string ResourceKey);

    private sealed record VectorIndexWorkItem(VectorIndexWorkKey Key, Func<CancellationToken, Task> Work);

    private sealed class Deferral(VectorIndexWorkCoordinator owner) : IVectorIndexWorkDeferral
    {
        private bool _completed;

        public async Task FlushAsync(CancellationToken cancellationToken = default)
        {
            if (_completed) return;

            _completed = true;
            await owner.CompleteDeferralAsync(flush: true, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (_completed) return;

            _completed = true;
            await owner.CompleteDeferralAsync(flush: false, CancellationToken.None);
        }
    }
}
