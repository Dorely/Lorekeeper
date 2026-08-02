using System.ComponentModel.DataAnnotations.Schema;
using Lorekeeper.Manuscripts;

namespace Lorekeeper.Models;

/// <summary>A chapter whose canonical prose is a versioned semantic manuscript.</summary>
public class Chapter
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    /// <summary>
    /// Owning <see cref="Act"/>, if any. Null = chapter lives in the project-level
    /// "Unassigned" bucket. Set <see cref="OnDelete.SetNull"/> in EF so deleting an act
    /// turns its chapters into unassigned rather than deleting them.
    /// </summary>
    public Guid? ActId { get; set; }
    public Act? Act { get; set; }

    public required string Title { get; set; }

    public string ManuscriptJson { get; set; } = string.Empty;

    public long ManuscriptRevision { get; set; }

    [NotMapped]
    public ManuscriptDocument Manuscript => ManuscriptCodec.Deserialize(
        ManuscriptJson,
        Id,
        ManuscriptRevision);

    [NotMapped]
    public string PlainText => ManuscriptCodec.ProjectPlainText(Manuscript);

    public string Synopsis { get; set; } = string.Empty;

    /// <summary>0-based display order within the chapter's act bucket (or the unassigned bucket).</summary>
    public int Order { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<EditorContextPreference> EditorContextPreferences { get; set; } = [];

    public ICollection<PageComposition> PageCompositions { get; set; } = [];

    public VectorIndexState VectorIndexState { get; set; } = VectorIndexState.UpToDate;

    /// <summary>Timestamp of last successful reindex; null if never indexed.</summary>
    public DateTime? VectorIndexedAt { get; set; }

    /// <summary>Last reindex error message, when <see cref="VectorIndexState"/> is <see cref="VectorIndexState.Failed"/>.</summary>
    public string? VectorIndexError { get; set; }

    /// <summary>Stable vector-store source id for this chapter.</summary>
    public string VectorSourceId => Id.ToString("N");
}

public enum VectorIndexState
{
    UpToDate,
    Stale,
    Failed,
    Disabled,
}
