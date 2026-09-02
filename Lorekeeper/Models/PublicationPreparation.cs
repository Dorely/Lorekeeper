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
    public Guid? BookRenderJobId { get; set; }
    public PublicationRenderJob? BookRenderJob { get; set; }
    public Guid? InteriorRenderJobId { get; set; }
    public PublicationRenderJob? InteriorRenderJob { get; set; }
    public Guid? CoverRenderJobId { get; set; }
    public PublicationRenderJob? CoverRenderJob { get; set; }
    public PublicationPreparationStatus Status { get; set; } = PublicationPreparationStatus.Queued;
    public string Step { get; set; } = "Queued";
    public int ProgressPercent { get; set; }
    public string Message { get; set; } = "Queued";
    public string DiagnosticsJson { get; set; } = "[]";
    public string ImagePreparationSummaryJson { get; set; } = "{}";
    public string SourceFingerprint { get; set; } = string.Empty;
    public bool CancellationRequested { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// A user-actionable failure found while preparing publication images.  The
/// optional placement information lets the publish surface identify where a
/// source image failed without exposing native Press implementation details.
/// </summary>
public sealed record PublicationImagePreparationFailure(
    string Code,
    string Message,
    Guid? AssetId = null,
    string? Surface = null,
    int? PageNumber = null,
    bool Retryable = false);

/// <summary>Durable, presentation-neutral image preparation accounting.</summary>
public sealed record PublicationImagePreparationSummary(
    int CreatedAssetCount,
    int ReusedAssetCount,
    int ReplacedReferenceCount,
    double ThresholdDpi,
    IReadOnlyList<PublicationImagePreparationFailure> Failures)
{
    public bool IsSuccessful => Failures.Count == 0;
}

/// <summary>Outcome of one deterministic publication image preparation pass.</summary>
public sealed record PublicationImagePreparationResult(
    PublicationImagePreparationSummary Summary,
    string SourceFingerprint,
    bool Committed)
{
    public bool IsSuccessful => Summary.IsSuccessful;
}
