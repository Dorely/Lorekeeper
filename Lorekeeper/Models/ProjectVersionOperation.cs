namespace Lorekeeper.Models;

public enum ProjectVersionOperationKind
{
    Initialize,
    Checkpoint,
    Clone,
    Fetch,
    Pull,
    Push,
    AutoPush,
    Restore,
    Relink,
}

public enum ProjectVersionOperationStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Canceled,
}

/// <summary>
/// Durable journal entry for a version-control operation. A running row is the
/// crash-recovery boundary; operation workers must make retry/resume decisions
/// from this row rather than circuit or process memory.
/// </summary>
public sealed class ProjectVersionOperation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectVersionRepositoryId { get; set; }
    public ProjectVersionRepository Repository { get; set; } = null!;

    /// <summary>
    /// Set only for durable automatic checkpoint-push intents. Keeping the
    /// attachment identity on the journal row prevents a reused remote name
    /// from ever consuming another attachment's work.
    /// </summary>
    public Guid? ProjectGitRemoteId { get; set; }

    /// <summary>Exact local commit this automatic push intends to publish.</summary>
    public string? TargetCommitSha { get; set; }

    public ProjectVersionOperationKind Kind { get; set; }
    public ProjectVersionOperationStatus Status { get; set; } = ProjectVersionOperationStatus.Pending;
    public string? RequestKey { get; set; }
    public bool IsResumable { get; set; }
    public int AttemptCount { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? HeartbeatAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
