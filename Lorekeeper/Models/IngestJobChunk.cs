namespace Lorekeeper.Models;

public class IngestJobChunk
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid JobId { get; set; }
    public IngestJob Job { get; set; } = null!;

    public Guid SourceChunkId { get; set; }
    public IngestSourceChunk SourceChunk { get; set; } = null!;

    public int SourceChunkIndex { get; set; }
    public IngestJobChunkStatus Status { get; set; } = IngestJobChunkStatus.Pending;
    public string Summary { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public int CreatedEntityCount { get; set; }
    public int CreatedRelationshipCount { get; set; }
    public int? LlmTokenCount { get; set; }
    public bool? LlmTokenCountIsExact { get; set; }
    public string? LlmTokenCountMethod { get; set; }
    public string? LlmTokenEncodingName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public enum IngestJobChunkStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Stopped,
}
