namespace Lorekeeper.Models;

/// <summary>
/// One AI-proposed tool mutation waiting for user approval.
/// The JSON fields store the tool payload, result, diff snapshots, and dependency metadata.
/// </summary>
public class AiChange
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid BatchId { get; set; }
    public AiChangeBatch Batch { get; set; } = null!;

    public int Order { get; set; }

    public string ToolCallId { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;
    public string ArgumentsJson { get; set; } = "{}";

    public string Summary { get; set; } = string.Empty;
    public string BeforeJson { get; set; } = "null";
    public string AfterJson { get; set; } = "null";
    public string? DraftAfterJson { get; set; }
    public string? ReviewStateJson { get; set; }
    public string ResultJson { get; set; } = "{}";

    public string ResourceKind { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string CreatedResourceIdsJson { get; set; } = "[]";
    public string ReferencedResourceIdsJson { get; set; } = "[]";
    public string DependsOnChangeIdsJson { get; set; } = "[]";

    public AiChangeStatus Status { get; set; } = AiChangeStatus.Pending;
    public string? RejectionMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }
}

public enum AiChangeStatus
{
    Pending,
    Applied,
    Rejected,
    Conflict,
}
