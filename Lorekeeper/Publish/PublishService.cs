using System.Globalization;
using System.Text;
using System.Text.Json;
using Lorekeeper.ChapterVisuals;
using Lorekeeper.ImportExport;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Publish;

public sealed class PublishService(
    AppDbContext db,
    IChapterVisualService chapterVisuals,
    IEnumerable<IPublishExportFormatter> formatters) : IPublishService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public async Task<PublishWorkspaceView> GetWorkspaceAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await GetProjectAsync(projectId, cancellationToken);
        var profile = await EnsureProfileAsync(project, cancellationToken);
        await ClearInvalidCoverChapterAsync(project, profile, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        var acts = await db.Acts
            .AsNoTracking()
            .Where(act => act.ProjectId == projectId)
            .OrderBy(act => act.Order)
            .ToListAsync(cancellationToken);
        var chapters = await db.Chapters
            .AsNoTracking()
            .Where(chapter => chapter.ProjectId == projectId)
            .OrderBy(chapter => chapter.Order)
            .ToListAsync(cancellationToken);
        var selections = await db.PublishOutlineSelections
            .AsNoTracking()
            .Where(selection => selection.ProjectId == projectId)
            .ToListAsync(cancellationToken);
        var placementRows = await db.PublishImagePlacements
            .AsNoTracking()
            .Where(placement => placement.ProjectId == projectId)
            .Select(placement => new PlacementRow(
                placement.Id,
                placement.AssetId,
                placement.Asset.FileName,
                placement.TargetKind,
                placement.TargetId,
                placement.PlacementKind,
                placement.Caption,
                placement.SortOrder))
            .ToListAsync(cancellationToken);

        var placementViews = placementRows
            .Select(placement => PlacementView(projectId, placement, acts, chapters))
            .OrderBy(placement => TargetReadingOrder(placement.TargetKind, placement.TargetId, acts, chapters))
            .ThenBy(placement => PlacementKindOrder(placement.PlacementKind))
            .ThenBy(placement => placement.SortOrder)
            .ToList();

        return new PublishWorkspaceView(
            ProfileView(project, profile),
            SectionViews(acts, chapters, selections, profile.SelectedCoverChapterId),
            CoverCandidateViews(projectId, acts, chapters),
            placementViews);
    }

    public async Task SaveProfileAsync(Guid projectId, PublishProfileUpdate update, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(update.TitlePageMode))
            throw new ArgumentOutOfRangeException(nameof(update), "The title page mode is invalid.");
        if (!Enum.IsDefined(update.PrintPicturePageSpreadMode))
            throw new ArgumentOutOfRangeException(nameof(update), "The Print/PDF spread mode is invalid.");
        if (!Enum.IsDefined(update.EpubPicturePageSpreadMode))
            throw new ArgumentOutOfRangeException(nameof(update), "The EPUB spread mode is invalid.");
        if (update.PageWidthInches is < 3 or > 24 || double.IsNaN(update.PageWidthInches) || double.IsInfinity(update.PageWidthInches))
            throw new ArgumentOutOfRangeException(nameof(update), "Page width must be between 3 and 24 inches.");
        if (update.PageHeightInches is < 3 or > 24 || double.IsNaN(update.PageHeightInches) || double.IsInfinity(update.PageHeightInches))
            throw new ArgumentOutOfRangeException(nameof(update), "Page height must be between 3 and 24 inches.");
        if (update.PageMarginInches < 0.125
            || update.PageMarginInches > Math.Min(update.PageWidthInches, update.PageHeightInches) / 3
            || double.IsNaN(update.PageMarginInches)
            || double.IsInfinity(update.PageMarginInches))
        {
            throw new ArgumentOutOfRangeException(nameof(update), "Page margin must be at least 0.125 inches and no more than one third of the shorter page edge.");
        }
        if (update.BodyFontSizePoints is < 7 or > 72 || double.IsNaN(update.BodyFontSizePoints) || double.IsInfinity(update.BodyFontSizePoints))
            throw new ArgumentOutOfRangeException(nameof(update), "Body font size must be between 7 and 72 points.");
        if (update.BodyLineHeight is < 1 or > 2.4 || double.IsNaN(update.BodyLineHeight) || double.IsInfinity(update.BodyLineHeight))
            throw new ArgumentOutOfRangeException(nameof(update), "Body line height must be between 1 and 2.4.");

        var project = await GetProjectAsync(projectId, cancellationToken);
        var profile = await EnsureProfileAsync(project, cancellationToken);
        profile.TitleOverride = Clean(update.TitleOverride);
        profile.Subtitle = Clean(update.Subtitle);
        profile.Author = Clean(update.Author);
        profile.Language = string.IsNullOrWhiteSpace(update.Language) ? "en" : Clean(update.Language);
        profile.Publisher = Clean(update.Publisher);
        profile.Copyright = Clean(update.Copyright);
        profile.Isbn = Clean(update.Isbn);
        profile.Description = Clean(update.Description);
        profile.Dedication = Clean(update.Dedication);
        profile.Acknowledgments = Clean(update.Acknowledgments);
        profile.References = Clean(update.References);
        profile.IncludeTableOfContents = update.IncludeTableOfContents;
        profile.IncludeVisibleTableOfContents = update.IncludeVisibleTableOfContents;
        profile.IncludeActSynopses = update.IncludeActSynopses;
        profile.IncludeChapterSynopses = update.IncludeChapterSynopses;
        profile.IncludeActHeadings = update.IncludeActHeadings;
        profile.IncludeChapterHeadings = update.IncludeChapterHeadings;
        profile.NumberActs = update.NumberActs;
        profile.NumberChapters = update.NumberChapters;
        profile.TitlePageMode = update.TitlePageMode;
        profile.PrintPicturePageSpreadMode = update.PrintPicturePageSpreadMode;
        profile.EpubPicturePageSpreadMode = update.EpubPicturePageSpreadMode;
        profile.PageWidthInches = update.PageWidthInches;
        profile.PageHeightInches = update.PageHeightInches;
        profile.PageMarginInches = update.PageMarginInches;
        profile.BodyFontSizePoints = update.BodyFontSizePoints;
        profile.BodyLineHeight = update.BodyLineHeight;
        Touch(profile, project);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetCoverChapterAsync(Guid projectId, Guid? chapterId, CancellationToken cancellationToken = default)
    {
        var project = await GetProjectAsync(projectId, cancellationToken);
        var profile = await EnsureProfileAsync(project, cancellationToken);
        if (chapterId is Guid id)
        {
            var isPicturePage = await db.Chapters.AnyAsync(
                chapter => chapter.ProjectId == projectId
                    && chapter.Id == id
                    && chapter.VisualMode == ChapterVisualMode.PicturePage,
                cancellationToken);
            if (!isPicturePage)
                throw new InvalidOperationException("Cover chapter must be a Picture Page in this project.");
        }

        profile.SelectedCoverChapterId = chapterId;
        Touch(profile, project);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task SetOutlineSelectionAsync(
        Guid projectId,
        PublishOutlineTargetKind targetKind,
        Guid targetId,
        bool isIncluded,
        CancellationToken cancellationToken = default) =>
        SetOutlineSelectionsAsync(
            projectId,
            [new PublishOutlineSelectionUpdate(targetKind, targetId, isIncluded)],
            cancellationToken);

    public async Task SetOutlineSelectionsAsync(
        Guid projectId,
        IReadOnlyList<PublishOutlineSelectionUpdate> updates,
        CancellationToken cancellationToken = default)
    {
        var project = await GetProjectAsync(projectId, cancellationToken);
        if (updates.Count == 0) return;

        var normalized = new Dictionary<(PublishOutlineTargetKind Kind, Guid TargetId), bool>();
        foreach (var update in updates)
        {
            if (!Enum.IsDefined(update.TargetKind))
                throw new InvalidOperationException("Outline target kind is invalid.");
            normalized[(update.TargetKind, update.TargetId)] = update.IsIncluded;
        }

        var actIds = normalized.Keys
            .Where(key => key.Kind == PublishOutlineTargetKind.Act)
            .Select(key => key.TargetId)
            .ToHashSet();
        if (actIds.Count > 0)
        {
            var existingActIds = await db.Acts
                .Where(act => act.ProjectId == projectId && actIds.Contains(act.Id))
                .Select(act => act.Id)
                .ToListAsync(cancellationToken);
            if (existingActIds.Count != actIds.Count)
                throw new InvalidOperationException("One or more publish acts were not found.");
        }

        var chapterIds = normalized.Keys
            .Where(key => key.Kind == PublishOutlineTargetKind.Chapter)
            .Select(key => key.TargetId)
            .ToHashSet();
        if (chapterIds.Count > 0)
        {
            var existingChapterIds = await db.Chapters
                .Where(chapter => chapter.ProjectId == projectId && chapterIds.Contains(chapter.Id))
                .Select(chapter => chapter.Id)
                .ToListAsync(cancellationToken);
            if (existingChapterIds.Count != chapterIds.Count)
                throw new InvalidOperationException("One or more publish chapters were not found.");
        }

        var existing = await db.PublishOutlineSelections
            .Where(selection => selection.ProjectId == projectId)
            .ToListAsync(cancellationToken);
        var existingByTarget = existing.ToDictionary(
            selection => (selection.TargetKind, selection.TargetId));
        var now = DateTime.UtcNow;
        foreach (var (target, isIncluded) in normalized)
        {
            if (existingByTarget.TryGetValue(target, out var selection))
            {
                selection.IsIncluded = isIncluded;
                selection.UpdatedAt = now;
                continue;
            }

            await db.PublishOutlineSelections.AddAsync(new PublishOutlineSelection
            {
                ProjectId = projectId,
                TargetKind = target.Kind,
                TargetId = target.TargetId,
                IsIncluded = isIncluded,
                CreatedAt = now,
                UpdatedAt = now,
            }, cancellationToken);
        }

        project.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<PublishImagePlacementView> AddImagePlacementAsync(
        Guid projectId,
        PublishImagePlacementCreate request,
        CancellationToken cancellationToken = default)
    {
        var project = await GetProjectAsync(projectId, cancellationToken);
        var metadata = await ValidatePlacementRequestAsync(
            projectId,
            request.AssetId,
            request.TargetKind,
            request.TargetId,
            request.PlacementKind,
            cancellationToken);

        var nextOrder = await db.PublishImagePlacements
            .Where(placement => placement.ProjectId == projectId
                && placement.TargetKind == request.TargetKind
                && placement.TargetId == request.TargetId
                && placement.PlacementKind == request.PlacementKind)
            .Select(placement => (int?)placement.SortOrder)
            .MaxAsync(cancellationToken) ?? -1;

        var placement = new PublishImagePlacement
        {
            ProjectId = projectId,
            AssetId = request.AssetId,
            TargetKind = request.TargetKind,
            TargetId = request.TargetId,
            PlacementKind = request.PlacementKind,
            SortOrder = nextOrder + 1,
            Caption = Clean(request.Caption),
        };
        await db.PublishImagePlacements.AddAsync(placement, cancellationToken);
        project.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return PlacementView(projectId, placement, metadata.AssetFileName, metadata.TargetTitle);
    }

    public async Task<PublishImagePlacementView> UpdateImagePlacementAsync(
        Guid projectId,
        Guid placementId,
        PublishImagePlacementUpdate request,
        CancellationToken cancellationToken = default)
    {
        var project = await GetProjectAsync(projectId, cancellationToken);
        var placement = await db.PublishImagePlacements.FirstOrDefaultAsync(
            candidate => candidate.ProjectId == projectId && candidate.Id == placementId,
            cancellationToken)
            ?? throw new InvalidOperationException("Image placement was not found.");
        var metadata = await ValidatePlacementRequestAsync(
            projectId,
            request.AssetId,
            request.TargetKind,
            request.TargetId,
            request.PlacementKind,
            cancellationToken);

        var oldGroup = (placement.TargetKind, placement.TargetId, placement.PlacementKind);
        var moved = oldGroup != (request.TargetKind, request.TargetId, request.PlacementKind);
        if (moved)
        {
            var nextOrder = await db.PublishImagePlacements
                .Where(candidate => candidate.ProjectId == projectId
                    && candidate.Id != placementId
                    && candidate.TargetKind == request.TargetKind
                    && candidate.TargetId == request.TargetId
                    && candidate.PlacementKind == request.PlacementKind)
                .Select(candidate => (int?)candidate.SortOrder)
                .MaxAsync(cancellationToken) ?? -1;
            placement.SortOrder = nextOrder + 1;
            await CompactPlacementGroupAsync(projectId, oldGroup.TargetKind, oldGroup.TargetId, oldGroup.PlacementKind, placementId, cancellationToken);
        }

        placement.AssetId = request.AssetId;
        placement.TargetKind = request.TargetKind;
        placement.TargetId = request.TargetId;
        placement.PlacementKind = request.PlacementKind;
        placement.Caption = Clean(request.Caption);
        placement.UpdatedAt = DateTime.UtcNow;
        project.UpdatedAt = placement.UpdatedAt;
        await db.SaveChangesAsync(cancellationToken);

        return PlacementView(projectId, placement, metadata.AssetFileName, metadata.TargetTitle);
    }

    public async Task ReorderImagePlacementsAsync(
        Guid projectId,
        IReadOnlyList<Guid> orderedPlacementIds,
        CancellationToken cancellationToken = default)
    {
        var project = await GetProjectAsync(projectId, cancellationToken);
        if (orderedPlacementIds.Count == 0) return;
        if (orderedPlacementIds.Distinct().Count() != orderedPlacementIds.Count)
            throw new InvalidOperationException("Placement reorder contains duplicate IDs.");

        var placements = await db.PublishImagePlacements
            .Where(placement => placement.ProjectId == projectId && orderedPlacementIds.Contains(placement.Id))
            .ToListAsync(cancellationToken);
        if (placements.Count != orderedPlacementIds.Count)
            throw new InvalidOperationException("One or more image placements were not found.");

        var first = placements[0];
        if (placements.Any(placement => placement.TargetKind != first.TargetKind
            || placement.TargetId != first.TargetId
            || placement.PlacementKind != first.PlacementKind))
        {
            throw new InvalidOperationException("Only placements at the same target and position can be reordered together.");
        }

        var groupIds = await db.PublishImagePlacements
            .Where(placement => placement.ProjectId == projectId
                && placement.TargetKind == first.TargetKind
                && placement.TargetId == first.TargetId
                && placement.PlacementKind == first.PlacementKind)
            .Select(placement => placement.Id)
            .ToListAsync(cancellationToken);
        if (!groupIds.ToHashSet().SetEquals(orderedPlacementIds))
            throw new InvalidOperationException("Placement reorder must include every image at that target and position.");

        var orderById = orderedPlacementIds
            .Select((id, order) => (id, order))
            .ToDictionary(item => item.id, item => item.order);
        var now = DateTime.UtcNow;
        foreach (var placement in placements)
        {
            placement.SortOrder = orderById[placement.Id];
            placement.UpdatedAt = now;
        }

        project.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteImagePlacementAsync(Guid projectId, Guid placementId, CancellationToken cancellationToken = default)
    {
        var project = await GetProjectAsync(projectId, cancellationToken);
        var placement = await db.PublishImagePlacements.FirstOrDefaultAsync(
            candidate => candidate.ProjectId == projectId && candidate.Id == placementId,
            cancellationToken);
        if (placement is null) return;

        await CompactPlacementGroupAsync(
            projectId,
            placement.TargetKind,
            placement.TargetId,
            placement.PlacementKind,
            placement.Id,
            cancellationToken);
        db.PublishImagePlacements.Remove(placement);
        project.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ProjectExportFile> ExportAsync(Guid projectId, PublishExportFormat format, CancellationToken cancellationToken = default)
    {
        var formatter = formatters.FirstOrDefault(candidate => candidate.Format == format)
            ?? throw new InvalidOperationException($"No publish formatter is registered for {format}.");
        var document = await GetDocumentAsync(projectId, cancellationToken);
        if (format == PublishExportFormat.Epub)
            document = await AttachRenderedPicturePagesAsync(document, cancellationToken);

        return new ProjectExportFile(
            FileName: ExportFileName(document, formatter.FileExtension),
            ContentType: formatter.ContentType,
            Content: formatter.Render(document));
    }

    public async Task<PublishDocument> GetDocumentAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await GetProjectAsync(projectId, cancellationToken);
        var profile = await EnsureProfileAsync(project, cancellationToken);
        await ClearInvalidCoverChapterAsync(project, profile, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        var acts = await db.Acts
            .AsNoTracking()
            .Where(act => act.ProjectId == projectId)
            .OrderBy(act => act.Order)
            .ToListAsync(cancellationToken);
        var chapters = await db.Chapters
            .AsNoTracking()
            .Where(chapter => chapter.ProjectId == projectId)
            .OrderBy(chapter => chapter.Order)
            .ToListAsync(cancellationToken);
        var selections = await db.PublishOutlineSelections
            .AsNoTracking()
            .Where(selection => selection.ProjectId == projectId)
            .ToListAsync(cancellationToken);
        var placements = await db.PublishImagePlacements
            .AsNoTracking()
            .Where(placement => placement.ProjectId == projectId)
            .ToListAsync(cancellationToken);

        var sections = new List<PublishSectionDocument>();
        var actNumber = 0;
        var chapterNumber = 0;
        foreach (var act in acts.OrderBy(act => act.Order))
        {
            var actChapters = new List<PublishChapterDocument>();
            foreach (var chapter in chapters
                .Where(chapter => chapter.ActId == act.Id
                    && chapter.Id != profile.SelectedCoverChapterId
                    && IsIncluded(selections, PublishOutlineTargetKind.Chapter, chapter.Id))
                .OrderBy(chapter => chapter.Order))
            {
                chapterNumber++;
                actChapters.Add(ChapterDocument(chapter, profile, chapterNumber));
            }

            if (actChapters.Count == 0) continue;
            actNumber++;
            var includeActPage = IsIncluded(selections, PublishOutlineTargetKind.Act, act.Id);
            var title = profile.NumberActs ? $"Act {actNumber}: {act.Title}" : act.Title;
            sections.Add(new PublishSectionDocument(
                act.Id,
                title,
                act.Synopsis,
                IsUnassigned: false,
                IncludePage: includeActPage,
                IncludeHeading: includeActPage && profile.IncludeActHeadings,
                act.Order,
                actChapters));
        }

        var unassigned = new List<PublishChapterDocument>();
        foreach (var chapter in chapters
            .Where(chapter => chapter.ActId is null
                && chapter.Id != profile.SelectedCoverChapterId
                && IsIncluded(selections, PublishOutlineTargetKind.Chapter, chapter.Id))
            .OrderBy(chapter => chapter.Order))
        {
            chapterNumber++;
            unassigned.Add(ChapterDocument(chapter, profile, chapterNumber));
        }

        if (unassigned.Count > 0)
        {
            sections.Add(new PublishSectionDocument(
                null,
                "Unassigned",
                string.Empty,
                IsUnassigned: true,
                IncludePage: false,
                IncludeHeading: false,
                int.MaxValue,
                unassigned));
        }

        var validPlacements = placements
            .Where(placement => (placement.TargetKind != PublishOutlineTargetKind.Chapter
                    || placement.TargetId != profile.SelectedCoverChapterId)
                && TargetIncluded(sections, placement.TargetKind, placement.TargetId))
            .ToList();
        var referencedAssetIds = sections
            .SelectMany(section => section.Chapters)
            .SelectMany(chapter => chapter.IllustrationLayout.Images.Select(image => image.ImageId)
                .Concat(chapter.PageLayout.Images.Select(image => image.ImageId)))
            .Concat(validPlacements.Select(placement => placement.AssetId))
            .ToHashSet();
        var assets = referencedAssetIds.Count == 0
            ? new Dictionary<Guid, PublishAsset>()
            : await db.PublishAssets
                .AsNoTracking()
                .Where(asset => asset.ProjectId == projectId && referencedAssetIds.Contains(asset.Id))
                .ToDictionaryAsync(asset => asset.Id, cancellationToken);

        var placementDocuments = validPlacements
            .Where(placement => assets.ContainsKey(placement.AssetId))
            .Select(placement => new PublishImagePlacementDocument(
                placement.Id,
                AssetDocument(assets[placement.AssetId]),
                placement.TargetKind,
                placement.TargetId,
                placement.PlacementKind,
                placement.Caption,
                placement.SortOrder))
            .ToList();
        var cover = await RenderCoverAsync(profile.SelectedCoverChapterId, chapters, cancellationToken);

        var coverPageLayoutKind = profile.SelectedCoverChapterId is Guid coverChapterId
            ? chapters.FirstOrDefault(chapter => chapter.Id == coverChapterId)?.PageLayoutKind
            : null;

        return new PublishDocument(
            project.Id,
            project.Name,
            project.Slug,
            DateTime.UtcNow,
            ProfileDocument(profile),
            cover,
            sections,
            assets.Values.Select(AssetDocument).ToList(),
            placementDocuments)
        {
            CoverPageLayoutKind = coverPageLayoutKind,
        };
    }

    private async Task<PublishDocument> AttachRenderedPicturePagesAsync(
        PublishDocument document,
        CancellationToken cancellationToken)
    {
        var picturePageIds = document.Sections
            .SelectMany(section => section.Chapters)
            .Where(chapter => chapter.VisualMode == ChapterVisualMode.PicturePage)
            .Select(chapter => chapter.Id)
            .Distinct()
            .ToList();
        if (picturePageIds.Count == 0)
            return document;

        var rotation = document.Profile.EpubPicturePageSpreadMode == EpubPicturePageSpreadMode.SidewaysPortrait
            ? ChapterPicturePageSurfaceRotation.Clockwise90
            : ChapterPicturePageSurfaceRotation.None;
        var surfaces = await chapterVisuals.RenderPicturePageSurfacesAsync(
            picturePageIds,
            physicalPageLongEdgePixels: 2400,
            rotation: rotation,
            cancellationToken: cancellationToken);
        var missingChapterIds = picturePageIds.Where(chapterId => !surfaces.ContainsKey(chapterId)).ToList();
        if (missingChapterIds.Count > 0)
            throw new InvalidOperationException("One or more Picture Pages could not be rendered for EPUB export.");

        var sections = document.Sections
            .Select(section => section with
            {
                Chapters = section.Chapters
                    .Select(chapter => chapter.VisualMode != ChapterVisualMode.PicturePage
                        ? chapter
                        : AttachRenderedPicturePage(chapter, surfaces[chapter.Id]))
                    .ToList(),
            })
            .ToList();
        return document with { Sections = sections };
    }

    private static PublishChapterDocument AttachRenderedPicturePage(
        PublishChapterDocument chapter,
        ChapterPicturePageSurface surface) =>
        chapter with
        {
            RenderedPicturePage = new PublishPicturePageDocument(
                new PublishAssetDocument(
                    chapter.Id,
                    surface.FileName,
                    surface.ContentType,
                    surface.Data,
                    chapter.Title),
                surface.PhysicalPageWidthPixels,
                surface.PhysicalPageHeightPixels,
                surface.LeafCount,
                surface.SurfaceWidthPixels,
                surface.SurfaceHeightPixels,
                surface.Rotation,
                surface.AccessibleText),
        };

    private async Task<Project> GetProjectAsync(Guid projectId, CancellationToken cancellationToken) =>
        await db.Projects.FirstOrDefaultAsync(project => project.Id == projectId, cancellationToken)
        ?? throw new InvalidOperationException($"Project {projectId} not found.");

    private async Task<PublishProfile> EnsureProfileAsync(Project project, CancellationToken cancellationToken)
    {
        var profile = await db.PublishProfiles.FirstOrDefaultAsync(candidate => candidate.ProjectId == project.Id, cancellationToken);
        if (profile is not null)
            return profile;

        profile = new PublishProfile
        {
            ProjectId = project.Id,
            TitleOverride = project.Name,
            Language = "en",
        };
        await db.PublishProfiles.AddAsync(profile, cancellationToken);
        return profile;
    }

    private async Task ClearInvalidCoverChapterAsync(
        Project project,
        PublishProfile profile,
        CancellationToken cancellationToken)
    {
        if (profile.SelectedCoverChapterId is not Guid coverChapterId) return;
        var isValid = await db.Chapters.AnyAsync(
            chapter => chapter.Id == coverChapterId
                && chapter.ProjectId == project.Id
                && chapter.VisualMode == ChapterVisualMode.PicturePage,
            cancellationToken);
        if (isValid) return;

        profile.SelectedCoverChapterId = null;
        Touch(profile, project);
    }

    private async Task<PlacementMetadata> ValidatePlacementRequestAsync(
        Guid projectId,
        Guid assetId,
        PublishOutlineTargetKind targetKind,
        Guid targetId,
        PublishImagePlacementKind placementKind,
        CancellationToken cancellationToken)
    {
        ValidatePlacementKind(targetKind, placementKind);
        var assetFileName = await db.PublishAssets
            .Where(asset => asset.ProjectId == projectId && asset.Id == assetId)
            .Select(asset => asset.FileName)
            .FirstOrDefaultAsync(cancellationToken);
        if (assetFileName is null)
            throw new InvalidOperationException("Project image was not found.");

        var targetTitle = targetKind switch
        {
            PublishOutlineTargetKind.Act => await db.Acts
                .Where(act => act.ProjectId == projectId && act.Id == targetId)
                .Select(act => act.Title)
                .FirstOrDefaultAsync(cancellationToken),
            PublishOutlineTargetKind.Chapter => await db.Chapters
                .Where(chapter => chapter.ProjectId == projectId && chapter.Id == targetId)
                .Select(chapter => chapter.Title)
                .FirstOrDefaultAsync(cancellationToken),
            _ => throw new InvalidOperationException("Outline target kind is invalid."),
        };
        if (targetTitle is null)
            throw new InvalidOperationException($"{targetKind} was not found.");

        var isIncluded = await db.PublishOutlineSelections
            .Where(selection => selection.ProjectId == projectId
                && selection.TargetKind == targetKind
                && selection.TargetId == targetId)
            .Select(selection => (bool?)selection.IsIncluded)
            .FirstOrDefaultAsync(cancellationToken) ?? true;
        if (!isIncluded)
            throw new InvalidOperationException("Images can only be placed on included publish targets.");

        if (targetKind == PublishOutlineTargetKind.Chapter)
        {
            var isCover = await db.PublishProfiles.AnyAsync(
                profile => profile.ProjectId == projectId && profile.SelectedCoverChapterId == targetId,
                cancellationToken);
            if (isCover)
                throw new InvalidOperationException("The cover chapter cannot also have an interior image placement.");
        }

        return new PlacementMetadata(assetFileName, targetTitle);
    }

    private async Task CompactPlacementGroupAsync(
        Guid projectId,
        PublishOutlineTargetKind targetKind,
        Guid targetId,
        PublishImagePlacementKind placementKind,
        Guid excludedPlacementId,
        CancellationToken cancellationToken)
    {
        var siblings = await db.PublishImagePlacements
            .Where(placement => placement.ProjectId == projectId
                && placement.Id != excludedPlacementId
                && placement.TargetKind == targetKind
                && placement.TargetId == targetId
                && placement.PlacementKind == placementKind)
            .OrderBy(placement => placement.SortOrder)
            .ThenBy(placement => placement.CreatedAt)
            .ToListAsync(cancellationToken);
        var now = DateTime.UtcNow;
        for (var order = 0; order < siblings.Count; order++)
        {
            siblings[order].SortOrder = order;
            siblings[order].UpdatedAt = now;
        }
    }

    private async Task<PublishAssetDocument?> RenderCoverAsync(
        Guid? coverChapterId,
        IReadOnlyList<Chapter> chapters,
        CancellationToken cancellationToken)
    {
        if (coverChapterId is not Guid chapterId) return null;
        var chapter = chapters.FirstOrDefault(candidate => candidate.Id == chapterId);
        if (chapter is null || chapter.VisualMode != ChapterVisualMode.PicturePage) return null;

        var surfaces = await chapterVisuals.RenderPicturePageSurfacesAsync(
            [chapterId],
            physicalPageLongEdgePixels: 2400,
            rotation: ChapterPicturePageSurfaceRotation.None,
            cancellationToken: cancellationToken);
        return !surfaces.TryGetValue(chapterId, out var surface)
            ? null
            : new PublishAssetDocument(
                chapterId,
                surface.FileName,
                surface.ContentType,
                surface.Data,
                chapter.Title);
    }

    private static PublishProfileView ProfileView(Project project, PublishProfile profile) =>
        new(
            profile.Id,
            project.Name,
            project.Slug,
            profile.TitleOverride,
            profile.Subtitle,
            profile.Author,
            profile.Language,
            profile.Publisher,
            profile.Copyright,
            profile.Isbn,
            profile.Description,
            profile.Dedication,
            profile.Acknowledgments,
            profile.References,
            profile.IncludeTableOfContents,
            profile.IncludeVisibleTableOfContents,
            profile.IncludeActSynopses,
            profile.IncludeChapterSynopses,
            profile.IncludeActHeadings,
            profile.IncludeChapterHeadings,
            profile.NumberActs,
            profile.NumberChapters,
            profile.TitlePageMode,
            profile.PrintPicturePageSpreadMode,
            profile.EpubPicturePageSpreadMode,
            profile.PageWidthInches,
            profile.PageHeightInches,
            profile.PageMarginInches,
            profile.BodyFontSizePoints,
            profile.BodyLineHeight,
            profile.SelectedCoverChapterId);

    private static PublishDocumentProfile ProfileDocument(PublishProfile profile) =>
        new(
            profile.TitleOverride,
            profile.Subtitle,
            profile.Author,
            profile.Language,
            profile.Publisher,
            profile.Copyright,
            profile.Isbn,
            profile.Description,
            profile.Dedication,
            profile.Acknowledgments,
            profile.References,
            profile.IncludeTableOfContents,
            profile.IncludeVisibleTableOfContents,
            profile.IncludeActSynopses,
            profile.IncludeChapterSynopses,
            profile.IncludeActHeadings,
            profile.IncludeChapterHeadings,
            profile.NumberActs,
            profile.NumberChapters,
            IncludeTitlePage(profile),
            profile.PrintPicturePageSpreadMode,
            profile.EpubPicturePageSpreadMode,
            profile.PageWidthInches,
            profile.PageHeightInches,
            profile.PageMarginInches,
            profile.BodyFontSizePoints,
            profile.BodyLineHeight);

    private static bool IncludeTitlePage(PublishProfile profile) => profile.TitlePageMode switch
    {
        PublishTitlePageMode.Include => true,
        PublishTitlePageMode.Omit => false,
        _ => profile.SelectedCoverChapterId is null,
    };

    private static List<PublishSectionView> SectionViews(
        IReadOnlyList<Act> acts,
        IReadOnlyList<Chapter> chapters,
        IReadOnlyList<PublishOutlineSelection> selections,
        Guid? coverChapterId)
    {
        var result = new List<PublishSectionView>();
        foreach (var act in acts.OrderBy(act => act.Order))
        {
            var actChapters = chapters
                .Where(chapter => chapter.ActId == act.Id)
                .OrderBy(chapter => chapter.Order)
                .Select(chapter => ChapterView(chapter, selections, coverChapterId))
                .ToList();

            result.Add(new PublishSectionView(
                act.Id,
                act.Title,
                IsUnassigned: false,
                IsIncluded(selections, PublishOutlineTargetKind.Act, act.Id),
                actChapters));
        }

        var unassigned = chapters
            .Where(chapter => chapter.ActId is null)
            .OrderBy(chapter => chapter.Order)
            .Select(chapter => ChapterView(chapter, selections, coverChapterId))
            .ToList();
        if (unassigned.Count > 0)
            result.Add(new PublishSectionView(null, "Unassigned", IsUnassigned: true, IsIncluded: true, unassigned));
        return result;
    }

    private static PublishChapterView ChapterView(
        Chapter chapter,
        IReadOnlyList<PublishOutlineSelection> selections,
        Guid? coverChapterId) =>
        new(
            chapter.Id,
            chapter.ActId,
            chapter.Title,
            IsIncluded(selections, PublishOutlineTargetKind.Chapter, chapter.Id),
            chapter.Id == coverChapterId,
            chapter.VisualMode,
            chapter.PageLayoutKind);

    private static List<PublishCoverCandidateView> CoverCandidateViews(
        Guid projectId,
        IReadOnlyList<Act> acts,
        IReadOnlyList<Chapter> chapters)
    {
        var actTitles = acts.ToDictionary(act => act.Id, act => act.Title);
        return OrderedChapters(acts, chapters)
            .Where(chapter => chapter.VisualMode == ChapterVisualMode.PicturePage)
            .Select(chapter =>
            {
                var sectionTitle = chapter.ActId is Guid actId && actTitles.TryGetValue(actId, out var actTitle)
                    ? actTitle
                    : "Unassigned";
                return new PublishCoverCandidateView(
                    chapter.Id,
                    chapter.Title,
                    $"{sectionTitle} · {chapter.Title}",
                    chapter.PageLayoutKind,
                    $"/projects/{projectId:N}/publish/cover/{chapter.Id:N}/preview?v={chapter.UpdatedAt.Ticks:x}");
            })
            .ToList();
    }

    private static IEnumerable<Chapter> OrderedChapters(IReadOnlyList<Act> acts, IReadOnlyList<Chapter> chapters)
    {
        foreach (var act in acts.OrderBy(act => act.Order))
        {
            foreach (var chapter in chapters.Where(chapter => chapter.ActId == act.Id).OrderBy(chapter => chapter.Order))
                yield return chapter;
        }

        foreach (var chapter in chapters.Where(chapter => chapter.ActId is null).OrderBy(chapter => chapter.Order))
            yield return chapter;
    }

    private static PublishImagePlacementView PlacementView(
        Guid projectId,
        PlacementRow placement,
        IReadOnlyList<Act> acts,
        IReadOnlyList<Chapter> chapters) =>
        new(
            placement.Id,
            placement.AssetId,
            placement.AssetFileName,
            AssetPreviewUrl(projectId, placement.AssetId),
            placement.TargetKind,
            placement.TargetId,
            TargetTitle(placement.TargetKind, placement.TargetId, acts, chapters),
            placement.PlacementKind,
            placement.Caption,
            placement.SortOrder);

    private static PublishImagePlacementView PlacementView(
        Guid projectId,
        PublishImagePlacement placement,
        string assetFileName,
        string targetTitle) =>
        new(
            placement.Id,
            placement.AssetId,
            assetFileName,
            AssetPreviewUrl(projectId, placement.AssetId),
            placement.TargetKind,
            placement.TargetId,
            targetTitle,
            placement.PlacementKind,
            placement.Caption,
            placement.SortOrder);

    private static string AssetPreviewUrl(Guid projectId, Guid assetId) =>
        $"/projects/{projectId:N}/images/{assetId:N}/content?maxEdge=320";

    private static string TargetTitle(
        PublishOutlineTargetKind kind,
        Guid targetId,
        IReadOnlyList<Act> acts,
        IReadOnlyList<Chapter> chapters) =>
        kind == PublishOutlineTargetKind.Act
            ? acts.FirstOrDefault(act => act.Id == targetId)?.Title ?? "Deleted act"
            : chapters.FirstOrDefault(chapter => chapter.Id == targetId)?.Title ?? "Deleted chapter";

    private static int TargetReadingOrder(
        PublishOutlineTargetKind kind,
        Guid targetId,
        IReadOnlyList<Act> acts,
        IReadOnlyList<Chapter> chapters)
    {
        var order = 0;
        foreach (var act in acts.OrderBy(act => act.Order))
        {
            if (kind == PublishOutlineTargetKind.Act && targetId == act.Id) return order;
            order++;
            foreach (var chapter in chapters.Where(chapter => chapter.ActId == act.Id).OrderBy(chapter => chapter.Order))
            {
                if (kind == PublishOutlineTargetKind.Chapter && targetId == chapter.Id) return order;
                order++;
            }
        }

        foreach (var chapter in chapters.Where(chapter => chapter.ActId is null).OrderBy(chapter => chapter.Order))
        {
            if (kind == PublishOutlineTargetKind.Chapter && targetId == chapter.Id) return order;
            order++;
        }

        return int.MaxValue;
    }

    private static int PlacementKindOrder(PublishImagePlacementKind kind) =>
        kind switch
        {
            PublishImagePlacementKind.BeforeAct or PublishImagePlacementKind.BeforeChapter => 0,
            PublishImagePlacementKind.ChapterOpening => 1,
            PublishImagePlacementKind.ChapterEnding => 2,
            PublishImagePlacementKind.AfterAct or PublishImagePlacementKind.AfterChapter => 3,
            _ => int.MaxValue,
        };

    private static PublishChapterDocument ChapterDocument(Chapter chapter, PublishProfile profile, int chapterNumber)
    {
        var illustrationLayout = chapter.VisualMode == ChapterVisualMode.IllustratedProse
            ? ReadIllustrationLayout(chapter)
            : new IllustratedProseLayout([]);
        var pageLayout = chapter.VisualMode == ChapterVisualMode.PicturePage
            ? ReadPageLayout(chapter)
            : new PicturePageLayout([], []);
        var pageLayoutKind = chapter.VisualMode == ChapterVisualMode.Prose
            ? ChapterPageLayoutKind.SinglePortrait
            : chapter.PageLayoutKind;
        return new(
            chapter.Id,
            chapter.ActId,
            profile.NumberChapters ? $"Chapter {chapterNumber}: {chapter.Title}" : chapter.Title,
            chapter.PlainText,
            chapter.Synopsis,
            chapterNumber - 1,
            profile.IncludeChapterHeadings,
            chapter.VisualMode,
            pageLayoutKind,
            illustrationLayout,
            pageLayout);
    }

    private static PublishAssetDocument AssetDocument(PublishAsset asset) =>
        new(asset.Id, asset.FileName, asset.ContentType, asset.Data, asset.AltText);

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

    private static bool IsIncluded(
        IReadOnlyList<PublishOutlineSelection> selections,
        PublishOutlineTargetKind kind,
        Guid targetId) =>
        selections.FirstOrDefault(selection => selection.TargetKind == kind && selection.TargetId == targetId)?.IsIncluded ?? true;

    private static bool TargetIncluded(
        IReadOnlyList<PublishSectionDocument> sections,
        PublishOutlineTargetKind kind,
        Guid targetId) =>
        kind == PublishOutlineTargetKind.Act
            ? sections.Any(section => section.ActId == targetId && section.IncludePage)
            : sections.SelectMany(section => section.Chapters).Any(chapter => chapter.Id == targetId);

    private static void ValidatePlacementKind(
        PublishOutlineTargetKind targetKind,
        PublishImagePlacementKind placementKind)
    {
        var valid = targetKind == PublishOutlineTargetKind.Act
            ? placementKind is PublishImagePlacementKind.BeforeAct or PublishImagePlacementKind.AfterAct
            : targetKind == PublishOutlineTargetKind.Chapter
                && placementKind is PublishImagePlacementKind.BeforeChapter
                    or PublishImagePlacementKind.ChapterOpening
                    or PublishImagePlacementKind.ChapterEnding
                    or PublishImagePlacementKind.AfterChapter;

        if (!valid)
            throw new InvalidOperationException($"{placementKind} cannot be used with a {targetKind} target.");
    }

    private static string Clean(string value) => value.Trim();

    private static string ExportFileName(PublishDocument document, string extension)
    {
        var exportedDate = document.ExportedAtUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var fileName = string.Join(
            '_',
            new[]
            {
                document.DisplayTitle,
                document.Profile.Author,
                document.Profile.Language,
                exportedDate,
            }
            .Select(SafeFileNameSegment)
            .Where(segment => !string.IsNullOrWhiteSpace(segment)));

        return $"{(string.IsNullOrWhiteSpace(fileName) ? "project" : fileName)}{extension}";
    }

    private static string SafeFileNameSegment(string input)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var builder = new StringBuilder(input.Length);
        var previousSeparator = false;

        foreach (var ch in input.Trim())
        {
            var isSeparator = char.IsWhiteSpace(ch)
                || invalid.Contains(ch)
                || ch is '_' or '/' or '\\';
            if (isSeparator)
            {
                if (builder.Length > 0 && !previousSeparator)
                {
                    builder.Append('_');
                    previousSeparator = true;
                }

                continue;
            }

            builder.Append(ch);
            previousSeparator = false;
        }

        return builder.ToString().Trim('_', ' ', '.');
    }

    private static void Touch(PublishProfile profile, Project project)
    {
        var now = DateTime.UtcNow;
        profile.UpdatedAt = now;
        project.UpdatedAt = now;
    }

    private sealed record PlacementMetadata(string AssetFileName, string TargetTitle);

    private sealed record PlacementRow(
        Guid Id,
        Guid AssetId,
        string AssetFileName,
        PublishOutlineTargetKind TargetKind,
        Guid TargetId,
        PublishImagePlacementKind PlacementKind,
        string Caption,
        int SortOrder);
}
