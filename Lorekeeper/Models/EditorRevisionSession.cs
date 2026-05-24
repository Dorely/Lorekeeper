namespace Lorekeeper.Models;

public class EditorRevisionSession
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid JobId { get; set; }
    public EditorRevisionJob Job { get; set; } = null!;

    public int Order { get; set; }

    public Guid ChapterId { get; set; }

    public string ChapterTitle { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public string Instructions { get; set; } = string.Empty;

    public string OriginalChapterBody { get; set; } = string.Empty;

    public int? ProviderId { get; set; }

    public string ProviderName { get; set; } = string.Empty;

    public string ModelName { get; set; } = string.Empty;

    public EditorRevisionSessionStatus Status { get; set; } = EditorRevisionSessionStatus.Queued;

    public string Summary { get; set; } = string.Empty;

    public string Rationale { get; set; } = string.Empty;

    public string MutationKind { get; set; } = string.Empty;

    public int? StartLine { get; set; }

    public int? EndLine { get; set; }

    public string ReplacementText { get; set; } = string.Empty;

    public string Notes { get; set; } = string.Empty;

    public string ProposalJson { get; set; } = "{}";

    public string RawResponse { get; set; } = string.Empty;

    public string? ErrorMessage { get; set; }

    public double? DurationMs { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    public ICollection<EditorRevisionMessage> Messages { get; set; } = [];
}

public enum EditorRevisionSessionStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Invalid,
    Cancelled,
}
