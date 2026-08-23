using System.Threading.Channels;

namespace Lorekeeper.VersionHistory.Sync;

/// <summary>
/// Process-local wake-up only. Automatic push intent rows in SQLite remain the
/// source of truth, so a lost signal cannot lose durable work.
/// </summary>
public interface IProjectVersionAutoPushQueue
{
    void Signal();
    ValueTask<bool> WaitAsync(CancellationToken cancellationToken = default);
}

public sealed class ProjectVersionAutoPushQueue : IProjectVersionAutoPushQueue
{
    private readonly Channel<bool> _wakeups = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });

    public void Signal() => _wakeups.Writer.TryWrite(true);

    public ValueTask<bool> WaitAsync(CancellationToken cancellationToken = default) =>
        _wakeups.Reader.ReadAsync(cancellationToken);
}
