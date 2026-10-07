using System.Text.Json;
using Lorekeeper.Authoring;
using Lorekeeper.Citations;
using Lorekeeper.Composition;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Publish;

public sealed record PublicationBookView(
    Guid ProjectId,
    long Revision,
    string Title,
    string Subtitle,
    string Author,
    string Language,
    string Publisher,
    string Copyright,
    string Description,
    bool IncludeTableOfContents,
    bool IncludeVisibleTableOfContents,
    bool IncludeActSynopses,
    bool IncludeChapterSynopses,
    bool IncludeActHeadings,
    bool IncludeChapterHeadings,
    bool NumberActs,
    bool NumberChapters,
    PublishTitlePageMode TitlePageMode,
    bool AllowDesignedPageOverrides,
    bool RectoChapterStarts,
    ProjectPageSetupView PageSetup,
    int IncludedChapterCount,
    int PublicationSectionCount,
    long CoverRevision)
{
    public CitationStyle CitationStyle { get; init; } = CitationStyle.Chicago18NotesBibliography;
}

public sealed record ProjectPageSetupView(
    double PageWidthInches,
    double PageHeightInches,
    double PageMarginInches,
    double BodyFontSizePoints,
    double BodyLineHeight,
    long Revision);

public sealed record PublicationBookOutlineView(
    PublishOutlineTargetKind TargetKind,
    Guid TargetId,
    string Title,
    bool IsIncluded,
    int SortOrder);

public sealed record PublicationBookDetails(
    IReadOnlyList<PublicationBookOutlineView> Outline);

public sealed record PublicationBookPatch(
    long ExpectedRevision,
    string? Title = null,
    string? Subtitle = null,
    string? Author = null,
    string? Language = null,
    string? Publisher = null,
    string? Copyright = null,
    string? Description = null,
    bool? IncludeTableOfContents = null,
    bool? IncludeVisibleTableOfContents = null,
    bool? IncludeActSynopses = null,
    bool? IncludeChapterSynopses = null,
    bool? IncludeActHeadings = null,
    bool? IncludeChapterHeadings = null,
    bool? NumberActs = null,
    bool? NumberChapters = null,
    PublishTitlePageMode? TitlePageMode = null,
    bool? AllowDesignedPageOverrides = null,
    bool? RectoChapterStarts = null,
    IReadOnlyList<string>? ClearFields = null,
    CitationStyle? CitationStyle = null);

public interface IPublicationBookService
{
    Task<PublicationBookView> GetOrCreateAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<PublicationBookDetails> GetDetailsAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<PublicationBookView> UpdateAsync(Guid projectId, PublicationBookPatch patch, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PublicationBookOutlineView>> ListOutlineAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<PublicationBookView> SetOutlineSelectionsAsync(Guid projectId, IReadOnlyList<PublicationEditionOutlineItemUpdate> updates, long expectedRevision, CancellationToken cancellationToken = default);
    Task<PublicationCoverDesignView> GetCoverAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<PublicationCoverDesignView> SaveCoverAsync(Guid projectId, long expectedBookRevision, PublicationCoverDesignUpdate update, CompositionScene scene, CancellationToken cancellationToken = default);
    Task<CompositionMutationStage> StageCoverSceneAsync(Guid projectId, Guid conversationId, long expectedBookRevision, long expectedCoverRevision, CompositionScene scene, CancellationToken cancellationToken = default);
    Task<PublicationCoverDesignView> ApplyCoverSceneStageAsync(Guid projectId, Guid conversationId, Guid stageId, long expectedBookRevision, long expectedCoverRevision, CancellationToken cancellationToken = default);
    Task<string> GetSourceFingerprintAsync(Guid projectId, CancellationToken cancellationToken = default);
}

internal readonly record struct PublicationOutlineTarget(
    PublishOutlineTargetKind TargetKind,
    Guid TargetId);

public sealed class PublicationBookService(
    IAppDatabaseOperationFactory database,
    IAuthoringGenerationService? authoringGenerations = null,
    IAuthoringMutationContextAccessor? authoringMutationContext = null) : IPublicationBookService
{
    public async Task<string> GetSourceFingerprintAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        _ = await GetOrCreateAsync(projectId, cancellationToken);
        var core = await db.PublicationBooks.AsNoTracking().Where(item => item.ProjectId == projectId)
            .Select(item => new
            {
                item.Revision,
                item.Title,
                item.Subtitle,
                item.Author,
                item.Language,
                item.Publisher,
                item.Copyright,
                item.Description,
                item.IncludeTableOfContents,
                item.IncludeVisibleTableOfContents,
                item.IncludeActSynopses,
                item.IncludeChapterSynopses,
                item.IncludeActHeadings,
                item.IncludeChapterHeadings,
                item.NumberActs,
                item.NumberChapters,
                item.TitlePageMode,
                item.RectoChapterStarts,
                item.CitationStyle,
            }).SingleAsync(cancellationToken);
        var pdfPresentation = await db.PublicationBookPdfPresentations.AsNoTracking()
            .Where(item => item.ProjectId == projectId)
            .Select(item => new { item.AllowDesignedPageOverrides })
            .SingleOrDefaultAsync(cancellationToken);
        var setup = await db.ProjectPageSetups.AsNoTracking().Where(item => item.ProjectId == projectId)
            .Select(item => new { item.Revision, item.PageWidthInches, item.PageHeightInches, item.PageMarginInches, item.BodyFontSizePoints, item.BodyLineHeight })
            .SingleOrDefaultAsync(cancellationToken);
        var chapters = await db.Chapters.AsNoTracking().Where(item => item.ProjectId == projectId)
            .OrderBy(item => item.Id).Select(item => new { item.Id, item.ActId, item.Order, item.Title, item.Synopsis, item.ManuscriptRevision, item.ManuscriptJson }).ToListAsync(cancellationToken);
        var publicationSections = await db.PublicationSections.AsNoTracking()
            .Where(item => item.ProjectId == projectId && item.EditionId == null)
            .OrderBy(item => item.Anchor).ThenBy(item => item.TargetId).ThenBy(item => item.LocalOrder).ThenBy(item => item.Id)
            .Select(item => new
            {
                item.Id,
                item.Revision,
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
            }).ToListAsync(cancellationToken);
        var placedDesignedPageIds = chapters
            .SelectMany(chapter => ManuscriptCodec.Deserialize(
                chapter.ManuscriptJson, chapter.Id, chapter.ManuscriptRevision).Content)
            .Concat(publicationSections.SelectMany(section => ManuscriptCodec.Deserialize(
                section.ManuscriptJson, section.Id, section.Revision).Content))
            .Where(block => block.Type == ManuscriptBlockType.DesignedPage && block.DesignedPageId.HasValue)
            .Select(block => block.DesignedPageId!.Value)
            .Distinct()
            .ToList();
        var compositions = await db.DesignedPageContents.AsNoTracking()
            // Only placed Core pages participate in the Core artifact
            // fingerprint. Unplaced pages must remain searchable without
            // invalidating publication output.
            .Where(item => item.ProjectId == projectId
                && item.Page.ScopeEditionId == null
                && item.EditionId == null
                && placedDesignedPageIds.Contains(item.DesignedPageId))
            .OrderBy(item => item.DesignedPageId).Select(item => new
            {
                Id = item.DesignedPageId,
                item.Revision,
                item.SemanticManuscriptJson,
                Variants = item.Variants.OrderBy(v => v.Id)
                    .Select(v => new { v.Id, v.Revision, v.GeometryKey, v.SceneJson })
            }).ToListAsync(cancellationToken);
        var assetRows = await db.PublishAssets.AsNoTracking().Where(item => item.ProjectId == projectId)
            .OrderBy(item => item.Id).Select(item => new { item.Id, item.UpdatedAt, item.FileName, item.Data }).ToListAsync(cancellationToken);
        var assets = assetRows.Select(item => new
        {
            item.Id,
            item.UpdatedAt,
            item.FileName,
            Hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(item.Data))
        }).ToList();
        var styles = await db.ManuscriptStyleDefinitions.AsNoTracking()
            .Where(item => item.ProjectId == projectId)
            .OrderBy(item => item.Id)
            .Select(item => new
            {
                item.Id,
                item.Name,
                item.Kind,
                item.SemanticRole,
                item.DefinitionJson,
                item.Revision,
            })
            .ToListAsync(cancellationToken);
        var fonts = await db.ProjectFontFamilies.AsNoTracking()
            .Where(item => item.ProjectId == projectId)
            .OrderBy(item => item.Id)
            .Select(item => new
            {
                item.Id,
                item.Name,
                item.EmbeddingRightsConfirmed,
                item.RightsDeclaration,
                Faces = item.Faces.OrderBy(face => face.Id).Select(face => new
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
        var bookRows = await db.PublicationBooks.AsNoTracking().Where(item => item.ProjectId == projectId)
            .Select(item => new
            {
                Outline = item.OutlineItems.OrderBy(row => row.SortOrder).Select(row => new { row.TargetKind, row.TargetId, row.IsIncluded, row.SortOrder }),
                Cover = item.CoverDesign == null ? null : new { item.CoverDesign.Revision, item.CoverDesign.BackgroundColor, item.CoverDesign.CompositionSceneJson },
            }).SingleAsync(cancellationToken);
        var normalizedBookRows = new
        {
            bookRows.Outline,
            Cover = bookRows.Cover is null
                ? null
                : new
                {
                    bookRows.Cover.Revision,
                    bookRows.Cover.BackgroundColor,
                    CompositionSceneJson = NormalizeCoverSceneJson(bookRows.Cover.CompositionSceneJson),
                },
        };
        var bibliography = await PublicationCitationDependencies.ReadAsync(db, projectId,
            chapters.Select(item => item.ManuscriptJson)
                .Concat(publicationSections.Select(item => item.ManuscriptJson))
                .Concat(compositions.Select(item => item.SemanticManuscriptJson)), cancellationToken);
        var payload = JsonSerializer.Serialize(
            new { core, pdfPresentation, setup, chapters, publicationSections, compositions, assets, styles, fonts, bibliography, bookRows = normalizedBookRows },
            ManuscriptCodec.JsonOptions);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    public async Task<PublicationBookView> GetOrCreateAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var existing = await ReadViewAsync(projectId, cancellationToken);
        if (existing is not null)
            return await EnsureOutlineCurrentAsync(projectId, existing, cancellationToken);

        existing = await ReadViewAsync(projectId, cancellationToken);
        if (existing is not null)
            return existing;

        var project = await db.Projects.Include(item => item.BookBrief)
            .SingleOrDefaultAsync(item => item.Id == projectId, cancellationToken)
            ?? throw new InvalidOperationException("Project was not found.");
        var setup = await db.ProjectPageSetups.SingleOrDefaultAsync(item => item.ProjectId == projectId, cancellationToken)
            ?? new ProjectPageSetup { ProjectId = projectId };
        if (db.Entry(setup).State == EntityState.Detached)
            db.ProjectPageSetups.Add(setup);

        var book = new PublicationBook
        {
            ProjectId = projectId,
            Title = project.Name.Trim(),
            Language = PublicationLanguage.Normalize(project.BookBrief?.LanguageLocale),
            IncludeVisibleTableOfContents = project.BookBrief?.BookKind is not (BookKind.Novel
                or BookKind.Novella
                or BookKind.ShortStory
                or BookKind.PictureBook
                or BookKind.Poetry),
        };
        db.PublicationBooks.Add(book);
        book.PdfPresentation = new PublicationBookPdfPresentation { ProjectId = projectId };
        await AddMissingOutlineAsync(book, cancellationToken);
        var coverEntity = new PublicationCoverDesign { EditionId = Guid.Empty };
        var coverEdition = new PublicationEdition
        {
            ProjectId = projectId,
            Name = "Core Book",
            Format = PublicationEditionFormat.DigitalPdf,
            PageWidthInches = setup.PageWidthInches,
            PageHeightInches = setup.PageHeightInches,
            PageMarginInches = setup.PageMarginInches,
            BodyFontSizePoints = setup.BodyFontSizePoints,
            BodyLineHeight = setup.BodyLineHeight,
        };
        book.CoverDesign = new PublicationBookCoverDesign
        {
            ProjectId = projectId,
            CompositionSceneJson = JsonSerializer.Serialize(
                CoverCompositionFactory.Create(coverEdition, coverEntity),
                ManuscriptCodec.JsonOptions),
        };
        await db.SaveChangesAsync(cancellationToken);
        return (await ReadViewAsync(projectId, cancellationToken))!;
    }

    private async Task<PublicationBookView> EnsureOutlineCurrentAsync(
        Guid projectId,
        PublicationBookView existing,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var targets = await ListCanonicalOutlineTargetsAsync(db, projectId, cancellationToken);
        var currentRows = await db.PublicationBookOutlineItems
            .Where(item => item.ProjectId == projectId).ToListAsync(cancellationToken);
        var currentByTarget = currentRows.ToDictionary(item => (item.TargetKind, item.TargetId));
        var canonicalTargetKeys = targets.Select(item => (item.TargetKind, item.TargetId)).ToHashSet();
        var now = DateTime.UtcNow;
        var changed = false;

        for (var sortOrder = 0; sortOrder < targets.Count; sortOrder++)
        {
            var target = targets[sortOrder];
            if (!currentByTarget.TryGetValue((target.TargetKind, target.TargetId), out var row))
            {
                db.PublicationBookOutlineItems.Add(NewOutline(
                    projectId, target.TargetKind, target.TargetId, sortOrder));
                changed = true;
                continue;
            }

            if (row.SortOrder == sortOrder)
                continue;

            row.SortOrder = sortOrder;
            row.UpdatedAt = now;
            changed = true;
        }

        var removed = currentRows.Where(item => !canonicalTargetKeys.Contains((item.TargetKind, item.TargetId))).ToList();
        if (removed.Count > 0)
        {
            db.PublicationBookOutlineItems.RemoveRange(removed);
            changed = true;
        }

        if (!changed)
            return existing;

        var book = await db.PublicationBooks.SingleAsync(item => item.ProjectId == projectId, cancellationToken);
        Touch(book);
        await db.SaveChangesAsync(cancellationToken);
        return (await ReadViewAsync(projectId, cancellationToken))!;
    }

    public async Task<PublicationBookView> UpdateAsync(
        Guid projectId,
        PublicationBookPatch patch,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var book = await GetTrackedBookAsync(projectId, patch.ExpectedRevision, cancellationToken);
        var coverDesign = await db.PublicationBookCoverDesigns.SingleAsync(
            item => item.ProjectId == projectId,
            cancellationToken);
        var beforeCoverHistory = CaptureCoreCover(book, coverDesign);

        var clear = (patch.ClearFields ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        book.Title = Value(patch.Title, book.Title, clear, nameof(patch.Title));
        book.Subtitle = Value(patch.Subtitle, book.Subtitle, clear, nameof(patch.Subtitle));
        book.Author = Value(patch.Author, book.Author, clear, nameof(patch.Author));
        book.Language = PublicationLanguage.Normalize(
            Value(patch.Language, book.Language, clear, nameof(patch.Language)));
        book.Publisher = Value(patch.Publisher, book.Publisher, clear, nameof(patch.Publisher));
        book.Copyright = Value(patch.Copyright, book.Copyright, clear, nameof(patch.Copyright));
        book.Description = Value(patch.Description, book.Description, clear, nameof(patch.Description));
        book.IncludeTableOfContents = patch.IncludeTableOfContents ?? book.IncludeTableOfContents;
        book.IncludeVisibleTableOfContents = patch.IncludeVisibleTableOfContents ?? book.IncludeVisibleTableOfContents;
        book.IncludeActSynopses = patch.IncludeActSynopses ?? book.IncludeActSynopses;
        book.IncludeChapterSynopses = patch.IncludeChapterSynopses ?? book.IncludeChapterSynopses;
        book.IncludeActHeadings = patch.IncludeActHeadings ?? book.IncludeActHeadings;
        book.IncludeChapterHeadings = patch.IncludeChapterHeadings ?? book.IncludeChapterHeadings;
        book.NumberActs = patch.NumberActs ?? book.NumberActs;
        book.NumberChapters = patch.NumberChapters ?? book.NumberChapters;
        book.TitlePageMode = patch.TitlePageMode ?? book.TitlePageMode;
        book.RectoChapterStarts = patch.RectoChapterStarts ?? book.RectoChapterStarts;
        book.CitationStyle = patch.CitationStyle ?? book.CitationStyle;
        var pdfPresentation = await db.PublicationBookPdfPresentations.SingleOrDefaultAsync(
            item => item.ProjectId == projectId,
            cancellationToken);
        if (pdfPresentation is null)
        {
            pdfPresentation = new PublicationBookPdfPresentation { ProjectId = projectId };
            db.PublicationBookPdfPresentations.Add(pdfPresentation);
        }
        pdfPresentation.AllowDesignedPageOverrides = patch.AllowDesignedPageOverrides
            ?? pdfPresentation.AllowDesignedPageOverrides;
        Validate(book);
        book.Revision++;
        book.UpdatedAt = DateTime.UtcNow;
        _ = await PublicationSectionService.RefreshBindingsAsync(
            db,
            new PublicationSectionTarget(projectId),
            new Dictionary<PublicationBoundField, string>
            {
                [PublicationBoundField.Title] = book.Title,
                [PublicationBoundField.Subtitle] = book.Subtitle,
                [PublicationBoundField.Author] = book.Author,
                [PublicationBoundField.Publisher] = book.Publisher,
                [PublicationBoundField.Copyright] = book.Copyright,
                [PublicationBoundField.Description] = book.Description,
                [PublicationBoundField.Isbn] = string.Empty,
            },
            cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        var afterCoverHistory = CaptureCoreCover(book, coverDesign);
        if (!string.Equals(beforeCoverHistory, afterCoverHistory, StringComparison.Ordinal))
            await InvalidateCoverHistoryAsync(projectId);
        return (await ReadViewAsync(projectId, cancellationToken))!;
    }

    public async Task<IReadOnlyList<PublicationBookOutlineView>> ListOutlineAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        _ = await GetOrCreateAsync(projectId, cancellationToken);
        return await ReadOutlineAsync(projectId, cancellationToken);
    }

    public async Task<PublicationBookDetails> GetDetailsAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        _ = await GetOrCreateAsync(projectId, cancellationToken);
        return new PublicationBookDetails(
            await ReadOutlineAsync(projectId, cancellationToken));
    }

    private async Task<IReadOnlyList<PublicationBookOutlineView>> ReadOutlineAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var targets = await ListCanonicalOutlineTargetsAsync(db, projectId, cancellationToken);
        var rows = await db.PublicationBookOutlineItems.AsNoTracking()
            .Where(item => item.ProjectId == projectId)
            .ToDictionaryAsync(item => (item.TargetKind, item.TargetId), cancellationToken);
        var chapters = await db.Chapters.AsNoTracking().Where(item => item.ProjectId == projectId)
            .ToDictionaryAsync(item => item.Id, item => item.Title, cancellationToken);
        return targets.Select((target, sortOrder) => new { target, sortOrder })
            .Where(item => item.target.TargetKind == PublishOutlineTargetKind.Chapter)
            .Select(item => new PublicationBookOutlineView(
                item.target.TargetKind,
                item.target.TargetId,
                chapters.GetValueOrDefault(item.target.TargetId, "Missing chapter"),
                rows.GetValueOrDefault((item.target.TargetKind, item.target.TargetId))?.IsIncluded ?? true,
                item.sortOrder))
            .ToList();
    }

    public async Task<PublicationBookView> SetOutlineSelectionsAsync(
        Guid projectId,
        IReadOnlyList<PublicationEditionOutlineItemUpdate> updates,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var book = await GetTrackedBookAsync(projectId, expectedRevision, cancellationToken);
        if (updates.Any(item => item.TargetKind != PublishOutlineTargetKind.Chapter))
            throw new InvalidOperationException("Core Book content inclusion applies to chapters; act presentation is controlled by the act heading and summary settings.");
        var rows = await db.PublicationBookOutlineItems
            .Where(item => item.ProjectId == projectId && item.TargetKind == PublishOutlineTargetKind.Chapter)
            .ToListAsync(cancellationToken);
        var requested = updates.ToDictionary(item => (item.TargetKind, item.TargetId), item => item.IsIncluded);
        if (requested.Count != updates.Count || requested.Keys.Any(key => rows.All(row => (row.TargetKind, row.TargetId) != key)))
            throw new InvalidOperationException("One or more Core Book content targets are invalid or duplicated.");
        foreach (var row in rows.Where(row => requested.ContainsKey((row.TargetKind, row.TargetId))))
        {
            row.IsIncluded = requested[(row.TargetKind, row.TargetId)];
            row.UpdatedAt = DateTime.UtcNow;
        }
        Touch(book);
        await db.SaveChangesAsync(cancellationToken);
        return (await ReadViewAsync(projectId, cancellationToken))!;
    }

    public async Task<PublicationCoverDesignView> GetCoverAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        _ = await GetOrCreateAsync(projectId, cancellationToken);
        var book = await db.PublicationBooks.SingleAsync(item => item.ProjectId == projectId, cancellationToken);
        var design = await db.PublicationBookCoverDesigns.SingleAsync(item => item.ProjectId == projectId, cancellationToken);
        var setup = await db.ProjectPageSetups.AsNoTracking().SingleAsync(item => item.ProjectId == projectId, cancellationToken);
        var edition = CoreCoverEdition(projectId, setup);
        var template = CoreCoverTemplate(setup);
        var beforeHistory = CaptureCoreCover(book, design);
        var scene = JsonSerializer.Deserialize<CompositionScene>(design.CompositionSceneJson, ManuscriptCodec.JsonOptions)
            ?? throw new InvalidDataException("The Core cover composition is empty.");
        scene = PublicationCoverService.ReflowToCurrentGeometry(
            edition,
            new PublicationCoverDesign { EditionId = Guid.Empty },
            template,
            scene,
            out var geometryChanged);
        if (geometryChanged)
        {
            PublicationCoverService.ValidateAuthoringScene(scene, edition, template);
            design.CompositionSceneJson = JsonSerializer.Serialize(scene, ManuscriptCodec.JsonOptions);
            design.Revision = checked(design.Revision + 1);
            design.UpdatedAt = DateTime.UtcNow;
            book.Revision = checked(book.Revision + 1);
            book.UpdatedAt = DateTime.UtcNow;
            var project = await db.Projects.SingleAsync(item => item.Id == projectId, cancellationToken);
            project.UpdatedAt = DateTime.UtcNow;
            var afterHistory = CaptureCoreCover(book, design);
            await db.SaveChangesAsync(cancellationToken);
            await InvalidateCoverHistoryAsync(projectId);
        }

        return CoreCoverView((await ReadViewAsync(projectId, cancellationToken))!, design);
    }

    public async Task<PublicationCoverDesignView> SaveCoverAsync(
        Guid projectId,
        long expectedBookRevision,
        PublicationCoverDesignUpdate update,
        CompositionScene scene,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        scene = CoverCompositionFactory.KeepArtworkBehindCopy(scene);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var book = await db.PublicationBooks.SingleOrDefaultAsync(
            item => item.ProjectId == projectId,
            cancellationToken) ?? throw new InvalidOperationException("Core Book was not found.");
        if (book.Revision != expectedBookRevision)
            throw new DbUpdateConcurrencyException($"Core Book changed in another editor (expected revision {expectedBookRevision}, current {book.Revision}).");
        var design = await db.PublicationBookCoverDesigns.SingleOrDefaultAsync(
            item => item.ProjectId == projectId,
            cancellationToken) ?? new PublicationBookCoverDesign { ProjectId = projectId };
        if (design.Revision != update.ExpectedRevision)
            throw new DbUpdateConcurrencyException("The Core cover changed in another editor.");
        var beforeHistory = CaptureCoreCover(book, design);
        if (update.Title.Trim().Length > 500 || update.Subtitle.Trim().Length > 500 || update.Author.Trim().Length > 500)
            throw new InvalidOperationException("Core cover copy exceeds its allowed length.");

        var setup = await db.ProjectPageSetups.AsNoTracking().SingleAsync(
            item => item.ProjectId == projectId,
            cancellationToken);
        var edition = CoreCoverEdition(projectId, setup);
        var template = CoreCoverTemplate(setup);
        scene = PublicationCoverService.ReflowToCurrentGeometry(
            edition,
            new PublicationCoverDesign { EditionId = Guid.Empty },
            template,
            scene,
            out _);
        PublicationCoverService.ValidateAuthoringScene(scene, edition, template);
        var imageIds = CompositionSceneResolver.Flatten(scene)
            .Where(item => item.ImageId is not null)
            .Select(item => item.ImageId!.Value)
            .Distinct()
            .ToList();
        var ownedImageCount = await db.PublishAssets.AsNoTracking().CountAsync(
            item => item.ProjectId == projectId
                && imageIds.Contains(item.Id)
                && (item.ContentType == "image/png" || item.ContentType == "image/jpeg"),
            cancellationToken);
        if (ownedImageCount != imageIds.Count)
            throw new InvalidDataException("The Core cover references artwork outside this project or an unsupported publication image.");

        book.Title = update.Title.Trim();
        book.Subtitle = update.Subtitle.Trim();
        book.Author = update.Author.Trim();
        book.Revision = checked(book.Revision + 1);
        book.UpdatedAt = DateTime.UtcNow;
        design.BackgroundColor = update.BackgroundColor.Trim().ToLowerInvariant();
        design.CompositionSceneJson = JsonSerializer.Serialize(scene, ManuscriptCodec.JsonOptions);
        design.Revision = checked(design.Revision + 1);
        design.UpdatedAt = DateTime.UtcNow;
        if (db.Entry(design).State == EntityState.Detached)
            db.PublicationBookCoverDesigns.Add(design);
        var afterHistory = CaptureCoreCover(book, design);
        if (string.Equals(beforeHistory, afterHistory, StringComparison.Ordinal))
        {
            db.ChangeTracker.Clear();
            design.Revision = update.ExpectedRevision;
            return CoreCoverView((await ReadViewAsync(projectId, cancellationToken))!, design);
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await InvalidateCoverHistoryAsync(projectId);
        return CoreCoverView((await ReadViewAsync(projectId, cancellationToken))!, design);
    }

    private static string CaptureCoreCover(PublicationBook book, PublicationBookCoverDesign design) =>
        AuthoringSnapshotCodec.Serialize(new AuthoringCoreCoverSnapshot(
            book.Title, book.Subtitle, book.Author, design.BackgroundColor, design.CompositionSceneJson));


    public async Task<CompositionMutationStage> StageCoverSceneAsync(
        Guid projectId,
        Guid conversationId,
        long expectedBookRevision,
        long expectedCoverRevision,
        CompositionScene scene,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        scene = CoverCompositionFactory.KeepArtworkBehindCopy(scene);
        if (conversationId == Guid.Empty)
            throw new ArgumentException("A conversation is required for staged Core cover changes.", nameof(conversationId));
        _ = await GetOrCreateAsync(projectId, cancellationToken);
        await db.CompositionMutationStages.Where(item => item.ProjectId == projectId
            && (item.ExpiresAt <= DateTime.UtcNow || item.AppliedAt != null)).ExecuteDeleteAsync(cancellationToken);
        var book = await db.PublicationBooks.AsNoTracking().SingleAsync(item => item.ProjectId == projectId, cancellationToken);
        var cover = await db.PublicationBookCoverDesigns.AsNoTracking().SingleAsync(item => item.ProjectId == projectId, cancellationToken);
        if (book.Revision != expectedBookRevision || cover.Revision != expectedCoverRevision)
            throw new DbUpdateConcurrencyException("The Core Book or its cover changed; reread it before staging a replacement scene.");
        scene = await ValidateCoreSceneAsync(projectId, scene, cancellationToken);
        var payload = JsonSerializer.Serialize(new CoreCoverStagePayload(expectedBookRevision, scene), ManuscriptCodec.JsonOptions);
        var stage = new CompositionMutationStage
        {
            ProjectId = projectId,
            ConversationId = conversationId,
            TargetKind = "core-cover-scene",
            TargetId = projectId,
            ExpectedRevision = expectedCoverRevision,
            OperationsJson = payload,
            PayloadSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload))),
            ExpiresAt = DateTime.UtcNow.AddMinutes(30),
        };
        db.CompositionMutationStages.Add(stage);
        await db.SaveChangesAsync(cancellationToken);
        return stage;
    }

    public async Task<PublicationCoverDesignView> ApplyCoverSceneStageAsync(
        Guid projectId,
        Guid conversationId,
        Guid stageId,
        long expectedBookRevision,
        long expectedCoverRevision,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var stage = await db.CompositionMutationStages.SingleOrDefaultAsync(item => item.Id == stageId
            && item.ProjectId == projectId && item.ConversationId == conversationId
            && item.TargetKind == "core-cover-scene", cancellationToken)
            ?? throw new KeyNotFoundException("Core cover stage was not found for this conversation.");
        if (stage.AppliedAt is not null)
            throw new InvalidOperationException("Core cover composition stages are non-replayable.");
        if (stage.ExpiresAt <= DateTime.UtcNow)
            throw new InvalidOperationException("The Core cover composition stage expired; submit it again.");
        if (stage.ExpectedRevision != expectedCoverRevision)
            throw new DbUpdateConcurrencyException("The staged Core cover revision does not match the requested revision.");
        var actualHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(stage.OperationsJson)));
        if (!string.Equals(actualHash, stage.PayloadSha256, StringComparison.Ordinal))
            throw new InvalidDataException("The staged Core cover composition failed its integrity check.");
        var payload = JsonSerializer.Deserialize<CoreCoverStagePayload>(stage.OperationsJson, ManuscriptCodec.JsonOptions)
            ?? throw new InvalidDataException("The staged Core cover composition is empty.");
        payload = payload with { Scene = CoverCompositionFactory.KeepArtworkBehindCopy(payload.Scene) };
        if (payload.ExpectedBookRevision != expectedBookRevision)
            throw new DbUpdateConcurrencyException("The staged Core Book revision does not match the requested revision.");
        var book = await db.PublicationBooks.SingleAsync(item => item.ProjectId == projectId, cancellationToken);
        var cover = await db.PublicationBookCoverDesigns.SingleAsync(item => item.ProjectId == projectId, cancellationToken);
        if (book.Revision != expectedBookRevision || cover.Revision != expectedCoverRevision)
            throw new DbUpdateConcurrencyException("The Core Book or its cover changed after the scene was staged.");
        var beforeHistory = CaptureCoreCover(book, cover);
        payload = payload with { Scene = await ValidateCoreSceneAsync(projectId, payload.Scene, cancellationToken) };
        cover.CompositionSceneJson = JsonSerializer.Serialize(payload.Scene, ManuscriptCodec.JsonOptions);
        cover.Revision = checked(cover.Revision + 1);
        cover.UpdatedAt = DateTime.UtcNow;
        book.Revision = checked(book.Revision + 1);
        book.UpdatedAt = DateTime.UtcNow;
        stage.AppliedAt = DateTime.UtcNow;
        var afterHistory = CaptureCoreCover(book, cover);
        if (string.Equals(beforeHistory, afterHistory, StringComparison.Ordinal))
        {
            db.Entry(book).State = EntityState.Unchanged;
            db.Entry(cover).State = EntityState.Unchanged;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            cover.Revision = expectedCoverRevision;
            return CoreCoverView((await ReadViewAsync(projectId, cancellationToken))!, cover);
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await InvalidateCoverHistoryAsync(projectId);
        return CoreCoverView((await ReadViewAsync(projectId, cancellationToken))!, cover);
    }

    private Task InvalidateCoverHistoryAsync(Guid projectId) =>
        authoringGenerations is null || authoringMutationContext?.IsHistorySuppressed == true
            ? Task.CompletedTask
            : authoringGenerations.InvalidateAsync(
                projectId,
                [$"core-cover:{projectId:D}"],
                CancellationToken.None);

    private async Task<CompositionScene> ValidateCoreSceneAsync(Guid projectId, CompositionScene scene, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var setup = await db.ProjectPageSetups.AsNoTracking().SingleAsync(
            item => item.ProjectId == projectId, cancellationToken);
        var edition = CoreCoverEdition(projectId, setup);
        var template = CoreCoverTemplate(setup);
        scene = PublicationCoverService.ReflowToCurrentGeometry(
            edition,
            new PublicationCoverDesign { EditionId = Guid.Empty },
            template,
            scene,
            out _);
        PublicationCoverService.ValidateAuthoringScene(scene, edition, template);
        var imageIds = CompositionSceneResolver.Flatten(scene).Where(item => item.ImageId is not null)
            .Select(item => item.ImageId!.Value).Distinct().ToList();
        var count = await db.PublishAssets.AsNoTracking().CountAsync(item => item.ProjectId == projectId
            && imageIds.Contains(item.Id) && (item.ContentType == "image/png" || item.ContentType == "image/jpeg"), cancellationToken);
        if (count != imageIds.Count)
            throw new InvalidDataException("The Core cover references artwork outside this project or an unsupported publication image.");
        return scene;
    }

    private sealed record CoreCoverStagePayload(long ExpectedBookRevision, CompositionScene Scene);

    private static PublicationCoverDesignView CoreCoverView(
        PublicationBookView book,
        PublicationBookCoverDesign design)
    {
        var scene = JsonSerializer.Deserialize<CompositionScene>(design.CompositionSceneJson, ManuscriptCodec.JsonOptions)
            ?? throw new InvalidDataException("The Core cover composition is empty.");
        var edition = CoreCoverEdition(book.ProjectId, book.PageSetup);
        var template = CoreCoverTemplate(book.PageSetup);
        scene = PublicationCoverService.ReflowToCurrentGeometry(
            edition,
            new PublicationCoverDesign { EditionId = Guid.Empty },
            template,
            scene,
            out _);
        scene = CoverCompositionFactory.KeepArtworkBehindCopy(scene);
        var diagnosticDetails = new List<PublicationCoverDiagnostic>();
        PublicationCoverService.AddSceneDiagnostics(
            edition,
            CoverCompositionFactory.Geometry(edition, 0),
            scene,
            diagnosticDetails);
        var diagnostics = diagnosticDetails.Select(item => item.Message).ToList();
        return new PublicationCoverDesignView(
            design.Id,
            Guid.Empty,
            book.Title,
            book.Subtitle,
            book.Author,
            string.Empty,
            book.Description,
            design.BackgroundColor,
            PublicationBarcodeMode.None,
            50,
            50,
            JsonSerializer.Serialize(scene, ManuscriptCodec.JsonOptions),
            design.Revision,
            template,
            diagnostics)
        {
            CoreBookRevision = book.Revision,
            DiagnosticDetails = diagnosticDetails,
        };
    }

    private static string NormalizeCoverSceneJson(string json)
    {
        var scene = JsonSerializer.Deserialize<CompositionScene>(json, ManuscriptCodec.JsonOptions)
            ?? throw new InvalidDataException("The Core cover composition is empty.");
        return JsonSerializer.Serialize(
            CoverCompositionFactory.KeepArtworkBehindCopy(scene),
            ManuscriptCodec.JsonOptions);
    }

    private static PublicationCoverTemplate CoreCoverTemplate(ProjectPageSetupView setup) => new(
        0,
        setup.PageWidthInches,
        setup.PageHeightInches,
        0,
        0,
        setup.PageWidthInches,
        setup.PageHeightInches,
        .25,
        0,
        0,
        $"core:{setup.Revision}",
        true);

    private static PublicationCoverTemplate CoreCoverTemplate(ProjectPageSetup setup) => CoreCoverTemplate(new ProjectPageSetupView(
        setup.PageWidthInches,
        setup.PageHeightInches,
        setup.PageMarginInches,
        setup.BodyFontSizePoints,
        setup.BodyLineHeight,
        setup.Revision));

    private static PublicationEdition CoreCoverEdition(Guid projectId, ProjectPageSetup setup) => new()
    {
        ProjectId = projectId,
        Name = "Core Book",
        Format = PublicationEditionFormat.DigitalPdf,
        PageWidthInches = setup.PageWidthInches,
        PageHeightInches = setup.PageHeightInches,
        PageMarginInches = setup.PageMarginInches,
        BodyFontSizePoints = setup.BodyFontSizePoints,
        BodyLineHeight = setup.BodyLineHeight,
    };

    private static PublicationEdition CoreCoverEdition(Guid projectId, ProjectPageSetupView setup) => new()
    {
        ProjectId = projectId,
        Name = "Core Book",
        Format = PublicationEditionFormat.DigitalPdf,
        PageWidthInches = setup.PageWidthInches,
        PageHeightInches = setup.PageHeightInches,
        PageMarginInches = setup.PageMarginInches,
        BodyFontSizePoints = setup.BodyFontSizePoints,
        BodyLineHeight = setup.BodyLineHeight,
    };

    private async Task AddMissingOutlineAsync(PublicationBook book, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var targets = await ListCanonicalOutlineTargetsAsync(db, book.ProjectId, cancellationToken);
        for (var sortOrder = 0; sortOrder < targets.Count; sortOrder++)
        {
            var target = targets[sortOrder];
            book.OutlineItems.Add(NewOutline(book.ProjectId, target.TargetKind, target.TargetId, sortOrder));
        }
    }

    internal static async Task<IReadOnlyList<PublicationOutlineTarget>> ListCanonicalOutlineTargetsAsync(
        AppDbContext db,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var acts = await db.Acts.AsNoTracking()
            .Where(item => item.ProjectId == projectId)
            .OrderBy(item => item.Order)
            .ThenBy(item => item.Id)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        var chapters = await db.Chapters.AsNoTracking()
            .Where(item => item.ProjectId == projectId)
            .Select(item => new { item.Id, item.ActId, item.Order })
            .ToListAsync(cancellationToken);
        var targets = new List<PublicationOutlineTarget>(acts.Count + chapters.Count);
        foreach (var actId in acts)
        {
            targets.Add(new PublicationOutlineTarget(PublishOutlineTargetKind.Act, actId));
            targets.AddRange(chapters
                .Where(item => item.ActId == actId)
                .OrderBy(item => item.Order)
                .ThenBy(item => item.Id)
                .Select(item => new PublicationOutlineTarget(PublishOutlineTargetKind.Chapter, item.Id)));
        }
        targets.AddRange(chapters
            .Where(item => item.ActId is null)
            .OrderBy(item => item.Order)
            .ThenBy(item => item.Id)
            .Select(item => new PublicationOutlineTarget(PublishOutlineTargetKind.Chapter, item.Id)));
        return targets;
    }

    private static PublicationBookOutlineItem NewOutline(
        Guid projectId,
        PublishOutlineTargetKind kind,
        Guid targetId,
        int order) => new()
        {
            ProjectId = projectId,
            TargetKind = kind,
            TargetId = targetId,
            ActId = kind == PublishOutlineTargetKind.Act ? targetId : null,
            ChapterId = kind == PublishOutlineTargetKind.Chapter ? targetId : null,
            SortOrder = order,
        };

    private async Task<PublicationBook> GetTrackedBookAsync(
        Guid projectId,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var book = await db.PublicationBooks.SingleOrDefaultAsync(
            item => item.ProjectId == projectId,
            cancellationToken) ?? throw new InvalidOperationException("Core Book was not found.");
        if (book.Revision != expectedRevision)
            throw new DbUpdateConcurrencyException($"Core Book changed in another editor (expected revision {expectedRevision}, current {book.Revision}).");
        return book;
    }

    private static void Touch(PublicationBook book)
    {
        book.Revision = checked(book.Revision + 1);
        book.UpdatedAt = DateTime.UtcNow;
    }

    private async Task<PublicationBookView?> ReadViewAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var book = await db.PublicationBooks.AsNoTracking().SingleOrDefaultAsync(item => item.ProjectId == projectId, cancellationToken);
        if (book is null)
            return null;
        var setup = await db.ProjectPageSetups.AsNoTracking().SingleAsync(
            item => item.ProjectId == projectId,
            cancellationToken);
        var pdfPresentation = await db.PublicationBookPdfPresentations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ProjectId == projectId, cancellationToken);
        return new PublicationBookView(
            projectId,
            book.Revision,
            book.Title,
            book.Subtitle,
            book.Author,
            PublicationLanguage.Normalize(book.Language),
            book.Publisher,
            book.Copyright,
            book.Description,
            book.IncludeTableOfContents,
            book.IncludeVisibleTableOfContents,
            book.IncludeActSynopses,
            book.IncludeChapterSynopses,
            book.IncludeActHeadings,
            book.IncludeChapterHeadings,
            book.NumberActs,
            book.NumberChapters,
            book.TitlePageMode,
            pdfPresentation?.AllowDesignedPageOverrides ?? false,
            book.RectoChapterStarts,
            new ProjectPageSetupView(
                setup.PageWidthInches,
                setup.PageHeightInches,
                setup.PageMarginInches,
                setup.BodyFontSizePoints,
                setup.BodyLineHeight,
                setup.Revision),
            await db.PublicationBookOutlineItems.CountAsync(item => item.ProjectId == projectId && item.TargetKind == PublishOutlineTargetKind.Chapter && item.IsIncluded, cancellationToken),
            await db.PublicationSections.CountAsync(item => item.ProjectId == projectId && item.EditionId == null
                && !item.IsExcluded && item.InclusionMode != PublicationSectionInclusionMode.Omitted, cancellationToken),
            await db.PublicationBookCoverDesigns.Where(item => item.ProjectId == projectId).Select(item => (long?)item.Revision).SingleOrDefaultAsync(cancellationToken) ?? 0)
        {
            CitationStyle = book.CitationStyle,
        };
    }

    private static string Value(string? value, string current, HashSet<string> clear, string name) =>
        clear.Contains(name) ? string.Empty : value is null ? current : value.Trim();

    private static void Validate(PublicationBook book)
    {
        if (book.Title.Length > 500 || book.Subtitle.Length > 500 || book.Author.Length > 500
            || book.Language.Length > 40 || book.Publisher.Length > 500
            || book.Copyright.Length > 100_000 || book.Description.Length > 100_000)
            throw new InvalidOperationException("One or more Core Book fields exceed supported limits.");
        if (!PublicationLanguage.IsPressSupported(book.Language))
            throw new InvalidOperationException("Choose English, English (United States), or English (United Kingdom) as the Core Book language.");
        if (!Enum.IsDefined(book.TitlePageMode))
            throw new InvalidOperationException("The Core Book title-page setting is invalid.");
        if (!Enum.IsDefined(book.CitationStyle))
            throw new InvalidOperationException("The Core Book citation style is invalid.");
    }
}

public sealed record EffectivePublicationRelease(
    PublicationBook Book,
    PublicationEdition Edition,
    IReadOnlySet<PublicationEditionOverrideField> OverrideFields,
    IReadOnlyList<PublicationEditionOutlineItem> OutlineItems,
    IReadOnlyList<PublicationSection> PublicationSections);

public interface IPublicationEffectiveConfigurationResolver
{
    Task<EffectivePublicationRelease> ResolveReleaseAsync(Guid projectId, Guid editionId, CancellationToken cancellationToken = default);
    IReadOnlySet<PublicationEditionOverrideField> ReadOverrideFields(PublicationEdition edition);
}

public sealed class PublicationEffectiveConfigurationResolver(
    IAppDatabaseOperationFactory database,
    bool readPdfPresentation = true,
    bool readPublicationSections = true) : IPublicationEffectiveConfigurationResolver
{
    public async Task<EffectivePublicationRelease> ResolveReleaseAsync(
        Guid projectId,
        Guid editionId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        return await ResolveReleaseAsync(db, projectId, editionId, cancellationToken);
    }

    internal async Task<EffectivePublicationRelease> ResolveReleaseAsync(
        AppDbContext db,
        Guid projectId,
        Guid editionId,
        CancellationToken cancellationToken)
    {
        var stored = await db.PublicationEditions.AsNoTracking().SingleAsync(
            item => item.ProjectId == projectId && item.Id == editionId,
            cancellationToken);
        var book = await db.PublicationBooks.AsNoTracking().SingleAsync(item => item.ProjectId == projectId, cancellationToken);
        var pdfPresentation = readPdfPresentation
            ? await db.PublicationBookPdfPresentations.AsNoTracking()
                .SingleOrDefaultAsync(item => item.ProjectId == projectId, cancellationToken)
            : null;
        var fields = ReadOverrideFields(stored);
        var setup = await db.ProjectPageSetups.AsNoTracking().SingleAsync(item => item.ProjectId == projectId, cancellationToken);
        var effective = Copy(stored);
        effective.TitleOverride = Pick(fields, PublicationEditionOverrideField.Title, stored.TitleOverride, book.Title);
        effective.Subtitle = Pick(fields, PublicationEditionOverrideField.Subtitle, stored.Subtitle, book.Subtitle);
        effective.Author = Pick(fields, PublicationEditionOverrideField.Author, stored.Author, book.Author);
        effective.Language = Pick(fields, PublicationEditionOverrideField.Language, stored.Language, book.Language);
        effective.Publisher = Pick(fields, PublicationEditionOverrideField.Publisher, stored.Publisher, book.Publisher);
        effective.Copyright = Pick(fields, PublicationEditionOverrideField.Copyright, stored.Copyright, book.Copyright);
        effective.Description = Pick(fields, PublicationEditionOverrideField.Description, stored.Description, book.Description);
        effective.IncludeTableOfContents = Pick(fields, PublicationEditionOverrideField.IncludeTableOfContents, stored.IncludeTableOfContents, book.IncludeTableOfContents);
        effective.IncludeVisibleTableOfContents = Pick(fields, PublicationEditionOverrideField.IncludeVisibleTableOfContents, stored.IncludeVisibleTableOfContents, book.IncludeVisibleTableOfContents);
        effective.IncludeActSynopses = Pick(fields, PublicationEditionOverrideField.IncludeActSynopses, stored.IncludeActSynopses, book.IncludeActSynopses);
        effective.IncludeChapterSynopses = Pick(fields, PublicationEditionOverrideField.IncludeChapterSynopses, stored.IncludeChapterSynopses, book.IncludeChapterSynopses);
        effective.IncludeActHeadings = Pick(fields, PublicationEditionOverrideField.IncludeActHeadings, stored.IncludeActHeadings, book.IncludeActHeadings);
        effective.IncludeChapterHeadings = Pick(fields, PublicationEditionOverrideField.IncludeChapterHeadings, stored.IncludeChapterHeadings, book.IncludeChapterHeadings);
        effective.NumberActs = Pick(fields, PublicationEditionOverrideField.NumberActs, stored.NumberActs, book.NumberActs);
        effective.NumberChapters = Pick(fields, PublicationEditionOverrideField.NumberChapters, stored.NumberChapters, book.NumberChapters);
        effective.TitlePageMode = Pick(fields, PublicationEditionOverrideField.TitlePageMode, stored.TitlePageMode, book.TitlePageMode);
        effective.CitationStyle = Pick(fields, PublicationEditionOverrideField.CitationStyle, stored.CitationStyle, book.CitationStyle);
        effective.RectoChapterStarts = effective.Format != PublicationEditionFormat.Epub
            && Pick(fields, PublicationEditionOverrideField.RectoChapterStarts, stored.RectoChapterStarts, book.RectoChapterStarts);
        effective.AllowDesignedPageOverrides = effective.Format == PublicationEditionFormat.DigitalPdf
            && Pick(fields, PublicationEditionOverrideField.AllowDesignedPageOverrides, stored.AllowDesignedPageOverrides,
                pdfPresentation?.AllowDesignedPageOverrides ?? false);
        effective.PageWidthInches = Pick(fields, PublicationEditionOverrideField.PageWidthInches, stored.PageWidthInches, setup.PageWidthInches);
        effective.PageHeightInches = Pick(fields, PublicationEditionOverrideField.PageHeightInches, stored.PageHeightInches, setup.PageHeightInches);
        effective.PageMarginInches = Pick(fields, PublicationEditionOverrideField.PageMarginInches, stored.PageMarginInches, setup.PageMarginInches);
        effective.BodyFontSizePoints = setup.BodyFontSizePoints;
        effective.BodyLineHeight = setup.BodyLineHeight;

        return new EffectivePublicationRelease(
            book,
            effective,
            fields,
            await ResolveOutlineAsync(db, projectId, editionId, cancellationToken),
            readPublicationSections
                ? await ResolveSectionsAsync(
                    db,
                    projectId,
                    editionId,
                    stored.PublicationSectionOrderJson,
                    cancellationToken)
                : []);
    }

    private async Task<IReadOnlyList<PublicationSection>> ResolveSectionsAsync(
        AppDbContext db,
        Guid projectId,
        Guid editionId,
        string publicationSectionOrderJson,
        CancellationToken cancellationToken)
    {
        var core = await db.PublicationSections.AsNoTracking()
            .Where(item => item.ProjectId == projectId && item.EditionId == null).ToListAsync(cancellationToken);
        var local = await db.PublicationSections.AsNoTracking()
            .Where(item => item.ProjectId == projectId && item.EditionId == editionId).ToListAsync(cancellationToken);
        var overlays = local.Where(item => item.CoreSectionId.HasValue).ToDictionary(item => item.CoreSectionId!.Value);
        var orderOverrides = PublicationSectionOrderCodec.Deserialize(publicationSectionOrderJson);
        var effective = core.Where(item => !overlays.TryGetValue(item.Id, out var overlay) || !overlay.IsExcluded)
            .Select(item => overlays.GetValueOrDefault(item.Id, item))
            .Concat(local.Where(item => item.CoreSectionId == null && !item.IsExcluded))
            .ToList();
        foreach (var section in effective)
        {
            if (orderOverrides.TryGetValue(section.CoreSectionId ?? section.Id, out var order))
                section.LocalOrder = order;
        }
        return effective
            .OrderBy(item => item.Anchor).ThenBy(item => item.LocalOrder).ThenBy(item => item.Id)
            .ToList();
    }

    public IReadOnlySet<PublicationEditionOverrideField> ReadOverrideFields(PublicationEdition edition)
    {
        try
        {
            return (JsonSerializer.Deserialize<PublicationEditionOverrideField[]>(edition.OverrideFieldsJson) ?? []).ToHashSet();
        }
        catch (JsonException)
        {
            throw new InvalidDataException($"Publication release {edition.Id:N} contains invalid override metadata.");
        }
    }

    private static async Task<IReadOnlyList<PublicationEditionOutlineItem>> ResolveOutlineAsync(
        AppDbContext db,
        Guid projectId,
        Guid editionId,
        CancellationToken cancellationToken)
    {
        var targets = await PublicationBookService.ListCanonicalOutlineTargetsAsync(db, projectId, cancellationToken);
        var core = await db.PublicationBookOutlineItems.AsNoTracking()
            .Where(item => item.ProjectId == projectId)
            .ToDictionaryAsync(item => (item.TargetKind, item.TargetId), cancellationToken);
        var overrides = await db.PublicationEditionOutlineItems.AsNoTracking().Where(item => item.EditionId == editionId).ToDictionaryAsync(item => (item.TargetKind, item.TargetId), cancellationToken);
        return targets.Select((target, sortOrder) =>
        {
            core.TryGetValue((target.TargetKind, target.TargetId), out var inherited);
            overrides.TryGetValue((target.TargetKind, target.TargetId), out var overlay);
            return new PublicationEditionOutlineItem
            {
                Id = overlay?.Id ?? inherited?.Id ?? Guid.Empty,
                EditionId = editionId,
                TargetKind = target.TargetKind,
                TargetId = target.TargetId,
                ActId = target.TargetKind == PublishOutlineTargetKind.Act ? target.TargetId : null,
                ChapterId = target.TargetKind == PublishOutlineTargetKind.Chapter ? target.TargetId : null,
                IsIncluded = overlay?.IsIncluded ?? inherited?.IsIncluded ?? true,
                SortOrder = sortOrder,
            };
        }).ToList();
    }

    private static T Pick<T>(IReadOnlySet<PublicationEditionOverrideField> fields, PublicationEditionOverrideField field, T stored, T inherited) =>
        fields.Contains(field) ? stored : inherited;

    private static PublicationEdition Copy(PublicationEdition source) => new()
    {
        Id = source.Id,
        ProjectId = source.ProjectId,
        Name = source.Name,
        Format = source.Format,
        Vendor = source.Vendor,
        VendorProfileVersion = source.VendorProfileVersion,
        Status = source.Status,
        Revision = source.Revision,
        OverrideFieldsJson = source.OverrideFieldsJson,
        TitleOverride = source.TitleOverride,
        Subtitle = source.Subtitle,
        Author = source.Author,
        Language = source.Language,
        Publisher = source.Publisher,
        Copyright = source.Copyright,
        Isbn = source.Isbn,
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
        CitationStyle = source.CitationStyle,
        RectoChapterStarts = source.RectoChapterStarts,
        PrintArtifactRegistryVersion = source.PrintArtifactRegistryVersion,
        PrintArtifactProfileKey = source.PrintArtifactProfileKey,
        PrintCoverMode = source.PrintCoverMode,
        PrintProjectUse = source.PrintProjectUse,
        PrintIdentifierMode = source.PrintIdentifierMode,
        PrintCoverSubmissionMode = source.PrintCoverSubmissionMode,
        Bleed = source.Bleed,
        PrinterPaperThicknessInches = source.PrinterPaperThicknessInches,
        PrinterSpineAllowanceInches = source.PrinterSpineAllowanceInches,
        PrinterCaseWrapInches = source.PrinterCaseWrapInches,
        PrinterCaseHingeInches = source.PrinterCaseHingeInches,
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
        CreatedAt = source.CreatedAt,
        UpdatedAt = source.UpdatedAt,
    };
}
