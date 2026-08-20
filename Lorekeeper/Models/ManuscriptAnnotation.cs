using System.Text.Json.Serialization;

namespace Lorekeeper.Models;

[JsonConverter(typeof(JsonStringEnumConverter<ManuscriptAnnotationKind>))]
public enum ManuscriptAnnotationKind
{
    Highlight,
    Note,
}

[JsonConverter(typeof(JsonStringEnumConverter<ManuscriptAnnotationAnchorState>))]
public enum ManuscriptAnnotationAnchorState
{
    Current,
    Outdated,
}

public sealed class ManuscriptAnnotation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public Guid ChapterId { get; set; }
    public Chapter Chapter { get; set; } = null!;
    public Guid? EditionId { get; set; }
    public PublicationEdition? Edition { get; set; }
    public ManuscriptAnnotationKind Kind { get; set; }
    public string NoteText { get; set; } = string.Empty;
    public long Revision { get; set; }
    public long AnchorManuscriptRevision { get; set; }
    public ManuscriptAnnotationAnchorState AnchorState { get; set; } = ManuscriptAnnotationAnchorState.Current;
    public required string StartBlockId { get; set; }
    public int StartOffset { get; set; }
    public required string EndBlockId { get; set; }
    public int EndOffset { get; set; }
    public required string OriginalQuote { get; set; }
    public string ContextBefore { get; set; } = string.Empty;
    public string ContextAfter { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
