using System.Runtime.InteropServices;
using Docnet.Core;
using Docnet.Core.Models;
using Microsoft.Extensions.Caching.Memory;
using SkiaSharp;
using UglyToad.PdfPig;

namespace Lorekeeper.Publish;

public sealed record PublicationArtifactPreviewPage(
    byte[] Data,
    string ArtifactSha256,
    DateTime CreatedAt,
    int PageNumber,
    int Width,
    int Height);

public interface IPublicationArtifactPreviewService
{
    Task<PublicationArtifactPreviewPage?> RenderPageAsync(
        Guid projectId,
        Guid artifactId,
        int pageNumber,
        int widthPixels,
        CancellationToken cancellationToken = default);
}

public sealed class PublicationArtifactPreviewCache : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions
    {
        SizeLimit = 96 * 1024 * 1024,
    });

    public bool TryGetValue(string key, out PublicationArtifactPreviewPage? page) =>
        _cache.TryGetValue(key, out page);

    public void Set(
        string key,
        PublicationArtifactPreviewPage page,
        MemoryCacheEntryOptions options) =>
        _cache.Set(key, page, options);

    public void Dispose() => _cache.Dispose();
}

public sealed class PublicationArtifactPreviewService(
    IPublicationRenderService renders,
    PublicationArtifactPreviewCache cache) : IPublicationArtifactPreviewService
{
    private const int MinimumWidth = 320;
    private const int MaximumWidth = 1600;
    private static readonly SemaphoreSlim RenderGate = new(2, 2);

    public async Task<PublicationArtifactPreviewPage?> RenderPageAsync(
        Guid projectId,
        Guid artifactId,
        int pageNumber,
        int widthPixels,
        CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1)
            return null;

        var artifact = await renders.GetArtifactAsync(projectId, artifactId, cancellationToken);
        if (artifact is null
            || !artifact.MediaType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)
            || artifact.PageCount is int declaredPageCount && pageNumber > declaredPageCount)
        {
            return null;
        }

        var requestedWidth = Math.Clamp(widthPixels, MinimumWidth, MaximumWidth);
        var cacheKey = $"publication-pdf-preview:{artifact.Sha256}:{pageNumber}:{requestedWidth}";
        if (cache.TryGetValue(cacheKey, out var cached))
            return cached;

        await RenderGate.WaitAsync(cancellationToken);
        try
        {
            if (cache.TryGetValue(cacheKey, out cached))
                return cached;

            cancellationToken.ThrowIfCancellationRequested();
            var preview = RenderPage(artifact, pageNumber, requestedWidth);
            cache.Set(
                cacheKey,
                preview,
                new MemoryCacheEntryOptions
                {
                    SlidingExpiration = TimeSpan.FromMinutes(20),
                    Size = preview.Data.LongLength,
                });
            return preview;
        }
        finally
        {
            RenderGate.Release();
        }
    }

    private static PublicationArtifactPreviewPage RenderPage(
        Lorekeeper.Models.PublicationArtifact artifact,
        int pageNumber,
        int widthPixels)
    {
        using var pdf = PdfDocument.Open(artifact.Data);
        if (pageNumber > pdf.NumberOfPages)
            throw new ArgumentOutOfRangeException(nameof(pageNumber));

        var sourcePage = pdf.GetPage(pageNumber);
        var sourceWidth = Convert.ToDouble(sourcePage.Width);
        var sourceHeight = Convert.ToDouble(sourcePage.Height);
        if (!double.IsFinite(sourceWidth) || !double.IsFinite(sourceHeight) || sourceWidth <= 0 || sourceHeight <= 0)
            throw new InvalidDataException("The PDF page geometry is invalid.");

        var heightPixels = Math.Max(1, (int)Math.Round(widthPixels * sourceHeight / sourceWidth));
        var smallerDimension = Math.Min(widthPixels, heightPixels);
        var largerDimension = Math.Max(widthPixels, heightPixels);
        using var documentReader = DocLib.Instance.GetDocReader(
            artifact.Data,
            new PageDimensions(smallerDimension, largerDimension));
        using var pageReader = documentReader.GetPageReader(pageNumber - 1);
        var rawBytes = pageReader.GetImage();
        var renderedWidth = pageReader.GetPageWidth();
        var renderedHeight = pageReader.GetPageHeight();

        using var bitmap = new SKBitmap(renderedWidth, renderedHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        Marshal.Copy(rawBytes, 0, bitmap.GetPixels(), rawBytes.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 95);
        return new(
            encoded.ToArray(),
            artifact.Sha256,
            artifact.CreatedAt,
            pageNumber,
            renderedWidth,
            renderedHeight);
    }
}
