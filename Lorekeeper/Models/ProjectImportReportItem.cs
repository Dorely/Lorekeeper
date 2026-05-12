namespace Lorekeeper.Models;

public class ProjectImportReportItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid JobId { get; set; }
    public ProjectImportJob Job { get; set; } = null!;

    public ProjectImportReportItemKind Kind { get; set; }
    public ProjectImportReportItemStatus Status { get; set; } = ProjectImportReportItemStatus.Active;
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public string ResourceKey { get; set; } = string.Empty;
    public Guid? EntityId { get; set; }
    public long? GraphNodeId { get; set; }
    public long? GraphEdgeId { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public string ErrorMessage { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum ProjectImportReportItemKind
{
    Validation,
    Structural,
    EntityType,
    Entity,
    Relationship,
    Indexing,
    Warning,
}

public enum ProjectImportReportItemStatus
{
    Pending,
    Active,
    Failed,
}
