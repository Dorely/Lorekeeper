using System.Text.Json;
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
    ProjectPageSetupView PageSetup,
    int IncludedChapterCount,
    int MatterCount,
    int ImagePlacementCount,
    long CoverRevision);

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
    IReadOnlyList<PublicationBookOutlineView> Outline,
    IReadOnlyList<PublicationMatterView> Matter,
    IReadOnlyList<PublicationImagePlacementView> ImagePlacements);

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
    IReadOnlyList<string>? ClearFields = null);

public interface IPublicationBookService
{
    Task<PublicationBookView> GetOrCreateAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<PublicationBookDetails> GetDetailsAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<PublicationBookView> UpdateAsync(Guid projectId, PublicationBookPatch patch, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PublicationBookOutlineView>> ListOutlineAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<PublicationBookView> SetOutlineSelectionsAsync(Guid projectId, IReadOnlyList<PublicationEditionOutlineItemUpdate> updates, long expectedRevision, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PublicationMatterView>> ListMatterAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<PublicationMatterView> UpsertMatterAsync(Guid projectId, PublicationMatterInput input, long expectedBookRevision, CancellationToken cancellationToken = default);
    Task DeleteMatterAsync(Guid projectId, Guid matterId, long expectedBookRevision, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PublicationImagePlacementView>> ListImagePlacementsAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<PublicationImagePlacementView> AddImagePlacementAsync(Guid projectId, PublicationImagePlacementCreate input, long expectedBookRevision, CancellationToken cancellationToken = default);
    Task<PublicationImagePlacementView> UpdateImagePlacementAsync(Guid projectId, Guid placementId, PublicationImagePlacementUpdate input, long expectedBookRevision, CancellationToken cancellationToken = default);
    Task ReorderImagePlacementsAsync(Guid projectId, IReadOnlyList<Guid> orderedPlacementIds, long expectedBookRevision, CancellationToken cancellationToken = default);
    Task DeleteImagePlacementAsync(Guid projectId, Guid placementId, long expectedBookRevision, CancellationToken cancellationToken = default);
    Task<PublicationCoverDesignView> GetCoverAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<PublicationCoverDesignView> SaveCoverAsync(Guid projectId, long expectedBookRevision, PublicationCoverDesignUpdate update, CompositionScene scene, CancellationToken cancellationToken = default);
    Task<CompositionMutationStage> StageCoverSceneAsync(Guid projectId, Guid conversationId, long expectedBookRevision, long expectedCoverRevision, CompositionScene scene, CancellationToken cancellationToken = default);
    Task<PublicationCoverDesignView> ApplyCoverSceneStageAsync(Guid projectId, Guid conversationId, Guid stageId, long expectedBookRevision, long expectedCoverRevision, CancellationToken cancellationToken = default);
    Task<string> GetSourceFingerprintAsync(Guid projectId, CancellationToken cancellationToken = default);
}

public sealed class PublicationBookService(
    AppDbContext db,
    IProjectMutationCoordinator projectMutations) : IPublicationBookService
{
    public async Task<string> GetSourceFingerprintAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        _ = await GetOrCreateAsync(projectId, cancellationToken);
        var core = await db.PublicationBooks.AsNoTracking().Where(item => item.ProjectId == projectId)
            .Select(item => new
            {
                item.Revision, item.Title, item.Subtitle, item.Author, item.Language, item.Publisher,
                item.Copyright, item.Description, item.IncludeTableOfContents, item.IncludeVisibleTableOfContents,
                item.IncludeActSynopses, item.IncludeChapterSynopses, item.IncludeActHeadings,
                item.IncludeChapterHeadings, item.NumberActs, item.NumberChapters, item.TitlePageMode,
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
        var compositions = await db.PageCompositions.AsNoTracking().Where(item => item.ProjectId == projectId)
            .OrderBy(item => item.Id).Select(item => new { item.Id, item.Revision, item.SemanticManuscriptJson, Variants = item.Variants.OrderBy(v => v.Id).Select(v => new { v.Id, v.Revision, v.GeometryKey, v.SceneJson }) }).ToListAsync(cancellationToken);
        var assetRows = await db.PublishAssets.AsNoTracking().Where(item => item.ProjectId == projectId)
            .OrderBy(item => item.Id).Select(item => new { item.Id, item.UpdatedAt, item.FileName, item.Data }).ToListAsync(cancellationToken);
        var assets = assetRows.Select(item => new { item.Id, item.UpdatedAt, item.FileName,
            Hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(item.Data)) }).ToList();
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
                Matter = item.Matter.OrderBy(row => row.SortOrder).Select(row => new { row.Id, row.Revision, row.Location, row.Kind, row.Title, row.ManuscriptJson, row.IsIncluded, row.SortOrder }),
                Placements = item.ImagePlacements.OrderBy(row => row.SortOrder).Select(row => new { row.Id, row.AssetId, row.TargetKind, row.TargetId, row.PlacementKind, row.Caption, row.PresentationJson, row.AltText, row.Decorative, row.Language, row.AccessibilityRole, row.SortOrder }),
                Cover = item.CoverDesign == null ? null : new { item.CoverDesign.Revision, item.CoverDesign.BackgroundColor, item.CoverDesign.CompositionSceneJson },
            }).SingleAsync(cancellationToken);
        var payload = JsonSerializer.Serialize(
            new { core, pdfPresentation, setup, chapters, compositions, assets, styles, fonts, bookRows },
            ManuscriptCodec.JsonOptions);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    public async Task<PublicationBookView> GetOrCreateAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var existing = await ReadViewAsync(projectId, cancellationToken);
        if (existing is not null)
            return await EnsureOutlineCurrentAsync(projectId, existing, cancellationToken);

        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
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
            Language = string.IsNullOrWhiteSpace(project.BookBrief?.LanguageLocale)
                ? "en"
                : project.BookBrief.LanguageLocale.Trim(),
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
            Binding = PublicationBinding.Digital,
            Paper = PublicationPaper.Digital,
            Ink = PublicationInk.Digital,
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
        var targetCount = await db.Acts.AsNoTracking().CountAsync(item => item.ProjectId == projectId, cancellationToken)
            + await db.Chapters.AsNoTracking().CountAsync(item => item.ProjectId == projectId, cancellationToken);
        if (await db.PublicationBookOutlineItems.AsNoTracking().CountAsync(item => item.ProjectId == projectId, cancellationToken) == targetCount)
            return existing;
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var currentRows = await db.PublicationBookOutlineItems.AsNoTracking()
            .Where(item => item.ProjectId == projectId).ToListAsync(cancellationToken);
        var known = currentRows.Select(item => (item.TargetKind, item.TargetId)).ToHashSet();
        var nextOrder = currentRows.Select(item => item.SortOrder).DefaultIfEmpty(-1).Max() + 1;
        var additions = new List<PublicationBookOutlineItem>();
        foreach (var actId in await db.Acts.AsNoTracking().Where(item => item.ProjectId == projectId)
            .OrderBy(item => item.Order).Select(item => item.Id).ToListAsync(cancellationToken))
            if (known.Add((PublishOutlineTargetKind.Act, actId)))
                additions.Add(NewOutline(projectId, PublishOutlineTargetKind.Act, actId, nextOrder++));
        foreach (var chapterId in await db.Chapters.AsNoTracking().Where(item => item.ProjectId == projectId)
            .OrderBy(item => item.Order).Select(item => item.Id).ToListAsync(cancellationToken))
            if (known.Add((PublishOutlineTargetKind.Chapter, chapterId)))
                additions.Add(NewOutline(projectId, PublishOutlineTargetKind.Chapter, chapterId, nextOrder++));
        if (additions.Count == 0)
            return (await ReadViewAsync(projectId, cancellationToken))!;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.PublicationBookOutlineItems.AddRange(additions);
        await db.SaveChangesAsync(cancellationToken);
        await db.PublicationBooks.Where(item => item.ProjectId == projectId).ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.Revision, item => item.Revision + 1)
            .SetProperty(item => item.UpdatedAt, DateTime.UtcNow), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (db.PublicationBooks.Local.FirstOrDefault(item => item.ProjectId == projectId) is { } trackedBook)
            await db.Entry(trackedBook).ReloadAsync(cancellationToken);
        return (await ReadViewAsync(projectId, cancellationToken))!;
    }

    public async Task<PublicationBookView> UpdateAsync(
        Guid projectId,
        PublicationBookPatch patch,
        CancellationToken cancellationToken = default)
    {
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var book = await db.PublicationBooks.SingleOrDefaultAsync(item => item.ProjectId == projectId, cancellationToken)
            ?? throw new InvalidOperationException("Core Book was not found.");
        if (book.Revision != patch.ExpectedRevision)
            throw new DbUpdateConcurrencyException($"Core Book changed in another editor (expected revision {patch.ExpectedRevision}, current {book.Revision}).");

        var clear = (patch.ClearFields ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        book.Title = Value(patch.Title, book.Title, clear, nameof(patch.Title));
        book.Subtitle = Value(patch.Subtitle, book.Subtitle, clear, nameof(patch.Subtitle));
        book.Author = Value(patch.Author, book.Author, clear, nameof(patch.Author));
        book.Language = Value(patch.Language, book.Language, clear, nameof(patch.Language));
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
        await db.SaveChangesAsync(cancellationToken);
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
            await ReadOutlineAsync(projectId, cancellationToken),
            await ReadMatterAsync(projectId, cancellationToken),
            await ReadImagePlacementsAsync(projectId, cancellationToken));
    }

    private async Task<IReadOnlyList<PublicationBookOutlineView>> ReadOutlineAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var chapters = await db.Chapters.AsNoTracking().Where(item => item.ProjectId == projectId)
            .ToDictionaryAsync(item => item.Id, item => item.Title, cancellationToken);
        return (await db.PublicationBookOutlineItems.AsNoTracking()
            .Where(item => item.ProjectId == projectId && item.TargetKind == PublishOutlineTargetKind.Chapter)
            .OrderBy(item => item.SortOrder).ToListAsync(cancellationToken))
            .Select(item => new PublicationBookOutlineView(
                item.TargetKind,
                item.TargetId,
                chapters.GetValueOrDefault(item.TargetId, "Missing chapter"),
                item.IsIncluded,
                item.SortOrder)).ToList();
    }

    public async Task<PublicationBookView> SetOutlineSelectionsAsync(
        Guid projectId,
        IReadOnlyList<PublicationEditionOutlineItemUpdate> updates,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
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

    public async Task<IReadOnlyList<PublicationMatterView>> ListMatterAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        _ = await GetOrCreateAsync(projectId, cancellationToken);
        return await ReadMatterAsync(projectId, cancellationToken);
    }

    private async Task<IReadOnlyList<PublicationMatterView>> ReadMatterAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        return (await db.PublicationBookMatter.AsNoTracking().Where(item => item.ProjectId == projectId)
            .OrderBy(item => item.Location).ThenBy(item => item.SortOrder).ToListAsync(cancellationToken))
            .Select(item => new PublicationMatterView(
                item.Id, item.Location, item.Kind, item.Title,
                ManuscriptCodec.Deserialize(item.ManuscriptJson, item.Id, item.Revision),
                item.IsIncluded, item.SortOrder, item.Revision)).ToList();
    }

    public async Task<PublicationMatterView> UpsertMatterAsync(
        Guid projectId,
        PublicationMatterInput input,
        long expectedBookRevision,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(input.Location) || !Enum.IsDefined(input.Kind))
            throw new InvalidOperationException("Core matter kind or location is invalid.");
        PublicationMatterFormatting.EnsureUserAuthoredKind(input.Kind);
        var title = input.Title.Trim();
        if (title.Length is < 1 or > 500 || title.Contains('\r') || title.Contains('\n') || input.SortOrder < 0)
            throw new InvalidOperationException("Core matter requires a one-line title, valid content, and non-negative order.");
        var document = ManuscriptCodec.Deserialize(input.ManuscriptJson, input.Id ?? Guid.Empty, input.ExpectedRevision ?? 0);
        if (document.Content.Any(block => block.Type == ManuscriptBlockType.DesignedPage))
            throw new InvalidOperationException("Front and back matter cannot own Designed Pages.");
        var styleEntities = await db.ManuscriptStyleDefinitions.AsNoTracking()
            .Where(style => style.ProjectId == projectId).ToListAsync(cancellationToken);
        ManuscriptStyleService.ValidateDocumentReferences(document, styleEntities.Select(style => new ManuscriptStyleView(
            style.Id, style.Name, style.Kind, style.SemanticRole,
            ManuscriptStyleService.NormalizeDefinition(JsonSerializer.Deserialize<ManuscriptStyleProperties>(
                style.DefinitionJson, ManuscriptCodec.JsonOptions) ?? new ManuscriptStyleProperties()), style.Revision)).ToList());
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var book = await GetTrackedBookAsync(projectId, expectedBookRevision, cancellationToken);
        var figureIds = document.Content.Where(block => block.Type == ManuscriptBlockType.Figure && block.ImageId is not null)
            .Select(block => block.ImageId!.Value).Distinct().ToList();
        if (figureIds.Count != await db.PublishAssets.AsNoTracking().CountAsync(
            item => item.ProjectId == projectId && figureIds.Contains(item.Id), cancellationToken))
            throw new InvalidOperationException("Core matter figures must reference project-owned images.");
        PublicationBookMatter matter;
        if (input.Id is Guid matterId)
        {
            matter = await db.PublicationBookMatter.SingleOrDefaultAsync(
                item => item.ProjectId == projectId && item.Id == matterId,
                cancellationToken) ?? throw new KeyNotFoundException("Core matter item was not found.");
            if (matter.Revision != input.ExpectedRevision)
                throw new DbUpdateConcurrencyException("Core matter changed in another editor.");
            matter.Revision = checked(matter.Revision + 1);
        }
        else
        {
            matter = new PublicationBookMatter { ProjectId = projectId, Title = title };
            db.PublicationBookMatter.Add(matter);
        }
        matter.Location = input.Location;
        matter.Kind = input.Kind;
        matter.Title = title;
        matter.ManuscriptJson = ManuscriptCodec.Serialize(document with { ManuscriptId = matter.Id, Revision = matter.Revision });
        matter.IsIncluded = input.IsIncluded;
        matter.SortOrder = input.SortOrder;
        matter.UpdatedAt = DateTime.UtcNow;
        Touch(book);
        await db.SaveChangesAsync(cancellationToken);
        return new PublicationMatterView(matter.Id, matter.Location, matter.Kind, matter.Title,
            document with { ManuscriptId = matter.Id, Revision = matter.Revision }, matter.IsIncluded, matter.SortOrder, matter.Revision);
    }

    public async Task DeleteMatterAsync(
        Guid projectId,
        Guid matterId,
        long expectedBookRevision,
        CancellationToken cancellationToken = default)
    {
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var book = await GetTrackedBookAsync(projectId, expectedBookRevision, cancellationToken);
        var matter = await db.PublicationBookMatter.SingleOrDefaultAsync(
            item => item.ProjectId == projectId && item.Id == matterId,
            cancellationToken);
        if (matter is null) return;
        db.PublicationBookMatter.Remove(matter);
        Touch(book);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PublicationImagePlacementView>> ListImagePlacementsAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        _ = await GetOrCreateAsync(projectId, cancellationToken);
        return await ReadImagePlacementsAsync(projectId, cancellationToken);
    }

    private async Task<IReadOnlyList<PublicationImagePlacementView>> ReadImagePlacementsAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var rows = await db.PublicationBookImagePlacements.AsNoTracking().Where(item => item.ProjectId == projectId)
            .OrderBy(item => item.SortOrder).ToListAsync(cancellationToken);
        var assets = await db.PublishAssets.AsNoTracking().Where(item => item.ProjectId == projectId)
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var acts = await db.Acts.AsNoTracking().Where(item => item.ProjectId == projectId).ToDictionaryAsync(item => item.Id, item => item.Title, cancellationToken);
        var chapters = await db.Chapters.AsNoTracking().Where(item => item.ProjectId == projectId).ToDictionaryAsync(item => item.Id, item => item.Title, cancellationToken);
        return rows.Select(item => PlacementView(projectId, item, assets.GetValueOrDefault(item.AssetId)?.FileName ?? "Missing image",
            item.TargetKind == PublishOutlineTargetKind.Act ? acts.GetValueOrDefault(item.TargetId, "Missing act") : chapters.GetValueOrDefault(item.TargetId, "Missing chapter"))).ToList();
    }

    public async Task<PublicationImagePlacementView> AddImagePlacementAsync(
        Guid projectId,
        PublicationImagePlacementCreate input,
        long expectedBookRevision,
        CancellationToken cancellationToken = default)
    {
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var book = await GetTrackedBookAsync(projectId, expectedBookRevision, cancellationToken);
        var asset = await db.PublishAssets.AsNoTracking().SingleOrDefaultAsync(
            item => item.ProjectId == projectId && item.Id == input.AssetId,
            cancellationToken) ?? throw new KeyNotFoundException("Project image was not found.");
        var targetTitle = input.TargetKind == PublishOutlineTargetKind.Act
            ? await db.Acts.AsNoTracking().Where(item => item.ProjectId == projectId && item.Id == input.TargetId).Select(item => item.Title).SingleOrDefaultAsync(cancellationToken)
            : await db.Chapters.AsNoTracking().Where(item => item.ProjectId == projectId && item.Id == input.TargetId).Select(item => item.Title).SingleOrDefaultAsync(cancellationToken);
        if (targetTitle is null)
            throw new KeyNotFoundException("Core image-placement target was not found.");
        var altText = input.Decorative ? string.Empty : string.IsNullOrWhiteSpace(input.AltText) ? asset.AltText : input.AltText.Trim();
        if (!input.Decorative && string.IsNullOrWhiteSpace(altText))
            throw new InvalidOperationException("Core image placements require alternative text or an explicit decorative decision.");
        var sortOrder = (await db.PublicationBookImagePlacements.Where(item => item.ProjectId == projectId
            && item.TargetKind == input.TargetKind && item.TargetId == input.TargetId && item.PlacementKind == input.PlacementKind)
            .Select(item => (int?)item.SortOrder).MaxAsync(cancellationToken) ?? -1) + 1;
        var placement = new PublicationBookImagePlacement
        {
            ProjectId = projectId, AssetId = input.AssetId, TargetKind = input.TargetKind, TargetId = input.TargetId,
            ActId = input.TargetKind == PublishOutlineTargetKind.Act ? input.TargetId : null,
            ChapterId = input.TargetKind == PublishOutlineTargetKind.Chapter ? input.TargetId : null,
            PlacementKind = input.PlacementKind, Caption = input.Caption.Trim(), SortOrder = sortOrder,
            PresentationJson = JsonSerializer.Serialize(input.Presentation ?? new FigurePresentation { Placement = FigurePlacementIntent.DedicatedPage }, ManuscriptCodec.JsonOptions),
            AltText = altText, Decorative = input.Decorative,
            Language = string.IsNullOrWhiteSpace(input.Language) ? "en" : input.Language.Trim(),
            AccessibilityRole = input.AccessibilityRole,
        };
        db.PublicationBookImagePlacements.Add(placement);
        Touch(book);
        await db.SaveChangesAsync(cancellationToken);
        return PlacementView(projectId, placement, asset.FileName, targetTitle);
    }

    public async Task DeleteImagePlacementAsync(
        Guid projectId,
        Guid placementId,
        long expectedBookRevision,
        CancellationToken cancellationToken = default)
    {
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var book = await GetTrackedBookAsync(projectId, expectedBookRevision, cancellationToken);
        var placement = await db.PublicationBookImagePlacements.SingleOrDefaultAsync(
            item => item.ProjectId == projectId && item.Id == placementId,
            cancellationToken);
        if (placement is null) return;
        db.PublicationBookImagePlacements.Remove(placement);
        Touch(book);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<PublicationImagePlacementView> UpdateImagePlacementAsync(
        Guid projectId,
        Guid placementId,
        PublicationImagePlacementUpdate input,
        long expectedBookRevision,
        CancellationToken cancellationToken = default)
    {
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var book = await GetTrackedBookAsync(projectId, expectedBookRevision, cancellationToken);
        var placement = await db.PublicationBookImagePlacements.SingleOrDefaultAsync(
            item => item.ProjectId == projectId && item.Id == placementId, cancellationToken)
            ?? throw new KeyNotFoundException("Core Book image placement was not found.");
        var asset = await db.PublishAssets.AsNoTracking().SingleOrDefaultAsync(
            item => item.ProjectId == projectId && item.Id == input.AssetId, cancellationToken)
            ?? throw new KeyNotFoundException("Project image was not found.");
        var targetTitle = input.TargetKind == PublishOutlineTargetKind.Act
            ? await db.Acts.AsNoTracking().Where(item => item.ProjectId == projectId && item.Id == input.TargetId).Select(item => item.Title).SingleOrDefaultAsync(cancellationToken)
            : await db.Chapters.AsNoTracking().Where(item => item.ProjectId == projectId && item.Id == input.TargetId).Select(item => item.Title).SingleOrDefaultAsync(cancellationToken);
        if (targetTitle is null)
            throw new KeyNotFoundException("Core image-placement target was not found.");
        var altText = input.Decorative ? string.Empty : string.IsNullOrWhiteSpace(input.AltText) ? asset.AltText : input.AltText.Trim();
        if (!input.Decorative && string.IsNullOrWhiteSpace(altText))
            throw new InvalidOperationException("Core image placements require alternative text or an explicit decorative decision.");
        placement.AssetId = input.AssetId;
        placement.TargetKind = input.TargetKind;
        placement.TargetId = input.TargetId;
        placement.ActId = input.TargetKind == PublishOutlineTargetKind.Act ? input.TargetId : null;
        placement.ChapterId = input.TargetKind == PublishOutlineTargetKind.Chapter ? input.TargetId : null;
        placement.PlacementKind = input.PlacementKind;
        placement.Caption = input.Caption.Trim();
        placement.PresentationJson = JsonSerializer.Serialize(input.Presentation ?? new FigurePresentation { Placement = FigurePlacementIntent.DedicatedPage }, ManuscriptCodec.JsonOptions);
        placement.AltText = altText;
        placement.Decorative = input.Decorative;
        placement.Language = string.IsNullOrWhiteSpace(input.Language) ? "en" : input.Language.Trim();
        placement.AccessibilityRole = input.AccessibilityRole;
        placement.UpdatedAt = DateTime.UtcNow;
        Touch(book);
        await db.SaveChangesAsync(cancellationToken);
        return PlacementView(projectId, placement, asset.FileName, targetTitle);
    }

    public async Task ReorderImagePlacementsAsync(
        Guid projectId,
        IReadOnlyList<Guid> orderedPlacementIds,
        long expectedBookRevision,
        CancellationToken cancellationToken = default)
    {
        if (orderedPlacementIds.Count != orderedPlacementIds.Distinct().Count())
            throw new InvalidOperationException("Core placement reorder contains duplicate IDs.");
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var book = await GetTrackedBookAsync(projectId, expectedBookRevision, cancellationToken);
        var rows = await db.PublicationBookImagePlacements.Where(item => item.ProjectId == projectId
            && orderedPlacementIds.Contains(item.Id)).ToListAsync(cancellationToken);
        if (rows.Count != orderedPlacementIds.Count)
            throw new InvalidOperationException("One or more Core Book image placements were not found.");
        if (rows.Count > 0)
        {
            var first = rows[0];
            if (rows.Any(item => item.TargetKind != first.TargetKind || item.TargetId != first.TargetId || item.PlacementKind != first.PlacementKind)
                || await db.PublicationBookImagePlacements.CountAsync(item => item.ProjectId == projectId
                    && item.TargetKind == first.TargetKind && item.TargetId == first.TargetId
                    && item.PlacementKind == first.PlacementKind, cancellationToken) != rows.Count)
                throw new InvalidOperationException("Reorder every Core Book image at one target and position together.");
        }
        var order = orderedPlacementIds.Select((id, index) => (id, index)).ToDictionary(item => item.id, item => item.index);
        foreach (var row in rows) { row.SortOrder = order[row.Id]; row.UpdatedAt = DateTime.UtcNow; }
        Touch(book);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<PublicationCoverDesignView> GetCoverAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var book = await GetOrCreateAsync(projectId, cancellationToken);
        var design = await db.PublicationBookCoverDesigns.AsNoTracking().SingleAsync(
            item => item.ProjectId == projectId,
            cancellationToken);
        return CoreCoverView(book, design);
    }

    public async Task<PublicationCoverDesignView> SaveCoverAsync(
        Guid projectId,
        long expectedBookRevision,
        PublicationCoverDesignUpdate update,
        CompositionScene scene,
        CancellationToken cancellationToken = default)
    {
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
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
        if (update.Title.Trim().Length > 500 || update.Subtitle.Trim().Length > 500 || update.Author.Trim().Length > 500)
            throw new InvalidOperationException("Core cover copy exceeds its allowed length.");

        var setup = await db.ProjectPageSetups.AsNoTracking().SingleAsync(
            item => item.ProjectId == projectId,
            cancellationToken);
        var edition = CoreCoverEdition(projectId, setup);
        PublicationCoverService.ValidateScene(scene, edition, CoreCoverTemplate(setup));
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
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return CoreCoverView((await ReadViewAsync(projectId, cancellationToken))!, design);
    }

    public async Task<CompositionMutationStage> StageCoverSceneAsync(
        Guid projectId,
        Guid conversationId,
        long expectedBookRevision,
        long expectedCoverRevision,
        CompositionScene scene,
        CancellationToken cancellationToken = default)
    {
        if (conversationId == Guid.Empty)
            throw new ArgumentException("A conversation is required for staged Core cover changes.", nameof(conversationId));
        _ = await GetOrCreateAsync(projectId, cancellationToken);
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        await db.CompositionMutationStages.Where(item => item.ProjectId == projectId
            && (item.ExpiresAt <= DateTime.UtcNow || item.AppliedAt != null)).ExecuteDeleteAsync(cancellationToken);
        var book = await db.PublicationBooks.AsNoTracking().SingleAsync(item => item.ProjectId == projectId, cancellationToken);
        var cover = await db.PublicationBookCoverDesigns.AsNoTracking().SingleAsync(item => item.ProjectId == projectId, cancellationToken);
        if (book.Revision != expectedBookRevision || cover.Revision != expectedCoverRevision)
            throw new DbUpdateConcurrencyException("The Core Book or its cover changed; reread it before staging a replacement scene.");
        await ValidateCoreSceneAsync(projectId, scene, cancellationToken);
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
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
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
        if (payload.ExpectedBookRevision != expectedBookRevision)
            throw new DbUpdateConcurrencyException("The staged Core Book revision does not match the requested revision.");
        var book = await db.PublicationBooks.SingleAsync(item => item.ProjectId == projectId, cancellationToken);
        var cover = await db.PublicationBookCoverDesigns.SingleAsync(item => item.ProjectId == projectId, cancellationToken);
        if (book.Revision != expectedBookRevision || cover.Revision != expectedCoverRevision)
            throw new DbUpdateConcurrencyException("The Core Book or its cover changed after the scene was staged.");
        await ValidateCoreSceneAsync(projectId, payload.Scene, cancellationToken);
        cover.CompositionSceneJson = JsonSerializer.Serialize(payload.Scene, ManuscriptCodec.JsonOptions);
        cover.Revision = checked(cover.Revision + 1);
        cover.UpdatedAt = DateTime.UtcNow;
        book.Revision = checked(book.Revision + 1);
        book.UpdatedAt = DateTime.UtcNow;
        stage.AppliedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return CoreCoverView((await ReadViewAsync(projectId, cancellationToken))!, cover);
    }

    private async Task ValidateCoreSceneAsync(Guid projectId, CompositionScene scene, CancellationToken cancellationToken)
    {
        var setup = await db.ProjectPageSetups.AsNoTracking().SingleAsync(
            item => item.ProjectId == projectId, cancellationToken);
        PublicationCoverService.ValidateScene(scene, CoreCoverEdition(projectId, setup), CoreCoverTemplate(setup));
        var imageIds = CompositionSceneResolver.Flatten(scene).Where(item => item.ImageId is not null)
            .Select(item => item.ImageId!.Value).Distinct().ToList();
        var count = await db.PublishAssets.AsNoTracking().CountAsync(item => item.ProjectId == projectId
            && imageIds.Contains(item.Id) && (item.ContentType == "image/png" || item.ContentType == "image/jpeg"), cancellationToken);
        if (count != imageIds.Count)
            throw new InvalidDataException("The Core cover references artwork outside this project or an unsupported publication image.");
    }

    private sealed record CoreCoverStagePayload(long ExpectedBookRevision, CompositionScene Scene);

    private static PublicationCoverDesignView CoreCoverView(
        PublicationBookView book,
        PublicationBookCoverDesign design)
    {
        var scene = JsonSerializer.Deserialize<CompositionScene>(design.CompositionSceneJson, ManuscriptCodec.JsonOptions)
            ?? throw new InvalidDataException("The Core cover composition is empty.");
        var diagnostics = CompositionSceneResolver.Flatten(scene)
            .Where(item => item.Kind == CompositionObjectKind.Image && item.AccessibilityDecisionPending)
            .Select(item => $"Artwork '{item.Name}' needs alternative text or a decorative decision.")
            .ToList();
        return new PublicationCoverDesignView(
            design.Id,
            Guid.Empty,
            book.Title,
            book.Subtitle,
            book.Author,
            string.Empty,
            string.Empty,
            design.BackgroundColor,
            PublicationBarcodeMode.None,
            50,
            50,
            design.CompositionSceneJson,
            design.Revision,
            CoreCoverTemplate(book.PageSetup),
            diagnostics)
        {
            CoreBookRevision = book.Revision,
        };
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
        Binding = PublicationBinding.Digital,
        Paper = PublicationPaper.Digital,
        Ink = PublicationInk.Digital,
        PageWidthInches = setup.PageWidthInches,
        PageHeightInches = setup.PageHeightInches,
        PageMarginInches = setup.PageMarginInches,
        BodyFontSizePoints = setup.BodyFontSizePoints,
        BodyLineHeight = setup.BodyLineHeight,
    };

    private async Task AddMissingOutlineAsync(PublicationBook book, CancellationToken cancellationToken)
    {
        var order = 0;
        foreach (var act in await db.Acts.Where(item => item.ProjectId == book.ProjectId).OrderBy(item => item.Order).ToListAsync(cancellationToken))
            book.OutlineItems.Add(NewOutline(book.ProjectId, PublishOutlineTargetKind.Act, act.Id, order++));
        foreach (var chapter in await db.Chapters.Where(item => item.ProjectId == book.ProjectId).OrderBy(item => item.Order).ToListAsync(cancellationToken))
            book.OutlineItems.Add(NewOutline(book.ProjectId, PublishOutlineTargetKind.Chapter, chapter.Id, order++));
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

    private static PublicationImagePlacementView PlacementView(
        Guid projectId,
        PublicationBookImagePlacement placement,
        string fileName,
        string targetTitle) => new(
            placement.Id,
            placement.AssetId,
            fileName,
            $"/projects/{projectId:N}/publish/assets/{placement.AssetId:N}/content",
            placement.TargetKind,
            placement.TargetId,
            targetTitle,
            placement.PlacementKind,
            placement.Caption,
            JsonSerializer.Deserialize<FigurePresentation>(placement.PresentationJson, ManuscriptCodec.JsonOptions),
            placement.AltText,
            placement.Decorative,
            placement.Language,
            placement.AccessibilityRole,
            placement.SortOrder);

    private async Task<PublicationBookView?> ReadViewAsync(Guid projectId, CancellationToken cancellationToken)
    {
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
            book.Language,
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
            new ProjectPageSetupView(
                setup.PageWidthInches,
                setup.PageHeightInches,
                setup.PageMarginInches,
                setup.BodyFontSizePoints,
                setup.BodyLineHeight,
                setup.Revision),
            await db.PublicationBookOutlineItems.CountAsync(item => item.ProjectId == projectId && item.TargetKind == PublishOutlineTargetKind.Chapter && item.IsIncluded, cancellationToken),
            await db.PublicationBookMatter.CountAsync(item => item.ProjectId == projectId && item.IsIncluded, cancellationToken),
            await db.PublicationBookImagePlacements.CountAsync(item => item.ProjectId == projectId, cancellationToken),
            await db.PublicationBookCoverDesigns.Where(item => item.ProjectId == projectId).Select(item => (long?)item.Revision).SingleOrDefaultAsync(cancellationToken) ?? 0);
    }

    private static string Value(string? value, string current, HashSet<string> clear, string name) =>
        clear.Contains(name) ? string.Empty : value is null ? current : value.Trim();

    private static void Validate(PublicationBook book)
    {
        if (book.Title.Length > 500 || book.Subtitle.Length > 500 || book.Author.Length > 500
            || book.Language.Length > 40 || book.Publisher.Length > 500
            || book.Copyright.Length > 100_000 || book.Description.Length > 100_000)
            throw new InvalidOperationException("One or more Core Book fields exceed supported limits.");
        if (!Enum.IsDefined(book.TitlePageMode))
            throw new InvalidOperationException("The Core Book title-page setting is invalid.");
    }
}

public sealed record EffectivePublicationRelease(
    PublicationBook Book,
    PublicationEdition Edition,
    IReadOnlySet<PublicationEditionOverrideField> OverrideFields,
    IReadOnlyList<PublicationEditionOutlineItem> OutlineItems,
    IReadOnlyList<PublicationMatter> Matter,
    IReadOnlyList<PublicationImagePlacement> ImagePlacements);

public interface IPublicationEffectiveConfigurationResolver
{
    Task<EffectivePublicationRelease> ResolveReleaseAsync(Guid projectId, Guid editionId, CancellationToken cancellationToken = default);
    IReadOnlySet<PublicationEditionOverrideField> ReadOverrideFields(PublicationEdition edition);
}

public sealed class PublicationEffectiveConfigurationResolver(
    AppDbContext db,
    bool readPdfPresentation = true) : IPublicationEffectiveConfigurationResolver
{
    public async Task<EffectivePublicationRelease> ResolveReleaseAsync(
        Guid projectId,
        Guid editionId,
        CancellationToken cancellationToken = default)
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
        effective.AllowDesignedPageOverrides = effective.Format == PublicationEditionFormat.DigitalPdf
            && Pick(fields, PublicationEditionOverrideField.AllowDesignedPageOverrides, stored.AllowDesignedPageOverrides,
                pdfPresentation?.AllowDesignedPageOverrides ?? false);
        effective.PageWidthInches = Pick(fields, PublicationEditionOverrideField.PageWidthInches, stored.PageWidthInches, setup.PageWidthInches);
        effective.PageHeightInches = Pick(fields, PublicationEditionOverrideField.PageHeightInches, stored.PageHeightInches, setup.PageHeightInches);
        effective.PageMarginInches = Pick(fields, PublicationEditionOverrideField.PageMarginInches, stored.PageMarginInches, setup.PageMarginInches);
        effective.BodyFontSizePoints = Pick(fields, PublicationEditionOverrideField.BodyFontSizePoints, stored.BodyFontSizePoints, setup.BodyFontSizePoints);
        effective.BodyLineHeight = Pick(fields, PublicationEditionOverrideField.BodyLineHeight, stored.BodyLineHeight, setup.BodyLineHeight);

        return new EffectivePublicationRelease(
            book,
            effective,
            fields,
            await ResolveOutlineAsync(projectId, editionId, cancellationToken),
            await ResolveMatterAsync(projectId, editionId, cancellationToken),
            await ResolvePlacementsAsync(projectId, editionId, cancellationToken));
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

    private async Task<IReadOnlyList<PublicationEditionOutlineItem>> ResolveOutlineAsync(Guid projectId, Guid editionId, CancellationToken cancellationToken)
    {
        var core = await db.PublicationBookOutlineItems.AsNoTracking().Where(item => item.ProjectId == projectId).ToListAsync(cancellationToken);
        var overrides = await db.PublicationEditionOutlineItems.AsNoTracking().Where(item => item.EditionId == editionId).ToDictionaryAsync(item => (item.TargetKind, item.TargetId), cancellationToken);
        return core.Select(item => overrides.TryGetValue((item.TargetKind, item.TargetId), out var value)
            ? value
            : new PublicationEditionOutlineItem
            {
                Id = item.Id, EditionId = editionId, TargetKind = item.TargetKind, TargetId = item.TargetId,
                ActId = item.ActId, ChapterId = item.ChapterId, IsIncluded = item.IsIncluded, SortOrder = item.SortOrder,
            }).OrderBy(item => item.SortOrder).ToList();
    }

    private async Task<IReadOnlyList<PublicationMatter>> ResolveMatterAsync(Guid projectId, Guid editionId, CancellationToken cancellationToken)
    {
        var core = await db.PublicationBookMatter.AsNoTracking().Where(item => item.ProjectId == projectId).ToListAsync(cancellationToken);
        var overrides = await db.PublicationMatter.AsNoTracking().Where(item => item.EditionId == editionId).ToListAsync(cancellationToken);
        var byCore = overrides.Where(item => item.CoreMatterId is not null).ToDictionary(item => item.CoreMatterId!.Value);
        var unlinkedBySlot = overrides.Where(item => item.CoreMatterId is null)
            .GroupBy(item => (item.Location, item.Kind, item.SortOrder)).ToDictionary(group => group.Key, group => group.First());
        var result = core.Where(item => !(byCore.TryGetValue(item.Id, out var linked) && linked.IsExcluded)
                && !(unlinkedBySlot.TryGetValue((item.Location, item.Kind, item.SortOrder), out var local) && local.IsExcluded))
            .Select(item => byCore.TryGetValue(item.Id, out var value) ? value
                : unlinkedBySlot.TryGetValue((item.Location, item.Kind, item.SortOrder), out var local) ? local
                : new PublicationMatter
            {
                Id = item.Id, EditionId = editionId, CoreMatterId = item.Id, Location = item.Location, Kind = item.Kind,
                Title = item.Title, ManuscriptJson = item.ManuscriptJson, Revision = item.Revision,
                IsIncluded = item.IsIncluded, SortOrder = item.SortOrder,
            });
        var coreSlots = core.Select(item => (item.Location, item.Kind, item.SortOrder)).ToHashSet();
        return result.Concat(overrides.Where(item => item.CoreMatterId is null && !item.IsExcluded
                && !coreSlots.Contains((item.Location, item.Kind, item.SortOrder))))
            .OrderBy(item => item.Location).ThenBy(item => item.SortOrder).ToList();
    }

    private async Task<IReadOnlyList<PublicationImagePlacement>> ResolvePlacementsAsync(Guid projectId, Guid editionId, CancellationToken cancellationToken)
    {
        var core = await db.PublicationBookImagePlacements.AsNoTracking().Where(item => item.ProjectId == projectId).ToListAsync(cancellationToken);
        var overrides = await db.PublicationImagePlacements.AsNoTracking().Where(item => item.EditionId == editionId).ToListAsync(cancellationToken);
        var byCore = overrides.Where(item => item.CorePlacementId is not null).ToDictionary(item => item.CorePlacementId!.Value);
        var unlinkedBySlot = overrides.Where(item => item.CorePlacementId is null)
            .GroupBy(item => (item.TargetKind, item.TargetId, item.PlacementKind, item.SortOrder)).ToDictionary(group => group.Key, group => group.First());
        var result = core.Where(item => !(byCore.TryGetValue(item.Id, out var linked) && linked.IsExcluded)
                && !(unlinkedBySlot.TryGetValue((item.TargetKind, item.TargetId, item.PlacementKind, item.SortOrder), out var local) && local.IsExcluded))
            .Select(item => byCore.TryGetValue(item.Id, out var value) ? value
                : unlinkedBySlot.TryGetValue((item.TargetKind, item.TargetId, item.PlacementKind, item.SortOrder), out var local) ? local
                : new PublicationImagePlacement
            {
                Id = item.Id, EditionId = editionId, CorePlacementId = item.Id, AssetId = item.AssetId,
                TargetKind = item.TargetKind, TargetId = item.TargetId, ActId = item.ActId, ChapterId = item.ChapterId,
                PlacementKind = item.PlacementKind, SortOrder = item.SortOrder, Caption = item.Caption,
                PresentationJson = item.PresentationJson, AltText = item.AltText, Decorative = item.Decorative,
                Language = item.Language, AccessibilityRole = item.AccessibilityRole,
            });
        var coreSlots = core.Select(item => (item.TargetKind, item.TargetId, item.PlacementKind, item.SortOrder)).ToHashSet();
        return result.Concat(overrides.Where(item => item.CorePlacementId is null && !item.IsExcluded
                && !coreSlots.Contains((item.TargetKind, item.TargetId, item.PlacementKind, item.SortOrder))))
            .OrderBy(item => item.SortOrder).ToList();
    }

    private static T Pick<T>(IReadOnlySet<PublicationEditionOverrideField> fields, PublicationEditionOverrideField field, T stored, T inherited) =>
        fields.Contains(field) ? stored : inherited;

    private static PublicationEdition Copy(PublicationEdition source) => new()
    {
        Id = source.Id, ProjectId = source.ProjectId, Name = source.Name, Format = source.Format,
        Vendor = source.Vendor, VendorProfileVersion = source.VendorProfileVersion, Status = source.Status,
        Revision = source.Revision, OverrideFieldsJson = source.OverrideFieldsJson,
        TitleOverride = source.TitleOverride, Subtitle = source.Subtitle, Author = source.Author, Language = source.Language,
        Publisher = source.Publisher, Copyright = source.Copyright, Isbn = source.Isbn, Description = source.Description,
        IncludeTableOfContents = source.IncludeTableOfContents, IncludeVisibleTableOfContents = source.IncludeVisibleTableOfContents,
        IncludeActSynopses = source.IncludeActSynopses, IncludeChapterSynopses = source.IncludeChapterSynopses,
        IncludeActHeadings = source.IncludeActHeadings, IncludeChapterHeadings = source.IncludeChapterHeadings,
        NumberActs = source.NumberActs, NumberChapters = source.NumberChapters, TitlePageMode = source.TitlePageMode,
        Binding = source.Binding, Paper = source.Paper, Ink = source.Ink, Bleed = source.Bleed,
        PageWidthInches = source.PageWidthInches, PageHeightInches = source.PageHeightInches,
        PageMarginInches = source.PageMarginInches, BodyFontSizePoints = source.BodyFontSizePoints,
        BodyLineHeight = source.BodyLineHeight, SelectedCoverImageId = source.SelectedCoverImageId,
        AllowDesignedPageOverrides = source.AllowDesignedPageOverrides, InheritsCoreCover = source.InheritsCoreCover,
        CreatedAt = source.CreatedAt, UpdatedAt = source.UpdatedAt,
    };
}
