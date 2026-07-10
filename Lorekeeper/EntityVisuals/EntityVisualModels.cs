using Lorekeeper.Images;
using Lorekeeper.Models;

namespace Lorekeeper.EntityVisuals;

public sealed record EntityVisualExampleView(
    Guid Id,
    Guid EntityId,
    string EntityType,
    string EntityName,
    string Label,
    int SortOrder,
    EntityVisualExampleOrigin Origin,
    Guid? SourceVisualCandidateId,
    ProjectImageView Image);

public sealed record EntityVisualTarget(Guid EntityId, string Label);

public sealed record EntityVisualChange(
    string Operation,
    Guid? ExampleId = null,
    Guid? EntityId = null,
    Guid? ImageId = null,
    Guid? CandidateId = null,
    string Label = "",
    int? SortOrder = null,
    IReadOnlyList<EntityVisualTarget>? Targets = null,
    ProjectImageCropRegion? Crop = null,
    string CropFileName = "",
    string CropAltText = "");

public sealed record SourceVisualCandidateView(
    Guid Id,
    Guid ProjectId,
    SourceVisualCandidateKind Kind,
    SourceVisualCandidateStatus Status,
    Guid? IngestSourceId,
    Guid? WebIngestCandidateId,
    string FileName,
    string ContentType,
    string PreviewUrl,
    string AltText,
    string Caption,
    string SourceUrl,
    string Locator,
    string MetadataJson,
    string ContentHash,
    int? StartChar,
    int? EndChar,
    Guid? PromotedImageId,
    int AttachedEntityCount,
    string ErrorMessage,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record SourceVisualCandidateCreateRequest(
    Guid ProjectId,
    SourceVisualCandidateKind Kind,
    string FileName,
    string ContentType,
    byte[] Data,
    string AltText = "",
    string Caption = "",
    string SourceUrl = "",
    string Locator = "",
    string MetadataJson = "{}",
    Guid? IngestSourceId = null,
    Guid? WebIngestCandidateId = null,
    int? StartChar = null,
    int? EndChar = null);

public sealed record SourceVisualCandidateData(Guid Id, string FileName, string ContentType, byte[] Data, string AltText);
