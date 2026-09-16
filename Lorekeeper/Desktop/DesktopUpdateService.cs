namespace Lorekeeper.Desktop;

public enum DesktopUpdateStatus
{
    Unsupported,
    StoreManaged,
    Idle,
    Checking,
    ManualUpdateAvailable,
    Error,
}

public sealed record DesktopUpdateSnapshot(
    DesktopUpdateStatus Status,
    string? Version = null,
    string? Error = null,
    Uri? ReleaseUri = null)
{
    public bool CanDownload => Status == DesktopUpdateStatus.ManualUpdateAvailable && ReleaseUri is not null;
}

public interface IDesktopUpdateService
{
    event EventHandler? StateChanged;

    DesktopUpdateSnapshot Snapshot { get; }
    string? InstalledVersion { get; }
    bool CanCheckForUpdates { get; }
    Task CheckForUpdatesAsync(CancellationToken cancellationToken = default);
    Task OpenDownloadAsync(CancellationToken cancellationToken = default);
}

public sealed class DesktopUpdateService : IDesktopUpdateService
{
    private readonly object _lock = new();
    private DesktopUpdateSnapshot _snapshot = new(DesktopUpdateStatus.Unsupported);
    private string? _installedVersion;
    private Func<Uri, CancellationToken, Task>? _openDownload;
    private Func<CancellationToken, Task>? _checkForUpdates;

    public event EventHandler? StateChanged;

    public DesktopUpdateSnapshot Snapshot
    {
        get
        {
            lock (_lock)
                return _snapshot;
        }
    }

    public string? InstalledVersion
    {
        get
        {
            lock (_lock)
                return _installedVersion;
        }
    }

    public bool CanCheckForUpdates
    {
        get
        {
            lock (_lock)
                return _checkForUpdates is not null;
        }
    }

    public Task CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Func<CancellationToken, Task> checkForUpdates;
        lock (_lock)
        {
            if (_checkForUpdates is null)
                throw new InvalidOperationException("Manual update checks are unavailable.");

            if (_snapshot.Status is DesktopUpdateStatus.Checking
                or DesktopUpdateStatus.ManualUpdateAvailable)
            {
                return Task.CompletedTask;
            }

            checkForUpdates = _checkForUpdates;
        }

        return checkForUpdates(cancellationToken);
    }

    public Task OpenDownloadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Func<Uri, CancellationToken, Task> openDownload;
        Uri releaseUri;
        lock (_lock)
        {
            if (!_snapshot.CanDownload || _snapshot.ReleaseUri is null || _openDownload is null)
                throw new InvalidOperationException("No newer release is available to download.");

            openDownload = _openDownload;
            releaseUri = _snapshot.ReleaseUri;
        }

        return openDownload(releaseUri, cancellationToken);
    }

    public void EnableManualDownloads(Func<Uri, CancellationToken, Task> openDownload)
    {
        ArgumentNullException.ThrowIfNull(openDownload);
        lock (_lock)
            _openDownload = openDownload;
    }

    public void EnableManualCheck(Func<CancellationToken, Task> checkForUpdates)
    {
        ArgumentNullException.ThrowIfNull(checkForUpdates);
        lock (_lock)
            _checkForUpdates = checkForUpdates;
        NotifyStateChanged();
    }

    public void SetInstalledVersion(string? version)
    {
        lock (_lock)
            _installedVersion = Clean(version);
        NotifyStateChanged();
    }

    public void MarkChecking()
    {
        lock (_lock)
        {
            if (_installedVersion is null && _snapshot.Version is { Length: > 0 } version)
                _installedVersion = version;
        }

        SetSnapshot(new DesktopUpdateSnapshot(DesktopUpdateStatus.Checking));
    }

    public void MarkIdle(string? version = null)
    {
        lock (_lock)
        {
            if (_installedVersion is null)
                _installedVersion = Clean(version);
        }

        SetSnapshot(new DesktopUpdateSnapshot(DesktopUpdateStatus.Idle, Clean(version)));
    }

    public void MarkStoreManaged(string? version = null)
    {
        lock (_lock)
        {
            if (_installedVersion is null)
                _installedVersion = Clean(version);
        }

        SetSnapshot(new DesktopUpdateSnapshot(DesktopUpdateStatus.StoreManaged, Clean(version)));
    }

    public void MarkManualUpdateAvailable(string version, Uri releaseUri)
    {
        ArgumentNullException.ThrowIfNull(releaseUri);
        SetSnapshot(new DesktopUpdateSnapshot(
            DesktopUpdateStatus.ManualUpdateAvailable,
            Clean(version),
            ReleaseUri: releaseUri));
    }

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
