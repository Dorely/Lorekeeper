namespace Lorekeeper.Persistence;

/// <summary>
/// Defers process-local effects until an enclosing application command commits.
/// Durable jobs remain the restart boundary; this scope owns no database context.
/// </summary>
internal sealed class MutationCommitEffects : IDisposable
{
    private static readonly AsyncLocal<MutationCommitEffects?> Ambient = new();
    private readonly MutationCommitEffects? _previous;
    private readonly List<Func<CancellationToken, Task>> _pending = [];
    private bool _closed;

    private MutationCommitEffects()
    {
        _previous = Ambient.Value;
        Ambient.Value = this;
    }

    public static MutationCommitEffects Begin() => new();

    public static bool TryDefer(Func<CancellationToken, Task> effect)
    {
        if (Ambient.Value is not { _closed: false } current) return false;
        current._pending.Add(effect);
        return true;
    }

    public async Task CompleteAsync(CancellationToken ct)
    {
        Dispose();
        List<Exception> failures = [];
        foreach (var effect in _pending)
        {
            try { await effect(ct); }
            catch (Exception exception) { failures.Add(exception); }
        }
        _pending.Clear();
        if (failures.Count > 0) throw new AggregateException("Committed changes have effects awaiting repair.", failures);
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        Ambient.Value = _previous;
    }
}
