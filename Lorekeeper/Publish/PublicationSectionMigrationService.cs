using System.Text.Json;
using Lorekeeper.Composition;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Lorekeeper.Publish;

public interface IPublicationSectionMigrationService
{
    Task ApplyPendingAsync(AppDbContext db, CancellationToken cancellationToken = default);
    Task RepairSemanticRevisionDriftAsync(AppDbContext db, CancellationToken cancellationToken = default);
}

public sealed class PublicationSectionMigrationService(
    IDatabaseMigrationRecoveryService recovery,
    ILogger<PublicationSectionMigrationService> logger) : IPublicationSectionMigrationService
{
    public const string MigrationName = "publication-sections-v1";
    public const string SemanticRevisionRepairMigrationName = "publication-section-semantic-revisions-v1";
    public const string AdditiveMigrationId = "20260812033915_AddPublicationSectionsV25";
    public const string StartSideMigrationId = "20260813181834_AddPublicationSectionStartSideV28";

    public async Task ApplyPendingAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        if (!applied.Contains(AdditiveMigrationId))
        {
            if (!applied.Contains(EditionContentMigrationService.CleanupMigrationId))
                await db.GetService<IMigrator>().MigrateAsync(EditionContentMigrationService.CleanupMigrationId, cancellationToken);
            await db.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER IF EXISTS TR_PageCompositions_ActiveAuthoringVariant_Update;",
                cancellationToken);
            await db.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER IF EXISTS TR_PageCompositionVariants_ClearAuthoringSelection;",
                cancellationToken);
            await db.GetService<IMigrator>().MigrateAsync(AdditiveMigrationId, cancellationToken);
        }
        await DatabaseStartupMigrationService.EnsurePublicationSectionStartSideCompatibilityColumnAsync(db, cancellationToken);
        await DatabaseStartupMigrationService.EnsurePrintProductCompatibilityColumnsAsync(db, cancellationToken);
        await EnsureAuthoringTriggersAsync(db, cancellationToken);
        if (await db.ManuscriptMigrationJournals.AsNoTracking().AnyAsync(
            item => item.MigrationName == MigrationName && item.Status == ManuscriptMigrationStatus.Completed,
            cancellationToken))
            return;

        var backupPath = await recovery.CreateBackupAsync("publishing", "pre-publication-sections-v1", cancellationToken);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var journal = new ManuscriptMigrationJournal
            {
                MigrationName = MigrationName,
                SourceSchemaVersion = ManuscriptDocument.CurrentSchemaVersion,
                TargetSchemaVersion = ManuscriptDocument.CurrentSchemaVersion,
                Phase = ManuscriptMigrationPhase.Transform,
                Status = ManuscriptMigrationStatus.Running,
                BackupPath = backupPath,
            };
            db.ManuscriptMigrationJournals.Add(journal);
            await db.SaveChangesAsync(cancellationToken);

            var artifactState = await db.PublicationArtifacts.AsNoTracking().OrderBy(item => item.Id)
                .Select(item => new { item.Id, item.Sha256, item.ByteLength }).ToListAsync(cancellationToken);
            var coreMatter = await db.PublicationBookMatter.AsNoTracking().OrderBy(item => item.ProjectId).ThenBy(item => item.SortOrder).ToListAsync(cancellationToken);
            var releaseMatter = await db.PublicationMatter.AsNoTracking().OrderBy(item => item.EditionId).ThenBy(item => item.SortOrder).ToListAsync(cancellationToken);
            var corePlacements = await db.PublicationBookImagePlacements.AsNoTracking().OrderBy(item => item.ProjectId).ThenBy(item => item.SortOrder).ToListAsync(cancellationToken);
            var releasePlacements = await db.PublicationImagePlacements.AsNoTracking().OrderBy(item => item.EditionId).ThenBy(item => item.SortOrder).ToListAsync(cancellationToken);
            var coreSectionMap = new Dictionary<Guid, Guid>();
            var corePlacementMap = new Dictionary<Guid, Guid>();

            foreach (var matter in coreMatter)
            {
                var section = FromCoreMatter(matter);
                coreSectionMap[matter.Id] = section.Id;
                db.PublicationSections.Add(section);
            }
            foreach (var matter in releaseMatter)
            {
                var edition = await db.PublicationEditions.AsNoTracking().SingleAsync(item => item.Id == matter.EditionId, cancellationToken);
                db.PublicationSections.Add(FromReleaseMatter(edition.ProjectId, matter, coreSectionMap));
            }
            await db.SaveChangesAsync(cancellationToken);

            foreach (var placement in corePlacements)
            {
                var setup = await db.ProjectPageSetups.AsNoTracking().SingleAsync(item => item.ProjectId == placement.ProjectId, cancellationToken);
                var converted = ConvertCorePlacement(placement, setup);
                corePlacementMap[placement.Id] = converted.Section.Id;
                db.PublicationSections.Add(converted.Section);
            }
            foreach (var placement in releasePlacements)
            {
                var edition = await db.PublicationEditions.AsNoTracking().SingleAsync(item => item.Id == placement.EditionId, cancellationToken);
                var converted = ConvertReleasePlacement(placement, edition, corePlacementMap);
                db.PublicationSections.Add(converted.Section);
            }
            await db.SaveChangesAsync(cancellationToken);

            foreach (var projectId in await db.Projects.AsNoTracking().Select(item => item.Id).ToListAsync(cancellationToken))
                await AddMissingSystemSectionsAsync(db, projectId, cancellationToken);

            await db.PublicationArtifacts.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsLegacy, true), cancellationToken);
            await db.PublicationRenderJobs.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsLegacy, true), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);

            var retainedArtifacts = await db.PublicationArtifacts.AsNoTracking().OrderBy(item => item.Id)
                .Select(item => new { item.Id, item.Sha256, item.ByteLength }).ToListAsync(cancellationToken);
            if (!artifactState.SequenceEqual(retainedArtifacts))
                throw new InvalidDataException("Publication-section migration changed retained artifact identities, sizes, or hashes.");
            if (await db.PublicationSections.CountAsync(cancellationToken)
                < coreMatter.Count + releaseMatter.Count + corePlacements.Count + releasePlacements.Count)
                throw new InvalidDataException("Publication-section migration did not preserve every stored matter and image-placement row.");
            var orphanedCompositions = await db.PageCompositions.CountAsync(
                item => (item.ChapterId == null) == (item.PublicationSectionId == null), cancellationToken);
            if (orphanedCompositions != 0)
                throw new InvalidDataException("Publication-section migration produced a Designed Page with invalid ownership.");

            journal.ValidationReportJson = JsonSerializer.Serialize(new
            {
                coreMatter = coreMatter.Count,
                releaseMatter = releaseMatter.Count,
                coreImagePages = corePlacements.Count,
                releaseImagePages = releasePlacements.Count,
                sections = await db.PublicationSections.CountAsync(cancellationToken),
                artifactsPreserved = artifactState.Count,
            });
            journal.Phase = ManuscriptMigrationPhase.Complete;
            journal.Status = ManuscriptMigrationStatus.Completed;
            journal.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            db.ChangeTracker.Clear();
            logger.LogError(exception, "Publication-section migration failed.");
            await recovery.EnterRecoveryModeAsync(db, backupPath, MigrationName, 18, 19, exception, cancellationToken);
        }
    }

    public async Task RepairSemanticRevisionDriftAsync(
        AppDbContext db,
        CancellationToken cancellationToken = default)
    {
        if (await db.ManuscriptMigrationJournals.AsNoTracking().AnyAsync(
            item => item.MigrationName == SemanticRevisionRepairMigrationName
                && item.Status == ManuscriptMigrationStatus.Completed,
            cancellationToken))
        {
            return;
        }

        var compositions = await db.PageCompositions
            .Where(item => item.PublicationSectionId != null)
            .OrderBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var documents = compositions.ToDictionary(
            item => item.Id,
            item => ManuscriptCodec.Deserialize(item.SemanticManuscriptJson));
        foreach (var composition in compositions)
        {
            var document = documents[composition.Id];
            ManuscriptCodec.Validate(document, composition.Id, document.Revision);
        }

        var drifted = compositions
            .Where(item => documents[item.Id].Revision != item.Revision)
            .ToList();
        if (drifted.Count == 0)
        {
            db.ManuscriptMigrationJournals.Add(CompletedRepairJournal(string.Empty, 0));
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var backupPath = await recovery.CreateBackupAsync(
            "publishing",
            "pre-publication-section-semantic-revisions-v1",
            cancellationToken);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var journal = new ManuscriptMigrationJournal
            {
                MigrationName = SemanticRevisionRepairMigrationName,
                SourceSchemaVersion = ManuscriptDocument.CurrentSchemaVersion,
                TargetSchemaVersion = ManuscriptDocument.CurrentSchemaVersion,
                Phase = ManuscriptMigrationPhase.Transform,
                Status = ManuscriptMigrationStatus.Running,
                BackupPath = backupPath,
            };
            db.ManuscriptMigrationJournals.Add(journal);

            foreach (var composition in drifted)
            {
                var document = documents[composition.Id];
                if (document.Revision > composition.Revision)
                {
                    throw new InvalidDataException(
                        $"Publication page {composition.Id:N} has semantic revision {document.Revision}, "
                        + $"which is newer than owning revision {composition.Revision}.");
                }

                composition.SemanticManuscriptJson = ManuscriptCodec.Serialize(
                    document with { Revision = composition.Revision });
                ManuscriptCodec.Deserialize(
                    composition.SemanticManuscriptJson,
                    composition.Id,
                    composition.Revision);
            }

            journal.ValidationReportJson = JsonSerializer.Serialize(new { repairedCompositions = drifted.Count });
            journal.Phase = ManuscriptMigrationPhase.Complete;
            journal.Status = ManuscriptMigrationStatus.Completed;
            journal.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            db.ChangeTracker.Clear();
            logger.LogError(exception, "Publication-section semantic revision repair failed.");
            await recovery.EnterRecoveryModeAsync(
                db,
                backupPath,
                SemanticRevisionRepairMigrationName,
                ManuscriptDocument.CurrentSchemaVersion,
                ManuscriptDocument.CurrentSchemaVersion,
                exception,
                cancellationToken);
        }
    }

    private static ManuscriptMigrationJournal CompletedRepairJournal(string backupPath, int repairedCompositions) =>
        new()
        {
            MigrationName = SemanticRevisionRepairMigrationName,
            SourceSchemaVersion = ManuscriptDocument.CurrentSchemaVersion,
            TargetSchemaVersion = ManuscriptDocument.CurrentSchemaVersion,
            Phase = ManuscriptMigrationPhase.Complete,
            Status = ManuscriptMigrationStatus.Completed,
            BackupPath = backupPath,
            ValidationReportJson = JsonSerializer.Serialize(new { repairedCompositions }),
            CompletedAt = DateTime.UtcNow,
        };

    private static async Task EnsureAuthoringTriggersAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        // SQLite defers table-rebuild operations produced by AlterColumn and
        // AddForeignKey. Recreating these triggers inside the EF migration can
        // therefore leave a trigger pointing at the temporary, already-dropped
        // table. Restore them only after the complete schema migration commits.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER IF NOT EXISTS TR_PageCompositions_ActiveAuthoringVariant_Update
            BEFORE UPDATE OF ActiveAuthoringVariantId ON PageCompositions
            WHEN NEW.ActiveAuthoringVariantId IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1 FROM PageCompositionVariants
                  WHERE Id = NEW.ActiveAuthoringVariantId AND CompositionId = NEW.Id)
            BEGIN
                SELECT RAISE(ABORT, 'Active authoring variant must belong to the composition.');
            END;
            """, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER IF NOT EXISTS TR_PageCompositionVariants_ClearAuthoringSelection
            AFTER DELETE ON PageCompositionVariants
            BEGIN
                UPDATE PageCompositions SET ActiveAuthoringVariantId = NULL
                WHERE ActiveAuthoringVariantId = OLD.Id;
            END;
            """, cancellationToken);
    }

    internal static async Task ConvertImportedLegacyProjectAsync(
        AppDbContext db,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        if (await db.PublicationSections.AnyAsync(item => item.ProjectId == projectId, cancellationToken))
            throw new InvalidDataException("Legacy publication content cannot be converted into a project that already contains publication sections.");

        var coreMatter = await db.PublicationBookMatter.AsNoTracking()
            .Where(item => item.ProjectId == projectId)
            .OrderBy(item => item.SortOrder)
            .ToListAsync(cancellationToken);
        var releases = await db.PublicationEditions.AsNoTracking()
            .Where(item => item.ProjectId == projectId)
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var releaseIds = releases.Keys.ToHashSet();
        var releaseMatter = await db.PublicationMatter.AsNoTracking()
            .Where(item => releaseIds.Contains(item.EditionId))
            .OrderBy(item => item.EditionId)
            .ThenBy(item => item.SortOrder)
            .ToListAsync(cancellationToken);
        var corePlacements = await db.PublicationBookImagePlacements.AsNoTracking()
            .Where(item => item.ProjectId == projectId)
            .OrderBy(item => item.SortOrder)
            .ToListAsync(cancellationToken);
        var releasePlacements = await db.PublicationImagePlacements.AsNoTracking()
            .Where(item => releaseIds.Contains(item.EditionId))
            .OrderBy(item => item.EditionId)
            .ThenBy(item => item.SortOrder)
            .ToListAsync(cancellationToken);

        var coreSectionMap = new Dictionary<Guid, Guid>();
        foreach (var matter in coreMatter)
        {
            var section = FromCoreMatter(matter);
            coreSectionMap[matter.Id] = section.Id;
            db.PublicationSections.Add(section);
        }
        foreach (var matter in releaseMatter)
            db.PublicationSections.Add(FromReleaseMatter(projectId, matter, coreSectionMap));
        await db.SaveChangesAsync(cancellationToken);

        var setup = await db.ProjectPageSetups.AsNoTracking()
            .SingleAsync(item => item.ProjectId == projectId, cancellationToken);
        var corePlacementMap = new Dictionary<Guid, Guid>();
        foreach (var placement in corePlacements)
        {
            var converted = ConvertCorePlacement(placement, setup);
            corePlacementMap[placement.Id] = converted.Section.Id;
            db.PublicationSections.Add(converted.Section);
        }
        foreach (var placement in releasePlacements)
        {
            var edition = releases[placement.EditionId];
            db.PublicationSections.Add(ConvertReleasePlacement(placement, edition, corePlacementMap).Section);
        }
        await db.SaveChangesAsync(cancellationToken);
        await AddMissingSystemSectionsAsync(db, projectId, cancellationToken);
    }

    private static PublicationSection FromCoreMatter(PublicationBookMatter matter)
    {
        var id = Guid.NewGuid();
        return new PublicationSection
        {
            Id = id, ProjectId = matter.ProjectId, Title = matter.Title,
            Kind = Kind(matter.Kind), SystemRole = Role(matter.Kind),
            Anchor = matter.Location == PublicationMatterLocation.Front ? PublicationSectionAnchor.Front : PublicationSectionAnchor.Back,
            InclusionMode = matter.IsIncluded ? PublicationSectionInclusionMode.Included : PublicationSectionInclusionMode.Omitted,
            StartSide = RecommendedStartSide(Role(matter.Kind), Kind(matter.Kind)),
            LocalOrder = LegacyMatterOrder(matter), ManuscriptJson = RemapDocument(matter.ManuscriptJson, matter.Id, matter.Revision, id),
            Revision = matter.Revision, CreatedAt = matter.CreatedAt, UpdatedAt = matter.UpdatedAt,
        };
    }

    private static PublicationSection FromReleaseMatter(
        Guid projectId,
        PublicationMatter matter,
        IReadOnlyDictionary<Guid, Guid> coreMap)
    {
        var id = Guid.NewGuid();
        return new PublicationSection
        {
            Id = id, ProjectId = projectId, EditionId = matter.EditionId,
            CoreSectionId = matter.CoreMatterId is Guid coreId ? coreMap.GetValueOrDefault(coreId) : null,
            IsExcluded = matter.IsExcluded, Title = matter.Title, Kind = Kind(matter.Kind), SystemRole = Role(matter.Kind),
            Anchor = matter.Location == PublicationMatterLocation.Front ? PublicationSectionAnchor.Front : PublicationSectionAnchor.Back,
            InclusionMode = matter.IsExcluded || !matter.IsIncluded ? PublicationSectionInclusionMode.Omitted : PublicationSectionInclusionMode.Included,
            StartSide = RecommendedStartSide(Role(matter.Kind), Kind(matter.Kind)),
            LocalOrder = LegacyMatterOrder(matter), ManuscriptJson = RemapDocument(matter.ManuscriptJson, matter.Id, matter.Revision, id),
            Revision = matter.Revision, CreatedAt = matter.CreatedAt, UpdatedAt = matter.UpdatedAt,
        };
    }

    private static ConvertedPlacement ConvertCorePlacement(PublicationBookImagePlacement placement, ProjectPageSetup setup)
    {
        var sectionId = Guid.NewGuid();
        var scene = CompositionService.CreatePageScene(setup);
        return ConvertPlacement(sectionId, placement.ProjectId, null, null, placement.AssetId, placement.Caption,
            placement.PresentationJson, placement.AltText, placement.Decorative, placement.Language,
            placement.AccessibilityRole, placement.TargetKind, placement.TargetId, placement.PlacementKind,
            placement.SortOrder, scene, placement.CreatedAt, placement.UpdatedAt);
    }

    private static ConvertedPlacement ConvertReleasePlacement(
        PublicationImagePlacement placement,
        PublicationEdition edition,
        IReadOnlyDictionary<Guid, Guid> coreMap)
    {
        if (placement.IsExcluded && placement.CorePlacementId is Guid excludedCore)
        {
            var excludedSectionId = Guid.NewGuid();
            return new ConvertedPlacement(new PublicationSection
            {
                Id = excludedSectionId, ProjectId = edition.ProjectId, EditionId = edition.Id,
                CoreSectionId = coreMap.GetValueOrDefault(excludedCore), IsExcluded = true,
                Title = "Image page", Kind = PublicationSectionKind.Custom,
                Anchor = Anchor(placement.PlacementKind), TargetKind = placement.TargetKind, TargetId = placement.TargetId,
                ActId = placement.ActId, ChapterId = placement.ChapterId,
                InclusionMode = PublicationSectionInclusionMode.Omitted, LocalOrder = placement.SortOrder,
                ManuscriptJson = ManuscriptCodec.Serialize(new ManuscriptDocument { ManuscriptId = excludedSectionId }),
                CreatedAt = placement.CreatedAt, UpdatedAt = placement.UpdatedAt,
            }, null);
        }
        var sectionId = Guid.NewGuid();
        var scene = CompositionService.CreatePageScene(edition);
        return ConvertPlacement(sectionId, edition.ProjectId, edition.Id,
            placement.CorePlacementId is Guid coreId ? coreMap.GetValueOrDefault(coreId) : null,
            placement.AssetId, placement.Caption, placement.PresentationJson, placement.AltText, placement.Decorative,
            placement.Language, placement.AccessibilityRole, placement.TargetKind, placement.TargetId,
            placement.PlacementKind, placement.SortOrder, scene, placement.CreatedAt, placement.UpdatedAt);
    }

    private static ConvertedPlacement ConvertPlacement(
        Guid sectionId,
        Guid projectId,
        Guid? editionId,
        Guid? coreSectionId,
        Guid imageId,
        string caption,
        string presentationJson,
        string altText,
        bool decorative,
        string language,
        FigureAccessibilityRole accessibilityRole,
        PublishOutlineTargetKind targetKind,
        Guid targetId,
        PublicationImagePlacementKind placementKind,
        int sortOrder,
        CompositionScene scene,
        DateTime createdAt,
        DateTime updatedAt)
    {
        var compositionId = Guid.NewGuid();
        var variantId = Guid.NewGuid();
        var imageObjectId = Guid.NewGuid();
        var captionBlockId = $"caption-{Guid.NewGuid():N}";
        var presentation = JsonSerializer.Deserialize<FigurePresentation>(presentationJson, ManuscriptCodec.JsonOptions) ?? new FigurePresentation();
        var semantic = new ManuscriptDocument
        {
            ManuscriptId = compositionId,
            Content = string.IsNullOrWhiteSpace(caption) ? [] :
            [
                new ManuscriptBlock
                {
                    Id = captionBlockId, Type = ManuscriptBlockType.Paragraph,
                    StyleRole = ManuscriptStyleRoles.FigureCaption,
                    Content = [new ManuscriptInline { Text = caption }],
                },
            ],
        };
        var layer = scene.Layers.Single();
        var hasCaption = semantic.Content.Count != 0;
        var objects = new List<CompositionObject>
        {
            new()
            {
                Id = imageObjectId, LayerId = layer.Id, Kind = CompositionObjectKind.Image,
                Name = "Publication image", ImageId = imageId,
                Bounds = new CompositionBounds { WidthPercent = 100, HeightPercent = hasCaption ? 90 : 100 },
                ImageFit = presentation.Fit, CropXPercent = presentation.CropXPercent, CropYPercent = presentation.CropYPercent,
                AltText = decorative ? string.Empty : altText, Decorative = decorative,
                AccessibilityDecisionPending = !decorative && string.IsNullOrWhiteSpace(altText),
                Language = PublicationLanguage.Normalize(language),
                SemanticRole = decorative ? CompositionSemanticRole.Artifact : CompositionSemanticRole.Figure,
                ReadingOrder = decorative ? null : 1,
            },
        };
        if (hasCaption)
        {
            objects.Add(new CompositionObject
            {
                Id = Guid.NewGuid(), LayerId = layer.Id, Kind = CompositionObjectKind.Text, Name = "Caption",
                Bounds = new CompositionBounds { YPercent = 90, WidthPercent = 100, HeightPercent = 10 },
                ContentReferences = [new ManuscriptRangeReference(captionBlockId)],
                SemanticRole = CompositionSemanticRole.Caption, ReadingOrder = decorative ? 1 : 2,
                FontSizePoints = 10, TextAlignment = CompositionTextAlignment.Center,
            });
        }
        scene = scene with { Objects = objects };
        var composition = new PageComposition
        {
            Id = compositionId, ProjectId = projectId, PublicationSectionId = sectionId, EditionId = editionId,
            Name = "Publication image page", SemanticManuscriptJson = ManuscriptCodec.Serialize(semantic),
            ActiveAuthoringVariantId = variantId,
            Variants =
            [
                new PageCompositionVariant
                {
                    Id = variantId, CompositionId = compositionId,
                    GeometryKey = CompositionService.SceneGeometryKey(scene),
                    SceneJson = JsonSerializer.Serialize(scene, ManuscriptCodec.JsonOptions),
                },
            ],
            CreatedAt = createdAt, UpdatedAt = updatedAt,
        };
        var sectionDocument = new ManuscriptDocument
        {
            ManuscriptId = sectionId,
            Content =
            [
                new ManuscriptBlock
                {
                    Id = $"designed-page-{Guid.NewGuid():N}", Type = ManuscriptBlockType.DesignedPage,
                    StyleRole = ManuscriptStyleRoles.DesignedPage, PageCompositionId = compositionId,
                },
            ],
        };
        var section = new PublicationSection
        {
            Id = sectionId, ProjectId = projectId, EditionId = editionId, CoreSectionId = coreSectionId,
            Title = string.IsNullOrWhiteSpace(caption) ? "Image page" : caption,
            Kind = PublicationSectionKind.Custom, Anchor = Anchor(placementKind),
            TargetKind = targetKind, TargetId = targetId,
            ActId = targetKind == PublishOutlineTargetKind.Act ? targetId : null,
            ChapterId = targetKind == PublishOutlineTargetKind.Chapter ? targetId : null,
            InclusionMode = PublicationSectionInclusionMode.Included, LocalOrder = sortOrder,
            ManuscriptJson = ManuscriptCodec.Serialize(sectionDocument),
            PageCompositions = [composition], CreatedAt = createdAt, UpdatedAt = updatedAt,
        };
        return new ConvertedPlacement(section, composition);
    }

    private static async Task AddMissingSystemSectionsAsync(AppDbContext db, Guid projectId, CancellationToken cancellationToken)
    {
        var existing = (await db.PublicationSections.Where(item => item.ProjectId == projectId && item.EditionId == null)
            .Select(item => item.SystemRole).ToListAsync(cancellationToken)).ToHashSet();
        foreach (var (role, kind, title, order, field) in new[]
        {
            (PublicationSectionSystemRole.Title, PublicationSectionKind.TitlePage, "Title page", 0, (PublicationBoundField?)PublicationBoundField.Title),
            (PublicationSectionSystemRole.Copyright, PublicationSectionKind.Copyright, "Copyright", 1, (PublicationBoundField?)PublicationBoundField.Copyright),
            (PublicationSectionSystemRole.Contents, PublicationSectionKind.Contents, "Contents", 2, (PublicationBoundField?)null),
        })
        {
            if (existing.Contains(role)) continue;
            var id = Guid.NewGuid();
            var content = role == PublicationSectionSystemRole.Copyright
                ? new List<ManuscriptBlock>
                {
                    BoundBlock(PublicationBoundField.Copyright, ManuscriptStyleRoles.Body),
                    BoundBlock(PublicationBoundField.Publisher, ManuscriptStyleRoles.Body),
                    BoundBlock(PublicationBoundField.Isbn, ManuscriptStyleRoles.Body),
                }
                : field is { } bound
                ? new List<ManuscriptBlock>
                {
                    new()
                    {
                        Id = $"field-{bound.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}",
                        Type = ManuscriptBlockType.Paragraph, StyleRole = bound == PublicationBoundField.Title
                            ? ManuscriptStyleRoles.ChapterHeading : ManuscriptStyleRoles.Body,
                        PublicationField = bound, Content = [new ManuscriptInline { Text = string.Empty }],
                    },
                }
                : [];
            db.PublicationSections.Add(new PublicationSection
            {
                Id = id, ProjectId = projectId, Title = title, Kind = kind, SystemRole = role,
                Anchor = PublicationSectionAnchor.Front, InclusionMode = PublicationSectionInclusionMode.Automatic,
                StartSide = RecommendedStartSide(role, kind),
                LocalOrder = order, ManuscriptJson = ManuscriptCodec.Serialize(new ManuscriptDocument { ManuscriptId = id, Content = content }),
            });
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static ManuscriptBlock BoundBlock(PublicationBoundField field, string styleRole) => new()
    {
        Id = $"field-{field.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}",
        Type = ManuscriptBlockType.Paragraph,
        StyleRole = styleRole,
        PublicationField = field,
        Content = [new ManuscriptInline { Text = string.Empty }],
    };

    private static PublicationSectionKind Kind(PublicationMatterKind kind) => Enum.Parse<PublicationSectionKind>(kind.ToString());
    private static int LegacyMatterOrder(PublicationBookMatter matter) => checked(10 + matter.SortOrder);
    private static int LegacyMatterOrder(PublicationMatter matter) => checked(10 + matter.SortOrder);
    private static PublicationSectionSystemRole Role(PublicationMatterKind kind) => kind switch
    {
        PublicationMatterKind.TitlePage => PublicationSectionSystemRole.Title,
        PublicationMatterKind.Copyright => PublicationSectionSystemRole.Copyright,
        PublicationMatterKind.Contents => PublicationSectionSystemRole.Contents,
        _ => PublicationSectionSystemRole.None,
    };
    private static PublicationSectionStartSide RecommendedStartSide(
        PublicationSectionSystemRole role,
        PublicationSectionKind kind) => role switch
    {
        PublicationSectionSystemRole.Title or PublicationSectionSystemRole.Contents => PublicationSectionStartSide.Recto,
        PublicationSectionSystemRole.Copyright => PublicationSectionStartSide.Verso,
        _ when kind is PublicationSectionKind.Dedication or PublicationSectionKind.AboutAuthor or PublicationSectionKind.References
            => PublicationSectionStartSide.Recto,
        _ => PublicationSectionStartSide.Next,
    };
    private static PublicationSectionAnchor Anchor(PublicationImagePlacementKind kind) => kind switch
    {
        PublicationImagePlacementKind.BeforeAct => PublicationSectionAnchor.BeforeAct,
        PublicationImagePlacementKind.AfterAct => PublicationSectionAnchor.AfterAct,
        PublicationImagePlacementKind.BeforeChapter or PublicationImagePlacementKind.ChapterOpening => PublicationSectionAnchor.BeforeChapter,
        PublicationImagePlacementKind.ChapterEnding or PublicationImagePlacementKind.AfterChapter => PublicationSectionAnchor.AfterChapter,
        _ => PublicationSectionAnchor.Back,
    };
    private static string RemapDocument(string json, Guid sourceId, long revision, out Guid id)
    {
        id = Guid.NewGuid();
        return RemapDocument(json, sourceId, revision, id);
    }
    private static string RemapDocument(string json, Guid sourceId, long revision, Guid id)
    {
        using var root = JsonDocument.Parse(json);
        var schemaVersion = root.RootElement.GetProperty("schemaVersion").GetInt32();
        var storedId = root.RootElement.TryGetProperty("manuscriptId", out var manuscriptIdElement)
            && manuscriptIdElement.TryGetGuid(out var manuscriptId)
                ? manuscriptId
                : sourceId;
        var storedRevision = root.RootElement.TryGetProperty("revision", out var revisionElement)
            && revisionElement.TryGetInt64(out var documentRevision)
                ? documentRevision
                : revision;
        json = schemaVersion switch
        {
            1 => ManuscriptSchemaUpgrade.UpgradeV1DocumentJson(json, storedId, storedRevision),
            2 => ManuscriptSchemaUpgrade.UpgradeV2DocumentJson(json, storedId, storedRevision),
            3 => ManuscriptSchemaUpgrade.UpgradeV3DocumentJson(json, storedId, storedRevision),
            ManuscriptDocument.CurrentSchemaVersion => json,
            _ => throw new InvalidDataException($"Unsupported publication-section manuscript schema version {schemaVersion}."),
        };
        var document = ManuscriptCodec.Deserialize(json);
        return ManuscriptCodec.Serialize(document with { ManuscriptId = id, Revision = revision });
    }
    private sealed record ConvertedPlacement(PublicationSection Section, PageComposition? Composition);
}
