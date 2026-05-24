using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Lorekeeper.EditorChat;

public interface IEditorRevisionJobNotifier
{
    IEditorRevisionJobUpdateSubscription Subscribe(Guid projectId);
    void Notify(EditorRevisionJobUpdate update);
}

public interface IEditorRevisionJobUpdateSubscription : IAsyncDisposable
{
    IAsyncEnumerable<EditorRevisionJobUpdate> ReadAllAsync(CancellationToken cancellationToken);
}

public sealed record EditorRevisionJobUpdate(
    Guid ProjectId,
    Guid ConversationId,
    string ToolCallId,
    Guid JobId,
    Guid? SessionId,
    EditorRevisionJobUpdateKind Kind,
    DateTime CreatedAtUtc);

public enum EditorRevisionJobUpdateKind
{
    Created,
    Queued,
    Progress,
    SessionCompleted,
    Completed,
    Failed,
    Cancelled,
}

public sealed class EditorRevisionJobNotifier : IEditorRevisionJobNotifier
{
    private readonly ConcurrentDictionary<Guid, Subscription> _subscriptions = new();

    public IEditorRevisionJobUpdateSubscription Subscribe(Guid projectId)
    {
        var subscription = new Subscription(projectId, RemoveSubscription);
        _subscriptions[subscription.Id] = subscription;
        return subscription;
    }

    public void Notify(EditorRevisionJobUpdate update)
    {
        foreach (var subscription in _subscriptions.Values)
        {
            if (subscription.ProjectId != update.ProjectId) continue;
            subscription.TryWrite(update);
        }
    }

    private void RemoveSubscription(Guid subscriptionId) =>
        _subscriptions.TryRemove(subscriptionId, out _);

    private sealed class Subscription(Guid projectId, Action<Guid> remove) : IEditorRevisionJobUpdateSubscription
    {
        private readonly Channel<EditorRevisionJobUpdate> _channel = Channel.CreateUnbounded<EditorRevisionJobUpdate>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });

        public Guid Id { get; } = Guid.NewGuid();
        public Guid ProjectId { get; } = projectId;

        public IAsyncEnumerable<EditorRevisionJobUpdate> ReadAllAsync(CancellationToken cancellationToken) =>
            _channel.Reader.ReadAllAsync(cancellationToken);

        public void TryWrite(EditorRevisionJobUpdate update) =>
            _channel.Writer.TryWrite(update);

        public ValueTask DisposeAsync()
        {
            remove(Id);
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
