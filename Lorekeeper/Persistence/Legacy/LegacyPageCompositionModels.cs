namespace Lorekeeper.Persistence.Legacy;

/// <summary>
/// Historical EF adapter used only while startup advances a pre-M2 database.
/// The tables are dropped by the M2 cleanup migration and are excluded from
/// the current model's migration output.
/// </summary>
internal sealed class LegacyPageComposition
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid? ChapterId { get; set; }
    public Guid? PublicationSectionId { get; set; }
    public Guid? EditionId { get; set; }
    public Guid? SourceCompositionId { get; set; }
    public string Name { get; set; } = "Designed page";
    public string SemanticManuscriptJson { get; set; } = string.Empty;
    public long Revision { get; set; }
    public Guid? ActiveAuthoringVariantId { get; set; }
    public ICollection<LegacyPageCompositionVariant> Variants { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DetachedAt { get; set; }
}

internal sealed class LegacyPageCompositionVariant
{
    public Guid Id { get; set; }
    public Guid CompositionId { get; set; }
    public LegacyPageComposition Composition { get; set; } = null!;
    public string GeometryKey { get; set; } = string.Empty;
    public string SceneJson { get; set; } = string.Empty;
    public long Revision { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DetachedAt { get; set; }
}
