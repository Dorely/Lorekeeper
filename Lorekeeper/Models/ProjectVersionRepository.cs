namespace Lorekeeper.Models;

/// <summary>
/// The app-managed version-control identity for one project. The creative
/// revision and cached head fields are reconciliation hints; the canonical
/// content hash remains the source of truth for dirty-state decisions.
/// </summary>
public sealed class ProjectVersionRepository
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public long CreativeRevision { get; set; }
    public long? LastCheckpointRevision { get; set; }
    public string? LastCheckpointContentHash { get; set; }
    public string? HeadCommitSha { get; set; }
    public string? HeadContentHash { get; set; }
    public DateTime? LastCheckpointAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<ProjectVersionCheckpoint> Checkpoints { get; set; } = [];
    public ICollection<ProjectVersionOperation> Operations { get; set; } = [];
    public ICollection<ProjectGitRemote> GitRemotes { get; set; } = [];
}
