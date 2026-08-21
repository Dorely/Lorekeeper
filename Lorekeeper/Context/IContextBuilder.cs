using System.Text.Json;
using Lorekeeper.EntityVisuals;
using Lorekeeper.Llm;
using Lorekeeper.Models;
using Lorekeeper.Manuscripts;

namespace Lorekeeper.Context;

public enum ContextBuildPurpose
{
    Editor,
    EditorRevision,
    Images,
    Research,
    Publish,
}

public sealed record ContextBuildRequest(
    Project Project,
    Chapter? ActiveChapter = null,
    string UserMessage = "",
    ContextBuildPurpose Purpose = ContextBuildPurpose.Editor,
    string? OperatingRules = null,
    EditorContentTarget ContentTarget = default);

/// <summary>
/// Builds the visible Context Feed and the literal system-role message. The request is
/// turn-aware so automatic context can be selected against the user's current intent.
/// </summary>
public interface IContextBuilder
{
    Task<ContextAssembly> BuildAsync(
        ContextBuildRequest request,
        CancellationToken cancellationToken = default);
}

public enum ContextItemOrigin
{
    Application,
    User,
    DirectLink,
    PreviousChapter,
    LexicalRetrieval,
    VectorRetrieval,
    GraphRetrieval,
    WritingSample,
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
    string? Reason = null,
    IReadOnlyList<EntityVisualContextReference>? Visuals = null,
    ContextItemOrigin Origin = ContextItemOrigin.Application,
    bool IsTransient = false,
    bool IsProtected = false,
    int EstimatedTokens = 0);

public enum ContextItemKind
{
    SystemInstructions,
    AssistantWorkflow,
    DynamicGuidance,
    ProjectGuidance,
    BookBrief,
    CurrentChapter,
    ManuscriptAnnotations,
    ProjectPageSetup,
    ManuscriptStyles,
    ProjectOutline,
    ProjectFacts,
    ProjectReferences,
    WritingSample,
    Entity,
    ChapterReference,
    ActReference,
    IngestSourceReference,
    IngestSourceChunkReference,
    ProjectImage,
    ChapterVisualLayout,
}

public sealed record ContextTraceEntry(
    string Key,
    string Label,
    ContextItemOrigin Origin,
    bool Included,
    bool Protected,
    bool Transient,
    int EstimatedTokens,
    string Reason,
    string Excerpt);

public sealed record ContextAssembly(
    IReadOnlyList<ContextItem> Items,
    IReadOnlyList<ContextTraceEntry>? OmittedItems = null)
{
    public IReadOnlyList<EntityVisualContextReference> Visuals => Items
        .Where(item => item.IsEnabled)
        .SelectMany(item => item.Visuals ?? [])
        .ToList();

    public IReadOnlyList<ContextTraceEntry> Trace =>
        Items.Select(item => new ContextTraceEntry(
                item.Key,
                item.Label,
                item.Origin,
                item.IsEnabled,
                item.IsProtected,
                item.IsTransient,
                item.EstimatedTokens,
                item.Reason ?? string.Empty,
                Excerpt(item.Body)))
            .Concat(OmittedItems ?? [])
            .ToList();

    /// <summary>The literal single system message sent to the provider.</summary>
    public string Assemble()
    {
        var builder = new System.Text.StringBuilder();
        foreach (var item in Items.Where(item => item.IsEnabled))
        {
            if (builder.Length > 0)
                builder.Append("\n\n");
            builder.Append("## ").Append(item.Label).Append('\n').Append(item.Body);
        }
        return builder.ToString();
    }

    public string SnapshotJson()
    {
        const int maximumTraceEntries = 96;
        var trace = Trace;
        var selectedKeys = trace
            .Where(item => item.Transient)
            .Concat(trace.Where(item => !item.Transient))
            .Take(maximumTraceEntries)
            .Select(item => (item.Key, item.Origin, item.Included))
            .ToHashSet();
        var bounded = trace
            .Where(item => selectedKeys.Contains((item.Key, item.Origin, item.Included)))
            .Take(maximumTraceEntries)
            .ToList();
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            createdAtUtc = DateTime.UtcNow,
            totalTraceEntries = trace.Count,
            truncatedTraceEntries = Math.Max(0, trace.Count - bounded.Count),
            included = bounded.Where(item => item.Included),
            omitted = bounded.Where(item => !item.Included),
        });
    }

    private static string Excerpt(string body)
    {
        const int limit = 1_200;
        return body.Length <= limit ? body : body[..limit] + "...";
    }
}
