using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Publish;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace Lorekeeper.ChapterVisuals;

public sealed class ChapterVisualService(AppDbContext db) : IChapterVisualService
{
    private const int MaxSnapshotPages = 12;
    private const string SnapshotContentType = "image/png";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public async Task<ChapterVisualState?> GetAsync(Guid chapterId, CancellationToken cancellationToken = default)
    {
        var chapter = await db.Chapters.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == chapterId, cancellationToken);
        return chapter is null ? null : State(chapter);
    }

    public async Task<ChapterVisualState> SetModeAsync(
        Guid chapterId,
        ChapterVisualModeUpdate update,
        CancellationToken cancellationToken = default)
    {
        var chapter = await GetChapterAsync(chapterId, cancellationToken);
        chapter.VisualMode = update.VisualMode;
        chapter.PageLayoutKind = NormalizePageLayoutKind(update.PageLayoutKind ?? chapter.PageLayoutKind);

        if (chapter.VisualMode == ChapterVisualMode.PicturePage)
        {
            var layout = EnsurePictureText(chapter);
            chapter.PageLayoutJson = JsonSerializer.Serialize(layout, JsonOptions);
            var projectedBody = ProjectPictureBody(layout);
            if (!string.Equals(chapter.Body, projectedBody, StringComparison.Ordinal))
            {
                chapter.Body = projectedBody;
                chapter.VectorIndexState = VectorIndexState.Stale;
                chapter.VectorIndexedAt = null;
                chapter.VectorIndexError = null;
            }
        }

        chapter.UpdatedAt = DateTime.UtcNow;
        await TouchProjectAsync(chapter.ProjectId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return State(chapter);
    }

    public async Task<ChapterImagePlacementResult> AddImageToChapterAsync(
        Guid projectId,
        Guid chapterId,
        Guid imageId,
        CancellationToken cancellationToken = default)
    {
        var chapter = await GetChapterAsync(chapterId, cancellationToken);
        if (chapter.ProjectId != projectId)
            throw new InvalidOperationException("Chapter does not belong to the project.");
        if (!await db.PublishAssets.AnyAsync(asset => asset.ProjectId == projectId && asset.Id == imageId, cancellationToken))
            throw new InvalidOperationException("Image was not found.");

        Guid elementId;
        if (chapter.VisualMode == ChapterVisualMode.PicturePage)
        {
            var layout = EnsurePictureText(chapter);
            var maxZ = layout.Images.Select(image => image.ZIndex)
                .Concat(layout.TextElements.Select(text => text.ZIndex))
                .DefaultIfEmpty(0)
                .Max();
            elementId = Guid.NewGuid();
            layout = layout with
            {
                Images = layout.Images
                    .Append(new PicturePageImageElement(
                        elementId,
                        imageId,
                        XPercent: 20,
                        YPercent: 20,
                        WidthPercent: 60,
                        HeightPercent: 45,
                        Fit: ChapterImageFit.Contain,
                        Opacity: 1,
                        ZIndex: maxZ + 1,
                        AltTextOverride: string.Empty))
                    .ToList(),
            };
            chapter.PageLayoutJson = JsonSerializer.Serialize(NormalizePageLayout(layout), JsonOptions);
        }
        else
        {
            chapter.VisualMode = ChapterVisualMode.IllustratedProse;
            var layout = ReadIllustrationLayout(chapter);
            elementId = Guid.NewGuid();
            var paragraphs = SplitParagraphs(chapter.Body);
            var paragraphIndex = Math.Max(0, paragraphs.Count - 1);
            layout = layout with
            {
                Images = layout.Images
                    .Append(new IllustratedProseImageBlock(
                        elementId,
                        imageId,
                        ChapterImageAnchorPosition.AfterParagraph,
                        paragraphIndex,
                        ParagraphHash(paragraphs.ElementAtOrDefault(paragraphIndex) ?? string.Empty),
                        WidthPercent: 70,
                        Alignment: ChapterImageAlignment.Center,
                        Caption: string.Empty,
                        AltTextOverride: string.Empty,
                        SortOrder: NextSortOrder(layout.Images),
                        StartOnNewPage: false))
                    .ToList(),
            };
            chapter.IllustrationLayoutJson = JsonSerializer.Serialize(NormalizeIllustrationLayout(layout, chapter.Body), JsonOptions);
        }

        chapter.UpdatedAt = DateTime.UtcNow;
        await TouchProjectAsync(chapter.ProjectId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return new ChapterImagePlacementResult(State(chapter), elementId);
    }

    public async Task<ChapterVisualState> SaveIllustrationLayoutAsync(
        Guid chapterId,
        IllustratedProseLayout layout,
        CancellationToken cancellationToken = default)
    {
        var chapter = await GetChapterAsync(chapterId, cancellationToken);
        chapter.VisualMode = chapter.VisualMode == ChapterVisualMode.Prose && layout.Images.Count > 0
            ? ChapterVisualMode.IllustratedProse
            : chapter.VisualMode;
        chapter.IllustrationLayoutJson = JsonSerializer.Serialize(NormalizeIllustrationLayout(layout, chapter.Body), JsonOptions);
        chapter.UpdatedAt = DateTime.UtcNow;
        await TouchProjectAsync(chapter.ProjectId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return State(chapter);
    }

    public async Task<ChapterVisualState> SavePageLayoutAsync(
        Guid chapterId,
        PicturePageLayout layout,
        CancellationToken cancellationToken = default)
    {
        var chapter = await GetChapterAsync(chapterId, cancellationToken);
        chapter.VisualMode = ChapterVisualMode.PicturePage;
        var normalized = NormalizePageLayout(layout);
        chapter.PageLayoutJson = JsonSerializer.Serialize(normalized, JsonOptions);
        var projectedBody = ProjectPictureBody(normalized);
        if (!string.Equals(chapter.Body, projectedBody, StringComparison.Ordinal))
        {
            chapter.Body = projectedBody;
            chapter.VectorIndexState = VectorIndexState.Stale;
            chapter.VectorIndexedAt = null;
            chapter.VectorIndexError = null;
        }

        chapter.UpdatedAt = DateTime.UtcNow;
        await TouchProjectAsync(chapter.ProjectId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return State(chapter);
    }

    public async Task<IReadOnlyList<ChapterVisualSnapshot>> RenderSnapshotsAsync(
        Guid chapterId,
        int maxEdge = 1400,
        CancellationToken cancellationToken = default)
    {
        var chapter = await db.Chapters.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == chapterId, cancellationToken);
        if (chapter is null)
            return [];

        var state = State(chapter);
        var imageIds = state.VisualMode == ChapterVisualMode.PicturePage
            ? state.PageLayout.Images.Select(image => image.ImageId)
            : state.IllustrationLayout.Images.Select(image => image.ImageId);
        var assets = await LoadImageAssetsAsync(chapter.ProjectId, imageIds, cancellationToken);
        if (assets.Count == 0)
            return [];

        var edge = (int)Clamp(maxEdge, 320, 2400, 1400);
        if (state.VisualMode == ChapterVisualMode.PicturePage)
            return [RenderPicturePageSnapshot(state, assets, edge)];

        var profile = await db.PublishProfiles.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.ProjectId == chapter.ProjectId, cancellationToken);
        return RenderIllustratedProseSnapshots(state, profile, assets, edge);
    }

    public async Task RemoveImageReferencesAsync(Guid projectId, Guid imageId, CancellationToken cancellationToken = default)
    {
        var chapters = await db.Chapters.Where(chapter => chapter.ProjectId == projectId).ToListAsync(cancellationToken);
        var changed = false;
        foreach (var chapter in chapters)
        {
            var chapterChanged = false;
            var illustrations = ReadIllustrationLayout(chapter);
            var filteredIllustrations = illustrations.Images.Where(image => image.ImageId != imageId).ToList();
            if (filteredIllustrations.Count != illustrations.Images.Count)
            {
                chapter.IllustrationLayoutJson = JsonSerializer.Serialize(illustrations with { Images = filteredIllustrations }, JsonOptions);
                chapterChanged = true;
            }

            var page = ReadPageLayout(chapter);
            var filteredPageImages = page.Images.Where(image => image.ImageId != imageId).ToList();
            if (filteredPageImages.Count != page.Images.Count)
            {
                chapter.PageLayoutJson = JsonSerializer.Serialize(page with { Images = filteredPageImages }, JsonOptions);
                chapterChanged = true;
            }

            if (chapterChanged)
            {
                chapter.UpdatedAt = DateTime.UtcNow;
                changed = true;
            }
        }

        if (!changed) return;
        await TouchProjectAsync(projectId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public string BuildManifest(ChapterVisualState state, IReadOnlyDictionary<Guid, string>? imageNames = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Visual mode: {state.VisualMode}");
        if (state.VisualMode == ChapterVisualMode.PicturePage)
        {
            var metrics = PageMetrics(state.PageLayoutKind);
            builder.AppendLine(
                $"Picture page layout: {state.PageLayoutKind}; physical page {metrics.PageWidthInches:0.##} x {metrics.PageHeightInches:0.##} in; spread: {metrics.IsDouble}");
            foreach (var guidanceLine in PicturePageImageGenerationGuidance.BuildManifestLines(state))
                builder.AppendLine(guidanceLine);
            foreach (var text in state.PageLayout.TextElements.OrderBy(text => text.ReadingOrder))
                builder.AppendLine($"Text box {text.ReadingOrder}: \"{text.Text}\" at {text.XPercent:0.#},{text.YPercent:0.#} size {text.WidthPercent:0.#}x{text.HeightPercent:0.#}");
            foreach (var image in state.PageLayout.Images.OrderBy(image => image.ZIndex))
            {
                var slot = PicturePageImageGenerationGuidance.ForSlot(state.PageLayoutKind, image);
                builder.AppendLine(
                    $"Image {Name(image.ImageId, imageNames)} at {image.XPercent:0.#},{image.YPercent:0.#} size {image.WidthPercent:0.#}x{image.HeightPercent:0.#}, fit {image.Fit}, z {image.ZIndex}; target aspect {slot.AspectRatio}, recommended size {slot.RecommendedSize}");
            }
            return builder.ToString();
        }

        builder.AppendLine($"Page layout: {state.PageLayoutKind}");
        foreach (var image in state.IllustrationLayout.Images.OrderBy(image => image.SortOrder))
        {
            builder.AppendLine(
                $"Illustration {Name(image.ImageId, imageNames)} {image.AnchorPosition} paragraph {image.ParagraphIndex}, width {image.WidthPercent:0.#}%, align {image.Alignment}, new page {image.StartOnNewPage}, caption \"{image.Caption}\"");
        }

        return builder.ToString();
    }

    private static string Name(Guid imageId, IReadOnlyDictionary<Guid, string>? imageNames) =>
        imageNames is not null && imageNames.TryGetValue(imageId, out var name) ? $"{name} ({imageId:N})" : imageId.ToString("N");

    private static ChapterPageLayoutKind NormalizePageLayoutKind(ChapterPageLayoutKind kind) =>
        Enum.IsDefined(kind) ? kind : ChapterPageLayoutKind.SinglePortrait;

    private static (
        double PageWidthInches,
        double PageHeightInches,
        bool IsDouble,
        double SurfaceWidthInches,
        double SurfaceHeightInches) PageMetrics(ChapterPageLayoutKind kind)
    {
        var normalized = NormalizePageLayoutKind(kind);
        var isLandscape = normalized is ChapterPageLayoutKind.SingleLandscape or ChapterPageLayoutKind.DoubleLandscape;
        var isDouble = normalized is ChapterPageLayoutKind.DoublePortrait or ChapterPageLayoutKind.DoubleLandscape;
        var pageWidth = isLandscape ? 11 : 8.5;
        var pageHeight = isLandscape ? 8.5 : 11;
        return (pageWidth, pageHeight, isDouble, isDouble ? pageWidth * 2 : pageWidth, pageHeight);
    }

    private async Task<IReadOnlyDictionary<Guid, PublishAsset>> LoadImageAssetsAsync(
        Guid projectId,
        IEnumerable<Guid> imageIds,
        CancellationToken cancellationToken)
    {
        var ids = imageIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, PublishAsset>();

        return await db.PublishAssets.AsNoTracking()
            .Where(asset => asset.ProjectId == projectId && ids.Contains(asset.Id))
            .ToDictionaryAsync(asset => asset.Id, cancellationToken);
    }

    private static ChapterVisualSnapshot RenderPicturePageSnapshot(
        ChapterVisualState state,
        IReadOnlyDictionary<Guid, PublishAsset> assets,
        int maxEdge)
    {
        var metrics = PageMetrics(state.PageLayoutKind);
        var (width, height) = ScaledPagePixels(metrics.SurfaceWidthInches, metrics.SurfaceHeightInches, maxEdge);

        using var surface = CreatePageSurface(width, height);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        foreach (var image in state.PageLayout.Images.OrderBy(image => image.ZIndex))
        {
            if (!assets.TryGetValue(image.ImageId, out var asset))
                continue;

            DrawImage(canvas, asset, PercentRect(image.XPercent, image.YPercent, image.WidthPercent, image.HeightPercent, width, height), image.Fit, image.Opacity);
        }

        foreach (var text in state.PageLayout.TextElements.OrderBy(text => text.ZIndex))
            DrawPictureTextBox(canvas, text, width, height);
        if (metrics.IsDouble)
            DrawSpreadSplit(canvas, width, height);

        return new ChapterVisualSnapshot(1, $"chapter-{state.ChapterId:N}-page-1.png", SnapshotContentType, EncodePng(surface));
    }

    private static IReadOnlyList<ChapterVisualSnapshot> RenderIllustratedProseSnapshots(
        ChapterVisualState state,
        PublishProfile? profile,
        IReadOnlyDictionary<Guid, PublishAsset> assets,
        int maxEdge)
    {
        var metrics = PageMetrics(state.PageLayoutKind);
        var pageWidthInches = metrics.PageWidthInches;
        var pageHeightInches = metrics.PageHeightInches;
        var pageMarginInches = Clamp(profile?.PageMarginInches ?? 0.75, 0.2, Math.Min(pageWidthInches, pageHeightInches) / 3, 0.75);
        var fontSizePoints = Clamp(profile?.BodyFontSizePoints ?? 12, 7, 24, 12);
        var lineHeightRatio = Clamp(profile?.BodyLineHeight ?? 1.55, 1, 2.4, 1.55);
        var (width, height) = ScaledPagePixels(pageWidthInches, pageHeightInches, maxEdge);
        var pixelsPerInch = width / pageWidthInches;
        var margin = (float)(pageMarginInches * pixelsPerInch);
        var contentWidth = Math.Max(1, width - (margin * 2));
        var contentBottom = Math.Max(margin, height - margin);
        var paragraphSpacing = (float)Math.Max(6, 0.14 * pixelsPerInch);
        var figureSpacing = (float)Math.Max(8, 0.16 * pixelsPerInch);
        var snapshots = new List<ChapterVisualSnapshot>();
        SKSurface? surface = null;
        SKCanvas? canvas = null;
        var pageNumber = 0;
        var y = margin;

        using var typeface = SKTypeface.FromFamilyName("Georgia");
        using var font = new SKFont(typeface ?? SKTypeface.Default, (float)(fontSizePoints / 72 * pixelsPerInch))
        {
            Edging = SKFontEdging.Antialias,
            Hinting = SKFontHinting.Normal,
            Subpixel = true,
        };
        using var bodyPaint = new SKPaint
        {
            Color = new SKColor(23, 32, 51),
            IsAntialias = true,
        };
        using var captionTypeface = SKTypeface.FromFamilyName("Arial");
        using var captionFont = new SKFont(captionTypeface ?? SKTypeface.Default, Math.Max(8, font.Size * 0.82f))
        {
            Edging = SKFontEdging.Antialias,
            Hinting = SKFontHinting.Normal,
            Subpixel = true,
        };
        using var captionPaint = new SKPaint
        {
            Color = new SKColor(71, 84, 103),
            IsAntialias = true,
        };

        var paragraphs = SplitParagraphs(state.Body);
        if (paragraphs.Count == 0)
            paragraphs = [string.Empty];

        StartPage();

        for (var index = 0; index < paragraphs.Count && snapshots.Count < MaxSnapshotPages; index++)
        {
            foreach (var block in BlocksAt(index, ChapterImageAnchorPosition.BeforeParagraph))
                DrawFigure(block);

            if (!string.IsNullOrWhiteSpace(paragraphs[index]))
                DrawParagraph(paragraphs[index]);

            foreach (var block in BlocksAt(index, ChapterImageAnchorPosition.AfterParagraph))
                DrawFigure(block);
        }

        FinishPage();
        return snapshots;

        IEnumerable<IllustratedProseImageBlock> BlocksAt(int paragraphIndex, ChapterImageAnchorPosition position) =>
            state.IllustrationLayout.Images
                .Where(block => block.ParagraphIndex == paragraphIndex && block.AnchorPosition == position)
                .OrderBy(block => block.SortOrder);

        void StartPage()
        {
            pageNumber++;
            surface = CreatePageSurface(width, height);
            canvas = surface.Canvas;
            canvas.Clear(SKColors.White);
            y = margin;
        }

        void FinishPage()
        {
            if (surface is null || snapshots.Count >= MaxSnapshotPages)
                return;

            snapshots.Add(new ChapterVisualSnapshot(
                pageNumber,
                $"chapter-{state.ChapterId:N}-page-{pageNumber}.png",
                SnapshotContentType,
                EncodePng(surface)));
            surface.Dispose();
            surface = null;
            canvas = null;
        }

        void NewPage()
        {
            FinishPage();
            if (snapshots.Count < MaxSnapshotPages)
                StartPage();
        }

        void EnsureSpace(float requiredHeight)
        {
            if (canvas is not null && y > margin && y + requiredHeight > contentBottom)
                NewPage();
        }

        void DrawParagraph(string paragraph)
        {
            if (canvas is null)
                return;

            var lines = WrapText(paragraph, contentWidth, font, bodyPaint);
            if (lines.Count == 0)
                return;

            var lineHeight = Math.Max(font.Size * (float)lineHeightRatio, font.Metrics.Descent - font.Metrics.Ascent + font.Metrics.Leading);
            var requiredHeight = (lines.Count * lineHeight) + paragraphSpacing;
            EnsureSpace(requiredHeight);
            if (canvas is null)
                return;

            var baseline = y - font.Metrics.Ascent;
            foreach (var line in lines)
            {
                canvas.DrawText(line, margin, baseline, SKTextAlign.Left, font, bodyPaint);
                baseline += lineHeight;
            }

            y += requiredHeight;
        }

        void DrawFigure(IllustratedProseImageBlock block)
        {
            if (!assets.TryGetValue(block.ImageId, out var asset))
                return;

            using var bitmap = DecodeBitmap(asset);
            if (bitmap is null)
                return;

            var figureWidth = contentWidth * (float)(Clamp(block.WidthPercent, 10, 100, 70) / 100);
            var imageHeight = figureWidth * bitmap.Height / Math.Max(1f, bitmap.Width);
            imageHeight = Math.Min(imageHeight, (float)(pageHeightInches * pixelsPerInch * 0.55));
            var captionLines = string.IsNullOrWhiteSpace(block.Caption)
                ? []
                : WrapText(block.Caption.Trim(), figureWidth, captionFont, captionPaint);
            var captionLineHeight = Math.Max(captionFont.Size * 1.2f, captionFont.Metrics.Descent - captionFont.Metrics.Ascent + captionFont.Metrics.Leading);
            var captionHeight = captionLines.Count == 0 ? 0 : (captionLines.Count * captionLineHeight) + (float)(0.08 * pixelsPerInch);
            var requiredHeight = imageHeight + captionHeight + figureSpacing;

            if (block.StartOnNewPage && y > margin)
                NewPage();
            EnsureSpace(requiredHeight);
            if (canvas is null)
                return;

            var left = block.Alignment switch
            {
                ChapterImageAlignment.Left => margin,
                ChapterImageAlignment.Right => margin + contentWidth - figureWidth,
                _ => margin + ((contentWidth - figureWidth) / 2),
            };
            var imageRect = new SKRect(left, y, left + figureWidth, y + imageHeight);
            DrawBitmap(canvas, bitmap, imageRect, ChapterImageFit.Contain, 1);
            y += imageHeight;

            if (captionLines.Count > 0)
            {
                y += (float)(0.08 * pixelsPerInch);
                var baseline = y - captionFont.Metrics.Ascent;
                foreach (var line in captionLines)
                {
                    canvas.DrawText(line, left + (figureWidth / 2), baseline, SKTextAlign.Center, captionFont, captionPaint);
                    baseline += captionLineHeight;
                    y += captionLineHeight;
                }
            }

            y += figureSpacing;
        }
    }

    private static SKSurface CreatePageSurface(int width, int height) =>
        SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));

    private static (int Width, int Height) ScaledPagePixels(double widthInches, double heightInches, int maxEdge)
    {
        var scale = maxEdge / Math.Max(widthInches, heightInches);
        return (
            Math.Max(1, (int)Math.Round(widthInches * scale)),
            Math.Max(1, (int)Math.Round(heightInches * scale)));
    }

    private static void DrawSpreadSplit(SKCanvas canvas, int width, int height)
    {
        using var shadowPaint = new SKPaint
        {
            Color = new SKColor(15, 23, 42, 34),
            IsAntialias = true,
            StrokeWidth = Math.Max(2, width * 0.004f),
        };
        using var linePaint = new SKPaint
        {
            Color = new SKColor(15, 23, 42, 72),
            IsAntialias = true,
            StrokeWidth = Math.Max(1, width * 0.0015f),
        };

        var center = width / 2f;
        canvas.DrawLine(center - shadowPaint.StrokeWidth, 0, center - shadowPaint.StrokeWidth, height, shadowPaint);
        canvas.DrawLine(center, 0, center, height, linePaint);
    }

    private static byte[] EncodePng(SKSurface surface)
    {
        surface.Canvas.Flush();
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, quality: 95);
        return data.ToArray();
    }

    private static SKRect PercentRect(
        double xPercent,
        double yPercent,
        double widthPercent,
        double heightPercent,
        int pageWidth,
        int pageHeight)
    {
        var x = pageWidth * (float)(Clamp(xPercent, 0, 100, 0) / 100);
        var y = pageHeight * (float)(Clamp(yPercent, 0, 100, 0) / 100);
        var width = pageWidth * (float)(Clamp(widthPercent, 1, 100, 1) / 100);
        var height = pageHeight * (float)(Clamp(heightPercent, 1, 100, 1) / 100);
        return new SKRect(x, y, Math.Min(pageWidth, x + width), Math.Min(pageHeight, y + height));
    }

    private static void DrawImage(SKCanvas canvas, PublishAsset asset, SKRect destination, ChapterImageFit fit, double opacity)
    {
        using var bitmap = DecodeBitmap(asset);
        if (bitmap is null)
            return;

        DrawBitmap(canvas, bitmap, destination, fit, opacity);
    }

    private static SKBitmap? DecodeBitmap(PublishAsset asset)
    {
        try
        {
            return SKBitmap.Decode(asset.Data);
        }
        catch
        {
            return null;
        }
    }

    private static void DrawBitmap(SKCanvas canvas, SKBitmap bitmap, SKRect destination, ChapterImageFit fit, double opacity)
    {
        if (destination.Width <= 0 || destination.Height <= 0 || bitmap.Width <= 0 || bitmap.Height <= 0)
            return;

        using var paint = new SKPaint
        {
            Color = SKColors.White.WithAlpha((byte)Math.Round(byte.MaxValue * Math.Clamp(opacity, 0, 1))),
            IsAntialias = true,
        };

        if (fit == ChapterImageFit.Fill)
        {
            canvas.DrawBitmap(bitmap, destination, paint);
            return;
        }

        if (fit == ChapterImageFit.Cover)
        {
            canvas.DrawBitmap(bitmap, CoverSourceRect(bitmap, destination.Width / destination.Height), destination, paint);
            return;
        }

        canvas.DrawBitmap(bitmap, ContainDestinationRect(destination, bitmap.Width / Math.Max(1f, bitmap.Height)), paint);
    }

    private static SKRect ContainDestinationRect(SKRect outer, float sourceRatio)
    {
        var destinationRatio = outer.Width / Math.Max(1f, outer.Height);
        if (destinationRatio > sourceRatio)
        {
            var width = outer.Height * sourceRatio;
            var left = outer.Left + ((outer.Width - width) / 2);
            return new SKRect(left, outer.Top, left + width, outer.Bottom);
        }

        var height = outer.Width / Math.Max(0.01f, sourceRatio);
        var top = outer.Top + ((outer.Height - height) / 2);
        return new SKRect(outer.Left, top, outer.Right, top + height);
    }

    private static SKRect CoverSourceRect(SKBitmap bitmap, float destinationRatio)
    {
        var sourceRatio = bitmap.Width / Math.Max(1f, bitmap.Height);
        if (sourceRatio > destinationRatio)
        {
            var width = bitmap.Height * destinationRatio;
            var left = (bitmap.Width - width) / 2;
            return new SKRect(left, 0, left + width, bitmap.Height);
        }

        var height = bitmap.Width / Math.Max(0.01f, destinationRatio);
        var top = (bitmap.Height - height) / 2;
        return new SKRect(0, top, bitmap.Width, top + height);
    }

    private static void DrawPictureTextBox(SKCanvas canvas, PicturePageTextElement text, int pageWidth, int pageHeight)
    {
        var rect = PercentRect(text.XPercent, text.YPercent, text.WidthPercent, text.HeightPercent, pageWidth, pageHeight);
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        if (text.BackgroundOpacity > 0)
        {
            using var backgroundPaint = new SKPaint
            {
                Color = TextColor(text.BackgroundColor, text.BackgroundOpacity),
                IsAntialias = true,
            };
            canvas.DrawRect(rect, backgroundPaint);
        }

        var fontSize = Math.Max(6, pageWidth * (float)(Clamp(text.FontSizePercent, 1, 18, 4.5) / 100));
        using var typeface = SKTypeface.FromFamilyName(FontFamily(text.FontFamily));
        using var font = new SKFont(typeface ?? SKTypeface.Default, fontSize)
        {
            Edging = SKFontEdging.Antialias,
            Hinting = SKFontHinting.Normal,
            Subpixel = true,
        };
        using var paint = new SKPaint
        {
            Color = TextColor(text.Color, 1),
            IsAntialias = true,
        };

        var padding = Math.Max(4, fontSize * 0.18f);
        var textRect = new SKRect(rect.Left + padding, rect.Top + padding, rect.Right - padding, rect.Bottom - padding);
        if (textRect.Width <= 0 || textRect.Height <= 0)
            return;

        var lines = WrapText(text.Text, textRect.Width, font, paint);
        if (lines.Count == 0)
            return;

        var lineHeight = Math.Max(fontSize * (float)Clamp(text.LineHeight, 0.9, 2.2, 1.25), font.Metrics.Descent - font.Metrics.Ascent + font.Metrics.Leading);
        var blockHeight = lines.Count * lineHeight;
        var startY = text.VerticalAlign switch
        {
            ChapterTextVerticalAlign.Top => textRect.Top,
            ChapterTextVerticalAlign.Bottom => textRect.Bottom - blockHeight,
            _ => textRect.Top + ((textRect.Height - blockHeight) / 2),
        };
        var baseline = startY - font.Metrics.Ascent;
        var align = TextAlign(text.TextAlign);
        var x = text.TextAlign switch
        {
            PublishCoverTextAlign.Left => textRect.Left,
            PublishCoverTextAlign.Right => textRect.Right,
            _ => textRect.Left + (textRect.Width / 2),
        };

        foreach (var line in lines)
        {
            if (baseline - fontSize > textRect.Bottom)
                break;
            canvas.DrawText(line, x, baseline, align, font, paint);
            baseline += lineHeight;
        }
    }

    private static IReadOnlyList<string> WrapText(string text, float maxWidth, SKFont font, SKPaint paint)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(paragraph))
                continue;

            var current = string.Empty;
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (current.Length == 0)
                {
                    AddWord(word);
                    continue;
                }

                var candidate = current + " " + word;
                if (Measure(candidate, font, paint) <= maxWidth)
                {
                    current = candidate;
                    continue;
                }

                lines.Add(current);
                current = string.Empty;
                AddWord(word);
            }

            if (current.Length > 0)
                lines.Add(current);

            void AddWord(string word)
            {
                if (Measure(word, font, paint) <= maxWidth)
                {
                    current = word;
                    return;
                }

                foreach (var segment in BreakLongWord(word, maxWidth, font, paint))
                {
                    if (current.Length == 0)
                    {
                        current = segment;
                        continue;
                    }

                    lines.Add(current);
                    current = segment;
                }
            }
        }

        return lines;
    }

    private static IEnumerable<string> BreakLongWord(string word, float maxWidth, SKFont font, SKPaint paint)
    {
        var segment = string.Empty;
        foreach (var rune in word.EnumerateRunes())
        {
            var candidate = segment + rune;
            if (segment.Length > 0 && Measure(candidate, font, paint) > maxWidth)
            {
                yield return segment;
                segment = rune.ToString();
                continue;
            }

            segment = candidate;
        }

        if (segment.Length > 0)
            yield return segment;
    }

    private static float Measure(string text, SKFont font, SKPaint paint) =>
        font.MeasureText(text, paint);

    private static string FontFamily(PublishCoverFontFamily fontFamily) =>
        fontFamily switch
        {
            PublishCoverFontFamily.Sans => "Arial",
            PublishCoverFontFamily.Display => "Trebuchet MS",
            PublishCoverFontFamily.Monospace => "Consolas",
            _ => "Georgia",
        };

    private static SKTextAlign TextAlign(PublishCoverTextAlign textAlign) =>
        textAlign switch
        {
            PublishCoverTextAlign.Left => SKTextAlign.Left,
            PublishCoverTextAlign.Right => SKTextAlign.Right,
            _ => SKTextAlign.Center,
        };

    private static SKColor TextColor(string color, double opacity)
    {
        var hex = color.TrimStart('#');
        if (hex.Length != 6
            || !byte.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var red)
            || !byte.TryParse(hex[2..4], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var green)
            || !byte.TryParse(hex[4..6], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var blue))
        {
            red = green = blue = byte.MaxValue;
        }

        return new SKColor(red, green, blue, (byte)Math.Round(byte.MaxValue * Math.Clamp(opacity, 0, 1)));
    }

    private async Task<Chapter> GetChapterAsync(Guid chapterId, CancellationToken cancellationToken) =>
        await db.Chapters.FirstOrDefaultAsync(chapter => chapter.Id == chapterId, cancellationToken)
        ?? throw new InvalidOperationException("Chapter was not found.");

    private async Task TouchProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await db.Projects.FirstOrDefaultAsync(project => project.Id == projectId, cancellationToken);
        if (project is not null)
            project.UpdatedAt = DateTime.UtcNow;
    }

    private static ChapterVisualState State(Chapter chapter) =>
        new(
            chapter.Id,
            chapter.VisualMode,
            NormalizePageLayoutKind(chapter.PageLayoutKind),
            NormalizeIllustrationLayout(ReadIllustrationLayout(chapter), chapter.Body),
            NormalizePageLayout(ReadPageLayout(chapter)),
            chapter.Body);

    private static IllustratedProseLayout ReadIllustrationLayout(Chapter chapter)
    {
        if (string.IsNullOrWhiteSpace(chapter.IllustrationLayoutJson))
            return new IllustratedProseLayout([]);

        try
        {
            return JsonSerializer.Deserialize<IllustratedProseLayout>(chapter.IllustrationLayoutJson, JsonOptions)
                ?? new IllustratedProseLayout([]);
        }
        catch (JsonException)
        {
            return new IllustratedProseLayout([]);
        }
    }

    private static PicturePageLayout ReadPageLayout(Chapter chapter)
    {
        if (string.IsNullOrWhiteSpace(chapter.PageLayoutJson))
            return new PicturePageLayout([], []);

        try
        {
            return JsonSerializer.Deserialize<PicturePageLayout>(chapter.PageLayoutJson, JsonOptions)
                ?? new PicturePageLayout([], []);
        }
        catch (JsonException)
        {
            return new PicturePageLayout([], []);
        }
    }

    private static PicturePageLayout EnsurePictureText(Chapter chapter)
    {
        var layout = NormalizePageLayout(ReadPageLayout(chapter));
        if (layout.TextElements.Count > 0 || string.IsNullOrWhiteSpace(chapter.Body))
            return layout;

        return layout with
        {
            TextElements =
            [
                new PicturePageTextElement(
                    Guid.NewGuid(),
                    chapter.Body,
                    XPercent: 12,
                    YPercent: 68,
                    WidthPercent: 76,
                    HeightPercent: 20,
                    ZIndex: 10,
                    ReadingOrder: 0,
                    FontFamily: PublishCoverFontFamily.Serif,
                    FontSizePercent: 4.5,
                    LineHeight: 1.25,
                    Color: "#111827",
                    BackgroundColor: "#FFFFFF",
                    BackgroundOpacity: 0,
                    TextAlign: PublishCoverTextAlign.Center,
                    VerticalAlign: ChapterTextVerticalAlign.Middle,
                    Shadow: PublishCoverShadow.None),
            ],
        };
    }

    private static IllustratedProseLayout NormalizeIllustrationLayout(IllustratedProseLayout? layout, string body)
    {
        var paragraphs = SplitParagraphs(body);
        var maxParagraphIndex = Math.Max(0, paragraphs.Count - 1);
        var images = (layout?.Images ?? [])
            .Where(image => image.ImageId != Guid.Empty)
            .Select((image, index) => image with
            {
                Id = image.Id == Guid.Empty ? Guid.NewGuid() : image.Id,
                ParagraphIndex = Math.Clamp(image.ParagraphIndex, 0, maxParagraphIndex),
                ParagraphHash = string.IsNullOrWhiteSpace(image.ParagraphHash)
                    ? ParagraphHash(paragraphs.ElementAtOrDefault(Math.Clamp(image.ParagraphIndex, 0, maxParagraphIndex)) ?? string.Empty)
                    : image.ParagraphHash,
                WidthPercent = Clamp(image.WidthPercent, 10, 100, 70),
                Alignment = Enum.IsDefined(image.Alignment) ? image.Alignment : ChapterImageAlignment.Center,
                AnchorPosition = Enum.IsDefined(image.AnchorPosition) ? image.AnchorPosition : ChapterImageAnchorPosition.AfterParagraph,
                Caption = image.Caption.Trim(),
                AltTextOverride = image.AltTextOverride.Trim(),
                SortOrder = image.SortOrder < 0 ? index : image.SortOrder,
            })
            .OrderBy(image => image.SortOrder)
            .ToList();

        return new IllustratedProseLayout(images);
    }

    private static PicturePageLayout NormalizePageLayout(PicturePageLayout? layout)
    {
        var images = (layout?.Images ?? [])
            .Where(image => image.ImageId != Guid.Empty)
            .Select((image, index) => image with
            {
                Id = image.Id == Guid.Empty ? Guid.NewGuid() : image.Id,
                XPercent = Clamp(image.XPercent, 0, 100, 20),
                YPercent = Clamp(image.YPercent, 0, 100, 20),
                WidthPercent = Clamp(image.WidthPercent, 1, 100, 60),
                HeightPercent = Clamp(image.HeightPercent, 1, 100, 45),
                Fit = Enum.IsDefined(image.Fit) ? image.Fit : ChapterImageFit.Contain,
                Opacity = Clamp(image.Opacity, 0, 1, 1),
                ZIndex = image.ZIndex == 0 ? index + 1 : image.ZIndex,
                AltTextOverride = image.AltTextOverride.Trim(),
            })
            .ToList();

        var texts = (layout?.TextElements ?? [])
            .Where(text => !string.IsNullOrWhiteSpace(text.Text))
            .Select((text, index) => text with
            {
                Id = text.Id == Guid.Empty ? Guid.NewGuid() : text.Id,
                Text = text.Text.Trim(),
                XPercent = Clamp(text.XPercent, 0, 100, 12),
                YPercent = Clamp(text.YPercent, 0, 100, 68),
                WidthPercent = Clamp(text.WidthPercent, 1, 100, 76),
                HeightPercent = Clamp(text.HeightPercent, 1, 100, 20),
                ZIndex = text.ZIndex == 0 ? 100 + index : text.ZIndex,
                ReadingOrder = text.ReadingOrder < 0 ? index : text.ReadingOrder,
                FontFamily = Enum.IsDefined(text.FontFamily) ? text.FontFamily : PublishCoverFontFamily.Serif,
                FontSizePercent = Clamp(text.FontSizePercent, 1, 18, 4.5),
                LineHeight = Clamp(text.LineHeight, 0.9, 2.2, 1.25),
                Color = CleanColor(text.Color, "#111827"),
                BackgroundColor = CleanColor(text.BackgroundColor, "#FFFFFF"),
                BackgroundOpacity = Clamp(text.BackgroundOpacity, 0, 1, 0),
                TextAlign = Enum.IsDefined(text.TextAlign) ? text.TextAlign : PublishCoverTextAlign.Center,
                VerticalAlign = Enum.IsDefined(text.VerticalAlign) ? text.VerticalAlign : ChapterTextVerticalAlign.Middle,
                Shadow = Enum.IsDefined(text.Shadow) ? text.Shadow : PublishCoverShadow.None,
            })
            .OrderBy(text => text.ReadingOrder)
            .ToList();

        return new PicturePageLayout(images, texts);
    }

    private static string ProjectPictureBody(PicturePageLayout layout) =>
        string.Join(
            Environment.NewLine + Environment.NewLine,
            layout.TextElements
                .OrderBy(text => text.ReadingOrder)
                .Select(text => text.Text.Trim())
                .Where(text => !string.IsNullOrWhiteSpace(text)));

    private static IReadOnlyList<string> SplitParagraphs(string body)
    {
        var paragraphs = new List<string>();
        var current = new StringBuilder();
        foreach (var line in body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                Flush();
                continue;
            }

            if (current.Length > 0)
                current.AppendLine();
            current.Append(line.TrimEnd());
        }

        Flush();
        return paragraphs;

        void Flush()
        {
            if (current.Length == 0) return;
            paragraphs.Add(current.ToString());
            current.Clear();
        }
    }

    private static string ParagraphHash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text.Trim()));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private static int NextSortOrder(IReadOnlyList<IllustratedProseImageBlock> images) =>
        images.Count == 0 ? 0 : images.Max(image => image.SortOrder) + 1;

    private static double Clamp(double value, double min, double max, double fallback) =>
        double.IsNaN(value) || double.IsInfinity(value)
            ? fallback
            : Math.Min(max, Math.Max(min, value));

    private static string CleanColor(string? value, string fallback)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 7
            && trimmed[0] == '#'
            && trimmed.Skip(1).All(Uri.IsHexDigit))
        {
            return trimmed.ToUpperInvariant();
        }

        return fallback;
    }
}
