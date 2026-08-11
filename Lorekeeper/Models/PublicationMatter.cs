using System.Text.Json.Serialization;

namespace Lorekeeper.Models;

[JsonConverter(typeof(JsonStringEnumConverter<PublicationMatterLocation>))]
public enum PublicationMatterLocation
{
    Front,
    Back,
}

[JsonConverter(typeof(JsonStringEnumConverter<PublicationMatterKind>))]
public enum PublicationMatterKind
{
    TitlePage,
    Copyright,
    Dedication,
    Epigraph,
    Contents,
    Acknowledgments,
    AboutAuthor,
    AlsoBy,
    References,
    Custom,
}

public class PublicationMatter
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EditionId { get; set; }
    public PublicationEdition Edition { get; set; } = null!;
    public Guid? CoreMatterId { get; set; }
    public PublicationBookMatter? CoreMatter { get; set; }
    public bool IsExcluded { get; set; }
    public PublicationMatterLocation Location { get; set; }
    public PublicationMatterKind Kind { get; set; }
    public required string Title { get; set; }
    public string ManuscriptJson { get; set; } = string.Empty;
    public long Revision { get; set; }
    public bool IsIncluded { get; set; } = true;
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class PublicationEditionAuditEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EditionId { get; set; }
    public PublicationEdition Edition { get; set; } = null!;
    public required string Action { get; set; }
    public string Actor { get; set; } = "user";
    public string BeforeHash { get; set; } = string.Empty;
    public string AfterHash { get; set; } = string.Empty;
    public string DetailJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class PublicationEditionMigrationJournal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string MigrationName { get; set; }
    public required string Status { get; set; }
    public string BackupPath { get; set; } = string.Empty;
    public int SourceProfileCount { get; set; }
    public int SourceSelectionCount { get; set; }
    public int SourcePlacementCount { get; set; }
    public int EditionCount { get; set; }
    public int OutlineItemCount { get; set; }
    public int PlacementCount { get; set; }
    public string SourceHash { get; set; } = string.Empty;
    public string TargetHash { get; set; } = string.Empty;
    public string ValidationReportJson { get; set; } = "{}";
    public string? ErrorDetail { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}
