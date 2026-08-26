using System.ComponentModel.DataAnnotations.Schema;
using Lorekeeper.Manuscripts;

namespace Lorekeeper.Models;

public class ContestBatch
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public Guid ConversationId { get; set; }

    public Guid? AssistantMessageId { get; set; }

    public Guid ChapterId { get; set; }
    public string ContentTargetKind { get; set; } = "Core";
    public Guid? ContentTargetEditionId { get; set; }

    public string ChapterTitle { get; set; } = string.Empty;

    public string OriginalManuscriptJson { get; set; } = string.Empty;

    /// <summary>
    /// The revision and semantic content hash captured when the contest started.
    /// A contest never mutates the live manuscript until it is resolved, so these
    /// values are the optimistic-concurrency fence for resolution.
    /// </summary>
    public long OriginalManuscriptRevision { get; set; }

    public string OriginalManuscriptHash { get; set; } = string.Empty;

    [NotMapped]
    public string OriginalPlainText =>
        ManuscriptCodec.ProjectPlainText(ManuscriptCodec.Deserialize(OriginalManuscriptJson));

    public Guid? WinningCandidateId { get; set; }

    /// <summary>The candidate currently selected in the review selector.</summary>
    public Guid? SelectedCandidateId { get; set; }

    public string ContextSnapshotJson { get; set; } = "{}";

    public ContestBatchStatus Status { get; set; } = ContestBatchStatus.Running;

    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    public ICollection<ContestCandidate> Candidates { get; set; } = [];

    [NotMapped]
    public bool IsUnresolved => Status is ContestBatchStatus.Running
        or ContestBatchStatus.Completed
        or ContestBatchStatus.Failed;
}

public enum ContestBatchStatus
{
    Running,
    Completed,
    Failed,
    Cancelled,
    Finished,
    Resolved,
    Discarded,
}
