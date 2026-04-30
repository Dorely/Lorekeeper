using Lorekeeper.Models;

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
public sealed record AiConsoleContext(Guid ProjectId, Guid? CurrentChapterId);

/// <summary>One serialized record in <see cref="AiConsoleEntry.ToolCallsJson"/>.</summary>
public sealed record AiToolCallRecord(
    string Name,
    string Arguments,
    string? Result,
    string? Error,
    DateTime StartedAt,
    DateTime CompletedAt);
