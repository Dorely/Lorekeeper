using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lorekeeper.Chapters;
using Lorekeeper.Fonts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace Lorekeeper.ChapterVisuals;

public sealed class ChapterVisualService(
    AppDbContext db,
    IChapterService chapters,
    IProjectFontService fonts) : IChapterVisualService
{
    private const int MaxSnapshotPages = 12;
    private const int TextFitSnapshotMaxEdge = 1400;
    private const int TextFitMinimumTenths = 80;
    private const int TextFitMaximumTenths = 1440;
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
        }
        else
        {
            var coverProfiles = await db.PublishProfiles
                .Where(profile => profile.SelectedCoverChapterId == chapter.Id)
                .ToListAsync(cancellationToken);
            foreach (var profile in coverProfiles)
            {
                profile.SelectedCoverChapterId = null;
                profile.UpdatedAt = DateTime.UtcNow;
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
        ChapterImagePlacementRequest? placement = null,
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
            var request = placement ?? new ChapterImagePlacementRequest();
            if (request.PicturePageRole != PicturePageImagePlacementRole.ReplaceElement
                && request.TargetPictureImageElementId is { } unusedTarget
                && unusedTarget != Guid.Empty)
            {
                throw new InvalidOperationException("targetPictureImageElementId is only valid with ReplaceElement placement.");
            }
            if (request.PicturePageRole == PicturePageImagePlacementRole.ReplaceElement)
            {
                if (request.TargetPictureImageElementId is not { } targetId || targetId == Guid.Empty)
                    throw new InvalidOperationException("ReplaceElement requires a target PicturePage image element.");
                if (layout.Images.All(image => image.Id != targetId))
                {
                    throw new InvalidOperationException(
                        $"PicturePage image element {targetId:N} was not found in chapter {chapterId:N}. " +
                        "Re-read the chapter visual layout and use a current image element id.");
                }
                elementId = targetId;
                layout = layout with
                {
                    Images = layout.Images
                        .Select(image => image.Id == targetId ? image with { ImageId = imageId } : image)
                        .ToList(),
                };
            }
            else
            {
                var zIndexes = layout.Images.Select(image => image.ZIndex)
                    .Concat(layout.TextElements.Select(text => text.ZIndex))
                    .ToList();
                var zIndex = request.PicturePageRole == PicturePageImagePlacementRole.Background
                    ? zIndexes.DefaultIfEmpty(0).Min() - 1
                    : request.ZIndex ?? zIndexes.DefaultIfEmpty(0).Max() + 1;
                elementId = Guid.NewGuid();
                var background = request.PicturePageRole == PicturePageImagePlacementRole.Background;
                layout = layout with
                {
                    Images = layout.Images
                        .Append(new PicturePageImageElement(
                            elementId,
                            imageId,
                            XPercent: background ? 0 : request.XPercent ?? 20,
                            YPercent: background ? 0 : request.YPercent ?? 20,
                            WidthPercent: background ? 100 : request.WidthPercent ?? 60,
                            HeightPercent: background ? 100 : request.HeightPercent ?? 45,
                            Fit: background ? ChapterImageFit.Cover : request.Fit ?? ChapterImageFit.Contain,
                            Opacity: 1,
                            ZIndex: zIndex,
                            AltTextOverride: string.Empty))
                        .ToList(),
                };
            }
            chapter.PageLayoutJson = JsonSerializer.Serialize(NormalizePageLayout(layout), JsonOptions);
        }
        else
        {
            if (placement is { PicturePageRole: not PicturePageImagePlacementRole.Freeform })
                throw new InvalidOperationException("PicturePage placement roles only apply to PicturePage chapters.");
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
        var projectedBody = ChapterTextLayoutSynchronizer.ProjectBody(normalized);
        var bodyChanged = !string.Equals(chapter.Body, projectedBody, StringComparison.Ordinal);

        chapter.UpdatedAt = DateTime.UtcNow;
        await TouchProjectAsync(chapter.ProjectId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (bodyChanged)
            chapter = await chapters.UpdateAsync(chapterId, body: projectedBody, cancellationToken: cancellationToken);
        return State(chapter);
    }

    public async Task<PicturePageTextFitResult> FitAndSavePicturePageTextAsync(
        Guid chapterId,
        PicturePageLayout layout,
        Guid textElementId,
        CancellationToken cancellationToken = default)
    {
        var chapter = await GetChapterAsync(chapterId, cancellationToken);
        if (chapter.VisualMode != ChapterVisualMode.PicturePage)
            throw new InvalidOperationException("Text fitting requires a PicturePage chapter.");

        var normalized = NormalizePageLayout(layout);
        var text = normalized.TextElements.FirstOrDefault(candidate => candidate.Id == textElementId)
            ?? throw new InvalidOperationException("PicturePage text element was not found.");
        var previousFontSizePoints = text.FontSizePoints;
        var metrics = PageMetrics(chapter.PageLayoutKind);
        var (pageWidth, pageHeight) = ScaledPagePixels(
            metrics.SurfaceWidthInches,
            metrics.SurfaceHeightInches,
            TextFitSnapshotMaxEdge);
        var fontFaces = await LoadFontFacesAsync(chapter.ProjectId, [text], cancellationToken);
        var selectedFace = fontFaces.GetValueOrDefault(
            new PictureFontFaceKey(text.FontFamilyKey, text.FontWeight, text.Italic));
        var fallbackFace = fontFaces.GetValueOrDefault(
            new PictureFontFaceKey(PicturePageFontKeys.Fallback, 400, false));

        using var measurementSurface = CreatePageSurface(1, 1);
        ChapterVisualTextFitDiagnostic Measure(int sizeTenths) =>
            DrawPictureTextBox(
                measurementSurface.Canvas,
                text with { FontSizePoints = sizeTenths / 10d },
                selectedFace,
                fallbackFace,
                pageWidth,
                pageHeight,
                metrics.SurfaceWidthInches);

        var fittedSizeTenths = TextFitMinimumTenths;
        var fittedDiagnostic = Measure(TextFitMinimumTenths);
        if (fittedDiagnostic.Fits)
        {
            var maximumDiagnostic = Measure(TextFitMaximumTenths);
            if (maximumDiagnostic.Fits)
            {
                fittedSizeTenths = TextFitMaximumTenths;
                fittedDiagnostic = maximumDiagnostic;
            }
            else
            {
                var passing = TextFitMinimumTenths;
                var failing = TextFitMaximumTenths;
                while (passing + 1 < failing)
                {
                    var candidate = passing + ((failing - passing) / 2);
                    var diagnostic = Measure(candidate);
                    if (diagnostic.Fits)
                    {
                        passing = candidate;
                        fittedDiagnostic = diagnostic;
                    }
                    else
                    {
                        failing = candidate;
                    }
                }

                fittedSizeTenths = passing;
                fittedDiagnostic = Measure(fittedSizeTenths);
            }
        }

        var fittedFontSizePoints = fittedSizeTenths / 10d;
        var fittedLayout = normalized with
        {
            TextElements = normalized.TextElements
                .Select(candidate => candidate.Id == textElementId
                    ? candidate with { FontSizePoints = fittedFontSizePoints }
                    : candidate)
                .ToList(),
        };
        var state = await SavePageLayoutAsync(chapterId, fittedLayout, cancellationToken);

        return new PicturePageTextFitResult(
            state,
            textElementId,
            previousFontSizePoints,
            fittedFontSizePoints,
            fittedDiagnostic,
            HitMinimum: fittedSizeTenths == TextFitMinimumTenths,
            HitMaximum: fittedSizeTenths == TextFitMaximumTenths);
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
        if (state.VisualMode == ChapterVisualMode.Prose)
            return [];
        var imageIds = state.VisualMode == ChapterVisualMode.PicturePage
            ? state.PageLayout.Images.Select(image => image.ImageId)
            : state.IllustrationLayout.Images.Select(image => image.ImageId);
        var assets = await LoadImageAssetsAsync(chapter.ProjectId, imageIds, cancellationToken);
        var edge = (int)Clamp(maxEdge, 320, 2400, 1400);
        if (state.VisualMode == ChapterVisualMode.PicturePage)
        {
            var fontFaces = await LoadFontFacesAsync(chapter.ProjectId, state.PageLayout.TextElements, cancellationToken);
            return [RenderPicturePageSnapshot(state, assets, fontFaces, edge)];
        }
        if (assets.Count == 0)
            return [];

        var profile = await db.PublishProfiles.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.ProjectId == chapter.ProjectId, cancellationToken);
        return RenderIllustratedProseSnapshots(state, profile, assets, edge);
    }

    public async Task<IReadOnlyDictionary<Guid, ChapterPicturePageSurface>> RenderPicturePageSurfacesAsync(
        IReadOnlyCollection<Guid> chapterIds,
        int physicalPageLongEdgePixels = 2400,
        ChapterPicturePageSurfaceRotation rotation = ChapterPicturePageSurfaceRotation.None,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(rotation))
            throw new ArgumentOutOfRangeException(nameof(rotation), rotation, "The picture page surface rotation is invalid.");

        var requestedIds = chapterIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (requestedIds.Count == 0)
            return new Dictionary<Guid, ChapterPicturePageSurface>();

        var chapters = await db.Chapters
            .AsNoTracking()
            .Where(chapter => requestedIds.Contains(chapter.Id)
                && chapter.VisualMode == ChapterVisualMode.PicturePage)
            .ToListAsync(cancellationToken);
        if (chapters.Count == 0)
            return new Dictionary<Guid, ChapterPicturePageSurface>();

        var chapterStates = chapters
            .Select(chapter => new PicturePageRenderRequest(chapter.ProjectId, State(chapter)))
            .ToList();
        var imageIds = chapterStates
            .SelectMany(request => request.State.PageLayout.Images)
            .Select(image => image.ImageId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        var projectIds = chapters
            .Select(chapter => chapter.ProjectId)
            .Distinct()
            .ToList();
        var assets = imageIds.Count == 0
            ? []
            : await db.PublishAssets
                .AsNoTracking()
                .Where(asset => projectIds.Contains(asset.ProjectId) && imageIds.Contains(asset.Id))
                .ToListAsync(cancellationToken);
        var assetsByProject = assets
            .GroupBy(asset => asset.ProjectId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyDictionary<Guid, PublishAsset>)group.ToDictionary(asset => asset.Id));

        var edge = (int)Clamp(physicalPageLongEdgePixels, 320, 2400, 2400);
        var surfaces = new Dictionary<Guid, ChapterPicturePageSurface>(chapterStates.Count);
        foreach (var request in chapterStates)
        {
            var metrics = PageMetrics(request.State.PageLayoutKind);
            var (pageWidth, pageHeight) = ScaledPagePixels(
                metrics.PageWidthInches,
                metrics.PageHeightInches,
                edge);
            var leafCount = metrics.IsDouble ? 2 : 1;
            var nativeSurfaceWidth = pageWidth * leafCount;
            var projectAssets = assetsByProject.GetValueOrDefault(request.ProjectId)
                ?? new Dictionary<Guid, PublishAsset>();
            var fontFaces = await LoadFontFacesAsync(
                request.ProjectId,
                request.State.PageLayout.TextElements,
                cancellationToken);
            var snapshot = RenderPicturePageSnapshot(
                request.State,
                projectAssets,
                fontFaces,
                nativeSurfaceWidth,
                pageHeight);
            var effectiveRotation = metrics.IsDouble
                ? rotation
                : ChapterPicturePageSurfaceRotation.None;
            var data = effectiveRotation == ChapterPicturePageSurfaceRotation.Clockwise90
                ? RotatePngClockwise90(snapshot.Data, nativeSurfaceWidth, pageHeight)
                : snapshot.Data;
            var surfaceWidth = effectiveRotation == ChapterPicturePageSurfaceRotation.Clockwise90
                ? pageHeight
                : nativeSurfaceWidth;
            var surfaceHeight = effectiveRotation == ChapterPicturePageSurfaceRotation.Clockwise90
                ? nativeSurfaceWidth
                : pageHeight;
            var rotationSuffix = effectiveRotation == ChapterPicturePageSurfaceRotation.Clockwise90
                ? "-clockwise-90"
                : string.Empty;
            var fileName = $"chapter-{request.State.ChapterId:N}-picture-page{rotationSuffix}.png";
            surfaces.Add(
                request.State.ChapterId,
                new ChapterPicturePageSurface(
                    request.State.ChapterId,
                    request.State.PageLayoutKind,
                    pageWidth,
                    pageHeight,
                    leafCount,
                    surfaceWidth,
                    surfaceHeight,
                    effectiveRotation,
                    fileName,
                    snapshot.ContentType,
                    data,
                    ProjectPictureBody(request.State.PageLayout)));
        }

        return surfaces;
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

    public string BuildManifest(
        ChapterVisualState state,
        IReadOnlyDictionary<Guid, string>? imageNames = null,
        IReadOnlyDictionary<string, string>? fontNames = null,
        bool includePicturePageGenerationGuidance = true)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Chapter id: {state.ChapterId:D}");
        builder.AppendLine($"Visual mode: {state.VisualMode}");
        if (state.VisualMode == ChapterVisualMode.Prose)
        {
            builder.AppendLine("No visual layout is active. Edit chapter text with edit_chapter; change visual mode explicitly before placing images.");
            return builder.ToString();
        }

        if (state.VisualMode == ChapterVisualMode.PicturePage)
        {
            var geometry = PicturePageImageGenerationGuidance.CanvasGeometry(state.PageLayoutKind);
            builder.AppendLine(
                $"Picture page layout: {geometry.LayoutKind}; leaf {geometry.LeafWidthInches:0.##} x {geometry.LeafHeightInches:0.##} in; canvas {geometry.CanvasWidthInches:0.##} x {geometry.CanvasHeightInches:0.##} in; canvas orientation {geometry.Orientation}; canvas aspect {geometry.AspectRatio}; spread: {geometry.IsSpread}; gutter center x: {(geometry.GutterCenterXPercent is { } gutter ? $"{gutter:0.#}%" : "none")}");
            if (includePicturePageGenerationGuidance)
            {
                foreach (var guidanceLine in PicturePageImageGenerationGuidance.BuildManifestLines(state))
                    builder.AppendLine(guidanceLine);
            }
            foreach (var text in state.PageLayout.TextElements.OrderBy(text => text.ReadingOrder))
            {
                var fontName = fontNames?.GetValueOrDefault(text.FontFamilyKey) ?? text.FontFamilyKey;
                builder.AppendLine(
                    $"Text element {text.Id:D}; reading order {text.ReadingOrder}: \"{text.Text}\" at {text.XPercent:0.#},{text.YPercent:0.#} size {text.WidthPercent:0.#}x{text.HeightPercent:0.#}; font {fontName} ({text.FontFamilyKey}) {text.FontWeight}{(text.Italic ? " italic" : string.Empty)}, {text.FontSizePoints:0.#} pt, line height {text.LineHeight:0.##}, tracking {text.LetterSpacingEm:0.###} em, {text.TextAlign}/{text.VerticalAlign}");
            }
            foreach (var image in state.PageLayout.Images.OrderBy(image => image.ZIndex))
            {
                var frame = PicturePageImageGenerationGuidance.ForSlot(state.PageLayoutKind, image);
                var placement =
                    $"Image element {image.Id:D}; library image {Name(image.ImageId, imageNames)}; frame at {image.XPercent:0.#},{image.YPercent:0.#} size {image.WidthPercent:0.#}x{image.HeightPercent:0.#}; frame aspect {frame.AspectRatio}; fit {image.Fit}; z {image.ZIndex}";
                builder.AppendLine(placement);
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

    public async Task<int> RepairTextLayoutsAsync(CancellationToken cancellationToken = default)
    {
        var candidates = await db.Chapters
            .Where(chapter => chapter.VisualMode == ChapterVisualMode.PicturePage
                || chapter.PageLayoutJson != string.Empty)
            .ToListAsync(cancellationToken);
        var repaired = 0;
        foreach (var chapter in candidates)
        {
            if (!ChapterTextLayoutSynchronizer.SynchronizeFromBody(
                    chapter,
                    chapter.Body,
                    ensureLayout: chapter.VisualMode == ChapterVisualMode.PicturePage))
            {
                continue;
            }

            chapter.UpdatedAt = DateTime.UtcNow;
            repaired++;
        }

        if (repaired > 0)
            await db.SaveChangesAsync(cancellationToken);
        return repaired;
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
        IReadOnlyDictionary<PictureFontFaceKey, LoadedPictureFontFace> fontFaces,
        int maxEdge)
    {
        var metrics = PageMetrics(state.PageLayoutKind);
        var (width, height) = ScaledPagePixels(metrics.SurfaceWidthInches, metrics.SurfaceHeightInches, maxEdge);
        return RenderPicturePageSnapshot(state, assets, fontFaces, width, height);
    }

    private static ChapterVisualSnapshot RenderPicturePageSnapshot(
        ChapterVisualState state,
        IReadOnlyDictionary<Guid, PublishAsset> assets,
        IReadOnlyDictionary<PictureFontFaceKey, LoadedPictureFontFace> fontFaces,
        int width,
        int height)
    {
        using var surface = CreatePageSurface(width, height);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        var textFitDiagnostics = new List<ChapterVisualTextFitDiagnostic>();
        var layoutDiagnostics = PicturePageLayoutDiagnostics.Evaluate(state.PageLayoutKind, state.PageLayout);
        var metrics = PageMetrics(state.PageLayoutKind);
        var layers = state.PageLayout.Images
            .Select((image, index) => new PicturePageRenderLayer(image.ZIndex, index, image, null))
            .Concat(state.PageLayout.TextElements.Select((text, index) =>
                new PicturePageRenderLayer(text.ZIndex, state.PageLayout.Images.Count + index, null, text)))
            .OrderBy(layer => layer.ZIndex)
            .ThenBy(layer => layer.StableOrder);
        foreach (var layer in layers)
        {
            if (layer.Image is { } image)
            {
                if (assets.TryGetValue(image.ImageId, out var asset))
                {
                    DrawImage(
                        canvas,
                        asset,
                        PercentRect(image.XPercent, image.YPercent, image.WidthPercent, image.HeightPercent, width, height),
                        image.Fit,
                        image.Opacity);
                }
                continue;
            }

            if (layer.Text is { } text)
            {
                var key = new PictureFontFaceKey(text.FontFamilyKey, text.FontWeight, text.Italic);
                fontFaces.TryGetValue(key, out var face);
                textFitDiagnostics.Add(DrawPictureTextBox(
                    canvas,
                    text,
                    face,
                    fontFaces.GetValueOrDefault(new PictureFontFaceKey(PicturePageFontKeys.Fallback, 400, false)),
                    width,
                    height,
                    metrics.SurfaceWidthInches));
            }
        }
        return new ChapterVisualSnapshot(1, $"chapter-{state.ChapterId:N}-page-1.png", SnapshotContentType, EncodePng(surface))
        {
            TextFitDiagnostics = textFitDiagnostics,
            LayoutDiagnostics = layoutDiagnostics
                .Concat(textFitDiagnostics.Where(diagnostic => !diagnostic.Fits).Select(diagnostic =>
                    new ChapterVisualLayoutDiagnostic(
                        "text_overflow",
                        "error",
                        $"Text needs {diagnostic.RequiredHeightPixels:0.#} px but only {diagnostic.AvailableHeightPixels:0.#} px is available.",
                        diagnostic.ElementId)))
                .Concat(textFitDiagnostics.Where(diagnostic => !diagnostic.FontFaceResolved).Select(diagnostic =>
                    new ChapterVisualLayoutDiagnostic(
                        "unsupported_font_face",
                        "error",
                        "The selected font family, weight, or italic face is unavailable; the fallback face was rendered.",
                        diagnostic.ElementId)))
                .Concat(textFitDiagnostics.Where(diagnostic => diagnostic.UsedMissingGlyphFallback).Select(diagnostic =>
                    new ChapterVisualLayoutDiagnostic(
                        "missing_glyph_fallback",
                        "warning",
                        "The selected font lacks one or more characters; Andika fallback glyphs were rendered.",
                        diagnostic.ElementId)))
                .ToList(),
        };
    }

    private static byte[] RotatePngClockwise90(byte[] data, int sourceWidth, int sourceHeight)
    {
        using var source = SKBitmap.Decode(data)
            ?? throw new InvalidOperationException("The rendered Picture Page surface could not be decoded.");
        using var rotatedSurface = CreatePageSurface(sourceHeight, sourceWidth);
        var canvas = rotatedSurface.Canvas;
        canvas.Clear(SKColors.White);
        canvas.Translate(sourceHeight, 0);
        canvas.RotateDegrees(90);
        canvas.DrawBitmap(source, 0, 0);
        canvas.Flush();
        return EncodePng(rotatedSurface);
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
        return new SKRect(x, y, x + width, y + height);
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

    private static ChapterVisualTextFitDiagnostic DrawPictureTextBox(
        SKCanvas canvas,
        PicturePageTextElement text,
        LoadedPictureFontFace? selectedFace,
        LoadedPictureFontFace? fallbackFace,
        int pageWidth,
        int pageHeight,
        double surfaceWidthInches)
    {
        var rect = PercentRect(text.XPercent, text.YPercent, text.WidthPercent, text.HeightPercent, pageWidth, pageHeight);
        if (rect.Width <= 0 || rect.Height <= 0)
            return new ChapterVisualTextFitDiagnostic(text.Id, 0, 0, 0, 0, Fits: false, selectedFace?.ExactMatch == true, false);

        if (text.BackgroundOpacity > 0)
        {
            using var backgroundPaint = new SKPaint
            {
                Color = TextColor(text.BackgroundColor, text.BackgroundOpacity),
                IsAntialias = true,
            };
            canvas.DrawRect(rect, backgroundPaint);
        }

        var pixelsPerInch = pageWidth / Math.Max(0.01, surfaceWidthInches);
        var fontSize = Math.Max(6, (float)(Clamp(text.FontSizePoints, 8, 144, 24) / 72 * pixelsPerInch));
        using var selectedTypeface = Typeface(selectedFace);
        using var selectedFont = new SKFont(selectedTypeface ?? SKTypeface.Default, fontSize)
        {
            Edging = SKFontEdging.Antialias,
            Hinting = SKFontHinting.Normal,
            Subpixel = true,
        };
        var usedMissingGlyphFallback = selectedFont.GetGlyphs(text.Text).Any(glyph => glyph == 0);
        using var fallbackTypeface = usedMissingGlyphFallback ? Typeface(fallbackFace) : null;
        using var font = usedMissingGlyphFallback
            ? new SKFont(fallbackTypeface ?? SKTypeface.Default, fontSize)
            {
                Edging = SKFontEdging.Antialias,
                Hinting = SKFontHinting.Normal,
                Subpixel = true,
            }
            : new SKFont(selectedTypeface ?? SKTypeface.Default, fontSize)
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
            return new ChapterVisualTextFitDiagnostic(text.Id, 0, 0, 0, 0, Fits: false, selectedFace?.ExactMatch == true, usedMissingGlyphFallback);

        var letterSpacing = fontSize * (float)Clamp(text.LetterSpacingEm, -0.1, 0.3, 0);
        var lines = WrapPictureText(text.Text, textRect.Width, font, paint, letterSpacing);
        if (lines.Count == 0)
            return new ChapterVisualTextFitDiagnostic(text.Id, 0, 0, textRect.Height, 0, Fits: true, selectedFace?.ExactMatch == true, usedMissingGlyphFallback);

        var lineHeight = fontSize * (float)Clamp(text.LineHeight, 0.9, 2.2, 1.35);
        var blockHeight = lines.Count * lineHeight;
        var startY = text.VerticalAlign switch
        {
            ChapterTextVerticalAlign.Top => textRect.Top,
            ChapterTextVerticalAlign.Bottom => textRect.Bottom - blockHeight,
            _ => textRect.Top + ((textRect.Height - blockHeight) / 2),
        };
        var baseline = startY - font.Metrics.Ascent;
        var drawnLineCount = 0;
        var align = TextAlign(text.TextAlign);
        var x = text.TextAlign switch
        {
            PicturePageTextAlign.Left => textRect.Left,
            PicturePageTextAlign.Right => textRect.Right,
            _ => textRect.Left + (textRect.Width / 2),
        };

        canvas.Save();
        canvas.ClipRect(rect);
        for (var index = 0; index < lines.Count; index++)
        {
            var lineTop = startY + (index * lineHeight);
            var lineBottom = lineTop + lineHeight;
            if (lineTop >= textRect.Top - 0.5f && lineBottom <= textRect.Bottom + 0.5f)
                drawnLineCount++;

            DrawPictureTextShadow(canvas, lines[index], x, baseline, align, font, text.Shadow, fontSize, letterSpacing);
            DrawTrackedText(canvas, lines[index], x, baseline, align, font, paint, letterSpacing);
            baseline += lineHeight;
        }
        canvas.Restore();

        return new ChapterVisualTextFitDiagnostic(
            text.Id,
            lines.Count,
            drawnLineCount,
            textRect.Height,
            blockHeight,
            Fits: blockHeight <= textRect.Height + 0.5f,
            FontFaceResolved: selectedFace?.ExactMatch == true,
            UsedMissingGlyphFallback: usedMissingGlyphFallback);
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

    private static IReadOnlyList<string> WrapPictureText(
        string text,
        float maxWidth,
        SKFont font,
        SKPaint paint,
        float letterSpacing)
    {
        var lines = new List<string>();
        var sourceLines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        foreach (var sourceLine in sourceLines)
        {
            var runes = sourceLine.EnumerateRunes().Select(rune => rune.ToString()).ToList();
            if (runes.Count == 0)
            {
                lines.Add(string.Empty);
                continue;
            }

            var start = 0;
            while (start < runes.Count)
            {
                var end = start;
                var lastWhitespaceBreak = -1;
                var candidate = string.Empty;
                while (end < runes.Count)
                {
                    var next = candidate + runes[end];
                    if (end > start && MeasureTracked(next, font, paint, letterSpacing) > maxWidth)
                        break;

                    candidate = next;
                    end++;
                    if (char.IsWhiteSpace(runes[end - 1], 0))
                        lastWhitespaceBreak = end;
                }

                if (end == start)
                    end++;
                var breakAt = end < runes.Count && lastWhitespaceBreak > start
                    ? lastWhitespaceBreak
                    : end;
                lines.Add(string.Concat(runes.Skip(start).Take(breakAt - start)));
                start = breakAt;
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

    private static float MeasureTracked(string text, SKFont font, SKPaint paint, float letterSpacing)
    {
        var runes = text.EnumerateRunes().Select(rune => rune.ToString()).ToList();
        if (runes.Count == 0)
            return 0;
        return runes.Sum(rune => Measure(rune, font, paint))
            + (letterSpacing * Math.Max(0, runes.Count - 1));
    }

    private static void DrawTrackedText(
        SKCanvas canvas,
        string text,
        float x,
        float y,
        SKTextAlign textAlign,
        SKFont font,
        SKPaint paint,
        float letterSpacing)
    {
        if (Math.Abs(letterSpacing) < 0.01f)
        {
            canvas.DrawText(text, x, y, textAlign, font, paint);
            return;
        }

        var runes = text.EnumerateRunes().Select(rune => rune.ToString()).ToList();
        var width = MeasureTracked(text, font, paint, letterSpacing);
        var cursor = textAlign switch
        {
            SKTextAlign.Center => x - (width / 2),
            SKTextAlign.Right => x - width,
            _ => x,
        };
        foreach (var rune in runes)
        {
            canvas.DrawText(rune, cursor, y, SKTextAlign.Left, font, paint);
            cursor += Measure(rune, font, paint) + letterSpacing;
        }
    }

    private static SKTypeface? Typeface(LoadedPictureFontFace? face)
    {
        if (face?.Data is not { Length: > 0 } data)
            return null;
        using var skData = SKData.CreateCopy(data);
        return SKTypeface.FromData(skData);
    }

    private static void DrawPictureTextShadow(
        SKCanvas canvas,
        string text,
        float x,
        float y,
        SKTextAlign textAlign,
        SKFont font,
        PicturePageTextShadow shadow,
        float fontSize,
        float letterSpacing)
    {
        if (shadow == PicturePageTextShadow.None)
            return;

        switch (shadow)
        {
            case PicturePageTextShadow.Glow:
                DrawPictureShadowText(canvas, text, x, y, textAlign, font, fontSize * 0.08f, new SKColor(255, 255, 255, 180), 0, 0, letterSpacing);
                DrawPictureShadowText(canvas, text, x, y, textAlign, font, fontSize * 0.045f, new SKColor(0, 0, 0, 145), 0, fontSize * 0.025f, letterSpacing);
                break;
            case PicturePageTextShadow.Strong:
                DrawPictureShadowText(canvas, text, x, y, textAlign, font, fontSize * 0.08f, new SKColor(0, 0, 0, 185), 0, fontSize * 0.045f, letterSpacing);
                DrawPictureShadowText(canvas, text, x, y, textAlign, font, 0, new SKColor(0, 0, 0, 220), 0, fontSize * 0.018f, letterSpacing);
                break;
            default:
                DrawPictureShadowText(canvas, text, x, y, textAlign, font, fontSize * 0.055f, new SKColor(0, 0, 0, 155), 0, fontSize * 0.03f, letterSpacing);
                break;
        }
    }

    private static void DrawPictureShadowText(
        SKCanvas canvas,
        string text,
        float x,
        float y,
        SKTextAlign textAlign,
        SKFont font,
        float blur,
        SKColor color,
        float offsetX,
        float offsetY,
        float letterSpacing)
    {
        using var paint = new SKPaint
        {
            Color = color,
            IsAntialias = true,
        };
        using var maskFilter = blur > 0 ? SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blur) : null;
        paint.MaskFilter = maskFilter;
        DrawTrackedText(canvas, text, x + offsetX, y + offsetY, textAlign, font, paint, letterSpacing);
    }

    private static SKTextAlign TextAlign(PicturePageTextAlign textAlign) =>
        textAlign switch
        {
            PicturePageTextAlign.Left => SKTextAlign.Left,
            PicturePageTextAlign.Right => SKTextAlign.Right,
            _ => SKTextAlign.Center,
        };

    private async Task<IReadOnlyDictionary<PictureFontFaceKey, LoadedPictureFontFace>> LoadFontFacesAsync(
        Guid projectId,
        IReadOnlyList<PicturePageTextElement> textElements,
        CancellationToken cancellationToken)
    {
        var keys = textElements
            .Select(text => new PictureFontFaceKey(text.FontFamilyKey, text.FontWeight, text.Italic))
            .Append(new PictureFontFaceKey(PicturePageFontKeys.Fallback, 400, false))
            .Distinct()
            .ToList();
        var result = new Dictionary<PictureFontFaceKey, LoadedPictureFontFace>();
        foreach (var key in keys)
        {
            var resolved = await fonts.ResolveFaceAsync(
                projectId,
                key.FamilyKey,
                key.Weight,
                key.Italic,
                requireExact: true,
                cancellationToken);
            var exact = resolved is not null;
            resolved ??= await fonts.ResolveFaceAsync(
                projectId,
                PicturePageFontKeys.Fallback,
                400,
                italic: false,
                requireExact: true,
                cancellationToken);
            if (resolved is not null)
                result[key] = new LoadedPictureFontFace(resolved.Data, exact);
        }

        return result;
    }

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

    private static ChapterVisualState State(Chapter chapter)
    {
        var illustrationLayout = chapter.VisualMode == ChapterVisualMode.IllustratedProse
            ? NormalizeIllustrationLayout(ReadIllustrationLayout(chapter), chapter.Body)
            : new IllustratedProseLayout([]);
        var pageLayout = chapter.VisualMode == ChapterVisualMode.PicturePage
            ? NormalizePageLayout(ReadPageLayout(chapter))
            : new PicturePageLayout([], []);
        var pageLayoutKind = chapter.VisualMode == ChapterVisualMode.Prose
            ? ChapterPageLayoutKind.SinglePortrait
            : NormalizePageLayoutKind(chapter.PageLayoutKind);
        return new ChapterVisualState(
            chapter.Id,
            chapter.VisualMode,
            pageLayoutKind,
            illustrationLayout,
            pageLayout,
            chapter.Body);
    }

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
        ChapterTextLayoutSynchronizer.SynchronizeFromBody(chapter, chapter.Body, ensureLayout: true);
        return NormalizePageLayout(ReadPageLayout(chapter));
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
                FontFamilyKey = string.IsNullOrWhiteSpace(text.FontFamilyKey) ? PicturePageFontKeys.Default : text.FontFamilyKey.Trim(),
                FontWeight = text.FontWeight is >= 100 and <= 900 ? text.FontWeight : 400,
                FontSizePoints = Clamp(text.FontSizePoints, 8, 144, 24),
                LetterSpacingEm = Clamp(text.LetterSpacingEm, -0.1, 0.3, 0),
                LineHeight = Clamp(text.LineHeight, 0.9, 2.2, 1.35),
                Color = CleanColor(text.Color, "#111827"),
                BackgroundColor = CleanColor(text.BackgroundColor, "#FFFFFF"),
                BackgroundOpacity = Clamp(text.BackgroundOpacity, 0, 1, 0),
                TextAlign = Enum.IsDefined(text.TextAlign) ? text.TextAlign : PicturePageTextAlign.Left,
                VerticalAlign = Enum.IsDefined(text.VerticalAlign) ? text.VerticalAlign : ChapterTextVerticalAlign.Top,
                Shadow = Enum.IsDefined(text.Shadow) ? text.Shadow : PicturePageTextShadow.None,
            })
            .OrderBy(text => text.ReadingOrder)
            .ToList();

        return new PicturePageLayout(images, texts);
    }

    private static string ProjectPictureBody(PicturePageLayout layout) =>
        ChapterTextLayoutSynchronizer.ProjectBody(layout);

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

    private sealed record PicturePageRenderLayer(
        int ZIndex,
        int StableOrder,
        PicturePageImageElement? Image,
        PicturePageTextElement? Text);

    private sealed record PicturePageRenderRequest(
        Guid ProjectId,
        ChapterVisualState State);

    private sealed record PictureFontFaceKey(string FamilyKey, int Weight, bool Italic);

    private sealed record LoadedPictureFontFace(byte[] Data, bool ExactMatch);
}
