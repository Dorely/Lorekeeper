using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lorekeeper.Composition;
using Lorekeeper.ImportExport;
using Lorekeeper.Models;
using Lorekeeper.Manuscripts;
using Lorekeeper.Persistence;
using Lorekeeper.Fonts;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Publish;

public sealed class PublishService(
    AppDbContext db,
    IPublicationEditionService editions,
    IProjectFontService projectFonts,
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
                placement.PresentationJson,
                placement.AltText,
                placement.Decorative,
                placement.Language,
                placement.AccessibilityRole,
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
        var compositions = await db.PageCompositions
            .AsNoTracking()
            .Where(composition => composition.ProjectId == projectId)
            .Include(composition => composition.Variants)
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
                chapterDocuments.Add(ChapterDocument(
                    chapter,
                    profile,
                    chapterNumber,
                    compositions.Where(composition => composition.ChapterId == chapter.Id).ToList()));
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
        var coverDesign = await db.PublicationCoverDesigns
            .AsNoTracking()
            .FirstOrDefaultAsync(design => design.EditionId == editionId, cancellationToken);
        var coverScene = string.IsNullOrWhiteSpace(coverDesign?.CompositionSceneJson)
            ? null
            : JsonSerializer.Deserialize<CompositionScene>(coverDesign.CompositionSceneJson, ManuscriptCodec.JsonOptions);
        var coverSceneImageIds = coverScene is null
            ? []
            : CompositionSceneResolver.Flatten(coverScene)
                .Where(item => item.Visible && item.ImageId is not null)
                .Select(item => item.ImageId!.Value)
                .ToArray();
        var referencedAssetIds = sections
            .SelectMany(section => section.Chapters)
            .SelectMany(chapter => chapter.Manuscript.Content
                    .Where(block => block.Type == ManuscriptBlockType.Figure)
                    .Select(block => block.ImageId!.Value)
                .Concat(chapter.PageCompositions
                    .SelectMany(composition => composition.Variants)
                    .SelectMany(variant => CompositionSceneResolver.Flatten(variant.Scene))
                    .Where(item => item.ImageId is not null)
                    .Select(item => item.ImageId!.Value)))
            .Concat(matterDocuments
                .SelectMany(item => item.Manuscript.Content)
                .Where(block => block.Type == ManuscriptBlockType.Figure)
                .Select(block => block.ImageId!.Value))
            .Concat(validPlacements.Select(placement => placement.AssetId))
            .Concat(coverSceneImageIds)
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
                JsonSerializer.Deserialize<FigurePresentation>(placement.PresentationJson, ManuscriptCodec.JsonOptions) ?? new FigurePresentation(),
                placement.AltText,
                placement.Decorative,
                placement.Language,
                placement.AccessibilityRole,
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
        var fontFamilies = await db.ProjectFontFamilies.AsNoTracking()
            .Include(family => family.Faces)
            .Where(family => family.ProjectId == projectId)
            .OrderBy(family => family.Id)
            .ToListAsync(cancellationToken);
        var referencedFontKeys = namedStyles
            .Select(item => item.Definition.FontFamilyKey)
            .Concat(sections.SelectMany(section => section.Chapters)
                .SelectMany(chapter => chapter.PageCompositions)
                .SelectMany(composition => composition.Variants)
                .SelectMany(variant => CompositionSceneResolver.Flatten(variant.Scene).Select(item => item.FontFamilyKey)
                    .Concat(variant.Scene.Styles.Select(style => style.FontFamilyKey))))
            .Concat(coverScene is null
                ? []
                : coverScene.Objects.Select(item => item.FontFamilyKey)
                    .Concat(coverScene.Styles.Select(style => style.FontFamilyKey)))
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var referencedFontIds = referencedFontKeys
            .Where(key => key.StartsWith("project:", StringComparison.OrdinalIgnoreCase))
            .Select(key => Guid.TryParse(key["project:".Length..], out var familyId) ? familyId : Guid.Empty)
            .ToHashSet();
        foreach (var familyId in referencedFontIds)
        {
            var family = fontFamilies.FirstOrDefault(item => item.Id == familyId)
                ?? throw new InvalidOperationException($"Publication content references missing project font family '{familyId}'.");
            if (!family.EmbeddingRightsConfirmed)
                throw new InvalidOperationException($"Embedding rights must be confirmed for project font family '{family.Name}' before publication export.");
            if (family.Faces.Count == 0)
                throw new InvalidOperationException($"Project font family '{family.Name}' has no usable font faces.");
        }
        var publishFonts = fontFamilies
            .Where(family => family.EmbeddingRightsConfirmed)
            .SelectMany(family => family.Faces
                .OrderBy(face => face.Weight)
                .ThenBy(face => face.Italic)
                .ThenBy(face => face.Id)
                .Select(face => new PublishFontDocument(
                    ProjectFontService.CustomKey(family.Id),
                    family.Id,
                    family.Name,
                    face.Id,
                    face.FileName,
                    face.ContentType,
                    face.Weight,
                    face.Italic,
                    face.Data)))
            .ToList();
        foreach (var familyKey in referencedFontKeys.Where(key => key.StartsWith("builtin:", StringComparison.OrdinalIgnoreCase)).Order())
        {
            var family = PublicationBuiltInFonts.Find(familyKey)
                ?? throw new InvalidOperationException($"Publication content references unsupported bundled font '{familyKey}'.");
            var familyId = DeterministicFontId(family.Key);
            foreach (var faceView in family.Faces)
            {
                var face = await projectFonts.ResolveFaceAsync(projectId, family.Key, faceView.Weight, faceView.Italic, requireExact: true, cancellationToken)
                    ?? throw new InvalidOperationException($"Bundled publication font '{family.Name}' is missing {faceView.SubfamilyName}.");
                publishFonts.Add(new PublishFontDocument(
                    family.Key,
                    familyId,
                    family.Name,
                    DeterministicFontId($"{family.Key}|{face.Weight}|{face.Italic}"),
                    face.FileName,
                    face.ContentType,
                    face.Weight,
                    face.Italic,
                    face.Data));
            }
        }

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
            Fonts = publishFonts,
            Matter = matterDocuments,
            Cover = coverDesign is null || coverScene is null
                ? null
                : new PublishCoverDocument(
                    coverDesign.Title,
                    coverDesign.Subtitle,
                    coverDesign.Author,
                    coverDesign.SpineText,
                    coverDesign.BackCopy,
                    coverDesign.BackgroundColor,
                    coverScene),
        };
    }

    private static Guid DeterministicFontId(string value) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));

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
        IReadOnlyList<PublicationEditionOutlineItem> selections)
    {
        var manuscript = chapter.Manuscript;
        return new(
            chapter.Id,
            chapter.ActId,
            chapter.Title,
            IsIncluded(selections, PublishOutlineTargetKind.Chapter, chapter.Id),
            manuscript.Content.Count(block => block.Type == ManuscriptBlockType.Figure),
            manuscript.Content.Count(block => block.Type == ManuscriptBlockType.DesignedPage),
            0);
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
            JsonSerializer.Deserialize<FigurePresentation>(placement.PresentationJson, ManuscriptCodec.JsonOptions) ?? new FigurePresentation(),
            placement.AltText,
            placement.Decorative,
            placement.Language,
            placement.AccessibilityRole,
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

    private static PublishChapterDocument ChapterDocument(
        Chapter chapter,
        PublicationEdition profile,
        int chapterNumber,
        IReadOnlyList<PageComposition> compositions)
    {
        return new(
            chapter.Id,
            chapter.ActId,
            profile.NumberChapters ? $"Chapter {chapterNumber}: {chapter.Title}" : chapter.Title,
            chapter.PlainText,
            chapter.Synopsis,
            chapterNumber - 1,
            profile.IncludeChapterHeadings,
            chapter.Manuscript,
            compositions.Select(composition => new PublishPageCompositionDocument(
                composition.Id,
                composition.Name,
                ManuscriptCodec.Deserialize(composition.SemanticManuscriptJson, composition.Id, composition.Revision),
                composition.Revision,
                composition.Variants
                    .Where(variant => CompositionService.VariantMatchesEdition(variant, profile))
                    .OrderByDescending(variant => variant.UpdatedAt)
                    .Take(1)
                    .Select(variant => new PublishPageCompositionVariantDocument(
                    variant.Id,
                    variant.GeometryKey,
                    JsonSerializer.Deserialize<CompositionScene>(variant.SceneJson, ManuscriptCodec.JsonOptions)
                        ?? throw new InvalidOperationException($"Page composition {composition.Id:N} has no scene."),
                    variant.Revision)).ToList())).ToList());
    }

    private static PublishAssetDocument AssetDocument(PublishAsset asset) =>
        new(asset.Id, asset.FileName, asset.ContentType, asset.Data, asset.AltText);

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
        string PresentationJson,
        string AltText,
        bool Decorative,
        string Language,
        FigureAccessibilityRole AccessibilityRole,
        int SortOrder);

    private sealed record SectionSource(Act? Act, int SortOrder);
}
