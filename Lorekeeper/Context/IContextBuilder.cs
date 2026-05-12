using Lorekeeper.Models;

namespace Lorekeeper.Context;

/// <summary>
/// Builds the user-visible Context Feed and the assembled system prompt sent to the LLM.
/// Every enabled item's body is concatenated into the system prompt — the Context Feed
/// IS the preview, no separate "assembled prompt" view exists.
/// </summary>
public interface IContextBuilder
{
    /// <summary>Build the assembly for the given project + (optional) currently-open chapter.</summary>
    Task<ContextAssembly> BuildAsync(Project project, Chapter? currentChapter, CancellationToken cancellationToken = default);
}

/// <summary>One renderable item in the Context Feed.</summary>
public sealed record ContextItem(
    string Key,
    ContextItemKind Kind,
    string Label,
    string Body,
    bool IsEnabled,
    bool IsRemovable,
    string? Badge = null,
    string? Reason = null);

public enum ContextItemKind
{
    SystemPrompt,
    AssistantWorkflow,
    CurrentChapter,
    ProjectOutline,
    ProjectFacts,
    GraphTraversalMap,
    WritingSample,
    Entity,
    ChapterReference,
    ActReference,
    IngestSourceReference,
    IngestSourceChunkReference,
}

public sealed record ContextAssembly(IReadOnlyList<ContextItem> Items)
{
    /// <summary>
    /// Concatenates every enabled item's body, in display order, separated by labeled
    /// section headers. The result is the literal system message sent to the LLM.
    /// </summary>
    public string Assemble(string? assistantWorkflowOverride = null)
    {
        var sb = new System.Text.StringBuilder();
        var first = true;
        foreach (var item in Items)
        {
            if (!item.IsEnabled) continue;
            if (!first) sb.Append("\n\n");
            first = false;
            sb.Append("## ").Append(item.Label).Append('\n');
            sb.Append(assistantWorkflowOverride is not null && item.Kind == ContextItemKind.AssistantWorkflow
                ? assistantWorkflowOverride
                : item.Body);
        }
        return sb.ToString();
    }
}
