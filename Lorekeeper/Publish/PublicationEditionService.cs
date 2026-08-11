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
    IPublicationBookService books,
    IPublicationEffectiveConfigurationResolver effectiveConfigurations,
    IPublicationReleasePresetService releasePresets,
    IPublicationActorContext actorContext) : IPublicationEditionService
{
    public PublicationEditionService(
        AppDbContext db,
        IProjectMutationCoordinator projectMutations,
        IPublicationActorContext actorContext)
        : this(db, projectMutations, new PublicationBookService(db, projectMutations),
            new PublicationEffectiveConfigurationResolver(db), new PublicationReleasePresetService(db), actorContext)
    {
    }

    public async Task<IReadOnlyList<PublicationEditionSummary>> ListAsync(
        Guid projectId,
        CancellationToken cancellationToken = default) =>
        await db.PublicationEditions
            .AsNoTracking()
            .Where(edition => edition.ProjectId == projectId)
            .OrderBy(edition => edition.Status)
            .ThenBy(edition => edition.Name)
            .Select(edition => Summary(edition))
            .ToListAsync(cancellationToken);

    public async Task<PublicationEditionView> CreateAsync(
        Guid projectId,
        PublicationEditionCreate input,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(input.Name, input.Format, input.Vendor);
        var core = await books.GetOrCreateAsync(projectId, cancellationToken);
        var preset = await releasePresets.ResolveAsync(projectId, input.Format, input.Vendor, cancellationToken);
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var project = await GetProjectAsync(projectId, cancellationToken);
        var name = input.Name.Trim();
        if (await db.PublicationEditions.AnyAsync(
            edition => edition.ProjectId == projectId && edition.Name == name,
            cancellationToken))
        {
            throw new InvalidOperationException($"A publication release named '{name}' already exists.");
        }
        var edition = new PublicationEdition
        {
            ProjectId = projectId,
            Name = name,
            Format = input.Format,
            Vendor = preset.Vendor,
            VendorProfileVersion = preset.ProfileId,
            OverrideFieldsJson = "[]",
            InheritsCoreCover = true,
            Binding = preset.Binding,
            Paper = preset.Paper,
            Ink = preset.Ink,
            PageWidthInches = core.PageSetup.PageWidthInches,
            PageHeightInches = core.PageSetup.PageHeightInches,
            PageMarginInches = core.PageSetup.PageMarginInches,
            BodyFontSizePoints = core.PageSetup.BodyFontSizePoints,
            BodyLineHeight = core.PageSetup.BodyLineHeight,
            Bleed = preset.Bleed,
            AllowDesignedPageOverrides = preset.AllowDesignedPageOverrides,
        };
        db.PublicationEditions.Add(edition);
        await SaveWithAuditAsync(edition, "create", string.Empty, new { input.Name, input.Format, input.Vendor }, cancellationToken);
        var effective = await effectiveConfigurations.ResolveReleaseAsync(projectId, edition.Id, cancellationToken);
        return View(project, effective.Edition);
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
            throw new InvalidOperationException($"A publication release named '{cleanName}' already exists.");
        }

        await db.Entry(source).Collection(edition => edition.OutlineItems).LoadAsync(cancellationToken);
        await db.Entry(source).Collection(edition => edition.Matter).LoadAsync(cancellationToken);
        await db.Entry(source).Collection(edition => edition.ChapterOverrides).LoadAsync(cancellationToken);
        await db.Entry(source).Collection(edition => edition.ImagePlacements).LoadAsync(cancellationToken);
        await db.Entry(source).Reference(edition => edition.CoverDesign).LoadAsync(cancellationToken);
        var clone = CopyEdition(source, cleanName);
        clone.Isbn = string.Empty;
        clone.OutlineItems = source.OutlineItems.Select(CopyOutlineItem).ToList();
        clone.Matter = source.Matter.Select(CopyMatter).ToList();
        clone.ImagePlacements = source.ImagePlacements.Select(CopyPlacement).ToList();
        var sourceCompositions = await db.PageCompositions.AsNoTracking()
            .Include(item => item.Variants)
            .Where(item => item.EditionId == source.Id)
            .ToListAsync(cancellationToken);
        var compositionMap = sourceCompositions.ToDictionary(item => item.Id, _ => Guid.NewGuid());
        clone.PageCompositions = sourceCompositions.Select(item => CopyEditionComposition(item, clone.Id, compositionMap[item.Id])).ToList();
        clone.ChapterOverrides = source.ChapterOverrides.Select(item =>
        {
            var document = ManuscriptCodec.Deserialize(item.ManuscriptJson, item.ChapterId, item.Revision);
            var remapped = document with
            {
                Content = document.Content.Select(block =>
                    block.PageCompositionId is Guid compositionId && compositionMap.TryGetValue(compositionId, out var cloneCompositionId)
                        ? block with { PageCompositionId = cloneCompositionId }
                        : block).ToList(),
            };
            return new PublicationEditionChapterOverride
            {
                EditionId = clone.Id,
                ChapterId = item.ChapterId,
                ManuscriptJson = ManuscriptCodec.Serialize(remapped),
                Revision = item.Revision,
                BaseCoreRevision = item.BaseCoreRevision,
                BaseCoreHash = item.BaseCoreHash,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
        }).ToList();
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

    public async Task<PublicationEditionView> PatchOverridesAsync(
        Guid projectId,
        Guid editionId,
        PublicationReleaseOverridePatch patch,
        CancellationToken cancellationToken = default)
    {
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var project = await GetProjectAsync(projectId, cancellationToken);
        var edition = await GetTrackedAsync(projectId, editionId, cancellationToken);
        EnsureRevision(edition, patch.ExpectedRevision);
        EnsureDraft(edition);
        if ((patch.ResetFields ?? []).Distinct().Count() != (patch.ResetFields?.Count ?? 0)
            || (patch.ResetFields ?? []).Any(field => !Enum.IsDefined(field)))
            throw new ArgumentException("Release reset fields contain an invalid or duplicate value.", nameof(patch));
        var before = await FingerprintAsync(projectId, editionId, cancellationToken);
        var fields = effectiveConfigurations.ReadOverrideFields(edition).ToHashSet();
        foreach (var reset in patch.ResetFields ?? []) fields.Remove(reset);

        if (patch.Name is not null) edition.Name = patch.Name.Trim();
        if (patch.Destination is { } destination)
        {
            edition.Vendor = destination;
            edition.VendorProfileVersion = DefaultProfile(edition.Format, destination);
        }
        if (patch.Isbn is not null) edition.Isbn = PublicationIsbn.NormalizeValidOrEmpty(patch.Isbn);
        if (patch.Paper is { } paper && edition.Format == PublicationEditionFormat.Paperback) edition.Paper = paper;
        if (patch.Ink is { } ink && edition.Format == PublicationEditionFormat.Paperback) edition.Ink = ink;
        if (edition.Format == PublicationEditionFormat.DigitalPdf)
            Override(fields, PublicationEditionOverrideField.AllowDesignedPageOverrides, patch.AllowDesignedPageOverrides, value => edition.AllowDesignedPageOverrides = value);

        Override(fields, PublicationEditionOverrideField.Title, patch.Title, value => edition.TitleOverride = value);
        Override(fields, PublicationEditionOverrideField.Subtitle, patch.Subtitle, value => edition.Subtitle = value);
        Override(fields, PublicationEditionOverrideField.Author, patch.Author, value => edition.Author = value);
        Override(fields, PublicationEditionOverrideField.Language, patch.Language,
            value => edition.Language = PublicationLanguage.Normalize(value));
        Override(fields, PublicationEditionOverrideField.Publisher, patch.Publisher, value => edition.Publisher = value);
        Override(fields, PublicationEditionOverrideField.Copyright, patch.Copyright, value => edition.Copyright = value);
        Override(fields, PublicationEditionOverrideField.Description, patch.Description, value => edition.Description = value);
        Override(fields, PublicationEditionOverrideField.IncludeTableOfContents, patch.IncludeTableOfContents, value => edition.IncludeTableOfContents = value);
        Override(fields, PublicationEditionOverrideField.IncludeVisibleTableOfContents, patch.IncludeVisibleTableOfContents, value => edition.IncludeVisibleTableOfContents = value);
        Override(fields, PublicationEditionOverrideField.IncludeActSynopses, patch.IncludeActSynopses, value => edition.IncludeActSynopses = value);
        Override(fields, PublicationEditionOverrideField.IncludeChapterSynopses, patch.IncludeChapterSynopses, value => edition.IncludeChapterSynopses = value);
        Override(fields, PublicationEditionOverrideField.IncludeActHeadings, patch.IncludeActHeadings, value => edition.IncludeActHeadings = value);
        Override(fields, PublicationEditionOverrideField.IncludeChapterHeadings, patch.IncludeChapterHeadings, value => edition.IncludeChapterHeadings = value);
        Override(fields, PublicationEditionOverrideField.NumberActs, patch.NumberActs, value => edition.NumberActs = value);
        Override(fields, PublicationEditionOverrideField.NumberChapters, patch.NumberChapters, value => edition.NumberChapters = value);
        Override(fields, PublicationEditionOverrideField.TitlePageMode, patch.TitlePageMode, value => edition.TitlePageMode = value);
        Override(fields, PublicationEditionOverrideField.PageWidthInches, patch.PageWidthInches, value => edition.PageWidthInches = value);
        Override(fields, PublicationEditionOverrideField.PageHeightInches, patch.PageHeightInches, value => edition.PageHeightInches = value);
        Override(fields, PublicationEditionOverrideField.PageMarginInches, patch.PageMarginInches, value => edition.PageMarginInches = value);
        ValidateReleaseState(edition);
        if (await db.PublicationEditions.AnyAsync(candidate => candidate.ProjectId == projectId
            && candidate.Id != editionId && candidate.Name == edition.Name, cancellationToken))
            throw new InvalidOperationException($"A publication release named '{edition.Name}' already exists.");
        edition.OverrideFieldsJson = JsonSerializer.Serialize(fields.Order());
        await SaveWithAuditAsync(edition, "patch-overrides", before, new { changed = fields, reset = patch.ResetFields ?? [] }, cancellationToken);
        var effective = await effectiveConfigurations.ResolveReleaseAsync(projectId, editionId, cancellationToken);
        return View(project, effective.Edition);
    }

    private static void Override(HashSet<PublicationEditionOverrideField> fields, PublicationEditionOverrideField field, string? value, Action<string> apply)
    {
        if (value is null) return;
        apply(value);
        fields.Add(field);
    }

    private static void Override<T>(HashSet<PublicationEditionOverrideField> fields, PublicationEditionOverrideField field, T? value, Action<T> apply)
        where T : struct
    {
        if (!value.HasValue) return;
        apply(value.Value);
        fields.Add(field);
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
        await SaveWithAuditAsync(edition, "archive", before, new { }, cancellationToken);
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
        if (updates.Any(item => item.TargetKind != PublishOutlineTargetKind.Chapter))
            throw new InvalidOperationException("Release content inclusion applies to chapters; act presentation is controlled by the release's act heading and summary settings.");
        await EnsureSharedIsbnContentMutableAsync(edition, cancellationToken);
        await EnsureTargetsAsync(projectId, updates, cancellationToken);
        var before = await FingerprintAsync(projectId, editionId, cancellationToken);
        var existing = await db.PublicationEditionOutlineItems
            .Where(item => item.EditionId == editionId)
            .ToListAsync(cancellationToken);
        var inherited = await db.PublicationBookOutlineItems.AsNoTracking()
            .Where(item => item.ProjectId == projectId)
            .ToDictionaryAsync(item => (item.TargetKind, item.TargetId), cancellationToken);
        foreach (var update in updates
            .GroupBy(update => (update.TargetKind, update.TargetId))
            .Select(group => group.Last()))
        {
            var item = existing.FirstOrDefault(candidate =>
                candidate.TargetKind == update.TargetKind && candidate.TargetId == update.TargetId);
            if (inherited.TryGetValue((update.TargetKind, update.TargetId), out var coreItem)
                && coreItem.IsIncluded == update.IsIncluded)
            {
                if (item is not null)
                {
                    db.PublicationEditionOutlineItems.Remove(item);
                    existing.Remove(item);
                }
                continue;
            }
            if (item is null)
            {
                item = NewOutlineItem(
                    editionId,
                    update.TargetKind,
                    update.TargetId,
                    inherited.GetValueOrDefault((update.TargetKind, update.TargetId))?.SortOrder ?? existing.Count);
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
        var coreChapters = await db.Chapters.AsNoTracking()
            .Where(chapter => chapter.ProjectId == projectId)
            .Select(chapter => new { chapter.Id, chapter.ActId })
            .ToListAsync(cancellationToken);
        var chapterParents = coreChapters.ToDictionary(chapter => chapter.Id, chapter => chapter.ActId);
        ValidateOutlineOrder(orderedItems, acts, chapterParents);
        var before = await FingerprintAsync(projectId, editionId, cancellationToken);
        var existing = await db.PublicationEditionOutlineItems
            .Where(item => item.EditionId == editionId)
            .ToListAsync(cancellationToken);
        var inherited = await db.PublicationBookOutlineItems.AsNoTracking()
            .Where(item => item.ProjectId == projectId)
            .ToDictionaryAsync(item => (item.TargetKind, item.TargetId), cancellationToken);
        for (var sortOrder = 0; sortOrder < orderedItems.Count; sortOrder++)
        {
            var ordered = orderedItems[sortOrder];
            var item = existing.FirstOrDefault(candidate =>
                candidate.TargetKind == ordered.TargetKind && candidate.TargetId == ordered.TargetId);
            var coreItem = inherited.GetValueOrDefault((ordered.TargetKind, ordered.TargetId));
            if (coreItem is not null && coreItem.SortOrder == sortOrder
                && (item is null || item.IsIncluded == coreItem.IsIncluded))
            {
                if (item is not null)
                {
                    db.PublicationEditionOutlineItems.Remove(item);
                    existing.Remove(item);
                }
                continue;
            }
            if (item is null)
            {
                item = NewOutlineItem(editionId, ordered.TargetKind, ordered.TargetId, sortOrder);
                item.IsIncluded = coreItem?.IsIncluded ?? true;
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
            var storedMatter = await db.PublicationMatter.FirstOrDefaultAsync(
                candidate => candidate.EditionId == editionId && candidate.Id == id,
                cancellationToken);
            if (storedMatter is null)
            {
                var inherited = await db.PublicationBookMatter.AsNoTracking().SingleOrDefaultAsync(
                    candidate => candidate.ProjectId == projectId && candidate.Id == id,
                    cancellationToken) ?? throw new InvalidOperationException("Publication matter was not found.");
                if (input.ExpectedRevision != inherited.Revision)
                    throw new DbUpdateConcurrencyException("Inherited Core Book matter changed in another editor.");
                matter = new PublicationMatter
                {
                    EditionId = editionId,
                    CoreMatterId = inherited.Id,
                    Title = inherited.Title,
                    Revision = checked(inherited.Revision + 1),
                };
                db.PublicationMatter.Add(matter);
                document = document with { ManuscriptId = matter.Id };
            }
            else
            {
                matter = storedMatter;
                if (input.ExpectedRevision != matter.Revision)
                    throw new DbUpdateConcurrencyException("Publication matter changed in another editor.");
                matter.Revision++;
            }
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
        matter.IsExcluded = false;
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
        var inherited = matter is null
            ? await db.PublicationBookMatter.AsNoTracking().SingleOrDefaultAsync(
                candidate => candidate.ProjectId == projectId && candidate.Id == matterId, cancellationToken)
            : matter.CoreMatterId is Guid coreId
                ? await db.PublicationBookMatter.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == coreId, cancellationToken)
                : null;
        if (matter is null && inherited is null) return;
        var before = await FingerprintAsync(projectId, editionId, cancellationToken);
        if (inherited is not null)
        {
            matter ??= new PublicationMatter
            {
                EditionId = editionId,
                CoreMatterId = inherited.Id,
                Location = inherited.Location,
                Kind = inherited.Kind,
                Title = inherited.Title,
                ManuscriptJson = inherited.ManuscriptJson,
                Revision = inherited.Revision,
                IsIncluded = inherited.IsIncluded,
                SortOrder = inherited.SortOrder,
            };
            if (db.Entry(matter).State == EntityState.Detached)
                db.PublicationMatter.Add(matter);
            matter.IsExcluded = true;
            matter.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            db.PublicationMatter.Remove(matter!);
        }
        await SaveWithAuditAsync(edition, "delete-matter", before, new { matterId }, cancellationToken);
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
            Language = PublicationLanguage.Normalize(input.Language),
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
            cancellationToken);
        if (placement is null)
        {
            var inherited = await db.PublicationBookImagePlacements.AsNoTracking().SingleOrDefaultAsync(
                candidate => candidate.ProjectId == projectId && candidate.Id == placementId,
                cancellationToken) ?? throw new InvalidOperationException("Image placement was not found.");
            placement = new PublicationImagePlacement
            {
                EditionId = editionId,
                CorePlacementId = inherited.Id,
                AssetId = inherited.AssetId,
                TargetKind = inherited.TargetKind,
                TargetId = inherited.TargetId,
                ActId = inherited.ActId,
                ChapterId = inherited.ChapterId,
                PlacementKind = inherited.PlacementKind,
                Caption = inherited.Caption,
                PresentationJson = inherited.PresentationJson,
                AltText = inherited.AltText,
                Decorative = inherited.Decorative,
                Language = inherited.Language,
                AccessibilityRole = inherited.AccessibilityRole,
                SortOrder = inherited.SortOrder,
            };
            db.PublicationImagePlacements.Add(placement);
        }
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
        placement.Language = PublicationLanguage.Normalize(input.Language);
        placement.AccessibilityRole = input.AccessibilityRole;
        placement.IsExcluded = false;
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
        var effective = await effectiveConfigurations.ResolveReleaseAsync(projectId, editionId, cancellationToken);
        var effectivePlacements = effective.ImagePlacements.Where(placement => orderedPlacementIds.Contains(placement.Id)).ToList();
        if (effectivePlacements.Count != orderedPlacementIds.Count)
            throw new InvalidOperationException("One or more image placements were not found.");
        if (effectivePlacements.Count > 0)
        {
            var first = effectivePlacements[0];
            if (effectivePlacements.Any(placement => placement.TargetKind != first.TargetKind
                || placement.TargetId != first.TargetId
                || placement.PlacementKind != first.PlacementKind))
            {
                throw new InvalidOperationException("Only placements at the same target and position can be reordered together.");
            }
            var groupCount = effective.ImagePlacements.Count(placement => placement.TargetKind == first.TargetKind
                && placement.TargetId == first.TargetId && placement.PlacementKind == first.PlacementKind);
            if (groupCount != orderedPlacementIds.Count)
                throw new InvalidOperationException("Placement reorder must include every image in the target group.");
        }
        var before = await FingerprintAsync(projectId, editionId, cancellationToken);
        var order = orderedPlacementIds.Select((id, index) => (id, index)).ToDictionary(item => item.id, item => item.index);
        var localRows = await db.PublicationImagePlacements.Where(item => item.EditionId == editionId).ToListAsync(cancellationToken);
        var coreRows = await db.PublicationBookImagePlacements.AsNoTracking().Where(item => item.ProjectId == projectId).ToListAsync(cancellationToken);
        foreach (var effectivePlacement in effectivePlacements)
        {
            var core = coreRows.FirstOrDefault(item => item.Id == effectivePlacement.Id)
                ?? coreRows.FirstOrDefault(item => item.Id == effectivePlacement.CorePlacementId);
            var placement = localRows.FirstOrDefault(item => item.Id == effectivePlacement.Id)
                ?? localRows.FirstOrDefault(item => item.CorePlacementId == effectivePlacement.Id);
            var desiredOrder = order[effectivePlacement.Id];
            if (placement is null && core is not null && core.SortOrder != desiredOrder)
            {
                placement = new PublicationImagePlacement
                {
                    EditionId = editionId, CorePlacementId = core.Id, AssetId = core.AssetId,
                    TargetKind = core.TargetKind, TargetId = core.TargetId, ActId = core.ActId, ChapterId = core.ChapterId,
                    PlacementKind = core.PlacementKind, Caption = core.Caption, PresentationJson = core.PresentationJson,
                    AltText = core.AltText, Decorative = core.Decorative, Language = core.Language,
                    AccessibilityRole = core.AccessibilityRole, SortOrder = desiredOrder,
                };
                db.PublicationImagePlacements.Add(placement);
                localRows.Add(placement);
            }
            else if (placement is not null)
            {
                placement.SortOrder = desiredOrder;
                placement.UpdatedAt = DateTime.UtcNow;
            }
        }
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
        var inherited = placement is null
            ? await db.PublicationBookImagePlacements.AsNoTracking().SingleOrDefaultAsync(
                candidate => candidate.ProjectId == projectId && candidate.Id == placementId, cancellationToken)
            : placement.CorePlacementId is Guid coreId
                ? await db.PublicationBookImagePlacements.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == coreId, cancellationToken)
                : null;
        if (placement is null && inherited is null) return;
        var before = await FingerprintAsync(projectId, editionId, cancellationToken);
        if (inherited is not null)
        {
            placement ??= new PublicationImagePlacement
            {
                EditionId = editionId,
                CorePlacementId = inherited.Id,
                AssetId = inherited.AssetId,
                TargetKind = inherited.TargetKind,
                TargetId = inherited.TargetId,
                ActId = inherited.ActId,
                ChapterId = inherited.ChapterId,
                PlacementKind = inherited.PlacementKind,
                Caption = inherited.Caption,
                PresentationJson = inherited.PresentationJson,
                AltText = inherited.AltText,
                Decorative = inherited.Decorative,
                Language = inherited.Language,
                AccessibilityRole = inherited.AccessibilityRole,
                SortOrder = inherited.SortOrder,
            };
            if (db.Entry(placement).State == EntityState.Detached)
                db.PublicationImagePlacements.Add(placement);
            placement.IsExcluded = true;
            placement.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            await CompactPlacementGroupAsync(
                editionId,
                placement!.TargetKind,
                placement.TargetId,
                placement.PlacementKind,
                placement.Id,
                cancellationToken);
            db.PublicationImagePlacements.Remove(placement);
        }
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
            ?? throw new InvalidOperationException("Left publication release was not found.");
        var right = editions.FirstOrDefault(edition => edition.Id == rightEditionId)
            ?? throw new InvalidOperationException("Right publication release was not found.");
        var differences = new List<string>();
        AddDifference(differences, "Format", left.Format, right.Format);
        AddDifference(differences, "Vendor", left.Vendor, right.Vendor);
        AddDifference(differences, "ISBN", left.Isbn, right.Isbn);
        AddDifference(differences, "Trim", $"{left.PageWidthInches}x{left.PageHeightInches}", $"{right.PageWidthInches}x{right.PageHeightInches}");
        AddDifference(differences, "Binding", left.Binding, right.Binding);
        AddDifference(differences, "Paper", left.Paper, right.Paper);
        AddDifference(differences, "Ink", left.Ink, right.Ink);
        var leftItems = (await effectiveConfigurations.ResolveReleaseAsync(projectId, leftEditionId, cancellationToken))
            .OutlineItems.Count(item => item.IsIncluded);
        var rightItems = (await effectiveConfigurations.ResolveReleaseAsync(projectId, rightEditionId, cancellationToken))
            .OutlineItems.Count(item => item.IsIncluded);
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
        var effective = await effectiveConfigurations.ResolveReleaseAsync(projectId, editionId, cancellationToken);
        var edition = effective.Edition;
        var items = effective.OutlineItems
            .OrderBy(item => item.SortOrder)
            .Select(item => new { item.TargetKind, item.TargetId, item.IsIncluded, item.SortOrder })
            .ToList();
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
        var coreChapters = await db.Chapters.AsNoTracking()
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
        var chapterOverrides = edition.EditionSpecificContentEnabled
            ? await db.PublicationEditionChapterOverrides.AsNoTracking()
                .Where(item => item.EditionId == editionId)
                .ToDictionaryAsync(item => item.ChapterId, cancellationToken)
            : [];
        var chapters = coreChapters.Select(chapter =>
        {
            chapterOverrides.TryGetValue(chapter.Id, out var chapterOverride);
            return new
            {
                chapter.Id,
                chapter.ActId,
                chapter.Title,
                chapter.Synopsis,
                chapter.Order,
                ManuscriptRevision = chapterOverride?.Revision ?? chapter.ManuscriptRevision,
                ManuscriptJson = chapterOverride?.ManuscriptJson ?? chapter.ManuscriptJson,
            };
        }).ToList();
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
        var matter = effective.Matter
            .OrderBy(item => item.Location).ThenBy(item => item.SortOrder).ThenBy(item => item.Id)
            .Select(item => new { item.Id, item.Kind, item.Location, item.Title, item.ManuscriptJson, item.Revision, item.IsIncluded, item.SortOrder })
            .ToList();
        var placements = effective.ImagePlacements
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
            .ToList();
        var referencedStyleRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var document in chapters.Select(chapter => ManuscriptCodec.Deserialize(chapter.ManuscriptJson, chapter.Id, chapter.ManuscriptRevision))
            .Concat(compositions.Select(composition => ManuscriptCodec.Deserialize(composition.SemanticManuscriptJson, composition.Id, composition.Revision)))
            .Concat(matter.Select(item => ManuscriptCodec.Deserialize(item.ManuscriptJson, item.Id, item.Revision))))
        {
            foreach (var block in document.Content)
            {
                if (!string.IsNullOrWhiteSpace(block.StyleRole))
                    referencedStyleRoles.Add(block.StyleRole);
                foreach (var role in block.Content.SelectMany(inline => inline.Marks)
                    .Where(mark => mark.Type == ManuscriptMarkType.CharacterStyle && !string.IsNullOrWhiteSpace(mark.Value))
                    .Select(mark => mark.Value!))
                    referencedStyleRoles.Add(role);
            }
        }
        var styles = await db.ManuscriptStyleDefinitions.AsNoTracking()
            .Where(style => style.ProjectId == projectId && referencedStyleRoles.Contains(style.SemanticRole))
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
        var inheritedCoreCover = edition.InheritsCoreCover
            ? await db.PublicationBookCoverDesigns.AsNoTracking()
                .Where(design => design.ProjectId == projectId)
                .Select(design => new
                {
                    Source = "CoreBook",
                    Title = edition.TitleOverride,
                    edition.Subtitle,
                    edition.Author,
                    design.BackgroundColor,
                    design.CompositionSceneJson,
                    design.Revision,
                })
                .SingleOrDefaultAsync(cancellationToken)
            : null;
        var normalizedReleaseCoverScene = coverDesign is null
            ? null
            : NormalizeCoverSceneJson(coverDesign.CompositionSceneJson);
        var normalizedCoreCoverScene = inheritedCoreCover is null
            ? null
            : NormalizeCoverSceneJson(inheritedCoreCover.CompositionSceneJson);
        object? canonicalCoverDesign = null;
        if (includeCover && inheritedCoreCover is not null)
        {
            canonicalCoverDesign = new
            {
                inheritedCoreCover.Source,
                inheritedCoreCover.Title,
                inheritedCoreCover.Subtitle,
                inheritedCoreCover.Author,
                inheritedCoreCover.BackgroundColor,
                CompositionSceneJson = normalizedCoreCoverScene,
                inheritedCoreCover.Revision,
            };
        }
        else if (includeCover && coverDesign is not null)
        {
            canonicalCoverDesign = new
            {
                coverDesign.Title,
                coverDesign.Subtitle,
                coverDesign.Author,
                coverDesign.SpineText,
                coverDesign.BackCopy,
                coverDesign.BackgroundColor,
                coverDesign.BarcodeMode,
                coverDesign.ImageCropXPercent,
                coverDesign.ImageCropYPercent,
                CompositionSceneJson = normalizedReleaseCoverScene,
            };
        }
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
            if (normalizedCoreCoverScene is not null)
                CollectReferencedImageIds(normalizedCoreCoverScene, referencedAssetIds);
            else if (normalizedReleaseCoverScene is not null)
                CollectReferencedImageIds(normalizedReleaseCoverScene, referencedAssetIds);
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
                edition.InheritsCoreCover,
                SelectedCoverImageId = includeCover ? edition.SelectedCoverImageId : null,
            },
            Items = items,
            Acts = acts,
            Chapters = chapters,
            Compositions = compositions,
            Matter = matter,
            Placements = placements,
            Assets = assets,
            Styles = styles,
            Fonts = fonts,
            CoverDesign = canonicalCoverDesign,
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

    private static string NormalizeCoverSceneJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return json;
        var scene = JsonSerializer.Deserialize<CompositionScene>(json, ManuscriptCodec.JsonOptions)
            ?? throw new InvalidDataException("The cover composition scene is empty.");
        return JsonSerializer.Serialize(
            CoverCompositionFactory.KeepArtworkBehindCopy(scene),
            ManuscriptCodec.JsonOptions);
    }

    private async Task<string> BibliographicContentHashAsync(
        Guid editionId,
        CancellationToken cancellationToken)
    {
        var projectId = await db.PublicationEditions.AsNoTracking().Where(item => item.Id == editionId)
            .Select(item => item.ProjectId).SingleAsync(cancellationToken);
        var effective = await effectiveConfigurations.ResolveReleaseAsync(projectId, editionId, cancellationToken);
        var outline = effective.OutlineItems.OrderBy(item => item.SortOrder)
            .Select(item => new { item.TargetKind, item.TargetId, item.IsIncluded, item.SortOrder }).ToList();
        var matter = effective.Matter.OrderBy(item => item.Location).ThenBy(item => item.SortOrder).ThenBy(item => item.Id).ToList();
        var placements = effective.ImagePlacements
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
            .ToList();
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
            new { Outline = outline, Matter = canonicalMatter, Placements = placements },
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
        var included = (await effectiveConfigurations.ResolveReleaseAsync(projectId, editionId, cancellationToken))
            .OutlineItems.FirstOrDefault(item => item.TargetKind == targetKind && item.TargetId == targetId)?.IsIncluded ?? true;
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

    private static void ValidateReleaseState(PublicationEdition edition)
    {
        ValidateIdentity(edition.Name, edition.Format, edition.Vendor);
        if (!Enum.IsDefined(edition.Binding) || !Enum.IsDefined(edition.Paper) || !Enum.IsDefined(edition.Ink)
            || !Enum.IsDefined(edition.TitlePageMode))
            throw new InvalidOperationException("One or more release settings are invalid.");
        if (edition.Format == PublicationEditionFormat.Paperback)
        {
            if (edition.Binding != PublicationBinding.PerfectBound
                || edition.Paper is not (PublicationPaper.White or PublicationPaper.Cream)
                || edition.Ink is not (PublicationInk.BlackAndWhite or PublicationInk.Color))
                throw new InvalidOperationException("Paperback releases require print binding, paper, and interior-color settings.");
        }
        else if (edition.Vendor != PublicationVendor.Generic
            || edition.Binding != PublicationBinding.Digital
            || edition.Paper != PublicationPaper.Digital
            || edition.Ink != PublicationInk.Digital
            || edition.Bleed)
        {
            throw new InvalidOperationException("Digital releases use application-managed digital product settings.");
        }
        if (!double.IsFinite(edition.PageWidthInches) || edition.PageWidthInches is < 3 or > 24
            || !double.IsFinite(edition.PageHeightInches) || edition.PageHeightInches is < 3 or > 24
            || !double.IsFinite(edition.PageMarginInches) || edition.PageMarginInches < .125
            || edition.PageMarginInches > Math.Min(edition.PageWidthInches, edition.PageHeightInches) / 3)
            throw new InvalidOperationException("Release page settings are outside supported limits.");
        if (edition.TitleOverride.Length > 500 || edition.Subtitle.Length > 500 || edition.Author.Length > 500
            || edition.Language.Length > 40 || edition.Publisher.Length > 500
            || edition.Copyright.Length > 100_000 || edition.Description.Length > 100_000)
            throw new InvalidOperationException("One or more release overrides exceed supported limits.");
        if (!PublicationLanguage.IsPressSupported(edition.Language))
            throw new InvalidOperationException("Choose English, English (United States), or English (United Kingdom) as the release language.");
    }

    private async Task<Project> GetProjectAsync(Guid projectId, CancellationToken cancellationToken) =>
        await db.Projects.FirstOrDefaultAsync(project => project.Id == projectId, cancellationToken)
        ?? throw new InvalidOperationException($"Project {projectId:N} was not found.");

    private async Task<PublicationEdition> GetTrackedAsync(
        Guid projectId,
        Guid editionId,
        CancellationToken cancellationToken)
    {
        if (db.PublicationEditions.Local.FirstOrDefault(edition => edition.Id == editionId) is { } tracked)
            await db.Entry(tracked).ReloadAsync(cancellationToken);

        return await db.PublicationEditions.FirstOrDefaultAsync(
            edition => edition.ProjectId == projectId && edition.Id == editionId,
            cancellationToken)
            ?? throw new InvalidOperationException("Publication release was not found.");
    }

    private async Task<PublicationEdition> GetReadOnlyAsync(Guid projectId, Guid editionId, CancellationToken cancellationToken) =>
        await db.PublicationEditions.AsNoTracking().FirstOrDefaultAsync(
            edition => edition.ProjectId == projectId && edition.Id == editionId,
            cancellationToken)
        ?? throw new InvalidOperationException("Publication release was not found.");

    private static void EnsureRevision(PublicationEdition edition, long expectedRevision)
    {
        if (edition.Revision != expectedRevision)
            throw new DbUpdateConcurrencyException(
                $"Publication release changed in another editor (expected revision {expectedRevision}, current {edition.Revision}).");
    }

    internal static void EnsureDraft(PublicationEdition edition)
    {
        if (edition.Status != PublicationEditionStatus.Draft)
            throw new InvalidOperationException("Archived publication releases are read-only. Clone this release to make changes.");
    }

    internal static PublicationEditionSummary Summary(PublicationEdition edition) =>
        new(
            edition.Id,
            edition.Name,
            edition.Format,
            edition.Vendor,
            edition.Status,
            edition.Revision,
            edition.PageWidthInches,
            edition.PageHeightInches,
            edition.PageMarginInches,
            edition.Bleed,
            edition.AllowDesignedPageOverrides);

    internal static PublicationEditionView View(Project project, PublicationEdition edition) =>
        new PublicationEditionView(
            edition.Id,
            edition.Name,
            edition.Format,
            edition.Vendor,
            edition.VendorProfileVersion,
            edition.Status,
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
            edition.AllowDesignedPageOverrides)
        {
            InheritsCoreCover = edition.InheritsCoreCover,
            EditionSpecificContentEnabled = edition.EditionSpecificContentEnabled,
        };

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
            CoreMatterId = source.CoreMatterId,
            Title = source.Title,
            Location = source.Location,
            Kind = source.Kind,
            IsIncluded = source.IsIncluded,
            SortOrder = source.SortOrder,
            Revision = source.Revision,
            IsExcluded = source.IsExcluded,
        };
        var document = ManuscriptCodec.Deserialize(source.ManuscriptJson, source.Id, source.Revision);
        copy.ManuscriptJson = ManuscriptCodec.Serialize(document with { ManuscriptId = copy.Id });
        return copy;
    }

    private static PublicationImagePlacement CopyPlacement(PublicationImagePlacement source) =>
        new()
        {
            CorePlacementId = source.CorePlacementId,
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
            IsExcluded = source.IsExcluded,
        };

    private static PageComposition CopyEditionComposition(PageComposition source, Guid editionId, Guid id)
    {
        var clone = new PageComposition
        {
            Id = id,
            ProjectId = source.ProjectId,
            ChapterId = source.ChapterId,
            EditionId = editionId,
            SourceCompositionId = source.SourceCompositionId,
            Name = source.Name,
            SemanticManuscriptJson = source.SemanticManuscriptJson,
            Revision = source.Revision,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var variantMap = source.Variants.ToDictionary(item => item.Id, _ => Guid.NewGuid());
        clone.Variants = source.Variants.Select(item => new PageCompositionVariant
        {
            Id = variantMap[item.Id],
            CompositionId = id,
            Composition = clone,
            GeometryKey = item.GeometryKey,
            SceneJson = item.SceneJson,
            Revision = item.Revision,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        }).ToList();
        if (source.ActiveAuthoringVariantId is Guid activeId && variantMap.TryGetValue(activeId, out var cloneActiveId))
            clone.ActiveAuthoringVariantId = cloneActiveId;
        return clone;
    }

    private static PublicationEdition CopyEdition(PublicationEdition source, string name) =>
        new()
        {
            ProjectId = source.ProjectId,
            Name = name,
            Format = source.Format,
            Vendor = source.Vendor,
            VendorProfileVersion = source.VendorProfileVersion,
            Status = PublicationEditionStatus.Draft,
            OverrideFieldsJson = source.OverrideFieldsJson,
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
            InheritsCoreCover = source.InheritsCoreCover,
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
