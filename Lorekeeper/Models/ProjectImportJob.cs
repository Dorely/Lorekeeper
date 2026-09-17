namespace Lorekeeper.Models;

public class ProjectImportJob
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public required string FileName { get; set; }
    public required string StagedFileKey { get; set; }
    public long StagedLength { get; set; }
    public required string StagedSha256 { get; set; }
    public ProjectImportInputKind InputKind { get; set; }
    public string FormatId { get; set; } = string.Empty;
    public int FormatVersion { get; set; }
    public string ExportKind { get; set; } = string.Empty;

    public ProjectImportJobStatus Status { get; set; } = ProjectImportJobStatus.Uploaded;
    public int TotalSteps { get; set; }
    public int CompletedSteps { get; set; }
    public int CreatedNodeCount { get; set; }
    public int MergedNodeCount { get; set; }
    public int CreatedEdgeCount { get; set; }
    public int MergedEdgeCount { get; set; }
    public int CreatedActCount { get; set; }
    public int CreatedChapterCount { get; set; }
    public int CreatedBeatCount { get; set; }
    public int WarningCount { get; set; }

    public string? CurrentMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public ICollection<ProjectImportReportItem> ReportItems { get; set; } = [];
}

public enum ProjectImportJobStatus
{
    Uploaded,
    Staged,
    Validated,
    Applying,
    Committed,
    Indexing,
    Completed,
    CompletedWithWarnings,
    Failed,
    Cancelled,
}

public enum ProjectImportInputKind
{
    LorekeeperArchive,
    LegacyJson,
}
