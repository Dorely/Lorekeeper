using System.Text.Json;
using Lorekeeper.ChapterVisuals;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Publish;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace Lorekeeper.Images;

public sealed class ProjectImageService(
    AppDbContext db,
    ICodexImageGenerationService codexImages,
    IChapterVisualService chapterVisuals,
    ILogger<ProjectImageService> logger) : IProjectImageService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public async Task<IReadOnlyList<ProjectImageView>> ListAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await db.PublishAssets
            .AsNoTracking()
            .Where(asset => asset.ProjectId == projectId)
            .OrderByDescending(asset => asset.CreatedAt)
            .Select(asset => ToView(projectId, asset))
            .ToListAsync(cancellationToken);

    public async Task<ProjectImageView?> GetAsync(Guid projectId, Guid imageId, CancellationToken cancellationToken = default)
    {
        var asset = await db.PublishAssets
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.ProjectId == projectId && candidate.Id == imageId, cancellationToken);

        return asset is null ? null : ToView(projectId, asset);
    }

    public async Task<ProjectImageData?> GetDataAsync(
        Guid projectId,
        Guid imageId,
        int? maxEdge = null,
        CancellationToken cancellationToken = default)
    {
        var asset = await db.PublishAssets
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.ProjectId == projectId && candidate.Id == imageId, cancellationToken);
        if (asset is null) return null;

        var data = maxEdge is int edge && edge > 0
            ? ResizeImage(asset.Data, asset.ContentType, edge)
            : asset.Data;

        return new ProjectImageData(asset.Id, asset.FileName, asset.ContentType, data, asset.AltText, asset.UpdatedAt);
    }

    public async Task<ProjectImageView> UploadAsync(Guid projectId, ProjectImageUpload upload, CancellationToken cancellationToken = default)
    {
        var project = await GetProjectAsync(projectId, cancellationToken);
        var contentType = NormalizeImageContentType(upload.ContentType)
            ?? throw new InvalidOperationException("Only PNG and JPEG images can be used.");
        if (upload.Data.Length == 0)
            throw new InvalidOperationException("Image file is empty.");

        var asset = new PublishAsset
        {
            ProjectId = projectId,
            Source = PublishAssetSource.Uploaded,
            FileName = SafeFileName(upload.FileName, contentType),
            ContentType = contentType,
            Data = upload.Data,
            AltText = Clean(upload.AltText),
        };

        await db.PublishAssets.AddAsync(asset, cancellationToken);
        project.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return ToView(projectId, asset);
    }

    public async Task<ProjectImageView> GenerateAsync(
        Guid projectId,
        ProjectImageGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        var project = await GetProjectAsync(projectId, cancellationToken);
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new InvalidOperationException("Image prompt is required.");

        var requestedReferenceIds = request.ReferenceImageIds.Distinct().ToList();
        if (requestedReferenceIds.Count > 4)
            throw new InvalidOperationException("Select no more than 4 reference images.");

        var referenceAssets = requestedReferenceIds.Count == 0
            ? new List<PublishAsset>()
            : await db.PublishAssets
                .Where(asset => asset.ProjectId == projectId && requestedReferenceIds.Contains(asset.Id))
                .ToListAsync(cancellationToken);
        if (referenceAssets.Count != requestedReferenceIds.Count)
            throw new InvalidOperationException("One or more selected reference images could not be found.");

        var referencesById = referenceAssets.ToDictionary(asset => asset.Id);
        var orderedReferences = requestedReferenceIds.Select(id => referencesById[id]).ToList();
        var generated = await codexImages.GenerateAsync(new CodexImageGenerationOptions(
            request.Prompt.Trim(),
            string.IsNullOrWhiteSpace(request.Size) ? "auto" : request.Size.Trim(),
            string.IsNullOrWhiteSpace(request.Quality) ? "auto" : request.Quality.Trim(),
            string.IsNullOrWhiteSpace(request.OutputFormat) ? "png" : request.OutputFormat.Trim(),
            request.OutputCompression,
            orderedReferences.Select(asset => new CodexImageReference(asset.FileName, asset.ContentType, asset.Data)).ToList()), cancellationToken);

        var asset = new PublishAsset
        {
            ProjectId = projectId,
            Source = PublishAssetSource.Generated,
            FileName = $"generated-{DateTime.UtcNow:yyyyMMddHHmmss}.{ExtensionForContentType(generated.ContentType)}",
            ContentType = generated.ContentType,
            Data = generated.Data,
            AltText = Clean(request.AltText),
            Prompt = request.Prompt.Trim(),
            GenerationModel = generated.ImageModel,
            SourceMetadataJson = JsonSerializer.Serialize(new
            {
                generated.MainlineModel,
                generated.ImageModel,
                generated.OutputFormat,
                generated.RevisedPrompt,
                generated.ResponseId,
                generated.CallId,
                ReferenceImages = orderedReferences.Select(asset => new
                {
                    asset.Id,
                    asset.FileName,
                    asset.ContentType,
                }),
            }, JsonOptions),
        };

        await db.PublishAssets.AddAsync(asset, cancellationToken);
        project.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return ToView(projectId, asset);
    }

    public async Task<ProjectImageView> UpdateAsync(
        Guid projectId,
        Guid imageId,
        ProjectImageUpdate update,
        CancellationToken cancellationToken = default)
    {
        var project = await GetProjectAsync(projectId, cancellationToken);
        var asset = await db.PublishAssets.FirstOrDefaultAsync(candidate => candidate.ProjectId == projectId && candidate.Id == imageId, cancellationToken)
            ?? throw new InvalidOperationException("Image was not found.");

        var fileName = Clean(update.FileName);
        if (!string.IsNullOrWhiteSpace(fileName))
            asset.FileName = fileName;
        asset.AltText = Clean(update.AltText);
        asset.UpdatedAt = DateTime.UtcNow;
        project.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return ToView(projectId, asset);
    }

    public async Task DeleteAsync(Guid projectId, Guid imageId, CancellationToken cancellationToken = default)
    {
        var project = await GetProjectAsync(projectId, cancellationToken);
        var asset = await db.PublishAssets.FirstOrDefaultAsync(candidate => candidate.ProjectId == projectId && candidate.Id == imageId, cancellationToken);
        if (asset is null) return;

        foreach (var profile in await db.PublishProfiles.Where(profile => profile.ProjectId == projectId && profile.SelectedCoverAssetId == imageId).ToListAsync(cancellationToken))
            profile.SelectedCoverAssetId = null;

        foreach (var placement in await db.PublishImagePlacements.Where(placement => placement.ProjectId == projectId && placement.AssetId == imageId).ToListAsync(cancellationToken))
            db.PublishImagePlacements.Remove(placement);

        await chapterVisuals.RemoveImageReferencesAsync(projectId, imageId, cancellationToken);

        db.PublishAssets.Remove(asset);
        project.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<Project> GetProjectAsync(Guid projectId, CancellationToken cancellationToken) =>
        await db.Projects.FirstOrDefaultAsync(project => project.Id == projectId, cancellationToken)
        ?? throw new InvalidOperationException($"Project {projectId} not found.");

    private static ProjectImageView ToView(Guid projectId, PublishAsset asset) =>
        new(
            asset.Id,
            asset.FileName,
            asset.ContentType,
            $"/projects/{projectId:N}/images/{asset.Id:N}/content?maxEdge=640",
            asset.AltText,
            asset.Source,
            asset.Prompt,
            asset.GenerationModel,
            asset.SourceMetadataJson,
            asset.CreatedAt,
            asset.UpdatedAt,
            asset.Data.LongLength);

    private byte[] ResizeImage(byte[] data, string contentType, int maxEdge)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(data);
            if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
                return data;

            var currentEdge = Math.Max(bitmap.Width, bitmap.Height);
            if (currentEdge <= maxEdge)
                return data;

            var scale = (double)maxEdge / currentEdge;
            var targetWidth = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
            var targetHeight = Math.Max(1, (int)Math.Round(bitmap.Height * scale));
            using var resized = bitmap.Resize(new SKImageInfo(targetWidth, targetHeight), SKSamplingOptions.Default);
            if (resized is null)
                return data;

            using var image = SKImage.FromBitmap(resized);
            using var encoded = image.Encode(
                contentType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png,
                84);
            return encoded?.ToArray() ?? data;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Image thumbnail generation failed; returning original bytes.");
            return data;
        }
    }

    private static string? NormalizeImageContentType(string contentType) =>
        contentType.Trim().ToLowerInvariant() switch
        {
            "image/png" => "image/png",
            "image/jpeg" => "image/jpeg",
            "image/jpg" => "image/jpeg",
            _ => null,
        };

    private static string SafeFileName(string fileName, string contentType)
    {
        var clean = Clean(fileName);
        if (!string.IsNullOrWhiteSpace(clean))
            return clean;

        return $"image-{DateTime.UtcNow:yyyyMMddHHmmss}.{ExtensionForContentType(contentType)}";
    }

    private static string ExtensionForContentType(string contentType) =>
        contentType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) ? "jpg" : "png";

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;
}
