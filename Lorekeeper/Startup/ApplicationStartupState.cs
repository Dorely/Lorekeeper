namespace Lorekeeper.Startup;

public enum ApplicationStartupStatus
{
    Starting,
    Migrating,
    Initializing,
    Ready,
    RecoveryRequired,
    Failed,
}

public sealed record ApplicationStartupSnapshot(
    ApplicationStartupStatus Status,
    string Title,
    string Detail,
    int ProgressPercent,
    int? Step = null,
    int? TotalSteps = null,
    string? Error = null)
{
    public bool CanUseDatabase => Status is ApplicationStartupStatus.Ready
        or ApplicationStartupStatus.RecoveryRequired;
}

public sealed record DatabaseStartupMigrationProgress(
    string Title,
    string Detail,
    int Step,
    int TotalSteps);

public interface IApplicationStartupState
{
    ApplicationStartupSnapshot Current { get; }
    event Action? Changed;
    Task<bool> WaitForDatabaseReadyAsync(CancellationToken cancellationToken = default);
}

public sealed class ApplicationStartupState(
    ILogger<ApplicationStartupState> logger) : IApplicationStartupState, IProgress<DatabaseStartupMigrationProgress>
{
    private readonly object _sync = new();
    private readonly TaskCompletionSource<bool> _databaseReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ApplicationStartupSnapshot _current = new(
        ApplicationStartupStatus.Starting,
        "Opening Lorekeeper",
        "Preparing your narrative workspace.",
        4);

    public ApplicationStartupSnapshot Current
    {
        get
        {
            lock (_sync)
                return _current;
        }
    }

    public event Action? Changed;

    public Task<bool> WaitForDatabaseReadyAsync(CancellationToken cancellationToken = default) =>
        _databaseReady.Task.WaitAsync(cancellationToken);

    public void Report(DatabaseStartupMigrationProgress value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var totalSteps = Math.Max(1, value.TotalSteps);
        var step = Math.Clamp(value.Step, 0, totalSteps);
        var progressPercent = 10 + (int)Math.Round(step * 60d / totalSteps);
        Set(new(
            ApplicationStartupStatus.Migrating,
            value.Title,
            value.Detail,
            progressPercent,
            step,
            totalSteps));
    }

    public void ReportInitialization(string title, string detail, int progressPercent) =>
        Set(new(
            ApplicationStartupStatus.Initializing,
            title,
            detail,
            Math.Clamp(progressPercent, 70, 98)));

    public void Complete()
    {
        Set(new(
            ApplicationStartupStatus.Ready,
            "Lorekeeper is ready",
            "Opening your workspace.",
            100));
        _databaseReady.TrySetResult(true);
    }

    public void CompleteRecovery()
    {
        Set(new(
            ApplicationStartupStatus.RecoveryRequired,
            "Data recovery is ready",
            "A protected migration backup is available. Opening recovery details.",
            100));
        _databaseReady.TrySetResult(true);
    }

    public void Fail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Set(new(
            ApplicationStartupStatus.Failed,
            "Lorekeeper could not finish starting",
            "Your database was not opened for normal work.",
            100,
            Error: exception.Message));
        _databaseReady.TrySetResult(false);
    }

    private void Set(ApplicationStartupSnapshot snapshot)
    {
        lock (_sync)
            _current = snapshot;
        var changed = Changed;
        if (changed is null)
            return;
        foreach (Action handler in changed.GetInvocationList())
        {
            try
            {
                handler();
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "A startup status subscriber could not process an update.");
            }
        }
    }
}

public sealed record ApplicationStartupOptions(TimeSpan MinimumSplashDuration);
