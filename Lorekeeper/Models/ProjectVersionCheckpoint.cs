namespace Lorekeeper.Models;

public enum ProjectVersionCheckpointKind
{
    Initial,
    Manual,
    Assistant,
    Imported,
    Restored,
}

public enum ProjectVersionCheckpointSource
{
    Local,
    Remote,
    Recovery,
}

/// <summary>
/// Immutable semantic metadata for one project snapshot/checkpoint.
/// Snapshot bytes and Git objects are owned by the version-control layer; this
/// row records the hashes and provenance needed to reconcile them with the app.
/// </summary>
public sealed class ProjectVersionCheckpoint
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectVersionRepositoryId { get; set; }
    public ProjectVersionRepository Repository { get; set; } = null!;

    public int ManifestSchemaVersion { get; set; } = 1;
    public long CreativeRevision { get; set; }
    public required string ContentHash { get; set; }
    public required string ManifestHash { get; set; }
    public string? CommitSha { get; set; }
    public string? ParentCommitSha { get; set; }
    public ProjectVersionCheckpointKind Kind { get; set; }
    public ProjectVersionCheckpointSource Source { get; set; } = ProjectVersionCheckpointSource.Local;
    public string? Message { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
