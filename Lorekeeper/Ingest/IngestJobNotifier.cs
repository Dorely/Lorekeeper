using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Lorekeeper.Ingest;

public sealed class IngestJobNotifier : IIngestJobNotifier
{
    private readonly ConcurrentDictionary<Guid, Subscription> _subscriptions = new();

    public IIngestJobUpdateSubscription Subscribe(Guid projectId)
    {
        var subscription = new Subscription(projectId, RemoveSubscription);
        _subscriptions[subscription.Id] = subscription;
        return subscription;
    }

    public void Notify(IngestJobUpdate update)
    {
        foreach (var subscription in _subscriptions.Values)
        {
            if (subscription.ProjectId != update.ProjectId) continue;
            subscription.TryWrite(update);
        }
    }

    private void RemoveSubscription(Guid subscriptionId) =>
        _subscriptions.TryRemove(subscriptionId, out _);

    private sealed class Subscription(Guid projectId, Action<Guid> remove) : IIngestJobUpdateSubscription
    {
        private readonly Channel<IngestJobUpdate> _channel = Channel.CreateUnbounded<IngestJobUpdate>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        public Guid Id { get; } = Guid.NewGuid();
        public Guid ProjectId { get; } = projectId;

        public IAsyncEnumerable<IngestJobUpdate> ReadAllAsync(CancellationToken cancellationToken) =>
            _channel.Reader.ReadAllAsync(cancellationToken);

        public void TryWrite(IngestJobUpdate update) =>
            _channel.Writer.TryWrite(update);

        public ValueTask DisposeAsync()
        {
            remove(Id);
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}