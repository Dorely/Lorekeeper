using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lorekeeper.Authoring;
using Lorekeeper.Citations;
using Lorekeeper.Composition;
using Lorekeeper.Fonts;
using Lorekeeper.ImportExport;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Publish;

public sealed class PublishService(
    IAppDatabaseOperationFactory database,
    IPublicationEditionService editions,
    IPublicationBookService books,
    IPublicationSectionService publicationSections,
    IPublicationEffectiveConfigurationResolver effectiveConfigurations,
    IProjectFontService projectFonts,
    IEnumerable<IPublishExportFormatter> formatters,
    IAuthoringMutationFence authoringFence,
    ICitationFormatter? citationFormatter = null) : IPublishService
{
    public Task<PublicationBookView> GetCoreWorkspaceAsync(
        Guid projectId,
        CancellationToken cancellationToken = default) =>
        ExecuteFenceAsync(projectId, "read the Core Book workspace", token => books.GetOrCreateAsync(projectId, token), cancellationToken);

    public Task<PublishWorkspaceView> GetWorkspaceAsync(
        Guid projectId,
        Guid editionId,
        CancellationToken cancellationToken = default) =>
        GetWorkspaceCoreAsync(projectId, editionId, includeSourceFingerprint: true, cancellationToken);

    public Task<PublishWorkspaceView> GetWorkspaceForEditingAsync(
        Guid projectId,
        Guid editionId,
        CancellationToken cancellationToken = default) =>
        GetWorkspaceCoreAsync(projectId, editionId, includeSourceFingerprint: false, cancellationToken);

    private Task<PublishWorkspaceView> GetWorkspaceCoreAsync(
        Guid projectId,
        Guid editionId,
        bool includeSourceFingerprint,
        CancellationToken cancellationToken) =>
        ExecuteFenceAsync(
            projectId,
            "read the publication workspace",
            token => BuildWorkspaceAsync(projectId, editionId, includeSourceFingerprint, token),
            cancellationToken);

    private async Task<PublishWorkspaceView> BuildWorkspaceAsync(
        Guid projectId,
        Guid editionId,
        bool includeSourceFingerprint,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var project = await GetProjectAsync(projectId, cancellationToken);
        _ = await books.GetOrCreateAsync(projectId, cancellationToken);
        var effective = await effectiveConfigurations.ResolveReleaseAsync(projectId, editionId, cancellationToken);
        var profile = effective.Edition;

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
        var selections = effective.OutlineItems;
        var publicationSectionViews = await publicationSections.ListAsync(new(projectId, editionId), cancellationToken);
        var fingerprint = includeSourceFingerprint
            ? await editions.GetSourceFingerprintAsync(projectId, editionId, cancellationToken)
            : string.Empty;
        return new PublishWorkspaceView(
            PublicationEditionService.View(project, profile),
            await editions.ListAsync(projectId, cancellationToken),
            SectionViews(acts, chapters, selections),
            fingerprint)
        {
            OverrideFields = effective.OverrideFields,
            HasContentOverrides = await db.PublicationEditionOutlineItems.AnyAsync(item => item.EditionId == editionId, cancellationToken),
            PublicationSections = publicationSectionViews,
        };
    }

    public Task<ProjectExportFile> ExportAsync(
        Guid projectId,
        Guid editionId,
        PublishExportFormat format,
        CancellationToken cancellationToken = default) =>
        ExecuteFenceAsync(
            projectId,
            "export a publication projection",
            token => ExportCoreAsync(projectId, editionId, format, token),
            cancellationToken);

    private async Task<ProjectExportFile> ExportCoreAsync(
        Guid projectId,
        Guid editionId,
        PublishExportFormat format,
        CancellationToken cancellationToken = default)
    {
        var formatter = formatters.FirstOrDefault(candidate => candidate.Format == format)
            ?? throw new InvalidOperationException($"No publish formatter is registered for {format}.");
        if (format == PublishExportFormat.Epub)
        {
            await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
            if (await databaseOperation.Db.PublicationEditions.AsNoTracking()
                .Where(edition => edition.ProjectId == projectId && edition.Id == editionId)
                .Select(edition => edition.Format)
                .SingleOrDefaultAsync(cancellationToken) != PublicationEditionFormat.Epub)
            {
                throw new InvalidOperationException(
                    "EPUB export is available only from an EPUB release so print ISBN and artifact metadata cannot leak into a digital release.");
            }
        }
        var document = await GetDocumentCoreAsync(projectId, editionId, cancellationToken);

        return new ProjectExportFile(
            FileName: ExportFileName(document, formatter.FileExtension),
            ContentType: formatter.ContentType,
            Content: await formatter.RenderAsync(document, cancellationToken));
    }

    public Task<ProjectExportFile> ExportCoreAsync(
        Guid projectId,
        PublishExportFormat format,
        CancellationToken cancellationToken = default) =>
        ExecuteFenceAsync(
            projectId,
            "export the Core Book projection",
            token => ExportCoreDocumentAsync(projectId, format, token),
            cancellationToken);

    private async Task<ProjectExportFile> ExportCoreDocumentAsync(
        Guid projectId,
        PublishExportFormat format,
        CancellationToken cancellationToken = default)
    {
        if (format == PublishExportFormat.Epub)
            throw new InvalidOperationException("Create an EPUB ebook release to prepare an EPUB file.");
        var formatter = formatters.FirstOrDefault(candidate => candidate.Format == format)
            ?? throw new InvalidOperationException($"No publish formatter is registered for {format}.");
        var document = await GetCoreDocumentCoreAsync(projectId, cancellationToken);
        return new ProjectExportFile(
            ExportFileName(document, formatter.FileExtension),
            formatter.ContentType,
            await formatter.RenderAsync(document, cancellationToken));
    }

    public Task<PublishDocument> GetDocumentAsync(
        Guid projectId,
        Guid editionId,
        CancellationToken cancellationToken = default) =>
        ExecuteFenceAsync(
            projectId,
            "capture the effective publication document",
            token => GetDocumentCoreAsync(projectId, editionId, token),
            cancellationToken);

    private async Task<PublishDocument> GetDocumentCoreAsync(
        Guid projectId,
        Guid editionId,
        CancellationToken cancellationToken = default)
    {
        var effective = await effectiveConfigurations.ResolveReleaseAsync(projectId, editionId, cancellationToken);
        var sectionViews = await publicationSections.ListAsync(new(projectId, editionId), cancellationToken);
        return await BuildDocumentAsync(
            projectId,
            editionId,
            effective.Edition,
            effective.OutlineItems,
            sectionViews.Where(item => ShouldIncludeSection(item, effective.Edition)).ToList(),
            coreTarget: false,
            cancellationToken);
    }

    public Task<PublishDocument> GetCoreDocumentAsync(
        Guid projectId,
        CancellationToken cancellationToken = default) =>
        ExecuteFenceAsync(
            projectId,
            "capture the effective Core Book document",
            token => GetCoreDocumentCoreAsync(projectId, token),
            cancellationToken);

    private async Task<PublishDocument> GetCoreDocumentCoreAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var core = await books.GetOrCreateAsync(projectId, cancellationToken);
        var book = await db.PublicationBooks.AsNoTracking()
            .Include(item => item.OutlineItems)
            .SingleAsync(item => item.ProjectId == projectId, cancellationToken);
        var profile = CoreProfile(projectId, core);
        var sectionViews = await publicationSections.ListAsync(new(projectId), cancellationToken);
        return await BuildDocumentAsync(
            projectId,
            Guid.Empty,
            profile,
            book.OutlineItems.Select(CoreOutline).ToList(),
            sectionViews.Where(item => ShouldIncludeSection(item, profile)).ToList(),
            coreTarget: true,
            cancellationToken);
    }

    private async Task<PublishDocument> BuildDocumentAsync(
        Guid projectId,
        Guid editionId,
        PublicationEdition profile,
        IReadOnlyList<PublicationEditionOutlineItem> selections,
        IReadOnlyList<PublicationSectionView> publicationSectionViews,
        bool coreTarget,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var project = await GetProjectAsync(projectId, cancellationToken);
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
        var chapterOverrides = coreTarget || !profile.EditionSpecificContentEnabled
            ? new Dictionary<Guid, PublicationEditionChapterOverride>()
            : await db.PublicationEditionChapterOverrides.AsNoTracking()
                .Where(item => item.EditionId == editionId)
                .ToDictionaryAsync(item => item.ChapterId, cancellationToken);
        foreach (var chapter in chapters)
        {
            if (!chapterOverrides.TryGetValue(chapter.Id, out var chapterOverride))
                continue;
            chapter.ManuscriptJson = chapterOverride.ManuscriptJson;
            chapter.ManuscriptRevision = chapterOverride.Revision;
        }
        var designedPages = await db.DesignedPages
            .AsNoTracking()
            .Where(page => page.ProjectId == projectId
                && (page.ScopeEditionId == null || page.ScopeEditionId == editionId))
            .Include(page => page.Contents)
            .ThenInclude(content => content.Variants)
            .ToListAsync(cancellationToken);
        var boundValues = new Dictionary<PublicationBoundField, string>();
        foreach (var field in Enum.GetValues<PublicationBoundField>())
            boundValues[field] = await publicationSections.ResolveBoundFieldAsync(new(projectId, coreTarget ? null : editionId), field, cancellationToken);
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
                    DesignedPagesForManuscript(chapter.Manuscript, designedPages, coreTarget ? null : editionId),
                    boundValues));
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
            var includeActPage = profile.IncludeActHeadings
                || profile.IncludeActSynopses && !string.IsNullOrWhiteSpace(source.Act.Synopsis);
            var title = profile.NumberActs ? $"Act {actNumber}: {source.Act.Title}" : source.Act.Title;
            sections.Add(new PublishSectionDocument(
                source.Act.Id,
                title,
                source.Act.Synopsis,
                IsUnassigned: false,
                IncludePage: includeActPage,
                IncludeHeading: profile.IncludeActHeadings,
                source.SortOrder,
                chapterDocuments));
        }

        var publicationSectionDocuments = publicationSectionViews.Select(item => new PublishPublicationSectionDocument(
            item.Id,
            item.CoreSectionId,
            item.Title,
            item.Kind,
            item.SystemRole,
            item.Anchor,
            item.TargetKind,
            item.TargetId,
            item.LocalOrder,
            item.StartSide,
            PublicationSectionService.ResolveBindings(item.Manuscript, boundValues),
            DesignedPagesForManuscript(item.Manuscript, designedPages, coreTarget ? null : editionId)
                .Select(page => DesignedPageDocument(page, profile, boundValues, coreTarget ? null : editionId)).ToList())).ToList();
        var coverDesign = coreTarget || profile.InheritsCoreCover
            ? await CoreCoverAsync(projectId, cancellationToken)
            : await db.PublicationCoverDesigns.AsNoTracking()
                .FirstOrDefaultAsync(design => design.EditionId == editionId, cancellationToken);
        var coverScene = string.IsNullOrWhiteSpace(coverDesign?.CompositionSceneJson)
            ? null
            : JsonSerializer.Deserialize<CompositionScene>(coverDesign.CompositionSceneJson, ManuscriptCodec.JsonOptions);
        if (coverScene is not null)
            coverScene = CoverCompositionFactory.KeepArtworkBehindCopy(coverScene);
        var coverSurfaceScenes = ReadCoverSurfaceScenes(coverDesign?.SurfaceScenesJson);
        var effectivePages = sections.SelectMany(section => section.Chapters).SelectMany(chapter => chapter.DesignedPages)
            .Concat(publicationSectionDocuments.SelectMany(section => section.DesignedPages)).ToList();
        var effectiveManuscripts = sections.SelectMany(section => section.Chapters).Select(chapter => chapter.Manuscript)
            .Concat(publicationSectionDocuments.Select(section => section.Manuscript))
            .Concat(effectivePages.Select(page => page.SemanticManuscript)).ToList();
        var effectiveScenes = effectivePages.SelectMany(page => page.Variants).Select(variant => variant.Scene)
            .Concat(coverScene is null ? [] : new[] { coverScene }).Concat(coverSurfaceScenes.Values).ToList();
        var referencedAssetIds = effectiveManuscripts.SelectMany(ManuscriptTraversal.EnumerateBlocks)
            .Where(block => block.Type == ManuscriptBlockType.Figure && block.ImageId.HasValue).Select(block => block.ImageId!.Value)
            .Concat(effectiveScenes.SelectMany(CompositionSceneResolver.Flatten)
                .Where(item => item.ImageId.HasValue).Select(item => item.ImageId!.Value))
            .Concat(profile.SelectedCoverImageId is Guid coverImageId ? [coverImageId] : [])
            .ToHashSet();
        var assets = referencedAssetIds.Count == 0
            ? new Dictionary<Guid, PublishAsset>()
            : await db.PublishAssets
                .AsNoTracking()
                .Where(asset => asset.ProjectId == projectId && referencedAssetIds.Contains(asset.Id))
                .ToDictionaryAsync(asset => asset.Id, cancellationToken);

        var cover = profile.SelectedCoverImageId is Guid selectedCoverImageId
            && assets.TryGetValue(selectedCoverImageId, out var selectedCoverImage)
                ? AssetDocument(selectedCoverImage)
                : null;
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
                return new PublishManuscriptStyleDocument(style.Name, style.Kind, style.SemanticRole, definition);
            })
            .ToList();
        var referencedFontKeys = namedStyles
            .Select(item => item.Definition.FontFamilyKey)
            .Concat(effectiveManuscripts.SelectMany(ManuscriptTraversal.EnumerateBlocks)
                .Select(block => block.ParagraphPresentation?.FontFamilyKey))
            .Concat(effectiveScenes.SelectMany(scene => CompositionSceneResolver.Flatten(scene).Select(item => item.FontFamilyKey)
                .Concat(scene.Styles.Select(style => style.FontFamilyKey))))
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var referencedFontIds = referencedFontKeys
            .Where(key => key.StartsWith("project:", StringComparison.OrdinalIgnoreCase))
            .Select(key => Guid.TryParse(key["project:".Length..], out var familyId) ? familyId : Guid.Empty)
            .ToHashSet();
        var fontFamilies = await db.ProjectFontFamilies.AsNoTracking()
            .Include(family => family.Faces)
            .Where(family => family.ProjectId == projectId && referencedFontIds.Contains(family.Id))
            .OrderBy(family => family.Id)
            .ToListAsync(cancellationToken);
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

        var bibliography = (await db.BibliographicRecords.AsNoTracking()
            .Where(record => record.ProjectId == projectId)
            .OrderBy(record => record.Id)
            .ToListAsync(cancellationToken))
            .Select(CitationRecord.FromEntity)
            .ToList();
        var document = new PublishDocument(
            editionId,
            project.Id,
            project.Name,
            project.Slug,
            DateTime.UtcNow,
            ProfileDocument(profile),
            cover,
            sections,
            assets.Values.Select(AssetDocument).ToList())
        {
            NamedStyles = namedStyles,
            Fonts = publishFonts,
            PublicationSections = publicationSectionDocuments,
            CoverSurfaceScenes = coverSurfaceScenes,
            Cover = coverDesign is null || coverScene is null
                ? null
                : new PublishCoverDocument(
                    coverDesign.Title,
                    coverDesign.Subtitle,
                    coverDesign.Author,
                    coverDesign.SpineText,
                    profile.Description,
                    coverDesign.BackgroundColor,
                    coverScene),
            CitationStyle = profile.CitationStyle,
            BibliographicRecords = bibliography,
        };
        var occurrences = PublicationCitationTraversal.Enumerate(document);
        var sourceLocationIds = occurrences.SelectMany(item => item.Cluster.Items)
            .Where(item => item.SourceLocationId.HasValue)
            .Select(item => item.SourceLocationId!.Value)
            .Distinct()
            .ToList();
        if (sourceLocationIds.Count > 0)
        {
            var validLocationIds = await db.SourceLocations.AsNoTracking()
                .Where(location => location.ProjectId == projectId && sourceLocationIds.Contains(location.Id))
                .Select(location => location.Id)
                .ToListAsync(cancellationToken);
            var missingLocationIds = sourceLocationIds.Except(validLocationIds).ToList();
            if (missingLocationIds.Count > 0)
                throw new InvalidDataException($"Publication citations reference missing source locations: {string.Join(", ", missingLocationIds)}.");
        }
        return document with
        {
            Citations = (citationFormatter ?? new CitationFormatter()).Format(
                profile.CitationStyle,
                bibliography,
                occurrences),
        };
    }

    private static IReadOnlyDictionary<string, CompositionScene> ReadCoverSurfaceScenes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, CompositionScene>();
        var serialized = JsonSerializer.Deserialize<Dictionary<string, string>>(json, ManuscriptCodec.JsonOptions)
            ?? new Dictionary<string, string>();
        return serialized.ToDictionary(
            item => item.Key,
            item => CoverCompositionFactory.KeepArtworkBehindCopy(
                JsonSerializer.Deserialize<CompositionScene>(item.Value, ManuscriptCodec.JsonOptions)
                    ?? throw new InvalidDataException($"Cover surface '{item.Key}' is empty.")),
            StringComparer.Ordinal);
    }

    private async Task<PublicationCoverDesign?> CoreCoverAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var book = await db.PublicationBooks.AsNoTracking()
            .Include(item => item.CoverDesign)
            .SingleOrDefaultAsync(item => item.ProjectId == projectId, cancellationToken);
        var cover = book?.CoverDesign;
        return cover is null ? null : new PublicationCoverDesign
        {
            EditionId = Guid.Empty,
            Title = book!.Title,
            Subtitle = book.Subtitle,
            Author = book.Author,
            BackgroundColor = cover.BackgroundColor,
            CompositionSceneJson = cover.CompositionSceneJson,
            Revision = cover.Revision,
        };
    }

    internal static PublicationEdition CoreProfile(Guid projectId, PublicationBookView core) => new()
    {
        ProjectId = projectId,
        Name = "Core Book",
        Format = PublicationEditionFormat.DigitalPdf,
        Vendor = PublicationVendor.Generic,
        VendorProfileVersion = PublicationRenderProcessor.ProfileFor(PublicationEditionFormat.DigitalPdf, PublicationVendor.Generic),
        TitleOverride = core.Title,
        Subtitle = core.Subtitle,
        Author = core.Author,
        Language = PublicationLanguage.Normalize(core.Language),
        Publisher = core.Publisher,
        Copyright = core.Copyright,
        Description = core.Description,
        IncludeTableOfContents = core.IncludeTableOfContents,
        IncludeVisibleTableOfContents = core.IncludeVisibleTableOfContents,
        IncludeActSynopses = core.IncludeActSynopses,
        IncludeChapterSynopses = core.IncludeChapterSynopses,
        IncludeActHeadings = core.IncludeActHeadings,
        IncludeChapterHeadings = core.IncludeChapterHeadings,
        NumberActs = core.NumberActs,
        NumberChapters = core.NumberChapters,
        TitlePageMode = core.TitlePageMode,
        CitationStyle = core.CitationStyle,
        RectoChapterStarts = core.RectoChapterStarts,
        AllowDesignedPageOverrides = core.AllowDesignedPageOverrides,
        PageWidthInches = core.PageSetup.PageWidthInches,
        PageHeightInches = core.PageSetup.PageHeightInches,
        PageMarginInches = core.PageSetup.PageMarginInches,
        BodyFontSizePoints = core.PageSetup.BodyFontSizePoints,
        BodyLineHeight = core.PageSetup.BodyLineHeight,
    };

    private static PublicationEditionOutlineItem CoreOutline(PublicationBookOutlineItem item) => new()
    {
        EditionId = Guid.Empty,
        TargetKind = item.TargetKind,
        TargetId = item.TargetId,
        IsIncluded = item.IsIncluded,
        SortOrder = item.SortOrder,
    };

    private static Guid DeterministicFontId(string value) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));

    private async Task<Project> GetProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        return await db.Projects.FirstOrDefaultAsync(project => project.Id == projectId, cancellationToken)
                ?? throw new InvalidOperationException($"Project {projectId} not found.");
    }
    private static PublishDocumentProfile ProfileDocument(PublicationEdition profile) =>
        new(
            profile.TitleOverride,
            profile.Subtitle,
            profile.Author,
            PublicationLanguage.Normalize(profile.Language),
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
            profile.BodyLineHeight)
        {
            AllowDesignedPageOverrides = profile.AllowDesignedPageOverrides,
            RectoChapterStarts = profile.RectoChapterStarts,
            BleedInches = profile.Bleed
                && profile.Format is PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover
                    ? .125
                    : 0,
        };

    private static bool IncludeTitlePage(PublicationEdition profile) => profile.TitlePageMode switch
    {
        PublishTitlePageMode.Include => true,
        PublishTitlePageMode.Omit => false,
        _ => true,
    };

    private static bool ShouldIncludeSection(PublicationSectionView section, PublicationEdition profile)
    {
        if (!section.IsIncluded || section.InclusionMode == PublicationSectionInclusionMode.Omitted)
            return false;
        if (section.InclusionMode == PublicationSectionInclusionMode.Included)
            return true;
        return section.SystemRole switch
        {
            PublicationSectionSystemRole.Title => IncludeTitlePage(profile),
            PublicationSectionSystemRole.Copyright => !string.IsNullOrWhiteSpace(profile.Copyright),
            PublicationSectionSystemRole.Contents => profile.IncludeVisibleTableOfContents,
            _ => true,
        };
    }

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
                IsIncluded: true,
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
            ManuscriptTraversal.EnumerateBlocks(manuscript).Count(block => block.Type == ManuscriptBlockType.Figure),
            manuscript.Content.Count(block => block.Type == ManuscriptBlockType.DesignedPage),
            0);
    }

    private static PublishChapterDocument ChapterDocument(
        Chapter chapter,
        PublicationEdition profile,
        int chapterNumber,
        IReadOnlyList<DesignedPage> pages,
        IReadOnlyDictionary<PublicationBoundField, string> boundValues)
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
            pages.Select(page => DesignedPageDocument(page, profile, boundValues, profile.Id == Guid.Empty ? null : profile.Id)).ToList());
    }

    private static PublishDesignedPageDocument DesignedPageDocument(
        DesignedPage page,
        PublicationEdition profile,
        IReadOnlyDictionary<PublicationBoundField, string> boundValues,
        Guid? editionId)
    {
        var content = page.Contents.SingleOrDefault(item => item.EditionId == editionId)
            ?? page.Contents.SingleOrDefault(item => item.EditionId is null)
            ?? throw new InvalidOperationException($"Designed Page {page.Id:N} has no effective content.");
        return new(
            page.Id,
            page.Name,
            PublicationSectionService.ResolveBindings(
                ManuscriptCodec.Deserialize(content.SemanticManuscriptJson, content.Id, content.Revision),
                boundValues),
            content.AccessibilityDescription,
            content.Revision,
            content.Variants
                .Where(variant => DesignedPageService.VariantMatchesEdition(variant, profile))
                .OrderByDescending(variant => content.ActiveVariantId == variant.Id)
                .ThenByDescending(variant => variant.UpdatedAt)
                .Take(1)
                .Select(variant => new PublishDesignedPageVariantDocument(
                    variant.Id,
                    variant.GeometryKey,
                    NormalizeSceneLanguages(PdfPresentationScene(
                        JsonSerializer.Deserialize<CompositionScene>(variant.SceneJson, ManuscriptCodec.JsonOptions)
                            ?? throw new InvalidOperationException($"Designed Page {page.Id:N} has no scene."),
                        profile)),
                    variant.Revision))
                .ToList());
    }

    private static IReadOnlyList<DesignedPage> DesignedPagesForManuscript(
        ManuscriptDocument manuscript,
        IReadOnlyList<DesignedPage> pages,
        Guid? editionId)
    {
        var byId = pages.ToDictionary(page => page.Id);
        return manuscript.Content
            .Where(block => block.Type == ManuscriptBlockType.DesignedPage)
            .Select(block => block.DesignedPageId is Guid pageId && byId.TryGetValue(pageId, out var page)
                && (page.ScopeEditionId is null || page.ScopeEditionId == editionId)
                    ? page
                    : throw new InvalidOperationException($"Designed Page block {block.Id} references a page that is unavailable for this publication target."))
            .ToList();
    }

    private static CompositionScene NormalizeSceneLanguages(CompositionScene scene) => scene with
    {
        Objects = scene.Objects.Select(item => item with
        {
            Language = PublicationLanguage.NormalizeOptional(item.Language) ?? string.Empty,
        }).ToArray(),
    };

    private static CompositionScene PdfPresentationScene(CompositionScene scene, PublicationEdition profile)
    {
        if (profile.Format != PublicationEditionFormat.DigitalPdf
            || !profile.AllowDesignedPageOverrides
            || scene.Surface.Kind is not (CompositionSurfaceKind.FacingSpread or CompositionSurfaceKind.IndependentPage))
            return scene;

        return scene with
        {
            Surface = scene.Surface with
            {
                OutputPageMode = CompositionOutputPageMode.SingleSurface,
                AllowIndependentPdfPage = true,
            },
        };
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

    private Task<T> ExecuteFenceAsync<T>(
        Guid projectId,
        string purpose,
        Func<CancellationToken, Task<T>> consume,
        CancellationToken cancellationToken)
    {
        if (projectId == Guid.Empty)
            throw new ArgumentException("A project ID is required.", nameof(projectId));
        return authoringFence.ExecuteAsync(
            new AuthoringFenceRequest(projectId, [], purpose),
            (_, token) => consume(token),
            cancellationToken);
    }

    private sealed record SectionSource(Act? Act, int SortOrder);
}
