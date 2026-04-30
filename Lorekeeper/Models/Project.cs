namespace Lorekeeper.Models;

/// <summary>
/// Top-level scope for all narrative data (graph nodes, vector chunks, future chapters/outlines).
/// Identified by a stable <see cref="Slug"/> in URLs and by <see cref="Id"/> internally; vector
/// store rows are partitioned by <see cref="ScopeKey(Guid)"/>.
/// </summary>
public class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string Name { get; set; }

    /// <summary>URL-safe identifier derived from <see cref="Name"/> at create time. Stable across renames.</summary>
    public required string Slug { get; set; }

    /// <summary>
    /// Project-owned system prompt shown and edited in the Context Feed. Seeded from
    /// <see cref="Llm.SeedSystemPrompt.Default"/> at create time. Never empty/whitespace.
    /// </summary>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>
    /// When true, the currently-open chapter is included in the assembled system prompt
    /// as a line-numbered block. Toggleable from the Context Feed.
    /// </summary>
    public bool IncludeCurrentChapterInContext { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<GraphNode> Nodes { get; set; } = [];

    public ICollection<Chapter> Chapters { get; set; } = [];

    public ICollection<AiConsoleEntry> AiConsoleEntries { get; set; } = [];

    /// <summary>Single source of truth for the vector-store scope key for a project.</summary>
    public static string ScopeKey(Guid id) => $"project:{id:N}";

    public string ScopeKeyValue => ScopeKey(Id);
}
