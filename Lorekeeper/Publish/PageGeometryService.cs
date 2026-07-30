using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Publish;

public sealed record BookPageGeometry(
    ChapterPageLayoutKind LayoutKind,
    double PageWidthInches,
    double PageHeightInches,
    double PageMarginInches,
    double BodyFontSizePoints,
    double BodyLineHeight,
    bool IsDouble,
    double SurfaceWidthInches,
    double SurfaceHeightInches,
    int RecommendedRasterWidthPixels,
    int RecommendedRasterHeightPixels)
{
    public string RecommendedRasterSize => $"{RecommendedRasterWidthPixels}x{RecommendedRasterHeightPixels}";
    public double SurfaceAspectRatio => SurfaceWidthInches / SurfaceHeightInches;
}

public interface IPageGeometryService
{
    Task<BookPageGeometry> GetAsync(
        Guid projectId,
        ChapterPageLayoutKind layoutKind,
        CancellationToken cancellationToken = default);

    BookPageGeometry Calculate(PublicationEdition? profile, ChapterPageLayoutKind layoutKind);

    BookPageGeometry Calculate(
        double pageWidthInches,
        double pageHeightInches,
        double pageMarginInches,
        double bodyFontSizePoints,
        double bodyLineHeight,
        ChapterPageLayoutKind layoutKind);
}

/// <summary>Single calculation used by screen previews, raster rendering, image briefs, and publishing.</summary>
public sealed class PageGeometryService(AppDbContext db) : IPageGeometryService
{
    private const double RasterPixelsPerInch = 160d;
    private const long MinimumRasterPixels = 655_360;
    private const long MaximumRasterPixels = 8_294_400;
    private const int MaximumRasterEdge = 3840;
    private const int RasterEdgeMultiple = 16;

    public async Task<BookPageGeometry> GetAsync(
        Guid projectId,
        ChapterPageLayoutKind layoutKind,
        CancellationToken cancellationToken = default)
    {
        var profile = await db.PublicationEditions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.ProjectId == projectId && candidate.IsDefault,
                cancellationToken);
        return Calculate(profile, layoutKind);
    }

    public BookPageGeometry Calculate(PublicationEdition? profile, ChapterPageLayoutKind layoutKind)
        => CalculateValues(
            profile?.PageWidthInches,
            profile?.PageHeightInches,
            profile?.PageMarginInches,
            profile?.BodyFontSizePoints,
            profile?.BodyLineHeight,
            layoutKind);

    public BookPageGeometry Calculate(
        double pageWidthInches,
        double pageHeightInches,
        double pageMarginInches,
        double bodyFontSizePoints,
        double bodyLineHeight,
        ChapterPageLayoutKind layoutKind)
        => CalculateValues(
            pageWidthInches,
            pageHeightInches,
            pageMarginInches,
            bodyFontSizePoints,
            bodyLineHeight,
            layoutKind);

    public static BookPageGeometry CalculateValues(
        double? pageWidthInches,
        double? pageHeightInches,
        double? pageMarginInches,
        double? bodyFontSizePoints,
        double? bodyLineHeight,
        ChapterPageLayoutKind layoutKind)
    {
        var normalized = Enum.IsDefined(layoutKind) ? layoutKind : ChapterPageLayoutKind.SinglePortrait;
        var baseWidth = Clamp(pageWidthInches ?? 8.5, 3, 24, 8.5);
        var baseHeight = Clamp(pageHeightInches ?? 11, 3, 24, 11);
        var isLandscape = normalized is ChapterPageLayoutKind.SingleLandscape or ChapterPageLayoutKind.DoubleLandscape;
        var isDouble = normalized is ChapterPageLayoutKind.DoublePortrait or ChapterPageLayoutKind.DoubleLandscape;
        var pageWidth = isLandscape ? Math.Max(baseWidth, baseHeight) : Math.Min(baseWidth, baseHeight);
        var pageHeight = isLandscape ? Math.Min(baseWidth, baseHeight) : Math.Max(baseWidth, baseHeight);
        var surfaceWidth = pageWidth * (isDouble ? 2 : 1);
        var surfaceHeight = pageHeight;
        var margin = Clamp(pageMarginInches ?? 0.75, 0.125, Math.Min(pageWidth, pageHeight) / 3, 0.75);
        var fontSize = Clamp(bodyFontSizePoints ?? 12, 7, 72, 12);
        var lineHeight = Clamp(bodyLineHeight ?? 1.55, 1, 2.4, 1.55);
        var (rasterWidth, rasterHeight) = RecommendedRasterSize(
            surfaceWidth * RasterPixelsPerInch,
            surfaceHeight * RasterPixelsPerInch);

        return new(
            normalized,
            pageWidth,
            pageHeight,
            margin,
            fontSize,
            lineHeight,
            isDouble,
            surfaceWidth,
            surfaceHeight,
            rasterWidth,
            rasterHeight);
    }

    private static (int Width, int Height) RecommendedRasterSize(double requestedWidth, double requestedHeight)
    {
        var scale = Math.Min(1d, MaximumRasterEdge / Math.Max(requestedWidth, requestedHeight));
        var scaledWidth = requestedWidth * scale;
        var scaledHeight = requestedHeight * scale;
        var area = scaledWidth * scaledHeight;
        if (area < MinimumRasterPixels)
        {
            var upScale = Math.Sqrt(MinimumRasterPixels / Math.Max(1, area)) * 1.01;
            scaledWidth *= upScale;
            scaledHeight *= upScale;
        }
        else if (area > MaximumRasterPixels)
        {
            var downScale = Math.Sqrt(MaximumRasterPixels / area);
            scaledWidth *= downScale;
            scaledHeight *= downScale;
        }

        var width = ValidRasterEdge(scaledWidth);
        var height = ValidRasterEdge(scaledHeight);
        while ((long)width * height < MinimumRasterPixels
               && Math.Max(width, height) < MaximumRasterEdge)
        {
            if (width <= height) width += RasterEdgeMultiple;
            else height += RasterEdgeMultiple;
        }
        while ((long)width * height > MaximumRasterPixels)
        {
            if (width >= height) width -= RasterEdgeMultiple;
            else height -= RasterEdgeMultiple;
        }
        return (width, height);
    }

    private static int ValidRasterEdge(double value) =>
        Math.Clamp(
            (int)Math.Round(value / RasterEdgeMultiple) * RasterEdgeMultiple,
            RasterEdgeMultiple,
            MaximumRasterEdge);

    private static double Clamp(double value, double minimum, double maximum, double fallback) =>
        double.IsNaN(value) || double.IsInfinity(value)
            ? fallback
            : Math.Clamp(value, minimum, maximum);
}
