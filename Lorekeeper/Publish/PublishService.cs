using System.Globalization;
using System.Text;
using System.Text.Json;
using Lorekeeper.ChapterVisuals;
using Lorekeeper.ImportExport;
using Lorekeeper.Models;
using Lorekeeper.Manuscripts;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Publish;

public sealed class PublishService(
    AppDbContext db,
    IChapterVisualService chapterVisuals,
    IPublicationEditionService editions,
    IEnumerable<IPublishExportFormatter> formatters) : IPublishService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public async Task<PublishWorkspaceView> GetWorkspaceAsync(
        Guid projectId,
        Guid editionId,
        CancellationToken cancellationToken = default)
    {
        var project = await GetProjectAsync(projectId, cancellationToken);
        var profile = await GetEditionAsync(projectId, editionId, cancellationToken);
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
        var selections = await db.PublicationEditionOutlineItems
            .AsNoTracking()
            .Where(selection => selection.EditionId == editionId)
            .ToListAsync(cancellationToken);
        var placementRows = await db.PublicationImagePlacements
            .AsNoTracking()
            .Where(placement => placement.EditionId == editionId)
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

        var matter = await db.PublicationMatter
            .AsNoTracking()
            .Where(item => item.EditionId == editionId)
            .OrderBy(item => item.Location)
            .ThenBy(item => item.SortOrder)
            .Select(item => PublicationEditionService.MatterView(item))
            .ToListAsync(cancellationToken);
        var mappings = await db.PublicationEditionStyleMappings
            .AsNoTracking()
            .Where(mapping => mapping.EditionId == editionId)
            .Include(mapping => mapping.ManuscriptStyleDefinition)
            .OrderBy(mapping => mapping.SemanticRole)
            .ToListAsync(cancellationToken);
        var fingerprint = await editions.GetSourceFingerprintAsync(projectId, editionId, cancellationToken);
        return new PublishWorkspaceView(
            PublicationEditionService.View(project, profile),
            await editions.ListAsync(projectId, cancellationToken),
            SectionViews(acts, chapters, selections, profile.SelectedCoverChapterId),
            CoverCandidateViews(projectId, acts, chapters),
            placementViews,
            matter,
            mappings.Select(mapping => PublicationEditionService.StyleMappingView(
                mapping,
                mapping.ManuscriptStyleDefinition.Name)).ToList(),
            fingerprint);
    }

    public async Task<ProjectExportFile> ExportAsync(
        Guid projectId,
        Guid editionId,
        PublishExportFormat format,
        CancellationToken cancellationToken = default)
    {
        var formatter = formatters.FirstOrDefault(candidate => candidate.Format == format)
            ?? throw new InvalidOperationException($"No publish formatter is registered for {format}.");
        var document = await GetDocumentAsync(projectId, editionId, cancellationToken);
        if (format == PublishExportFormat.Epub)
            document = await AttachRenderedPicturePagesAsync(document, cancellationToken);

        return new ProjectExportFile(
            FileName: ExportFileName(document, formatter.FileExtension),
            ContentType: formatter.ContentType,
            Content: formatter.Render(document));
    }

    public async Task<PublishDocument> GetDocumentAsync(
        Guid projectId,
        Guid editionId,
        CancellationToken cancellationToken = default)
    {
        var project = await GetProjectAsync(projectId, cancellationToken);
        var profile = await GetEditionAsync(projectId, editionId, cancellationToken);
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
        var selections = await db.PublicationEditionOutlineItems
            .AsNoTracking()
            .Where(selection => selection.EditionId == editionId)
            .ToListAsync(cancellationToken);
        var placements = await db.PublicationImagePlacements
            .AsNoTracking()
            .Where(placement => placement.EditionId == editionId)
            .ToListAsync(cancellationToken);
        var matter = await db.PublicationMatter
            .AsNoTracking()
            .Where(item => item.EditionId == editionId && item.IsIncluded)
            .OrderBy(item => item.Location)
            .ThenBy(item => item.SortOrder)
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
                .Concat(chapter.PageLayout.Images.Select(image => image.ImageId))
                .Concat(chapter.Manuscript.Content
                    .Where(block => block.Type == ManuscriptBlockType.Figure)
                    .Select(block => block.ImageId!.Value)))
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
            .Select(placement => new PublicationImagePlacementDocument(
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
        var namedStyles = (await db.ManuscriptStyleDefinitions
            .AsNoTracking()
            .Where(style => style.ProjectId == projectId)
            .OrderBy(style => style.Kind)
            .ThenBy(style => style.Name)
            .ToListAsync(cancellationToken))
            .Select(style => new PublishManuscriptStyleDocument(
                style.Name,
                style.Kind,
                style.SemanticRole,
                ManuscriptStyleService.NormalizeDefinition(
                    JsonSerializer.Deserialize<ManuscriptStyleProperties>(
                        style.DefinitionJson,
                        ManuscriptCodec.JsonOptions) ?? new ManuscriptStyleProperties())))
            .ToList();

        return new PublishDocument(
            editionId,
            project.Id,
            project.Name,
            project.Slug,
            DateTime.UtcNow,
            ProfileDocument(profile, matter),
            cover,
            sections,
            assets.Values.Select(AssetDocument).ToList(),
            placementDocuments)
        {
            CoverPageLayoutKind = coverPageLayoutKind,
            NamedStyles = namedStyles,
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

    private async Task<PublicationEdition> GetEditionAsync(
        Guid projectId,
        Guid editionId,
        CancellationToken cancellationToken) =>
        await db.PublicationEditions.FirstOrDefaultAsync(
            edition => edition.ProjectId == projectId && edition.Id == editionId,
            cancellationToken)
        ?? throw new InvalidOperationException("Publication edition was not found.");

    private async Task ClearInvalidCoverChapterAsync(
        Project project,
        PublicationEdition profile,
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

    private static PublishDocumentProfile ProfileDocument(
        PublicationEdition profile,
        IReadOnlyList<PublicationMatter> matter) =>
        new(
            profile.TitleOverride,
            profile.Subtitle,
            profile.Author,
            profile.Language,
            profile.Publisher,
            profile.Copyright,
            profile.Isbn,
            profile.Description,
            MatterText(matter, PublicationMatterKind.Dedication),
            MatterText(matter, PublicationMatterKind.Acknowledgments),
            MatterText(matter, PublicationMatterKind.References),
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

    private static string MatterText(
        IReadOnlyList<PublicationMatter> matter,
        PublicationMatterKind kind) =>
        string.Join(
            "\n\n",
            matter
                .Where(item => item.Kind == kind)
                .Select(item => ManuscriptCodec.ProjectPlainText(
                    item.ManuscriptJson,
                    item.Id,
                    item.Revision))
                .Where(text => !string.IsNullOrWhiteSpace(text)));

    private static bool IncludeTitlePage(PublicationEdition profile) => profile.TitlePageMode switch
    {
        PublishTitlePageMode.Include => true,
        PublishTitlePageMode.Omit => false,
        _ => profile.SelectedCoverChapterId is null,
    };

    private static List<PublishSectionView> SectionViews(
        IReadOnlyList<Act> acts,
        IReadOnlyList<Chapter> chapters,
        IReadOnlyList<PublicationEditionOutlineItem> selections,
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
        IReadOnlyList<PublicationEditionOutlineItem> selections,
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

    private static PublicationImagePlacementView PlacementView(
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

    private static int PlacementKindOrder(PublicationImagePlacementKind kind) =>
        kind switch
        {
            PublicationImagePlacementKind.BeforeAct or PublicationImagePlacementKind.BeforeChapter => 0,
            PublicationImagePlacementKind.ChapterOpening => 1,
            PublicationImagePlacementKind.ChapterEnding => 2,
            PublicationImagePlacementKind.AfterAct or PublicationImagePlacementKind.AfterChapter => 3,
            _ => int.MaxValue,
        };

    private static PublishChapterDocument ChapterDocument(Chapter chapter, PublicationEdition profile, int chapterNumber)
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
            pageLayout,
            chapter.Manuscript);
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
        IReadOnlyList<PublicationEditionOutlineItem> selections,
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

    private static void Touch(PublicationEdition profile, Project project)
    {
        var now = DateTime.UtcNow;
        profile.UpdatedAt = now;
        project.UpdatedAt = now;
    }

    private sealed record PlacementRow(
        Guid Id,
        Guid AssetId,
        string AssetFileName,
        PublishOutlineTargetKind TargetKind,
        Guid TargetId,
        PublicationImagePlacementKind PlacementKind,
        string Caption,
        int SortOrder);
}
