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
    /// Optional user-authored direction appended to Lorekeeper's code-owned system
    /// instructions. This is never treated as the system role by itself.
    /// </summary>
    public string ProjectGuidance { get; set; } = string.Empty;

    public BookBrief? BookBrief { get; set; }

    public ProjectPageSetup? PageSetup { get; set; }

    public PublicationBook? PublicationBook { get; set; }

    /// <summary>
    /// When true, the currently-open chapter is included in the assembled system prompt
    /// as a line-numbered block. Toggleable from the Context Feed.
    /// </summary>
    public bool IncludeCurrentChapterInContext { get; set; } = true;

    /// <summary>
    /// When true, assistant edits participate in the Review Edits workflow.
    /// </summary>
    public bool ReviewEditsEnabled { get; set; }

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
    public ICollection<ManuscriptAnnotation> ManuscriptAnnotations { get; set; } = [];

    public ICollection<PageComposition> PageCompositions { get; set; } = [];
    public ICollection<CompositionMutationStage> CompositionMutationStages { get; set; } = [];

    public ICollection<OutlineConversation> OutlineConversations { get; set; } = [];

    public ICollection<EditorConversation> EditorConversations { get; set; } = [];

    public ICollection<WritingSample> WritingSamples { get; set; } = [];

    public ICollection<WritingCoachConversation> WritingCoachConversations { get; set; } = [];

    public ICollection<ResearchConversation> ResearchConversations { get; set; } = [];

    public ICollection<PublishConversation> PublishConversations { get; set; } = [];

    public ICollection<ProjectImageConversation> ProjectImageConversations { get; set; } = [];

    public ICollection<ProjectImageChatAttachment> ProjectImageChatAttachments { get; set; } = [];


    public ICollection<ContestBatch> ContestBatches { get; set; } = [];

    public ICollection<EditorRevisionJob> EditorRevisionJobs { get; set; } = [];

    public ICollection<EditorContextPreference> EditorContextPreferences { get; set; } = [];

    public ICollection<IngestSource> IngestSources { get; set; } = [];

    public ICollection<WebIngestCandidate> WebIngestCandidates { get; set; } = [];

    public ICollection<SourceVisualCandidate> SourceVisualCandidates { get; set; } = [];

    public ICollection<EntityVisualExample> EntityVisualExamples { get; set; } = [];

    public ICollection<IngestJob> IngestJobs { get; set; } = [];

    public ICollection<ProjectImportJob> ProjectImportJobs { get; set; } = [];

    public ICollection<PublicationEdition> PublicationEditions { get; set; } = [];

    public ICollection<PublicationPreparationJob> PublicationPreparationJobs { get; set; } = [];
    public ICollection<PublicationRenderJob> PublicationRenderJobs { get; set; } = [];
    public ICollection<PublicationArtifact> PublicationArtifacts { get; set; } = [];

    public ICollection<PublishAsset> PublishAssets { get; set; } = [];

    public ICollection<ProjectImageGenerationJob> ProjectImageGenerationJobs { get; set; } = [];

    public ICollection<ProjectImagePartial> ProjectImagePartials { get; set; } = [];

    public ICollection<ProjectImageMask> ProjectImageMasks { get; set; } = [];

    public ICollection<ProjectFontFamily> FontFamilies { get; set; } = [];
    public ICollection<ManuscriptStyleDefinition> ManuscriptStyles { get; set; } = [];

    /// <summary>Projects this project directly references for read-only continuity context.</summary>
    public ICollection<ProjectReference> OutgoingReferences { get; set; } = [];

    /// <summary>References that currently resolve to this local project.</summary>
    public ICollection<ProjectReference> ResolvedIncomingReferences { get; set; } = [];

    /// <summary>The local version-history identity for this project, when initialized.</summary>
    public ProjectVersionRepository? VersionHistoryRepository { get; set; }

    /// <summary>Single source of truth for the vector-store scope key for a project.</summary>
    public static string ScopeKey(Guid id) => $"project:{id:N}";

    public string ScopeKeyValue => ScopeKey(Id);
}
