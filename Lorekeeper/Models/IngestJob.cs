namespace Lorekeeper.Models;

public class IngestJob
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public Guid SourceId { get; set; }
    public IngestSource Source { get; set; } = null!;

    public required string Instructions { get; set; }
    public IngestJobStatus Status { get; set; } = IngestJobStatus.Queued;

    public int TotalSourceChunks { get; set; }
    public int CompletedSourceChunks { get; set; }
    public int CreatedEntityCount { get; set; }
    public int CreatedRelationshipCount { get; set; }

    public string? CurrentMessage { get; set; }
    public string? ErrorMessage { get; set; }
    public int? ProviderId { get; set; }
    public LlmProvider? Provider { get; set; }
    public string? ModelName { get; set; }
    public string? EncodingName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public ICollection<IngestJobChunk> Chunks { get; set; } = [];
    public ICollection<IngestReportItem> ReportItems { get; set; } = [];
    public ICollection<IngestJobEvent> Events { get; set; } = [];
}

public enum IngestJobStatus
{
    Queued,
    Running,
    StopRequested,
    Stopped,
    Completed,
    Failed,
}