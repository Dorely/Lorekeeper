using Microsoft.Extensions.Caching.Memory;

namespace Lorekeeper.Publish;

public sealed record PublicationCoverPreviewDocument(
    int Index,
    string Kind,
    string Label,
    string FileName,
    int PageCount);

public sealed record PublicationCoverPreviewView(
    Guid Token,
    IReadOnlyList<PublicationCoverPreviewDocument> Documents,
    IReadOnlyList<PublicationRenderDiagnostic> Diagnostics);

public interface IPublicationCoverPreviewService
{
    Task<PublicationCoverPreviewView> GenerateAsync(
        Guid projectId,
        Guid? editionId,
        string surfaceRole,
        CancellationToken cancellationToken = default);

    Task<PublicationArtifactPreviewPage?> RenderPageAsync(
        Guid projectId,
        Guid token,
        int documentIndex,
        int pageNumber,
        int widthPixels,
        CancellationToken cancellationToken = default);
}

public sealed class PublicationCoverPreviewCache : IDisposable
{
    private const long MaximumEntryBytes = 256L * 1024 * 1024;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions
    {
        SizeLimit = 512L * 1024 * 1024,
    });

    internal void Set(Guid token, PublicationCoverPreviewEntry entry)
    {
        var size = Math.Max(1, entry.Pdfs.Sum(item => item.Data.LongLength));
        if (size > MaximumEntryBytes)
            throw new InvalidOperationException("The generated cover preview is too large to keep in the transient preview cache.");
        _cache.Set(
            token,
            entry,
            new MemoryCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromMinutes(10),
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30),
                Size = size,
            });
    }

    internal bool TryGet(Guid token, out PublicationCoverPreviewEntry? entry) =>
        _cache.TryGetValue(token, out entry);

    public void Dispose() => _cache.Dispose();
}

internal sealed record PublicationCoverPreviewEntry(
    Guid ProjectId,
    DateTime CreatedAt,
    IReadOnlyList<PublicationTransientCoverPdf> Pdfs,
    IReadOnlyList<PublicationRenderDiagnostic> Diagnostics);

public sealed class PublicationCoverPreviewService(
    PublicationRenderProcessor renderer,
    PublicationCoverPreviewCache previews,
    PublicationArtifactPreviewCache pages) : IPublicationCoverPreviewService
{
    private const int MinimumWidth = 320;
    private const int MaximumWidth = 1600;
    private static readonly SemaphoreSlim PageRenderGate = new(2, 2);

    public async Task<PublicationCoverPreviewView> GenerateAsync(
        Guid projectId,
        Guid? editionId,
        string surfaceRole,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(surfaceRole, "digital-cloth-setup", StringComparison.Ordinal))
            throw new InvalidOperationException("Cloth setup produces a manifest rather than a cover PDF.");
        var render = await renderer.RenderCoverPreviewAsync(
            projectId,
            editionId,
            surfaceRole,
            cancellationToken);
        var token = Guid.NewGuid();
        var createdAt = DateTime.UtcNow;
        previews.Set(token, new(projectId, createdAt, render.Pdfs, render.Diagnostics));
        return View(token, render.Pdfs, render.Diagnostics);
    }

    public async Task<PublicationArtifactPreviewPage?> RenderPageAsync(
        Guid projectId,
        Guid token,
        int documentIndex,
        int pageNumber,
        int widthPixels,
        CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1
            || !previews.TryGet(token, out var entry)
            || entry is null
            || entry.ProjectId != projectId
            || documentIndex < 0
            || documentIndex >= entry.Pdfs.Count)
            return null;
        var pdf = entry.Pdfs[documentIndex];
        if (pageNumber > pdf.PageCount)
            return null;
        var requestedWidth = Math.Clamp(widthPixels, MinimumWidth, MaximumWidth);
        var cacheKey = $"cover-pdf-preview:{token:N}:{pdf.Sha256}:{pageNumber}:{requestedWidth}";
        if (pages.TryGetValue(cacheKey, out var cached))
            return cached;
        await PageRenderGate.WaitAsync(cancellationToken);
        try
        {
            if (pages.TryGetValue(cacheKey, out cached))
                return cached;
            cancellationToken.ThrowIfCancellationRequested();
            var page = PublicationArtifactPreviewService.RenderPage(
                pdf.Data,
                pdf.Sha256,
                entry.CreatedAt,
                pageNumber,
                requestedWidth);
            pages.Set(
                cacheKey,
                page,
                new MemoryCacheEntryOptions
                {
                    SlidingExpiration = TimeSpan.FromMinutes(10),
                    Size = page.Data.LongLength,
                });
            return page;
        }
        finally
        {
            PageRenderGate.Release();
        }
    }

    private static PublicationCoverPreviewView View(
        Guid token,
        IReadOnlyList<PublicationTransientCoverPdf> pdfs,
        IReadOnlyList<PublicationRenderDiagnostic> diagnostics) =>
        new(
            token,
            pdfs.Select((pdf, index) => new PublicationCoverPreviewDocument(
                    index,
                    pdf.Kind,
                    Label(pdf.Kind),
                    pdf.FileName,
                    pdf.PageCount))
                .OrderBy(document => DocumentOrder(document.Kind))
                .ToList(),
            diagnostics);

    private static int DocumentOrder(string kind) => kind switch
    {
        "front-cover-pdf" => 0,
        "back-cover-pdf" => 1,
        _ => 0,
    };

    private static string Label(string kind) => kind switch
    {
        "perfect-bound-cover-pdf" => "Cover",
        "case-cover-pdf" => "Case",
        "dust-jacket-pdf" => "Jacket",
        "front-cover-pdf" => "Front",
        "back-cover-pdf" => "Back",
        _ => "Cover",
    };
}
