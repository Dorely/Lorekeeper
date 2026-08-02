using System.Text.Json.Serialization;

namespace Lorekeeper.Models;

[JsonConverter(typeof(JsonStringEnumConverter<PublicationRenderStatus>))]
public enum PublicationRenderStatus
{
    Queued,
    Rendering,
    Completed,
    Failed,
    Cancelled,
}

[JsonConverter(typeof(JsonStringEnumConverter<PublicationArtifactKind>))]
public enum PublicationArtifactKind
{
    InteriorPdf,
    CoverPdf,
    BookPdf,
    Epub,
    FrontCoverImage,
    Manifest,
    PreflightReport,
    PublicationPackage,
    ProofRecord,
}

public class PublicationRenderJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EditionId { get; set; }
    public PublicationEdition Edition { get; set; } = null!;
    public PublicationRenderStatus Status { get; set; } = PublicationRenderStatus.Queued;
    public string SourceFingerprint { get; set; } = string.Empty;
    public string PaginationFingerprint { get; set; } = string.Empty;
    public string RendererVersion { get; set; } = string.Empty;
    public string ProfileId { get; set; } = string.Empty;
    public string DiagnosticsJson { get; set; } = "[]";
    public string EvidenceJson { get; set; } = "{}";
    public int ProgressPercent { get; set; }
    public string ProgressMessage { get; set; } = "Queued";
    public bool CancellationRequested { get; set; }
    public bool IsLegacy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public ICollection<PublicationArtifact> Artifacts { get; set; } = [];
    public ICollection<PublicationPageMapEntry> PageMapEntries { get; set; } = [];
}

public class PublicationArtifact
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EditionId { get; set; }
    public PublicationEdition Edition { get; set; } = null!;
    public Guid? RenderJobId { get; set; }
    public PublicationRenderJob? RenderJob { get; set; }
    public PublicationArtifactKind Kind { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string MediaType { get; set; } = "application/octet-stream";
    public byte[] Data { get; set; } = [];
    public string Sha256 { get; set; } = string.Empty;
    public long ByteLength { get; set; }
    public int? PageCount { get; set; }
    public string SourceFingerprint { get; set; } = string.Empty;
    public string PaginationFingerprint { get; set; } = string.Empty;
    public string RendererVersion { get; set; } = string.Empty;
    public string ProfileId { get; set; } = string.Empty;
    public bool IsLegacy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class PublicationPageMapEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RenderJobId { get; set; }
    public PublicationRenderJob RenderJob { get; set; } = null!;
    public Guid ChapterId { get; set; }
    public Guid BlockId { get; set; }
    public int PageNumber { get; set; }
}
