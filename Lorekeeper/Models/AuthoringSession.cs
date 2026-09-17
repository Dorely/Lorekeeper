namespace Lorekeeper.Models;

public sealed class AuthoringSession
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid ProcessIncarnationId { get; set; }
    public long LastSequence { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class AuthoringBatchReceipt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid SessionId { get; set; }
    public Guid BatchId { get; set; }
    public long Sequence { get; set; }
    public string RequestHash { get; set; } = string.Empty;
    public string ResultJson { get; set; } = string.Empty;
    public DateTime? AcknowledgedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class AuthoringTargetGeneration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string TargetId { get; set; } = string.Empty;
    public long Generation { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
