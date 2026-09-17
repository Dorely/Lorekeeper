namespace Lorekeeper.Authoring;

public sealed record AuthoringMutationContext(
    Guid AssistantTurnId,
    string ActionLabel)
{
    public bool IsAssistant => AssistantTurnId != Guid.Empty;
}

public interface IAuthoringMutationContextAccessor
{
    AuthoringMutationContext? Current { get; }
    bool IsHistorySuppressed { get; }
    IDisposable BeginAssistantTurn(
        Guid turnId,
        string actionLabel);
    IDisposable SuppressHistory();
}

public sealed class AuthoringMutationContextAccessor : IAuthoringMutationContextAccessor
{
    private static readonly AsyncLocal<AuthoringMutationContext?> Ambient = new();
    private static readonly AsyncLocal<int> HistorySuppressionDepth = new();

    // The scoped value survives AI tool schedulers that do not flow ExecutionContext. The
    // ambient value carries the same turn into intentionally created revision-worker scopes.
    // AsyncLocal keeps simultaneous project turns isolated from one another.
    private AuthoringMutationContext? _current;

    public AuthoringMutationContext? Current => Ambient.Value ?? _current;
    public bool IsHistorySuppressed => HistorySuppressionDepth.Value > 0;

    public IDisposable BeginAssistantTurn(
        Guid turnId,
        string actionLabel)
    {
        var previous = _current;
        var previousAmbient = Ambient.Value;
        _current = new AuthoringMutationContext(turnId, actionLabel);
        Ambient.Value = _current;
        return new Scope(() =>
        {
            Ambient.Value = previousAmbient;
            _current = previous;
        });
    }

    public IDisposable SuppressHistory()
    {
        HistorySuppressionDepth.Value++;
        return new Scope(() => HistorySuppressionDepth.Value = Math.Max(0, HistorySuppressionDepth.Value - 1));
    }

    private sealed class Scope(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
