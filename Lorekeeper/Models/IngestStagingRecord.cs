namespace Lorekeeper.Models;

public class IngestStagingRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid JobId { get; set; }
    public IngestJob Job { get; set; } = null!;

    public Guid SourceId { get; set; }
    public IngestSource Source { get; set; } = null!;

    public Guid? SourceChunkId { get; set; }
    public IngestSourceChunk? SourceChunk { get; set; }
    public int? SourceChunkIndex { get; set; }

    public IngestStagingRecordKind Kind { get; set; }
    public IngestStagingRecordStatus Status { get; set; } = IngestStagingRecordStatus.Active;

    public Guid? EntityId { get; set; }
    public long? GraphNodeId { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string AliasesJson { get; set; } = "[]";
    public string WikiSectionsJson { get; set; } = "[]";
    public string Notes { get; set; } = string.Empty;

    public Guid? FromEntityId { get; set; }
    public Guid? ToEntityId { get; set; }
    public long? GraphEdgeId { get; set; }
    public string EdgeType { get; set; } = string.Empty;

    public string PayloadJson { get; set; } = "{}";
    public string ErrorMessage { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedAt { get; set; }
    public DateTime? FinalizedAt { get; set; }
}

public enum IngestStagingRecordKind
{
    Entity,
    Relationship,
    SourceChunkNote,
}

public enum IngestStagingRecordStatus
{
    Active,
    Deleted,
    Finalized,
    Failed,
}
