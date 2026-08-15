using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lorekeeper.Fonts;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Publish;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Lorekeeper.Composition;

public enum CompositionCanvasPreviewMode
{
    Annotated,
    Clean,
}

public sealed record CompositionCanvasPreviewDiagnostic(
    string Severity,
    string Code,
    string Message,
    Guid? ObjectId = null);

public sealed record CompositionCanvasPreviewResult(
    Guid CompositionId,
    Guid VariantId,
    long CompositionRevision,
    long VariantRevision,
    CompositionCanvasPreviewMode Mode,
    double SurfaceWidthPoints,
    double SurfaceHeightPoints,
    int PixelWidth,
    int PixelHeight,
    int VisibleObjectCount,
    int HiddenObjectCount,
    byte[] Data,
    IReadOnlyList<CompositionCanvasPreviewDiagnostic> Diagnostics);

public interface ICompositionCanvasPreviewService
{
    Task<CompositionCanvasPreviewResult> RenderAsync(
        Guid projectId,
        Guid compositionId,
        Guid variantId,
        CompositionCanvasPreviewMode mode,
        CancellationToken cancellationToken = default);

    Task<CompositionCanvasPreviewResult> RenderSceneAsync(
        Guid projectId,
        Guid targetId,
        long revision,
        CompositionScene scene,
        CompositionCanvasPreviewMode mode,
        CancellationToken cancellationToken = default);

    Task<CompositionCanvasPreviewResult> RenderSceneAsync(
        Guid projectId,
        Guid targetId,
        long revision,
        CompositionScene scene,
        ManuscriptDocument semantic,
        CompositionCanvasPreviewMode mode,
        CancellationToken cancellationToken = default);
}

public sealed partial class CompositionCanvasPreviewService(
    IAppDatabaseOperationFactory database,
    IProjectFontService projectFonts,
    IOptions<PublicationPressOptions> options) : ICompositionCanvasPreviewService
{
    private const int MaximumCachedPreviews = 64;
    private static readonly ConcurrentDictionary<string, CompositionCanvasPreviewResult> Cache = new(StringComparer.Ordinal);
    private static readonly ConcurrentQueue<string> CacheOrder = new();

    public async Task<CompositionCanvasPreviewResult> RenderAsync(
        Guid projectId,
        Guid compositionId,
        Guid variantId,
        CompositionCanvasPreviewMode mode,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var variant = await db.PageCompositionVariants
            .AsNoTracking()
            .Include(item => item.Composition)
            .SingleOrDefaultAsync(item => item.Id == variantId
                && item.CompositionId == compositionId
                && item.DetachedAt == null
                && item.Composition.ProjectId == projectId
                && item.Composition.DetachedAt == null,
                cancellationToken)
            ?? throw new KeyNotFoundException("Composition variant was not found in this project.");
        var scene = JsonSerializer.Deserialize<CompositionScene>(variant.SceneJson, ManuscriptCodec.JsonOptions)
            ?? throw new InvalidDataException("The composition scene is empty.");
        var semantic = ManuscriptCodec.Deserialize(
            variant.Composition.SemanticManuscriptJson,
            variant.Composition.Id,
            variant.Composition.Revision);
        var imageIds = CompositionSceneResolver.Flatten(scene)
            .Where(item => item.Kind == CompositionObjectKind.Image && item.ImageId is not null)
            .Select(item => item.ImageId!.Value)
            .Distinct()
            .ToList();
        var assets = imageIds.Count == 0
            ? []
            : await db.PublishAssets.AsNoTracking()
                .Where(item => item.ProjectId == projectId && imageIds.Contains(item.Id))
                .ToListAsync(cancellationToken);
        var fontFaces = await db.ProjectFontFaces.AsNoTracking()
            .Where(item => item.Family.ProjectId == projectId)
            .Select(item => new { item.Id, item.FamilyId, item.Weight, item.Italic, item.Data })
            .ToListAsync(cancellationToken);
        var cacheKey = CacheKey(variant, mode, assets, fontFaces.Select(item => (item.Id, item.FamilyId, item.Weight, item.Italic, item.Data)));
        if (Cache.TryGetValue(cacheKey, out var cached))
            return cached;

        var result = await RasterizeAsync(projectId, variant, scene, semantic, assets, mode, cancellationToken);
        Cache[cacheKey] = result;
        CacheOrder.Enqueue(cacheKey);
        while (Cache.Count > MaximumCachedPreviews && CacheOrder.TryDequeue(out var expired))
            Cache.TryRemove(expired, out _);
        return result;
    }

    public async Task<CompositionCanvasPreviewResult> RenderSceneAsync(
        Guid projectId,
        Guid targetId,
        long revision,
        CompositionScene scene,
        CompositionCanvasPreviewMode mode,
        CancellationToken cancellationToken = default) =>
        await RenderSceneAsync(
            projectId,
            targetId,
            revision,
            scene,
            ManuscriptCodec.CreateEmpty(targetId),
            mode,
            cancellationToken);

    public async Task<CompositionCanvasPreviewResult> RenderSceneAsync(
        Guid projectId,
        Guid targetId,
        long revision,
        CompositionScene scene,
        ManuscriptDocument semantic,
        CompositionCanvasPreviewMode mode,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(semantic);
        var imageIds = CompositionSceneResolver.Flatten(scene)
            .Where(item => item.Kind == CompositionObjectKind.Image && item.ImageId is not null)
            .Select(item => item.ImageId!.Value)
            .Distinct()
            .ToList();
        var assets = imageIds.Count == 0
            ? []
            : await db.PublishAssets.AsNoTracking()
                .Where(item => item.ProjectId == projectId && imageIds.Contains(item.Id))
                .ToListAsync(cancellationToken);
        var fontFaces = await db.ProjectFontFaces.AsNoTracking()
            .Where(item => item.Family.ProjectId == projectId)
            .Select(item => new { item.Id, item.FamilyId, item.Weight, item.Italic, item.Data })
            .ToListAsync(cancellationToken);
        var sceneJson = JsonSerializer.Serialize(scene, ManuscriptCodec.JsonOptions);
        var syntheticVariant = new PageCompositionVariant
        {
            Id = targetId,
            CompositionId = targetId,
            Revision = revision,
            SceneJson = sceneJson,
            Composition = new PageComposition
            {
                Id = targetId,
                ProjectId = projectId,
                Revision = revision,
                SemanticManuscriptJson = ManuscriptCodec.Serialize(semantic),
            },
        };
        var cacheKey = CacheKey(
            syntheticVariant,
            mode,
            assets,
            fontFaces.Select(item => (item.Id, item.FamilyId, item.Weight, item.Italic, item.Data)));
        if (Cache.TryGetValue(cacheKey, out var cached))
            return cached;

        var result = await RasterizeAsync(projectId, syntheticVariant, scene, semantic, assets, mode, cancellationToken);
        Cache[cacheKey] = result;
        CacheOrder.Enqueue(cacheKey);
        while (Cache.Count > MaximumCachedPreviews && CacheOrder.TryDequeue(out var expired))
            Cache.TryRemove(expired, out _);
        return result;
    }

    private async Task<CompositionCanvasPreviewResult> RasterizeAsync(
        Guid projectId,
        PageCompositionVariant variant,
        CompositionScene scene,
        ManuscriptDocument semantic,
        IReadOnlyList<PublishAsset> assets,
        CompositionCanvasPreviewMode mode,
        CancellationToken cancellationToken)
    {
        var surfaceWidth = Math.Max(1, scene.Surface.WidthPoints);
        var surfaceHeight = Math.Max(1, scene.Surface.HeightPoints);
        var maxEdge = Math.Clamp(options.Value.PreviewImageMaxEdge, 640, 3200);
        var scale = Math.Min(maxEdge / surfaceWidth, maxEdge / surfaceHeight);
        var pixelWidth = Math.Max(1, (int)Math.Round(surfaceWidth * scale));
        var pixelHeight = Math.Max(1, (int)Math.Round(surfaceHeight * scale));
        var visibleLayerIds = scene.Layers.Where(layer => layer.Visible).Select(layer => layer.Id).ToHashSet();
        var layerOrder = scene.Layers.ToDictionary(layer => layer.Id, layer => layer.Order);
        var flattened = CompositionSceneResolver.Flatten(scene);
        var visibleObjects = flattened
            .Where(item => item.Visible && visibleLayerIds.Contains(item.LayerId))
            .OrderBy(item => layerOrder.GetValueOrDefault(item.LayerId))
            .ThenBy(item => item.ZIndex)
            .ThenBy(item => item.Id)
            .ToList();
        var hiddenCount = flattened.Count - visibleObjects.Count;
        var styles = scene.Styles.ToDictionary(item => item.Id);
        var resolvedObjects = visibleObjects.Select(item => ResolveStyle(item, styles)).ToList();
        var diagnostics = new List<CompositionCanvasPreviewDiagnostic>();
        var assetsById = assets.ToDictionary(item => item.Id);
        var bitmaps = new Dictionary<Guid, SKBitmap>();
        var typefaces = await LoadTypefacesAsync(projectId, resolvedObjects, semantic, cancellationToken);
        try
        {
            foreach (var item in resolvedObjects.Where(item => item.Kind == CompositionObjectKind.Image && item.ImageId is not null))
            {
                if (!assetsById.TryGetValue(item.ImageId!.Value, out var asset))
                {
                    diagnostics.Add(new("error", "IMAGE_MISSING", "The scene image is not available in this project.", item.Id));
                    continue;
                }
                var bitmap = SKBitmap.Decode(asset.Data);
                if (bitmap is null)
                {
                    diagnostics.Add(new("error", "IMAGE_UNREADABLE", "The scene image could not be decoded.", item.Id));
                    continue;
                }
                bitmaps[item.ImageId.Value] = bitmap;
            }

            using var raster = SKSurface.Create(new SKImageInfo(pixelWidth, pixelHeight, SKColorType.Rgba8888, SKAlphaType.Premul))
                ?? throw new InvalidOperationException("The composition preview canvas could not be created.");
            var canvas = raster.Canvas;
            canvas.Clear(SKColors.White);
            canvas.Scale((float)(pixelWidth / surfaceWidth), (float)(pixelHeight / surfaceHeight));
            canvas.ClipRect(new SKRect(0, 0, (float)surfaceWidth, (float)surfaceHeight));
            foreach (var item in resolvedObjects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (OutsideSurface(item.Bounds))
                    diagnostics.Add(new("warning", "OBJECT_CLIPPED", "Part of this object extends beyond the page and is clipped in output.", item.Id));
                DrawObject(canvas, scene, semantic, item, bitmaps, typefaces, diagnostics);
            }
            if (mode == CompositionCanvasPreviewMode.Annotated)
                DrawAnnotations(canvas, scene, resolvedObjects, diagnostics);

            using var image = raster.Snapshot();
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new InvalidOperationException("The composition preview could not be encoded as PNG.");
            return new CompositionCanvasPreviewResult(
                variant.CompositionId,
                variant.Id,
                variant.Composition.Revision,
                variant.Revision,
                mode,
                surfaceWidth,
                surfaceHeight,
                pixelWidth,
                pixelHeight,
                visibleObjects.Count,
                hiddenCount,
                encoded.ToArray(),
                diagnostics
                    .OrderBy(item => item.Severity == "error" ? 0 : 1)
                    .ThenBy(item => item.Code, StringComparer.Ordinal)
                    .ThenBy(item => item.ObjectId)
                    .ToList());
        }
        finally
        {
            foreach (var bitmap in bitmaps.Values)
                bitmap.Dispose();
            foreach (var typeface in typefaces.Values)
                typeface.Dispose();
        }
    }

    private async Task<Dictionary<FontKey, SKTypeface>> LoadTypefacesAsync(
        Guid projectId,
        IReadOnlyList<CompositionObject> objects,
        ManuscriptDocument semantic,
        CancellationToken cancellationToken)
    {
        var keys = new HashSet<FontKey>();
        foreach (var item in objects.Where(item => item.Kind == CompositionObjectKind.Text))
        {
            keys.Add(new(item.FontFamilyKey, item.FontWeight, item.Italic));
            foreach (var inline in ResolveInlines(semantic, item))
            {
                keys.Add(FontForInline(item, inline.Marks));
            }
        }

        var result = new Dictionary<FontKey, SKTypeface>();
        try
        {
            foreach (var key in keys)
            {
                var face = await projectFonts.ResolveFaceAsync(
                    projectId,
                    key.FamilyKey,
                    key.Weight,
                    key.Italic,
                    requireExact: false,
                    cancellationToken)
                    ?? throw new InvalidDataException($"The composition font '{key.FamilyKey}' could not be resolved.");
                using var data = SKData.CreateCopy(face.Data);
                result[key] = SKTypeface.FromData(data)
                    ?? throw new InvalidDataException($"The composition font '{key.FamilyKey}' could not be decoded.");
            }
            return result;
        }
        catch
        {
            foreach (var typeface in result.Values)
                typeface.Dispose();
            throw;
        }
    }

    private static void DrawObject(
        SKCanvas canvas,
        CompositionScene scene,
        ManuscriptDocument semantic,
        CompositionObject item,
        IReadOnlyDictionary<Guid, SKBitmap> bitmaps,
        IReadOnlyDictionary<FontKey, SKTypeface> typefaces,
        List<CompositionCanvasPreviewDiagnostic> diagnostics)
    {
        var rect = Rect(scene.Surface, item.Bounds);
        canvas.Save();
        canvas.RotateDegrees((float)item.RotationDegrees, rect.MidX, rect.MidY);
        switch (item.Kind)
        {
            case CompositionObjectKind.Image when item.ImageId is Guid imageId && bitmaps.TryGetValue(imageId, out var bitmap):
                DrawImage(canvas, rect, item, bitmap);
                break;
            case CompositionObjectKind.Text:
                DrawText(canvas, rect, semantic, item, typefaces, diagnostics);
                break;
            case CompositionObjectKind.Rectangle:
            case CompositionObjectKind.Ellipse:
            case CompositionObjectKind.Line:
                DrawShape(canvas, rect, item);
                break;
        }
        canvas.Restore();
    }

    private static void DrawImage(SKCanvas canvas, SKRect frame, CompositionObject item, SKBitmap bitmap)
    {
        canvas.Save();
        canvas.ClipRect(frame);
        using var paint = new SKPaint
        {
            Color = SKColors.White.WithAlpha(Alpha(item.Opacity)),
            IsAntialias = true,
        };
        if (item.ImageFit == FigureImageFit.Stretch)
        {
            canvas.DrawBitmap(bitmap, frame, paint);
            canvas.Restore();
            return;
        }

        var scale = item.ImageFit == FigureImageFit.Cover
            ? Math.Max(frame.Width / bitmap.Width, frame.Height / bitmap.Height)
            : Math.Min(frame.Width / bitmap.Width, frame.Height / bitmap.Height);
        var width = bitmap.Width * scale;
        var height = bitmap.Height * scale;
        var positionX = item.ImageFit == FigureImageFit.Cover ? Math.Clamp(item.CropXPercent / 100, 0, 1) : .5;
        var positionY = item.ImageFit == FigureImageFit.Cover ? Math.Clamp(item.CropYPercent / 100, 0, 1) : .5;
        var left = frame.Left + (frame.Width - width) * positionX;
        var top = frame.Top + (frame.Height - height) * positionY;
        canvas.DrawBitmap(bitmap, new SKRect((float)left, (float)top, (float)(left + width), (float)(top + height)), paint);
        canvas.Restore();
    }

    private static void DrawShape(SKCanvas canvas, SKRect rect, CompositionObject item)
    {
        if (TryColor(item.FillColor, item.Opacity, out var fillColor)
            && item.Kind != CompositionObjectKind.Line)
        {
            using var fill = new SKPaint { Color = fillColor, IsAntialias = true, Style = SKPaintStyle.Fill };
            if (item.Kind == CompositionObjectKind.Ellipse) canvas.DrawOval(rect, fill);
            else canvas.DrawRect(rect, fill);
        }
        if (TryColor(item.StrokeColor, item.Opacity, out var strokeColor) && item.StrokeWidthPoints > 0)
        {
            using var stroke = new SKPaint
            {
                Color = strokeColor,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)item.StrokeWidthPoints,
            };
            if (item.Kind == CompositionObjectKind.Line) canvas.DrawLine(rect.Left, rect.MidY, rect.Right, rect.MidY, stroke);
            else if (item.Kind == CompositionObjectKind.Ellipse) canvas.DrawOval(rect, stroke);
            else canvas.DrawRect(rect, stroke);
        }
    }

    private static void DrawText(
        SKCanvas canvas,
        SKRect frame,
        ManuscriptDocument semantic,
        CompositionObject item,
        IReadOnlyDictionary<FontKey, SKTypeface> typefaces,
        List<CompositionCanvasPreviewDiagnostic> diagnostics)
    {
        if (TryColor(item.BackgroundColor, item.BackgroundOpacity * item.Opacity, out var background))
        {
            using var backgroundPaint = new SKPaint { Color = background, Style = SKPaintStyle.Fill };
            canvas.DrawRect(frame, backgroundPaint);
        }
        if (TryColor(item.StrokeColor, item.Opacity, out var stroke) && item.StrokeWidthPoints > 0)
        {
            using var border = new SKPaint { Color = stroke, Style = SKPaintStyle.Stroke, StrokeWidth = (float)item.StrokeWidthPoints, IsAntialias = true };
            canvas.DrawRect(frame, border);
        }

        var inlines = ResolveInlines(semantic, item);
        var lines = LayoutLines(item, inlines, typefaces, frame.Width);
        var lineHeight = Math.Max(1, item.FontSizePoints * item.LineHeight);
        var totalHeight = lines.Count * lineHeight;
        if (totalHeight > frame.Height + .01)
            diagnostics.Add(new("warning", "TEXT_OVERFLOW", "Text exceeds its frame and is clipped in the canvas preview.", item.Id));
        var top = item.VerticalAlignment switch
        {
            CompositionVerticalAlignment.Center => frame.Top + (frame.Height - totalHeight) / 2,
            CompositionVerticalAlignment.Bottom => frame.Bottom - totalHeight,
            _ => frame.Top,
        };
        canvas.Save();
        canvas.ClipRect(frame);
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            var x = item.TextAlignment switch
            {
                CompositionTextAlignment.Center => frame.Left + (frame.Width - line.Width) / 2,
                CompositionTextAlignment.End => frame.Right - line.Width,
                _ => frame.Left,
            };
            var baseline = top + lineIndex * lineHeight + item.FontSizePoints;
            foreach (var token in line.Tokens)
            {
                using var font = new SKFont(typefaces[token.Font], (float)token.Size);
                using var paint = new SKPaint
                {
                    Color = ParseColor(item.FillColor, item.Opacity),
                    IsAntialias = true,
                    Style = SKPaintStyle.Fill,
                };
                var tokenBaseline = baseline + token.BaselineShift * item.FontSizePoints;
                DrawShadow(canvas, token.Text, x, tokenBaseline, font, paint, item.TextShadow);
                canvas.DrawText(token.Text, (float)x, (float)tokenBaseline, font, paint);
                if (token.Underline || token.Strikethrough)
                {
                    using var decoration = new SKPaint { Color = paint.Color, Style = SKPaintStyle.Stroke, StrokeWidth = (float)Math.Max(.5, token.Size * .05), IsAntialias = true };
                    if (token.Underline) canvas.DrawLine((float)x, (float)(tokenBaseline + token.Size * .1), (float)(x + token.Width), (float)(tokenBaseline + token.Size * .1), decoration);
                    if (token.Strikethrough) canvas.DrawLine((float)x, (float)(tokenBaseline - token.Size * .3), (float)(x + token.Width), (float)(tokenBaseline - token.Size * .3), decoration);
                }
                x += token.Width;
            }
        }
        canvas.Restore();
    }

    private static List<TextLine> LayoutLines(
        CompositionObject item,
        IReadOnlyList<ManuscriptInline> inlines,
        IReadOnlyDictionary<FontKey, SKTypeface> typefaces,
        double maximumWidth)
    {
        var lines = new List<TextLine> { new([], 0) };
        foreach (var inline in inlines)
        {
            var fontKey = FontForInline(item, inline.Marks);
            var size = item.FontSizePoints * (inline.Marks.Any(mark => mark.Type is ManuscriptMarkType.Superscript or ManuscriptMarkType.Subscript) ? .7 : 1);
            var text = inline.Marks.Any(mark => mark.Type == ManuscriptMarkType.SmallCaps)
                ? inline.Text.ToUpperInvariant()
                : inline.Text;
            foreach (Match match in TextTokens().Matches(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')))
            {
                var value = match.Value;
                if (value == "\n")
                {
                    lines.Add(new([], 0));
                    continue;
                }
                using var font = new SKFont(typefaces[fontKey], (float)size);
                var width = font.MeasureText(value) + Math.Max(0, value.Length - 1) * item.LetterSpacingEm * size;
                var whitespace = value.All(char.IsWhiteSpace);
                var current = lines[^1];
                if (!whitespace && current.Tokens.Count > 0 && current.Width + width > maximumWidth)
                {
                    lines.Add(new([], 0));
                    current = lines[^1];
                }
                if (whitespace && current.Tokens.Count == 0)
                    continue;
                if (width > maximumWidth && !whitespace)
                {
                    foreach (var rune in value.EnumerateRunes())
                    {
                        var glyph = rune.ToString();
                        var glyphWidth = font.MeasureText(glyph) + item.LetterSpacingEm * size;
                        current = lines[^1];
                        if (current.Tokens.Count > 0 && current.Width + glyphWidth > maximumWidth)
                        {
                            lines.Add(new([], 0));
                            current = lines[^1];
                        }
                        AddToken(lines, Token(glyph, glyphWidth, fontKey, size, inline.Marks));
                    }
                    continue;
                }
                AddToken(lines, Token(value, width, fontKey, size, inline.Marks));
            }
        }
        return lines;
    }

    private static DrawToken Token(string text, double width, FontKey font, double size, IReadOnlyList<ManuscriptMark> marks) => new(
        text,
        width,
        font,
        size,
        marks.Any(mark => mark.Type == ManuscriptMarkType.Underline),
        marks.Any(mark => mark.Type == ManuscriptMarkType.Strikethrough),
        marks.Any(mark => mark.Type == ManuscriptMarkType.Superscript) ? -.35 : marks.Any(mark => mark.Type == ManuscriptMarkType.Subscript) ? .2 : 0);

    private static void AddToken(List<TextLine> lines, DrawToken token)
    {
        var line = lines[^1];
        line.Tokens.Add(token);
        lines[^1] = line with { Width = line.Width + token.Width };
    }

    private static void DrawShadow(SKCanvas canvas, string text, double x, double baseline, SKFont font, SKPaint paint, CompositionTextShadow shadow)
    {
        if (shadow == CompositionTextShadow.None)
            return;
        using var shadowPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Color = shadow == CompositionTextShadow.Glow
                ? SKColors.White.WithAlpha(180)
                : SKColors.Black.WithAlpha(shadow == CompositionTextShadow.Strong ? (byte)190 : (byte)110),
        };
        var offset = shadow == CompositionTextShadow.Strong ? 2 : 1;
        canvas.DrawText(text, (float)(x + offset), (float)(baseline + offset), font, shadowPaint);
    }

    private static void DrawAnnotations(
        SKCanvas canvas,
        CompositionScene scene,
        IReadOnlyList<CompositionObject> objects,
        IReadOnlyList<CompositionCanvasPreviewDiagnostic> diagnostics)
    {
        using var guide = new SKPaint { Color = new SKColor(37, 99, 235, 190), Style = SKPaintStyle.Stroke, StrokeWidth = 1, PathEffect = SKPathEffect.CreateDash([5, 4], 0), IsAntialias = true };
        using var safe = new SKPaint { Color = new SKColor(5, 150, 105, 210), Style = SKPaintStyle.Stroke, StrokeWidth = 1, PathEffect = SKPathEffect.CreateDash([4, 3], 0), IsAntialias = true };
        using var gutter = new SKPaint { Color = new SKColor(220, 38, 38, 190), Style = SKPaintStyle.Stroke, StrokeWidth = 1, PathEffect = SKPathEffect.CreateDash([3, 3], 0), IsAntialias = true };
        using var trim = new SKPaint { Color = new SKColor(217, 119, 6, 210), Style = SKPaintStyle.Stroke, StrokeWidth = 1, PathEffect = SKPathEffect.CreateDash([6, 3], 0), IsAntialias = true };
        var page = new SKRect(0, 0, (float)scene.Surface.WidthPoints, (float)scene.Surface.HeightPoints);
        canvas.DrawRect(page, guide);
        var bleed = (float)Math.Clamp(scene.Surface.BleedPoints, 0, Math.Min(page.Width, page.Height) / 2);
        if (bleed > 0)
            canvas.DrawRect(new SKRect(bleed, bleed, page.Right - bleed, page.Bottom - bleed), trim);
        var inset = (float)Math.Clamp(scene.Surface.SafeInsetPoints, 0, Math.Min(page.Width, page.Height) / 2);
        canvas.DrawRect(new SKRect(inset, inset, page.Right - inset, page.Bottom - inset), safe);
        if (scene.Surface.Kind == CompositionSurfaceKind.FacingSpread)
            canvas.DrawLine(page.MidX, page.Top, page.MidX, page.Bottom, gutter);
        if (scene.Surface.TrimWidthPoints > 0 && scene.Surface.SpineWidthPoints > 0)
        {
            var backEdge = bleed + (float)scene.Surface.TrimWidthPoints;
            var frontEdge = backEdge + (float)scene.Surface.SpineWidthPoints;
            canvas.DrawLine(backEdge, page.Top, backEdge, page.Bottom, gutter);
            canvas.DrawLine(frontEdge, page.Top, frontEdge, page.Bottom, gutter);
        }

        using var labelFont = new SKFont(SKTypeface.Default, 8);
        foreach (var item in objects)
        {
            var rect = Rect(scene.Surface, item.Bounds);
            var problem = diagnostics.Any(diagnostic => diagnostic.ObjectId == item.Id
                && diagnostic.Code is "TEXT_OVERFLOW" or "OBJECT_CLIPPED");
            using var frame = new SKPaint
            {
                Color = problem ? new SKColor(220, 38, 38, 230) : new SKColor(79, 70, 229, 180),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = problem ? 2 : 1,
                IsAntialias = true,
            };
            canvas.DrawRect(rect, frame);
            using var labelBackground = new SKPaint { Color = new SKColor(255, 255, 255, 220), Style = SKPaintStyle.Fill };
            using var labelPaint = new SKPaint { Color = frame.Color, IsAntialias = true };
            var label = $"{item.Kind} {item.Id:N}"[..Math.Min(item.Kind.ToString().Length + 9, item.Kind.ToString().Length + 32)];
            var labelWidth = labelFont.MeasureText(label) + 4;
            var labelTop = Math.Max(0, rect.Top - 10);
            canvas.DrawRect(new SKRect(rect.Left, labelTop, rect.Left + labelWidth, labelTop + 10), labelBackground);
            canvas.DrawText(label, rect.Left + 2, labelTop + 8, labelFont, labelPaint);
        }
    }

    private static IReadOnlyList<ManuscriptInline> ResolveInlines(ManuscriptDocument semantic, CompositionObject item)
    {
        if (!string.IsNullOrWhiteSpace(item.TextBinding))
            return [new ManuscriptInline { Text = item.TextBinding }];
        if (item.ContentReferences.Count == 0)
            return [];
        try
        {
            var blocks = ManuscriptRangeResolver.ResolveBlocks(semantic, item.ContentReferences);
            var result = new List<ManuscriptInline>();
            foreach (var block in blocks)
            {
                if (result.Count > 0)
                    result.Add(new ManuscriptInline { Text = "\n" });
                result.AddRange(block.Content);
            }
            return result;
        }
        catch (InvalidDataException)
        {
            return [new ManuscriptInline { Text = "Invalid text binding" }];
        }
    }

    private static FontKey FontForInline(CompositionObject item, IReadOnlyList<ManuscriptMark> marks) => new(
        marks.Any(mark => mark.Type == ManuscriptMarkType.Code) ? "builtin:roboto-mono" : item.FontFamilyKey,
        marks.Any(mark => mark.Type == ManuscriptMarkType.Strong) ? Math.Max(700, item.FontWeight) : item.FontWeight,
        item.Italic || marks.Any(mark => mark.Type == ManuscriptMarkType.Emphasis));

    private static CompositionObject ResolveStyle(
        CompositionObject item,
        IReadOnlyDictionary<Guid, CompositionObjectStyle> styles)
    {
        if (item.StyleId is not Guid styleId || !styles.TryGetValue(styleId, out var style))
            return item;
        return item with
        {
            FontFamilyKey = style.FontFamilyKey,
            FontWeight = style.FontWeight,
            Italic = style.Italic,
            FontSizePoints = style.FontSizePoints,
            LineHeight = style.LineHeight,
            LetterSpacingEm = style.LetterSpacingEm,
            FillColor = style.FillColor,
            BackgroundColor = style.BackgroundColor,
            BackgroundOpacity = style.BackgroundOpacity,
            StrokeColor = style.StrokeColor,
            StrokeWidthPoints = style.StrokeWidthPoints,
            TextAlignment = style.TextAlignment,
            VerticalAlignment = style.VerticalAlignment,
            TextShadow = style.TextShadow,
        };
    }

    private static SKRect Rect(CompositionSurface surface, CompositionBounds bounds) => new(
        (float)(surface.WidthPoints * bounds.XPercent / 100),
        (float)(surface.HeightPoints * bounds.YPercent / 100),
        (float)(surface.WidthPoints * (bounds.XPercent + bounds.WidthPercent) / 100),
        (float)(surface.HeightPoints * (bounds.YPercent + bounds.HeightPercent) / 100));

    private static bool OutsideSurface(CompositionBounds bounds) =>
        bounds.XPercent < 0 || bounds.YPercent < 0
        || bounds.XPercent + bounds.WidthPercent > 100
        || bounds.YPercent + bounds.HeightPercent > 100;

    private static bool TryColor(string value, double opacity, out SKColor color)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("transparent", StringComparison.OrdinalIgnoreCase) || opacity <= 0)
        {
            color = SKColors.Transparent;
            return false;
        }
        color = ParseColor(value, opacity);
        return color.Alpha > 0;
    }

    private static SKColor ParseColor(string value, double opacity)
    {
        if (!SKColor.TryParse(value, out var color))
            color = SKColors.Black;
        return color.WithAlpha(Alpha(opacity));
    }

    private static byte Alpha(double opacity) =>
        (byte)Math.Clamp(Math.Round(Math.Clamp(opacity, 0, 1) * 255), 0, 255);

    private static string CacheKey(
        PageCompositionVariant variant,
        CompositionCanvasPreviewMode mode,
        IReadOnlyList<PublishAsset> assets,
        IEnumerable<(Guid Id, Guid FamilyId, int Weight, bool Italic, byte[] Data)> fontFaces)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "composition-canvas-preview-v1");
        Append(hash, variant.CompositionId.ToString("N"));
        Append(hash, variant.Composition.Revision.ToString());
        Append(hash, variant.Revision.ToString());
        Append(hash, mode.ToString());
        Append(hash, variant.SceneJson);
        Append(hash, variant.Composition.SemanticManuscriptJson);
        foreach (var asset in assets.OrderBy(item => item.Id))
        {
            Append(hash, asset.Id.ToString("N"));
            hash.AppendData(asset.Data);
        }
        foreach (var face in fontFaces.OrderBy(item => item.Id))
        {
            Append(hash, $"{face.Id:N}:{face.FamilyId:N}:{face.Weight}:{face.Italic}");
            hash.AppendData(face.Data);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value) =>
        hash.AppendData(Encoding.UTF8.GetBytes(value));

    [GeneratedRegex("(\\n|[^\\S\\r\\n]+|[^\\s]+)", RegexOptions.CultureInvariant)]
    private static partial Regex TextTokens();

    private sealed record FontKey(string FamilyKey, int Weight, bool Italic);
    private sealed record DrawToken(
        string Text,
        double Width,
        FontKey Font,
        double Size,
        bool Underline,
        bool Strikethrough,
        double BaselineShift);
    private sealed record TextLine(List<DrawToken> Tokens, double Width);
}
