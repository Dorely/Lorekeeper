using Lorekeeper.Models;
using Lorekeeper.EntityVisuals;

namespace Lorekeeper.Images;

public sealed record ProjectImageView(
    Guid Id,
    string FileName,
    string ContentType,
    string PreviewUrl,
    string AltText,
    PublishAssetSource Source,
    string Prompt,
    string GenerationModel,
    string SourceMetadataJson,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    long SizeBytes,
    int Width = 0,
    int Height = 0,
    Guid? DerivedFromImageId = null,
    IReadOnlyList<ProjectImageUpscaleSummary>? DirectUpscales = null)
{
    public IReadOnlyList<ProjectImageUpscaleSummary> Upscales => DirectUpscales ?? [];
}

public sealed record ProjectImageUpscaleSummary(
    Guid Id,
    string FileName,
    string PreviewUrl,
    int Width,
    int Height,
    string Raster,
    double? SourceEffectiveDpi,
    double? RequiredEffectiveDpi,
    double? TargetEffectiveDpi,
    string Algorithm,
    string AlgorithmVersion,
    string SourceByteHash,
    bool AddsNewDetail,
    string CreationTrigger,
    DateTime CreatedAt,
    string SourceRaster = "",
    string TargetRaster = "");

public sealed record ProjectImageChapterUsageView(
    Guid ImageId,
    IReadOnlyList<string> ChapterTitles);

public sealed record ProjectImageUpload(
    string FileName,
    string ContentType,
    byte[] Data,
    string AltText);

public sealed record ProjectImageUpdate(
    string FileName,
    string AltText);

public sealed record ProjectImageCropRegion(
    double XPercent,
    double YPercent,
    double WidthPercent,
    double HeightPercent);

public sealed record ProjectImageCropRequest(
    ProjectImageCropRegion Crop,
    string FileName,
    string AltText);

public sealed record ProjectImageResizeRequest(
    int Width,
    int Height,
    string FileName,
    string AltText);

public sealed record ProjectImagePrintUpscaleRequest(
    int Width,
    int Height,
    double WidthInches,
    double HeightInches,
    double TargetDpi,
    string CreationTrigger = "print-upscale-pipeline");

public sealed record ProjectImagePrintUpscaleResult(
    ProjectImageView Image,
    bool WasCreated);

public sealed record ProjectImageCropSaved(
    ProjectImageView Image,
    IReadOnlyList<EntityVisualTarget> EntityTargets);

public sealed record ProjectImageGenerationRequest(
    string Prompt,
    string Size,
    string Quality,
    string OutputFormat,
    int? OutputCompression,
    string AltText,
    IReadOnlyList<Guid> ReferenceImageIds,
    IReadOnlyList<EntityVisualTarget>? EntityTargets = null);

public sealed record ProjectImageData(
    Guid Id,
    string FileName,
    string ContentType,
    byte[] Data,
    string AltText,
    DateTime UpdatedAt);

public sealed record ProjectImagePartialView(
    Guid Id,
    Guid JobId,
    int OutputIndex,
    int Attempt,
    int PartialImageIndex,
    string FileName,
    string ContentType,
    string PreviewUrl,
    int Width,
    int Height,
    string Provider,
    string MainlineModel,
    string ImageModel,
    string? RequestId,
    string? ResponseId,
    string? CallId,
    string? ItemId,
    string? LastEventType,
    int EventCount,
    Guid? FinalOutputImageId,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record ProjectImagePartialData(
    Guid Id,
    string FileName,
    string ContentType,
    byte[] Data,
    int Width,
    int Height,
    DateTime UpdatedAt);

public sealed record ProjectImageGenerateJobRequest(
    string Prompt,
    string Size,
    string Quality,
    string OutputFormat,
    int? OutputCompression,
    string AltText,
    int Count,
    IReadOnlyList<Guid> ReferenceImageIds,
    string? Label = null,
    IReadOnlyList<EntityVisualTarget>? EntityTargets = null,
    string? BriefJson = null,
    string? ReferenceManifestJson = null,
    string? TargetGeometryJson = null);

public sealed record ProjectImageEditJobRequest(
    Guid SourceImageId,
    string Prompt,
    string Size,
    string Quality,
    string OutputFormat,
    int? OutputCompression,
    string AltText,
    int Count,
    string? MaskPngDataUrl,
    IReadOnlyList<Guid> ReferenceImageIds,
    string? Label = null,
    ProjectImageMaskShapeRequest? RegionalGuide = null,
    IReadOnlyList<EntityVisualTarget>? EntityTargets = null,
    bool InheritSourceEntityTargets = true,
    string? BriefJson = null,
    string? ReferenceManifestJson = null,
    string? TargetGeometryJson = null);

public sealed record ProjectImageJobView(
    Guid Id,
    ProjectImageGenerationJobKind Kind,
    ProjectImageGenerationJobStatus Status,
    string Label,
    string Prompt,
    string Size,
    string Quality,
    string OutputFormat,
    int? OutputCompression,
    int Count,
    string AltText,
    Guid? SourceImageId,
    Guid? MaskId,
    IReadOnlyList<Guid> ReferenceImageIds,
    IReadOnlyList<Guid> OutputImageIds,
    IReadOnlyList<ProjectImageOutputStateView> OutputStates,
    IReadOnlyList<ProjectImageOutputErrorView> OutputErrors,
    string Provider,
    string MainlineModel,
    string ImageModel,
    string Error,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    IReadOnlyList<EntityVisualTarget> EntityTargets,
    bool InheritSourceEntityTargets,
    string BriefJson,
    string ReferenceManifestJson,
    string TargetGeometryJson,
    IReadOnlyList<string> ProviderRevisedPrompts);

public sealed record ProjectImageOutputStateView(
    int OutputIndex,
    ProjectImageOutputStatus Status,
    int Attempt = 0,
    string Message = "",
    string Error = "",
    string? ErrorKind = null,
    string? RequestId = null,
    string? ResponseId = null,
    string? CallId = null,
    string? LastEventType = null,
    int EventCount = 0,
    DateTime? StartedAt = null,
    DateTime? UpdatedAt = null,
    DateTime? CompletedAt = null);

public sealed record ProjectImageOutputErrorView(
    int OutputIndex,
    string Error,
    string? ErrorKind = null,
    string? RequestId = null,
    string? ResponseId = null,
    string? CallId = null,
    int? StatusCode = null,
    string? LastEventType = null,
    int EventCount = 0);

public enum ProjectImageOutputStatus
{
    Queued,
    Running,
    Generating,
    Succeeded,
    Failed,
    Cancelled,
}

public sealed record ProjectImageProviderGenerateRequest(
    string Prompt,
    string Size,
    int Count,
    string MainlineModel,
    string ImageModel,
    IReadOnlyList<ProjectImageProviderReference> ReferenceImages,
    string OutputFormat,
    string Quality,
    int? OutputCompression);

public sealed record ProjectImageProviderEditRequest(
    string Prompt,
    string Size,
    int Count,
    string MainlineModel,
    string ImageModel,
    ProjectImageProviderReference SourceImage,
    ProjectImageProviderReference? Mask,
    IReadOnlyList<ProjectImageProviderReference> ReferenceImages,
    string OutputFormat,
    string Quality,
    int? OutputCompression);

public sealed record ProjectImageProviderReference(
    string FileName,
    string ContentType,
    byte[] Data);

public sealed record ProjectImageProviderResult(
    IReadOnlyList<ProjectImageProviderImage> Images,
    string Provider,
    string MainlineModel,
    string ImageModel,
    string RawMetadataJson);

public sealed record ProjectImageProviderImage(
    byte[] Data,
    string ContentType,
    string OutputFormat,
    string? RevisedPrompt,
    string? ResponseId,
    string? CallId);

public sealed record ProjectImageProviderProgress(
    ProjectImageProviderProgressKind Kind,
    string Message,
    string? RequestId = null,
    string? ResponseId = null,
    string? CallId = null,
    string? ItemId = null,
    int? ProviderOutputIndex = null,
    int? PartialImageIndex = null,
    string? PartialImageDataUrl = null,
    string? ErrorKind = null,
    int? StatusCode = null,
    string? LastEventType = null,
    int EventCount = 0);

public enum ProjectImageProviderProgressKind
{
    Started,
    InProgress,
    Generating,
    PartialImage,
    Completed,
    Failed,
    StreamEndedWithoutImage,
}

public sealed class ProjectImageProviderException : InvalidOperationException
{
    public ProjectImageProviderException(
        string message,
        string errorKind,
        string? requestId = null,
        string? responseId = null,
        string? callId = null,
        int? statusCode = null,
        string? lastEventType = null,
        int eventCount = 0,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorKind = string.IsNullOrWhiteSpace(errorKind) ? "unknown" : errorKind.Trim();
        RequestId = requestId;
        ResponseId = responseId;
        CallId = callId;
        StatusCode = statusCode;
        LastEventType = lastEventType;
        EventCount = eventCount;
    }

    public string ErrorKind { get; }
    public string? RequestId { get; }
    public string? ResponseId { get; }
    public string? CallId { get; }
    public int? StatusCode { get; }
    public string? LastEventType { get; }
    public int EventCount { get; }
}

public sealed record ProjectImageGenerationRuntimeSnapshot(
    IReadOnlyList<ProjectImageGenerationJobRuntimeView> Jobs);

public sealed record ProjectImageGenerationJobRuntimeView(
    Guid ProjectId,
    Guid JobId,
    bool IsRunning,
    IReadOnlyList<ProjectImageOutputRuntimeView> Outputs);

public sealed record ProjectImageOutputRuntimeView(
    int OutputIndex,
    ProjectImageOutputStatus Status,
    int Attempt,
    string Message,
    string Error,
    string? ErrorKind,
    string? RequestId,
    string? ResponseId,
    string? CallId,
    string? LastEventType,
    int EventCount,
    string? PartialImageUrl);
