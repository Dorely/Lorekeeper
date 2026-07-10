using Lorekeeper.ChapterVisuals;
using Lorekeeper.Context;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Lorekeeper.Images;

public sealed class ProjectImageService(
    AppDbContext db,
    IProjectImageJobService imageJobs,
    IProjectImageGenerationRuntime imageRuntime,
    IOptions<ProjectImageGenerationOptions> imageOptions,
    IChapterVisualService chapterVisuals,
    IContextIndexingService contextIndexing) : IProjectImageService
{
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
            ? ProjectImageResize.Resize(asset.Data, asset.ContentType, edge)
            : asset.Data;

        return new ProjectImageData(asset.Id, asset.FileName, asset.ContentType, data, asset.AltText, asset.UpdatedAt);
    }

    public async Task<ProjectImageView> UploadAsync(Guid projectId, ProjectImageUpload upload, CancellationToken cancellationToken = default)
    {
        var project = await GetProjectAsync(projectId, cancellationToken);
        var normalized = ProjectImageBinary.Normalize(upload.Data, upload.ContentType, upload.FileName);

        var asset = new PublishAsset
        {
            ProjectId = projectId,
            Source = PublishAssetSource.Uploaded,
            FileName = normalized.FileName,
            ContentType = normalized.ContentType,
            Data = normalized.Data,
            AltText = Clean(upload.AltText),
        };

        await db.PublishAssets.AddAsync(asset, cancellationToken);
        project.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return ToView(projectId, asset);
    }

    public async Task<ProjectImageView> CropAsync(
        Guid projectId,
        Guid sourceImageId,
        ProjectImageCropRequest request,
        CancellationToken cancellationToken = default)
    {
        var project = await GetProjectAsync(projectId, cancellationToken);
        var source = await db.PublishAssets
            .FirstOrDefaultAsync(asset => asset.ProjectId == projectId && asset.Id == sourceImageId, cancellationToken)
            ?? throw new InvalidOperationException("Source image was not found in this project.");
        var crop = NormalizeCrop(request.Crop);

        var existing = await db.PublishAssets
            .AsNoTracking()
            .FirstOrDefaultAsync(asset => asset.ProjectId == projectId
                && asset.DerivedFromImageId == sourceImageId
                && asset.CropXPercent == crop.XPercent
                && asset.CropYPercent == crop.YPercent
                && asset.CropWidthPercent == crop.WidthPercent
                && asset.CropHeightPercent == crop.HeightPercent,
                cancellationToken);
        if (existing is not null)
            return ToView(projectId, existing);

        using var sourceStream = new SKMemoryStream(source.Data);
        using var sourceCodec = SKCodec.Create(sourceStream)
            ?? throw new InvalidOperationException("Source image data could not be decoded.");
        using var sourceBitmap = SKBitmap.Decode(source.Data)
            ?? throw new InvalidOperationException("Source image data could not be decoded.");
        if (sourceBitmap.Width <= 0 || sourceBitmap.Height <= 0)
            throw new InvalidOperationException("Source image dimensions are invalid.");

        var left = Math.Clamp((int)Math.Floor(crop.XPercent / 100d * sourceBitmap.Width), 0, sourceBitmap.Width - 1);
        var top = Math.Clamp((int)Math.Floor(crop.YPercent / 100d * sourceBitmap.Height), 0, sourceBitmap.Height - 1);
        var right = Math.Clamp((int)Math.Ceiling((crop.XPercent + crop.WidthPercent) / 100d * sourceBitmap.Width), left + 1, sourceBitmap.Width);
        var bottom = Math.Clamp((int)Math.Ceiling((crop.YPercent + crop.HeightPercent) / 100d * sourceBitmap.Height), top + 1, sourceBitmap.Height);
        var width = right - left;
        var height = bottom - top;

        using var croppedBitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(croppedBitmap))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { IsAntialias = true };
            canvas.DrawBitmap(
                sourceBitmap,
                new SKRect(left, top, right, bottom),
                new SKRect(0, 0, width, height),
                paint);
        }

        var jpeg = sourceCodec.EncodedFormat == SKEncodedImageFormat.Jpeg;
        using var croppedImage = SKImage.FromBitmap(croppedBitmap);
        using var encoded = croppedImage.Encode(jpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, jpeg ? 95 : 100);
        var data = encoded?.ToArray() ?? throw new InvalidOperationException("Cropped image could not be encoded.");
        var contentType = jpeg ? "image/jpeg" : "image/png";
        var fileName = CropFileName(request.FileName, source.FileName, contentType);

        var asset = new PublishAsset
        {
            ProjectId = projectId,
            Source = PublishAssetSource.Cropped,
            FileName = fileName,
            ContentType = contentType,
            Data = data,
            AltText = Clean(request.AltText),
            Prompt = string.Empty,
            GenerationModel = string.Empty,
            SourceMetadataJson = string.Empty,
            DerivedFromImageId = source.Id,
            CropXPercent = crop.XPercent,
            CropYPercent = crop.YPercent,
            CropWidthPercent = crop.WidthPercent,
            CropHeightPercent = crop.HeightPercent,
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
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new InvalidOperationException("Image prompt is required.");

        var job = await imageJobs.CreateGenerateJobAsync(projectId, new ProjectImageGenerateJobRequest(
            request.Prompt.Trim(),
            string.IsNullOrWhiteSpace(request.Size) ? imageOptions.Value.DefaultSize : request.Size.Trim(),
            string.IsNullOrWhiteSpace(request.Quality) ? imageOptions.Value.DefaultQuality : request.Quality.Trim(),
            string.IsNullOrWhiteSpace(request.OutputFormat) ? imageOptions.Value.DefaultOutputFormat : request.OutputFormat.Trim(),
            request.OutputCompression,
            Clean(request.AltText),
            Count: 1,
            request.ReferenceImageIds.Distinct().ToList(),
            Label: "Generated image",
            EntityTargets: request.EntityTargets), cancellationToken);

        await imageRuntime.EnqueueProjectAsync(projectId, cancellationToken);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(imageOptions.Value.AgentJobWaitTimeoutSeconds, 1, 3600));
        if (!await imageRuntime.WaitForJobCompletionAsync(job.Id, timeout, cancellationToken))
            throw new TimeoutException("Timed out waiting for the image generation job to complete.");

        var completed = await imageJobs.GetJobAsync(projectId, job.Id, cancellationToken)
            ?? throw new InvalidOperationException("Image generation job was not found after completion.");
        var outputId = completed.OutputImageIds.FirstOrDefault();
        if (outputId == Guid.Empty)
            throw new InvalidOperationException(completed.Error.Length > 0 ? completed.Error : "Image generation did not produce an output image.");

        return await GetAsync(projectId, outputId, cancellationToken)
            ?? throw new InvalidOperationException("Generated image was not found after completion.");
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
        foreach (var entityId in await AttachedEntityIdsAsync(projectId, imageId, cancellationToken))
            await contextIndexing.ReindexEntityAsync(projectId, entityId, cancellationToken);
        return ToView(projectId, asset);
    }

    public async Task DeleteAsync(Guid projectId, Guid imageId, CancellationToken cancellationToken = default)
    {
        var project = await GetProjectAsync(projectId, cancellationToken);
        var asset = await db.PublishAssets.FirstOrDefaultAsync(candidate => candidate.ProjectId == projectId && candidate.Id == imageId, cancellationToken);
        if (asset is null) return;
        var entityIds = await AttachedEntityIdsAsync(projectId, imageId, cancellationToken);

        foreach (var profile in await db.PublishProfiles.Where(profile => profile.ProjectId == projectId && profile.SelectedCoverAssetId == imageId).ToListAsync(cancellationToken))
            profile.SelectedCoverAssetId = null;

        foreach (var placement in await db.PublishImagePlacements.Where(placement => placement.ProjectId == projectId && placement.AssetId == imageId).ToListAsync(cancellationToken))
            db.PublishImagePlacements.Remove(placement);

        await chapterVisuals.RemoveImageReferencesAsync(projectId, imageId, cancellationToken);

        db.PublishAssets.Remove(asset);
        project.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        foreach (var entityId in entityIds)
            await contextIndexing.ReindexEntityAsync(projectId, entityId, cancellationToken);
    }

    private async Task<Project> GetProjectAsync(Guid projectId, CancellationToken cancellationToken) =>
        await db.Projects.FirstOrDefaultAsync(project => project.Id == projectId, cancellationToken)
        ?? throw new InvalidOperationException($"Project {projectId} not found.");

    public static ProjectImageView ToView(Guid projectId, PublishAsset asset) =>
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

    private async Task<IReadOnlyList<Guid>> AttachedEntityIdsAsync(Guid projectId, Guid imageId, CancellationToken cancellationToken)
    {
        var keys = await db.EntityVisualExamples
            .AsNoTracking()
            .Where(example => example.ProjectId == projectId && example.ImageId == imageId)
            .Select(example => example.GraphNode.Key)
            .ToListAsync(cancellationToken);
        return keys.Where(key => Guid.TryParseExact(key, "N", out _)).Select(key => Guid.ParseExact(key, "N")).ToList();
    }

    private static ProjectImageCropRegion NormalizeCrop(ProjectImageCropRegion crop)
    {
        var values = new[] { crop.XPercent, crop.YPercent, crop.WidthPercent, crop.HeightPercent };
        if (values.Any(value => double.IsNaN(value) || double.IsInfinity(value)))
            throw new InvalidOperationException("Crop coordinates must be finite numbers.");
        if (crop.XPercent < 0 || crop.YPercent < 0 || crop.WidthPercent <= 0 || crop.HeightPercent <= 0)
            throw new InvalidOperationException("Crop coordinates must start within the image and have positive width and height.");
        if (crop.XPercent > 100
            || crop.YPercent > 100
            || crop.WidthPercent > 100 - crop.XPercent
            || crop.HeightPercent > 100 - crop.YPercent)
            throw new InvalidOperationException("Crop rectangle must stay within the image bounds.");

        return new ProjectImageCropRegion(
            crop.XPercent == 0 ? 0 : crop.XPercent,
            crop.YPercent == 0 ? 0 : crop.YPercent,
            crop.WidthPercent,
            crop.HeightPercent);
    }

    private static string CropFileName(string? requestedFileName, string sourceFileName, string contentType)
    {
        var extension = contentType == "image/jpeg" ? ".jpg" : ".png";
        var requested = Path.GetFileName(requestedFileName?.Trim());
        if (!string.IsNullOrWhiteSpace(requested))
            return Path.ChangeExtension(requested, extension);
        var sourceStem = Path.GetFileNameWithoutExtension(sourceFileName);
        if (string.IsNullOrWhiteSpace(sourceStem)) sourceStem = "image";
        return $"{sourceStem}-crop{extension}";
    }

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;
}
