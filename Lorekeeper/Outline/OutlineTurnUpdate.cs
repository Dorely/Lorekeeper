using System.Text.Json.Serialization;
using Lorekeeper.ChatTurns;

namespace Lorekeeper.Outline;

/// <summary>
/// Discriminated update type yielded by <see cref="IOutlineCollaborationService.SendAsync"/>
/// during a collaborative chat turn. The UI consumes the stream and updates state per kind.
/// </summary>
[JsonDerivedType(typeof(TextDelta), typeDiscriminator: "text")]
[JsonDerivedType(typeof(ReasoningDelta), typeDiscriminator: "reasoning")]
[JsonDerivedType(typeof(ToolCallStarted), typeDiscriminator: "tool-start")]
[JsonDerivedType(typeof(ToolCallArgumentsDelta), typeDiscriminator: "tool-args")]
[JsonDerivedType(typeof(ToolCallCompleted), typeDiscriminator: "tool-end")]
[JsonDerivedType(typeof(ContextTrimmed), typeDiscriminator: "context-trimmed")]
[JsonDerivedType(typeof(PendingAiChangeCreated), typeDiscriminator: "pending-change")]
[JsonDerivedType(typeof(AssistantMessageCompleted), typeDiscriminator: "assistant-end")]
[JsonDerivedType(typeof(OutlineMutated), typeDiscriminator: "outline-mutated")]
[JsonDerivedType(typeof(TurnError), typeDiscriminator: "error")]
public abstract record OutlineTurnUpdate;

/// <summary>Streaming text chunk from the assistant.</summary>
public sealed record TextDelta(string Text) : OutlineTurnUpdate;

/// <summary>Streaming model-reasoning chunk from the assistant.</summary>
public sealed record ReasoningDelta(string Text) : OutlineTurnUpdate;

/// <summary>A function call has been parsed; tool invocation is about to begin.</summary>
public sealed record ToolCallStarted(string CallId, string ToolName, string ArgumentsJson, bool ArgumentsComplete = true) : OutlineTurnUpdate;

/// <summary>Streaming argument text for an already-started function call.</summary>
public sealed record ToolCallArgumentsDelta(string CallId, string ArgumentsDelta, bool ArgumentsComplete) : OutlineTurnUpdate;

/// <summary>A function call has finished. <paramref name="Error"/> is null on success.</summary>
public sealed record ToolCallCompleted(string CallId, string ToolName, string? Result, string? Error, double DurationMs) : OutlineTurnUpdate;

public sealed record ContextTrimmed(ChatCompactionResult Result) : OutlineTurnUpdate;

/// <summary>A mutating tool call was queued for approval instead of being applied immediately.</summary>
public sealed record PendingAiChangeCreated(Guid BatchId, Guid ChangeId, string ToolCallId, string ToolName, string Summary) : OutlineTurnUpdate;

/// <summary>The assistant turn has fully concluded (no more tool round-trips).</summary>
public sealed record AssistantMessageCompleted(Guid MessageId) : OutlineTurnUpdate;

/// <summary>Signals the UI to refresh the live outline tree (a mutating tool just ran).</summary>
public sealed record OutlineMutated : OutlineTurnUpdate;

/// <summary>A non-recoverable error or cancellation. The turn is terminated.</summary>
public sealed record TurnError(string Message, bool Cancelled) : OutlineTurnUpdate;
