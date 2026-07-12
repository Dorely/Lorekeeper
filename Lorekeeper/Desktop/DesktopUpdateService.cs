namespace Lorekeeper.Desktop;

public enum DesktopUpdateStatus
{
    Unsupported,
    Checking,
    Downloading,
    Ready,
    Restarting,
    Error,
}

public sealed record DesktopUpdateSnapshot(
    DesktopUpdateStatus Status,
    string? Version = null,
    double? DownloadPercent = null,
    string? Error = null)
{
    public bool CanRestart => Status == DesktopUpdateStatus.Ready;
}

public interface IDesktopUpdateService
{
    event EventHandler? StateChanged;

    DesktopUpdateSnapshot Snapshot { get; }
    Task RestartToUpdateAsync(CancellationToken cancellationToken = default);
}

public sealed class DesktopUpdateService : IDesktopUpdateService
{
    private readonly object _lock = new();
    private DesktopUpdateSnapshot _snapshot = new(DesktopUpdateStatus.Unsupported);
    private Action? _restart;

    public event EventHandler? StateChanged;

    public DesktopUpdateSnapshot Snapshot
    {
        get
        {
            lock (_lock)
                return _snapshot;
        }
    }

    public Task RestartToUpdateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Action restart;
        lock (_lock)
        {
            if (!_snapshot.CanRestart || _restart is null)
                throw new InvalidOperationException("No downloaded update is ready to install.");

            _snapshot = _snapshot with { Status = DesktopUpdateStatus.Restarting, Error = null };
            restart = _restart;
        }

        NotifyStateChanged();
        restart();
        return Task.CompletedTask;
    }

    public void Enable(Action restart)
    {
        lock (_lock)
            _restart = restart;
    }

    public void MarkChecking() => SetSnapshot(new DesktopUpdateSnapshot(DesktopUpdateStatus.Checking));

    public void MarkDownloading(string? version, double? percent = null) =>
        SetSnapshot(new DesktopUpdateSnapshot(DesktopUpdateStatus.Downloading, Clean(version), percent));

    public void MarkReady(string? version) =>
        SetSnapshot(new DesktopUpdateSnapshot(DesktopUpdateStatus.Ready, Clean(version)));

    public void MarkError(string error) =>
        SetSnapshot(new DesktopUpdateSnapshot(DesktopUpdateStatus.Error, Error: Clean(error)));

    private void SetSnapshot(DesktopUpdateSnapshot snapshot)
    {
        lock (_lock)
            _snapshot = snapshot;
        NotifyStateChanged();
    }

    private void NotifyStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
