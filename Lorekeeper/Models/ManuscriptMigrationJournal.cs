namespace Lorekeeper.Models;

public sealed class ManuscriptMigrationJournal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string MigrationName { get; set; } = string.Empty;
    public int SourceSchemaVersion { get; set; }
    public int TargetSchemaVersion { get; set; }
    public ManuscriptMigrationPhase Phase { get; set; } = ManuscriptMigrationPhase.Preflight;
    public ManuscriptMigrationStatus Status { get; set; } = ManuscriptMigrationStatus.Running;
    public string BackupPath { get; set; } = string.Empty;
    public int ChapterCount { get; set; }
    public int ContestBatchCount { get; set; }
    public int ContestCandidateCount { get; set; }
    public int RevisionSessionCount { get; set; }
    public string SourceHash { get; set; } = string.Empty;
    public string TargetHash { get; set; } = string.Empty;
    public string ValidationReportJson { get; set; } = "{}";
    public string? ErrorDetail { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

public enum ManuscriptMigrationPhase
{
    Preflight,
    Backup,
    Expand,
    Transform,
    Validate,
    Contract,
    Complete,
    Recovery,
}

public enum ManuscriptMigrationStatus
{
    Running,
    Completed,
    Failed,
    Restored,
}
