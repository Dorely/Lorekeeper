using System.ComponentModel.DataAnnotations.Schema;
using Lorekeeper.Manuscripts;

namespace Lorekeeper.Models;

public class ContestCandidate
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid BatchId { get; set; }
    public ContestBatch Batch { get; set; } = null!;

    public int Order { get; set; }

    public int ProviderId { get; set; }

    public string ProviderName { get; set; } = string.Empty;

    public string ModelName { get; set; } = string.Empty;

    public ContestCandidateStatus Status { get; set; } = ContestCandidateStatus.Pending;

    public string Summary { get; set; } = string.Empty;

    public string MutationsJson { get; set; } = "[]";

    public string ProposedManuscriptJson { get; set; } = string.Empty;

    [NotMapped]
    public string ProposedPlainText =>
        ManuscriptCodec.ProjectPlainText(ManuscriptCodec.Deserialize(ProposedManuscriptJson));

    public string RawResponse { get; set; } = string.Empty;

    public string ReviewStateJson { get; set; } = "{}";

    public string? Notes { get; set; }

    public string? ErrorMessage { get; set; }

    public double? DurationMs { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

public enum ContestCandidateStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Invalid,
    Selected,
    Rejected,
}
