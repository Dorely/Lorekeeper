namespace Lorekeeper.Models;

public enum AuthoringHistoryDocumentKind
{
    CoreChapter,
    EditionChapter,
    PublicationSection,
    PageComposition,
    CoreCover,
    ReleaseCover
}

public enum AuthoringHistoryOrigin
{
    Manual,
    Assistant
}

public enum AuthoringHistoryDependencyKind
{
    ProjectImage,
    ProjectFont,
    PageComposition
}

public enum AuthoringTurnHistoryBatchStatus
{
    Open,
    Completed,
    Stopped,
    Failed,
    Cancelled,
    Recovered
}

public class AuthoringHistoryStream
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public string StreamKey { get; set; } = string.Empty;
    public AuthoringHistoryDocumentKind DocumentKind { get; set; }
    public Guid DocumentId { get; set; }
    public Guid? EditionId { get; set; }
    public byte[] BaselineSnapshot { get; set; } = [];
    public string BaselineHash { get; set; } = string.Empty;
    public long FirstSequence { get; set; }
    public long LastSequence { get; set; }
    public long CursorSequence { get; set; }
    public long Revision { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<AuthoringHistoryEntry> Entries { get; set; } = [];
    public ICollection<AuthoringTurnHistoryBatch> TurnBatches { get; set; } = [];
}

public class AuthoringHistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StreamId { get; set; }
    public AuthoringHistoryStream Stream { get; set; } = null!;
    public long Sequence { get; set; }
    public string ActionLabel { get; set; } = string.Empty;
    public AuthoringHistoryOrigin Origin { get; set; }
    public Guid? AssistantTurnId { get; set; }
    public byte[] ResultSnapshot { get; set; } = [];
    public string ResultHash { get; set; } = string.Empty;
    public string SelectionJson { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<AuthoringHistoryDependency> Dependencies { get; set; } = [];
}

public class AuthoringTurnHistoryBatch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StreamId { get; set; }
    public AuthoringHistoryStream Stream { get; set; } = null!;
    public Guid AssistantTurnId { get; set; }
    public string ActionLabel { get; set; } = string.Empty;
    public byte[] BeforeSnapshot { get; set; } = [];
    public string BeforeHash { get; set; } = string.Empty;
    public byte[] AfterSnapshot { get; set; } = [];
    public string AfterHash { get; set; } = string.Empty;
    public string SelectionJson { get; set; } = string.Empty;
    public AuthoringTurnHistoryBatchStatus Status { get; set; } = AuthoringTurnHistoryBatchStatus.Open;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinalizedAt { get; set; }
    public ICollection<AuthoringHistoryDependency> Dependencies { get; set; } = [];
}

public class AuthoringHistoryDependency
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? EntryId { get; set; }
    public AuthoringHistoryEntry? Entry { get; set; }
    public Guid? TurnBatchId { get; set; }
    public AuthoringTurnHistoryBatch? TurnBatch { get; set; }
    public AuthoringHistoryDependencyKind Kind { get; set; }
    public Guid ResourceId { get; set; }
}
