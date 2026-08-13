using System.Security.Cryptography;
using System.Text.Json;
using Lorekeeper.Composition;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

namespace Lorekeeper.Publish;

public interface IPublicationCoreMigrationService
{
    Task ApplyPendingAsync(AppDbContext db, CancellationToken cancellationToken = default);
}

public sealed class PublicationCoreMigrationService(
    IDatabaseMigrationRecoveryService recovery,
    ILogger<PublicationCoreMigrationService> logger) : IPublicationCoreMigrationService
{
    public const string MigrationName = "publication-core-book-v1";
    public const string SchemaMigrationId = "20260803222946_AddPublicationCoreBookV20";
    public const string CleanupMigrationId = "20260803225119_RemovePublicationDefaultReleaseV21";

    public async Task ApplyPendingAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        if (await db.ManuscriptMigrationJournals.AsNoTracking().AnyAsync(
            item => item.MigrationName == MigrationName && item.Status == ManuscriptMigrationStatus.Completed,
            cancellationToken))
            return;

        var backupPath = await recovery.CreateBackupAsync("publication", "pre-core-book-v1", cancellationToken);
        try
        {
            if (!(await db.Database.GetAppliedMigrationsAsync(cancellationToken)).Contains(SchemaMigrationId, StringComparer.Ordinal))
                await db.GetService<IMigrator>().MigrateAsync(SchemaMigrationId, cancellationToken);
            db.ChangeTracker.Clear();

            var journal = new ManuscriptMigrationJournal
            {
                MigrationName = MigrationName,
                SourceSchemaVersion = 15,
                TargetSchemaVersion = 16,
                Phase = ManuscriptMigrationPhase.Transform,
                Status = ManuscriptMigrationStatus.Running,
                BackupPath = backupPath,
            };
            db.ManuscriptMigrationJournals.Add(journal);
            await SaveMigrationChangesAsync(db, "migration journal initialization", cancellationToken);

            var artifactState = await ArtifactStateAsync(db, cancellationToken);
            var releaseProjectionState = await ReleaseProjectionStateAsync(db, useCoreInheritance: false, cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var projects = await db.Projects.OrderBy(item => item.Id).ToListAsync(cancellationToken);
            foreach (var project in projects)
                await MigrateProjectAsync(db, project, cancellationToken);
            await db.PublicationArtifacts.Where(item => !item.IsLegacy)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsLegacy, true), cancellationToken);

            await SaveMigrationChangesAsync(db, "final sparse-overlay save", cancellationToken);
            if (await db.PublicationBooks.CountAsync(cancellationToken) != projects.Count)
                throw new InvalidDataException("Core Book migration did not create exactly one Core Book per project.");
            if (!string.Equals(artifactState, await ArtifactStateAsync(db, cancellationToken), StringComparison.Ordinal))
                throw new InvalidDataException("Core Book migration changed publication artifact bytes or hashes.");
            if (!string.Equals(releaseProjectionState, await ReleaseProjectionStateAsync(db, useCoreInheritance: true, cancellationToken), StringComparison.Ordinal))
                throw new InvalidDataException("Core Book migration changed the effective configuration of an existing publication release.");
            if (await HasForeignKeyViolationsAsync(db, cancellationToken))
                throw new InvalidDataException("Core Book migration left invalid foreign keys.");

            journal.ChapterCount = await db.Chapters.CountAsync(cancellationToken);
            journal.ValidationReportJson = JsonSerializer.Serialize(new
            {
                projects = projects.Count,
                releases = await db.PublicationEditions.CountAsync(cancellationToken),
                coreMatter = await db.PublicationBookMatter.CountAsync(cancellationToken),
                corePlacements = await db.PublicationBookImagePlacements.CountAsync(cancellationToken),
                artifacts = await db.PublicationArtifacts.CountAsync(cancellationToken),
            });
            journal.Phase = ManuscriptMigrationPhase.Complete;
            journal.Status = ManuscriptMigrationStatus.Completed;
            journal.CompletedAt = DateTime.UtcNow;
            await SaveMigrationChangesAsync(db, "migration journal completion", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Core Book publication migration failed.");
            await recovery.EnterRecoveryModeAsync(db, backupPath, MigrationName, 15, 16, exception, cancellationToken);
        }
    }

    private static async Task MigrateProjectAsync(AppDbContext db, Project project, CancellationToken cancellationToken)
    {
        if (await db.PublicationBooks.AnyAsync(item => item.ProjectId == project.Id, cancellationToken))
            return;
        var setup = await db.ProjectPageSetups.AsNoTracking().SingleOrDefaultAsync(item => item.ProjectId == project.Id, cancellationToken);
        var legacyDefaultId = await ReadLegacyDefaultReleaseIdAsync(db, project.Id, cancellationToken);
        var releases = await db.PublicationEditions.Where(item => item.ProjectId == project.Id)
            .Include(item => item.OutlineItems).Include(item => item.Matter).Include(item => item.ImagePlacements)
            .Include(item => item.CoverDesign)
            .OrderBy(item => item.CreatedAt).ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        await ApplyLegacyTypographyAsync(db, releases, cancellationToken);
        var source = releases.FirstOrDefault(item => item.Id == legacyDefaultId) ?? releases.FirstOrDefault();
        var briefLanguage = await db.BookBriefs.AsNoTracking().Where(item => item.ProjectId == project.Id)
            .Select(item => item.LanguageLocale).SingleOrDefaultAsync(cancellationToken);
        var book = new PublicationBook
        {
            ProjectId = project.Id,
            Revision = 1,
            Title = Text(source?.TitleOverride, project.Name),
            Subtitle = source?.Subtitle ?? string.Empty,
            Author = source?.Author ?? string.Empty,
            Language = Text(source?.Language, Text(briefLanguage, "en")),
            Publisher = source?.Publisher ?? string.Empty,
            Copyright = source?.Copyright ?? string.Empty,
            Description = source?.Description ?? string.Empty,
            IncludeTableOfContents = source?.IncludeTableOfContents ?? true,
            IncludeVisibleTableOfContents = source?.IncludeVisibleTableOfContents ?? false,
            IncludeActSynopses = source?.IncludeActSynopses ?? false,
            IncludeChapterSynopses = source?.IncludeChapterSynopses ?? false,
            IncludeActHeadings = source?.IncludeActHeadings ?? true,
            IncludeChapterHeadings = source?.IncludeChapterHeadings ?? true,
            NumberActs = source?.NumberActs ?? false,
            NumberChapters = source?.NumberChapters ?? false,
            TitlePageMode = source?.TitlePageMode ?? PublishTitlePageMode.Automatic,
        };
        db.PublicationBooks.Add(book);

        if (source is not null)
        {
            book.OutlineItems = source.OutlineItems.Select(item => new PublicationBookOutlineItem
            {
                Id = item.Id, ProjectId = project.Id, TargetKind = item.TargetKind, TargetId = item.TargetId,
                ActId = item.ActId, ChapterId = item.ChapterId, IsIncluded = item.IsIncluded, SortOrder = item.SortOrder,
            }).ToList();
            book.Matter = source.Matter.Select(item => new PublicationBookMatter
            {
                Id = item.Id, ProjectId = project.Id, Location = item.Location, Kind = item.Kind, Title = item.Title,
                ManuscriptJson = item.ManuscriptJson, Revision = item.Revision, IsIncluded = item.IsIncluded, SortOrder = item.SortOrder,
            }).ToList();
            book.ImagePlacements = source.ImagePlacements.Select(item => new PublicationBookImagePlacement
            {
                Id = item.Id, ProjectId = project.Id, AssetId = item.AssetId, TargetKind = item.TargetKind, TargetId = item.TargetId,
                ActId = item.ActId, ChapterId = item.ChapterId, PlacementKind = item.PlacementKind, SortOrder = item.SortOrder,
                Caption = item.Caption, PresentationJson = item.PresentationJson, AltText = item.AltText,
                Decorative = item.Decorative, Language = item.Language, AccessibilityRole = item.AccessibilityRole,
            }).ToList();
        }
        book.CoverDesign = new PublicationBookCoverDesign
        {
            Id = source?.CoverDesign?.Id ?? Guid.NewGuid(),
            ProjectId = project.Id,
            BackgroundColor = source?.CoverDesign?.BackgroundColor ?? "#5c7ca5",
            CompositionSceneJson = CoreCoverScene(project.Id, source, setup),
            Revision = source?.CoverDesign?.Revision ?? 0,
        };
        await SaveMigrationChangesAsync(db, $"Core seed for project {project.Id:N}", cancellationToken);

        foreach (var release in releases)
        {
            release.OverrideFieldsJson = JsonSerializer.Serialize(ChangedFields(book, setup, release));
            release.InheritsCoreCover = release.CoverDesign is null;
            if (release.CoverDesign is not null) release.CoverDesign.InheritsCoreFront = false;
            var releaseOutline = release.OutlineItems.ToList();
            foreach (var row in releaseOutline)
            {
                var inherited = book.OutlineItems.SingleOrDefault(item => item.TargetKind == row.TargetKind && item.TargetId == row.TargetId);
                if (inherited is not null && inherited.IsIncluded == row.IsIncluded && inherited.SortOrder == row.SortOrder)
                {
                    await db.PublicationEditionOutlineItems.Where(item => item.Id == row.Id)
                        .ExecuteDeleteAsync(cancellationToken);
                    db.Entry(row).State = EntityState.Detached;
                }
            }
            foreach (var inherited in book.OutlineItems.Where(core => !releaseOutline.Any(
                row => row.TargetKind == core.TargetKind && row.TargetId == core.TargetId)))
            {
                db.PublicationEditionOutlineItems.Add(new PublicationEditionOutlineItem
                {
                    EditionId = release.Id,
                    TargetKind = inherited.TargetKind,
                    TargetId = inherited.TargetId,
                    ActId = inherited.ActId,
                    ChapterId = inherited.ChapterId,
                    IsIncluded = false,
                    SortOrder = inherited.SortOrder,
                });
            }

            var releaseMatter = release.Matter.ToList();
            var matchedCoreMatter = new HashSet<Guid>();
            foreach (var row in releaseMatter)
            {
                var inherited = book.Matter.FirstOrDefault(item => !matchedCoreMatter.Contains(item.Id)
                    && item.Location == row.Location && item.Kind == row.Kind && item.SortOrder == row.SortOrder);
                if (inherited is null) continue;
                matchedCoreMatter.Add(inherited.Id);
                if (row.Title == inherited.Title && row.ManuscriptJson == inherited.ManuscriptJson
                    && row.Revision == inherited.Revision && row.IsIncluded == inherited.IsIncluded)
                {
                    await db.PublicationMatter.Where(item => item.Id == row.Id)
                        .ExecuteDeleteAsync(cancellationToken);
                    db.Entry(row).State = EntityState.Detached;
                }
                else
                    row.CoreMatterId = inherited.Id;
            }
            foreach (var inherited in book.Matter.Where(item => !matchedCoreMatter.Contains(item.Id)))
            {
                db.PublicationMatter.Add(new PublicationMatter
                {
                    EditionId = release.Id,
                    CoreMatterId = inherited.Id,
                    IsExcluded = true,
                    Location = inherited.Location,
                    Kind = inherited.Kind,
                    Title = inherited.Title,
                    ManuscriptJson = inherited.ManuscriptJson,
                    Revision = inherited.Revision,
                    IsIncluded = false,
                    SortOrder = inherited.SortOrder,
                });
            }

            var releasePlacements = release.ImagePlacements.ToList();
            var matchedCorePlacements = new HashSet<Guid>();
            foreach (var row in releasePlacements)
            {
                var inherited = book.ImagePlacements.FirstOrDefault(item => !matchedCorePlacements.Contains(item.Id)
                    && item.TargetKind == row.TargetKind
                    && item.TargetId == row.TargetId && item.PlacementKind == row.PlacementKind && item.SortOrder == row.SortOrder);
                if (inherited is null) continue;
                matchedCorePlacements.Add(inherited.Id);
                if (row.AssetId == inherited.AssetId && row.Caption == inherited.Caption
                    && row.PresentationJson == inherited.PresentationJson && row.AltText == inherited.AltText
                    && row.Decorative == inherited.Decorative && row.Language == inherited.Language
                    && row.AccessibilityRole == inherited.AccessibilityRole)
                {
                    await db.PublicationImagePlacements.Where(item => item.Id == row.Id)
                        .ExecuteDeleteAsync(cancellationToken);
                    db.Entry(row).State = EntityState.Detached;
                }
                else
                    row.CorePlacementId = inherited.Id;
            }
            foreach (var inherited in book.ImagePlacements.Where(item => !matchedCorePlacements.Contains(item.Id)))
            {
                db.PublicationImagePlacements.Add(new PublicationImagePlacement
                {
                    EditionId = release.Id,
                    CorePlacementId = inherited.Id,
                    IsExcluded = true,
                    AssetId = inherited.AssetId,
                    TargetKind = inherited.TargetKind,
                    TargetId = inherited.TargetId,
                    ActId = inherited.ActId,
                    ChapterId = inherited.ChapterId,
                    PlacementKind = inherited.PlacementKind,
                    SortOrder = inherited.SortOrder,
                    Caption = inherited.Caption,
                    PresentationJson = inherited.PresentationJson,
                    AltText = inherited.AltText,
                    Decorative = inherited.Decorative,
                    Language = inherited.Language,
                    AccessibilityRole = inherited.AccessibilityRole,
                });
            }
        }
    }

    private static string CoreCoverScene(Guid projectId, PublicationEdition? source, ProjectPageSetup? setup)
    {
        var coreEdition = new PublicationEdition
        {
            ProjectId = projectId,
            Name = "Core Book",
            Format = PublicationEditionFormat.DigitalPdf,
            PageWidthInches = setup?.PageWidthInches ?? 6,
            PageHeightInches = setup?.PageHeightInches ?? 9,
            PageMarginInches = setup?.PageMarginInches ?? .75,
            BodyFontSizePoints = setup?.BodyFontSizePoints ?? 12,
            BodyLineHeight = setup?.BodyLineHeight ?? 1.55,
        };
        if (source?.CoverDesign is null || string.IsNullOrWhiteSpace(source.CoverDesign.CompositionSceneJson))
            return JsonSerializer.Serialize(
                CoverCompositionFactory.Create(coreEdition, new PublicationCoverDesign { EditionId = Guid.Empty }),
                ManuscriptCodec.JsonOptions);
        var sourceScene = JsonSerializer.Deserialize<CompositionScene>(
            source.CoverDesign.CompositionSceneJson,
            ManuscriptCodec.JsonOptions) ?? throw new InvalidDataException("The source release cover scene is empty.");
        return JsonSerializer.Serialize(
            CoverCompositionFactory.CreateCoreFrontFromRelease(source, sourceScene, coreEdition),
            ManuscriptCodec.JsonOptions);
    }

    private static async Task SaveMigrationChangesAsync(
        AppDbContext db,
        string phase,
        CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            var entries = exception.Entries.Select(entry =>
            {
                var keys = entry.Metadata.FindPrimaryKey()?.Properties
                    .Select(property => $"{property.Name}={entry.Property(property.Name).CurrentValue}") ?? [];
                return $"{entry.Metadata.ClrType.Name}({string.Join(',', keys)})";
            });
            throw new InvalidDataException(
                $"Core Book migration lost a row during {phase}: {string.Join("; ", entries)}.",
                exception);
        }
    }

    internal static PublicationEditionOverrideField[] ChangedFields(PublicationBook core, ProjectPageSetup? setup, PublicationEdition release)
    {
        var fields = new List<PublicationEditionOverrideField>();
        Add(fields, PublicationEditionOverrideField.Title, release.TitleOverride != core.Title);
        Add(fields, PublicationEditionOverrideField.Subtitle, release.Subtitle != core.Subtitle);
        Add(fields, PublicationEditionOverrideField.Author, release.Author != core.Author);
        Add(fields, PublicationEditionOverrideField.Language, release.Language != core.Language);
        Add(fields, PublicationEditionOverrideField.Publisher, release.Publisher != core.Publisher);
        Add(fields, PublicationEditionOverrideField.Copyright, release.Copyright != core.Copyright);
        Add(fields, PublicationEditionOverrideField.Description, release.Description != core.Description);
        Add(fields, PublicationEditionOverrideField.IncludeTableOfContents, release.IncludeTableOfContents != core.IncludeTableOfContents);
        Add(fields, PublicationEditionOverrideField.IncludeVisibleTableOfContents, release.IncludeVisibleTableOfContents != core.IncludeVisibleTableOfContents);
        Add(fields, PublicationEditionOverrideField.IncludeActSynopses, release.IncludeActSynopses != core.IncludeActSynopses);
        Add(fields, PublicationEditionOverrideField.IncludeChapterSynopses, release.IncludeChapterSynopses != core.IncludeChapterSynopses);
        Add(fields, PublicationEditionOverrideField.IncludeActHeadings, release.IncludeActHeadings != core.IncludeActHeadings);
        Add(fields, PublicationEditionOverrideField.IncludeChapterHeadings, release.IncludeChapterHeadings != core.IncludeChapterHeadings);
        Add(fields, PublicationEditionOverrideField.NumberActs, release.NumberActs != core.NumberActs);
        Add(fields, PublicationEditionOverrideField.NumberChapters, release.NumberChapters != core.NumberChapters);
        Add(fields, PublicationEditionOverrideField.TitlePageMode, release.TitlePageMode != core.TitlePageMode);
        Add(fields, PublicationEditionOverrideField.AllowDesignedPageOverrides,
            release.Format == PublicationEditionFormat.DigitalPdf && release.AllowDesignedPageOverrides);
        Add(fields, PublicationEditionOverrideField.PageWidthInches, setup is not null && release.PageWidthInches != setup.PageWidthInches);
        Add(fields, PublicationEditionOverrideField.PageHeightInches, setup is not null && release.PageHeightInches != setup.PageHeightInches);
        Add(fields, PublicationEditionOverrideField.PageMarginInches, setup is not null && release.PageMarginInches != setup.PageMarginInches);
        return fields.ToArray();
    }

    private static void Add(List<PublicationEditionOverrideField> fields, PublicationEditionOverrideField field, bool condition)
    {
        if (condition) fields.Add(field);
    }

    private static string Text(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static async Task<Guid?> ReadLegacyDefaultReleaseIdAsync(AppDbContext db, Guid projectId, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, ProjectId FROM PublicationEditions WHERE IsDefault = 1";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (ReadGuid(reader.GetValue(1)) != projectId)
                continue;
            return ReadGuid(reader.GetValue(0));
        }
        return null;
    }

    private static Guid? ReadGuid(object value)
    {
        if (value is Guid id)
            return id;
        if (value is byte[] bytes && bytes.Length == 16)
            return new Guid(bytes);
        return Guid.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), out id)
            ? id
            : null;
    }

    private static async Task<string> ArtifactStateAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var artifacts = await db.PublicationArtifacts.AsNoTracking().OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.Sha256, item.ByteLength, item.Data }).ToListAsync(cancellationToken);
        return Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('|', artifacts.Select(
            item => $"{item.Id:N}:{item.Sha256}:{item.ByteLength}:{Convert.ToHexStringLower(SHA256.HashData(item.Data))}")))));
    }

    private static async Task<bool> HasForeignKeyViolationsAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check";
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken);
    }

    private static async Task<string> ReleaseProjectionStateAsync(
        AppDbContext db,
        bool useCoreInheritance,
        CancellationToken cancellationToken)
    {
        var releases = await db.PublicationEditions.AsNoTracking().OrderBy(item => item.Id).ToListAsync(cancellationToken);
        await ApplyLegacyTypographyAsync(db, releases, cancellationToken);
        var resolver = new PublicationEffectiveConfigurationResolver(
            db,
            readPdfPresentation: false,
            readPublicationSections: false);
        var projections = new List<object>(releases.Count);
        foreach (var stored in releases)
        {
            var resolved = useCoreInheritance
                ? await resolver.ResolveReleaseAsync(stored.ProjectId, stored.Id, cancellationToken)
                : new EffectivePublicationRelease(
                    new PublicationBook(), stored, new HashSet<PublicationEditionOverrideField>(),
                    await db.PublicationEditionOutlineItems.AsNoTracking().Where(item => item.EditionId == stored.Id).ToListAsync(cancellationToken),
                    []);
            var matter = useCoreInheritance
                ? await ResolveLegacyMatterAsync(db, stored.ProjectId, stored.Id, cancellationToken)
                : await db.PublicationMatter.AsNoTracking().Where(item => item.EditionId == stored.Id).ToListAsync(cancellationToken);
            var placements = useCoreInheritance
                ? await ResolveLegacyPlacementsAsync(db, stored.ProjectId, stored.Id, cancellationToken)
                : await db.PublicationImagePlacements.AsNoTracking().Where(item => item.EditionId == stored.Id).ToListAsync(cancellationToken);
            var edition = resolved.Edition;
            var cover = await db.PublicationCoverDesigns.AsNoTracking().SingleOrDefaultAsync(item => item.EditionId == stored.Id, cancellationToken);
            projections.Add(new
            {
                edition.Id, edition.ProjectId, edition.Name, edition.Format, edition.Vendor, edition.VendorProfileVersion,
                edition.Status, edition.Revision, edition.TitleOverride, edition.Subtitle, edition.Author, edition.Language,
                edition.Publisher, edition.Copyright, edition.Isbn, edition.Description,
                edition.IncludeTableOfContents, edition.IncludeVisibleTableOfContents,
                edition.IncludeActSynopses, edition.IncludeChapterSynopses, edition.IncludeActHeadings,
                edition.IncludeChapterHeadings, edition.NumberActs, edition.NumberChapters, edition.TitlePageMode,
                edition.PrintRegistryVersion, edition.PrintProductKey, edition.PrintFinish,
                edition.PrintCoverMode, edition.GenericPrintTemplateJson, edition.Bleed, edition.PageWidthInches,
                edition.PageHeightInches, edition.PageMarginInches, edition.BodyFontSizePoints,
                edition.BodyLineHeight, edition.SelectedCoverImageId, edition.AllowDesignedPageOverrides,
                Outline = resolved.OutlineItems.Where(item => item.IsIncluded).OrderBy(item => item.SortOrder)
                    .Select(item => new { item.TargetKind, item.TargetId, item.IsIncluded, item.SortOrder }),
                Matter = matter.OrderBy(item => item.Location).ThenBy(item => item.SortOrder)
                    .Select(item => new { item.Location, item.Kind, item.Title, item.ManuscriptJson, item.Revision, item.IsIncluded, item.SortOrder }),
                Placements = placements.OrderBy(item => item.SortOrder)
                    .Select(item => new { item.AssetId, item.TargetKind, item.TargetId, item.PlacementKind, item.SortOrder,
                        item.Caption, item.PresentationJson, item.AltText, item.Decorative, item.Language, item.AccessibilityRole }),
                Cover = cover is null ? null : new { cover.Title, cover.Subtitle, cover.Author, cover.SpineText,
                    cover.BackCopy, cover.BackgroundColor, cover.BarcodeMode, cover.ImageCropXPercent,
                    cover.ImageCropYPercent, cover.CompositionSceneJson, cover.Revision },
            });
        }
        var json = JsonSerializer.Serialize(projections, ManuscriptCodec.JsonOptions);
        return Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    private static async Task<IReadOnlyList<PublicationMatter>> ResolveLegacyMatterAsync(
        AppDbContext db,
        Guid projectId,
        Guid editionId,
        CancellationToken cancellationToken)
    {
        var core = await db.PublicationBookMatter.AsNoTracking()
            .Where(item => item.ProjectId == projectId).ToListAsync(cancellationToken);
        var overrides = await db.PublicationMatter.AsNoTracking()
            .Where(item => item.EditionId == editionId).ToListAsync(cancellationToken);
        var byCore = overrides.Where(item => item.CoreMatterId is not null)
            .ToDictionary(item => item.CoreMatterId!.Value);
        var inherited = core
            .Where(item => !(byCore.TryGetValue(item.Id, out var linked) && linked.IsExcluded))
            .Select(item => byCore.TryGetValue(item.Id, out var value) ? value : new PublicationMatter
            {
                Id = item.Id,
                EditionId = editionId,
                CoreMatterId = item.Id,
                Location = item.Location,
                Kind = item.Kind,
                Title = item.Title,
                ManuscriptJson = item.ManuscriptJson,
                Revision = item.Revision,
                IsIncluded = item.IsIncluded,
                SortOrder = item.SortOrder,
            });
        return inherited.Concat(overrides.Where(item => item.CoreMatterId is null && !item.IsExcluded))
            .OrderBy(item => item.Location).ThenBy(item => item.SortOrder).ToList();
    }

    private static async Task<IReadOnlyList<PublicationImagePlacement>> ResolveLegacyPlacementsAsync(
        AppDbContext db,
        Guid projectId,
        Guid editionId,
        CancellationToken cancellationToken)
    {
        var core = await db.PublicationBookImagePlacements.AsNoTracking()
            .Where(item => item.ProjectId == projectId).ToListAsync(cancellationToken);
        var overrides = await db.PublicationImagePlacements.AsNoTracking()
            .Where(item => item.EditionId == editionId).ToListAsync(cancellationToken);
        var byCore = overrides.Where(item => item.CorePlacementId is not null)
            .ToDictionary(item => item.CorePlacementId!.Value);
        var inherited = core
            .Where(item => !(byCore.TryGetValue(item.Id, out var linked) && linked.IsExcluded))
            .Select(item => byCore.TryGetValue(item.Id, out var value) ? value : new PublicationImagePlacement
            {
                Id = item.Id,
                EditionId = editionId,
                CorePlacementId = item.Id,
                AssetId = item.AssetId,
                TargetKind = item.TargetKind,
                TargetId = item.TargetId,
                ActId = item.ActId,
                ChapterId = item.ChapterId,
                PlacementKind = item.PlacementKind,
                SortOrder = item.SortOrder,
                Caption = item.Caption,
                PresentationJson = item.PresentationJson,
                AltText = item.AltText,
                Decorative = item.Decorative,
                Language = item.Language,
                AccessibilityRole = item.AccessibilityRole,
            });
        return inherited.Concat(overrides.Where(item => item.CorePlacementId is null && !item.IsExcluded))
            .OrderBy(item => item.SortOrder).ToList();
    }

    private static async Task ApplyLegacyTypographyAsync(
        AppDbContext db,
        IReadOnlyCollection<PublicationEdition> releases,
        CancellationToken cancellationToken)
    {
        if (releases.Count == 0)
            return;
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "SELECT Id, BodyFontSizePoints, BodyLineHeight FROM PublicationEditions";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var byId = releases.ToDictionary(item => item.Id);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!Guid.TryParse(reader.GetString(0), out var id) || !byId.TryGetValue(id, out var release))
                continue;
            release.BodyFontSizePoints = reader.GetDouble(1);
            release.BodyLineHeight = reader.GetDouble(2);
        }
    }

}
