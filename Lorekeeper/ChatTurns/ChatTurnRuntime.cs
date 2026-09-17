using System.Threading.Channels;

namespace Lorekeeper.ChatTurns;

public enum ChatTurnSurface
{
    Outline,
    Editor,
    Research,
    Images,
    WritingCoach,
    Publish,
}

public readonly record struct ChatTurnKey(Guid ProjectId, ChatTurnSurface Surface);

public sealed record ChatTurnModelSnapshot(
    int ProviderId,
    string ModelId,
    string Label,
    int? MaxInputTokens,
    Lorekeeper.Models.LlmProviderResolvedMetadata? ResolvedMetadata = null);

public sealed record ChatTurnSnapshot(
    Guid TurnId,
    Guid ProjectId,
    ChatTurnSurface Surface,
    DateTime StartedAtUtc,
    string UserText,
    IReadOnlyList<ChatTurnImageAttachment> Images,
    ChatTurnModelSnapshot? Model);

public interface IChatTurnSubscription<TUpdate> : IAsyncDisposable
{
    Guid TurnId { get; }
    DateTime StartedAtUtc { get; }
    string UserText { get; }
    IAsyncEnumerable<TUpdate> ReadAllAsync(CancellationToken cancellationToken);
}

/// <summary>
/// App-wide active-turn coordinator. A project may run one turn per chat surface concurrently;
/// reopening a panel receives the buffered updates for that exact project/surface key.
/// </summary>
public sealed class ChatTurnRuntime
{
    private readonly object _lock = new();
    private readonly Dictionary<ChatTurnKey, IActiveTurn> _activeTurns = [];
    private readonly HashSet<ChatTurnKey> _maintenanceKeys = [];

    public ChatTurnSnapshot? GetActiveTurn(ChatTurnKey key)
    {
        lock (_lock)
        {
            return _activeTurns.TryGetValue(key, out var turn) ? turn.Snapshot : null;
        }
    }

    public bool TryStart<TUpdate>(
        ChatTurnKey key,
        string userText,
        Func<CancellationToken, IAsyncEnumerable<TUpdate>> run,
        Func<Exception, bool, TUpdate> errorUpdateFactory,
        Func<TUpdate, bool> isTerminalUpdate,
        IReadOnlyList<ChatTurnImageAttachment>? images = null,
        ChatTurnModelSnapshot? model = null)
    {
        ActiveTurn<TUpdate> turn;
        lock (_lock)
        {
            if (_activeTurns.ContainsKey(key) || _maintenanceKeys.Contains(key))
                return false;

            turn = new ActiveTurn<TUpdate>(key, userText, images ?? [], model, RemoveSubscription);
            _activeTurns[key] = turn;
        }

        _ = Task.Run(
            () => RunTurnAsync(turn, run, errorUpdateFactory, isTerminalUpdate),
            CancellationToken.None);
        return true;
    }

    /// <summary>
    /// Reserves an idle surface for an asynchronous lifecycle operation such as
    /// transcript reset. New turns are rejected until the returned lease is disposed.
    /// </summary>
    public IDisposable? TryBeginMaintenance(ChatTurnKey key)
    {
        lock (_lock)
        {
            if (_activeTurns.ContainsKey(key) || !_maintenanceKeys.Add(key))
                return null;
        }
        return new MaintenanceLease(this, key);
    }

    public IChatTurnSubscription<TUpdate>? Subscribe<TUpdate>(ChatTurnKey key)
    {
        lock (_lock)
        {
            if (!_activeTurns.TryGetValue(key, out var untyped))
                return null;
            if (untyped is not ActiveTurn<TUpdate> turn)
                throw new InvalidOperationException($"Active chat turn {key} has update type {untyped.UpdateType.Name}, not {typeof(TUpdate).Name}.");
            return turn.Subscribe();
        }
    }

    public void Cancel(ChatTurnKey key)
    {
        lock (_lock)
        {
            if (_activeTurns.TryGetValue(key, out var turn))
                turn.Cancel();
        }
    }

    private async Task RunTurnAsync<TUpdate>(
        ActiveTurn<TUpdate> turn,
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
                if (_activeTurns.TryGetValue(turn.Key, out var active) && ReferenceEquals(active, turn))
                    _activeTurns.Remove(turn.Key);
            }
            turn.Dispose();
        }
    }

    private void RemoveSubscription(ChatTurnKey key, Guid turnId, Guid subscriptionId)
    {
        lock (_lock)
        {
            if (_activeTurns.TryGetValue(key, out var active)
                && active.TurnId == turnId)
            {
                active.RemoveSubscription(subscriptionId);
            }
        }
    }

    private void EndMaintenance(ChatTurnKey key)
    {
        lock (_lock)
            _maintenanceKeys.Remove(key);
    }

    private sealed class MaintenanceLease(ChatTurnRuntime owner, ChatTurnKey key) : IDisposable
    {
        private ChatTurnRuntime? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndMaintenance(key);
    }

    private interface IActiveTurn
    {
        ChatTurnKey Key { get; }
        Guid TurnId { get; }
        Type UpdateType { get; }
        ChatTurnSnapshot Snapshot { get; }
        void Cancel();
        void RemoveSubscription(Guid subscriptionId);
    }

    private sealed class ActiveTurn<TUpdate>(
        ChatTurnKey key,
        string userText,
        IReadOnlyList<ChatTurnImageAttachment> images,
        ChatTurnModelSnapshot? model,
        Action<ChatTurnKey, Guid, Guid> removeSubscription) : IActiveTurn
    {
        private readonly object _turnLock = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly List<TUpdate> _buffer = [];
        private readonly Dictionary<Guid, Subscription<TUpdate>> _subscriptions = [];
        private bool _completed;

        public ChatTurnKey Key { get; } = key;
        public Guid TurnId { get; } = Guid.NewGuid();
        public Type UpdateType => typeof(TUpdate);
        public DateTime StartedAtUtc { get; } = DateTime.UtcNow;
        public CancellationToken CancellationToken => _cts.Token;
        public bool IsCancellationRequested => _cts.IsCancellationRequested;
        public ChatTurnSnapshot Snapshot => new(TurnId, Key.ProjectId, Key.Surface, StartedAtUtc, userText, images, model);

        public IChatTurnSubscription<TUpdate> Subscribe()
        {
            var subscription = new Subscription<TUpdate>(Key, TurnId, StartedAtUtc, userText, removeSubscription);
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
                _subscriptions.Remove(subscriptionId);
        }

        public void Cancel() => _cts.Cancel();
        public void Dispose() => _cts.Dispose();
    }

    private sealed class Subscription<TUpdate> : IChatTurnSubscription<TUpdate>
    {
        private readonly ChatTurnKey _key;
        private readonly Action<ChatTurnKey, Guid, Guid> _remove;
        private readonly Channel<TUpdate> _channel = Channel.CreateUnbounded<TUpdate>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        public Subscription(
            ChatTurnKey key,
            Guid turnId,
            DateTime startedAtUtc,
            string userText,
            Action<ChatTurnKey, Guid, Guid> remove)
        {
            _key = key;
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
            _remove(_key, TurnId, Id);
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
