namespace Lorekeeper.Models;

/// <summary>
/// One stateless AI Console turn: the command sent, the system prompt the LLM saw,
/// the tool-call timeline, and the final short response. Persisted per project so the
/// history modal can display past turns across sessions.
/// </summary>
public class AiConsoleEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    /// <summary>Chapter that was open when the turn was kicked off, if any.</summary>
    public Guid? ChapterId { get; set; }

    public required string Command { get; set; }

    /// <summary>Snapshot of the assembled system prompt at the moment the turn started.</summary>
    public required string SystemPromptSnapshot { get; set; }

    /// <summary>Final short text response from the model, if any.</summary>
    public string? ResponseText { get; set; }

    /// <summary>JSON array of tool-call records: { name, arguments, result, error, startedAt, completedAt }.</summary>
    public string ToolCallsJson { get; set; } = "[]";

    public AiConsoleEntryStatus Status { get; set; } = AiConsoleEntryStatus.Pending;

    public string? ErrorMessage { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

public enum AiConsoleEntryStatus
{
    Pending,
    Completed,
    Failed,
    Cancelled,
}
