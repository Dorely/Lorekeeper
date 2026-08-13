using System.Text.Json.Serialization;

namespace Lorekeeper.Models;

[JsonConverter(typeof(JsonStringEnumConverter<PublicationSectionKind>))]
public enum PublicationSectionKind
{
    TitlePage,
    Copyright,
    Contents,
    Dedication,
    Epigraph,
    Acknowledgments,
    AboutAuthor,
    AlsoBy,
    References,
    Custom,
}

[JsonConverter(typeof(JsonStringEnumConverter<PublicationSectionSystemRole>))]
public enum PublicationSectionSystemRole
{
    None,
    Title,
    Copyright,
    Contents,
}

[JsonConverter(typeof(JsonStringEnumConverter<PublicationSectionAnchor>))]
public enum PublicationSectionAnchor
{
    Front,
    BeforeAct,
    AfterAct,
    BeforeChapter,
    AfterChapter,
    Back,
}

[JsonConverter(typeof(JsonStringEnumConverter<PublicationSectionInclusionMode>))]
public enum PublicationSectionInclusionMode
{
    Automatic,
    Included,
    Omitted,
}

[JsonConverter(typeof(JsonStringEnumConverter<PublicationSectionStartSide>))]
public enum PublicationSectionStartSide
{
    Next,
    Recto,
    Verso,
}

[JsonConverter(typeof(JsonStringEnumConverter<PublicationBoundField>))]
public enum PublicationBoundField
{
    Title,
    Subtitle,
    Author,
    Publisher,
    Copyright,
    Description,
    Isbn,
}

public class PublicationSection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public Guid? EditionId { get; set; }
    public PublicationEdition? Edition { get; set; }
    public Guid? CoreSectionId { get; set; }
    public PublicationSection? CoreSection { get; set; }
    public bool IsExcluded { get; set; }
    public string Title { get; set; } = "Section";
    public PublicationSectionKind Kind { get; set; } = PublicationSectionKind.Custom;
    public PublicationSectionSystemRole SystemRole { get; set; }
    public PublicationSectionAnchor Anchor { get; set; } = PublicationSectionAnchor.Back;
    public PublishOutlineTargetKind? TargetKind { get; set; }
    public Guid? TargetId { get; set; }
    public Guid? ActId { get; set; }
    public Act? Act { get; set; }
    public Guid? ChapterId { get; set; }
    public Chapter? Chapter { get; set; }
    public PublicationSectionInclusionMode InclusionMode { get; set; } = PublicationSectionInclusionMode.Automatic;
    public PublicationSectionStartSide StartSide { get; set; } = PublicationSectionStartSide.Next;
    public int LocalOrder { get; set; }
    public string ManuscriptJson { get; set; } = string.Empty;
    public long Revision { get; set; }
    public ICollection<PageComposition> PageCompositions { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
