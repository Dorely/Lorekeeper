using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Lorekeeper.ImportExport;

public interface IProjectImportJobNotifier
{
    IProjectImportJobUpdateSubscription Subscribe(Guid projectId);
    void Notify(ProjectImportJobUpdate update);
}

public interface IProjectImportJobUpdateSubscription : IAsyncDisposable
{
    IAsyncEnumerable<ProjectImportJobUpdate> ReadAllAsync(CancellationToken cancellationToken);
}

public sealed record ProjectImportJobUpdate(
    Guid ProjectId,
    Guid JobId,
    ProjectImportJobUpdateKind Kind,
    DateTime CreatedAtUtc);

public enum ProjectImportJobUpdateKind
{
    Created,
    Queued,
    Progress,
    Report,
    Completed,
    Failed,
    Deleted,
}

public sealed class ProjectImportJobNotifier : IProjectImportJobNotifier
{
    private readonly ConcurrentDictionary<Guid, Subscription> _subscriptions = new();

    public IProjectImportJobUpdateSubscription Subscribe(Guid projectId)
    {
        var subscription = new Subscription(projectId, RemoveSubscription);
        _subscriptions[subscription.Id] = subscription;
        return subscription;
    }

    public void Notify(ProjectImportJobUpdate update)
    {
        foreach (var subscription in _subscriptions.Values)
        {
            if (subscription.ProjectId != update.ProjectId) continue;
            subscription.TryWrite(update);
        }
    }

    private void RemoveSubscription(Guid subscriptionId) =>
        _subscriptions.TryRemove(subscriptionId, out _);

    private sealed class Subscription(Guid projectId, Action<Guid> remove) : IProjectImportJobUpdateSubscription
    {
        private readonly Channel<ProjectImportJobUpdate> _channel = Channel.CreateUnbounded<ProjectImportJobUpdate>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        public Guid Id { get; } = Guid.NewGuid();
        public Guid ProjectId { get; } = projectId;

        public IAsyncEnumerable<ProjectImportJobUpdate> ReadAllAsync(CancellationToken cancellationToken) =>
            _channel.Reader.ReadAllAsync(cancellationToken);

        public void TryWrite(ProjectImportJobUpdate update) =>
            _channel.Writer.TryWrite(update);

        public ValueTask DisposeAsync()
        {
            remove(Id);
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
