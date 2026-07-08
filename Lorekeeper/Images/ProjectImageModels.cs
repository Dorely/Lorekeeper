using Lorekeeper.Models;

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
    long SizeBytes);

public sealed record ProjectImageUpload(
    string FileName,
    string ContentType,
    byte[] Data,
    string AltText);

public sealed record ProjectImageUpdate(
    string FileName,
    string AltText);

public sealed record ProjectImageGenerationRequest(
    string Prompt,
    string Size,
    string Quality,
    string OutputFormat,
    int? OutputCompression,
    string AltText,
    IReadOnlyList<Guid> ReferenceImageIds);

public sealed record ProjectImageData(
    Guid Id,
    string FileName,
    string ContentType,
    byte[] Data,
    string AltText,
    DateTime UpdatedAt);
