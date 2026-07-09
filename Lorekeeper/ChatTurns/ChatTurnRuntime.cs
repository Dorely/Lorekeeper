using System.Threading.Channels;

namespace Lorekeeper.ChatTurns;

public sealed record ChatTurnSnapshot(Guid TurnId, Guid ProjectId, DateTime StartedAtUtc, string UserText);

public interface IChatTurnSubscription<TUpdate> : IAsyncDisposable
{
    Guid TurnId { get; }
    DateTime StartedAtUtc { get; }
    string UserText { get; }
    IAsyncEnumerable<TUpdate> ReadAllAsync(CancellationToken cancellationToken);
}

public sealed class ChatTurnRuntime<TUpdate>
{
    private readonly object _lock = new();
    private readonly Dictionary<Guid, ActiveTurn> _activeTurns = [];

    public ChatTurnSnapshot? GetActiveTurn(Guid projectId)
    {
        lock (_lock)
        {
            return _activeTurns.TryGetValue(projectId, out var turn) ? turn.Snapshot : null;
        }
    }

    public bool TryStart(
        Guid projectId,
        string userText,
        Func<CancellationToken, IAsyncEnumerable<TUpdate>> run,
        Func<Exception, bool, TUpdate> errorUpdateFactory,
        Func<TUpdate, bool> isTerminalUpdate)
    {
        ActiveTurn turn;
        lock (_lock)
        {
            if (_activeTurns.ContainsKey(projectId))
                return false;

            turn = new ActiveTurn(projectId, userText, RemoveSubscription);
            _activeTurns[projectId] = turn;
        }

        _ = Task.Run(
            () => RunTurnAsync(turn, run, errorUpdateFactory, isTerminalUpdate),
            CancellationToken.None);
        return true;
    }

    public IChatTurnSubscription<TUpdate>? Subscribe(Guid projectId)
    {
        lock (_lock)
        {
            return _activeTurns.TryGetValue(projectId, out var turn)
                ? turn.Subscribe()
                : null;
        }
    }

    public void Cancel(Guid projectId)
    {
        lock (_lock)
        {
            if (_activeTurns.TryGetValue(projectId, out var turn))
                turn.Cancel();
        }
    }

    private async Task RunTurnAsync(
        ActiveTurn turn,
        Func<CancellationToken, IAsyncEnumerable<TUpdate>> run,
        Func<Exception, bool, TUpdate> errorUpdateFactory,
        Func<TUpdate, bool> isTerminalUpdate)
    {
        var terminalPublished = false;
        try
        {
            await foreach (var update in run(turn.CancellationToken))
            {
                turn.Publish(update);
                if (!isTerminalUpdate(update))
                    continue;

                terminalPublished = true;
                break;
            }
        }
        catch (OperationCanceledException ex) when (turn.IsCancellationRequested)
        {
            if (!terminalPublished)
            {
                turn.Publish(errorUpdateFactory(ex, true));
                terminalPublished = true;
            }
        }
        catch (Exception ex)
        {
            if (!terminalPublished)
            {
                turn.Publish(errorUpdateFactory(ex, false));
                terminalPublished = true;
            }
        }
        finally
        {
            turn.Complete();
            lock (_lock)
            {
                if (_activeTurns.TryGetValue(turn.ProjectId, out var active) && ReferenceEquals(active, turn))
                    _activeTurns.Remove(turn.ProjectId);
            }

            turn.Dispose();
        }
    }

    private void RemoveSubscription(Guid projectId, Guid turnId, Guid subscriptionId)
    {
        lock (_lock)
        {
            if (_activeTurns.TryGetValue(projectId, out var turn) && turn.TurnId == turnId)
                turn.RemoveSubscription(subscriptionId);
        }
    }

    private sealed class ActiveTurn(Guid projectId, string userText, Action<Guid, Guid, Guid> removeSubscription)
    {
        private readonly object _turnLock = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly List<TUpdate> _buffer = [];
        private readonly Dictionary<Guid, Subscription> _subscriptions = [];
        private bool _completed;

        public Guid TurnId { get; } = Guid.NewGuid();
        public Guid ProjectId { get; } = projectId;
        public DateTime StartedAtUtc { get; } = DateTime.UtcNow;
        public CancellationToken CancellationToken => _cts.Token;
        public bool IsCancellationRequested => _cts.IsCancellationRequested;
        public ChatTurnSnapshot Snapshot => new(TurnId, ProjectId, StartedAtUtc, userText);

        public IChatTurnSubscription<TUpdate> Subscribe()
        {
            var subscription = new Subscription(ProjectId, TurnId, StartedAtUtc, userText, removeSubscription);
            lock (_turnLock)
            {
                foreach (var update in _buffer)
                    subscription.TryWrite(update);

                if (_completed)
                    subscription.Complete();
                else
                    _subscriptions[subscription.Id] = subscription;
            }

            return subscription;
        }

        public void Publish(TUpdate update)
        {
            lock (_turnLock)
            {
                if (_completed)
                    return;

                _buffer.Add(update);
                foreach (var subscription in _subscriptions.Values)
                    subscription.TryWrite(update);
            }
        }

        public void Complete()
        {
            lock (_turnLock)
            {
                if (_completed)
                    return;

                _completed = true;
                foreach (var subscription in _subscriptions.Values)
                    subscription.Complete();
                _subscriptions.Clear();
            }
        }

        public void RemoveSubscription(Guid subscriptionId)
        {
            lock (_turnLock)
            {
                _subscriptions.Remove(subscriptionId);
            }
        }

        public void Cancel() => _cts.Cancel();

        public void Dispose() => _cts.Dispose();
    }

    private sealed class Subscription : IChatTurnSubscription<TUpdate>
    {
        private readonly Guid _projectId;
        private readonly Action<Guid, Guid, Guid> _remove;
        private readonly Channel<TUpdate> _channel = Channel.CreateUnbounded<TUpdate>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });

        public Subscription(
            Guid projectId,
            Guid turnId,
            DateTime startedAtUtc,
            string userText,
            Action<Guid, Guid, Guid> remove)
        {
            _projectId = projectId;
            _remove = remove;
            TurnId = turnId;
            StartedAtUtc = startedAtUtc;
            UserText = userText;
        }

        public Guid Id { get; } = Guid.NewGuid();
        public Guid TurnId { get; }
        public DateTime StartedAtUtc { get; }
        public string UserText { get; }

        public IAsyncEnumerable<TUpdate> ReadAllAsync(CancellationToken cancellationToken) =>
            _channel.Reader.ReadAllAsync(cancellationToken);

        public void TryWrite(TUpdate update) => _channel.Writer.TryWrite(update);

        public void Complete() => _channel.Writer.TryComplete();

        public ValueTask DisposeAsync()
        {
            _remove(_projectId, TurnId, Id);
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
