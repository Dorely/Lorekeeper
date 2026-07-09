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

    /// <summary>
    /// When true, mutating AI tool calls are staged for user approval before they are
    /// applied to the project's durable outline state.
    /// </summary>
    public bool AiChangeApprovalEnabled { get; set; } = true;

    /// <summary>
    /// When true, editor chat prepares generation requests with read-only tools, then
    /// starts a contest across the configured model slots instead of mutating directly.
    /// </summary>
    public bool ContestModeEnabled { get; set; }

    public int? ContestProviderSlot1Id { get; set; }
    public int? ContestProviderSlot2Id { get; set; }
    public int? ContestProviderSlot3Id { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<GraphNode> Nodes { get; set; } = [];

    public ICollection<GraphEntityType> EntityTypes { get; set; } = [];

    public ICollection<Act> Acts { get; set; } = [];

    public ICollection<Chapter> Chapters { get; set; } = [];

    public ICollection<OutlineConversation> OutlineConversations { get; set; } = [];

    public ICollection<EditorConversation> EditorConversations { get; set; } = [];

    public ICollection<WritingSample> WritingSamples { get; set; } = [];

    public ICollection<WritingCoachConversation> WritingCoachConversations { get; set; } = [];

    public ICollection<ResearchConversation> ResearchConversations { get; set; } = [];

    public ICollection<ProjectImageConversation> ProjectImageConversations { get; set; } = [];

    public ICollection<ProjectImageChatAttachment> ProjectImageChatAttachments { get; set; } = [];

    public ICollection<AiChangeBatch> AiChangeBatches { get; set; } = [];

    public ICollection<ContestBatch> ContestBatches { get; set; } = [];

    public ICollection<EditorRevisionJob> EditorRevisionJobs { get; set; } = [];

    public ICollection<EditorContextPreference> EditorContextPreferences { get; set; } = [];

    public ICollection<IngestSource> IngestSources { get; set; } = [];

    public ICollection<WebIngestCandidate> WebIngestCandidates { get; set; } = [];

    public ICollection<IngestJob> IngestJobs { get; set; } = [];

    public ICollection<ProjectImportJob> ProjectImportJobs { get; set; } = [];

    public ICollection<PublishProfile> PublishProfiles { get; set; } = [];

    public ICollection<PublishAsset> PublishAssets { get; set; } = [];

    public ICollection<ProjectImageGenerationJob> ProjectImageGenerationJobs { get; set; } = [];

    public ICollection<ProjectImageMask> ProjectImageMasks { get; set; } = [];

    public ICollection<PublishOutlineSelection> PublishOutlineSelections { get; set; } = [];

    public ICollection<PublishImagePlacement> PublishImagePlacements { get; set; } = [];

    /// <summary>Single source of truth for the vector-store scope key for a project.</summary>
    public static string ScopeKey(Guid id) => $"project:{id:N}";

    public string ScopeKeyValue => ScopeKey(Id);
}
