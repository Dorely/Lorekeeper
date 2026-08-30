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
    IAppDatabaseOperationFactory database,
    IPublicationBookService books,
    IPublicationEffectiveConfigurationResolver effectiveConfigurations,
    IPublicationReleasePresetService releasePresets,
    IPrintProductRegistry printProducts,
    IPublicationActorContext actorContext) : IPublicationEditionService
{
    public PublicationEditionService(
        IAppDatabaseOperationFactory database,
        IPublicationActorContext actorContext)
        : this(database, new PublicationBookService(database),
            new PublicationEffectiveConfigurationResolver(database),
            new PublicationReleasePresetService(database, new PrintProductRegistry()),
            new PrintProductRegistry(), actorContext)
    {
    }

    public async Task<IReadOnlyList<PublicationEditionSummary>> ListAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        return await db.PublicationEditions
                    .AsNoTracking()
                    .Where(edition => edition.ProjectId == projectId)
                    .OrderBy(edition => edition.Status)
                    .ThenBy(edition => edition.Name)
                    .Select(edition => Summary(edition))
                    .ToListAsync(cancellationToken);
    }
    public async Task<PublicationEditionView> CreateAsync(
        Guid projectId,
        PublicationEditionCreate input,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        ValidateIdentity(input.Name, input.Format, input.Vendor);
        var core = await books.GetOrCreateAsync(projectId, cancellationToken);
        var preset = await releasePresets.ResolveAsync(projectId, input.Format, input.Vendor, cancellationToken);
        var project = await GetProjectAsync(projectId, cancellationToken);
        var name = await AllocateUniqueNameAsync(projectId, input.Name.Trim(), null, cancellationToken);
        var edition = new PublicationEdition
        {
            ProjectId = projectId,
            Name = name,
            Format = input.Format,
            Vendor = preset.Vendor,
            VendorProfileVersion = preset.ProfileId,
            OverrideFieldsJson = "[]",
            InheritsCoreCover = true,
            PrintRegistryVersion = preset.RegistryVersion,
            PrintProductKey = preset.ProductKey ?? string.Empty,
            PrintFinish = preset.Finish,
            PrintCoverMode = preset.CoverMode,
            PrintProjectUse = preset.ProjectUse,
            PrintIdentifierMode = preset.IdentifierMode,
            PrintCoverSubmissionMode = preset.CoverSubmissionMode,
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
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var project = await GetProjectAsync(projectId, cancellationToken);
        var source = await GetTrackedAsync(projectId, editionId, cancellationToken);
        EnsureRevision(source, expectedRevision);
        var requestedName = name.Trim();
        ValidateIdentity(requestedName, source.Format, source.Vendor);
        var cleanName = await AllocateUniqueNameAsync(projectId, requestedName, null, cancellationToken);

        await db.Entry(source).Collection(edition => edition.OutlineItems).LoadAsync(cancellationToken);
        await db.Entry(source).Collection(edition => edition.ChapterOverrides).LoadAsync(cancellationToken);
        await db.Entry(source).Collection(edition => edition.PublicationSections).LoadAsync(cancellationToken);
        await db.Entry(source).Reference(edition => edition.CoverDesign).LoadAsync(cancellationToken);
        var clone = CopyEdition(source, cleanName);
        clone.Isbn = string.Empty;
        clone.OutlineItems = source.OutlineItems.Select(CopyOutlineItem).ToList();
        var sourceCompositions = await db.PageCompositions.AsNoTracking()
            .Include(item => item.Variants.Where(variant => variant.DetachedAt == null))
            .Where(item => item.EditionId == source.Id && item.DetachedAt == null)
            .ToListAsync(cancellationToken);
        var compositionMap = sourceCompositions.ToDictionary(item => item.Id, _ => Guid.NewGuid());
        var sectionMap = source.PublicationSections.ToDictionary(item => item.Id, _ => Guid.NewGuid());
        clone.PublicationSectionOrderJson = PublicationSectionOrderCodec.Serialize(
            PublicationSectionOrderCodec.Deserialize(source.PublicationSectionOrderJson)
                .ToDictionary(item => sectionMap.GetValueOrDefault(item.Key, item.Key), item => item.Value));
        clone.PageCompositions = sourceCompositions.Select(item => CopyEditionComposition(
            item,
            clone.Id,
            compositionMap[item.Id],
            item.PublicationSectionId is Guid sectionId ? sectionMap.GetValueOrDefault(sectionId) : null)).ToList();
        clone.PublicationSections = source.PublicationSections.Select(item =>
            CopyPublicationSection(item, clone.Id, sectionMap[item.Id], compositionMap)).ToList();
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
                SpineReadingDirection = source.CoverDesign.SpineReadingDirection,
                BackgroundColor = source.CoverDesign.BackgroundColor,
                BarcodeMode = source.CoverDesign.BarcodeMode,
                ImageCropXPercent = source.CoverDesign.ImageCropXPercent,
                ImageCropYPercent = source.CoverDesign.ImageCropYPercent,
                CompositionSceneJson = source.CoverDesign.CompositionSceneJson,
                SurfaceScenesJson = source.CoverDesign.SurfaceScenesJson,
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
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
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

        if (patch.Name is not null)
        {
            var requestedName = patch.Name.Trim();
            ValidateIdentity(requestedName, edition.Format, edition.Vendor);
            edition.Name = await AllocateUniqueNameAsync(projectId, requestedName, editionId, cancellationToken);
        }
        if (patch.Destination is { } destination)
        {
            edition.Vendor = destination;
            if (edition.Format is PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover)
            {
                var product = printProducts.GetDefault(edition.Format, destination);
                edition.PrintRegistryVersion = printProducts.Version;
                edition.PrintProductKey = product.Key;
                edition.VendorProfileVersion = product.PdfProfile;
                edition.PrintFinish = PrintFinish.Matte;
                edition.PrintCoverMode = PrintCoverMode.Simplex;
                edition.PrintProjectUse = product.DefaultProjectUse;
                edition.PrintIdentifierMode = destination == PublicationVendor.BarnesAndNoblePress
                    ? PrintIdentifierMode.VendorSku
                    : PrintIdentifierMode.UserSuppliedIsbn;
                edition.PrintCoverSubmissionMode = PrintCoverSubmissionMode.FullWrapMeasured;
            }
            else
            {
                edition.VendorProfileVersion = DefaultProfile(edition.Format, destination);
            }
        }
        if (patch.Isbn is not null) edition.Isbn = PublicationIsbn.NormalizeValidOrEmpty(patch.Isbn);
        if (patch.PrintProductKey is { } productKey)
        {
            var product = printProducts.GetRequired(productKey);
            if (product.Format != edition.Format || product.Vendor != edition.Vendor)
                throw new InvalidOperationException("The selected print product is not available for this release type and destination.");
            edition.PrintRegistryVersion = printProducts.Version;
            edition.PrintProductKey = product.Key;
            edition.VendorProfileVersion = product.PdfProfile;
            if (!product.Finishes.Contains(edition.PrintFinish)) edition.PrintFinish = product.Finishes[0];
            if (!product.CoverModes.Contains(edition.PrintCoverMode)) edition.PrintCoverMode = product.CoverModes[0];
        }
        if (patch.PrintFinish is { } finish) edition.PrintFinish = finish;
        if (patch.PrintCoverMode is { } coverMode) edition.PrintCoverMode = coverMode;
        if (patch.PrintProjectUse is { } projectUse)
        {
            edition.PrintProjectUse = projectUse;
            if (edition.Vendor == PublicationVendor.BarnesAndNoblePress)
                edition.PrintIdentifierMode = projectUse == PrintProjectUse.PersonalUse
                    ? PrintIdentifierMode.VendorSku
                    : PrintIdentifierMode.VendorAssignedIsbn;
        }
        if (patch.PrintIdentifierMode is { } identifierMode) edition.PrintIdentifierMode = identifierMode;
        if (patch.PrintCoverSubmissionMode is { } submissionMode) edition.PrintCoverSubmissionMode = submissionMode;
        if (patch.PrintTemplateEvidenceJson is not null) edition.PrintTemplateEvidenceJson = patch.PrintTemplateEvidenceJson;
        if (edition.Format == PublicationEditionFormat.DigitalPdf)
            Override(fields, PublicationEditionOverrideField.AllowDesignedPageOverrides, patch.AllowDesignedPageOverrides, value => edition.AllowDesignedPageOverrides = value);
        if (edition.Format == PublicationEditionFormat.Epub)
            fields.Remove(PublicationEditionOverrideField.RectoChapterStarts);
        else
            Override(fields, PublicationEditionOverrideField.RectoChapterStarts, patch.RectoChapterStarts, value => edition.RectoChapterStarts = value);

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
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
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
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
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

    public async Task<PublicationEditionCompareView> CompareAsync(
        Guid projectId,
        Guid leftEditionId,
        Guid rightEditionId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
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
        AddDifference(differences, "Print product", left.PrintProductKey, right.PrintProductKey);
        AddDifference(differences, "Finish", left.PrintFinish, right.PrintFinish);
        AddDifference(differences, "Cover mode", left.PrintCoverMode, right.PrintCoverMode);
        var leftEffective = await effectiveConfigurations.ResolveReleaseAsync(projectId, leftEditionId, cancellationToken);
        var rightEffective = await effectiveConfigurations.ResolveReleaseAsync(projectId, rightEditionId, cancellationToken);
        var leftItems = leftEffective.OutlineItems.Count(item => item.IsIncluded);
        var rightItems = rightEffective.OutlineItems.Count(item => item.IsIncluded);
        AddDifference(differences, "Included content", leftItems, rightItems);
        AddDifference(
            differences,
            "Start chapters on right-hand pages",
            leftEffective.Edition.RectoChapterStarts,
            rightEffective.Edition.RectoChapterStarts);
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
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
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
        await using var databaseOperation = await database.OpenWriteAsync(edition.ProjectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
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
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
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
        var publicationSections = effective.PublicationSections
            .OrderBy(item => item.Anchor)
            .ThenBy(item => item.TargetId)
            .ThenBy(item => item.LocalOrder)
            .ThenBy(item => item.Id)
            .Select(item => new
            {
                item.Id,
                item.CoreSectionId,
                item.Title,
                item.Kind,
                item.SystemRole,
                item.Anchor,
                item.TargetKind,
                item.TargetId,
                item.InclusionMode,
                item.StartSide,
                item.LocalOrder,
                item.ManuscriptJson,
                item.Revision,
            })
            .ToList();
        compositionIds.AddRange(publicationSections
            .SelectMany(item => ManuscriptCodec.Deserialize(item.ManuscriptJson, item.Id, item.Revision).Content)
            .Where(block => block.Type == ManuscriptBlockType.DesignedPage && block.PageCompositionId.HasValue)
            .Select(block => block.PageCompositionId!.Value));
        compositionIds = compositionIds.Distinct().OrderBy(id => id).ToList();
        var compositions = await db.PageCompositions.AsNoTracking()
            .Where(composition => compositionIds.Contains(composition.Id) && composition.DetachedAt == null)
            .OrderBy(composition => composition.Id)
            .Select(composition => new
            {
                composition.Id,
                composition.ChapterId,
                composition.Name,
                composition.SemanticManuscriptJson,
                composition.Revision,
                Variants = composition.Variants.Where(variant => variant.DetachedAt == null)
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
        var referencedStyleRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var document in chapters.Select(chapter => ManuscriptCodec.Deserialize(chapter.ManuscriptJson, chapter.Id, chapter.ManuscriptRevision))
            .Concat(compositions.Select(composition => ManuscriptCodec.Deserialize(composition.SemanticManuscriptJson, composition.Id, composition.Revision)))
            .Concat(publicationSections.Select(item => ManuscriptCodec.Deserialize(item.ManuscriptJson, item.Id, item.Revision))))
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
                design.SpineReadingDirection,
                design.CompositionSceneJson,
                design.SurfaceScenesJson,
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
        var normalizedReleaseSurfaceScenes = coverDesign is null
            ? null
            : NormalizeCoverSurfaceScenesJson(coverDesign.SurfaceScenesJson);
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
                coverDesign.SpineReadingDirection,
                CompositionSceneJson = normalizedReleaseCoverScene,
                SurfaceScenesJson = normalizedReleaseSurfaceScenes,
            };
        }
        var referencedAssetIds = new HashSet<Guid>();
        foreach (var chapter in chapters)
            CollectReferencedImageIds(chapter.ManuscriptJson, referencedAssetIds);
        foreach (var item in publicationSections)
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
            if (normalizedReleaseSurfaceScenes is not null)
                CollectReferencedImageIds(normalizedReleaseSurfaceScenes, referencedAssetIds);
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
                edition.RectoChapterStarts,
                edition.PrintRegistryVersion,
                edition.PrintProductKey,
                edition.PrintFinish,
                edition.PrintCoverMode,
                edition.PrintProjectUse,
                edition.PrintIdentifierMode,
                edition.PrintCoverSubmissionMode,
                edition.PrintTemplateEvidenceJson,
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
            PublicationSections = publicationSections,
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

    private static string NormalizeCoverSurfaceScenesJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "{}";
        var scenes = JsonSerializer.Deserialize<Dictionary<string, string>>(json, ManuscriptCodec.JsonOptions)
            ?? new Dictionary<string, string>();
        return JsonSerializer.Serialize(
            scenes.OrderBy(item => item.Key, StringComparer.Ordinal)
                .ToDictionary(item => item.Key, item => NormalizeCoverSceneJson(item.Value), StringComparer.Ordinal),
            ManuscriptCodec.JsonOptions);
    }

    private async Task<string> BibliographicContentHashAsync(
        Guid editionId,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var projectId = await db.PublicationEditions.AsNoTracking().Where(item => item.Id == editionId)
            .Select(item => item.ProjectId).SingleAsync(cancellationToken);
        var effective = await effectiveConfigurations.ResolveReleaseAsync(projectId, editionId, cancellationToken);
        var outline = effective.OutlineItems.OrderBy(item => item.SortOrder)
            .Select(item => new { item.TargetKind, item.TargetId, item.IsIncluded, item.SortOrder }).ToList();
        var publicationSections = effective.PublicationSections
            .OrderBy(item => item.Anchor).ThenBy(item => item.TargetId).ThenBy(item => item.LocalOrder).ThenBy(item => item.Id)
            .Select(item => new
            {
                item.CoreSectionId,
                item.Title,
                item.Kind,
                item.SystemRole,
                item.Anchor,
                item.TargetKind,
                item.TargetId,
                item.InclusionMode,
                item.StartSide,
                item.LocalOrder,
                Content = CanonicalManuscriptContent(ManuscriptCodec.Deserialize(item.ManuscriptJson, item.Id, item.Revision)),
            });
        var canonical = JsonSerializer.Serialize(
            new { Outline = outline, PublicationSections = publicationSections },
            ManuscriptCodec.JsonOptions);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private async Task EnsureSharedIsbnContentMutableAsync(
        PublicationEdition edition,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
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

    private async Task EnsureTargetsAsync(
        Guid projectId,
        IReadOnlyList<PublicationEditionOutlineItemUpdate> updates,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var actIds = updates.Where(update => update.TargetKind == PublishOutlineTargetKind.Act).Select(update => update.TargetId).Distinct().ToList();
        var chapterIds = updates.Where(update => update.TargetKind == PublishOutlineTargetKind.Chapter).Select(update => update.TargetId).Distinct().ToList();
        if (actIds.Count != await db.Acts.CountAsync(act => act.ProjectId == projectId && actIds.Contains(act.Id), cancellationToken)
            || chapterIds.Count != await db.Chapters.CountAsync(chapter => chapter.ProjectId == projectId && chapterIds.Contains(chapter.Id), cancellationToken))
        {
            throw new InvalidOperationException("One or more edition content targets were not found in this project.");
        }
    }

    private static void ValidateIdentity(string name, PublicationEditionFormat format, PublicationVendor vendor)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 120)
            throw new InvalidOperationException("Edition name is required and cannot exceed 120 characters.");
        if (!Enum.IsDefined(format) || !Enum.IsDefined(vendor))
            throw new InvalidOperationException("Edition format or vendor is invalid.");
    }

    private async Task<string> AllocateUniqueNameAsync(
        Guid projectId,
        string requestedName,
        Guid? excludedEditionId,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var names = await db.PublicationEditions.AsNoTracking()
                .Where(edition => edition.ProjectId == projectId
                    && (!excludedEditionId.HasValue || edition.Id != excludedEditionId.Value))
                .Select(edition => edition.Name)
                .ToListAsync(cancellationToken);
        return PublicationReleaseNaming.AllocateUnique(requestedName, names);
    }

    private void ValidateReleaseState(PublicationEdition edition)
    {
        ValidateIdentity(edition.Name, edition.Format, edition.Vendor);
        if (!Enum.IsDefined(edition.PrintFinish) || !Enum.IsDefined(edition.PrintCoverMode)
            || !Enum.IsDefined(edition.PrintProjectUse) || !Enum.IsDefined(edition.PrintIdentifierMode)
            || !Enum.IsDefined(edition.PrintCoverSubmissionMode)
            || !Enum.IsDefined(edition.TitlePageMode))
            throw new InvalidOperationException("One or more release settings are invalid.");
        if (edition.Format is PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover)
        {
            if (string.IsNullOrWhiteSpace(edition.PrintProductKey))
                throw new InvalidOperationException("Print releases require an exact print product.");
            var product = printProducts.GetRequired(edition.PrintProductKey);
            if (product.Format != edition.Format || product.Vendor != edition.Vendor)
                throw new InvalidOperationException("The selected print product does not belong to this release type and destination.");
            if (!product.Finishes.Contains(edition.PrintFinish) || !product.CoverModes.Contains(edition.PrintCoverMode))
                throw new InvalidOperationException("The selected finish or cover mode is unavailable for this exact print product.");
            if (!product.EffectiveSupportedProjectUses.Contains(edition.PrintProjectUse))
                throw new InvalidOperationException("The selected project use is unavailable for this exact print product.");
            if (edition.Vendor == PublicationVendor.BarnesAndNoblePress)
            {
                if (edition.PrintProjectUse == PrintProjectUse.PersonalUse
                    && edition.PrintIdentifierMode != PrintIdentifierMode.VendorSku)
                    throw new InvalidOperationException("B&N Press personal-use projects use a vendor SKU.");
                if (edition.PrintProjectUse == PrintProjectUse.ForSale
                    && edition.PrintIdentifierMode == PrintIdentifierMode.VendorSku)
                    throw new InvalidOperationException("B&N Press for-sale projects require a user-supplied or vendor-assigned ISBN.");
                if (edition.PrintIdentifierMode == PrintIdentifierMode.UserSuppliedIsbn
                    && string.IsNullOrWhiteSpace(edition.Isbn))
                    throw new InvalidOperationException("A valid ISBN-13 is required when user-supplied ISBN is selected.");
            }
            else if (edition.PrintCoverSubmissionMode != PrintCoverSubmissionMode.FullWrapMeasured)
            {
                throw new InvalidOperationException("Separate vendor-spine submission is currently supported only for B&N Press.");
            }
            var trimMatches = product.TrimSizes.Any(trim =>
            {
                var parts = trim.Split('x', StringSplitOptions.TrimEntries);
                return parts.Length == 2
                    && double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var width)
                    && double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var height)
                    && Math.Abs(width - edition.PageWidthInches) < .0001
                    && Math.Abs(height - edition.PageHeightInches) < .0001;
            });
            if (!trimMatches && !product.AllowsCustomTrim)
                throw new InvalidOperationException($"{product.DisplayName} is unavailable at {edition.PageWidthInches:0.###} x {edition.PageHeightInches:0.###} inches.");
        }
        else if (edition.Vendor != PublicationVendor.Generic || edition.Bleed)
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

    private async Task<Project> GetProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        return await db.Projects.FirstOrDefaultAsync(project => project.Id == projectId, cancellationToken)
                ?? throw new InvalidOperationException($"Project {projectId:N} was not found.");
    }
    private async Task<PublicationEdition> GetTrackedAsync(
        Guid projectId,
        Guid editionId,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        return await db.PublicationEditions.AsTracking().FirstOrDefaultAsync(
                    edition => edition.ProjectId == projectId && edition.Id == editionId,
                    cancellationToken)
                    ?? throw new InvalidOperationException("Publication release was not found.");
    }
    private async Task<PublicationEdition> GetReadOnlyAsync(Guid projectId, Guid editionId, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        return await db.PublicationEditions.AsNoTracking().FirstOrDefaultAsync(
                    edition => edition.ProjectId == projectId && edition.Id == editionId,
                    cancellationToken)
                ?? throw new InvalidOperationException("Publication release was not found.");
    }
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
            edition.AllowDesignedPageOverrides,
            edition.RectoChapterStarts)
        {
            PrintProductKey = edition.PrintProductKey,
            PrintCoverMode = edition.PrintCoverMode,
            PrintProjectUse = edition.PrintProjectUse,
        };

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
            edition.PrintRegistryVersion,
            edition.PrintProductKey,
            edition.PrintFinish,
            edition.PrintCoverMode,
            edition.PrintTemplateEvidenceJson,
            edition.Bleed,
            edition.AllowDesignedPageOverrides,
            edition.RectoChapterStarts)
        {
            InheritsCoreCover = edition.InheritsCoreCover,
            EditionSpecificContentEnabled = edition.EditionSpecificContentEnabled,
            PrintProjectUse = edition.PrintProjectUse,
            PrintIdentifierMode = edition.PrintIdentifierMode,
            PrintCoverSubmissionMode = edition.PrintCoverSubmissionMode,
        };

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

    private static PublicationSection CopyPublicationSection(
        PublicationSection source,
        Guid editionId,
        Guid id,
        IReadOnlyDictionary<Guid, Guid> compositionMap)
    {
        var document = ManuscriptCodec.Deserialize(source.ManuscriptJson, source.Id, source.Revision);
        document = document with
        {
            ManuscriptId = id,
            Content = document.Content.Select(block =>
                block.PageCompositionId is Guid compositionId && compositionMap.TryGetValue(compositionId, out var cloneCompositionId)
                    ? block with { PageCompositionId = cloneCompositionId }
                    : block).ToList(),
        };
        return new PublicationSection
        {
            Id = id,
            ProjectId = source.ProjectId,
            EditionId = editionId,
            CoreSectionId = source.CoreSectionId,
            IsExcluded = source.IsExcluded,
            Title = source.Title,
            Kind = source.Kind,
            SystemRole = source.SystemRole,
            Anchor = source.Anchor,
            TargetKind = source.TargetKind,
            TargetId = source.TargetId,
            ActId = source.ActId,
            ChapterId = source.ChapterId,
            InclusionMode = source.InclusionMode,
            StartSide = source.StartSide,
            LocalOrder = source.LocalOrder,
            ManuscriptJson = ManuscriptCodec.Serialize(document),
            Revision = source.Revision,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
    }

    private static PageComposition CopyEditionComposition(
        PageComposition source,
        Guid editionId,
        Guid id,
        Guid? publicationSectionId)
    {
        var clone = new PageComposition
        {
            Id = id,
            ProjectId = source.ProjectId,
            ChapterId = source.ChapterId,
            PublicationSectionId = publicationSectionId,
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
            RectoChapterStarts = source.RectoChapterStarts,
            PrintRegistryVersion = source.PrintRegistryVersion,
            PrintProductKey = source.PrintProductKey,
            PrintProjectUse = source.PrintProjectUse,
            PrintIdentifierMode = source.PrintIdentifierMode,
            PrintFinish = source.PrintFinish,
            PrintCoverMode = source.PrintCoverMode,
            PrintCoverSubmissionMode = source.PrintCoverSubmissionMode,
            PrintTemplateEvidenceJson = source.PrintTemplateEvidenceJson,
            Bleed = source.Bleed,
            PageWidthInches = source.PageWidthInches,
            PageHeightInches = source.PageHeightInches,
            PageMarginInches = source.PageMarginInches,
            BodyFontSizePoints = source.BodyFontSizePoints,
            BodyLineHeight = source.BodyLineHeight,
            SelectedCoverImageId = source.SelectedCoverImageId,
            AllowDesignedPageOverrides = source.AllowDesignedPageOverrides,
            InheritsCoreCover = source.InheritsCoreCover,
            EditionSpecificContentEnabled = source.EditionSpecificContentEnabled,
            PublicationSectionOrderJson = source.PublicationSectionOrderJson,
        };

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;

    private static void AddDifference<T>(List<string> differences, string label, T left, T right)
    {
        if (!EqualityComparer<T>.Default.Equals(left, right))
            differences.Add($"{label}: {left} → {right}");
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

}
