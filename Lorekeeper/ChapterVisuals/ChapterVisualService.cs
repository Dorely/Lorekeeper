using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lorekeeper.Fonts;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Publish;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace Lorekeeper.ChapterVisuals;

public sealed class ChapterVisualService(
    AppDbContext db,
    IManuscriptService manuscripts,
    IProjectFontService fonts,
    IPageGeometryService pageGeometry,
    IProjectMutationCoordinator projectMutations) : IChapterVisualService
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
        var projectId = await GetChapterProjectIdAsync(chapterId, cancellationToken);
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var chapter = await GetChapterAsync(chapterId, cancellationToken);
        var previousMode = chapter.VisualMode;
        var targetMode = update.VisualMode;
        var targetLayoutKind = NormalizePageLayoutKind(update.PageLayoutKind ?? chapter.PageLayoutKind);
        PicturePageLayout? picturePageLayout = null;

        if (targetMode == ChapterVisualMode.PicturePage)
        {
            var layout = EnsurePictureTextWithoutTrackingMutation(chapter);
            await ValidateLayoutImageReferencesAsync(
                projectId,
                layout.Images.Select(image => image.ImageId),
                cancellationToken);
            picturePageLayout = layout with { Revision = checked(layout.Revision + 1) };
        }

        chapter.VisualMode = targetMode;
        chapter.PageLayoutKind = targetLayoutKind;
        if (picturePageLayout is not null)
        {
            chapter.PageLayoutJson = JsonSerializer.Serialize(picturePageLayout, JsonOptions);
        }
        else
        {
            if (previousMode == ChapterVisualMode.PicturePage)
            {
                var layout = ReadPageLayout(chapter);
                chapter.PageLayoutJson = JsonSerializer.Serialize(
                    layout with { Revision = checked(layout.Revision + 1) },
                    JsonOptions);
            }
            var coverEditions = await db.PublicationEditions
                .Where(edition => edition.SelectedCoverChapterId == chapter.Id)
                .ToListAsync(cancellationToken);
            foreach (var profile in coverEditions)
            {
                profile.SelectedCoverChapterId = null;
                profile.UpdatedAt = DateTime.UtcNow;
            }
        }
        if (previousMode == ChapterVisualMode.IllustratedProse
            && targetMode != ChapterVisualMode.IllustratedProse)
        {
            var layout = ReadIllustrationLayout(chapter);
            chapter.IllustrationLayoutJson = JsonSerializer.Serialize(
                layout with { Revision = checked(layout.Revision + 1) },
                JsonOptions);
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
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
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
            var normalized = NormalizePageLayout(layout);
            await ValidateLayoutImageReferencesAsync(
                projectId,
                normalized.Images.Select(image => image.ImageId),
                cancellationToken);
            chapter.PageLayoutJson = JsonSerializer.Serialize(
                normalized with { Revision = checked(normalized.Revision + 1) },
                JsonOptions);
        }
        else
        {
            if (placement is { PicturePageRole: not PicturePageImagePlacementRole.Freeform })
                throw new InvalidOperationException("PicturePage placement roles only apply to PicturePage chapters.");
            var layout = ReadIllustrationLayout(chapter);
            elementId = Guid.NewGuid();
            var anchorBlocks = AnchorBlocks(chapter.Manuscript);
            if (anchorBlocks.Count == 0)
                throw new InvalidOperationException("Add manuscript text before anchoring an illustration.");
            var paragraphIndex = anchorBlocks.Count - 1;
            layout = layout with
            {
                Images = layout.Images
                    .Append(new IllustratedProseImageBlock(
                        elementId,
                        imageId,
                        ChapterImageAnchorPosition.AfterParagraph,
                        anchorBlocks[paragraphIndex].Id,
                        WidthPercent: 70,
                        Alignment: ChapterImageAlignment.Center,
                        Caption: string.Empty,
                        AltTextOverride: string.Empty,
                        SortOrder: NextSortOrder(layout.Images),
                        StartOnNewPage: false)
                    {
                        ParagraphIndex = paragraphIndex,
                    })
                    .ToList(),
            };
            var normalized = NormalizeIllustrationLayout(layout, chapter.Manuscript);
            await ValidateLayoutImageReferencesAsync(
                projectId,
                normalized.Images.Select(image => image.ImageId),
                cancellationToken);
            chapter.VisualMode = ChapterVisualMode.IllustratedProse;
            chapter.IllustrationLayoutJson = JsonSerializer.Serialize(
                normalized with { Revision = checked(normalized.Revision + 1) },
                JsonOptions);
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
        var projectId = await GetChapterProjectIdAsync(chapterId, cancellationToken);
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var chapter = await GetChapterAsync(chapterId, cancellationToken);
        var currentLayout = ReadIllustrationLayout(chapter);
        EnsureLayoutRevision("Illustrated Prose", layout.Revision, currentLayout.Revision);
        var normalized = NormalizeIllustrationLayout(layout, chapter.Manuscript);
        await ValidateLayoutImageReferencesAsync(
            projectId,
            normalized.Images.Select(image => image.ImageId),
            cancellationToken);
        chapter.VisualMode = chapter.VisualMode == ChapterVisualMode.Prose && layout.Images.Count > 0
            ? ChapterVisualMode.IllustratedProse
            : chapter.VisualMode;
        chapter.IllustrationLayoutJson = JsonSerializer.Serialize(
            normalized with { Revision = checked(currentLayout.Revision + 1) },
            JsonOptions);
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
        var projectId = await GetChapterProjectIdAsync(chapterId, cancellationToken);
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var chapter = await GetChapterAsync(chapterId, cancellationToken);
        var priorLayout = ChapterTextLayoutSynchronizer.ReadLayout(chapter.PageLayoutJson);
        EnsureLayoutRevision("Picture Page", layout.Revision, priorLayout.Revision);
        var normalized = NormalizePageLayout(layout);
        await ValidateLayoutImageReferencesAsync(
            projectId,
            normalized.Images.Select(image => image.ImageId),
            cancellationToken);
        var projectedBody = ChapterTextLayoutSynchronizer.ProjectBody(normalized);
        var bodyChanged = !string.Equals(chapter.PlainText, projectedBody, StringComparison.Ordinal);

        if (bodyChanged)
        {
            if (!ManuscriptCodec.IsPlainTextOnly(chapter.Manuscript))
            {
                throw new InvalidOperationException(
                    "Picture Page text cannot flatten a semantically formatted manuscript. "
                    + "Edit semantic structure in the manuscript editor; the Picture Page layout "
                    + "will retain its stable block references.");
            }
            await manuscripts.ApplyUnderProjectMutationLeaseAsync(
                chapterId,
                chapter.ManuscriptRevision,
                BuildPlainTextLayoutOperations(chapter.Manuscript, priorLayout, normalized),
                cancellationToken);
            chapter = await GetChapterAsync(chapterId, cancellationToken);
        }
        var referenced = ChapterTextLayoutSynchronizer.AttachReferences(normalized, chapter.Manuscript);
        chapter.VisualMode = ChapterVisualMode.PicturePage;
        chapter.PageLayoutJson = JsonSerializer.Serialize(
            referenced with { Revision = checked(priorLayout.Revision + 1) },
            JsonOptions);
        chapter.UpdatedAt = DateTime.UtcNow;
        await TouchProjectAsync(chapter.ProjectId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return State(chapter);
    }

    internal static IReadOnlyList<ManuscriptOperation> BuildPlainTextLayoutOperations(
        ManuscriptDocument current,
        PicturePageLayout priorLayout,
        PicturePageLayout layout)
    {
        var currentById = current.Content.ToDictionary(block => block.Id, StringComparer.Ordinal);
        var priorReferences = priorLayout.TextElements
            .SelectMany(element => element.ContentReferences ?? [])
            .Select(reference => reference.BlockId)
            .ToHashSet(StringComparer.Ordinal);
        if (current.Content.Count > 0
            && current.Content.Any(block => !priorReferences.Contains(block.Id)))
        {
            throw new InvalidOperationException(
                "The stored Picture Page layout does not reference the complete manuscript. "
                + "Refresh the layout from the manuscript before editing its text.");
        }
        var elementBlocks = new List<PicturePageElementBlocks>();
        var referencedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in layout.TextElements
            .OrderBy(element => element.ReadingOrder)
            .ThenBy(element => element.Id))
        {
            var parsed = ManuscriptCodec.FromPlainText(
                current.ManuscriptId,
                element.Text,
                current.Revision,
                deterministicIds: true);
            var references = element.ContentReferences ?? [];
            if (references.Any(reference =>
                reference.StartOffset is not null || reference.EndOffset is not null))
            {
                throw new InvalidOperationException(
                    "Picture Page text with partial-block references must be edited in the manuscript editor.");
            }
            foreach (var reference in references)
            {
                if (!currentById.ContainsKey(reference.BlockId) || !referencedIds.Add(reference.BlockId))
                {
                    throw new InvalidOperationException(
                        "Picture Page text contains a missing or duplicate manuscript block reference.");
                }
            }
            elementBlocks.Add(new PicturePageElementBlocks(
                references.Select(reference => currentById[reference.BlockId]).ToList(),
                parsed.Content,
                new string?[parsed.Content.Count]));
        }

        var matchedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in elementBlocks)
            MatchExactPicturePageBlocks(element, element.Existing, matchedIds);
        var globalRemaining = elementBlocks
            .SelectMany(element => element.Existing)
            .Where(block => !matchedIds.Contains(block.Id))
            .ToList();
        foreach (var element in elementBlocks)
            MatchExactPicturePageBlocks(element, globalRemaining, matchedIds);
        foreach (var element in elementBlocks)
        {
            var localRemaining = new Queue<ManuscriptBlock>(
                element.Existing.Where(block => !matchedIds.Contains(block.Id)));
            for (var index = 0; index < element.ExistingIds.Length; index++)
            {
                if (element.ExistingIds[index] is not null || localRemaining.Count == 0)
                    continue;
                var matched = localRemaining.Dequeue();
                element.ExistingIds[index] = matched.Id;
                matchedIds.Add(matched.Id);
            }
        }

        var desired = new List<PicturePageDesiredBlock>();
        foreach (var element in elementBlocks)
        {
            for (var index = 0; index < element.Desired.Count; index++)
            {
                var parsedBlock = element.Desired[index];
                desired.Add(new PicturePageDesiredBlock(
                    element.ExistingIds[index],
                    parsedBlock.Type,
                    ManuscriptCodec.Text(parsedBlock),
                    parsedBlock.StyleRole));
            }
        }
        var operations = new List<ManuscriptOperation>();
        var retainedIds = desired
            .Where(block => block.ExistingId is not null)
            .Select(block => block.ExistingId!)
            .ToHashSet(StringComparer.Ordinal);
        for (var index = current.Content.Count - 1; index >= 0; index--)
        {
            if (!retainedIds.Contains(current.Content[index].Id))
                operations.Add(new DeleteManuscriptBlock(current.Content[index].Id));
        }

        var workingIds = current.Content
            .Where(block => retainedIds.Contains(block.Id))
            .Select(block => block.Id)
            .ToList();
        for (var targetIndex = 0; targetIndex < desired.Count; targetIndex++)
        {
            var target = desired[targetIndex];
            if (target.ExistingId is null)
            {
                operations.Add(new InsertManuscriptBlock(
                    targetIndex,
                    target.Type,
                    target.Text,
                    target.StyleRole));
                workingIds.Insert(targetIndex, $"new:{targetIndex}");
                continue;
            }

            var sourceIndex = workingIds.IndexOf(target.ExistingId);
            if (sourceIndex != targetIndex)
            {
                operations.Add(new MoveManuscriptBlock(target.ExistingId, targetIndex));
                workingIds.RemoveAt(sourceIndex);
                workingIds.Insert(targetIndex, target.ExistingId);
            }
        }

        foreach (var target in desired.Where(block => block.ExistingId is not null))
        {
            var existing = currentById[target.ExistingId!];
            if (existing.Type != target.Type)
            {
                if (target.Type == ManuscriptBlockType.SceneBreak)
                {
                    if (ManuscriptCodec.Text(existing).Length > 0)
                        operations.Add(new ReplaceManuscriptBlockText(existing.Id, string.Empty));
                    operations.Add(new SetManuscriptBlockType(
                        existing.Id,
                        target.Type,
                        target.StyleRole));
                }
                else
                {
                    operations.Add(new SetManuscriptBlockType(
                        existing.Id,
                        target.Type,
                        target.StyleRole));
                    operations.Add(new ReplaceManuscriptBlockText(existing.Id, target.Text));
                }
            }
            else if (existing.Type != ManuscriptBlockType.SceneBreak
                && !string.Equals(ManuscriptCodec.Text(existing), target.Text, StringComparison.Ordinal))
            {
                operations.Add(new ReplaceManuscriptBlockText(existing.Id, target.Text));
            }
        }
        return operations;
    }

    private static void MatchExactPicturePageBlocks(
        PicturePageElementBlocks element,
        IReadOnlyList<ManuscriptBlock> candidates,
        HashSet<string> matchedIds)
    {
        var availableByContent = candidates
            .Where(block => !matchedIds.Contains(block.Id))
            .GroupBy(PicturePageBlockIdentity)
            .ToDictionary(
                group => group.Key,
                group => new Queue<ManuscriptBlock>(group),
                StringComparer.Ordinal);
        for (var index = 0; index < element.Desired.Count; index++)
        {
            if (element.ExistingIds[index] is not null)
                continue;
            var identity = PicturePageBlockIdentity(element.Desired[index]);
            if (!availableByContent.TryGetValue(identity, out var matchingQueue) || matchingQueue.Count == 0)
                continue;

            var matched = matchingQueue.Dequeue();
            element.ExistingIds[index] = matched.Id;
            matchedIds.Add(matched.Id);
        }
    }

    private static string PicturePageBlockIdentity(ManuscriptBlock block) =>
        $"{block.Type}\u001f{block.StyleRole}\u001f{ManuscriptCodec.Text(block)}";

    private sealed record PicturePageDesiredBlock(
        string? ExistingId,
        ManuscriptBlockType Type,
        string Text,
        string StyleRole);

    private sealed record PicturePageElementBlocks(
        IReadOnlyList<ManuscriptBlock> Existing,
        IReadOnlyList<ManuscriptBlock> Desired,
        string?[] ExistingIds);

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
        var metrics = await pageGeometry.GetAsync(chapter.ProjectId, chapter.PageLayoutKind, cancellationToken);
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
        var imageIds = state.VisualMode == ChapterVisualMode.PicturePage
            ? state.PageLayout.Images.Select(image => image.ImageId)
            : state.IllustrationLayout.Images.Select(image => image.ImageId);
        var assets = await LoadImageAssetsAsync(chapter.ProjectId, imageIds, cancellationToken);
        var edge = (int)Clamp(maxEdge, 320, 2400, 1400);
        var profile = await db.PublicationEditions.AsNoTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.ProjectId == chapter.ProjectId && candidate.IsDefault,
                cancellationToken);
        var geometry = pageGeometry.Calculate(profile, state.PageLayoutKind);
        if (state.VisualMode == ChapterVisualMode.PicturePage)
        {
            var fontFaces = await LoadFontFacesAsync(chapter.ProjectId, state.PageLayout.TextElements, cancellationToken);
            var brief = await db.BookBriefs.AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.ProjectId == chapter.ProjectId, cancellationToken);
            return [RenderPicturePageSnapshot(state, assets, fontFaces, edge, geometry, brief)];
        }
        return RenderIllustratedProseSnapshots(state, geometry, assets, edge);
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
            var metrics = await pageGeometry.GetAsync(request.ProjectId, request.State.PageLayoutKind, cancellationToken);
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
                pageHeight,
                metrics);
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
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        await RemoveImageReferencesUnderProjectMutationLeaseAsync(
            projectId,
            imageId,
            cancellationToken);
    }

    public async Task RemoveImageReferencesUnderProjectMutationLeaseAsync(
        Guid projectId,
        Guid imageId,
        CancellationToken cancellationToken = default)
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
                chapter.IllustrationLayoutJson = JsonSerializer.Serialize(
                    illustrations with
                    {
                        Images = filteredIllustrations,
                        Revision = checked(illustrations.Revision + 1),
                    },
                    JsonOptions);
                chapterChanged = true;
            }

            var page = ReadPageLayout(chapter);
            var filteredPageImages = page.Images.Where(image => image.ImageId != imageId).ToList();
            if (filteredPageImages.Count != page.Images.Count)
            {
                chapter.PageLayoutJson = JsonSerializer.Serialize(
                    page with
                    {
                        Images = filteredPageImages,
                        Revision = checked(page.Revision + 1),
                    },
                    JsonOptions);
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
            builder.AppendLine("No visual layout is active. Edit semantic manuscript blocks with manuscript operations; change visual mode explicitly before placing images.");
            return builder.ToString();
        }

        if (state.VisualMode == ChapterVisualMode.PicturePage)
        {
            builder.AppendLine($"Picture Page layout revision: {state.PageLayout.Revision}");
            var bookGeometry = GeometryForState(state);
            var geometry = PicturePageImageGenerationGuidance.CanvasGeometry(bookGeometry);
            builder.AppendLine(
                $"Picture page layout: {geometry.LayoutKind}; leaf {geometry.LeafWidthInches:0.##} x {geometry.LeafHeightInches:0.##} in; canvas {geometry.CanvasWidthInches:0.##} x {geometry.CanvasHeightInches:0.##} in; canvas orientation {geometry.Orientation}; canvas aspect {geometry.AspectRatio}; spread: {geometry.IsSpread}; gutter center x: {(geometry.GutterCenterXPercent is { } gutter ? $"{gutter:0.#}%" : "none")}");
            if (includePicturePageGenerationGuidance)
            {
                foreach (var guidanceLine in PicturePageImageGenerationGuidance.BuildManifestLines(state, bookGeometry))
                    builder.AppendLine(guidanceLine);
            }
            foreach (var text in state.PageLayout.TextElements.OrderBy(text => text.ReadingOrder))
            {
                var fontName = fontNames?.GetValueOrDefault(text.FontFamilyKey) ?? text.FontFamilyKey;
                builder.AppendLine(
                    $"Text element {text.Id:D}; role {text.Role}; reading order {text.ReadingOrder}: \"{text.Text}\" at {text.XPercent:0.#},{text.YPercent:0.#} size {text.WidthPercent:0.#}x{text.HeightPercent:0.#}; font {fontName} ({text.FontFamilyKey}) {text.FontWeight}{(text.Italic ? " italic" : string.Empty)}, {text.FontSizePoints:0.#} pt, line height {text.LineHeight:0.##}, tracking {text.LetterSpacingEm:0.###} em, {text.TextAlign}/{text.VerticalAlign}");
            }
            foreach (var image in state.PageLayout.Images.OrderBy(image => image.ZIndex))
            {
                var frame = PicturePageImageGenerationGuidance.ForSlot(bookGeometry, image);
                var placement =
                    $"Image element {image.Id:D}; library image {Name(image.ImageId, imageNames)}; frame at {image.XPercent:0.#},{image.YPercent:0.#} size {image.WidthPercent:0.#}x{image.HeightPercent:0.#}; frame aspect {frame.AspectRatio}; fit {image.Fit}; z {image.ZIndex}";
                builder.AppendLine(placement);
            }
            return builder.ToString();
        }

        builder.AppendLine($"Page layout: {state.PageLayoutKind}");
        builder.AppendLine($"Illustrated Prose layout revision: {state.IllustrationLayout.Revision}");
        foreach (var image in state.IllustrationLayout.Images.OrderBy(image => image.SortOrder))
        {
            builder.AppendLine(
                $"Illustration {Name(image.ImageId, imageNames)} {image.AnchorPosition} paragraph {image.ParagraphIndex}, width {image.WidthPercent:0.#}%, align {image.Alignment}, new page {image.StartOnNewPage}, caption \"{image.Caption}\"");
        }

        return builder.ToString();
    }

    private BookPageGeometry GeometryForState(ChapterVisualState state)
    {
        var projectId = db.Chapters
            .AsNoTracking()
            .Where(chapter => chapter.Id == state.ChapterId)
            .Select(chapter => chapter.ProjectId)
            .FirstOrDefault();
        var profile = projectId == Guid.Empty
            ? null
            : db.PublicationEditions.AsNoTracking().FirstOrDefault(
                candidate => candidate.ProjectId == projectId && candidate.IsDefault);
        return pageGeometry.Calculate(profile, state.PageLayoutKind);
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
            if (!ChapterTextLayoutSynchronizer.SynchronizeFromManuscript(
                    chapter,
                    chapter.Manuscript,
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
        int maxEdge,
        BookPageGeometry geometry,
        BookBrief? brief = null)
    {
        var (width, height) = ScaledPagePixels(geometry.SurfaceWidthInches, geometry.SurfaceHeightInches, maxEdge);
        return RenderPicturePageSnapshot(state, assets, fontFaces, width, height, geometry, brief);
    }

    private static ChapterVisualSnapshot RenderPicturePageSnapshot(
        ChapterVisualState state,
        IReadOnlyDictionary<Guid, PublishAsset> assets,
        IReadOnlyDictionary<PictureFontFaceKey, LoadedPictureFontFace> fontFaces,
        int width,
        int height,
        BookPageGeometry geometry,
        BookBrief? brief = null)
    {
        using var surface = CreatePageSurface(width, height);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        var textFitDiagnostics = new List<ChapterVisualTextFitDiagnostic>();
        var layoutDiagnostics = PicturePageLayoutDiagnostics.Evaluate(geometry, state.PageLayout, brief).ToList();
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
                if (RenderedContrastDiagnostic(surface, text, width, height) is { } contrastDiagnostic)
                    layoutDiagnostics.Add(contrastDiagnostic);

                var key = new PictureFontFaceKey(text.FontFamilyKey, text.FontWeight, text.Italic);
                fontFaces.TryGetValue(key, out var face);
                textFitDiagnostics.Add(DrawPictureTextBox(
                    canvas,
                    text,
                    face,
                    fontFaces.GetValueOrDefault(new PictureFontFaceKey(PicturePageFontKeys.Fallback, 400, false)),
                    width,
                    height,
                    geometry.SurfaceWidthInches));
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
                        diagnostic.ElementId,
                        null,
                        $"{diagnostic.RequiredHeightPixels:0.#} px required",
                        $"<= {diagnostic.AvailableHeightPixels:0.#} px available",
                        "Enlarge the box, reduce or reflow the copy, or deliberately reduce type while preserving readability.")))
                .Concat(textFitDiagnostics.Where(diagnostic => !diagnostic.FontFaceResolved).Select(diagnostic =>
                    new ChapterVisualLayoutDiagnostic(
                        "unsupported_font_face",
                        "error",
                        "The selected font family, weight, or italic face is unavailable; the fallback face was rendered.",
                        diagnostic.ElementId,
                        null,
                        "requested face unresolved",
                        "an installed exact family/weight/style face",
                        "Choose an available face or add the required font file to the project.")))
                .ToList(),
        };
    }

    private static ChapterVisualLayoutDiagnostic? RenderedContrastDiagnostic(
        SKSurface surface,
        PicturePageTextElement text,
        int pageWidth,
        int pageHeight)
    {
        var rect = PercentRect(text.XPercent, text.YPercent, text.WidthPercent, text.HeightPercent, pageWidth, pageHeight);
        if (rect.Width <= 1 || rect.Height <= 1 || string.IsNullOrWhiteSpace(text.Text))
            return null;

        surface.Canvas.Flush();
        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);
        var foreground = TextColor(text.Color, 1);
        var backing = TextColor(text.BackgroundColor, text.BackgroundOpacity);
        var minimumRatio = double.MaxValue;

        const int samplesPerAxis = 5;
        for (var row = 0; row < samplesPerAxis; row++)
        {
            for (var column = 0; column < samplesPerAxis; column++)
            {
                var x = Math.Clamp((int)Math.Round(rect.Left + ((column + 0.5) / samplesPerAxis * rect.Width)), 0, pageWidth - 1);
                var y = Math.Clamp((int)Math.Round(rect.Top + ((row + 0.5) / samplesPerAxis * rect.Height)), 0, pageHeight - 1);
                var background = bitmap.GetPixel(x, y);
                if (backing.Alpha > 0)
                    background = Composite(backing, background);
                minimumRatio = Math.Min(minimumRatio, ContrastRatio(foreground, background));
            }
        }

        var isLarge = text.FontSizePoints >= 18
            || text.FontSizePoints >= 14 && text.FontWeight >= 700;
        var threshold = isLarge ? 3d : 4.5d;
        if (minimumRatio >= threshold)
            return null;

        return new ChapterVisualLayoutDiagnostic(
            "insufficient_text_contrast",
            "error",
            "Rendered text contrast is below the accessibility threshold in at least one sampled part of the text box.",
            text.Id,
            null,
            $"{minimumRatio:0.00}:1 minimum sampled",
            $">= {threshold:0.0}:1 ({(isLarge ? "large" : "normal")} text)",
            "Move the text into its planned quiet region, choose a contrast-safe text color, or edit/regenerate the art to provide a more uniform light or dark field. Use an opaque backing only when explicitly requested or when those composition corrections cannot satisfy accessibility.");
    }

    private static SKColor Composite(SKColor foreground, SKColor background)
    {
        var alpha = foreground.Alpha / 255d;
        byte Channel(byte front, byte back) => (byte)Math.Round((front * alpha) + (back * (1 - alpha)));
        return new SKColor(
            Channel(foreground.Red, background.Red),
            Channel(foreground.Green, background.Green),
            Channel(foreground.Blue, background.Blue));
    }

    private static double ContrastRatio(SKColor first, SKColor second)
    {
        static double Luminance(SKColor color)
        {
            static double Linear(byte channel)
            {
                var value = channel / 255d;
                return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }

            return (0.2126 * Linear(color.Red)) + (0.7152 * Linear(color.Green)) + (0.0722 * Linear(color.Blue));
        }

        var lighter = Math.Max(Luminance(first), Luminance(second));
        var darker = Math.Min(Luminance(first), Luminance(second));
        return (lighter + 0.05) / (darker + 0.05);
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
        BookPageGeometry geometry,
        IReadOnlyDictionary<Guid, PublishAsset> assets,
        int maxEdge)
    {
        var pageWidthInches = geometry.PageWidthInches;
        var pageHeightInches = geometry.PageHeightInches;
        var pageMarginInches = geometry.PageMarginInches;
        var fontSizePoints = geometry.BodyFontSizePoints;
        var lineHeightRatio = geometry.BodyLineHeight;
        var (width, height) = ScaledPagePixels(pageWidthInches, pageHeightInches, maxEdge);
        var pixelsPerInch = width / pageWidthInches;
        var margin = (float)(pageMarginInches * pixelsPerInch);
        var contentWidth = Math.Max(1, width - (margin * 2));
        var contentBottom = Math.Max(margin, height - margin);
        var paragraphSpacing = (float)Math.Max(6, 0.14 * pixelsPerInch);
        var figureSpacing = (float)Math.Max(8, 0.16 * pixelsPerInch);
        var snapshots = new List<ChapterVisualSnapshot>();
        var proseDiagnostics = new List<ChapterVisualLayoutDiagnostic>();
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

        var estimatedCharactersPerLine = Math.Max(
            1,
            (int)Math.Round((contentWidth / Math.Max(1, font.Size)) / 0.52));
        if (estimatedCharactersPerLine is < 45 or > 90)
        {
            proseDiagnostics.Add(new(
                "general_prose_line_length",
                "warning",
                "The estimated prose line length is outside the general-reading range; pagination remains advisory for reflowable EPUB.",
                null,
                null,
                $"about {estimatedCharactersPerLine} characters",
                "45-90 characters",
                estimatedCharactersPerLine < 45
                    ? "Reduce the page margin or body size, or intentionally retain the narrow measure for this format."
                    : "Increase the margin or body size to make line tracking easier."));
        }
        if (fontSizePoints < PicturePageLayoutDiagnostics.MinimumBodyFontPoints)
        {
            proseDiagnostics.Add(new(
                "undersized_body_copy",
                "warning",
                "The publish profile body size is unusually small for sustained prose.",
                null,
                null,
                $"{fontSizePoints:0.#} pt",
                $">= {PicturePageLayoutDiagnostics.MinimumBodyFontPoints:0.#} pt",
                "Increase the Publish body-font size unless the smaller setting is a deliberate production choice."));
        }

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
        if (snapshots.Count > 0 && proseDiagnostics.Count > 0)
        {
            snapshots[0] = snapshots[0] with
            {
                LayoutDiagnostics = proseDiagnostics
                    .DistinctBy(diagnostic => (diagnostic.Code, diagnostic.MeasuredValue))
                    .ToList(),
            };
        }
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
            var availablePageHeight = contentBottom - margin;
            if (requiredHeight > availablePageHeight)
            {
                proseDiagnostics.Add(new(
                    "prose_paragraph_overflow",
                    "error",
                    "A prose paragraph is taller than the available page body and cannot paginate as one block.",
                    null,
                    null,
                    $"{requiredHeight:0.#} px required",
                    $"<= {availablePageHeight:0.#} px available",
                    "Break the paragraph at a logical point, reduce body size, or increase the usable page area."));
            }
            if (lines.Count > 1 && lines[^1].Length is > 0 and < 15 && lines[^2].Length >= 30)
            {
                proseDiagnostics.Add(new(
                    "short_final_line",
                    "warning",
                    "A paragraph ends with a conspicuously short final line in this advisory pagination.",
                    null,
                    null,
                    $"{lines[^1].Length} characters",
                    "avoid markedly short final lines when practical",
                    "Adjust the measure, body size, or copy while preserving intentional rhythm."));
            }
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
            return new ChapterVisualTextFitDiagnostic(text.Id, 0, 0, 0, 0, Fits: false, selectedFace?.ExactMatch == true);

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
        var needsFallbackTypeface = selectedFont.GetGlyphs(text.Text).Any(glyph => glyph == 0);
        using var fallbackTypeface = needsFallbackTypeface ? Typeface(fallbackFace) : null;
        using var font = needsFallbackTypeface
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
            return new ChapterVisualTextFitDiagnostic(text.Id, 0, 0, 0, 0, Fits: false, selectedFace?.ExactMatch == true);

        var letterSpacing = fontSize * (float)Clamp(text.LetterSpacingEm, -0.1, 0.3, 0);
        var lines = WrapPictureText(text.Text, textRect.Width, font, paint, letterSpacing);
        if (lines.Count == 0)
            return new ChapterVisualTextFitDiagnostic(text.Id, 0, 0, textRect.Height, 0, Fits: true, selectedFace?.ExactMatch == true);

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
            FontFaceResolved: selectedFace?.ExactMatch == true);
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

    private async Task<Guid> GetChapterProjectIdAsync(
        Guid chapterId,
        CancellationToken cancellationToken) =>
        await db.Chapters
            .AsNoTracking()
            .Where(chapter => chapter.Id == chapterId)
            .Select(chapter => (Guid?)chapter.ProjectId)
            .FirstOrDefaultAsync(cancellationToken)
        ?? throw new InvalidOperationException("Chapter was not found.");

    private async Task ValidateLayoutImageReferencesAsync(
        Guid projectId,
        IEnumerable<Guid> imageIds,
        CancellationToken cancellationToken)
    {
        var expected = imageIds
            .Where(imageId => imageId != Guid.Empty)
            .Distinct()
            .ToHashSet();
        if (expected.Count == 0)
            return;

        var existing = await db.PublishAssets
            .AsNoTracking()
            .Where(asset => asset.ProjectId == projectId && expected.Contains(asset.Id))
            .Select(asset => asset.Id)
            .ToListAsync(cancellationToken);
        expected.ExceptWith(existing);
        if (expected.Count > 0)
        {
            throw new InvalidOperationException(
                "The visual layout references missing project image(s): "
                + string.Join(", ", expected.Order().Select(imageId => imageId.ToString("N")))
                + ". Reload the chapter visual layout before saving.");
        }
    }

    private static void EnsureLayoutRevision(
        string layoutKind,
        long expected,
        long actual)
    {
        if (expected != actual)
            throw new ChapterVisualRevisionConflictException(layoutKind, expected, actual);
    }

    private async Task TouchProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await db.Projects.FirstOrDefaultAsync(project => project.Id == projectId, cancellationToken);
        if (project is not null)
            project.UpdatedAt = DateTime.UtcNow;
    }

    private static ChapterVisualState State(Chapter chapter)
    {
        var illustrationLayout = chapter.VisualMode == ChapterVisualMode.IllustratedProse
            ? NormalizeIllustrationLayout(ReadIllustrationLayout(chapter), chapter.Manuscript)
            : new IllustratedProseLayout([]);
        var pageLayout = chapter.VisualMode == ChapterVisualMode.PicturePage
            ? NormalizePageLayout(ReadPageLayout(chapter))
            : new PicturePageLayout([], []);
        var pageLayoutKind = chapter.VisualMode == ChapterVisualMode.Prose
            ? ChapterPageLayoutKind.SinglePortrait
            : NormalizePageLayoutKind(chapter.PageLayoutKind);
        return new ChapterVisualState(
            chapter.Id,
            chapter.ManuscriptRevision,
            chapter.VisualMode,
            pageLayoutKind,
            illustrationLayout,
            pageLayout,
            chapter.PlainText);
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
            var layout = JsonSerializer.Deserialize<PicturePageLayout>(chapter.PageLayoutJson, JsonOptions)
                ?? new PicturePageLayout([], []);
            return ChapterTextLayoutSynchronizer.Hydrate(layout, chapter.Manuscript);
        }
        catch (JsonException)
        {
            return new PicturePageLayout([], []);
        }
    }

    private static PicturePageLayout EnsurePictureText(Chapter chapter)
    {
        ChapterTextLayoutSynchronizer.SynchronizeFromManuscript(
            chapter,
            chapter.Manuscript,
            ensureLayout: true);
        return NormalizePageLayout(ReadPageLayout(chapter));
    }

    private static PicturePageLayout EnsurePictureTextWithoutTrackingMutation(Chapter chapter)
    {
        var transient = new Chapter
        {
            Id = chapter.Id,
            ProjectId = chapter.ProjectId,
            Title = chapter.Title,
            ManuscriptJson = chapter.ManuscriptJson,
            ManuscriptRevision = chapter.ManuscriptRevision,
            PageLayoutJson = chapter.PageLayoutJson,
        };
        return EnsurePictureText(transient);
    }

    private static IllustratedProseLayout NormalizeIllustrationLayout(
        IllustratedProseLayout? layout,
        ManuscriptDocument manuscript)
    {
        var anchorBlocks = AnchorBlocks(manuscript);
        var indexById = anchorBlocks
            .Select((block, index) => (block.Id, index))
            .ToDictionary(item => item.Id, item => item.index, StringComparer.Ordinal);
        var images = (layout?.Images ?? [])
            .Where(image => image.ImageId != Guid.Empty)
            .Select((image, index) =>
            {
                var requestedIndex = image.ParagraphIndex < 0 ? 0 : image.ParagraphIndex;
                var blockId = image.BlockId;
                if (!string.IsNullOrWhiteSpace(blockId) && !indexById.ContainsKey(blockId))
                {
                    throw new InvalidDataException(
                        $"Illustration {image.Id:N} references missing manuscript block {blockId}.");
                }
                if (string.IsNullOrWhiteSpace(blockId))
                {
                    blockId = anchorBlocks.ElementAtOrDefault(
                        Math.Clamp(requestedIndex, 0, Math.Max(0, anchorBlocks.Count - 1)))?.Id
                        ?? string.Empty;
                }
                return image with
                {
                    Id = image.Id == Guid.Empty ? Guid.NewGuid() : image.Id,
                    BlockId = blockId,
                    ParagraphIndex = indexById.GetValueOrDefault(blockId, -1),
                    WidthPercent = Clamp(image.WidthPercent, 10, 100, 70),
                    Alignment = Enum.IsDefined(image.Alignment) ? image.Alignment : ChapterImageAlignment.Center,
                    AnchorPosition = Enum.IsDefined(image.AnchorPosition) ? image.AnchorPosition : ChapterImageAnchorPosition.AfterParagraph,
                    Caption = image.Caption.Trim(),
                    AltTextOverride = image.AltTextOverride.Trim(),
                    SortOrder = image.SortOrder < 0 ? index : image.SortOrder,
                };
            })
            .Where(image => !string.IsNullOrWhiteSpace(image.BlockId))
            .OrderBy(image => image.SortOrder)
            .ToList();

        return new IllustratedProseLayout(images)
        {
            Revision = layout?.Revision ?? 0,
        };
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
                Role = Enum.IsDefined(text.Role) ? text.Role : PicturePageTextRole.Body,
            })
            .OrderBy(text => text.ReadingOrder)
            .ToList();

        return new PicturePageLayout(images, texts)
        {
            Revision = layout?.Revision ?? 0,
        };
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

    private static IReadOnlyList<ManuscriptBlock> AnchorBlocks(ManuscriptDocument manuscript) =>
        manuscript.Content;

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
