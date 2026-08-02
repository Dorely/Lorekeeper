using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lorekeeper.Composition;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class UnifiedCompositionMigrationTests
{
    [Fact]
    public async Task PicturePageWithoutEditionMaterializesAnExactVariantWhenFirstEditionIsCreated()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "orphan-layout.db");
        var configuration = Configuration(databasePath);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={databasePath}").Options;
        try
        {
            await using var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance);
            await db.GetService<IMigrator>().MigrateAsync(VisualCompositionMigrationService.AdditiveMigrationId);
            var project = new Project { Name = "No-edition project", Slug = $"no-edition-{Guid.NewGuid():N}" };
            var document = ManuscriptCodec.FromPlainText(Guid.NewGuid(), "Preserved page copy", revision: 2, deterministicIds: true);
            var chapter = new Chapter
            {
                Id = document.ManuscriptId,
                Project = project,
                ProjectId = project.Id,
                Title = "Designed leaf",
                ManuscriptJson = ManuscriptCodec.Serialize(document),
                ManuscriptRevision = document.Revision,
            };
            db.Projects.Add(project);
            await db.SaveChangesAsync();
            await InsertLegacyChapterAsync(db, chapter);
            var layout = new PicturePageLayout(
                [],
                [new PicturePageTextElement(
                    Guid.NewGuid(), string.Empty, 10, 12, 80, 30, 1, 0, "builtin:nunito", 400, false,
                    15, 0, 1.2, "#111111", "transparent", 0, PicturePageTextAlign.Center,
                    ChapterTextVerticalAlign.Middle, PicturePageTextShadow.None,
                    ContentReferences: [new ManuscriptRangeReference(document.Content[0].Id)])]);
            await SetLegacyVisualAsync(
                db,
                chapter.Id,
                ChapterVisualMode.PicturePage,
                ChapterPageLayoutKind.SingleLandscape,
                JsonSerializer.Serialize(layout, ManuscriptCodec.JsonOptions),
                string.Empty);

            var recovery = new DatabaseMigrationRecoveryService(configuration, NullLogger<DatabaseMigrationRecoveryService>.Instance);
            var migration = new VisualCompositionMigrationService(recovery, NullLogger<VisualCompositionMigrationService>.Instance);
            await migration.ApplyPendingAsync(db);
            var composition = await db.PageCompositions.Include(item => item.Variants).SingleAsync();
            Assert.Empty(composition.Variants);
            var seed = await db.CompositionMutationStages.SingleAsync(item => item.TargetKind == "page-composition-seed");
            var seedScene = JsonSerializer.Deserialize<CompositionScene>(seed.OperationsJson, ManuscriptCodec.JsonOptions)!;
            await migration.ApplyFinalSchemaAsync(db);

            var edition = new PublicationEdition
            {
                ProjectId = project.Id,
                Name = "First edition",
                Format = PublicationEditionFormat.DigitalPdf,
                VendorProfileVersion = "generic-digital-pdf-v1",
                PageWidthInches = 8,
                PageHeightInches = 5,
                AllowDesignedPageOverrides = true,
            };
            db.PublicationEditions.Add(edition);
            await db.SaveChangesAsync();
            var compositions = new CompositionService(
                db,
                null!,
                null!,
                new ProjectMutationCoordinator($"Data Source={databasePath}"));
            var exact = await compositions.GetOrCreateVariantAsync(project.Id, composition.Id, edition.Id);
            db.ChangeTracker.Clear();

            var variants = await db.PageCompositionVariants.Where(item => item.CompositionId == composition.Id).ToListAsync();
            Assert.Single(variants);
            var exactScene = JsonSerializer.Deserialize<CompositionScene>(exact.SceneJson, ManuscriptCodec.JsonOptions)!;
            Assert.Equal(CompositionService.GeometryKey(edition, exactScene), exact.GeometryKey);
            Assert.False(await db.CompositionMutationStages.AnyAsync(item => item.TargetKind == "page-composition-seed"));
            var mapped = JsonSerializer.Deserialize<CompositionScene>(exact.SceneJson, ManuscriptCodec.JsonOptions)!;
            Assert.Equal(CompositionSurfaceKind.IndependentPage, mapped.Surface.Kind);
            Assert.Equal(seedScene.Surface.WidthPoints, mapped.Surface.WidthPoints);
            Assert.Equal(seedScene.Surface.HeightPoints, mapped.Surface.HeightPoints);
            Assert.Contains("Preserved page copy", ManuscriptCodec.ProjectPlainText(ManuscriptCodec.Deserialize(composition.SemanticManuscriptJson)));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MalformedLegacySceneEntersRecoveryWithProtectedOriginal()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "malformed-layout.db");
        var configuration = Configuration(databasePath);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={databasePath}").Options;
        try
        {
            await using var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance);
            await db.GetService<IMigrator>().MigrateAsync(VisualCompositionMigrationService.AdditiveMigrationId);
            var project = new Project { Name = "Malformed layout", Slug = $"malformed-{Guid.NewGuid():N}" };
            var document = ManuscriptCodec.FromPlainText(Guid.NewGuid(), "Original manuscript", deterministicIds: true);
            var chapter = new Chapter
            {
                Id = document.ManuscriptId,
                Project = project,
                ProjectId = project.Id,
                Title = "Broken visual",
                ManuscriptJson = ManuscriptCodec.Serialize(document),
                ManuscriptRevision = document.Revision,
            };
            db.Projects.Add(project);
            await db.SaveChangesAsync();
            await InsertLegacyChapterAsync(db, chapter);
            await SetLegacyVisualAsync(db, chapter.Id, ChapterVisualMode.PicturePage, ChapterPageLayoutKind.SinglePortrait, "not-json", string.Empty);

            var recovery = new DatabaseMigrationRecoveryService(configuration, NullLogger<DatabaseMigrationRecoveryService>.Instance);
            var migration = new VisualCompositionMigrationService(recovery, NullLogger<VisualCompositionMigrationService>.Instance);
            await migration.ApplyPendingAsync(db);

            var state = await recovery.GetStateAsync();
            Assert.True(state.RecoveryRequired);
            Assert.Equal(VisualCompositionMigrationService.MigrationName, state.MigrationName);
            Assert.True(File.Exists(state.BackupPath));
            var backupBytes = await File.ReadAllBytesAsync(state.BackupPath!);
            Assert.NotEmpty(backupBytes);
            Assert.Contains("Json", state.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FinalCompositionSchemaFailureAlsoEntersProtectedRecovery()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "final-schema-failure.db");
        var configuration = Configuration(databasePath);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={databasePath}").Options;
        try
        {
            await using var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance);
            await db.GetService<IMigrator>().MigrateAsync(VisualCompositionMigrationService.AdditiveMigrationId);
            var recovery = new DatabaseMigrationRecoveryService(configuration, NullLogger<DatabaseMigrationRecoveryService>.Instance);
            var migration = new VisualCompositionMigrationService(recovery, NullLogger<VisualCompositionMigrationService>.Instance);
            await migration.ApplyPendingAsync(db);
            await db.GetService<IMigrator>().MigrateAsync(VisualCompositionMigrationService.CleanupMigrationId);
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE PublicationArtifacts ADD COLUMN PaginationFingerprint TEXT NOT NULL DEFAULT '';");

            await migration.ApplyFinalSchemaAsync(db);

            var state = await recovery.GetStateAsync();
            Assert.True(state.RecoveryRequired);
            Assert.Equal(VisualCompositionMigrationService.MigrationName, state.MigrationName);
            Assert.True(File.Exists(state.BackupPath));
            Assert.Contains("duplicate column", state.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PopulatedVisualDatabasePreservesSemanticAndPublicationDataAcrossGuardedCutover()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "visual-cutover.db");
        var configuration = Configuration(databasePath);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={databasePath}").Options;
        try
        {
            await using var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance);
            await db.GetService<IMigrator>().MigrateAsync(VisualCompositionMigrationService.AdditiveMigrationId);

            var project = new Project { Name = "Populated visual project", Slug = $"visual-{Guid.NewGuid():N}" };
            var image = new PublishAsset
            {
                Project = project, ProjectId = project.Id, FileName = "art.png", ContentType = "image/png",
                Data = [137, 80, 78, 71], AltText = string.Empty,
            };
            var edition = new PublicationEdition
            {
                Project = project, ProjectId = project.Id, Name = "Paperback", Format = PublicationEditionFormat.Paperback,
                PageWidthInches = 5, PageHeightInches = 8, VendorProfileVersion = "generic-paperback-v1",
            };
            var artifactBytes = Encoding.UTF8.GetBytes("immutable publication bytes");
            var artifactHash = Convert.ToHexStringLower(SHA256.HashData(artifactBytes));
            var render = new PublicationRenderJob
            {
                Edition = edition, EditionId = edition.Id, Status = PublicationRenderStatus.Completed,
                ProfileId = edition.VendorProfileVersion, RendererVersion = "historical", SourceFingerprint = "source",
            };
            var artifact = new PublicationArtifact
            {
                Edition = edition, EditionId = edition.Id, RenderJob = render, RenderJobId = render.Id,
                Kind = PublicationArtifactKind.InteriorPdf, FileName = "interior.pdf", MediaType = "application/pdf",
                Data = artifactBytes, ByteLength = artifactBytes.Length, Sha256 = artifactHash, PageCount = 120,
                SourceFingerprint = "source", RendererVersion = "historical", ProfileId = edition.VendorProfileVersion,
            };
            var package = new PublicationArtifact
            {
                Edition = edition, EditionId = edition.Id, Kind = PublicationArtifactKind.PublicationPackage,
                FileName = "package.zip", MediaType = "application/zip", Data = [1, 2, 3], ByteLength = 3,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData([1, 2, 3])), SourceFingerprint = "source",
            };
            var proof = new PublicationArtifact
            {
                Edition = edition, EditionId = edition.Id, Kind = PublicationArtifactKind.ProofRecord,
                FileName = "proof.json", MediaType = "application/json", Data = [123, 125], ByteLength = 2,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData([123, 125])), SourceFingerprint = "source",
            };
            var cover = new PublicationCoverDesign
            {
                Edition = edition, EditionId = edition.Id, Title = "Bound title", Author = "Bound author",
                BackCopy = "Bound back copy",
            };
            var audit = new PublicationEditionAuditEntry
            {
                Edition = edition, EditionId = edition.Id, Action = "fixture", BeforeHash = "before", AfterHash = "after",
            };
            var chapters = new List<Chapter>();
            var layouts = Enum.GetValues<ChapterPageLayoutKind>();
            for (var index = 0; index < layouts.Length; index++)
            {
                var document = ManuscriptCodec.FromPlainText(Guid.NewGuid(), $"Placed {layouts[index]}\n\nUnplaced {layouts[index]}", revision: 4, deterministicIds: true);
                chapters.Add(new Chapter
                {
                    Id = document.ManuscriptId, Project = project, ProjectId = project.Id,
                    Title = layouts[index].ToString(), Order = index, ManuscriptRevision = document.Revision,
                    ManuscriptJson = ManuscriptCodec.Serialize(document),
                });
            }
            var illustratedDocument = ManuscriptCodec.FromPlainText(Guid.NewGuid(), "Anchor paragraph", revision: 3, deterministicIds: true);
            var illustrated = new Chapter
            {
                Id = illustratedDocument.ManuscriptId, Project = project, ProjectId = project.Id,
                Title = "Illustrated", Order = 10, ManuscriptRevision = illustratedDocument.Revision,
                ManuscriptJson = ManuscriptCodec.Serialize(illustratedDocument),
            };
            chapters.Add(illustrated);
            var batch = new AiChangeBatch { Project = project, ProjectId = project.Id, ConversationId = Guid.NewGuid() };
            var safeChange = new AiChange
            {
                Batch = batch, BatchId = batch.Id, Order = 0, ToolName = "update_chapter",
                ArgumentsJson = JsonSerializer.Serialize(new { chapterId = chapters[0].Id, title = "Revised title" }),
                AfterJson = JsonSerializer.Serialize(new { id = chapters[0].Id, title = "Revised title", visualMode = "PicturePage", pageLayoutKind = "SinglePortrait" }),
            };
            var visualChange = new AiChange
            {
                Batch = batch, BatchId = batch.Id, Order = 1, ToolName = "update_chapter",
                ArgumentsJson = JsonSerializer.Serialize(new { chapterId = chapters[1].Id, visualMode = "PicturePage" }),
            };
            db.AddRange(project, image, batch, safeChange, visualChange);
            await db.SaveChangesAsync();
            await InsertLegacyEditionAsync(db, edition);
            db.Attach(edition);
            db.AddRange(cover, audit);
            await db.SaveChangesAsync();
            await InsertLegacyPublicationOutputsAsync(db, render, artifact, package, proof);
            foreach (var chapter in chapters)
                await InsertLegacyChapterAsync(db, chapter);

            for (var index = 0; index < layouts.Length; index++)
            {
                var source = ManuscriptCodec.Deserialize(chapters[index].ManuscriptJson);
                var layout = new PicturePageLayout(
                    [new PicturePageImageElement(Guid.NewGuid(), image.Id, 5, 5, 90, 90, ChapterImageFit.Cover, 1, 0, "")],
                    [new PicturePageTextElement(Guid.NewGuid(), string.Empty, 10, 10, 40, 30, 2, 1, "builtin:nunito", 600, true, 18, .08, 1.2, "#111111", "#fedcba", .4, PicturePageTextAlign.Center, ChapterTextVerticalAlign.Bottom, PicturePageTextShadow.Strong, ContentReferences: [new ManuscriptRangeReference(source.Content[0].Id)])]);
                await SetLegacyVisualAsync(db, chapters[index].Id, ChapterVisualMode.PicturePage, layouts[index], JsonSerializer.Serialize(layout, ManuscriptCodec.JsonOptions), "");
            }
            var illustratedLayout = new IllustratedProseLayout(
                [new IllustratedProseImageBlock(Guid.NewGuid(), image.Id, ChapterImageAnchorPosition.AfterParagraph, illustratedDocument.Content[0].Id, 62, ChapterImageAlignment.Center, "Preserved caption", "Preserved alt", 0, true)]);
            await SetLegacyVisualAsync(db, illustrated.Id, ChapterVisualMode.IllustratedProse, ChapterPageLayoutKind.SinglePortrait, "", JsonSerializer.Serialize(illustratedLayout, ManuscriptCodec.JsonOptions));

            var recovery = new DatabaseMigrationRecoveryService(configuration, NullLogger<DatabaseMigrationRecoveryService>.Instance);
            var migration = new VisualCompositionMigrationService(recovery, NullLogger<VisualCompositionMigrationService>.Instance);
            await migration.ApplyPendingAsync(db);
            var recoveryState = await recovery.GetStateAsync();
            Assert.False(recoveryState.RecoveryRequired, recoveryState.Error);
            await db.GetService<IMigrator>().MigrateAsync(VisualCompositionMigrationService.CleanupMigrationId);
            var placementId = Guid.NewGuid();
            var stageId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO "PublicationImagePlacements" (
                    "Id", "EditionId", "AssetId", "TargetKind", "TargetId", "ActId", "ChapterId",
                    "PlacementKind", "SortOrder", "Caption", "CreatedAt", "UpdatedAt")
                VALUES (
                    {{placementId}}, {{edition.Id}}, {{image.Id}}, 'Chapter', {{chapters[0].Id}}, NULL,
                    {{chapters[0].Id}}, 'AfterChapter', 0, 'Migrated placement', {{DateTime.UtcNow}}, {{DateTime.UtcNow}});
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO "CompositionMutationStages" (
                    "Id", "ProjectId", "ConversationId", "TargetKind", "TargetId", "ExpectedRevision",
                    "OperationsJson", "PayloadSha256", "ExpiresAt", "AppliedAt", "CreatedAt")
                VALUES (
                    {{stageId}}, {{project.Id}}, {{Guid.NewGuid()}}, 'PageComposition', {{Guid.NewGuid()}}, 0,
                    '[]', 'stage-hash', {{DateTime.UtcNow.AddHours(1)}}, NULL, {{DateTime.UtcNow}});
                """);
            await db.GetService<IMigrator>().MigrateAsync();
            await migration.ApplyPendingAsync(db);
            Assert.False((await recovery.GetStateAsync()).RecoveryRequired);
            db.ChangeTracker.Clear();

            var migratedChapters = await db.Chapters.OrderBy(item => item.Order).ToListAsync();
            Assert.Equal(chapters.Count, migratedChapters.Count);
            foreach (var chapter in migratedChapters.Take(layouts.Length))
            {
                var block = Assert.Single(ManuscriptCodec.Deserialize(chapter.ManuscriptJson).Content);
                Assert.Equal(ManuscriptBlockType.DesignedPage, block.Type);
                var composition = await db.PageCompositions.Include(item => item.Variants).SingleAsync(item => item.Id == block.PageCompositionId);
                Assert.Contains("Placed", ManuscriptCodec.ProjectPlainText(ManuscriptCodec.Deserialize(composition.SemanticManuscriptJson)));
                Assert.Contains("Unplaced", ManuscriptCodec.ProjectPlainText(ManuscriptCodec.Deserialize(composition.SemanticManuscriptJson)));
                var variant = Assert.Single(composition.Variants);
                var scene = JsonSerializer.Deserialize<CompositionScene>(variant.SceneJson, ManuscriptCodec.JsonOptions)!;
                Assert.Equal(chapter.Title.StartsWith("Double", StringComparison.Ordinal) ? 720 : 360, scene.Surface.WidthPoints);
                Assert.Equal(576, scene.Surface.HeightPoints);
                Assert.NotEqual(CompositionSurfaceKind.IndependentPage, scene.Surface.Kind);
                var migratedImage = Assert.Single(scene.Objects, item => item.Kind == CompositionObjectKind.Image);
                Assert.Equal(string.Empty, migratedImage.AltText);
                Assert.False(migratedImage.Decorative);
                Assert.True(migratedImage.AccessibilityDecisionPending);
                var migratedText = Assert.Single(scene.Objects, item => item.Kind == CompositionObjectKind.Text);
                Assert.Equal(.08, migratedText.LetterSpacingEm);
                Assert.Equal("#fedcba", migratedText.BackgroundColor);
                Assert.Equal(.4, migratedText.BackgroundOpacity);
                Assert.Equal(CompositionTextAlignment.Center, migratedText.TextAlignment);
                Assert.Equal(CompositionVerticalAlignment.Bottom, migratedText.VerticalAlignment);
                Assert.Equal(CompositionTextShadow.Strong, migratedText.TextShadow);
            }
            var migratedIllustrated = ManuscriptCodec.Deserialize(migratedChapters.Single(item => item.Id == illustrated.Id).ManuscriptJson);
            var figure = Assert.Single(migratedIllustrated.Content, item => item.Type == ManuscriptBlockType.Figure);
            Assert.Equal(image.Id, figure.ImageId);
            Assert.Equal("Preserved caption", ManuscriptCodec.Text(figure));
            Assert.Equal("Preserved alt", figure.AltText);
            Assert.True(figure.FigurePresentation!.StartOnNewPage);

            var preservedArtifact = await db.PublicationArtifacts.SingleAsync(item => item.Id == artifact.Id);
            Assert.Equal(artifactBytes, preservedArtifact.Data);
            Assert.Equal(artifactHash, preservedArtifact.Sha256);
            Assert.True(preservedArtifact.IsLegacy);
            Assert.True((await db.PublicationRenderJobs.SingleAsync(item => item.Id == render.Id)).IsLegacy);
            Assert.Equal(3, await db.PublicationArtifacts.CountAsync());
            Assert.Equal("after", (await db.PublicationEditionAuditEntries.SingleAsync()).AfterHash);
            Assert.NotEmpty((await db.PublicationCoverDesigns.SingleAsync()).CompositionSceneJson);
            var migratedPlacement = await db.PublicationImagePlacements.SingleAsync(item => item.Id == placementId);
            Assert.Equal(string.Empty, migratedPlacement.AltText);
            Assert.Equal("en", migratedPlacement.Language);
            Assert.Contains("dedicatedPage", migratedPlacement.PresentationJson, StringComparison.Ordinal);
            Assert.Equal(project.Id, (await db.CompositionMutationStages.SingleAsync(item => item.Id == stageId)).ProjectId);
            Assert.Equal(string.Empty, preservedArtifact.PaginationFingerprint);
            Assert.Equal(string.Empty, (await db.PublicationRenderJobs.SingleAsync(item => item.Id == render.Id)).PaginationFingerprint);
            Assert.DoesNotContain("visualMode", (await db.AiChanges.SingleAsync(item => item.Id == safeChange.Id)).AfterJson, StringComparison.OrdinalIgnoreCase);
            var replanned = await db.AiChanges.SingleAsync(item => item.Id == visualChange.Id);
            Assert.Equal(AiChangeStatus.Conflict, replanned.Status);
            Assert.StartsWith("RequiresReplan:", replanned.ErrorMessage);
            Assert.Contains(await db.ManuscriptMigrationJournals.ToListAsync(), item => item.MigrationName == VisualCompositionMigrationService.MigrationName && item.Status == ManuscriptMigrationStatus.Completed);
            Assert.Contains(await db.ManuscriptMigrationJournals.ToListAsync(), item => item.MigrationName == VisualCompositionMigrationService.AccessibilityDecisionMigrationName && item.Status == ManuscriptMigrationStatus.Completed);

            var digitalEdition = new PublicationEdition
            {
                ProjectId = project.Id,
                Name = "Digital override geometry",
                Format = PublicationEditionFormat.DigitalPdf,
                VendorProfileVersion = "generic-digital-pdf-v1",
                PageWidthInches = edition.PageWidthInches,
                PageHeightInches = edition.PageHeightInches,
                AllowDesignedPageOverrides = true,
            };
            db.PublicationEditions.Add(digitalEdition);
            var policyComposition = await db.PageCompositions.Include(item => item.Variants).OrderBy(item => item.CreatedAt).FirstAsync();
            db.PageCompositionVariants.Add(new PageCompositionVariant
            {
                CompositionId = policyComposition.Id,
                GeometryKey = CompositionService.LegacyGeometryKey(digitalEdition),
                SceneJson = policyComposition.Variants.Single().SceneJson,
            });
            await db.SaveChangesAsync();
            await db.ManuscriptMigrationJournals
                .Where(item => item.MigrationName == VisualCompositionMigrationService.GeometryPolicyMigrationName)
                .ExecuteDeleteAsync();
            await migration.ApplyPendingAsync(db);
            db.ChangeTracker.Clear();
            var isolatedVariants = await db.PageCompositionVariants
                .Where(item => item.CompositionId == policyComposition.Id)
                .OrderBy(item => item.GeometryKey)
                .ToListAsync();
            var persistedPaperback = await db.PublicationEditions.SingleAsync(item => item.Id == edition.Id);
            var persistedDigital = await db.PublicationEditions.SingleAsync(item => item.Id == digitalEdition.Id);
            Assert.Equal(2, isolatedVariants.Count);
            Assert.All(isolatedVariants, variant =>
            {
                var scene = JsonSerializer.Deserialize<CompositionScene>(variant.SceneJson, ManuscriptCodec.JsonOptions)!;
                Assert.True(
                    variant.GeometryKey == CompositionService.GeometryKey(persistedPaperback, scene)
                    || variant.GeometryKey == CompositionService.GeometryKey(persistedDigital, scene));
            });
            Assert.Contains(isolatedVariants, variant => CompositionService.VariantMatchesEdition(variant, persistedPaperback));
            Assert.Contains(isolatedVariants, variant => CompositionService.VariantMatchesEdition(variant, persistedDigital));
            Assert.Contains(await db.ManuscriptMigrationJournals.ToListAsync(), item => item.MigrationName == VisualCompositionMigrationService.GeometryPolicyMigrationName && item.Status == ManuscriptMigrationStatus.Completed);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task InsertLegacyPublicationOutputsAsync(
        AppDbContext db,
        PublicationRenderJob render,
        params PublicationArtifact[] artifacts)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "PublicationRenderJobs" (
                "Id", "EditionId", "Status", "ProgressPercent", "ProgressMessage", "DiagnosticsJson",
                "EvidenceJson", "SourceFingerprint", "RendererVersion", "ProfileId", "CancellationRequested",
                "IsLegacy", "CreatedAt", "StartedAt", "CompletedAt")
            VALUES (
                {{render.Id}}, {{render.EditionId}}, {{render.Status.ToString()}}, {{render.ProgressPercent}},
                {{render.ProgressMessage}}, {{render.DiagnosticsJson}}, {{render.EvidenceJson}},
                {{render.SourceFingerprint}}, {{render.RendererVersion}}, {{render.ProfileId}},
                {{render.CancellationRequested}}, {{render.IsLegacy}}, {{render.CreatedAt}},
                {{render.StartedAt}}, {{render.CompletedAt}});
            """);
        foreach (var artifact in artifacts)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO "PublicationArtifacts" (
                    "Id", "EditionId", "RenderJobId", "Kind", "FileName", "MediaType", "Data", "ByteLength",
                    "Sha256", "PageCount", "SourceFingerprint", "RendererVersion", "ProfileId", "IsLegacy", "CreatedAt")
                VALUES (
                    {{artifact.Id}}, {{artifact.EditionId}}, {{artifact.RenderJobId}}, {{artifact.Kind.ToString()}},
                    {{artifact.FileName}}, {{artifact.MediaType}}, {{artifact.Data}}, {{artifact.ByteLength}},
                    {{artifact.Sha256}}, {{artifact.PageCount}}, {{artifact.SourceFingerprint}},
                    {{artifact.RendererVersion}}, {{artifact.ProfileId}}, {{artifact.IsLegacy}}, {{artifact.CreatedAt}});
                """);
        }
    }

    private static async Task SetLegacyVisualAsync(
        AppDbContext db, Guid chapterId, ChapterVisualMode mode, ChapterPageLayoutKind kind, string pageJson, string illustrationJson)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE "Chapters"
            SET "VisualMode" = {{mode.ToString()}}, "PageLayoutKind" = {{kind.ToString()}},
                "PageLayoutJson" = {{pageJson}}, "IllustrationLayoutJson" = {{illustrationJson}}
            WHERE "Id" = {{chapterId}};
            """);
    }

    private static async Task InsertLegacyChapterAsync(AppDbContext db, Chapter chapter)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "Chapters" (
                "Id", "ProjectId", "ActId", "Title", "Synopsis", "Order", "CreatedAt", "UpdatedAt",
                "ManuscriptJson", "ManuscriptRevision", "VectorIndexState", "VectorIndexedAt", "VectorIndexError",
                "VisualMode", "PageLayoutKind", "PageLayoutJson", "IllustrationLayoutJson")
            VALUES (
                {{chapter.Id}}, {{chapter.ProjectId}}, NULL, {{chapter.Title}}, {{chapter.Synopsis}}, {{chapter.Order}},
                {{chapter.CreatedAt}}, {{chapter.UpdatedAt}}, {{chapter.ManuscriptJson}}, {{chapter.ManuscriptRevision}},
                {{chapter.VectorIndexState.ToString()}}, NULL, NULL, 'Prose', 'SinglePortrait', '', '');
            """);
    }

    private static async Task InsertLegacyEditionAsync(AppDbContext db, PublicationEdition edition)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "PublicationEditions" (
                "Id", "ProjectId", "Name", "Format", "Vendor", "VendorProfileVersion", "Status", "IsDefault", "Revision",
                "TitleOverride", "Subtitle", "Author", "Language", "Publisher", "Copyright", "Isbn", "Description",
                "IncludeTableOfContents", "IncludeVisibleTableOfContents", "IncludeActSynopses", "IncludeChapterSynopses",
                "IncludeActHeadings", "IncludeChapterHeadings", "NumberActs", "NumberChapters", "TitlePageMode", "Binding",
                "Paper", "Ink", "Bleed", "AllowDesignedPageOverrides", "PageWidthInches", "PageHeightInches", "PageMarginInches",
                "BodyFontSizePoints", "BodyLineHeight", "SelectedCoverImageId", "CreatedAt", "UpdatedAt",
                "PrintPicturePageSpreadMode", "EpubPicturePageSpreadMode")
            VALUES (
                {{edition.Id}}, {{edition.ProjectId}}, {{edition.Name}}, {{edition.Format.ToString()}}, {{edition.Vendor.ToString()}},
                {{edition.VendorProfileVersion}}, {{edition.Status.ToString()}}, {{edition.IsDefault}}, {{edition.Revision}},
                {{edition.TitleOverride}}, {{edition.Subtitle}}, {{edition.Author}}, {{edition.Language}}, {{edition.Publisher}},
                {{edition.Copyright}}, {{edition.Isbn}}, {{edition.Description}}, {{edition.IncludeTableOfContents}},
                {{edition.IncludeVisibleTableOfContents}}, {{edition.IncludeActSynopses}}, {{edition.IncludeChapterSynopses}},
                {{edition.IncludeActHeadings}}, {{edition.IncludeChapterHeadings}}, {{edition.NumberActs}}, {{edition.NumberChapters}},
                {{edition.TitlePageMode.ToString()}}, {{edition.Binding.ToString()}}, {{edition.Paper.ToString()}}, {{edition.Ink.ToString()}},
                {{edition.Bleed}}, {{edition.AllowDesignedPageOverrides}}, {{edition.PageWidthInches}}, {{edition.PageHeightInches}},
                {{edition.PageMarginInches}}, {{edition.BodyFontSizePoints}}, {{edition.BodyLineHeight}}, NULL, {{edition.CreatedAt}},
                {{edition.UpdatedAt}}, 'FacingPages', 'FacingPages');
            """);
    }

    private static IConfiguration Configuration(string databasePath) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = $"Data Source={databasePath}",
        })
        .Build();
}
