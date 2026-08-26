using System.Threading;

namespace Lorekeeper.EditorChat;

public sealed class EditorContestMutationContext : IEditorContestMutationContext
{
    private readonly AsyncLocal<AuthorizationScope?> _current = new();

    public bool IsAuthorized(Guid projectId) =>
        projectId != Guid.Empty
        && _current.Value?.ProjectId == projectId;

    public IDisposable BeginAuthorizedMutation(Guid projectId)
    {
        if (projectId == Guid.Empty)
            throw new ArgumentException("A project is required for contest authorization.", nameof(projectId));

        var previous = _current.Value;
        _current.Value = new AuthorizationScope(projectId);
        return new Scope(() => _current.Value = previous);
    }

    private sealed record AuthorizationScope(Guid ProjectId);

    private sealed class Scope(Action onDispose) : IDisposable
    {
        private Action? _onDispose = onDispose;

        public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }
}
