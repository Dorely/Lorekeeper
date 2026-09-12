using System.Security.Cryptography;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Composition;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

namespace Lorekeeper.Manuscripts;

public interface IVisualCompositionMigrationService
{
    Task ApplyPendingAsync(AppDbContext db, CancellationToken cancellationToken = default);
    Task ApplyFinalSchemaAsync(AppDbContext db, CancellationToken cancellationToken = default);
}

public sealed class VisualCompositionMigrationService(
    IDatabaseMigrationRecoveryService recovery,
    ILogger<VisualCompositionMigrationService> logger) : IVisualCompositionMigrationService
{
    public const string MigrationName = "unified-composition-v3";
    public const string GeometryPolicyMigrationName = "composition-exact-geometry-v2";
    public const string AccessibilityDecisionMigrationName = "composition-accessibility-decisions-v1";
    public const string AdditiveMigrationId = "20260801192206_AddPageCompositionsV16";
    public const string CleanupMigrationId = "20260801205441_RemoveLegacyChapterVisualsV17";
    public const string FinalMigrationId = "20260802002923_CompleteCompositionContractsV18";
    public const int SourceVersion = 2;
    public const int TargetVersion = 3;
    private static readonly IReadOnlySet<string> EmptyColumns = new HashSet<string>(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> ProtectedTables =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["Projects"] = EmptyColumns,
            ["Acts"] = EmptyColumns,
            ["Chapters"] = Columns("ManuscriptJson", "ManuscriptRevision", "UpdatedAt"),
            ["PublicationEditions"] = EmptyColumns,
            ["PublicationEditionOutlineItems"] = EmptyColumns,
            ["PublicationMatter"] = EmptyColumns,
            ["PublicationEditionStyleMappings"] = EmptyColumns,
            ["PublicationImagePlacements"] = EmptyColumns,
            ["PublicationEditionAuditEntries"] = EmptyColumns,
            ["PublicationArtifacts"] = Columns("IsLegacy"),
            ["PublicationRenderJobs"] = Columns("IsLegacy"),
            ["PublicationPageMapEntries"] = EmptyColumns,
            ["PublicationCoverDesigns"] = Columns("CompositionSceneJson", "BarcodeMode"),
            ["PublishAssets"] = EmptyColumns,
            ["ProjectFontFamilies"] = EmptyColumns,
            ["ProjectFontFaces"] = EmptyColumns,
        };

    public async Task ApplyPendingAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        if (!await HasColumnAsync(db, "Chapters", "VisualMode", cancellationToken))
        {
            await ApplyGeometryPolicyMigrationAsync(db, cancellationToken);
            if ((await db.Database.GetAppliedMigrationsAsync(cancellationToken)).Contains(FinalMigrationId, StringComparer.Ordinal))
                await ApplyAccessibilityDecisionMigrationAsync(db, cancellationToken);
            return;
        }
        if (await db.ManuscriptMigrationJournals.AsNoTracking().AnyAsync(
            item => item.MigrationName == MigrationName && item.Status == ManuscriptMigrationStatus.Completed,
            cancellationToken))
        {
            await ApplyGeometryPolicyMigrationAsync(db, cancellationToken);
            return;
        }

        var backupPath = await recovery.CreateBackupAsync("manuscripts", "pre-composition-v3", cancellationToken);
        var journal = new ManuscriptMigrationJournal
        {
            MigrationName = MigrationName,
            SourceSchemaVersion = SourceVersion,
            TargetSchemaVersion = TargetVersion,
            Phase = ManuscriptMigrationPhase.Transform,
            Status = ManuscriptMigrationStatus.Running,
            BackupPath = backupPath,
        };
        db.ManuscriptMigrationJournals.Add(journal);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var protectedStateHash = await HashProtectedStateAsync(db, cancellationToken);
            var legacyRows = await ReadLegacyChaptersAsync(db, cancellationToken);
            var chapters = await db.Chapters.OrderBy(item => item.ProjectId).ThenBy(item => item.Order)
                .ToDictionaryAsync(item => item.Id, cancellationToken);
            var assets = await db.PublishAssets.AsNoTracking().ToDictionaryAsync(item => item.Id, cancellationToken);
            var legacyEditions = await ReadMigrationEditionsAsync(db, cancellationToken);
            var editionsByProject = legacyEditions
                .GroupBy(item => item.ProjectId)
                .ToDictionary(group => group.Key, group => group.ToList());
            var editionsById = legacyEditions.ToDictionary(item => item.Id);
            var warnings = new List<string>();
            var sourceHashes = new List<string>();
            var targetHashes = new List<string>();
            var sourceVisualHashes = new List<string>();
            var targetVisualHashes = new List<string>();
            var compositionCount = 0;
            foreach (var legacy in legacyRows)
            {
                var chapter = chapters[legacy.Id];
                var sourceDocument = ReadCurrent(chapter);
                sourceHashes.Add(HashSemantic(sourceDocument));
                ManuscriptDocument targetDocument;
                switch (legacy.VisualMode)
                {
                    case ChapterVisualMode.IllustratedProse:
                        targetDocument = ConvertIllustrated(
                            sourceDocument,
                            legacy.IllustrationLayoutJson,
                            assets,
                            warnings);
                        chapter.ManuscriptJson = ManuscriptCodec.Serialize(targetDocument);
                        break;
                    case ChapterVisualMode.PicturePage:
                        var projectEditions = editionsByProject.GetValueOrDefault(legacy.ProjectId) ?? [];
                        var expectedComposition = ConvertPicturePage(legacy, sourceDocument, projectEditions, assets, []);
                        var sourceVisualHash = HashCompositionVisual(expectedComposition);
                        var composition = ConvertPicturePage(
                            legacy,
                            sourceDocument,
                            projectEditions,
                            assets,
                            warnings);
                        sourceVisualHashes.Add(sourceVisualHash);
                        targetVisualHashes.Add(HashCompositionVisual(composition));
                        var seed = composition.Variants.Single(item =>
                            string.Equals(item.GeometryKey, "migration-seed", StringComparison.Ordinal));
                        composition.Variants.Remove(seed);
                        db.CompositionMutationStages.Add(CreateCompositionSeed(composition, seed.SceneJson));
                        await InsertPreAuthoringCompositionAsync(db, composition, cancellationToken);
                        compositionCount++;
                        targetDocument = ManuscriptCodec.Deserialize(
                            composition.SemanticManuscriptJson,
                            composition.Id,
                            composition.Revision);
                        chapter.ManuscriptRevision = checked(chapter.ManuscriptRevision + 1);
                        chapter.ManuscriptJson = ManuscriptCodec.Serialize(new ManuscriptDocument
                        {
                            ManuscriptId = chapter.Id,
                            Revision = chapter.ManuscriptRevision,
                            Content =
                            [
                                new ManuscriptBlock
                                {
                                    Id = DeterministicId(chapter.Id, "designed-page"),
                                    Type = ManuscriptBlockType.DesignedPage,
                                    StyleRole = ManuscriptStyleRoles.DesignedPage,
                                    PageCompositionId = composition.Id,
                                },
                            ],
                        });
                        break;
                    case ChapterVisualMode.Prose:
                        targetDocument = sourceDocument;
                        chapter.ManuscriptJson = ManuscriptCodec.Serialize(targetDocument);
                        break;
                    default:
                        throw new InvalidDataException($"Chapter {chapter.Id:N} has an unsupported visual mode.");
                }
                targetHashes.Add(HashPreservedSemantic(sourceDocument, targetDocument));
                chapter.UpdatedAt = DateTime.UtcNow;
            }

            foreach (var cover in await ReadMigrationCoversAsync(db, cancellationToken))
            {
                cover.Edition = editionsById[cover.EditionId];
                if (cover.Edition.Format != PublicationEditionFormat.Paperback)
                    cover.BarcodeMode = PublicationBarcodeMode.None;
                if (string.IsNullOrWhiteSpace(cover.CompositionSceneJson))
                {
                    var pageCount = await db.PublicationArtifacts.AsNoTracking()
                        .Where(item => item.EditionId == cover.EditionId && item.Kind == PublicationArtifactKind.InteriorPdf)
                        .OrderByDescending(item => item.CreatedAt)
                        .Select(item => item.PageCount)
                        .FirstOrDefaultAsync(cancellationToken) ?? 0;
                    cover.CompositionSceneJson = JsonSerializer.Serialize(
                        CoverCompositionFactory.Create(cover.Edition, cover, pageCount),
                        ManuscriptCodec.JsonOptions);
                }
                await db.PublicationCoverDesigns.Where(item => item.Id == cover.Id).ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(item => item.BarcodeMode, cover.BarcodeMode)
                        .SetProperty(item => item.CompositionSceneJson, cover.CompositionSceneJson),
                    cancellationToken);
            }
            await NormalizeLegacyPendingChapterChangesAsync(db, cancellationToken);
            await db.PublicationArtifacts.ExecuteUpdateAsync(
                setters => setters.SetProperty(item => item.IsLegacy, true),
                cancellationToken);
            await db.PublicationRenderJobs.ExecuteUpdateAsync(
                setters => setters.SetProperty(item => item.IsLegacy, true),
                cancellationToken);

            journal.ChapterCount = chapters.Count;
            journal.SourceHash = AggregateHash(sourceHashes);
            journal.TargetHash = AggregateHash(targetHashes);
            if (!string.Equals(journal.SourceHash, journal.TargetHash, StringComparison.Ordinal))
                throw new InvalidDataException("Unified composition migration changed semantic manuscript content.");
            var sourceVisualAggregate = AggregateHash(sourceVisualHashes);
            var targetVisualAggregate = AggregateHash(targetVisualHashes);
            if (!string.Equals(sourceVisualAggregate, targetVisualAggregate, StringComparison.Ordinal))
                throw new InvalidDataException("Unified composition migration changed Picture Page visual or accessibility data.");
            journal.ValidationReportJson = JsonSerializer.Serialize(new
            {
                chapters = chapters.Count,
                compositions = compositionCount,
                sourceVisualHash = sourceVisualAggregate,
                targetVisualHash = targetVisualAggregate,
                warnings,
            });
            journal.Phase = ManuscriptMigrationPhase.Complete;
            journal.Status = ManuscriptMigrationStatus.Completed;
            journal.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await ValidateCompositionDocumentsAsync(db, cancellationToken);
            var migratedStateHash = await HashProtectedStateAsync(db, cancellationToken);
            if (!string.Equals(protectedStateHash, migratedStateHash, StringComparison.Ordinal))
                throw new InvalidDataException("Unified composition migration changed protected publication or project data.");
            await EnsureForeignKeysAsync(db, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await ApplyGeometryPolicyMigrationAsync(db, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unified manuscript composition migration failed.");
            await recovery.EnterRecoveryModeAsync(
                db,
                backupPath,
                MigrationName,
                SourceVersion,
                TargetVersion,
                exception,
                cancellationToken);
        }
    }

    public async Task ApplyFinalSchemaAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        if (applied.Contains(FinalMigrationId))
            return;

        var completedCutover = await db.ManuscriptMigrationJournals.AsNoTracking()
            .Where(item => item.MigrationName == MigrationName && item.Status == ManuscriptMigrationStatus.Completed)
            .OrderByDescending(item => item.CompletedAt)
            .FirstOrDefaultAsync(cancellationToken);
        var backupPath = completedCutover?.BackupPath;
        if (string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath))
            backupPath = await recovery.CreateBackupAsync("manuscripts", "pre-composition-contracts-v18", cancellationToken);

        try
        {
            await db.GetService<IMigrator>().MigrateAsync(FinalMigrationId, cancellationToken);
            await ApplyAccessibilityDecisionMigrationAsync(db, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Final unified composition schema migration failed.");
            await recovery.EnterRecoveryModeAsync(
                db,
                backupPath,
                MigrationName,
                SourceVersion,
                TargetVersion,
                exception,
                cancellationToken);
        }
    }

    private async Task ApplyAccessibilityDecisionMigrationAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (await db.ManuscriptMigrationJournals.AsNoTracking().AnyAsync(
            item => item.MigrationName == AccessibilityDecisionMigrationName
                && item.Status == ManuscriptMigrationStatus.Completed,
            cancellationToken))
            return;
        var placementIds = await db.PublicationImagePlacements.AsNoTracking()
            .Where(item => !item.Decorative
                && item.AltText != string.Empty
                && item.Asset.AltText == string.Empty
                && item.AltText == item.Asset.FileName)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        if (placementIds.Count == 0)
        {
            db.ManuscriptMigrationJournals.Add(new ManuscriptMigrationJournal
            {
                MigrationName = AccessibilityDecisionMigrationName,
                SourceSchemaVersion = TargetVersion,
                TargetSchemaVersion = TargetVersion,
                Phase = ManuscriptMigrationPhase.Complete,
                Status = ManuscriptMigrationStatus.Completed,
                ValidationReportJson = "{\"placements\":0}",
                CompletedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(cancellationToken);
            return;
        }
        var backupPath = await recovery.CreateBackupAsync("manuscripts", "pre-composition-accessibility-decisions", cancellationToken);
        var journal = new ManuscriptMigrationJournal
        {
            MigrationName = AccessibilityDecisionMigrationName,
            SourceSchemaVersion = TargetVersion,
            TargetSchemaVersion = TargetVersion,
            Phase = ManuscriptMigrationPhase.Transform,
            Status = ManuscriptMigrationStatus.Running,
            BackupPath = backupPath,
        };
        db.ManuscriptMigrationJournals.Add(journal);
        await db.SaveChangesAsync(cancellationToken);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.PublicationImagePlacements
                .Where(item => placementIds.Contains(item.Id))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.AltText, string.Empty)
                    .SetProperty(item => item.UpdatedAt, DateTime.UtcNow), cancellationToken);
            journal.Phase = ManuscriptMigrationPhase.Complete;
            journal.Status = ManuscriptMigrationStatus.Completed;
            journal.ValidationReportJson = JsonSerializer.Serialize(new { placements = placementIds.Count });
            journal.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Composition accessibility-decision migration failed.");
            await recovery.EnterRecoveryModeAsync(
                db,
                backupPath,
                AccessibilityDecisionMigrationName,
                TargetVersion,
                TargetVersion,
                exception,
                cancellationToken);
        }
    }

    private static ManuscriptDocument ReadCurrent(Chapter chapter)
    {
        using var json = JsonDocument.Parse(chapter.ManuscriptJson);
        var version = json.RootElement.GetProperty("schemaVersion").GetInt32();
        return version switch
        {
            ManuscriptDocument.CurrentSchemaVersion => ManuscriptCodec.Deserialize(
                chapter.ManuscriptJson,
                chapter.Id,
                chapter.ManuscriptRevision),
            3 => ManuscriptCodec.Deserialize(
                ManuscriptSchemaUpgrade.UpgradeV3DocumentJson(
                    chapter.ManuscriptJson,
                    chapter.Id,
                    chapter.ManuscriptRevision),
                chapter.Id,
                chapter.ManuscriptRevision),
            2 => ManuscriptCodec.Deserialize(
                ManuscriptSchemaUpgrade.UpgradeV2DocumentJson(
                    chapter.ManuscriptJson,
                    chapter.Id,
                    chapter.ManuscriptRevision),
                chapter.Id,
                chapter.ManuscriptRevision),
            _ => throw new InvalidDataException($"Chapter {chapter.Id:N} is not ready for schema-v3 migration."),
        };
    }

    private static ManuscriptDocument ConvertIllustrated(
        ManuscriptDocument source,
        string layoutJson,
        IReadOnlyDictionary<Guid, PublishAsset> assets,
        List<string> warnings)
    {
        var layout = string.IsNullOrWhiteSpace(layoutJson)
            ? new IllustratedProseLayout([])
            : JsonSerializer.Deserialize<IllustratedProseLayout>(layoutJson, ManuscriptCodec.JsonOptions)
                ?? new IllustratedProseLayout([]);
        var content = source.Content.Select(block => block with { Content = block.Content.ToList() }).ToList();
        foreach (var image in layout.Images.OrderBy(item => item.SortOrder).ThenBy(item => item.Id))
        {
            var anchor = content.FindIndex(block => string.Equals(block.Id, image.BlockId, StringComparison.OrdinalIgnoreCase));
            if (anchor < 0)
            {
                anchor = content.Count;
                warnings.Add($"Illustration {image.Id:N} had no current anchor and was placed at the manuscript end.");
            }
            else if (image.AnchorPosition == ChapterImageAnchorPosition.AfterParagraph)
            {
                anchor++;
            }
            var fallback = assets.GetValueOrDefault(image.ImageId);
            var alt = FirstTextOrEmpty(image.AltTextOverride, fallback?.AltText);
            content.Insert(anchor, new ManuscriptBlock
            {
                Id = image.Id.ToString("N"),
                Type = ManuscriptBlockType.Figure,
                StyleRole = ManuscriptStyleRoles.FigureCaption,
                ImageId = image.ImageId,
                AltText = alt,
                AccessibilityRole = FigureAccessibilityRole.Illustration,
                FigurePresentation = new FigurePresentation
                {
                    Placement = image.Alignment == ChapterImageAlignment.Center
                        ? FigurePlacementIntent.Centered
                        : FigurePlacementIntent.Float,
                    WidthPercent = Math.Clamp(image.WidthPercent, 1, 100),
                    Alignment = image.Alignment switch
                    {
                        ChapterImageAlignment.Left => FigureAlignment.Start,
                        ChapterImageAlignment.Right => FigureAlignment.End,
                        _ => FigureAlignment.Center,
                    },
                    TextWrap = image.Alignment switch
                    {
                        ChapterImageAlignment.Left => FigureTextWrap.End,
                        ChapterImageAlignment.Right => FigureTextWrap.Start,
                        _ => FigureTextWrap.None,
                    },
                    StartOnNewPage = image.StartOnNewPage,
                },
                Content = string.IsNullOrWhiteSpace(image.Caption)
                    ? []
                    : [new ManuscriptInline { Text = image.Caption.Trim() }],
            });
        }
        return source with { Content = content };
    }

    private static PageComposition ConvertPicturePage(
        LegacyChapterVisualRow chapter,
        ManuscriptDocument source,
        IReadOnlyList<PublicationEdition> editions,
        IReadOnlyDictionary<Guid, PublishAsset> assets,
        List<string> warnings)
    {
        var layout = string.IsNullOrWhiteSpace(chapter.PageLayoutJson)
            ? new PicturePageLayout([], [])
            : JsonSerializer.Deserialize<PicturePageLayout>(chapter.PageLayoutJson, ManuscriptCodec.JsonOptions)
                ?? new PicturePageLayout([], []);
        var compositionId = Guid.NewGuid();
        var semantic = source with { ManuscriptId = compositionId };
        var layerId = Guid.NewGuid();
        var objects = new List<CompositionObject>();
        var nextImageReadingOrder = layout.TextElements.Select(item => item.ReadingOrder).DefaultIfEmpty(0).Max() + 1;
        foreach (var image in layout.Images)
        {
            var altText = FirstTextOrEmpty(
                image.AltTextOverride,
                assets.GetValueOrDefault(image.ImageId)?.AltText);
            var accessibilityPending = string.IsNullOrWhiteSpace(altText);
            objects.Add(new CompositionObject
            {
                Id = image.Id,
                LayerId = layerId,
                Kind = CompositionObjectKind.Image,
                Bounds = Bounds(image.XPercent, image.YPercent, image.WidthPercent, image.HeightPercent),
                ImageId = image.ImageId,
                ImageFit = image.Fit switch
                {
                    ChapterImageFit.Contain => FigureImageFit.Contain,
                    ChapterImageFit.Fill => FigureImageFit.Contain,
                    _ => FigureImageFit.Cover,
                },
                Opacity = Math.Clamp(image.Opacity, 0, 1),
                ZIndex = image.ZIndex,
                AltText = altText,
                Decorative = false,
                AccessibilityDecisionPending = accessibilityPending,
                SemanticRole = CompositionSemanticRole.Figure,
                ReadingOrder = nextImageReadingOrder++,
            });
        }
        objects.AddRange(layout.TextElements
            .Where(text => text.ContentReferences is { Count: > 0 })
            .Select(text => new CompositionObject
        {
            Id = text.Id,
            LayerId = layerId,
            Kind = CompositionObjectKind.Text,
            Bounds = Bounds(text.XPercent, text.YPercent, text.WidthPercent, text.HeightPercent),
            ContentReferences = text.ContentReferences ?? [],
            FontFamilyKey = text.FontFamilyKey,
            FontWeight = text.FontWeight,
            Italic = text.Italic,
            FontSizePoints = text.FontSizePoints,
            LineHeight = text.LineHeight,
            LetterSpacingEm = text.LetterSpacingEm,
            FillColor = text.Color,
            BackgroundColor = text.BackgroundColor,
            BackgroundOpacity = text.BackgroundOpacity,
            TextAlignment = text.TextAlign switch
            {
                PicturePageTextAlign.Center => CompositionTextAlignment.Center,
                PicturePageTextAlign.Right => CompositionTextAlignment.End,
                _ => CompositionTextAlignment.Start,
            },
            VerticalAlignment = text.VerticalAlign switch
            {
                ChapterTextVerticalAlign.Middle => CompositionVerticalAlignment.Center,
                ChapterTextVerticalAlign.Bottom => CompositionVerticalAlignment.Bottom,
                _ => CompositionVerticalAlignment.Top,
            },
            TextShadow = text.Shadow switch
            {
                PicturePageTextShadow.Soft => CompositionTextShadow.Soft,
                PicturePageTextShadow.Strong => CompositionTextShadow.Strong,
                PicturePageTextShadow.Glow => CompositionTextShadow.Glow,
                _ => CompositionTextShadow.None,
            },
            ZIndex = text.ZIndex,
            ReadingOrder = text.ReadingOrder,
            SemanticRole = text.Role switch
            {
                PicturePageTextRole.Title => CompositionSemanticRole.Heading1,
                PicturePageTextRole.Heading => CompositionSemanticRole.Heading2,
                PicturePageTextRole.Caption => CompositionSemanticRole.Caption,
                PicturePageTextRole.Credit => CompositionSemanticRole.Credit,
                _ => CompositionSemanticRole.Paragraph,
            },
        }));
        var referenced = objects.SelectMany(item => item.ContentReferences).Select(item => item.BlockId)
            .ToHashSet(StringComparer.Ordinal);
        var unplaced = source.Content.Count(block => !referenced.Contains(block.Id));
        if (unplaced > 0)
            warnings.Add($"Picture Page chapter {chapter.Id:N} retained {unplaced} unplaced semantic block(s).");
        var surfaceKind = chapter.PageLayoutKind switch
        {
            ChapterPageLayoutKind.SingleLandscape => CompositionSurfaceKind.IndependentPage,
            ChapterPageLayoutKind.DoublePortrait or ChapterPageLayoutKind.DoubleLandscape =>
                CompositionSurfaceKind.FacingSpread,
            _ => CompositionSurfaceKind.SinglePage,
        };
        var (width, height) = LegacyPicturePageGeometry.SurfacePoints(chapter.PageLayoutKind);
        var legacyScene = new CompositionScene
        {
            Surface = new CompositionSurface { Kind = surfaceKind, WidthPoints = width, HeightPoints = height },
            Layers = [new CompositionLayer(layerId, "Content", 0)],
            Objects = objects,
        };
        return new PageComposition
        {
            Id = compositionId,
            ProjectId = chapter.ProjectId,
            ChapterId = chapter.Id,
            Name = chapter.Title,
            SemanticManuscriptJson = ManuscriptCodec.Serialize(semantic),
            Revision = source.Revision,
            Variants =
            [
                new PageCompositionVariant
                {
                    GeometryKey = "migration-seed",
                    SceneJson = JsonSerializer.Serialize(legacyScene, ManuscriptCodec.JsonOptions),
                },
                .. editions.Select(edition => new
                    {
                        Edition = edition,
                        Scene = AdaptLegacySceneForEdition(legacyScene, chapter.PageLayoutKind, edition),
                    })
                    .Select(item => new
                    {
                        Key = CompositionService.GeometryKey(item.Edition, item.Scene),
                        item.Scene,
                    })
                    .GroupBy(item => item.Key, StringComparer.Ordinal)
                    .Select(group => new PageCompositionVariant
                    {
                        GeometryKey = group.Key,
                        SceneJson = JsonSerializer.Serialize(group.First().Scene, ManuscriptCodec.JsonOptions),
                    }),
            ],
        };
    }

    private static CompositionMutationStage CreateCompositionSeed(PageComposition composition, string sceneJson) => new()
    {
        ProjectId = composition.ProjectId,
        ConversationId = Guid.Empty,
        TargetKind = "page-composition-seed",
        TargetId = composition.Id,
        ExpectedRevision = composition.Revision,
        OperationsJson = sceneJson,
        PayloadSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sceneJson))),
        ExpiresAt = DateTime.MaxValue,
    };

    private static async Task InsertPreAuthoringCompositionAsync(
        AppDbContext db,
        PageComposition composition,
        CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO PageCompositions
                (Id, ProjectId, ChapterId, Name, SemanticManuscriptJson, Revision, CreatedAt, UpdatedAt)
            VALUES
                ({composition.Id}, {composition.ProjectId}, {composition.ChapterId}, {composition.Name},
                 {composition.SemanticManuscriptJson}, {composition.Revision}, {composition.CreatedAt}, {composition.UpdatedAt});
            """,
            cancellationToken);
        foreach (var variant in composition.Variants)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO PageCompositionVariants
                    (Id, CompositionId, GeometryKey, SceneJson, Revision, CreatedAt, UpdatedAt)
                VALUES
                    ({variant.Id}, {composition.Id}, {variant.GeometryKey}, {variant.SceneJson},
                     {variant.Revision}, {variant.CreatedAt}, {variant.UpdatedAt});
                """,
                cancellationToken);
        }
    }

    private async Task ApplyGeometryPolicyMigrationAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (await db.ManuscriptMigrationJournals.AsNoTracking().AnyAsync(
            item => item.MigrationName == GeometryPolicyMigrationName
                && item.Status == ManuscriptMigrationStatus.Completed,
            cancellationToken))
        {
            return;
        }

        var editions = await ReadMigrationEditionsAsync(db, cancellationToken);
        var compositionProjects = await db.PageCompositions.AsNoTracking()
            .Select(item => new { item.Id, item.ProjectId })
            .ToDictionaryAsync(item => item.Id, item => item.ProjectId, cancellationToken);
        var variants = await db.PageCompositionVariants.ToListAsync(cancellationToken);
        if (variants.Count == 0)
        {
            db.ManuscriptMigrationJournals.Add(new ManuscriptMigrationJournal
            {
                MigrationName = GeometryPolicyMigrationName,
                SourceSchemaVersion = TargetVersion,
                TargetSchemaVersion = TargetVersion,
                Phase = ManuscriptMigrationPhase.Complete,
                Status = ManuscriptMigrationStatus.Completed,
                ValidationReportJson = "{\"variants\":0}",
                CompletedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var backupPath = await recovery.CreateBackupAsync("manuscripts", "pre-composition-geometry-policy", cancellationToken);
        var journal = new ManuscriptMigrationJournal
        {
            MigrationName = GeometryPolicyMigrationName,
            SourceSchemaVersion = TargetVersion,
            TargetSchemaVersion = TargetVersion,
            Phase = ManuscriptMigrationPhase.Transform,
            Status = ManuscriptMigrationStatus.Running,
            BackupPath = backupPath,
        };
        db.ManuscriptMigrationJournals.Add(journal);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var changed = 0;
            foreach (var source in variants.ToList())
            {
                var sourceScene = JsonSerializer.Deserialize<CompositionScene>(source.SceneJson, ManuscriptCodec.JsonOptions)
                    ?? throw new InvalidDataException($"Composition variant {source.Id:N} has no scene.");
                var projectEditions = editions.Where(item => item.ProjectId == compositionProjects[source.CompositionId]).ToList();
                var matched = projectEditions.Where(edition =>
                    string.Equals(source.GeometryKey, CompositionService.GeometryKey(edition, sourceScene), StringComparison.Ordinal)
                    || string.Equals(source.GeometryKey, CompositionService.LegacyEditionOnlyGeometryKey(edition), StringComparison.Ordinal)
                    || string.Equals(source.GeometryKey, CompositionService.LegacyGeometryKey(edition), StringComparison.Ordinal)).ToList();
                var candidates = matched.Count > 0
                    ? matched
                    : projectEditions.Where(edition => CanNormalizeForEdition(sourceScene, edition)).ToList();
                var destinations = new Dictionary<string, CompositionScene>(StringComparer.Ordinal);
                foreach (var edition in candidates)
                {
                    var normalized = NormalizeForEdition(sourceScene, edition);
                    CompositionService.ValidateVariantGeometry(edition, normalized);
                    destinations.TryAdd(CompositionService.GeometryKey(edition, normalized), normalized);
                }
                if (destinations.Count == 0)
                    throw new InvalidDataException($"Composition variant {source.Id:N} does not match any edition geometry in its project.");

                var sourceWasReused = false;
                foreach (var destination in destinations)
                {
                    var sceneJson = JsonSerializer.Serialize(destination.Value, ManuscriptCodec.JsonOptions);
                    var existing = variants.FirstOrDefault(item => item.CompositionId == source.CompositionId
                        && item.Id != source.Id
                        && string.Equals(item.GeometryKey, destination.Key, StringComparison.Ordinal));
                    if (existing is not null)
                    {
                        var existingScene = JsonSerializer.Deserialize<CompositionScene>(existing.SceneJson, ManuscriptCodec.JsonOptions);
                        if (existingScene is null
                            || !string.Equals(
                                JsonSerializer.Serialize(existingScene, ManuscriptCodec.JsonOptions),
                                sceneJson,
                                StringComparison.Ordinal))
                        {
                            throw new InvalidDataException(
                                $"Composition {source.CompositionId:N} contains conflicting layouts for exact geometry {destination.Key}.");
                        }
                        continue;
                    }
                    if (!sourceWasReused)
                    {
                        source.GeometryKey = destination.Key;
                        source.SceneJson = sceneJson;
                        source.UpdatedAt = DateTime.UtcNow;
                        sourceWasReused = true;
                    }
                    else
                    {
                        var clone = new PageCompositionVariant
                        {
                            CompositionId = source.CompositionId,
                            GeometryKey = destination.Key,
                            SceneJson = sceneJson,
                            Revision = source.Revision,
                            CreatedAt = source.CreatedAt,
                            UpdatedAt = source.UpdatedAt,
                        };
                        db.PageCompositionVariants.Add(clone);
                        variants.Add(clone);
                    }
                    changed++;
                }
                if (!sourceWasReused)
                    db.PageCompositionVariants.Remove(source);
            }
            journal.Phase = ManuscriptMigrationPhase.Complete;
            journal.Status = ManuscriptMigrationStatus.Completed;
            journal.ValidationReportJson = JsonSerializer.Serialize(new { variants = changed });
            journal.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await ValidateCompositionDocumentsAsync(db, cancellationToken);
            await EnsureForeignKeysAsync(db, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Composition geometry-policy migration failed.");
            await recovery.EnterRecoveryModeAsync(
                db,
                backupPath,
                GeometryPolicyMigrationName,
                TargetVersion,
                TargetVersion,
                exception,
                cancellationToken);
        }
    }

    private static bool CanNormalizeForEdition(CompositionScene scene, PublicationEdition edition)
    {
        try
        {
            _ = NormalizeForEdition(scene, edition);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static CompositionScene NormalizeForEdition(CompositionScene scene, PublicationEdition edition)
    {
        if (scene.Surface.Kind == CompositionSurfaceKind.IndependentPage)
        {
            if (edition.Format != PublicationEditionFormat.DigitalPdf || !edition.AllowDesignedPageOverrides)
                throw new InvalidDataException("Independent composition geometry is incompatible with this edition.");
            return scene with
            {
                Surface = scene.Surface with
                {
                    OutputPageMode = CompositionOutputPageMode.SingleSurface,
                    AllowIndependentPdfPage = true,
                },
            };
        }
        var expectedWidth = edition.PageWidthInches * 72
            * (scene.Surface.Kind == CompositionSurfaceKind.FacingSpread ? 2 : 1);
        var expectedHeight = edition.PageHeightInches * 72;
        if (Math.Abs(scene.Surface.WidthPoints - expectedWidth) > .01
            || Math.Abs(scene.Surface.HeightPoints - expectedHeight) > .01)
            throw new InvalidDataException("Composition dimensions do not match this edition.");
        return scene with
        {
            Surface = scene.Surface with
            {
                OutputPageMode = CompositionOutputPageMode.EditionLeaves,
                AllowIndependentPdfPage = false,
            },
        };
    }

    private static CompositionScene AdaptSceneToStandardLeaf(
        CompositionScene source,
        PublicationEdition edition)
    {
        var targetWidth = edition.PageWidthInches * 72;
        var targetHeight = edition.PageHeightInches * 72;
        var scale = Math.Min(targetWidth / source.Surface.WidthPoints, targetHeight / source.Surface.HeightPoints);
        var contentWidth = source.Surface.WidthPoints * scale;
        var contentHeight = source.Surface.HeightPoints * scale;
        var offsetX = (targetWidth - contentWidth) / 2;
        var offsetY = (targetHeight - contentHeight) / 2;
        return source with
        {
            Surface = source.Surface with
            {
                Kind = CompositionSurfaceKind.SinglePage,
                OutputPageMode = CompositionOutputPageMode.EditionLeaves,
                WidthPoints = targetWidth,
                HeightPoints = targetHeight,
                BleedPoints = edition.Bleed ? 9 : 0,
                SafeInsetPoints = edition.PageMarginInches * 72,
                AllowIndependentPdfPage = false,
            },
            Objects = source.Objects.Select(item => item with
            {
                Bounds = new CompositionBounds
                {
                    XPercent = (offsetX + item.Bounds.XPercent / 100 * contentWidth) / targetWidth * 100,
                    YPercent = (offsetY + item.Bounds.YPercent / 100 * contentHeight) / targetHeight * 100,
                    WidthPercent = item.Bounds.WidthPercent / 100 * contentWidth / targetWidth * 100,
                    HeightPercent = item.Bounds.HeightPercent / 100 * contentHeight / targetHeight * 100,
                },
            }).ToList(),
        };
    }

    internal static CompositionScene AdaptLegacySceneForEdition(
        CompositionScene source,
        ChapterPageLayoutKind layoutKind,
        PublicationEdition edition)
    {
        if (layoutKind == ChapterPageLayoutKind.SingleLandscape
            && edition.Format == PublicationEditionFormat.DigitalPdf
            && edition.AllowDesignedPageOverrides)
        {
            return source with
            {
                Surface = source.Surface with
                {
                    Kind = CompositionSurfaceKind.IndependentPage,
                    OutputPageMode = CompositionOutputPageMode.SingleSurface,
                    AllowIndependentPdfPage = true,
                },
            };
        }

        var facing = layoutKind is ChapterPageLayoutKind.DoublePortrait or ChapterPageLayoutKind.DoubleLandscape;
        var targetWidth = edition.PageWidthInches * 72 * (facing ? 2 : 1);
        var targetHeight = edition.PageHeightInches * 72;
        var scale = Math.Min(targetWidth / source.Surface.WidthPoints, targetHeight / source.Surface.HeightPoints);
        var contentWidth = source.Surface.WidthPoints * scale;
        var contentHeight = source.Surface.HeightPoints * scale;
        var offsetX = (targetWidth - contentWidth) / 2;
        var offsetY = (targetHeight - contentHeight) / 2;
        CompositionBounds Map(CompositionBounds bounds) => new()
        {
            XPercent = (offsetX + bounds.XPercent / 100 * contentWidth) / targetWidth * 100,
            YPercent = (offsetY + bounds.YPercent / 100 * contentHeight) / targetHeight * 100,
            WidthPercent = bounds.WidthPercent / 100 * contentWidth / targetWidth * 100,
            HeightPercent = bounds.HeightPercent / 100 * contentHeight / targetHeight * 100,
        };
        return source with
        {
            Surface = source.Surface with
            {
                Kind = facing ? CompositionSurfaceKind.FacingSpread : CompositionSurfaceKind.SinglePage,
                OutputPageMode = CompositionOutputPageMode.EditionLeaves,
                WidthPoints = targetWidth,
                HeightPoints = targetHeight,
                BleedPoints = edition.Bleed ? 9 : 0,
                SafeInsetPoints = edition.PageMarginInches * 72,
                AllowIndependentPdfPage = false,
            },
            Objects = source.Objects.Select(item => item with { Bounds = Map(item.Bounds) }).ToList(),
        };
    }

    private static CompositionBounds Bounds(double x, double y, double width, double height) => new()
    {
        XPercent = x,
        YPercent = y,
        WidthPercent = width,
        HeightPercent = height,
    };

    private static string FirstTextOrEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string DeterministicId(Guid id, string suffix) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{id:N}:{suffix}")))[..32];

    private static string HashSemantic(ManuscriptDocument document) =>
        HashBlocks(document.Content);

    private static string HashPreservedSemantic(ManuscriptDocument source, ManuscriptDocument target)
    {
        var targetById = target.Content.ToDictionary(block => block.Id, StringComparer.Ordinal);
        var preserved = new List<ManuscriptBlock>(source.Content.Count);
        foreach (var sourceBlock in source.Content)
        {
            if (!targetById.TryGetValue(sourceBlock.Id, out var targetBlock)
                || targetBlock.Type != sourceBlock.Type
                || !string.Equals(targetBlock.StyleRole, sourceBlock.StyleRole, StringComparison.Ordinal)
                || !string.Equals(ManuscriptCodec.Text(targetBlock), ManuscriptCodec.Text(sourceBlock), StringComparison.Ordinal))
                throw new InvalidDataException($"Semantic block {sourceBlock.Id} was not preserved by the composition migration.");
            preserved.Add(targetBlock);
        }
        return HashBlocks(preserved);
    }

    private static string HashBlocks(IEnumerable<ManuscriptBlock> blocks)
    {
        var canonical = string.Join("\n", blocks.Select(block =>
            $"{block.Id}|{block.Type}|{block.StyleRole}|{ManuscriptCodec.Text(block)}"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string HashCompositionVisual(PageComposition composition)
    {
        var canonical = new List<string>();
        foreach (var variant in composition.Variants.OrderBy(item => item.GeometryKey, StringComparer.Ordinal))
        {
            var scene = JsonSerializer.Deserialize<CompositionScene>(variant.SceneJson, ManuscriptCodec.JsonOptions)
                ?? throw new InvalidDataException("Migrated Picture Page scene is empty.");
            canonical.Add(string.Join('|', variant.GeometryKey, scene.Surface.Kind, scene.Surface.OutputPageMode,
                scene.Surface.WidthPoints, scene.Surface.HeightPoints, scene.Surface.BleedPoints,
                scene.Surface.SafeInsetPoints, scene.Surface.AllowIndependentPdfPage));
            canonical.AddRange(scene.Objects.OrderBy(item => item.Id).Select(item => string.Join('|',
                item.Id, item.Kind, item.Bounds.XPercent, item.Bounds.YPercent, item.Bounds.WidthPercent,
                item.Bounds.HeightPercent, item.RotationDegrees, item.Opacity, item.ZIndex, item.Visible,
                item.Locked, item.ImageId, item.ImageFit, item.CropXPercent, item.CropYPercent,
                item.AltText, item.Decorative, item.AccessibilityDecisionPending, item.Language,
                item.SemanticRole, item.ReadingOrder, item.FontFamilyKey,
                item.FontWeight, item.Italic, item.FontSizePoints, item.LetterSpacingEm, item.LineHeight,
                item.FillColor, item.BackgroundColor, item.BackgroundOpacity, item.StrokeColor,
                item.StrokeWidthPoints, item.TextAlignment, item.VerticalAlignment, item.TextShadow,
                string.Join(',', item.ContentReferences.Select(reference => $"{reference.BlockId}:{reference.StartOffset}:{reference.EndOffset}")))));
        }
        return AggregateHash(canonical);
    }

    private static string AggregateHash(IEnumerable<string> hashes) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", hashes))));

    private static async Task NormalizeLegacyPendingChapterChangesAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
        var rows = new List<(string Id, string Arguments, string Before, string After, string? Draft, string? Review, string Result)>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            select.CommandText = """
                SELECT Id, ArgumentsJson, BeforeJson, AfterJson, DraftAfterJson,
                       ReviewStateJson, ResultJson
                FROM AiChanges
                WHERE lower(Status) = 'pending'
                  AND ToolName IN ('create_chapter', 'update_chapter');
                """;
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                rows.Add((
                    reader.GetValue(0).ToString()!,
                    reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? string.Empty : reader.GetString(6)));
        }

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ContainsVisualChoice(row.Arguments))
            {
                await using var conflict = connection.CreateCommand();
                conflict.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
                conflict.CommandText = """
                    UPDATE AiChanges
                    SET Status = 'Conflict',
                        ErrorMessage = $error,
                        UpdatedAt = $updatedAt
                    WHERE Id = $id;
                    """;
                AddParameter(conflict, "$error", "RequiresReplan: choose Figures or Designed Pages within the chapter's semantic manuscript.");
                AddParameter(conflict, "$updatedAt", DateTime.UtcNow);
                AddParameter(conflict, "$id", row.Id);
                await conflict.ExecuteNonQueryAsync(cancellationToken);
                continue;
            }

            await using var update = connection.CreateCommand();
            update.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            update.CommandText = """
                UPDATE AiChanges
                SET ArgumentsJson = $arguments, BeforeJson = $before,
                    AfterJson = $after, DraftAfterJson = $draft,
                    ReviewStateJson = $review, ResultJson = $result,
                    UpdatedAt = $updatedAt
                WHERE Id = $id;
                """;
            AddParameter(update, "$arguments", RemoveVisualFields(row.Arguments));
            AddParameter(update, "$before", RemoveVisualFields(row.Before));
            AddParameter(update, "$after", RemoveVisualFields(row.After));
            AddParameter(update, "$draft", row.Draft is string draft ? RemoveVisualFields(draft) : DBNull.Value);
            AddParameter(update, "$review", row.Review is string review ? RemoveVisualFields(review) : DBNull.Value);
            AddParameter(update, "$result", RemoveVisualFields(row.Result));
            AddParameter(update, "$updatedAt", DateTime.UtcNow);
            AddParameter(update, "$id", row.Id);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static IReadOnlySet<string> Columns(params string[] names) =>
        names.ToHashSet(StringComparer.Ordinal);

    private static async Task<string> HashProtectedStateAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
        var rows = new List<string>();
        foreach (var (table, excluded) in ProtectedTables.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var columns = await ReadColumnsAsync(db, table, cancellationToken);
            var included = columns.Where(column => !excluded.Contains(column)).ToArray();
            await using var command = connection.CreateCommand();
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = $"SELECT {string.Join(", ", included.Select(QuoteIdentifier))} FROM {QuoteIdentifier(table)};";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var tableRows = new List<string>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var values = new string[included.Length];
                for (var index = 0; index < included.Length; index++)
                {
                    values[index] = reader.IsDBNull(index)
                        ? "null"
                        : reader.GetValue(index) is byte[] bytes
                            ? $"blob:{Convert.ToHexStringLower(SHA256.HashData(bytes))}"
                            : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture) ?? string.Empty;
                }
                tableRows.Add(string.Join("\u001f", values));
            }
            tableRows.Sort(StringComparer.Ordinal);
            rows.Add($"{table}|{string.Join("\u001e", included)}|{tableRows.Count}|{string.Join("\u001d", tableRows)}");
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", rows))));
    }

    private static async Task<IReadOnlyList<string>> ReadColumnsAsync(
        AppDbContext db,
        string table,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(table)});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var columns = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
            columns.Add(reader.GetString(1));
        if (columns.Count == 0)
            throw new InvalidDataException($"Protected migration table {table} is missing.");
        return columns;
    }

    private static async Task ValidateCompositionDocumentsAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var compositions = await db.PageCompositions.AsNoTracking()
            .Select(item => new PageComposition
            {
                Id = item.Id,
                ProjectId = item.ProjectId,
                ChapterId = item.ChapterId,
                Name = item.Name,
                SemanticManuscriptJson = item.SemanticManuscriptJson,
                Revision = item.Revision,
                CreatedAt = item.CreatedAt,
                UpdatedAt = item.UpdatedAt,
            })
            .ToListAsync(cancellationToken);
        var variants = await db.PageCompositionVariants.AsNoTracking().ToListAsync(cancellationToken);
        var picturePageSeeds = await db.CompositionMutationStages.AsNoTracking()
            .Where(item => item.TargetKind == "page-composition-seed" && item.ConversationId == Guid.Empty)
            .ToListAsync(cancellationToken);
        var imageOwners = await db.PublishAssets.AsNoTracking()
            .Select(item => new { item.Id, item.ProjectId })
            .ToDictionaryAsync(item => item.Id, item => item.ProjectId, cancellationToken);
        foreach (var composition in compositions)
            composition.Variants = variants.Where(item => item.CompositionId == composition.Id).ToList();
        var editions = await ReadMigrationEditionsAsync(db, cancellationToken);
        foreach (var composition in compositions)
        {
            var semantic = ManuscriptCodec.Deserialize(
                composition.SemanticManuscriptJson,
                composition.Id,
                composition.Revision);
            var seeds = picturePageSeeds.Where(item => item.TargetId == composition.Id).ToList();
            if (composition.Variants.Count == 0 && seeds.Count != 1)
                throw new InvalidDataException($"Migrated Picture Page {composition.Id:N} has neither a layout nor a unique authoring seed.");
            foreach (var variant in composition.Variants)
            {
                var scene = JsonSerializer.Deserialize<CompositionScene>(variant.SceneJson, ManuscriptCodec.JsonOptions)
                    ?? throw new InvalidDataException($"Composition variant {variant.Id:N} has no scene.");
                CompositionService.Validate(scene, semantic);
                var matchingEdition = editions.FirstOrDefault(item => item.ProjectId == composition.ProjectId
                    && CompositionService.VariantMatchesEdition(variant, item));
                if (matchingEdition is not null)
                    CompositionService.ValidateVariantGeometry(matchingEdition, scene);
            }
            foreach (var seed in seeds)
            {
                if (seed.ProjectId != composition.ProjectId
                    || seed.ExpectedRevision != composition.Revision
                    || seed.AppliedAt is not null
                    || !string.Equals(
                        seed.PayloadSha256,
                        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seed.OperationsJson))),
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Migrated Picture Page seed {seed.Id:N} failed its ownership, revision, or hash validation.");
                var scene = JsonSerializer.Deserialize<CompositionScene>(seed.OperationsJson, ManuscriptCodec.JsonOptions)
                    ?? throw new InvalidDataException($"Migrated Picture Page seed {seed.Id:N} has no scene.");
                CompositionService.Validate(scene, semantic);
                foreach (var imageId in scene.Objects
                    .Where(item => item.Kind == CompositionObjectKind.Image && item.ImageId is not null)
                    .Select(item => item.ImageId!.Value)
                    .Distinct())
                    if (!imageOwners.TryGetValue(imageId, out var owner) || owner != composition.ProjectId)
                        throw new InvalidDataException($"Migrated Picture Page seed {seed.Id:N} references an image outside its project.");
            }
        }
        if (picturePageSeeds.Any(seed => compositions.All(composition => composition.Id != seed.TargetId)))
            throw new InvalidDataException("A migrated Picture Page seed references a missing Designed Page.");
        foreach (var cover in await ReadMigrationCoversAsync(db, cancellationToken))
        {
            var scene = JsonSerializer.Deserialize<CompositionScene>(cover.CompositionSceneJson, ManuscriptCodec.JsonOptions)
                ?? throw new InvalidDataException($"Cover {cover.Id:N} has no composition scene.");
            CompositionService.Validate(scene, ManuscriptCodec.CreateEmpty(cover.Id), allowCanonicalTextBindings: true);
        }
    }

    private static async Task EnsureForeignKeysAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
            throw new InvalidDataException($"Foreign-key validation failed for table {reader.GetString(0)}.");
    }

    // Geometry repair can remain pending after the Core schema is applied.
    // Supply historical defaults only for absent Core columns; selecting an
    // existing column twice prevents EF from materializing any edition rows.
    private static async Task<List<PublicationEdition>> ReadMigrationEditionsAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var sql = "SELECT PublicationEditions.*";
        if (!await HasColumnAsync(db, "PublicationEditions", "OverrideFieldsJson", cancellationToken))
            sql += ", '[]' AS OverrideFieldsJson";
        if (!await HasColumnAsync(db, "PublicationEditions", "InheritsCoreCover", cancellationToken))
            sql += ", 0 AS InheritsCoreCover";
        sql += " FROM PublicationEditions";
        return await db.PublicationEditions.FromSqlRaw(sql).AsNoTracking().ToListAsync(cancellationToken);
    }

    private static async Task<List<PublicationCoverDesign>> ReadMigrationCoversAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var cropX = await HasColumnAsync(db, "PublicationCoverDesigns", "ImageCropXPercent", cancellationToken)
            ? "ImageCropXPercent"
            : "ImageFocalXPercent";
        var cropY = await HasColumnAsync(db, "PublicationCoverDesigns", "ImageCropYPercent", cancellationToken)
            ? "ImageCropYPercent"
            : "ImageFocalYPercent";
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = $"""
            SELECT Id, EditionId, Title, Subtitle, Author, SpineText,
                   BackgroundColor, BarcodeMode, {QuoteIdentifier(cropX)}, {QuoteIdentifier(cropY)},
                   AcknowledgedTemplateFingerprint, CompositionSceneJson, Revision
            FROM PublicationCoverDesigns
            ORDER BY Id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var covers = new List<PublicationCoverDesign>();
        while (await reader.ReadAsync(cancellationToken))
        {
            covers.Add(new PublicationCoverDesign
            {
                Id = reader.GetGuid(0),
                EditionId = reader.GetGuid(1),
                Title = reader.GetString(2),
                Subtitle = reader.GetString(3),
                Author = reader.GetString(4),
                SpineText = reader.GetString(5),
                BackgroundColor = reader.GetString(6),
                BarcodeMode = Enum.Parse<PublicationBarcodeMode>(reader.GetString(7)),
                ImageCropXPercent = reader.GetDouble(8),
                ImageCropYPercent = reader.GetDouble(9),
                AcknowledgedTemplateFingerprint = reader.GetString(10),
                CompositionSceneJson = reader.GetString(11),
                Revision = reader.GetInt64(12),
            });
        }
        return covers;
    }

    private static string QuoteIdentifier(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static bool ContainsVisualChoice(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return ContainsVisualChoice(document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ContainsVisualChoice(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name is "visualMode" or "pageLayoutKind")
                    return true;
                if (ContainsVisualChoice(property.Value))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray().Any(ContainsVisualChoice);
        }
        return false;
    }

    private static string RemoveVisualFields(string json)
    {
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(json);
            RemoveVisualFields(node);
            return node?.ToJsonString(ManuscriptCodec.JsonOptions) ?? "null";
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static void RemoveVisualFields(System.Text.Json.Nodes.JsonNode? node)
    {
        if (node is System.Text.Json.Nodes.JsonObject obj)
        {
            obj.Remove("visualMode");
            obj.Remove("pageLayoutKind");
            foreach (var value in obj.Select(item => item.Value).ToList())
                RemoveVisualFields(value);
        }
        else if (node is System.Text.Json.Nodes.JsonArray array)
        {
            foreach (var value in array)
                RemoveVisualFields(value);
        }
    }

    private static async Task<bool> HasColumnAsync(
        AppDbContext db,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        if (command.Connection!.State != System.Data.ConnectionState.Open)
            await command.Connection.OpenAsync(cancellationToken);
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = $"PRAGMA table_info(\"{table}\");";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static async Task<IReadOnlyList<LegacyChapterVisualRow>> ReadLegacyChaptersAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var rows = new List<LegacyChapterVisualRow>();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        if (command.Connection!.State != System.Data.ConnectionState.Open)
            await command.Connection.OpenAsync(cancellationToken);
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = """
            SELECT "Id", "ProjectId", "Title", "VisualMode", "PageLayoutKind",
                   "PageLayoutJson", "IllustrationLayoutJson"
            FROM "Chapters"
            ORDER BY "ProjectId", "Order";
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new LegacyChapterVisualRow(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                Enum.Parse<ChapterVisualMode>(reader.GetString(3), ignoreCase: true),
                Enum.Parse<ChapterPageLayoutKind>(reader.GetString(4), ignoreCase: true),
                reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                reader.IsDBNull(6) ? string.Empty : reader.GetString(6)));
        }
        return rows;
    }

    private sealed record LegacyChapterVisualRow(
        Guid Id,
        Guid ProjectId,
        string Title,
        ChapterVisualMode VisualMode,
        ChapterPageLayoutKind PageLayoutKind,
        string PageLayoutJson,
        string IllustrationLayoutJson);
}
