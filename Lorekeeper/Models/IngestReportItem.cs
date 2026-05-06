namespace Lorekeeper.Models;

public class IngestReportItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid JobId { get; set; }
    public IngestJob Job { get; set; } = null!;

    public Guid? SourceChunkId { get; set; }
    public IngestSourceChunk? SourceChunk { get; set; }

    public IngestReportItemKind Kind { get; set; }
    public IngestReportItemStatus Status { get; set; } = IngestReportItemStatus.Active;
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public string Evidence { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public Guid? EntityId { get; set; }
    public long? GraphNodeId { get; set; }
    public long? GraphEdgeId { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public string ErrorMessage { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedAt { get; set; }
}

public enum IngestReportItemKind
{
    Entity,
    Relationship,
    SourceChunkNote,
}

public enum IngestReportItemStatus
{
    Pending,
    Active,
    Deleted,
    Failed,
}