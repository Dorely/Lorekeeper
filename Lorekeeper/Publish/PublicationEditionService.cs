using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lorekeeper.Composition;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Publish;

public sealed class PublicationEditionService(
    AppDbContext db,
    IProjectMutationCoordinator projectMutations,
    IPublicationActorContext actorContext) : IPublicationEditionService
{
    public async Task<IReadOnlyList<PublicationEditionSummary>> ListAsync(
        Guid projectId,
        CancellationToken cancellationToken = default) =>
        await db.PublicationEditions
            .AsNoTracking()
            .Where(edition => edition.ProjectId == projectId)
            .OrderByDescending(edition => edition.IsDefault)
            .ThenBy(edition => edition.Status)
            .ThenBy(edition => edition.Name)
            .Select(edition => Summary(edition))
            .ToListAsync(cancellationToken);

    public async Task<PublicationEditionView> CreateAsync(
        Guid projectId,
        PublicationEditionCreate input,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(input.Name, input.Format, input.Vendor);
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var project = await GetProjectAsync(projectId, cancellationToken);
        var name = input.Name.Trim();
        if (await db.PublicationEditions.AnyAsync(
            edition => edition.ProjectId == projectId && edition.Name == name,
            cancellationToken))
        {
            throw new InvalidOperationException($"A publication edition named '{name}' already exists.");
        }
        var isFirst = !await db.PublicationEditions.AnyAsync(
            edition => edition.ProjectId == projectId,
            cancellationToken);
        var defaultGeometry = input.Format == PublicationEditionFormat.DigitalPdf
            ? await db.PublicationEditions.AsNoTracking()
                .Where(edition => edition.ProjectId == projectId && edition.IsDefault)
                .Select(edition => new { edition.PageWidthInches, edition.PageHeightInches, edition.PageMarginInches })
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        var edition = new PublicationEdition
        {
            ProjectId = projectId,
            Name = name,
            Format = input.Format,
            Vendor = input.Vendor,
            VendorProfileVersion = DefaultProfile(input.Format, input.Vendor),
            IsDefault = isFirst,
            TitleOverride = project.Name,
            Binding = input.Format != PublicationEditionFormat.Paperback
                ? PublicationBinding.Digital
                : PublicationBinding.PerfectBound,
            Paper = input.Format != PublicationEditionFormat.Paperback
                ? PublicationPaper.Digital
                : PublicationPaper.White,
            Ink = input.Format != PublicationEditionFormat.Paperback
                ? PublicationInk.Digital
                : PublicationInk.BlackAndWhite,
            PageWidthInches = input.Format == PublicationEditionFormat.Epub ? 8.5 : defaultGeometry?.PageWidthInches ?? 6,
            PageHeightInches = input.Format == PublicationEditionFormat.Epub ? 11 : defaultGeometry?.PageHeightInches ?? 9,
            PageMarginInches = defaultGeometry?.PageMarginInches ?? 0.75,
            BodyFontSizePoints = input.Format == PublicationEditionFormat.Paperback ? 11 : 12,
            BodyLineHeight = input.Format == PublicationEditionFormat.Paperback ? 1.4 : 1.55,
            Bleed = false,
        };
        db.PublicationEditions.Add(edition);
        await MaterializeOutlineAsync(edition, cancellationToken);
        await SaveWithAuditAsync(edition, "create", string.Empty, new { input.Name, input.Format, input.Vendor }, cancellationToken);
        return View(project, edition);
    }

    internal static string DefaultProfile(PublicationEditionFormat format, PublicationVendor vendor) =>
        format == PublicationEditionFormat.Epub
            ? "epub3-v1"
            : PublicationRenderProcessor.ProfileFor(format, vendor);

    public async Task<PublicationEditionView> CloneAsync(
        Guid projectId,
        Guid editionId,
        string name,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var project = await GetProjectAsync(projectId, cancellationToken);
        var source = await GetTrackedAsync(projectId, editionId, cancellationToken);
        EnsureRevision(source, expectedRevision);
        var cleanName = name.Trim();
        ValidateIdentity(cleanName, source.Format, source.Vendor);
        if (await db.PublicationEditions.AnyAsync(
            edition => edition.ProjectId == projectId && edition.Name == cleanName,
            cancellationToken))
        {
            throw new InvalidOperationException($"A publication edition named '{cleanName}' already exists.");
        }

        await db.Entry(source).Collection(edition => edition.OutlineItems).LoadAsync(cancellationToken);
        await db.Entry(source).Collection(edition => edition.Matter).LoadAsync(cancellationToken);
        await db.Entry(source).Collection(edition => edition.StyleMappings).LoadAsync(cancellationToken);
        await db.Entry(source).Collection(edition => edition.ImagePlacements).LoadAsync(cancellationToken);
        await db.Entry(source).Reference(edition => edition.CoverDesign).LoadAsync(cancellationToken);
        var clone = CopyEdition(source, cleanName);
        clone.Isbn = string.Empty;
        clone.OutlineItems = source.OutlineItems.Select(CopyOutlineItem).ToList();
        clone.Matter = source.Matter.Select(CopyMatter).ToList();
        clone.StyleMappings = source.StyleMappings.Select(CopyStyleMapping).ToList();
        clone.ImagePlacements = source.ImagePlacements.Select(CopyPlacement).ToList();
        if (source.CoverDesign is not null)
            clone.CoverDesign = new PublicationCoverDesign
            {
                EditionId = clone.Id,
                Title = source.CoverDesign.Title,
                Subtitle = source.CoverDesign.Subtitle,
                Author = source.CoverDesign.Author,
                SpineText = source.CoverDesign.SpineText,
                BackCopy = source.CoverDesign.BackCopy,
                BackgroundColor = source.CoverDesign.BackgroundColor,
                BarcodeMode = source.CoverDesign.BarcodeMode,
                ImageCropXPercent = source.CoverDesign.ImageCropXPercent,
                ImageCropYPercent = source.CoverDesign.ImageCropYPercent,
                CompositionSceneJson = source.CoverDesign.CompositionSceneJson,
            };
        db.PublicationEditions.Add(clone);
        await SaveWithAuditAsync(clone, "clone", string.Empty, new { sourceEditionId = source.Id }, cancellationToken);
        return View(project, clone);
    }

    public async Task<PublicationEditionView> UpdateAsync(
        Guid projectId,
        Guid editionId,
        PublicationEditionUpdate input,
        CancellationToken cancellationToken = default)
    {
        ValidateUpdate(input);
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var project = await GetProjectAsync(projectId, cancellationToken);
        var edition = await GetTrackedAsync(projectId, editionId, cancellationToken);
        EnsureRevision(edition, input.ExpectedRevision);
        EnsureDraft(edition);
        if (edition.Format != input.Format)
            throw new InvalidOperationException("An edition's product format cannot be changed after creation because its cover and page compositions are format-aware. Create a new edition for the other format.");
        ValidateProductCombination(input, edition);
        var before = await FingerprintAsync(projectId, editionId, cancellationToken);
        var name = input.Name.Trim();
        var normalizedIsbn = PublicationIsbn.NormalizeValidOrEmpty(input.Isbn);
        if (!string.Equals(name, edition.Name, StringComparison.Ordinal)
            && await db.PublicationEditions.AnyAsync(
                candidate => candidate.ProjectId == projectId
                    && candidate.Id != editionId
                    && candidate.Name == name,
                cancellationToken))
        {
            throw new InvalidOperationException($"A publication edition named '{name}' already exists.");
        }
        var sharedIsbnEditions = normalizedIsbn.Length == 0
            ? []
            : await db.PublicationEditions.AsNoTracking()
                .Where(candidate => candidate.ProjectId == projectId
                    && candidate.Id != editionId
                    && candidate.Isbn == normalizedIsbn)
                .ToListAsync(cancellationToken);
        if (sharedIsbnEditions.Any(candidate =>
            candidate.Format != input.Format
            || !BibliographicProductSettingsMatch(candidate, input)))
        {
            throw new InvalidOperationException(
                "An ISBN-13 can be shared only by vendor editions with matching bibliographic content and product-form settings.");
        }
        if (sharedIsbnEditions.Count > 0)
        {
            var contentHash = await BibliographicContentHashAsync(editionId, cancellationToken);
            foreach (var candidate in sharedIsbnEditions)
            {
                if (!string.Equals(
                    contentHash,
                    await BibliographicContentHashAsync(candidate.Id, cancellationToken),
                    StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Vendor editions sharing an ISBN-13 must contain the same ordered manuscript and publication matter.");
                }
            }
        }

        edition.Name = name;
        edition.Format = input.Format;
        edition.Vendor = input.Vendor;
        edition.VendorProfileVersion = Clean(input.VendorProfileVersion);
        edition.Binding = input.Binding;
        edition.Paper = input.Paper;
        edition.Ink = input.Ink;
        edition.Bleed = input.Bleed;
        edition.AllowDesignedPageOverrides = input.Format == PublicationEditionFormat.DigitalPdf
            && input.AllowDesignedPageOverrides;
        edition.TitleOverride = Clean(input.TitleOverride);
        edition.Subtitle = Clean(input.Subtitle);
        edition.Author = Clean(input.Author);
        edition.Language = string.IsNullOrWhiteSpace(input.Language) ? "en" : Clean(input.Language);
        edition.Publisher = Clean(input.Publisher);
        edition.Copyright = Clean(input.Copyright);
        edition.Isbn = normalizedIsbn;
        edition.Description = Clean(input.Description);
        edition.IncludeTableOfContents = input.IncludeTableOfContents;
        edition.IncludeVisibleTableOfContents = input.IncludeVisibleTableOfContents;
        edition.IncludeActSynopses = input.IncludeActSynopses;
        edition.IncludeChapterSynopses = input.IncludeChapterSynopses;
        edition.IncludeActHeadings = input.IncludeActHeadings;
        edition.IncludeChapterHeadings = input.IncludeChapterHeadings;
        edition.NumberActs = input.NumberActs;
        edition.NumberChapters = input.NumberChapters;
        edition.TitlePageMode = input.TitlePageMode;
        edition.PageWidthInches = input.PageWidthInches;
        edition.PageHeightInches = input.PageHeightInches;
        edition.PageMarginInches = input.PageMarginInches;
        edition.BodyFontSizePoints = input.BodyFontSizePoints;
        edition.BodyLineHeight = input.BodyLineHeight;
        await SaveWithAuditAsync(edition, "update", before, new { }, cancellationToken);
        return View(project, edition);
    }

    public async Task ArchiveAsync(
        Guid projectId,
        Guid editionId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var edition = await GetTrackedAsync(projectId, editionId, cancellationToken);
        EnsureRevision(edition, expectedRevision);
        EnsureDraft(edition);
        var before = await FingerprintAsync(projectId, editionId, cancellationToken);
        edition.Status = PublicationEditionStatus.Archived;
        if (edition.IsDefault)
        {
            edition.IsDefault = false;
            var replacement = await db.PublicationEditions
                .Where(candidate => candidate.ProjectId == projectId
                    && candidate.Id != editionId
                    && candidate.Status == PublicationEditionStatus.Draft)
                .OrderBy(candidate => candidate.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (replacement is not null)
            {
                replacement.IsDefault = true;
                replacement.Revision++;
                replacement.UpdatedAt = DateTime.UtcNow;
            }
        }
        await SaveWithAuditAsync(edition, "archive", before, new { }, cancellationToken);
    }

    public async Task<PublicationEditionView> SetDefaultAsync(
        Guid projectId,
        Guid editionId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var project = await GetProjectAsync(projectId, cancellationToken);
        var edition = await GetTrackedAsync(projectId, editionId, cancellationToken);
        EnsureRevision(edition, expectedRevision);
        EnsureDraft(edition);
        var before = await FingerprintAsync(projectId, editionId, cancellationToken);
        var current = await db.PublicationEditions.FirstOrDefaultAsync(
            candidate => candidate.ProjectId == projectId && candidate.IsDefault,
            cancellationToken);
        if (current is not null && current.Id != editionId)
        {
            current.IsDefault = false;
            current.Revision++;
            current.UpdatedAt = DateTime.UtcNow;
        }
        edition.IsDefault = true;
        await SaveWithAuditAsync(edition, "set-default", before, new { }, cancellationToken);
        return View(project, edition);
    }

    public async Task<PublicationEditionView> SetOutlineSelectionsAsync(
        Guid projectId,
        Guid editionId,
        IReadOnlyList<PublicationEditionOutlineItemUpdate> updates,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var project = await GetProjectAsync(projectId, cancellationToken);
        var edition = await GetTrackedAsync(projectId, editionId, cancellationToken);
        EnsureRevision(edition, expectedRevision);
        EnsureDraft(edition);
        await EnsureSharedIsbnContentMutableAsync(edition, cancellationToken);
        await EnsureTargetsAsync(projectId, updates, cancellationToken);
        var before = await FingerprintAsync(projectId, editionId, cancellationToken);
        var existing = await db.PublicationEditionOutlineItems
            .Where(item => item.EditionId == editionId)
            .ToListAsync(cancellationToken);
        foreach (var update in updates
            .GroupBy(update => (update.TargetKind, update.TargetId))
            .Select(group => group.Last()))
        {
            var item = existing.FirstOrDefault(candidate =>
                candidate.TargetKind == update.TargetKind && candidate.TargetId == update.TargetId);
            if (item is null)
            {
                item = NewOutlineItem(editionId, update.TargetKind, update.TargetId, existing.Count);
                db.PublicationEditionOutlineItems.Add(item);
                existing.Add(item);
            }
            item.IsIncluded = update.IsIncluded;
            item.UpdatedAt = DateTime.UtcNow;
        }
        await SaveWithAuditAsync(edition, "set-content", before, new { count = updates.Count }, cancellationToken);
        return View(project, edition);
    }

    public async Task<PublicationEditionView> ReorderOutlineAsync(
        Guid projectId,
        Guid editionId,
        IReadOnlyList<PublicationEditionOutlineItemOrder> orderedItems,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var project = await GetProjectAsync(projectId, cancellationToken);
        var edition = await GetTrackedAsync(projectId, editionId, cancellationToken);
        EnsureRevision(edition, expectedRevision);
        EnsureDraft(edition);
        await EnsureSharedIsbnContentMutableAsync(edition, cancellationToken);

        var acts = await db.Acts.AsNoTracking()
            .Where(act => act.ProjectId == projectId)
            .Select(act => act.Id)
            .ToListAsync(cancellationToken);
        var chapters = await db.Chapters.AsNoTracking()
            .Where(chapter => chapter.ProjectId == projectId)
            .Select(chapter => new { chapter.Id, chapter.ActId })
            .ToListAsync(cancellationToken);
        var chapterParents = chapters.ToDictionary(chapter => chapter.Id, chapter => chapter.ActId);
        ValidateOutlineOrder(orderedItems, acts, chapterParents);
        var before = await FingerprintAsync(projectId, editionId, cancellationToken);
        var existing = await db.PublicationEditionOutlineItems
            .Where(item => item.EditionId == editionId)
            .ToListAsync(cancellationToken);
        for (var sortOrder = 0; sortOrder < orderedItems.Count; sortOrder++)
        {
            var ordered = orderedItems[sortOrder];
            var item = existing.FirstOrDefault(candidate =>
                candidate.TargetKind == ordered.TargetKind && candidate.TargetId == ordered.TargetId);
            if (item is null)
            {
                item = NewOutlineItem(editionId, ordered.TargetKind, ordered.TargetId, sortOrder);
                db.PublicationEditionOutlineItems.Add(item);
                existing.Add(item);
            }
            item.SortOrder = sortOrder;
            item.UpdatedAt = DateTime.UtcNow;
        }

        await SaveWithAuditAsync(edition, "reorder-content", before, new { count = orderedItems.Count }, cancellationToken);
        return View(project, edition);
    }

    internal static void ValidateOutlineOrder(
        IReadOnlyList<PublicationEditionOutlineItemOrder> orderedItems,
        IReadOnlyCollection<Guid> actIds,
        IReadOnlyDictionary<Guid, Guid?> chapterParents)
    {
        var expected = actIds
            .Select(id => new PublicationEditionOutlineItemOrder(PublishOutlineTargetKind.Act, id))
            .Concat(chapterParents.Keys.Select(id =>
                new PublicationEditionOutlineItemOrder(PublishOutlineTargetKind.Chapter, id)))
            .ToHashSet();
        if (orderedItems.Count != expected.Count
            || orderedItems.Distinct().Count() != orderedItems.Count
            || !orderedItems.All(expected.Contains))
        {
            throw new InvalidOperationException("Edition content order must contain every current act and chapter exactly once.");
        }
        Guid? currentActId = null;
        var reachedUnassigned = false;
        foreach (var ordered in orderedItems)
        {
            if (ordered.TargetKind == PublishOutlineTargetKind.Act)
            {
                if (reachedUnassigned)
                    throw new InvalidOperationException("Act groups cannot appear after unassigned chapters.");
                currentActId = ordered.TargetId;
                continue;
            }

            var parentActId = chapterParents[ordered.TargetId];
            if (parentActId is null)
            {
                reachedUnassigned = true;
                currentActId = null;
            }
            else if (reachedUnassigned || currentActId != parentActId)
            {
                throw new InvalidOperationException("Every chapter must remain contiguous beneath its owning act.");
            }
        }
    }

    public async Task<PublicationMatterView> UpsertMatterAsync(
        Guid projectId,
        Guid editionId,
        PublicationMatterInput input,
        long expectedEditionRevision,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(input.Location) || !Enum.IsDefined(input.Kind))
            throw new InvalidOperationException("Publication matter kind or location is invalid.");
        PublicationMatterFormatting.EnsureUserAuthoredKind(input.Kind);
        if (string.IsNullOrWhiteSpace(input.Title)
            || input.Title.Trim().Length > 500
            || input.Title.Contains('\r')
            || input.Title.Contains('\n'))
        {
            throw new InvalidOperationException("Publication matter title must contain 1 to 500 characters on one line.");
        }
        if (input.SortOrder < 0)
            throw new InvalidOperationException("Publication matter order cannot be negative.");
        var document = ManuscriptCodec.Deserialize(
            input.ManuscriptJson,
            input.Id ?? Guid.Empty,
            input.ExpectedRevision ?? 0);
        if (document.Content.Any(block => block.Type == ManuscriptBlockType.DesignedPage))
            throw new InvalidOperationException("Publication matter cannot own Designed Pages; insert them in a chapter where their composition has explicit semantic ownership.");
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var edition = await GetTrackedAsync(projectId, editionId, cancellationToken);
        EnsureRevision(edition, expectedEditionRevision);
        EnsureDraft(edition);
        await EnsureSharedIsbnContentMutableAsync(edition, cancellationToken);
        var styleEntities = await db.ManuscriptStyleDefinitions.AsNoTracking()
            .Where(style => style.ProjectId == projectId)
            .ToListAsync(cancellationToken);
        ManuscriptStyleService.ValidateDocumentReferences(
            document,
            styleEntities.Select(style => new ManuscriptStyleView(
                style.Id,
                style.Name,
                style.Kind,
                style.SemanticRole,
                ManuscriptStyleService.NormalizeDefinition(
                    JsonSerializer.Deserialize<ManuscriptStyleProperties>(
                        style.DefinitionJson,
                        ManuscriptCodec.JsonOptions) ?? new ManuscriptStyleProperties()),
                style.Revision)).ToList());
        var figureImageIds = document.Content
            .Where(block => block.Type == ManuscriptBlockType.Figure && block.ImageId is not null)
            .Select(block => block.ImageId!.Value)
            .Distinct()
            .ToList();
        if (figureImageIds.Count > 0
            && await db.PublishAssets.AsNoTracking().CountAsync(
                image => image.ProjectId == projectId && figureImageIds.Contains(image.Id),
                cancellationToken) != figureImageIds.Count)
        {
            throw new InvalidOperationException(
                "Publication matter figures must reference images owned by this project.");
        }
        var before = await FingerprintAsync(projectId, editionId, cancellationToken);
        PublicationMatter matter;
        if (input.Id is Guid id)
        {
            matter = await db.PublicationMatter.FirstOrDefaultAsync(
                candidate => candidate.EditionId == editionId && candidate.Id == id,
                cancellationToken)
                ?? throw new InvalidOperationException("Publication matter was not found.");
            if (input.ExpectedRevision != matter.Revision)
                throw new DbUpdateConcurrencyException("Publication matter changed in another editor.");
            matter.Revision++;
        }
        else
        {
            matter = new PublicationMatter { EditionId = editionId, Title = string.Empty };
            db.PublicationMatter.Add(matter);
            document = document with { ManuscriptId = matter.Id };
        }
        matter.Location = input.Location;
        matter.Kind = input.Kind;
        matter.Title = Clean(input.Title);
        matter.ManuscriptJson = ManuscriptCodec.Serialize(document with { ManuscriptId = matter.Id, Revision = matter.Revision });
        matter.IsIncluded = input.IsIncluded;
        matter.SortOrder = input.SortOrder;
        matter.UpdatedAt = DateTime.UtcNow;
        await SaveWithAuditAsync(edition, "upsert-matter", before, new { matter.Id, matter.Kind }, cancellationToken);
        return MatterView(matter);
    }

    public async Task DeleteMatterAsync(
        Guid projectId,
        Guid editionId,
        Guid matterId,
        long expectedEditionRevision,
        CancellationToken cancellationToken = default)
    {
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var edition = await GetTrackedAsync(projectId, editionId, cancellationToken);
        EnsureRevision(edition, expectedEditionRevision);
        EnsureDraft(edition);
        await EnsureSharedIsbnContentMutableAsync(edition, cancellationToken);
        var matter = await db.PublicationMatter.FirstOrDefaultAsync(
            candidate => candidate.EditionId == editionId && candidate.Id == matterId,
            cancellationToken);
        if (matter is null) return;
        var before = await FingerprintAsync(projectId, editionId, cancellationToken);
        db.PublicationMatter.Remove(matter);
        await SaveWithAuditAsync(edition, "delete-matter", before, new { matterId }, cancellationToken);
    }

    public async Task<PublicationEditionStyleMappingView> UpsertStyleMappingAsync(
        Guid projectId,
        Guid editionId,
        PublicationEditionStyleMappingInput input,
        long expectedEditionRevision,
        CancellationToken cancellationToken = default)
    {
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var edition = await GetTrackedAsync(projectId, editionId, cancellationToken);
        EnsureRevision(edition, expectedEditionRevision);
        EnsureDraft(edition);
        await EnsureSharedIsbnContentMutableAsync(edition, cancellationToken);
        var style = await db.ManuscriptStyleDefinitions.FirstOrDefaultAsync(
            candidate => candidate.ProjectId == projectId
                && candidate.Id == input.ManuscriptStyleDefinitionId,
            cancellationToken)
            ?? throw new InvalidOperationException("Book Text Style was not found.");
        var normalized = ManuscriptStyleService.NormalizeOverride(style.Kind, input.Override);
        var before = await FingerprintAsync(projectId, editionId, cancellationToken);
        var mapping = await db.PublicationEditionStyleMappings.FirstOrDefaultAsync(
            candidate => candidate.EditionId == editionId
                && candidate.ManuscriptStyleDefinitionId == style.Id,
            cancellationToken);
        if (mapping is null)
        {
            mapping = new PublicationEditionStyleMapping
            {
                EditionId = editionId,
                ManuscriptStyleDefinitionId = style.Id,
                SemanticRole = style.SemanticRole,
            };
            db.PublicationEditionStyleMappings.Add(mapping);
        }
        else
        {
            if (input.ExpectedRevision != mapping.Revision)
                throw new DbUpdateConcurrencyException("Edition style mapping changed in another editor.");
            mapping.Revision++;
        }
        mapping.OverrideJson = JsonSerializer.Serialize(normalized, ManuscriptCodec.JsonOptions);
        mapping.UpdatedAt = DateTime.UtcNow;
        await SaveWithAuditAsync(edition, "upsert-style-mapping", before, new { style.Id }, cancellationToken);
        return StyleMappingView(mapping, style.Name);
    }

    public async Task DeleteStyleMappingAsync(
        Guid projectId,
        Guid editionId,
        Guid mappingId,
        long expectedEditionRevision,
        CancellationToken cancellationToken = default)
    {
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var edition = await GetTrackedAsync(projectId, editionId, cancellationToken);
        EnsureRevision(edition, expectedEditionRevision);
        EnsureDraft(edition);
        await EnsureSharedIsbnContentMutableAsync(edition, cancellationToken);
        var mapping = await db.PublicationEditionStyleMappings.FirstOrDefaultAsync(
            candidate => candidate.EditionId == editionId && candidate.Id == mappingId,
            cancellationToken);
        if (mapping is null) return;
        var before = await FingerprintAsync(projectId, editionId, cancellationToken);
        db.PublicationEditionStyleMappings.Remove(mapping);
        await SaveWithAuditAsync(edition, "delete-style-mapping", before, new { mappingId }, cancellationToken);
    }

    public async Task<PublicationImagePlacementView> AddImagePlacementAsync(
        Guid projectId,
        Guid editionId,
        PublicationImagePlacementCreate input,
        long expectedEditionRevision,
        CancellationToken cancellationToken = default)
    {
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var edition = await GetTrackedAsync(projectId, editionId, cancellationToken);
        EnsureRevision(edition, expectedEditionRevision);
        EnsureDraft(edition);
        await EnsureSharedIsbnContentMutableAsync(edition, cancellationToken);
        var metadata = await ValidatePlacementAsync(projectId, editionId, input.AssetId, input.TargetKind, input.TargetId, input.PlacementKind, cancellationToken);
        var presentation = input.Presentation ?? new FigurePresentation { Placement = FigurePlacementIntent.DedicatedPage };
        var altText = FirstNonEmpty(input.AltText, metadata.AssetAltText);
        await ValidatePlacementPresentationAsync(projectId, presentation, altText, input.Decorative, input.Language, input.AccessibilityRole, cancellationToken);
        var before = await FingerprintAsync(projectId, editionId, cancellationToken);
        var order = await db.PublicationImagePlacements
            .Where(placement => placement.EditionId == editionId
                && placement.TargetKind == input.TargetKind
                && placement.TargetId == input.TargetId
                && placement.PlacementKind == input.PlacementKind)
            .Select(placement => (int?)placement.SortOrder)
            .MaxAsync(cancellationToken) ?? -1;
        var placement = new PublicationImagePlacement
        {
            EditionId = editionId,
            AssetId = input.AssetId,
            TargetKind = input.TargetKind,
            TargetId = input.TargetId,
            ActId = input.TargetKind == PublishOutlineTargetKind.Act ? input.TargetId : null,
            ChapterId = input.TargetKind == PublishOutlineTargetKind.Chapter ? input.TargetId : null,
            PlacementKind = input.PlacementKind,
            Caption = Clean(input.Caption),
            PresentationJson = JsonSerializer.Serialize(presentation, ManuscriptCodec.JsonOptions),
            AltText = input.Decorative ? string.Empty : altText,
            Decorative = input.Decorative,
            Language = Clean(input.Language),
            AccessibilityRole = input.AccessibilityRole,
            SortOrder = order + 1,
        };
        db.PublicationImagePlacements.Add(placement);
        await SaveWithAuditAsync(edition, "add-image-placement", before, new { placement.Id }, cancellationToken);
        return PlacementView(projectId, placement, metadata.AssetFileName, metadata.TargetTitle);
    }

    public async Task<PublicationImagePlacementView> UpdateImagePlacementAsync(
        Guid projectId,
        Guid editionId,
        Guid placementId,
        PublicationImagePlacementUpdate input,
        long expectedEditionRevision,
        CancellationToken cancellationToken = default)
    {
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var edition = await GetTrackedAsync(projectId, editionId, cancellationToken);
        EnsureRevision(edition, expectedEditionRevision);
        EnsureDraft(edition);
        await EnsureSharedIsbnContentMutableAsync(edition, cancellationToken);
        var placement = await db.PublicationImagePlacements.FirstOrDefaultAsync(
            candidate => candidate.EditionId == editionId && candidate.Id == placementId,
            cancellationToken)
            ?? throw new InvalidOperationException("Image placement was not found.");
        var metadata = await ValidatePlacementAsync(projectId, editionId, input.AssetId, input.TargetKind, input.TargetId, input.PlacementKind, cancellationToken);
        var presentation = input.Presentation ?? new FigurePresentation { Placement = FigurePlacementIntent.DedicatedPage };
        var altText = FirstNonEmpty(input.AltText, metadata.AssetAltText);
        await ValidatePlacementPresentationAsync(projectId, presentation, altText, input.Decorative, input.Language, input.AccessibilityRole, cancellationToken);
        var before = await FingerprintAsync(projectId, editionId, cancellationToken);
        var oldGroup = (placement.TargetKind, placement.TargetId, placement.PlacementKind);
        var moved = oldGroup != (input.TargetKind, input.TargetId, input.PlacementKind);
        if (moved)
        {
            var nextOrder = await db.PublicationImagePlacements
                .Where(candidate => candidate.EditionId == editionId
                    && candidate.Id != placementId
                    && candidate.TargetKind == input.TargetKind
                    && candidate.TargetId == input.TargetId
                    && candidate.PlacementKind == input.PlacementKind)
                .Select(candidate => (int?)candidate.SortOrder)
                .MaxAsync(cancellationToken) ?? -1;
            placement.SortOrder = nextOrder + 1;
            await CompactPlacementGroupAsync(
                editionId,
                oldGroup.TargetKind,
                oldGroup.TargetId,
                oldGroup.PlacementKind,
                placementId,
                cancellationToken);
        }
        placement.AssetId = input.AssetId;
        placement.TargetKind = input.TargetKind;
        placement.TargetId = input.TargetId;
        placement.ActId = input.TargetKind == PublishOutlineTargetKind.Act ? input.TargetId : null;
        placement.ChapterId = input.TargetKind == PublishOutlineTargetKind.Chapter ? input.TargetId : null;
        placement.PlacementKind = input.PlacementKind;
        placement.Caption = Clean(input.Caption);
        placement.PresentationJson = JsonSerializer.Serialize(presentation, ManuscriptCodec.JsonOptions);
        placement.AltText = input.Decorative ? string.Empty : altText;
        placement.Decorative = input.Decorative;
        placement.Language = Clean(input.Language);
        placement.AccessibilityRole = input.AccessibilityRole;
        placement.UpdatedAt = DateTime.UtcNow;
        await SaveWithAuditAsync(edition, "update-image-placement", before, new { placementId }, cancellationToken);
        return PlacementView(projectId, placement, metadata.AssetFileName, metadata.TargetTitle);
    }

    public async Task ReorderImagePlacementsAsync(
        Guid projectId,
        Guid editionId,
        IReadOnlyList<Guid> orderedPlacementIds,
        long expectedEditionRevision,
        CancellationToken cancellationToken = default)
    {
        if (orderedPlacementIds.Count != orderedPlacementIds.Distinct().Count())
            throw new InvalidOperationException("Placement reorder contains duplicate IDs.");
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var edition = await GetTrackedAsync(projectId, editionId, cancellationToken);
        EnsureRevision(edition, expectedEditionRevision);
        EnsureDraft(edition);
        await EnsureSharedIsbnContentMutableAsync(edition, cancellationToken);
        var placements = await db.PublicationImagePlacements
            .Where(placement => placement.EditionId == editionId && orderedPlacementIds.Contains(placement.Id))
            .ToListAsync(cancellationToken);
        if (placements.Count != orderedPlacementIds.Count)
            throw new InvalidOperationException("One or more image placements were not found.");
        if (placements.Count > 0)
        {
            var first = placements[0];
            if (placements.Any(placement => placement.TargetKind != first.TargetKind
                || placement.TargetId != first.TargetId
                || placement.PlacementKind != first.PlacementKind))
            {
                throw new InvalidOperationException("Only placements at the same target and position can be reordered together.");
            }
            var groupCount = await db.PublicationImagePlacements.CountAsync(
                placement => placement.EditionId == editionId
                    && placement.TargetKind == first.TargetKind
                    && placement.TargetId == first.TargetId
                    && placement.PlacementKind == first.PlacementKind,
                cancellationToken);
            if (groupCount != orderedPlacementIds.Count)
                throw new InvalidOperationException("Placement reorder must include every image in the target group.");
        }
        var before = await FingerprintAsync(projectId, editionId, cancellationToken);
        var order = orderedPlacementIds.Select((id, index) => (id, index)).ToDictionary(item => item.id, item => item.index);
        foreach (var placement in placements)
            placement.SortOrder = order[placement.Id];
        await SaveWithAuditAsync(edition, "reorder-image-placements", before, new { orderedPlacementIds }, cancellationToken);
    }

    public async Task DeleteImagePlacementAsync(
        Guid projectId,
        Guid editionId,
        Guid placementId,
        long expectedEditionRevision,
        CancellationToken cancellationToken = default)
    {
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var edition = await GetTrackedAsync(projectId, editionId, cancellationToken);
        EnsureRevision(edition, expectedEditionRevision);
        EnsureDraft(edition);
        await EnsureSharedIsbnContentMutableAsync(edition, cancellationToken);
        var placement = await db.PublicationImagePlacements.FirstOrDefaultAsync(
            candidate => candidate.EditionId == editionId && candidate.Id == placementId,
            cancellationToken);
        if (placement is null) return;
        var before = await FingerprintAsync(projectId, editionId, cancellationToken);
        await CompactPlacementGroupAsync(
            editionId,
            placement.TargetKind,
            placement.TargetId,
            placement.PlacementKind,
            placement.Id,
            cancellationToken);
        db.PublicationImagePlacements.Remove(placement);
        await SaveWithAuditAsync(edition, "delete-image-placement", before, new { placementId }, cancellationToken);
    }

    private async Task CompactPlacementGroupAsync(
        Guid editionId,
        PublishOutlineTargetKind targetKind,
        Guid targetId,
        PublicationImagePlacementKind placementKind,
        Guid excludedId,
        CancellationToken cancellationToken)
    {
        var group = await db.PublicationImagePlacements
            .Where(placement => placement.EditionId == editionId
                && placement.Id != excludedId
                && placement.TargetKind == targetKind
                && placement.TargetId == targetId
                && placement.PlacementKind == placementKind)
            .OrderBy(placement => placement.SortOrder)
            .ThenBy(placement => placement.Id)
            .ToListAsync(cancellationToken);
        for (var index = 0; index < group.Count; index++)
            group[index].SortOrder = index;
    }

    public async Task<PublicationEditionCompareView> CompareAsync(
        Guid projectId,
        Guid leftEditionId,
        Guid rightEditionId,
        CancellationToken cancellationToken = default)
    {
        var editions = await db.PublicationEditions.AsNoTracking()
            .Where(edition => edition.ProjectId == projectId
                && (edition.Id == leftEditionId || edition.Id == rightEditionId))
            .ToListAsync(cancellationToken);
        var left = editions.FirstOrDefault(edition => edition.Id == leftEditionId)
            ?? throw new InvalidOperationException("Left publication edition was not found.");
        var right = editions.FirstOrDefault(edition => edition.Id == rightEditionId)
            ?? throw new InvalidOperationException("Right publication edition was not found.");
        var differences = new List<string>();
        AddDifference(differences, "Format", left.Format, right.Format);
        AddDifference(differences, "Vendor", left.Vendor, right.Vendor);
        AddDifference(differences, "ISBN", left.Isbn, right.Isbn);
        AddDifference(differences, "Trim", $"{left.PageWidthInches}x{left.PageHeightInches}", $"{right.PageWidthInches}x{right.PageHeightInches}");
        AddDifference(differences, "Binding", left.Binding, right.Binding);
        AddDifference(differences, "Paper", left.Paper, right.Paper);
        AddDifference(differences, "Ink", left.Ink, right.Ink);
        var leftItems = await db.PublicationEditionOutlineItems.AsNoTracking().CountAsync(item => item.EditionId == leftEditionId && item.IsIncluded, cancellationToken);
        var rightItems = await db.PublicationEditionOutlineItems.AsNoTracking().CountAsync(item => item.EditionId == rightEditionId && item.IsIncluded, cancellationToken);
        AddDifference(differences, "Included content", leftItems, rightItems);
        AddDifference(
            differences,
            "Complete source/settings fingerprint",
            await FingerprintAsync(projectId, leftEditionId, cancellationToken),
            await FingerprintAsync(projectId, rightEditionId, cancellationToken));
        return new(Summary(left), Summary(right), differences);
    }

    public async Task<IReadOnlyList<PublicationEditionAuditView>> GetAuditAsync(
        Guid projectId,
        Guid editionId,
        CancellationToken cancellationToken = default)
    {
        _ = await GetReadOnlyAsync(projectId, editionId, cancellationToken);
        return await db.PublicationEditionAuditEntries.AsNoTracking()
            .Where(entry => entry.EditionId == editionId)
            .OrderByDescending(entry => entry.CreatedAt)
            .Select(entry => new PublicationEditionAuditView(
                entry.Id,
                entry.Action,
                entry.Actor,
                entry.BeforeHash,
                entry.AfterHash,
                entry.DetailJson,
                entry.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<string> GetSourceFingerprintAsync(
        Guid projectId,
        Guid editionId,
        CancellationToken cancellationToken = default) =>
        await FingerprintAsync(projectId, editionId, cancellationToken);

    public async Task<string> GetPaginationFingerprintAsync(
        Guid projectId,
        Guid editionId,
        CancellationToken cancellationToken = default) =>
        await FingerprintAsync(projectId, editionId, cancellationToken, includeCover: false);

    private async Task SaveWithAuditAsync(
        PublicationEdition edition,
        string action,
        string beforeHash,
        object detail,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        edition.Revision++;
        edition.UpdatedAt = DateTime.UtcNow;
        var project = await db.Projects.FirstAsync(project => project.Id == edition.ProjectId, cancellationToken);
        project.UpdatedAt = edition.UpdatedAt;
        await db.SaveChangesAsync(cancellationToken);
        var afterHash = await FingerprintAsync(edition.ProjectId, edition.Id, cancellationToken);
        db.PublicationEditionAuditEntries.Add(new PublicationEditionAuditEntry
        {
            EditionId = edition.Id,
            Action = action,
            Actor = actorContext.Actor,
            BeforeHash = beforeHash,
            AfterHash = afterHash,
            DetailJson = JsonSerializer.Serialize(detail, ManuscriptCodec.JsonOptions),
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<string> FingerprintAsync(
        Guid projectId,
        Guid editionId,
        CancellationToken cancellationToken,
        bool includeCover = true)
    {
        var edition = await GetReadOnlyAsync(projectId, editionId, cancellationToken);
        var items = await db.PublicationEditionOutlineItems.AsNoTracking()
            .Where(item => item.EditionId == editionId)
            .OrderBy(item => item.SortOrder)
            .Select(item => new { item.TargetKind, item.TargetId, item.IsIncluded, item.SortOrder })
            .ToListAsync(cancellationToken);
        var excludedChapterIds = items
            .Where(item => item.TargetKind == PublishOutlineTargetKind.Chapter && !item.IsIncluded)
            .Select(item => item.TargetId)
            .ToList();
        var project = await db.Projects.AsNoTracking()
            .Where(candidate => candidate.Id == projectId)
            .Select(candidate => new { candidate.Name, candidate.Slug })
            .SingleAsync(cancellationToken);
        var acts = await db.Acts.AsNoTracking()
            .Where(act => act.ProjectId == projectId)
            .OrderBy(act => act.Order)
            .Select(act => new { act.Id, act.Title, act.Synopsis, act.Order })
            .ToListAsync(cancellationToken);
        var chapters = await db.Chapters.AsNoTracking()
            .Where(chapter => chapter.ProjectId == projectId && !excludedChapterIds.Contains(chapter.Id))
            .OrderBy(chapter => chapter.Id)
            .Select(chapter => new
            {
                chapter.Id,
                chapter.ActId,
                chapter.Title,
                chapter.Synopsis,
                chapter.Order,
                chapter.ManuscriptRevision,
                chapter.ManuscriptJson,
            })
            .ToListAsync(cancellationToken);
        var compositionIds = chapters
            .SelectMany(chapter => ManuscriptCodec.Deserialize(
                chapter.ManuscriptJson,
                chapter.Id,
                chapter.ManuscriptRevision).Content)
            .Where(block => block.Type == ManuscriptBlockType.DesignedPage
                && block.PageCompositionId is not null)
            .Select(block => block.PageCompositionId!.Value)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
        var compositions = await db.PageCompositions.AsNoTracking()
            .Where(composition => compositionIds.Contains(composition.Id))
            .OrderBy(composition => composition.Id)
            .Select(composition => new
            {
                composition.Id,
                composition.ChapterId,
                composition.Name,
                composition.SemanticManuscriptJson,
                composition.Revision,
                Variants = composition.Variants
                    .Select(variant => new
                    {
                        variant.Id,
                        variant.GeometryKey,
                        variant.SceneJson,
                        variant.Revision,
                        variant.UpdatedAt,
                    }),
            })
            .ToListAsync(cancellationToken);
        var matter = await db.PublicationMatter.AsNoTracking()
            .Where(item => item.EditionId == editionId)
            .OrderBy(item => item.Location).ThenBy(item => item.SortOrder).ThenBy(item => item.Id)
            .Select(item => new { item.Id, item.Kind, item.Location, item.Title, item.ManuscriptJson, item.Revision, item.IsIncluded, item.SortOrder })
            .ToListAsync(cancellationToken);
        var mappings = await db.PublicationEditionStyleMappings.AsNoTracking()
            .Where(mapping => mapping.EditionId == editionId)
            .OrderBy(mapping => mapping.SemanticRole)
            .Select(mapping => new { mapping.ManuscriptStyleDefinitionId, mapping.SemanticRole, mapping.OverrideJson, mapping.Revision })
            .ToListAsync(cancellationToken);
        var placements = await db.PublicationImagePlacements.AsNoTracking()
            .Where(placement => placement.EditionId == editionId)
            .OrderBy(placement => placement.TargetKind).ThenBy(placement => placement.TargetId)
            .ThenBy(placement => placement.PlacementKind).ThenBy(placement => placement.SortOrder)
            .Select(placement => new
            {
                placement.Id,
                placement.AssetId,
                placement.TargetKind,
                placement.TargetId,
                placement.PlacementKind,
                placement.Caption,
                placement.PresentationJson,
                placement.AltText,
                placement.Decorative,
                placement.Language,
                placement.AccessibilityRole,
                placement.SortOrder,
            })
            .ToListAsync(cancellationToken);
        var styles = await db.ManuscriptStyleDefinitions.AsNoTracking()
            .Where(style => style.ProjectId == projectId)
            .OrderBy(style => style.Id)
            .Select(style => new
            {
                style.Id,
                style.Name,
                style.Kind,
                style.SemanticRole,
                style.DefinitionJson,
                style.Revision,
            })
            .ToListAsync(cancellationToken);
        var fonts = await db.ProjectFontFamilies.AsNoTracking()
            .Where(family => family.ProjectId == projectId)
            .OrderBy(family => family.Id)
            .Select(family => new
            {
                family.Id,
                family.Name,
                family.EmbeddingRightsConfirmed,
                family.RightsDeclaration,
                Faces = family.Faces
                    .OrderBy(face => face.Id)
                    .Select(face => new
                    {
                        face.Id,
                        face.SubfamilyName,
                        face.FileName,
                        face.ContentType,
                        face.Weight,
                        face.Italic,
                        face.Data,
                    }),
            })
            .ToListAsync(cancellationToken);
        var coverDesign = await db.PublicationCoverDesigns.AsNoTracking()
            .Where(design => design.EditionId == editionId)
            .Select(design => new
            {
                design.Title,
                design.Subtitle,
                design.Author,
                design.SpineText,
                design.BackCopy,
                design.BackgroundColor,
                design.BarcodeMode,
                design.ImageCropXPercent,
                design.ImageCropYPercent,
                design.CompositionSceneJson,
            })
            .SingleOrDefaultAsync(cancellationToken);
        var referencedAssetIds = new HashSet<Guid>(placements.Select(placement => placement.AssetId));
        foreach (var chapter in chapters)
            CollectReferencedImageIds(chapter.ManuscriptJson, referencedAssetIds);
        foreach (var item in matter.Where(item => item.IsIncluded))
            CollectReferencedImageIds(item.ManuscriptJson, referencedAssetIds);
        foreach (var composition in compositions)
        {
            CollectReferencedImageIds(composition.SemanticManuscriptJson, referencedAssetIds);
            foreach (var variant in composition.Variants)
                CollectReferencedImageIds(variant.SceneJson, referencedAssetIds);
        }
        if (includeCover)
        {
            if (edition.SelectedCoverImageId is { } selectedCoverImageId)
                referencedAssetIds.Add(selectedCoverImageId);
            if (coverDesign is not null)
                CollectReferencedImageIds(coverDesign.CompositionSceneJson, referencedAssetIds);
        }
        var assets = await db.PublishAssets.AsNoTracking()
            .Where(asset => asset.ProjectId == projectId && referencedAssetIds.Contains(asset.Id))
            .OrderBy(asset => asset.Id)
            .Select(asset => new
            {
                asset.Id,
                asset.FileName,
                asset.ContentType,
                asset.AltText,
                asset.Data,
                asset.SourceMetadataJson,
            })
            .ToListAsync(cancellationToken);
        var canonical = JsonSerializer.Serialize(new
        {
            Project = project,
            Edition = new
            {
                edition.Name,
                edition.Format,
                edition.Vendor,
                edition.VendorProfileVersion,
                edition.Status,
                edition.TitleOverride,
                edition.Subtitle,
                edition.Author,
                edition.Language,
                edition.Publisher,
                edition.Copyright,
                edition.Isbn,
                edition.Description,
                edition.IncludeTableOfContents,
                edition.IncludeVisibleTableOfContents,
                edition.IncludeActSynopses,
                edition.IncludeChapterSynopses,
                edition.IncludeActHeadings,
                edition.IncludeChapterHeadings,
                edition.NumberActs,
                edition.NumberChapters,
                edition.TitlePageMode,
                edition.Binding,
                edition.Paper,
                edition.Ink,
                edition.Bleed,
                edition.AllowDesignedPageOverrides,
                edition.PageWidthInches,
                edition.PageHeightInches,
                edition.PageMarginInches,
                edition.BodyFontSizePoints,
                edition.BodyLineHeight,
                SelectedCoverImageId = includeCover ? edition.SelectedCoverImageId : null,
            },
            Items = items,
            Acts = acts,
            Chapters = chapters,
            Compositions = compositions,
            Matter = matter,
            Mappings = mappings,
            Placements = placements,
            Assets = assets,
            Styles = styles,
            Fonts = fonts,
            CoverDesign = includeCover ? coverDesign : null,
        }, ManuscriptCodec.JsonOptions);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static void CollectReferencedImageIds(string json, ISet<Guid> target)
    {
        if (string.IsNullOrWhiteSpace(json))
            return;
        using var document = JsonDocument.Parse(json);
        Visit(document.RootElement);
        return;

        void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name is "imageId" or "assetId"
                        && property.Value.ValueKind == JsonValueKind.String
                        && Guid.TryParse(property.Value.GetString(), out var id)
                        && id != Guid.Empty)
                    {
                        target.Add(id);
                    }
                    Visit(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                    Visit(item);
            }
        }
    }

    private async Task<string> BibliographicContentHashAsync(
        Guid editionId,
        CancellationToken cancellationToken)
    {
        var outline = await db.PublicationEditionOutlineItems.AsNoTracking()
            .Where(item => item.EditionId == editionId)
            .OrderBy(item => item.SortOrder)
            .Select(item => new { item.TargetKind, item.TargetId, item.IsIncluded, item.SortOrder })
            .ToListAsync(cancellationToken);
        var matter = await db.PublicationMatter.AsNoTracking()
            .Where(item => item.EditionId == editionId)
            .OrderBy(item => item.Location).ThenBy(item => item.SortOrder).ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var mappings = await db.PublicationEditionStyleMappings.AsNoTracking()
            .Where(mapping => mapping.EditionId == editionId)
            .OrderBy(mapping => mapping.ManuscriptStyleDefinitionId)
            .Select(mapping => new
            {
                mapping.ManuscriptStyleDefinitionId,
                mapping.SemanticRole,
                mapping.OverrideJson,
            })
            .ToListAsync(cancellationToken);
        var placements = await db.PublicationImagePlacements.AsNoTracking()
            .Where(placement => placement.EditionId == editionId)
            .OrderBy(placement => placement.TargetKind)
            .ThenBy(placement => placement.TargetId)
            .ThenBy(placement => placement.PlacementKind)
            .ThenBy(placement => placement.SortOrder)
            .Select(placement => new
            {
                placement.AssetId,
                placement.TargetKind,
                placement.TargetId,
                placement.PlacementKind,
                placement.Caption,
                placement.SortOrder,
            })
            .ToListAsync(cancellationToken);
        var canonicalMatter = matter.Select(item => new
        {
            item.Location,
            item.Kind,
            item.Title,
            Content = CanonicalManuscriptContent(
                ManuscriptCodec.Deserialize(item.ManuscriptJson, item.Id, item.Revision)),
            item.IsIncluded,
            item.SortOrder,
        });
        var canonical = JsonSerializer.Serialize(
            new { Outline = outline, Matter = canonicalMatter, Mappings = mappings, Placements = placements },
            ManuscriptCodec.JsonOptions);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private async Task EnsureSharedIsbnContentMutableAsync(
        PublicationEdition edition,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(edition.Isbn))
            return;
        if (await db.PublicationEditions.AsNoTracking().AnyAsync(candidate =>
            candidate.ProjectId == edition.ProjectId
            && candidate.Id != edition.Id
            && candidate.Format == edition.Format
            && candidate.Isbn == edition.Isbn,
            cancellationToken))
        {
            throw new InvalidOperationException(
                "This edition shares an ISBN-13 with another vendor edition. Clear the shared ISBN before changing ordered content or publication matter, then synchronize the vendor editions before reassigning it.");
        }
    }

    private static bool BibliographicProductSettingsMatch(
        PublicationEdition edition,
        PublicationEditionUpdate input) =>
        string.Equals(edition.TitleOverride, Clean(input.TitleOverride), StringComparison.Ordinal)
        && string.Equals(edition.Subtitle, Clean(input.Subtitle), StringComparison.Ordinal)
        && string.Equals(edition.Author, Clean(input.Author), StringComparison.Ordinal)
        && string.Equals(edition.Language, string.IsNullOrWhiteSpace(input.Language) ? "en" : Clean(input.Language), StringComparison.OrdinalIgnoreCase)
        && string.Equals(edition.Publisher, Clean(input.Publisher), StringComparison.Ordinal)
        && string.Equals(edition.Copyright, Clean(input.Copyright), StringComparison.Ordinal)
        && string.Equals(edition.Description, Clean(input.Description), StringComparison.Ordinal)
        && edition.IncludeTableOfContents == input.IncludeTableOfContents
        && edition.IncludeVisibleTableOfContents == input.IncludeVisibleTableOfContents
        && edition.IncludeActSynopses == input.IncludeActSynopses
        && edition.IncludeChapterSynopses == input.IncludeChapterSynopses
        && edition.IncludeActHeadings == input.IncludeActHeadings
        && edition.IncludeChapterHeadings == input.IncludeChapterHeadings
        && edition.NumberActs == input.NumberActs
        && edition.NumberChapters == input.NumberChapters
        && edition.TitlePageMode == input.TitlePageMode
        && edition.Binding == input.Binding
        && edition.Paper == input.Paper
        && edition.Ink == input.Ink
        && edition.Bleed == input.Bleed
        && edition.AllowDesignedPageOverrides == input.AllowDesignedPageOverrides
        && edition.PageWidthInches.Equals(input.PageWidthInches)
        && edition.PageHeightInches.Equals(input.PageHeightInches)
        && edition.PageMarginInches.Equals(input.PageMarginInches)
        && edition.BodyFontSizePoints.Equals(input.BodyFontSizePoints)
        && edition.BodyLineHeight.Equals(input.BodyLineHeight);

    internal static IReadOnlyList<ManuscriptBlock> CanonicalManuscriptContent(ManuscriptDocument document) =>
        document.Content.Select(block => block with { Id = string.Empty }).ToList();

    private async Task MaterializeOutlineAsync(PublicationEdition edition, CancellationToken cancellationToken)
    {
        var order = 0;
        var acts = await db.Acts.AsNoTracking()
            .Where(act => act.ProjectId == edition.ProjectId)
            .OrderBy(act => act.Order)
            .ToListAsync(cancellationToken);
        var chapters = await db.Chapters.AsNoTracking()
            .Where(chapter => chapter.ProjectId == edition.ProjectId)
            .OrderBy(chapter => chapter.Order)
            .ToListAsync(cancellationToken);
        foreach (var act in acts)
        {
            edition.OutlineItems.Add(NewOutlineItem(edition.Id, PublishOutlineTargetKind.Act, act.Id, order++));
            foreach (var chapter in chapters.Where(chapter => chapter.ActId == act.Id).OrderBy(chapter => chapter.Order))
                edition.OutlineItems.Add(NewOutlineItem(edition.Id, PublishOutlineTargetKind.Chapter, chapter.Id, order++));
        }
        foreach (var chapter in chapters.Where(chapter => chapter.ActId is null).OrderBy(chapter => chapter.Order))
            edition.OutlineItems.Add(NewOutlineItem(edition.Id, PublishOutlineTargetKind.Chapter, chapter.Id, order++));
    }

    private async Task EnsureTargetsAsync(
        Guid projectId,
        IReadOnlyList<PublicationEditionOutlineItemUpdate> updates,
        CancellationToken cancellationToken)
    {
        var actIds = updates.Where(update => update.TargetKind == PublishOutlineTargetKind.Act).Select(update => update.TargetId).Distinct().ToList();
        var chapterIds = updates.Where(update => update.TargetKind == PublishOutlineTargetKind.Chapter).Select(update => update.TargetId).Distinct().ToList();
        if (actIds.Count != await db.Acts.CountAsync(act => act.ProjectId == projectId && actIds.Contains(act.Id), cancellationToken)
            || chapterIds.Count != await db.Chapters.CountAsync(chapter => chapter.ProjectId == projectId && chapterIds.Contains(chapter.Id), cancellationToken))
        {
            throw new InvalidOperationException("One or more edition content targets were not found in this project.");
        }
    }

    private async Task<PlacementMetadata> ValidatePlacementAsync(
        Guid projectId,
        Guid editionId,
        Guid assetId,
        PublishOutlineTargetKind targetKind,
        Guid targetId,
        PublicationImagePlacementKind placementKind,
        CancellationToken cancellationToken)
    {
        ValidatePlacementKind(targetKind, placementKind);
        var asset = await db.PublishAssets
            .Where(asset => asset.ProjectId == projectId && asset.Id == assetId)
            .Select(asset => new { asset.FileName, asset.AltText, asset.ContentType })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Project image was not found.");
        if (asset.ContentType is not ("image/png" or "image/jpeg"))
            throw new InvalidOperationException("Edition illustrations must use publication-compatible PNG or JPEG assets.");
        var targetTitle = targetKind switch
        {
            PublishOutlineTargetKind.Act => await db.Acts
                .Where(act => act.ProjectId == projectId && act.Id == targetId)
                .Select(act => act.Title).FirstOrDefaultAsync(cancellationToken),
            PublishOutlineTargetKind.Chapter => await db.Chapters
                .Where(chapter => chapter.ProjectId == projectId && chapter.Id == targetId)
                .Select(chapter => chapter.Title).FirstOrDefaultAsync(cancellationToken),
            _ => null,
        } ?? throw new InvalidOperationException("Edition image-placement target was not found.");
        var included = await db.PublicationEditionOutlineItems
            .Where(item => item.EditionId == editionId
                && item.TargetKind == targetKind
                && item.TargetId == targetId)
            .Select(item => (bool?)item.IsIncluded)
            .FirstOrDefaultAsync(cancellationToken) ?? true;
        if (!included)
            throw new InvalidOperationException("Images can only be placed on included edition content.");
        return new(asset.FileName, asset.AltText, targetTitle);
    }

    private async Task ValidatePlacementPresentationAsync(
        Guid projectId,
        FigurePresentation presentation,
        string altText,
        bool decorative,
        string language,
        FigureAccessibilityRole role,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(role)
            || presentation.WidthPercent is <= 0 or > 100
            || presentation.CropXPercent is < 0 or > 100
            || presentation.CropYPercent is < 0 or > 100
            || presentation.SpacingBeforePoints is < 0 or > 288
            || presentation.SpacingAfterPoints is < 0 or > 288)
        {
            throw new InvalidOperationException("Edition illustration presentation is outside supported limits.");
        }
        if (decorative && !string.IsNullOrWhiteSpace(altText))
            throw new InvalidOperationException("Decorative edition illustrations cannot carry alternative text.");
        if (!decorative && string.IsNullOrWhiteSpace(altText))
            throw new InvalidOperationException("Edition illustrations require alternative text or a decorative decision.");
        if (string.IsNullOrWhiteSpace(language) || language.Trim().Length > 35)
            throw new InvalidOperationException("Edition illustration language must be a compact BCP 47 tag.");
    }

    private static void ValidatePlacementKind(PublishOutlineTargetKind targetKind, PublicationImagePlacementKind placementKind)
    {
        var valid = targetKind == PublishOutlineTargetKind.Act
            ? placementKind is PublicationImagePlacementKind.BeforeAct or PublicationImagePlacementKind.AfterAct
            : targetKind == PublishOutlineTargetKind.Chapter
                && placementKind is PublicationImagePlacementKind.BeforeChapter
                    or PublicationImagePlacementKind.ChapterOpening
                    or PublicationImagePlacementKind.ChapterEnding
                    or PublicationImagePlacementKind.AfterChapter;
        if (!valid)
            throw new InvalidOperationException($"{placementKind} cannot be used with a {targetKind} target.");
    }

    private static void ValidateIdentity(string name, PublicationEditionFormat format, PublicationVendor vendor)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 120)
            throw new InvalidOperationException("Edition name is required and cannot exceed 120 characters.");
        if (!Enum.IsDefined(format) || !Enum.IsDefined(vendor))
            throw new InvalidOperationException("Edition format or vendor is invalid.");
    }

    private static void ValidateUpdate(PublicationEditionUpdate input)
    {
        ValidateIdentity(input.Name, input.Format, input.Vendor);
        if (!Enum.IsDefined(input.Binding) || !Enum.IsDefined(input.Paper) || !Enum.IsDefined(input.Ink)
            || !Enum.IsDefined(input.TitlePageMode))
            throw new InvalidOperationException("One or more edition settings are invalid.");
        var boundedFields = new (string Label, string? Value, int Maximum)[]
        {
            ("Vendor profile version", input.VendorProfileVersion, 120),
            ("Title", input.TitleOverride, 500),
            ("Subtitle", input.Subtitle, 500),
            ("Author", input.Author, 500),
            ("Language", input.Language, 40),
            ("Publisher", input.Publisher, 500),
            ("Copyright", input.Copyright, 100_000),
            ("Description", input.Description, 100_000),
        };
        foreach (var (label, value, maximum) in boundedFields)
        {
            if ((value?.Trim().Length ?? 0) > maximum)
                throw new InvalidOperationException($"{label} cannot exceed {maximum:N0} characters.");
        }
        _ = PublicationIsbn.NormalizeValidOrEmpty(input.Isbn);
        if (!double.IsFinite(input.PageWidthInches)
            || !double.IsFinite(input.PageHeightInches)
            || !double.IsFinite(input.PageMarginInches)
            || !double.IsFinite(input.BodyFontSizePoints)
            || !double.IsFinite(input.BodyLineHeight)
            || input.PageWidthInches is < 3 or > 24
            || input.PageHeightInches is < 3 or > 24
            || input.PageMarginInches < 0.125
            || input.PageMarginInches > Math.Min(input.PageWidthInches, input.PageHeightInches) / 3
            || input.BodyFontSizePoints is < 7 or > 72
            || input.BodyLineHeight is < 1 or > 2.4)
            throw new InvalidOperationException("Edition geometry or body typography is outside supported limits.");
    }

    private static void ValidateProductCombination(PublicationEditionUpdate input, PublicationEdition current)
    {
        var digital = input.Format is PublicationEditionFormat.Epub or PublicationEditionFormat.DigitalPdf;
        if (digital && (input.Vendor != PublicationVendor.Generic
            || input.Binding != PublicationBinding.Digital
            || input.Paper != PublicationPaper.Digital
            || input.Ink != PublicationInk.Digital
            || input.Bleed))
            throw new InvalidOperationException("EPUB and Digital PDF editions require the Generic vendor, digital binding/paper/ink, and no print bleed.");
        if (input.Format == PublicationEditionFormat.Paperback
            && (input.Binding == PublicationBinding.Digital
                || input.Paper == PublicationPaper.Digital
                || input.Ink == PublicationInk.Digital))
            throw new InvalidOperationException("Paperback editions require physical binding, paper, and print ink settings.");
        var expectedProfile = DefaultProfile(input.Format, input.Vendor);
        var productIdentityChanged = input.Format != current.Format || input.Vendor != current.Vendor;
        if (!string.Equals(input.VendorProfileVersion.Trim(), expectedProfile, StringComparison.Ordinal)
            && (productIdentityChanged
                || !string.Equals(input.VendorProfileVersion.Trim(), current.VendorProfileVersion, StringComparison.Ordinal)))
            throw new InvalidOperationException($"The selected format and vendor require profile '{expectedProfile}'.");
    }

    private async Task<Project> GetProjectAsync(Guid projectId, CancellationToken cancellationToken) =>
        await db.Projects.FirstOrDefaultAsync(project => project.Id == projectId, cancellationToken)
        ?? throw new InvalidOperationException($"Project {projectId:N} was not found.");

    private async Task<PublicationEdition> GetTrackedAsync(Guid projectId, Guid editionId, CancellationToken cancellationToken) =>
        await db.PublicationEditions.FirstOrDefaultAsync(
            edition => edition.ProjectId == projectId && edition.Id == editionId,
            cancellationToken)
        ?? throw new InvalidOperationException("Publication edition was not found.");

    private async Task<PublicationEdition> GetReadOnlyAsync(Guid projectId, Guid editionId, CancellationToken cancellationToken) =>
        await db.PublicationEditions.AsNoTracking().FirstOrDefaultAsync(
            edition => edition.ProjectId == projectId && edition.Id == editionId,
            cancellationToken)
        ?? throw new InvalidOperationException("Publication edition was not found.");

    private static void EnsureRevision(PublicationEdition edition, long expectedRevision)
    {
        if (edition.Revision != expectedRevision)
            throw new DbUpdateConcurrencyException(
                $"Publication edition changed in another editor (expected revision {expectedRevision}, current {edition.Revision}).");
    }

    internal static void EnsureDraft(PublicationEdition edition)
    {
        if (edition.Status != PublicationEditionStatus.Draft)
            throw new InvalidOperationException("Archived publication editions are read-only. Clone this edition to make changes.");
    }

    internal static PublicationEditionSummary Summary(PublicationEdition edition) =>
        new(
            edition.Id,
            edition.Name,
            edition.Format,
            edition.Vendor,
            edition.Status,
            edition.IsDefault,
            edition.Revision,
            edition.PageWidthInches,
            edition.PageHeightInches,
            edition.PageMarginInches,
            edition.Bleed,
            edition.AllowDesignedPageOverrides);

    internal static PublicationEditionView View(Project project, PublicationEdition edition) =>
        new(
            edition.Id,
            edition.Name,
            edition.Format,
            edition.Vendor,
            edition.VendorProfileVersion,
            edition.Status,
            edition.IsDefault,
            edition.Revision,
            project.Name,
            project.Slug,
            edition.TitleOverride,
            edition.Subtitle,
            edition.Author,
            edition.Language,
            edition.Publisher,
            edition.Copyright,
            edition.Isbn,
            edition.Description,
            edition.IncludeTableOfContents,
            edition.IncludeVisibleTableOfContents,
            edition.IncludeActSynopses,
            edition.IncludeChapterSynopses,
            edition.IncludeActHeadings,
            edition.IncludeChapterHeadings,
            edition.NumberActs,
            edition.NumberChapters,
            edition.TitlePageMode,
            edition.PageWidthInches,
            edition.PageHeightInches,
            edition.PageMarginInches,
            edition.BodyFontSizePoints,
            edition.BodyLineHeight,
            edition.SelectedCoverImageId,
            edition.Binding,
            edition.Paper,
            edition.Ink,
            edition.Bleed,
            edition.AllowDesignedPageOverrides);

    internal static PublicationMatterView MatterView(PublicationMatter matter) =>
        new(
            matter.Id,
            matter.Location,
            matter.Kind,
            matter.Title,
            ManuscriptCodec.Deserialize(matter.ManuscriptJson, matter.Id, matter.Revision),
            matter.IsIncluded,
            matter.SortOrder,
            matter.Revision);

    internal static PublicationEditionStyleMappingView StyleMappingView(
        PublicationEditionStyleMapping mapping,
        string styleName) =>
        new(
            mapping.Id,
            mapping.ManuscriptStyleDefinitionId,
            styleName,
            mapping.SemanticRole,
            JsonSerializer.Deserialize<ManuscriptStyleProperties>(
                mapping.OverrideJson,
                ManuscriptCodec.JsonOptions) ?? new ManuscriptStyleProperties(),
            mapping.Revision);

    private static PublicationImagePlacementView PlacementView(
        Guid projectId,
        PublicationImagePlacement placement,
        string assetFileName,
        string targetTitle) =>
        new(
            placement.Id,
            placement.AssetId,
            assetFileName,
            $"/projects/{projectId:N}/images/{placement.AssetId:N}/content?maxEdge=320",
            placement.TargetKind,
            placement.TargetId,
            targetTitle,
            placement.PlacementKind,
            placement.Caption,
            JsonSerializer.Deserialize<FigurePresentation>(placement.PresentationJson, ManuscriptCodec.JsonOptions) ?? new FigurePresentation(),
            placement.AltText,
            placement.Decorative,
            placement.Language,
            placement.AccessibilityRole,
            placement.SortOrder);

    private static PublicationEditionOutlineItem NewOutlineItem(
        Guid editionId,
        PublishOutlineTargetKind kind,
        Guid targetId,
        int sortOrder) =>
        new()
        {
            EditionId = editionId,
            TargetKind = kind,
            TargetId = targetId,
            ActId = kind == PublishOutlineTargetKind.Act ? targetId : null,
            ChapterId = kind == PublishOutlineTargetKind.Chapter ? targetId : null,
            SortOrder = sortOrder,
        };

    private static PublicationEditionOutlineItem CopyOutlineItem(PublicationEditionOutlineItem source)
    {
        var copy = NewOutlineItem(Guid.Empty, source.TargetKind, source.TargetId, source.SortOrder);
        copy.IsIncluded = source.IsIncluded;
        return copy;
    }

    private static PublicationMatter CopyMatter(PublicationMatter source)
    {
        var copy = new PublicationMatter
        {
            Title = source.Title,
            Location = source.Location,
            Kind = source.Kind,
            IsIncluded = source.IsIncluded,
            SortOrder = source.SortOrder,
            Revision = source.Revision,
        };
        var document = ManuscriptCodec.Deserialize(source.ManuscriptJson, source.Id, source.Revision);
        copy.ManuscriptJson = ManuscriptCodec.Serialize(document with { ManuscriptId = copy.Id });
        return copy;
    }

    private static PublicationEditionStyleMapping CopyStyleMapping(PublicationEditionStyleMapping source) =>
        new()
        {
            ManuscriptStyleDefinitionId = source.ManuscriptStyleDefinitionId,
            SemanticRole = source.SemanticRole,
            OverrideJson = source.OverrideJson,
            Revision = source.Revision,
        };

    private static PublicationImagePlacement CopyPlacement(PublicationImagePlacement source) =>
        new()
        {
            AssetId = source.AssetId,
            TargetKind = source.TargetKind,
            TargetId = source.TargetId,
            ActId = source.ActId,
            ChapterId = source.ChapterId,
            PlacementKind = source.PlacementKind,
            SortOrder = source.SortOrder,
            Caption = source.Caption,
            PresentationJson = source.PresentationJson,
            AltText = source.AltText,
            Decorative = source.Decorative,
            Language = source.Language,
            AccessibilityRole = source.AccessibilityRole,
        };

    private static PublicationEdition CopyEdition(PublicationEdition source, string name) =>
        new()
        {
            ProjectId = source.ProjectId,
            Name = name,
            Format = source.Format,
            Vendor = source.Vendor,
            VendorProfileVersion = source.VendorProfileVersion,
            Status = PublicationEditionStatus.Draft,
            TitleOverride = source.TitleOverride,
            Subtitle = source.Subtitle,
            Author = source.Author,
            Language = source.Language,
            Publisher = source.Publisher,
            Copyright = source.Copyright,
            Description = source.Description,
            IncludeTableOfContents = source.IncludeTableOfContents,
            IncludeVisibleTableOfContents = source.IncludeVisibleTableOfContents,
            IncludeActSynopses = source.IncludeActSynopses,
            IncludeChapterSynopses = source.IncludeChapterSynopses,
            IncludeActHeadings = source.IncludeActHeadings,
            IncludeChapterHeadings = source.IncludeChapterHeadings,
            NumberActs = source.NumberActs,
            NumberChapters = source.NumberChapters,
            TitlePageMode = source.TitlePageMode,
            Binding = source.Binding,
            Paper = source.Paper,
            Ink = source.Ink,
            Bleed = source.Bleed,
            PageWidthInches = source.PageWidthInches,
            PageHeightInches = source.PageHeightInches,
            PageMarginInches = source.PageMarginInches,
            BodyFontSizePoints = source.BodyFontSizePoints,
            BodyLineHeight = source.BodyLineHeight,
            SelectedCoverImageId = source.SelectedCoverImageId,
            AllowDesignedPageOverrides = source.AllowDesignedPageOverrides,
        };

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;

    private static void AddDifference<T>(List<string> differences, string label, T left, T right)
    {
        if (!EqualityComparer<T>.Default.Equals(left, right))
            differences.Add($"{label}: {left} → {right}");
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private sealed record PlacementMetadata(string AssetFileName, string AssetAltText, string TargetTitle);
}
