using System.Text.Json;
using Lorekeeper.Composition;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Lorekeeper.Images;

public interface IProjectImageOutpaintService
{
    Task<ProjectImageOutpaintResult> OutpaintAsync(
        Guid projectId,
        Guid sourceImageId,
        ProjectImageOutpaintRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record ProjectImageOutpaintRequest(
    int Width,
    int Height,
    string Prompt,
    string FileName,
    string AltText);

public sealed record ProjectImageOutpaintResult(
    ProjectImageView Image,
    Guid SourceImageId,
    string SourceRaster,
    string TargetRaster,
    string ProviderRaster,
    bool ProviderRasterResized,
    bool SourceRegionRestored,
    bool AddsGeneratedBorderContent,
    bool PreservesSourcePixels,
    bool DeterministicResizeAddsNewDetail,
    string Interpolation,
    string Summary);

public sealed class ProjectImageOutpaintService(
    IAppDatabaseOperationFactory database,
    IProjectImageService images,
    IProjectImageProvider provider,
    IOptions<ProjectImageGenerationOptions> options) : IProjectImageOutpaintService
{
    public async Task<ProjectImageOutpaintResult> OutpaintAsync(
        Guid projectId,
        Guid sourceImageId,
        ProjectImageOutpaintRequest request,
        CancellationToken cancellationToken = default)
    {
        if (sourceImageId == Guid.Empty)
            throw new ArgumentException("sourceImageId is required.", nameof(sourceImageId));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Outpaint prompt is required.", nameof(request));
        LayoutImageSizeResolver.Validate(request.Width, request.Height);

        var source = await images.GetAsync(projectId, sourceImageId, cancellationToken)
            ?? throw new InvalidOperationException("Source image was not found in this project.");
        var sourceData = await images.GetDataAsync(projectId, sourceImageId, cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Source image data was not found in this project.");
        using var sourceBitmap = SKBitmap.Decode(sourceData.Data)
            ?? throw new InvalidOperationException("Source image data could not be decoded.");
        if (sourceBitmap.Width <= 0 || sourceBitmap.Height <= 0)
            throw new InvalidOperationException("Source image dimensions are invalid.");
        if (sourceBitmap.Width > request.Width || sourceBitmap.Height > request.Height)
            throw new InvalidOperationException("The requested outpaint canvas must contain the complete source image.");
        if (sourceBitmap.Width == request.Width && sourceBitmap.Height == request.Height)
            throw new InvalidOperationException("The requested canvas is the same size as the source; use edit_project_image instead.");

        var left = (request.Width - sourceBitmap.Width) / 2;
        var top = (request.Height - sourceBitmap.Height) / 2;
        var sourceCanvas = CreateCanvas(sourceBitmap, request.Width, request.Height, left, top);
        var editMask = CreateOutpaintMask(request.Width, request.Height, left, top, sourceBitmap.Width, sourceBitmap.Height);
        var prompt = $"{request.Prompt.Trim()}\n\nStrict source-preserving outpaint: the supplied source artwork is centered on the larger canvas. Extend only into the transparent masked border. Do not redraw, alter, crop, move, scale, or reinterpret any source pixels inside the existing artwork rectangle. The final result must fill the added canvas around that rectangle.";
        var providerResult = await provider.EditAsync(
            new ProjectImageProviderEditRequest(
                prompt,
                $"{request.Width}x{request.Height}",
                Count: 1,
                string.IsNullOrWhiteSpace(options.Value.DefaultMainlineModel) ? string.Empty : options.Value.DefaultMainlineModel,
                string.IsNullOrWhiteSpace(options.Value.DefaultImageModel) ? string.Empty : options.Value.DefaultImageModel,
                new ProjectImageProviderReference("outpaint-canvas.png", "image/png", sourceCanvas),
                new ProjectImageProviderReference("outpaint-mask.png", "image/png", editMask),
                [],
                "png",
                string.IsNullOrWhiteSpace(options.Value.DefaultQuality) ? "auto" : options.Value.DefaultQuality,
                null),
            cancellationToken);
        if (providerResult.Images.Count == 0)
            throw new InvalidOperationException("The image provider returned no outpaint image; no derived image was created.");

        var providerImage = providerResult.Images[0];
        using var providerBitmap = SKBitmap.Decode(providerImage.Data)
            ?? throw new InvalidOperationException("The image provider returned an unreadable outpaint image; no derived image was created.");
        if (providerBitmap.Width <= 0 || providerBitmap.Height <= 0)
            throw new InvalidOperationException("The image provider returned invalid outpaint dimensions; no derived image was created.");
        var providerAspect = (double)providerBitmap.Width / providerBitmap.Height;
        if (!LayoutImageSizeResolver.AspectMatches(providerAspect, (double)request.Width / request.Height))
            throw new InvalidOperationException($"The image provider returned {providerBitmap.Width}x{providerBitmap.Height}, which does not match the requested {request.Width}x{request.Height} outpaint aspect; no derived image was created.");

        var providerRaster = $"{providerBitmap.Width}x{providerBitmap.Height}";
        var providerRasterResized = providerBitmap.Width != request.Width || providerBitmap.Height != request.Height;
        byte[] providerData = providerRasterResized
            ? ProjectImageResize.ResizeExact(providerImage.Data, request.Width, request.Height)
            : providerImage.Data;
        using var finalProviderBitmap = SKBitmap.Decode(providerData)
            ?? throw new InvalidOperationException("The provider outpaint could not be normalized to the requested raster; no derived image was created.");
        var finalData = CompositeSource(finalProviderBitmap, sourceBitmap, request.Width, request.Height, left, top);
        var fileName = OutpaintFileName(request.FileName, source.FileName);
        var normalized = ProjectImageBinary.Normalize(
            finalData,
            "image/png",
            fileName,
            Math.Max(ProjectImageBinary.DefaultMaxBytes, options.Value.MaxProviderOutputBytes));
        if (normalized.Width != request.Width || normalized.Height != request.Height)
            throw new InvalidOperationException("The strict outpaint result did not retain the requested raster; no derived image was created.");

        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var project = await db.Projects.FirstOrDefaultAsync(item => item.Id == projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");
        var sourceInProject = await db.PublishAssets.AsNoTracking()
            .FirstOrDefaultAsync(item => item.ProjectId == projectId && item.Id == sourceImageId, cancellationToken)
            ?? throw new InvalidOperationException("Source image was deleted before the outpaint could be saved; no derived image was created.");
        var now = DateTime.UtcNow;
        var asset = new PublishAsset
        {
            ProjectId = projectId,
            Source = PublishAssetSource.Outpainted,
            FileName = normalized.FileName,
            ContentType = normalized.ContentType,
            Data = normalized.Data,
            AltText = string.IsNullOrWhiteSpace(request.AltText) ? sourceInProject.AltText : request.AltText.Trim(),
            Prompt = request.Prompt.Trim(),
            GenerationModel = providerResult.ImageModel,
            SourceMetadataJson = JsonSerializer.Serialize(new
            {
                Transform = new
                {
                    Kind = "strict-source-preserving-outpaint",
                    SourceImageId = sourceInProject.Id,
                    SourceRaster = $"{sourceBitmap.Width}x{sourceBitmap.Height}",
                    TargetRaster = $"{request.Width}x{request.Height}",
                    SourceRegion = new { Left = left, Top = top, Width = sourceBitmap.Width, Height = sourceBitmap.Height },
                    SourceRegionRestored = true,
                    Interpolation = ProjectImageResize.DeterministicInterpolation,
                    AddsGeneratedBorderContent = true,
                    PreservesSourcePixels = true,
                    DeterministicResizeAddsNewDetail = false,
                },
                Provider = new
                {
                    providerResult.Provider,
                    providerResult.MainlineModel,
                    providerResult.ImageModel,
                    ProviderOutputRaster = providerRaster,
                    ProviderOutputResized = providerRasterResized,
                    IntermediatePersisted = false,
                    RevisedPrompt = providerImage.RevisedPrompt,
                    providerImage.ResponseId,
                    providerImage.CallId,
                },
                RasterStorage = new
                {
                    normalized.ContentType,
                    normalized.Width,
                    normalized.Height,
                    LayoutTransform = "centered-source-region-restored",
                },
            }),
            DerivedFromImageId = sourceInProject.Id,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await db.PublishAssets.AddAsync(asset, cancellationToken);
        project.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);

        return new ProjectImageOutpaintResult(
            ProjectImageService.ToView(projectId, asset),
            sourceInProject.Id,
            $"{sourceBitmap.Width}x{sourceBitmap.Height}",
            $"{request.Width}x{request.Height}",
            providerRaster,
            providerRasterResized,
            SourceRegionRestored: true,
            AddsGeneratedBorderContent: true,
            PreservesSourcePixels: true,
            DeterministicResizeAddsNewDetail: false,
            ProjectImageResize.DeterministicInterpolation,
            providerRasterResized
                ? $"Strict outpaint completed at {request.Width}x{request.Height}. The provider generated the added border and returned {providerRaster}; it was deterministically resized with {ProjectImageResize.DeterministicInterpolation}, then the source region was restored exactly. The deterministic resize added no new detail."
                : $"Strict outpaint completed at {request.Width}x{request.Height}. The original source region was restored exactly; no deterministic provider resize was required.");
    }

    private static byte[] CreateCanvas(SKBitmap source, int width, int height, int left, int top)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var sourcePaint = new SKPaint { BlendMode = SKBlendMode.Src, IsAntialias = false };
        canvas.DrawBitmap(source, left, top, sourcePaint);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("The outpaint source canvas could not be encoded.");
        return encoded.ToArray();
    }

    private static byte[] CreateOutpaintMask(int width, int height, int left, int top, int sourceWidth, int sourceHeight)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var sourcePaint = new SKPaint { Color = SKColors.White, BlendMode = SKBlendMode.Src, IsAntialias = false };
        canvas.DrawRect(new SKRect(left, top, left + sourceWidth, top + sourceHeight), sourcePaint);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("The outpaint mask could not be encoded.");
        return encoded.ToArray();
    }

    private static byte[] CompositeSource(SKBitmap provider, SKBitmap source, int width, int height, int left, int top)
    {
        if (provider.Width != width || provider.Height != height)
            throw new InvalidOperationException("The normalized provider outpaint has unexpected dimensions.");
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var providerPaint = new SKPaint { BlendMode = SKBlendMode.Src, IsAntialias = false };
        canvas.DrawBitmap(provider, 0, 0, providerPaint);
        using var sourcePaint = new SKPaint { BlendMode = SKBlendMode.Src, IsAntialias = false };
        canvas.DrawBitmap(source, left, top, sourcePaint);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("The strict outpaint result could not be encoded.");
        return encoded.ToArray();
    }

    private static string OutpaintFileName(string? requestedFileName, string sourceFileName)
    {
        var requested = Path.GetFileName(requestedFileName?.Trim());
        if (!string.IsNullOrWhiteSpace(requested))
            return Path.ChangeExtension(requested, ".png");
        var sourceStem = Path.GetFileNameWithoutExtension(sourceFileName);
        return $"{(string.IsNullOrWhiteSpace(sourceStem) ? "image" : sourceStem)}-outpaint.png";
    }
}
