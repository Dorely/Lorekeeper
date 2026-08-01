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
            SectionViews(acts, chapters, selections),
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
        if (format == PublishExportFormat.Epub
            && await db.PublicationEditions.AsNoTracking()
                .Where(edition => edition.ProjectId == projectId && edition.Id == editionId)
                .Select(edition => edition.Format)
                .SingleOrDefaultAsync(cancellationToken) != PublicationEditionFormat.Epub)
        {
            throw new InvalidOperationException(
                "EPUB export is available only from an EPUB publication edition so print ISBN and product metadata cannot leak into a digital product.");
        }
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
        var unassignedChapters = chapters.Where(chapter => chapter.ActId is null).ToList();
        var sectionSources = acts
            .Select(act => new SectionSource(
                act,
                OutlineOrder(selections, PublishOutlineTargetKind.Act, act.Id, act.Order)))
            .ToList();
        if (unassignedChapters.Count > 0)
        {
            sectionSources.Add(new SectionSource(
                null,
                unassignedChapters.Min(chapter => OutlineOrder(
                    selections,
                    PublishOutlineTargetKind.Chapter,
                    chapter.Id,
                    int.MaxValue - unassignedChapters.Count + chapter.Order))));
        }

        foreach (var source in sectionSources.OrderBy(source => source.SortOrder))
        {
            var sourceChapters = source.Act is null
                ? unassignedChapters
                : chapters.Where(chapter => chapter.ActId == source.Act.Id).ToList();
            var includedChapters = sourceChapters
                .Where(chapter => IsIncluded(selections, PublishOutlineTargetKind.Chapter, chapter.Id))
                .OrderBy(chapter => OutlineOrder(
                    selections,
                    PublishOutlineTargetKind.Chapter,
                    chapter.Id,
                    chapter.Order))
                .ToList();
            if (includedChapters.Count == 0)
                continue;

            var chapterDocuments = new List<PublishChapterDocument>();
            foreach (var chapter in includedChapters)
            {
                chapterNumber++;
                chapterDocuments.Add(ChapterDocument(chapter, profile, chapterNumber));
            }

            if (source.Act is null)
            {
                sections.Add(new PublishSectionDocument(
                    null,
                    "Unassigned",
                    string.Empty,
                    IsUnassigned: true,
                    IncludePage: false,
                    IncludeHeading: false,
                    source.SortOrder,
                    chapterDocuments));
                continue;
            }

            actNumber++;
            var includeActPage = IsIncluded(selections, PublishOutlineTargetKind.Act, source.Act.Id);
            var title = profile.NumberActs ? $"Act {actNumber}: {source.Act.Title}" : source.Act.Title;
            sections.Add(new PublishSectionDocument(
                source.Act.Id,
                title,
                source.Act.Synopsis,
                IsUnassigned: false,
                IncludePage: includeActPage,
                IncludeHeading: includeActPage && profile.IncludeActHeadings,
                source.SortOrder,
                chapterDocuments));
        }

        var validPlacements = placements
            .Where(placement => TargetIncluded(sections, placement.TargetKind, placement.TargetId))
            .ToList();
        var matterDocuments = matter
            .Select(item => new PublishMatterDocument(
                item.Id,
                item.Location,
                item.Kind,
                item.Title,
                item.SortOrder,
                ManuscriptCodec.Deserialize(item.ManuscriptJson, item.Id, item.Revision)))
            .ToList();
        var referencedAssetIds = sections
            .SelectMany(section => section.Chapters)
            .SelectMany(chapter => chapter.IllustrationLayout.Images.Select(image => image.ImageId)
                .Concat(chapter.PageLayout.Images.Select(image => image.ImageId))
                .Concat(chapter.Manuscript.Content
                    .Where(block => block.Type == ManuscriptBlockType.Figure)
                    .Select(block => block.ImageId!.Value)))
            .Concat(matterDocuments
                .SelectMany(item => item.Manuscript.Content)
                .Where(block => block.Type == ManuscriptBlockType.Figure)
                .Select(block => block.ImageId!.Value))
            .Concat(validPlacements.Select(placement => placement.AssetId))
            .Concat(profile.SelectedCoverImageId is Guid coverImageId ? [coverImageId] : [])
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
        var cover = profile.SelectedCoverImageId is Guid selectedCoverImageId
            && assets.TryGetValue(selectedCoverImageId, out var selectedCoverImage)
                ? AssetDocument(selectedCoverImage)
                : null;
        var editionStyleMappings = await db.PublicationEditionStyleMappings
            .AsNoTracking()
            .Where(mapping => mapping.EditionId == editionId)
            .ToDictionaryAsync(
                mapping => mapping.ManuscriptStyleDefinitionId,
                mapping => new
                {
                    mapping.SemanticRole,
                    Override = JsonSerializer.Deserialize<ManuscriptStyleProperties>(
                        mapping.OverrideJson,
                        ManuscriptCodec.JsonOptions) ?? new ManuscriptStyleProperties(),
                },
                cancellationToken);
        var namedStyles = (await db.ManuscriptStyleDefinitions
            .AsNoTracking()
            .Where(style => style.ProjectId == projectId)
            .OrderBy(style => style.Kind)
            .ThenBy(style => style.Name)
            .ToListAsync(cancellationToken))
            .Select(style =>
            {
                var definition = ManuscriptStyleService.NormalizeDefinition(
                    JsonSerializer.Deserialize<ManuscriptStyleProperties>(
                        style.DefinitionJson,
                        ManuscriptCodec.JsonOptions) ?? new ManuscriptStyleProperties());
                if (!editionStyleMappings.TryGetValue(style.Id, out var mapping))
                    return new PublishManuscriptStyleDocument(style.Name, style.Kind, style.SemanticRole, definition);
                return new PublishManuscriptStyleDocument(
                    style.Name,
                    style.Kind,
                    mapping.SemanticRole,
                    MergeStyleDefinition(definition, mapping.Override));
            })
            .ToList();

        return new PublishDocument(
            editionId,
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
            NamedStyles = namedStyles,
            Matter = matterDocuments,
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
            geometry: PicturePageGeometry(document.Profile),
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

    private static ChapterPicturePageGeometryProfile PicturePageGeometry(PublishDocumentProfile profile) =>
        new(
            profile.PageWidthInches,
            profile.PageHeightInches,
            profile.PageMarginInches,
            profile.BodyFontSizePoints,
            profile.BodyLineHeight);

    private static PublishDocumentProfile ProfileDocument(PublicationEdition profile) =>
        new(
            profile.TitleOverride,
            profile.Subtitle,
            profile.Author,
            profile.Language,
            profile.Publisher,
            profile.Copyright,
            PublicationIsbn.CanonicalForOutput(profile.Isbn),
            profile.Description,
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

    private static ManuscriptStyleProperties MergeStyleDefinition(
        ManuscriptStyleProperties inherited,
        ManuscriptStyleProperties editionOverride)
    {
        var normalized = editionOverride;
        return inherited with
        {
            FontFamilyKey = normalized.FontFamilyKey ?? inherited.FontFamilyKey,
            FontSizePoints = normalized.FontSizePoints ?? inherited.FontSizePoints,
            FontWeight = normalized.FontWeight ?? inherited.FontWeight,
            Italic = normalized.Italic ?? inherited.Italic,
            SmallCaps = normalized.SmallCaps ?? inherited.SmallCaps,
            LineHeight = normalized.LineHeight ?? inherited.LineHeight,
            SpaceBeforePoints = normalized.SpaceBeforePoints ?? inherited.SpaceBeforePoints,
            SpaceAfterPoints = normalized.SpaceAfterPoints ?? inherited.SpaceAfterPoints,
            KeepWithNext = normalized.KeepWithNext ?? inherited.KeepWithNext,
            TextAlign = normalized.TextAlign ?? inherited.TextAlign,
        };
    }

    private static bool IncludeTitlePage(PublicationEdition profile) => profile.TitlePageMode switch
    {
        PublishTitlePageMode.Include => true,
        PublishTitlePageMode.Omit => false,
        _ => true,
    };

    private static List<PublishSectionView> SectionViews(
        IReadOnlyList<Act> acts,
        IReadOnlyList<Chapter> chapters,
        IReadOnlyList<PublicationEditionOutlineItem> selections)
    {
        var result = new List<PublishSectionView>();
        foreach (var act in acts.OrderBy(act => OutlineOrder(
            selections,
            PublishOutlineTargetKind.Act,
            act.Id,
            act.Order)))
        {
            var actChapters = chapters
                .Where(chapter => chapter.ActId == act.Id)
                .OrderBy(chapter => OutlineOrder(
                    selections,
                    PublishOutlineTargetKind.Chapter,
                    chapter.Id,
                    chapter.Order))
                .Select(chapter => ChapterView(chapter, selections))
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
            .OrderBy(chapter => OutlineOrder(
                selections,
                PublishOutlineTargetKind.Chapter,
                chapter.Id,
                int.MaxValue - chapters.Count + chapter.Order))
            .Select(chapter => ChapterView(chapter, selections))
            .ToList();
        if (unassigned.Count > 0)
            result.Add(new PublishSectionView(null, "Unassigned", IsUnassigned: true, IsIncluded: true, unassigned));
        return result;
    }

    private static PublishChapterView ChapterView(
        Chapter chapter,
        IReadOnlyList<PublicationEditionOutlineItem> selections) =>
        new(
            chapter.Id,
            chapter.ActId,
            chapter.Title,
            IsIncluded(selections, PublishOutlineTargetKind.Chapter, chapter.Id),
            chapter.VisualMode,
            chapter.PageLayoutKind);

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

    internal static PicturePageLayout ReadPageLayout(Chapter chapter)
    {
        if (string.IsNullOrWhiteSpace(chapter.PageLayoutJson))
            return new PicturePageLayout([], []);

        try
        {
            var layout = ChapterTextLayoutSynchronizer.DeserializePersistedLayout(chapter.PageLayoutJson);
            return ChapterTextLayoutSynchronizer.Hydrate(layout, chapter.Manuscript);
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

    private static int OutlineOrder(
        IReadOnlyList<PublicationEditionOutlineItem> selections,
        PublishOutlineTargetKind kind,
        Guid targetId,
        int fallback) =>
        selections.FirstOrDefault(selection =>
            selection.TargetKind == kind && selection.TargetId == targetId)?.SortOrder ?? fallback;

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

    private sealed record PlacementRow(
        Guid Id,
        Guid AssetId,
        string AssetFileName,
        PublishOutlineTargetKind TargetKind,
        Guid TargetId,
        PublicationImagePlacementKind PlacementKind,
        string Caption,
        int SortOrder);

    private sealed record SectionSource(Act? Act, int SortOrder);
}
