namespace Lorekeeper.Models;

public class EntityVisualExample
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public long GraphNodeId { get; set; }
    public GraphNode GraphNode { get; set; } = null!;

    public Guid ImageId { get; set; }
    public PublishAsset Image { get; set; } = null!;

    public string Label { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public EntityVisualExampleOrigin Origin { get; set; } = EntityVisualExampleOrigin.Manual;

    public Guid? SourceVisualCandidateId { get; set; }
    public SourceVisualCandidate? SourceVisualCandidate { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum EntityVisualExampleOrigin
{
    Manual,
    Agent,
    Research,
    Ingest,
    Import,
}
