using System.Text.Json.Serialization;

namespace Lorekeeper.Models;

[JsonConverter(typeof(JsonStringEnumConverter<PublicationTargetKind>))]
public enum PublicationTargetKind
{
    CoreBook,
    Release,
}

[JsonConverter(typeof(JsonStringEnumConverter<PublicationPreparationStatus>))]
public enum PublicationPreparationStatus
{
    Queued,
    Preparing,
    Blocked,
    Ready,
    Failed,
    Cancelled,
}

public class PublicationPreparationJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public PublicationTargetKind TargetKind { get; set; }
    public Guid? EditionId { get; set; }
    public PublicationEdition? Edition { get; set; }
    public Guid? RenderJobId { get; set; }
    public PublicationRenderJob? RenderJob { get; set; }
    public PublicationPreparationStatus Status { get; set; } = PublicationPreparationStatus.Queued;
    public string Step { get; set; } = "Queued";
    public int ProgressPercent { get; set; }
    public string Message { get; set; } = "Queued";
    public string DiagnosticsJson { get; set; } = "[]";
    public string SourceFingerprint { get; set; } = string.Empty;
    public bool CancellationRequested { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
