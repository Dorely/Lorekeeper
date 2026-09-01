using Lorekeeper.Authoring;
using Lorekeeper.Composition;
using Lorekeeper.Context;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SkiaSharp;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lorekeeper.Images;

public sealed class ProjectImageService(
    IAppDatabaseOperationFactory database,
    IProjectImageJobService imageJobs,
    IProjectImageGenerationRuntime imageRuntime,
    IOptions<ProjectImageGenerationOptions> imageOptions,
    IContextIndexingService contextIndexing,
    IAuthoringHistoryRuntime authoringHistory) : IProjectImageService
{
    public async Task<IReadOnlyList<ProjectImageView>> ListAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var assets = await db.PublishAssets
            .AsNoTracking()
            .Where(asset => asset.ProjectId == projectId)
            .OrderByDescending(asset => asset.CreatedAt)
            .ToListAsync(cancellationToken);

        return ToViews(projectId, assets);
    }

    public async Task<IReadOnlyList<ProjectImageChapterUsageView>> ListChapterUsageAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var usages = await BuildChapterUsageAsync(databaseOperation.Db, projectId, cancellationToken);
        return usages
            .OrderBy(usage => usage.Key)
            .Select(usage => new ProjectImageChapterUsageView(
                usage.Key,
                usage.Value.Values.OrderBy(title => title, StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();
    }

    public async Task<IReadOnlyList<ProjectImageView>> ListByIdsAsync(
        Guid projectId,
        IReadOnlyCollection<Guid> imageIds,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var requestedIds = imageIds
            .Where(imageId => imageId != Guid.Empty)
            .Distinct()
            .ToList();
        if (requestedIds.Count == 0)
            return [];

        var assets = await db.PublishAssets
            .AsNoTracking()
            .Where(asset => asset.ProjectId == projectId
                && (requestedIds.Contains(asset.Id)
                    || (asset.DerivedFromImageId != null
                        && requestedIds.Contains(asset.DerivedFromImageId.Value))))
            .OrderByDescending(asset => asset.CreatedAt)
            .ToListAsync(cancellationToken);

        return ToViews(projectId, assets)
            .Where(view => requestedIds.Contains(view.Id))
            .ToList();
    }

    public async Task<ProjectImageView?> GetAsync(Guid projectId, Guid imageId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var asset = await db.PublishAssets
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.ProjectId == projectId && candidate.Id == imageId, cancellationToken);

        if (asset is null)
            return null;

        var directUpscales = await db.PublishAssets
            .AsNoTracking()
            .Where(candidate => candidate.ProjectId == projectId
                && candidate.DerivedFromImageId == imageId
                && candidate.Source == PublishAssetSource.Upscaled)
            .OrderBy(candidate => candidate.CreatedAt)
            .ToListAsync(cancellationToken);
        return ToView(projectId, asset, directUpscales);
    }

    public async Task<ProjectImageData?> GetDataAsync(
        Guid projectId,
        Guid imageId,
        int? maxEdge = null,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
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
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
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
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
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

    public async Task<ProjectImageView> ResizeAsync(
        Guid projectId,
        Guid sourceImageId,
        ProjectImageResizeRequest request,
        CancellationToken cancellationToken = default)
    {
        LayoutImageSizeResolver.Validate(request.Width, request.Height);
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var project = await GetProjectAsync(projectId, cancellationToken);
        var source = await db.PublishAssets
            .AsNoTracking()
            .FirstOrDefaultAsync(asset => asset.ProjectId == projectId && asset.Id == sourceImageId, cancellationToken)
            ?? throw new InvalidOperationException("Source image was not found in this project.");

        using var sourceBitmap = SKBitmap.Decode(source.Data)
            ?? throw new InvalidOperationException("Source image data could not be decoded.");
        if (sourceBitmap.Width <= 0 || sourceBitmap.Height <= 0)
            throw new InvalidOperationException("Source image dimensions are invalid.");
        if (!LayoutImageSizeResolver.AspectMatches(
                (double)request.Width / request.Height,
                (double)sourceBitmap.Width / sourceBitmap.Height))
            throw new InvalidOperationException("The requested resize raster must preserve the source aspect ratio. Use edit_project_image with an intentional-outpainting brief for model-driven expansion.");
        if (sourceBitmap.Width == request.Width && sourceBitmap.Height == request.Height)
            throw new InvalidOperationException("The source image already has the requested raster; no derived image was created.");

        var fileName = ResizeFileName(request.FileName, source.FileName);
        var data = ProjectImageResize.ResizeExact(source.Data, request.Width, request.Height);
        var normalized = ProjectImageBinary.Normalize(
            data,
            "image/png",
            fileName,
            Math.Max(ProjectImageBinary.DefaultMaxBytes, imageOptions.Value.MaxProviderOutputBytes));
        var now = DateTime.UtcNow;
        var asset = new PublishAsset
        {
            ProjectId = projectId,
            Source = PublishAssetSource.Resized,
            FileName = normalized.FileName,
            ContentType = normalized.ContentType,
            Data = normalized.Data,
            AltText = string.IsNullOrWhiteSpace(request.AltText) ? source.AltText : Clean(request.AltText),
            Prompt = string.Empty,
            GenerationModel = string.Empty,
            SourceMetadataJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                Transform = new
                {
                    Kind = "deterministic-resize",
                    SourceImageId = source.Id,
                    SourceRaster = $"{sourceBitmap.Width}x{sourceBitmap.Height}",
                    TargetRaster = $"{normalized.Width}x{normalized.Height}",
                    Interpolation = ProjectImageResize.DeterministicInterpolation,
                    AddsNewDetail = false,
                },
                RasterStorage = new
                {
                    normalized.ContentType,
                    normalized.Width,
                    normalized.Height,
                    LayoutTransform = "none",
                },
            }),
            DerivedFromImageId = source.Id,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await db.PublishAssets.AddAsync(asset, cancellationToken);
        project.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return ToView(projectId, asset);
    }

    public async Task<ProjectImagePrintUpscaleResult> EnsurePrintUpscaleAsync(
        Guid projectId,
        Guid sourceImageId,
        ProjectImagePrintUpscaleRequest request,
        CancellationToken cancellationToken = default)
    {
        // A publication-preparation transaction can call this helper through
        // the ambient write operation. In that case the owner commits the
        // derivative and its reference replacements together; standalone
        // generation calls retain the service-owned save below.
        var ownsWriteOperation = AppDatabaseOperationAmbient.Current is null;
        LayoutImageSizeResolver.ValidatePrintRaster(request.Width, request.Height);
        LayoutImageSizeResolver.ValidatePhysicalDimensions(request.WidthInches, request.HeightInches);
        LayoutImageSizeResolver.ValidateMinimumDpi(request.TargetDpi);
        var minimum = LayoutImageSizeResolver.ResolvePrintRaster(request.WidthInches, request.HeightInches, request.TargetDpi);
        if (request.Width < minimum.Width || request.Height < minimum.Height)
            throw new InvalidOperationException(
                $"The requested print raster {request.Width}x{request.Height} is below the physical target minimum "
                + $"({request.WidthInches:0.####} x {request.HeightInches:0.####} inches at {request.TargetDpi:0.##} DPI = {minimum.Size}).");

        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var project = await db.Projects.FirstOrDefaultAsync(item => item.Id == projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");
        var source = await db.PublishAssets
            .FirstOrDefaultAsync(asset => asset.ProjectId == projectId && asset.Id == sourceImageId, cancellationToken)
            ?? throw new InvalidOperationException("Source image was not found in this project.");

        var sourceRoot = await FindNonUpscaledRootAsync(db, source, cancellationToken);
        using var sourceBitmap = SKBitmap.Decode(sourceRoot.Data)
            ?? throw new InvalidOperationException("Source image data could not be decoded.");
        if (sourceBitmap.Width <= 0 || sourceBitmap.Height <= 0)
            throw new InvalidOperationException("Source image dimensions are invalid.");
        if (!LayoutImageSizeResolver.AspectMatches(
                (double)request.Width / request.Height,
                (double)sourceBitmap.Width / sourceBitmap.Height))
        {
            throw new InvalidOperationException(
                $"The requested print raster {request.Width}x{request.Height} must preserve the source image aspect ratio "
                + $"({sourceBitmap.Width}x{sourceBitmap.Height}).");
        }

        if (sourceBitmap.Width >= request.Width && sourceBitmap.Height >= request.Height)
        {
            // A native image that already satisfies the required raster is compliant.
            // No derivative is needed, and callers can treat this as a reuse.
            return new ProjectImagePrintUpscaleResult(ToView(projectId, sourceRoot), false);
        }

        var sourceHash = Convert.ToHexString(SHA256.HashData(sourceRoot.Data)).ToLowerInvariant();
        var expectedId = DeterministicUpscaleId(sourceRoot.Id, sourceHash, request.Width, request.Height);

        PublishAsset? reusable = null;
        var reusableWidth = 0;
        var reusableHeight = 0;
        var linkedDerivatives = await db.PublishAssets
            .AsNoTracking()
            .Where(asset => asset.ProjectId == projectId
                && asset.DerivedFromImageId == sourceRoot.Id
                && asset.Source == PublishAssetSource.Upscaled)
            .ToListAsync(cancellationToken);
        var linkedDerivativeIds = linkedDerivatives.Select(asset => asset.Id).ToHashSet();
        linkedDerivatives.AddRange(db.PublishAssets.Local
            .Where(asset => asset.ProjectId == projectId
                && asset.DerivedFromImageId == sourceRoot.Id
                && asset.Source == PublishAssetSource.Upscaled
                && !linkedDerivativeIds.Contains(asset.Id)));

        foreach (var derived in linkedDerivatives)
        {
            using var derivedBitmap = SKBitmap.Decode(derived.Data);
            if (derivedBitmap is not { Width: > 0, Height: > 0 })
                continue;

            // Reuse the smallest linked derivative that already satisfies the
            // requested raster. Reading dimensions from bytes also detects old
            // or hand-edited metadata before it can be treated as compliant.
            if (derivedBitmap.Width < request.Width || derivedBitmap.Height < request.Height)
                continue;
            if (!HasMatchingUpscaleProvenance(derived.SourceMetadataJson, sourceHash))
                continue;

            if (reusable is null
                || (long)derivedBitmap.Width * derivedBitmap.Height < (long)reusableWidth * reusableHeight
                || ((long)derivedBitmap.Width * derivedBitmap.Height == (long)reusableWidth * reusableHeight
                    && (derivedBitmap.Width < reusableWidth
                        || derivedBitmap.Width == reusableWidth && derivedBitmap.Height < reusableHeight)))
            {
                reusable = derived;
                reusableWidth = derivedBitmap.Width;
                reusableHeight = derivedBitmap.Height;
            }
        }

        if (reusable is not null)
            return new ProjectImagePrintUpscaleResult(ToView(projectId, reusable), false);

        var fileName = PrintUpscaleFileName(sourceRoot.FileName, request.Width, request.Height);
        var data = ProjectImageResampler.ResampleExact(sourceRoot.Data, request.Width, request.Height);
        var normalized = ProjectImageBinary.Normalize(
            data,
            "image/png",
            fileName,
            PrintUpscaleMaxBytes());
        var now = DateTime.UtcNow;
        var asset = new PublishAsset
        {
            ProjectId = projectId,
            Id = expectedId,
            Source = PublishAssetSource.Upscaled,
            FileName = normalized.FileName,
            ContentType = normalized.ContentType,
            Data = normalized.Data,
            AltText = sourceRoot.AltText,
            Prompt = string.Empty,
            GenerationModel = string.Empty,
            SourceMetadataJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                Transform = new
                {
                    Kind = "print-upscale",
                    SourceImageId = sourceRoot.Id,
                    DerivedFromImageId = sourceRoot.Id,
                    SourceRaster = $"{sourceBitmap.Width}x{sourceBitmap.Height}",
                    TargetRaster = $"{normalized.Width}x{normalized.Height}",
                    SourceRasterWidth = sourceBitmap.Width,
                    SourceRasterHeight = sourceBitmap.Height,
                    TargetRasterWidth = normalized.Width,
                    TargetRasterHeight = normalized.Height,
                    WidthInches = request.WidthInches,
                    HeightInches = request.HeightInches,
                    SourceEffectiveDpi = Math.Min(sourceBitmap.Width / request.WidthInches, sourceBitmap.Height / request.HeightInches),
                    RequiredEffectiveDpi = request.TargetDpi,
                    RequiredDpi = request.TargetDpi,
                    TargetDpi = request.TargetDpi,
                    TargetEffectiveDpi = Math.Min(normalized.Width / request.WidthInches, normalized.Height / request.HeightInches),
                    Algorithm = ProjectImageResampler.Algorithm,
                    AlgorithmVersion = ProjectImageResampler.AlgorithmVersion,
                    Interpolation = ProjectImageResampler.Lanczos3Interpolation,
                    AddsNewDetail = false,
                    SourceByteHash = sourceHash,
                    CreationTrigger = CleanOrDefault(request.CreationTrigger, "print-upscale-pipeline"),
                },
                RasterStorage = new
                {
                    normalized.ContentType,
                    normalized.Width,
                    normalized.Height,
                    LayoutTransform = "none",
                },
            }),
            DerivedFromImageId = sourceRoot.Id,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await db.PublishAssets.AddAsync(asset, cancellationToken);
        project.UpdatedAt = now;
        if (ownsWriteOperation)
            await db.SaveChangesAsync(cancellationToken);
        return new ProjectImagePrintUpscaleResult(ToView(projectId, asset), true);
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
            request.Size?.Trim() ?? string.Empty,
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
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
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
        var entityIds = await AttachedEntityIdsAsync(projectId, imageId, cancellationToken);
        await databaseOperation.DisposeAsync();
        foreach (var entityId in entityIds)
            await contextIndexing.ReindexEntityAsync(projectId, entityId, cancellationToken);
        return ToView(projectId, asset);
    }

    public async Task DeleteAsync(Guid projectId, Guid imageId, bool clearAffectedHistory = false, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var project = await GetProjectAsync(projectId, cancellationToken);
        var asset = await db.PublishAssets.FirstOrDefaultAsync(candidate => candidate.ProjectId == projectId && candidate.Id == imageId, cancellationToken);
        if (asset is null) return;
        if (await db.PublishAssets.AsNoTracking().AnyAsync(
            candidate => candidate.ProjectId == projectId
                && candidate.DerivedFromImageId == imageId
                && candidate.Source == PublishAssetSource.Upscaled,
            cancellationToken))
        {
            throw new InvalidOperationException(
                $"Image '{asset.FileName}' has linked upscale derivatives. Delete the upscales before deleting their original image.");
        }
        var chapterUsage = await BuildChapterUsageAsync(db, projectId, cancellationToken);
        var figureChapters = chapterUsage.GetValueOrDefault(imageId)?.Values.ToList() ?? [];
        if (figureChapters.Count > 0)
        {
            throw new InvalidOperationException(
                $"Image '{asset.FileName}' is used in: "
                + string.Join(", ", figureChapters.OrderBy(title => title, StringComparer.OrdinalIgnoreCase))
                + ". Remove or replace every chapter figure or Designed Page placement before deleting the image.");
        }
        var figureMatter = (await db.PublicationMatter
            .AsNoTracking()
            .Where(matter => matter.Edition.ProjectId == projectId)
            .Select(matter => new { matter.Title, EditionName = matter.Edition.Name, matter.ManuscriptJson })
            .ToListAsync(cancellationToken))
            .Where(matter => ManuscriptCodec.Deserialize(matter.ManuscriptJson).Content.Any(
                block => block.Type == ManuscriptBlockType.Figure && block.ImageId == imageId))
            .Select(matter => $"{matter.EditionName} / {matter.Title}")
            .ToList();
        if (figureMatter.Count > 0)
        {
            throw new InvalidOperationException(
                $"Image '{asset.FileName}' is used by a semantic publication-matter figure in: "
                + string.Join(", ", figureMatter)
                + ". Remove or replace those figures before deleting the image.");
        }
        var compositionUses = (await db.PageCompositionVariants
            .AsNoTracking()
            .Where(variant => variant.Composition.ProjectId == projectId
                && variant.DetachedAt == null && variant.Composition.DetachedAt == null)
            .Select(variant => new { variant.Composition.Name, variant.SceneJson })
            .ToListAsync(cancellationToken))
            .Where(item => SceneUsesImage(item.SceneJson, imageId))
            .Select(item => item.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var coverUses = (await db.PublicationCoverDesigns
            .AsNoTracking()
            .Where(cover => cover.Edition.ProjectId == projectId)
            .Select(cover => new { cover.Edition.Name, cover.CompositionSceneJson, cover.SurfaceScenesJson })
            .ToListAsync(cancellationToken))
            .Where(item => SceneUsesImage(item.CompositionSceneJson, imageId)
                || SurfaceScenesUseImage(item.SurfaceScenesJson, imageId))
            .Select(item => item.Name)
            .ToList();
        var coreCoverUses = (await db.PublicationBookCoverDesigns
            .AsNoTracking()
            .Where(cover => cover.ProjectId == projectId)
            .Select(cover => cover.CompositionSceneJson)
            .ToListAsync(cancellationToken))
            .Where(sceneJson => SceneUsesImage(sceneJson, imageId))
            .Select(_ => "Core Book")
            .ToList();
        if (compositionUses.Count > 0 || coverUses.Count > 0 || coreCoverUses.Count > 0)
        {
            throw new InvalidOperationException(
                $"Image '{asset.FileName}' is used by a page or cover composition: "
                + string.Join(", ", compositionUses.Concat(coverUses).Concat(coreCoverUses).Distinct(StringComparer.Ordinal))
                + ". Remove or replace those scene objects before deleting the image.");
        }
        var entityIds = await AttachedEntityIdsAsync(projectId, imageId, cancellationToken);
        if (await db.PublicationEditions.AsNoTracking().AnyAsync(
            edition => edition.ProjectId == projectId && edition.SelectedCoverImageId == imageId,
            cancellationToken))
        {
            throw new InvalidOperationException(
                $"Image '{asset.FileName}' is selected as a publication cover. Choose another cover in Publish before deleting the image.");
        }
        if (await db.PublicationImagePlacements.AsNoTracking().AnyAsync(
            placement => placement.Edition.ProjectId == projectId && placement.AssetId == imageId,
            cancellationToken))
        {
            throw new InvalidOperationException(
                $"Image '{asset.FileName}' is used by a publication-edition placement. Remove the placement in Publish before deleting the image.");
        }

        var dependentHistory = await authoringHistory.FindDependentStreamsAsync(
            projectId, AuthoringHistoryDependencyKind.ProjectImage, imageId, cancellationToken);
        if (dependentHistory.Count > 0 && !clearAffectedHistory)
            throw new InvalidOperationException($"AUTHORING_HISTORY_DEPENDENCY: This image is retained by {dependentHistory.Count} current in-process Undo/Redo histor{(dependentHistory.Count == 1 ? "y" : "ies")}. Delete it and clear the affected history?");

        db.PublishAssets.Remove(asset);
        project.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (dependentHistory.Count > 0)
            await authoringHistory.ClearDependentStreamsAsync(projectId, AuthoringHistoryDependencyKind.ProjectImage, imageId, CancellationToken.None);
        await transaction.DisposeAsync();
        await databaseOperation.DisposeAsync();
        foreach (var entityId in entityIds)
            await contextIndexing.ReindexEntityAsync(projectId, entityId, cancellationToken);
    }

    private async Task<Project> GetProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        return await db.Projects.FirstOrDefaultAsync(project => project.Id == projectId, cancellationToken)
                ?? throw new InvalidOperationException($"Project {projectId} not found.");
    }
    public static ProjectImageView ToView(Guid projectId, PublishAsset asset) =>
        ToView(projectId, asset, []);

    private static ProjectImageView ToView(
        Guid projectId,
        PublishAsset asset,
        IReadOnlyList<PublishAsset> directUpscales)
    {
        var (width, height) = ReadDimensions(asset.Data);
        return new ProjectImageView(
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
            asset.Data.LongLength,
            width,
            height,
            asset.DerivedFromImageId,
            directUpscales
                .Where(child => child.Source == PublishAssetSource.Upscaled)
                .Select(child => ToUpscaleSummary(projectId, child))
                .OrderBy(child => child.Width * (long)child.Height)
                .ThenBy(child => child.Width)
                .ThenBy(child => child.Height)
                .ToList());
    }

    private static IReadOnlyList<ProjectImageView> ToViews(Guid projectId, IReadOnlyList<PublishAsset> assets)
    {
        var directUpscales = assets
            .Where(asset => asset.Source == PublishAssetSource.Upscaled && asset.DerivedFromImageId is not null)
            .GroupBy(asset => asset.DerivedFromImageId!.Value)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<PublishAsset>)group.ToList());

        return assets
            .Select(asset => ToView(projectId, asset, directUpscales.GetValueOrDefault(asset.Id, [])))
            .ToList();
    }

    private static ProjectImageUpscaleSummary ToUpscaleSummary(Guid projectId, PublishAsset asset)
    {
        var (width, height) = ReadDimensions(asset.Data);
        var metadata = ReadUpscaleMetadata(asset.SourceMetadataJson);
        return new ProjectImageUpscaleSummary(
            asset.Id,
            asset.FileName,
            $"/projects/{projectId:N}/images/{asset.Id:N}/content?maxEdge=640",
            width,
            height,
            width > 0 && height > 0 ? $"{width}x{height}" : string.Empty,
            metadata.SourceEffectiveDpi,
            metadata.RequiredEffectiveDpi,
            metadata.TargetEffectiveDpi,
            metadata.Algorithm,
            metadata.AlgorithmVersion,
            metadata.SourceByteHash,
            metadata.AddsNewDetail,
            metadata.CreationTrigger,
            asset.CreatedAt,
            metadata.SourceRaster,
            metadata.TargetRaster.Length > 0 ? metadata.TargetRaster : width > 0 && height > 0 ? $"{width}x{height}" : string.Empty);
    }

    private static (int Width, int Height) ReadDimensions(byte[] data)
    {
        try
        {
            using var stream = new SKMemoryStream(data);
            using var codec = SKCodec.Create(stream);
            return codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0
                ? (0, 0)
                : (codec.Info.Width, codec.Info.Height);
        }
        catch (Exception)
        {
            // A corrupt asset remains visible in the library with unknown
            // dimensions; publication preflight is responsible for blocking it.
            return (0, 0);
        }
    }

    private static async Task<PublishAsset> FindNonUpscaledRootAsync(
        AppDbContext db,
        PublishAsset source,
        CancellationToken cancellationToken)
    {
        var current = source;
        var visited = new HashSet<Guid>();
        while (current.Source == PublishAssetSource.Upscaled)
        {
            if (current.DerivedFromImageId is not Guid parentId || parentId == Guid.Empty)
                throw new InvalidOperationException("The image upscale source is missing its provenance parent.");
            if (!visited.Add(current.Id))
                throw new InvalidOperationException("The image upscale lineage contains a cycle.");

            current = await db.PublishAssets
                .AsNoTracking()
                .FirstOrDefaultAsync(asset => asset.ProjectId == source.ProjectId && asset.Id == parentId, cancellationToken)
                ?? throw new InvalidOperationException("The image upscale source is missing its provenance parent.");
        }

        return current;
    }

    private static Guid DeterministicUpscaleId(Guid sourceId, string sourceHash, int width, int height)
    {
        var identity = $"lorekeeper-image-upscale|{ProjectImageResampler.AlgorithmVersion}|{sourceId:N}|{sourceHash}|{width}x{height}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static bool HasMatchingUpscaleProvenance(string metadataJson, string sourceHash)
    {
        var metadata = ReadUpscaleMetadata(metadataJson);
        if (!string.Equals(metadata.Algorithm, ProjectImageResampler.Algorithm, StringComparison.Ordinal)
            || !string.Equals(metadata.AlgorithmVersion, ProjectImageResampler.AlgorithmVersion, StringComparison.Ordinal)
            || metadata.AddsNewDetail)
        {
            return false;
        }

        // Legacy derivatives may not have captured a byte hash. Their parent
        // identity remains authoritative and dimensions are still decoded above.
        return string.IsNullOrWhiteSpace(metadata.SourceByteHash)
            || string.Equals(metadata.SourceByteHash, sourceHash, StringComparison.OrdinalIgnoreCase);
    }

    private static UpscaleMetadata ReadUpscaleMetadata(string metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
            return new UpscaleMetadata();

        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            if (!TryGetProperty(document.RootElement, "Transform", out var transform)
                || transform.ValueKind != JsonValueKind.Object)
            {
                return new UpscaleMetadata();
            }

            var interpolation = ReadString(transform, "Interpolation");
            var kind = ReadString(transform, "Kind");
            var knownLanczosTransform = string.Equals(kind, "print-upscale", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "print-resample", StringComparison.OrdinalIgnoreCase)
                || string.Equals(interpolation, ProjectImageResampler.Lanczos3Interpolation, StringComparison.Ordinal);
            var algorithm = ReadString(transform, "Algorithm")
                ?? (knownLanczosTransform
                    ? ProjectImageResampler.Algorithm
                    : string.Empty);
            var algorithmVersion = ReadString(transform, "AlgorithmVersion")
                ?? (string.Equals(algorithm, ProjectImageResampler.Algorithm, StringComparison.Ordinal)
                    ? ProjectImageResampler.AlgorithmVersion
                    : string.Empty);
            return new UpscaleMetadata(
                ReadDouble(transform, "SourceEffectiveDpi"),
                ReadDouble(transform, "RequiredEffectiveDpi")
                    ?? ReadDouble(transform, "RequiredDpi")
                    ?? ReadDouble(transform, "TargetDpi"),
                ReadDouble(transform, "TargetEffectiveDpi"),
                algorithm,
                algorithmVersion,
                ReadString(transform, "SourceByteHash") ?? string.Empty,
                ReadBoolean(transform, "AddsNewDetail"),
                ReadString(transform, "CreationTrigger") ?? ReadString(transform, "PlannedBy") ?? string.Empty,
                ReadString(transform, "SourceRaster") ?? string.Empty,
                ReadString(transform, "TargetRaster") ?? string.Empty);
        }
        catch (JsonException)
        {
            return new UpscaleMetadata();
        }
        catch (InvalidOperationException)
        {
            return new UpscaleMetadata();
        }
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        TryGetProperty(element, propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? ReadDouble(JsonElement element, string propertyName) =>
        TryGetProperty(element, propertyName, out var value) && value.TryGetDouble(out var result)
            ? result
            : null;

    private static bool ReadBoolean(JsonElement element, string propertyName) =>
        TryGetProperty(element, propertyName, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
        && value.GetBoolean();

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.TryGetProperty(propertyName, out value))
            return true;

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private int PrintUpscaleMaxBytes()
    {
        const int pressMaximumBytes = 256 * 1024 * 1024;
        var configured = imageOptions.Value.MaxPrintUpscaleBytes;
        return configured > 0 ? Math.Min(configured, pressMaximumBytes) : pressMaximumBytes;
    }

    private sealed record UpscaleMetadata(
        double? SourceEffectiveDpi = null,
        double? RequiredEffectiveDpi = null,
        double? TargetEffectiveDpi = null,
        string Algorithm = "",
        string AlgorithmVersion = "",
        string SourceByteHash = "",
        bool AddsNewDetail = false,
        string CreationTrigger = "",
        string SourceRaster = "",
        string TargetRaster = "");

    private async Task<IReadOnlyList<Guid>> AttachedEntityIdsAsync(Guid projectId, Guid imageId, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var keys = await db.EntityVisualExamples
            .AsNoTracking()
            .Where(example => example.ProjectId == projectId && example.ImageId == imageId)
            .Select(example => example.GraphNode.Key)
            .ToListAsync(cancellationToken);
        return keys.Where(key => Guid.TryParseExact(key, "N", out _)).Select(key => Guid.ParseExact(key, "N")).ToList();
    }

    private static async Task<Dictionary<Guid, Dictionary<Guid, string>>> BuildChapterUsageAsync(
        AppDbContext db,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var usages = new Dictionary<Guid, Dictionary<Guid, string>>();
        var chapters = await db.Chapters
            .AsNoTracking()
            .Where(chapter => chapter.ProjectId == projectId)
            .Select(chapter => new { chapter.Id, chapter.Title, chapter.ManuscriptJson })
            .ToListAsync(cancellationToken);
        var chapterTitles = chapters.ToDictionary(chapter => chapter.Id, chapter => chapter.Title);

        foreach (var chapter in chapters)
            AddChapterUses(usages, chapter.Id, chapter.Title, FigureImageIds(chapter.ManuscriptJson));

        var compositions = await db.PageCompositions
            .AsNoTracking()
            .Where(composition => composition.ProjectId == projectId
                && composition.ChapterId != null
                && composition.DetachedAt == null)
            .Select(composition => new
            {
                composition.Id,
                ChapterId = composition.ChapterId!.Value,
                composition.SemanticManuscriptJson,
            })
            .ToListAsync(cancellationToken);
        var compositionChapters = compositions.ToDictionary(
            composition => composition.Id,
            composition => new
            {
                composition.ChapterId,
                Title = chapterTitles.GetValueOrDefault(composition.ChapterId, "Untitled chapter"),
            });

        foreach (var composition in compositions)
        {
            AddChapterUses(
                usages,
                composition.ChapterId,
                compositionChapters[composition.Id].Title,
                FigureImageIds(composition.SemanticManuscriptJson));
        }

        var compositionIds = compositionChapters.Keys.ToList();
        if (compositionIds.Count == 0)
            return usages;

        var variants = await db.PageCompositionVariants
            .AsNoTracking()
            .Where(variant => compositionIds.Contains(variant.CompositionId) && variant.DetachedAt == null)
            .Select(variant => new { variant.CompositionId, variant.SceneJson })
            .ToListAsync(cancellationToken);
        foreach (var variant in variants)
        {
            AddChapterUses(
                usages,
                compositionChapters[variant.CompositionId].ChapterId,
                compositionChapters[variant.CompositionId].Title,
                SceneImageIds(variant.SceneJson));
        }

        return usages;
    }

    private static void AddChapterUses(
        Dictionary<Guid, Dictionary<Guid, string>> usages,
        Guid chapterId,
        string chapterTitle,
        IEnumerable<Guid> imageIds)
    {
        foreach (var imageId in imageIds.Where(imageId => imageId != Guid.Empty).Distinct())
        {
            if (!usages.TryGetValue(imageId, out var chapters))
            {
                chapters = [];
                usages.Add(imageId, chapters);
            }

            chapters[chapterId] = string.IsNullOrWhiteSpace(chapterTitle) ? "Untitled chapter" : chapterTitle.Trim();
        }
    }

    private static IEnumerable<Guid> FigureImageIds(string json)
    {
        var manuscript = ManuscriptCodec.Deserialize(json);
        return manuscript.Content
            .Where(block => block.Type == ManuscriptBlockType.Figure && block.ImageId is not null)
            .Select(block => block.ImageId!.Value);
    }

    private static IEnumerable<Guid> SceneImageIds(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var scene = System.Text.Json.JsonSerializer.Deserialize<CompositionScene>(json, ManuscriptCodec.JsonOptions);
            return scene?.Objects
                .Where(item => item.Kind == CompositionObjectKind.Image && item.ImageId is not null)
                .Select(item => item.ImageId!.Value)
                .ToList() ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
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

    private static bool SceneUsesImage(string json, Guid imageId)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            var scene = System.Text.Json.JsonSerializer.Deserialize<CompositionScene>(json, ManuscriptCodec.JsonOptions);
            return scene?.Objects.Any(item => item.ImageId == imageId) == true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static bool SurfaceScenesUseImage(string json, Guid imageId)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            var scenes = JsonSerializer.Deserialize<Dictionary<string, string>>(json, ManuscriptCodec.JsonOptions);
            return scenes?.Values.Any(sceneJson => SceneUsesImage(sceneJson, imageId)) == true;
        }
        catch (JsonException)
        {
            // A malformed surface collection cannot be proven free of a
            // reference; keep the asset until the cover data is repaired.
            return true;
        }
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

    private static string ResizeFileName(string? requestedFileName, string sourceFileName)
    {
        var requested = Path.GetFileName(requestedFileName?.Trim());
        if (!string.IsNullOrWhiteSpace(requested))
            return Path.ChangeExtension(requested, ".png");
        var sourceStem = Path.GetFileNameWithoutExtension(sourceFileName);
        if (string.IsNullOrWhiteSpace(sourceStem)) sourceStem = "image";
        return $"{sourceStem}-resized.png";
    }

    private static string PrintUpscaleFileName(string sourceFileName, int width, int height)
    {
        var sourceStem = Path.GetFileNameWithoutExtension(sourceFileName);
        if (string.IsNullOrWhiteSpace(sourceStem)) sourceStem = "image";
        return $"{sourceStem}-upscale-{width}x{height}.png";
    }

    private static string CleanOrDefault(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;
}
