using Lorekeeper.Models;
using Lorekeeper.Outline;

namespace Lorekeeper.AiConsole;

/// <summary>
/// Stateless AI Console runner. Each call is a single turn: builds the system prompt
/// from the Context Feed, invokes the default LLM provider with tool access, runs a
/// tool-call loop, and persists the full timeline as an <see cref="AiConsoleEntry"/>.
/// </summary>
public interface IAiConsoleService
{
    Task<AiConsoleEntry> RunAsync(
        Guid projectId,
        Guid? currentChapterId,
        string command,
        CancellationToken cancellationToken = default);
}

/// <summary>Per-request scope of a tool invocation.</summary>
public sealed class AiConsoleContext(
    Guid projectId,
    Guid? currentChapterId,
    Guid entryId,
    bool reviewEdits,
    OutlineToolStagingContext? outlineStaging,
    AiConsoleChangeStagingContext? consoleStaging)
{
    public Guid ProjectId { get; } = projectId;
    public Guid? CurrentChapterId { get; } = currentChapterId;
    public Guid EntryId { get; } = entryId;
    public bool ReviewEdits { get; } = reviewEdits;
    public OutlineToolStagingContext? OutlineStaging { get; } = outlineStaging;
    public AiConsoleChangeStagingContext? ConsoleStaging { get; } = consoleStaging;

    public void BeginToolCall(string toolCallId, string toolName, string argumentsJson)
    {
        OutlineStaging?.BeginToolCall(EntryId, toolCallId, toolName, argumentsJson);
        ConsoleStaging?.BeginToolCall(EntryId, toolCallId, toolName, argumentsJson);
    }
}

/// <summary>One serialized record in <see cref="AiConsoleEntry.ToolCallsJson"/>.</summary>
public sealed record AiToolCallRecord(
    string Name,
    string Arguments,
    string? Result,
    string? Error,
    DateTime StartedAt,
    DateTime CompletedAt);
