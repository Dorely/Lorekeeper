namespace Lorekeeper.VersionHistory.Services;

/// <summary>
/// Process-local notifications that keep independently rendered history controls
/// synchronized after a checkpoint changes the local timeline.
/// </summary>
public sealed class ProjectVersionHistoryUiEvents
{
    private readonly Lock _operationLock = new();
    private readonly Dictionary<Guid, int> _activeOperations = [];

    public event Action<Guid>? CheckpointCreated;
    public event Action<Guid>? OperationStateChanged;
    public event Action<Guid>? RemoteSyncChanged;
    public event Action<Guid>? ReviewStateChanged;

    public bool IsProjectOperationActive(Guid projectId)
    {
        ValidateProjectId(projectId);
        lock (_operationLock)
            return _activeOperations.ContainsKey(projectId);
    }

    public IDisposable BeginProjectOperation(Guid projectId)
    {
        ValidateProjectId(projectId);
        var becameActive = false;
        lock (_operationLock)
        {
            if (_activeOperations.TryGetValue(projectId, out var count))
            {
                _activeOperations[projectId] = count + 1;
            }
            else
            {
                _activeOperations.Add(projectId, 1);
                becameActive = true;
            }
        }

        if (becameActive)
            Publish(OperationStateChanged, projectId);
        return new ProjectOperationLease(this, projectId);
    }

    public void PublishCheckpointCreated(Guid projectId)
    {
        ValidateProjectId(projectId);
        Publish(CheckpointCreated, projectId);
    }

    public void PublishRemoteSyncChanged(Guid projectId)
    {
        ValidateProjectId(projectId);
        Publish(RemoteSyncChanged, projectId);
    }

    /// <summary>
    /// Notifies rendered review surfaces after a durable review setting,
    /// approval, undo, or contest state transition. This is intentionally
    /// separate from checkpoint notifications because a live review mutation
    /// can leave the approved Git head unchanged.
    /// </summary>
    public void PublishReviewStateChanged(Guid projectId)
    {
        ValidateProjectId(projectId);
        Publish(ReviewStateChanged, projectId);
    }

    private static void Publish(Action<Guid>? handlers, Guid projectId)
    {
        foreach (var subscriber in handlers?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action<Guid>)subscriber)(projectId);
            }
            catch
            {
                // A rendered surface can disappear between durable checkpoint
                // completion and notification. Other surfaces must still refresh.
            }
        }
    }

    private void EndProjectOperation(Guid projectId)
    {
        var becameIdle = false;
        lock (_operationLock)
        {
            if (!_activeOperations.TryGetValue(projectId, out var count))
                return;

            if (count == 1)
            {
                _activeOperations.Remove(projectId);
                becameIdle = true;
            }
            else
            {
                _activeOperations[projectId] = count - 1;
            }
        }

        if (becameIdle)
            Publish(OperationStateChanged, projectId);
    }

    private static void ValidateProjectId(Guid projectId)
    {
        if (projectId == Guid.Empty)
            throw new ArgumentException("A project ID is required.", nameof(projectId));
    }

    private sealed class ProjectOperationLease(ProjectVersionHistoryUiEvents owner, Guid projectId) : IDisposable
    {
        private ProjectVersionHistoryUiEvents? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndProjectOperation(projectId);
    }
}
