using System.Security.Cryptography;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Publish;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class LorekeeperPressMigrationTests
{
    private const string PreviousMigration = "20260801022548_PublicationCoverImagesV14";

    [Fact]
    public async Task PopulatedPressDatabasePreservesArtifactsAndSafelyCutsOverProfilesAndJobs()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var databasePath = Path.Combine(directory, "press-cutover.db");
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;
            var projectId = Guid.NewGuid();
            var editionId = Guid.NewGuid();
            var unknownEditionId = Guid.NewGuid();
            var completedJobId = Guid.NewGuid();
            var queuedJobId = Guid.NewGuid();
            var artifactId = Guid.NewGuid();
            var actId = Guid.NewGuid();
            var chapterId = Guid.NewGuid();
            var styleId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var matterId = Guid.NewGuid();
            var coverDesignId = Guid.NewGuid();
            var assetBytes = "preserved publish image bytes"u8.ToArray();
            var bytes = "%PDF-1.7\nimmutable legacy bytes"u8.ToArray();
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var now = DateTime.UtcNow;
            var emptyJson = "{}";
            var legacyManuscriptJson = ManuscriptCodec.Serialize(ManuscriptCodec.CreateEmpty(chapterId, revision: 7))
                .Replace($"\"schemaVersion\":{ManuscriptDocument.CurrentSchemaVersion}", "\"schemaVersion\":2", StringComparison.Ordinal);

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
                db.Projects.Add(new Project { Id = projectId, Name = "Existing", Slug = $"existing-{projectId:N}" });
                db.Acts.Add(new Act { Id = actId, ProjectId = projectId, Title = "Existing act" });
                db.ManuscriptStyleDefinitions.Add(new ManuscriptStyleDefinition
                {
                    Id = styleId,
                    ProjectId = projectId,
                    Name = "Existing body",
                    NameKey = "existing body",
                    Kind = ManuscriptStyleKind.Paragraph,
                    SemanticRole = "body",
                    SemanticRoleKey = "body",
                    DefinitionJson = "{\"font\":\"Lora\"}",
                });
                await db.SaveChangesAsync();
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO Chapters (
                        Id, ProjectId, ActId, Title, Synopsis, "Order", VisualMode,
                        IllustrationLayoutJson, PageLayoutJson, PageLayoutKind,
                        ManuscriptJson, ManuscriptRevision, VectorIndexState, VectorIndexError,
                        VectorIndexedAt, CreatedAt, UpdatedAt)
                    VALUES ({chapterId}, {projectId}, {actId}, 'Existing chapter', '', 0, 'Prose',
                        '', '', 'SinglePortrait', {legacyManuscriptJson}, 7,
                        'Stale', NULL, NULL, {now}, {now});
                    """);
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO PublicationEditions (
                        Id, ProjectId, Name, Format, Vendor, VendorProfileVersion, Status,
                        IsDefault, Revision, TitleOverride, Subtitle, Author, Language,
                        Publisher, Copyright, Isbn, Description, IncludeTableOfContents,
                        IncludeVisibleTableOfContents, IncludeActSynopses, IncludeChapterSynopses,
                        IncludeActHeadings, IncludeChapterHeadings, NumberActs, NumberChapters,
                        TitlePageMode, PrintPicturePageSpreadMode, EpubPicturePageSpreadMode,
                        Binding, Paper, Ink, Bleed, PageWidthInches, PageHeightInches,
                        PageMarginInches, BodyFontSizePoints, BodyLineHeight,
                        SelectedCoverImageId, CreatedAt, UpdatedAt)
                    VALUES (
                        {editionId}, {projectId}, 'Paperback', 'Paperback', 'AmazonKdp', 'preview-1',
                        'Draft', 1, 9, 'Existing title', '', 'Author', 'en', '', '', '', '',
                        1, 1, 0, 0, 1, 1, 0, 0, 'Automatic', 'WholeSpread', 'RequestLandscape',
                        'PerfectBound', 'White', 'BlackAndWhite', 1, 6, 9, 0.75, 11, 1.4,
                        NULL, {now}, {now});

                    INSERT INTO PublicationEditions (
                        Id, ProjectId, Name, Format, Vendor, VendorProfileVersion, Status,
                        IsDefault, Revision, TitleOverride, Subtitle, Author, Language,
                        Publisher, Copyright, Isbn, Description, IncludeTableOfContents,
                        IncludeVisibleTableOfContents, IncludeActSynopses, IncludeChapterSynopses,
                        IncludeActHeadings, IncludeChapterHeadings, NumberActs, NumberChapters,
                        TitlePageMode, PrintPicturePageSpreadMode, EpubPicturePageSpreadMode,
                        Binding, Paper, Ink, Bleed, PageWidthInches, PageHeightInches,
                        PageMarginInches, BodyFontSizePoints, BodyLineHeight,
                        SelectedCoverImageId, CreatedAt, UpdatedAt)
                    VALUES (
                        {unknownEditionId}, {projectId}, 'Unknown profile', 'Paperback', 'Generic', 'custom-profile-v9',
                        'Draft', 0, 3, 'Unknown', '', 'Author', 'en', '', '', '', '',
                        1, 1, 0, 0, 1, 1, 0, 0, 'Automatic', 'WholeSpread', 'RequestLandscape',
                        'PerfectBound', 'White', 'BlackAndWhite', 0, 6, 9, 0.75, 11, 1.4,
                        NULL, {now}, {now});

                    INSERT INTO PublicationRenderJobs (
                        Id, EditionId, Status, SourceFingerprint, RendererVersion, ProfileId,
                        DiagnosticsJson, EvidenceJson, ProgressPercent, ProgressMessage,
                        CancellationRequested, CreatedAt, StartedAt, CompletedAt)
                    VALUES
                        ({completedJobId}, {editionId}, 'Completed', 'source-hash', '0.2.0',
                         'kdp-paperback-6x9-preview-v1', '[]', {emptyJson}, 100, 'Completed', 0,
                         {now}, {now}, {now}),
                        ({queuedJobId}, {editionId}, 'Rendering', 'source-hash', '0.2.0',
                         'kdp-paperback-6x9-preview-v1', '[]', {emptyJson}, 40, 'Interrupted', 0,
                         {now}, {now}, NULL);

                    INSERT INTO PublicationArtifacts (
                        Id, EditionId, RenderJobId, Kind, FileName, MediaType, Data, Sha256,
                        ByteLength, PageCount, SourceFingerprint, RendererVersion, ProfileId, CreatedAt)
                    VALUES ({artifactId}, {editionId}, {completedJobId}, 'InteriorPdf', 'interior.pdf',
                        'application/pdf', {bytes}, {hash}, {bytes.Length}, 24, 'source-hash',
                        '0.2.0', 'kdp-paperback-6x9-preview-v1', {now});
                    """);

                db.PublishAssets.Add(new PublishAsset
                {
                    Id = assetId,
                    ProjectId = projectId,
                    Source = PublishAssetSource.Uploaded,
                    FileName = "existing.png",
                    ContentType = "image/png",
                    Data = assetBytes,
                    AltText = "Preserved art",
                });
                db.PublicationEditionOutlineItems.Add(new PublicationEditionOutlineItem
                {
                    EditionId = editionId,
                    TargetKind = PublishOutlineTargetKind.Chapter,
                    TargetId = chapterId,
                    ActId = actId,
                    ChapterId = chapterId,
                    SortOrder = 2,
                });
                db.PublicationEditionStyleMappings.Add(new PublicationEditionStyleMapping
                {
                    EditionId = editionId,
                    ManuscriptStyleDefinitionId = styleId,
                    SemanticRole = "body",
                    OverrideJson = "{\"size\":11}",
                    Revision = 3,
                });
                db.PublicationEditionAuditEntries.Add(new PublicationEditionAuditEntry
                {
                    EditionId = editionId,
                    Action = "existing-audit",
                    BeforeHash = "before",
                    AfterHash = "after",
                    DetailJson = "{\"preserved\":true}",
                });
                db.PublicationPageMapEntries.Add(new PublicationPageMapEntry
                {
                    RenderJobId = completedJobId,
                    ChapterId = chapterId,
                    BlockId = Guid.NewGuid(),
                    PageNumber = 17,
                });
                var conversation = new PublishConversation { ProjectId = projectId };
                db.PublishConversations.Add(conversation);
                db.PublishMessages.Add(new PublishMessage
                {
                    ConversationId = conversation.Id,
                    Order = 1,
                    Role = PublishMessageRole.User,
                    Content = "Preserve this publishing decision.",
                });
                await db.SaveChangesAsync();
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO PublicationMatter (
                        Id, EditionId, Location, Kind, Title, ManuscriptJson, Revision,
                        IsIncluded, SortOrder, CreatedAt, UpdatedAt)
                    VALUES ({matterId}, {editionId}, 'Front', 'Dedication', 'Existing dedication',
                        '{legacyManuscriptJson}', 4, 1, 1, {now}, {now});
                    """);
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO PublicationImagePlacements (
                        Id, EditionId, AssetId, TargetKind, TargetId, ActId, ChapterId,
                        PlacementKind, SortOrder, Caption, CreatedAt, UpdatedAt)
                    VALUES ({Guid.NewGuid()}, {editionId}, {assetId}, 'Chapter', {chapterId}, {actId},
                        {chapterId}, 'ChapterOpening', 0, 'Existing caption', {now}, {now});
                    """);
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO PublicationCoverDesigns (
                        Id, EditionId, Title, Subtitle, Author, SpineText, BackCopy,
                        BackgroundColor, BarcodeMode, ImageFocalXPercent, ImageFocalYPercent,
                        AcknowledgedTemplateFingerprint, Revision, CreatedAt, UpdatedAt)
                    VALUES ({coverDesignId}, {editionId}, 'Existing cover', '', 'Author', '',
                        'Existing back copy', '#5c7ca5', 'VendorOverlay', 50, 50, '', 5,
                        {now}, {now});
                    """);
            }

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = $"Data Source={databasePath}",
                })
                .Build();
            var recovery = new DatabaseMigrationRecoveryService(
                configuration,
                NullLogger<DatabaseMigrationRecoveryService>.Instance);
            var migration = new PublicationPressMigrationService(
                configuration,
                recovery,
                NullLogger<PublicationPressMigrationService>.Instance);
            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await migration.ApplyPendingAsync(db);
                var recoveryState = await recovery.GetStateAsync();
                Assert.False(recoveryState.RecoveryRequired, recoveryState.Error);
                await db.GetService<IMigrator>().MigrateAsync(
                    VisualCompositionMigrationService.AdditiveMigrationId);
                var visualMigration = new VisualCompositionMigrationService(
                    recovery,
                    NullLogger<VisualCompositionMigrationService>.Instance);
                await visualMigration.ApplyPendingAsync(db);
                var visualRecoveryState = await recovery.GetStateAsync();
                Assert.False(visualRecoveryState.RecoveryRequired, visualRecoveryState.Error);
                await visualMigration.ApplyFinalSchemaAsync(db);
                await db.GetService<IMigrator>().MigrateAsync(PublicationCoreMigrationService.SchemaMigrationId);
                var authoringMigration = new AuthoringPageMigrationService(
                    recovery,
                    NullLogger<AuthoringPageMigrationService>.Instance);
                await authoringMigration.ApplyPendingAsync(db);
                var authoringRecoveryState = await recovery.GetStateAsync();
                Assert.False(authoringRecoveryState.RecoveryRequired, authoringRecoveryState.Error);
                var coreMigration = new PublicationCoreMigrationService(
                    recovery,
                    NullLogger<PublicationCoreMigrationService>.Instance);
                await coreMigration.ApplyPendingAsync(db);
                var coreRecoveryState = await recovery.GetStateAsync();
                Assert.False(coreRecoveryState.RecoveryRequired, coreRecoveryState.Error);
                await db.GetService<IMigrator>().MigrateAsync(PublicationCoreMigrationService.CleanupMigrationId);
                await db.GetService<IMigrator>().MigrateAsync();
            }

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                Assert.Equal(
                    await db.PublicationBooks.AsNoTracking().CountAsync(),
                    await db.PublicationBookPdfPresentations.AsNoTracking().CountAsync());
                var edition = await db.PublicationEditions.AsNoTracking().SingleAsync(item => item.Id == editionId);
                var unknownEdition = await db.PublicationEditions.AsNoTracking()
                    .SingleAsync(item => item.Id == unknownEditionId);
                var jobs = await db.PublicationRenderJobs.AsNoTracking().OrderBy(job => job.Id).ToListAsync();
                var completed = jobs.Single(job => job.Id == completedJobId);
                var queued = jobs.Single(job => job.Id == queuedJobId);
                var artifact = await db.PublicationArtifacts.AsNoTracking().SingleAsync();

                Assert.Equal("kdp-paperback-v1", edition.VendorProfileVersion);
                Assert.Equal(9, edition.Revision);
                Assert.Equal("custom-profile-v9", unknownEdition.VendorProfileVersion);
                Assert.Equal(3, unknownEdition.Revision);
                Assert.True(completed.IsLegacy);
                Assert.Equal("0.2.0", completed.RendererVersion);
                Assert.True(queued.IsLegacy);
                Assert.Equal(PublicationRenderStatus.Queued, queued.Status);
                Assert.Equal("kdp-paperback-v1", queued.ProfileId);
                Assert.Null(queued.StartedAt);
                Assert.True(artifact.IsLegacy);
                Assert.Equal(artifactId, artifact.Id);
                Assert.Equal(hash, artifact.Sha256);
                Assert.Equal(bytes, artifact.Data);
                Assert.Equal(assetBytes, (await db.PublishAssets.AsNoTracking().SingleAsync()).Data);
                var coreBook = await db.PublicationBooks.AsNoTracking().SingleAsync();
                Assert.Equal("Existing title", coreBook.Title);
                Assert.Equal("Author", coreBook.Author);
                Assert.Single(await db.PublicationBookOutlineItems.AsNoTracking().ToListAsync());
                Assert.Single(await db.PublicationBookMatter.AsNoTracking().ToListAsync());
                Assert.Single(await db.PublicationBookImagePlacements.AsNoTracking().ToListAsync());
                var releaseOutline = Assert.Single(await db.PublicationEditionOutlineItems.AsNoTracking().ToListAsync());
                Assert.Equal(unknownEditionId, releaseOutline.EditionId);
                Assert.False(releaseOutline.IsIncluded);
                var releaseMatter = Assert.Single(await db.PublicationMatter.AsNoTracking().ToListAsync());
                Assert.Equal(unknownEditionId, releaseMatter.EditionId);
                Assert.True(releaseMatter.IsExcluded);
                Assert.Single(await db.PublicationEditionStyleMappings.AsNoTracking().ToListAsync());
                var releasePlacement = Assert.Single(await db.PublicationImagePlacements.AsNoTracking().ToListAsync());
                Assert.Equal(unknownEditionId, releasePlacement.EditionId);
                Assert.True(releasePlacement.IsExcluded);
                Assert.Single(await db.PublicationCoverDesigns.AsNoTracking().ToListAsync());
                Assert.Single(await db.PublicationEditionAuditEntries.AsNoTracking().ToListAsync());
                Assert.Single(await db.PublicationPageMapEntries.AsNoTracking().ToListAsync());
                Assert.Equal("Preserve this publishing decision.", (await db.PublishMessages.AsNoTracking().SingleAsync()).Content);
                var journal = await db.PublicationEditionMigrationJournals.AsNoTracking()
                    .SingleAsync(item => item.MigrationName == PublicationPressMigrationService.MigrationName);
                Assert.Equal("Completed", journal.Status);
                Assert.True(File.Exists(journal.BackupPath));
                Assert.Contains("guarded-cutover", journal.ValidationReportJson, StringComparison.Ordinal);
                var coreJournal = await db.ManuscriptMigrationJournals.AsNoTracking()
                    .SingleAsync(item => item.MigrationName == PublicationCoreMigrationService.MigrationName);
                Assert.Equal(ManuscriptMigrationStatus.Completed, coreJournal.Status);
                Assert.True(File.Exists(coreJournal.BackupPath));
                var resolver = new PublicationEffectiveConfigurationResolver(db);
                Assert.Single((await resolver.ResolveReleaseAsync(projectId, editionId)).Matter);
                Assert.Empty((await resolver.ResolveReleaseAsync(projectId, unknownEditionId)).Matter);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AlreadyAppliedPressSchemaGetsAnHonestProtectedReconciliationBaseline()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var databasePath = Path.Combine(directory, "already-v15.db");
            var connectionString = $"Data Source={databasePath}";
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
                await db.GetService<IMigrator>().MigrateAsync(PublicationPressMigrationService.EfMigrationId);

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = connectionString,
                })
                .Build();
            var recovery = new DatabaseMigrationRecoveryService(
                configuration,
                NullLogger<DatabaseMigrationRecoveryService>.Instance);
            var migration = new PublicationPressMigrationService(
                configuration,
                recovery,
                NullLogger<PublicationPressMigrationService>.Instance);
            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
                await migration.ApplyPendingAsync(db);

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                var journal = await db.PublicationEditionMigrationJournals.AsNoTracking()
                    .SingleAsync(item => item.MigrationName == PublicationPressMigrationService.MigrationName);
                Assert.Contains("post-v15-reconciliation", journal.ValidationReportJson, StringComparison.Ordinal);
                Assert.True(File.Exists(journal.BackupPath));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MalformedPendingMarkerIsQuarantinedBeforeAProtectedFreshCutover()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var databasePath = Path.Combine(directory, "malformed-marker.db");
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;
            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
                await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            var markerDirectory = Path.Combine(directory, ".migration-backups", "press");
            Directory.CreateDirectory(markerDirectory);
            var marker = Path.Combine(markerDirectory, "lorekeeper-press-v15.pending.json");
            await File.WriteAllTextAsync(marker, "{malformed");

            var configuration = TestConfiguration(databasePath);
            var migration = Migration(configuration);
            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
                await migration.ApplyPendingAsync(db);

            Assert.False(File.Exists(marker));
            Assert.Single(Directory.EnumerateFiles(markerDirectory, "*.invalid"));
            await using var verified = new AppDbContext(options, NullLogger<AppDbContext>.Instance);
            Assert.Contains(
                PublicationPressMigrationService.EfMigrationId,
                await verified.Database.GetAppliedMigrationsAsync());
            Assert.True(await verified.PublicationEditionMigrationJournals.AnyAsync(
                journal => journal.MigrationName == PublicationPressMigrationService.MigrationName));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SharedPublicationLockBlocksPressV14WhileAnotherMigrationOwnerHoldsIt()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var databasePath = Path.Combine(directory, "cross-process-lock.db");
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;
            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
                await db.GetService<IMigrator>().MigrateAsync(PublicationEditionMigrationService.EfMigrationId);
            var connectionString = $"Data Source={databasePath}";
            var lockPath = PublicationMigrationLock.LockPath(connectionString);
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
            await using (var held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
                var migration = Migration(TestConfiguration(databasePath));
                await using var blocked = new AppDbContext(options, NullLogger<AppDbContext>.Instance);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => migration.ApplyPendingAsync(blocked, cancellation.Token));

                await using var unchanged = new AppDbContext(options, NullLogger<AppDbContext>.Instance);
                Assert.DoesNotContain(
                    PublicationPressMigrationService.PreviousSchemaMigrationId,
                    await unchanged.Database.GetAppliedMigrationsAsync());
            }

            var resumedMigration = Migration(TestConfiguration(databasePath));
            await using var resumed = new AppDbContext(options, NullLogger<AppDbContext>.Instance);
            await resumedMigration.ApplyPendingAsync(resumed);
            var applied = await resumed.Database.GetAppliedMigrationsAsync();
            Assert.Contains(PublicationPressMigrationService.PreviousSchemaMigrationId, applied);
            Assert.Contains(PublicationPressMigrationService.EfMigrationId, applied);
            Assert.True(await resumed.PublicationEditionMigrationJournals.AnyAsync(
                journal => journal.MigrationName == PublicationPressMigrationService.MigrationName));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CleanInstallAdvancesIntermediateSchemasBeforeGuardingV15()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var databasePath = Path.Combine(directory, "clean-install.db");
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;
            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
                await db.GetService<IMigrator>().MigrateAsync(PublicationEditionMigrationService.EfMigrationId);

            var migration = Migration(TestConfiguration(databasePath));
            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
                await migration.ApplyPendingAsync(db);

            await using var verified = new AppDbContext(options, NullLogger<AppDbContext>.Instance);
            var applied = await verified.Database.GetAppliedMigrationsAsync();
            Assert.Contains(PublicationPressMigrationService.PreviousSchemaMigrationId, applied);
            Assert.Contains(PublicationPressMigrationService.EfMigrationId, applied);
            Assert.True(await verified.PublicationEditionMigrationJournals.AnyAsync(
                journal => journal.MigrationName == PublicationPressMigrationService.MigrationName));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static IConfiguration TestConfiguration(string databasePath) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = $"Data Source={databasePath}",
            })
            .Build();

    private static PublicationPressMigrationService Migration(IConfiguration configuration) =>
        new(
            configuration,
            new DatabaseMigrationRecoveryService(
                configuration,
                NullLogger<DatabaseMigrationRecoveryService>.Instance),
            NullLogger<PublicationPressMigrationService>.Instance);
}
