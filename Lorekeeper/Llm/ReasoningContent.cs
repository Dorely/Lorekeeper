using Microsoft.Extensions.AI;

namespace Lorekeeper.Llm;

/// <summary>
/// Reasoning/thinking summary content streamed by the LLM. Distinguishable from
/// regular <see cref="TextContent"/> so callers can render it differently.
/// </summary>
public sealed class ReasoningContent(string text) : AIContent
{
    public string Text { get; } = text;
}
