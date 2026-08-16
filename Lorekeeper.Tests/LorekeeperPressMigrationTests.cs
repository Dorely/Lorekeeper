using System.Security.Cryptography;
using System.Text.Json;
using Lorekeeper.Authoring;
using Lorekeeper.Composition;
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

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options, NullLogger<AppDbContext>.Instance);
    }

    [Fact]
    public async Task InstalledPopulatedDatabaseRunsActualStartupMigrationWithoutDataLossOrRecovery()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var databasePath = Path.Combine(directory, "press-cutover.db");
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
                .Options;
            var projectId = Guid.NewGuid();
            var pictureProjectId = Guid.NewGuid();
            var editionId = Guid.NewGuid();
            var unknownEditionId = Guid.NewGuid();
            var completedJobId = Guid.NewGuid();
            var queuedJobId = Guid.NewGuid();
            var artifactId = Guid.NewGuid();
            var actId = Guid.NewGuid();
            var chapterId = Guid.NewGuid();
            var pictureChapterId = Guid.NewGuid();
            var pictureImageId = Guid.NewGuid();
            var pictureImageObjectId = Guid.NewGuid();
            var pictureTextObjectId = Guid.NewGuid();
            var emptyPictureTextObjectId = Guid.NewGuid();
            const string pictureBlockId = "picture-page-story-text";
            const string pictureSecondBlockId = "picture-page-story-text-two";
            var styleId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var matterId = Guid.NewGuid();
            var coverDesignId = Guid.NewGuid();
            var assetBytes = "preserved publish image bytes"u8.ToArray();
            var pictureImageBytes = "preserved picture page image bytes"u8.ToArray();
            var bytes = "%PDF-1.7\nimmutable legacy bytes"u8.ToArray();
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var now = DateTime.UtcNow;
            var emptyJson = "{}";
            var legacyManuscriptJson = ManuscriptCodec.Serialize(
                    ManuscriptCodec.FromPlainText(chapterId, "Existing chapter text.", revision: 7))
                .Replace($"\"schemaVersion\":{ManuscriptDocument.CurrentSchemaVersion}", "\"schemaVersion\":2", StringComparison.Ordinal);
            var pictureManuscriptJson = ManuscriptCodec.Serialize(new ManuscriptDocument
            {
                ManuscriptId = pictureChapterId,
                Revision = 3,
                Content =
                [
                    new ManuscriptBlock
                    {
                        Id = pictureBlockId,
                        Type = ManuscriptBlockType.Paragraph,
                        StyleRole = ManuscriptStyleRoles.Body,
                        Content = [new ManuscriptInline { Text = "The lighthouse shone across the water." }],
                    },
                    new ManuscriptBlock
                    {
                        Id = pictureSecondBlockId,
                        Type = ManuscriptBlockType.Paragraph,
                        StyleRole = ManuscriptStyleRoles.Body,
                        Content = [new ManuscriptInline { Text = "A second paragraph remained in the same legacy frame." }],
                    },
                ],
            }).Replace($"\"schemaVersion\":{ManuscriptDocument.CurrentSchemaVersion}", "\"schemaVersion\":2", StringComparison.Ordinal);
            var pictureLayoutJson = JsonSerializer.Serialize(new PicturePageLayout(
                [
                    new PicturePageImageElement(
                        pictureImageObjectId,
                        pictureImageId,
                        0,
                        0,
                        100,
                        100,
                        ChapterImageFit.Cover,
                        1,
                        9,
                        "A lighthouse shines across dark water."),
                ],
                [
                    new PicturePageTextElement(
                        pictureTextObjectId,
                        string.Empty,
                        55,
                        10,
                        35,
                        25,
                        10,
                        1,
                        "builtin:andika",
                        400,
                        false,
                        32,
                        0,
                        1.15,
                        "#ffffff",
                        "#000000",
                        0,
                        PicturePageTextAlign.Left,
                        ChapterTextVerticalAlign.Top,
                        PicturePageTextShadow.Soft,
                        PicturePageTextRole.Body,
                        [
                            new ManuscriptRangeReference(pictureBlockId, null, null),
                            new ManuscriptRangeReference(pictureSecondBlockId, null, null),
                        ]),
                    new PicturePageTextElement(
                        emptyPictureTextObjectId,
                        string.Empty,
                        5,
                        5,
                        20,
                        10,
                        11,
                        3,
                        "builtin:andika",
                        400,
                        false,
                        12,
                        0,
                        1.2,
                        "#ffffff",
                        "transparent",
                        0,
                        PicturePageTextAlign.Left,
                        ChapterTextVerticalAlign.Top,
                        PicturePageTextShadow.None),
                ]), ManuscriptCodec.JsonOptions);

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
                db.Projects.Add(new Project { Id = projectId, Name = "Existing", Slug = $"existing-{projectId:N}" });
                db.Projects.Add(new Project
                {
                    Id = pictureProjectId,
                    Name = "Installed picture book",
                    Slug = $"picture-{pictureProjectId:N}",
                });
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
                var styleOverrideJson = """{"fontSizePoints":11}""";
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
                    INSERT INTO Chapters (
                        Id, ProjectId, ActId, Title, Synopsis, "Order", VisualMode,
                        IllustrationLayoutJson, PageLayoutJson, PageLayoutKind,
                        ManuscriptJson, ManuscriptRevision, VectorIndexState, VectorIndexError,
                        VectorIndexedAt, CreatedAt, UpdatedAt)
                    VALUES ({pictureChapterId}, {pictureProjectId}, NULL, 'Picture page', '', 0, 'PicturePage',
                        '', {pictureLayoutJson}, 'DoublePortrait', {pictureManuscriptJson}, 3,
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
                db.PublishAssets.Add(new PublishAsset
                {
                    Id = pictureImageId,
                    ProjectId = pictureProjectId,
                    Source = PublishAssetSource.Uploaded,
                    FileName = "picture-page.png",
                    ContentType = "image/png",
                    Data = pictureImageBytes,
                    AltText = "A lighthouse shines across dark water.",
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
                    INSERT INTO PublicationEditionStyleMappings (
                        Id, EditionId, SemanticRole, ManuscriptStyleDefinitionId, OverrideJson,
                        Revision, CreatedAt, UpdatedAt)
                    VALUES ({Guid.NewGuid()}, {editionId}, 'body', {styleId},
                        {styleOverrideJson}, 3, {now}, {now});
                    """);
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO PublicationMatter (
                        Id, EditionId, Location, Kind, Title, ManuscriptJson, Revision,
                        IsIncluded, SortOrder, CreatedAt, UpdatedAt)
                    VALUES ({matterId}, {editionId}, 'Front', 'Dedication', 'Existing dedication',
                        {legacyManuscriptJson}, 4, 1, 1, {now}, {now});
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
            var database = new AppDatabaseOperationFactory(
                new TestDbContextFactory(options),
                new AppDatabaseWriteCoordinator(),
                new ProjectMutationCoordinator());
            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                var startupMigration = new DatabaseStartupMigrationService(
                    database,
                    new ManuscriptMigrationService(
                        configuration,
                        recovery,
                        NullLogger<ManuscriptMigrationService>.Instance),
                    new PublicationEditionMigrationService(
                        configuration,
                        recovery,
                        NullLogger<PublicationEditionMigrationService>.Instance),
                    new PublicationPressMigrationService(
                        configuration,
                        recovery,
                        NullLogger<PublicationPressMigrationService>.Instance),
                    new VisualCompositionMigrationService(
                        recovery,
                        NullLogger<VisualCompositionMigrationService>.Instance),
                    new AuthoringPageMigrationService(
                        recovery,
                        NullLogger<AuthoringPageMigrationService>.Instance),
                    new PublicationCoreMigrationService(
                        database,
                        recovery,
                        NullLogger<PublicationCoreMigrationService>.Instance),
                    new EditionContentMigrationService(
                        recovery,
                        new MigrationManuscriptService(db),
                        NullLogger<EditionContentMigrationService>.Instance),
                    new PublicationSectionMigrationService(
                        recovery,
                        NullLogger<PublicationSectionMigrationService>.Instance),
                    new PrintProductMigrationService(
                        recovery,
                        new PrintProductRegistry(),
                        NullLogger<PrintProductMigrationService>.Instance),
                    recovery);
                Assert.True(await startupMigration.ApplyAsync(), (await recovery.GetStateAsync()).Error);
                var picturePdfPresentation = await db.PublicationBookPdfPresentations.AsTracking()
                    .SingleAsync(item => item.ProjectId == pictureProjectId);
                picturePdfPresentation.AllowDesignedPageOverrides = true;
                await db.SaveChangesAsync();
                Assert.True(await startupMigration.ApplyAsync(), (await recovery.GetStateAsync()).Error);
                db.ChangeTracker.Clear();
                Assert.True((await db.PublicationBookPdfPresentations.AsNoTracking()
                    .SingleAsync(item => item.ProjectId == pictureProjectId)).AllowDesignedPageOverrides);
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

                Assert.Equal("kdp-paperback-v2", edition.VendorProfileVersion);
                Assert.Equal("2026.08.1", edition.PrintRegistryVersion);
                Assert.Equal("kdp-pb-bw-white", edition.PrintProductKey);
                Assert.Equal(PrintFinish.Matte, edition.PrintFinish);
                Assert.Equal(PrintCoverMode.Simplex, edition.PrintCoverMode);
                Assert.Equal("{}", edition.PublicationSectionOrderJson);
                Assert.Equal(10, edition.Revision);
                Assert.True(edition.EditionSpecificContentEnabled);
                Assert.Equal("custom-profile-v9", unknownEdition.VendorProfileVersion);
                Assert.Equal(3, unknownEdition.Revision);
                Assert.True(completed.IsLegacy);
                Assert.Equal("0.2.0", completed.RendererVersion);
                Assert.True(queued.IsLegacy);
                Assert.Equal(PublicationRenderStatus.Queued, queued.Status);
                Assert.Equal("kdp-paperback-v1", queued.ProfileId);
                Assert.Null(queued.StartedAt);
                Assert.True(artifact.IsLegacy);
                Assert.Equal(PublicationArtifactKind.InteriorPdf, artifact.Kind);
                Assert.Equal(artifactId, artifact.Id);
                Assert.Equal(hash, artifact.Sha256);
                Assert.Equal(bytes, artifact.Data);
                Assert.Equal(assetBytes, (await db.PublishAssets.AsNoTracking().SingleAsync(item => item.Id == assetId)).Data);
                var coreBook = await db.PublicationBooks.AsNoTracking().SingleAsync(item => item.ProjectId == projectId);
                Assert.Equal("Existing title", coreBook.Title);
                Assert.Equal("Author", coreBook.Author);
                Assert.Single(await db.PublicationBookOutlineItems.AsNoTracking()
                    .Where(item => item.ProjectId == coreBook.ProjectId)
                    .ToListAsync());
                Assert.Single(await db.PublicationBookMatter.AsNoTracking().ToListAsync());
                Assert.Single(await db.PublicationBookImagePlacements.AsNoTracking().ToListAsync());
                var releaseOutline = Assert.Single(await db.PublicationEditionOutlineItems.AsNoTracking().ToListAsync());
                Assert.Equal(unknownEditionId, releaseOutline.EditionId);
                Assert.False(releaseOutline.IsIncluded);
                var releaseMatter = Assert.Single(await db.PublicationMatter.AsNoTracking().ToListAsync());
                Assert.Equal(unknownEditionId, releaseMatter.EditionId);
                Assert.True(releaseMatter.IsExcluded);
                Assert.True(edition.EditionSpecificContentEnabled);
                Assert.Single(await db.PublicationEditionChapterOverrides.AsNoTracking()
                    .Where(item => item.EditionId == editionId)
                    .ToListAsync());
                Assert.Contains(await db.ManuscriptStyleDefinitions.AsNoTracking().ToListAsync(),
                    item => item.ProjectId == projectId && item.Name.Contains("Paperback", StringComparison.Ordinal));
                Assert.False(await db.Database.SqlQueryRaw<int>(
                    "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = 'PublicationEditionStyleMappings'")
                    .AnyAsync(value => value > 0));
                var releasePlacement = Assert.Single(await db.PublicationImagePlacements.AsNoTracking().ToListAsync());
                Assert.Equal(unknownEditionId, releasePlacement.EditionId);
                Assert.True(releasePlacement.IsExcluded);
                var migratedCover = Assert.Single(await db.PublicationCoverDesigns.AsNoTracking().ToListAsync());
                Assert.Contains("perfect-bound-outside", migratedCover.SurfaceScenesJson, StringComparison.Ordinal);
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
                var resolver = new PublicationEffectiveConfigurationResolver(database);
                Assert.Equal(5, (await resolver.ResolveReleaseAsync(projectId, editionId)).PublicationSections.Count);
                Assert.Equal(3, (await resolver.ResolveReleaseAsync(projectId, unknownEditionId)).PublicationSections.Count);
                Assert.Equal(7, await db.PublicationSections.AsNoTracking().CountAsync(item => item.ProjectId == projectId));
                Assert.Equal(3, await db.PublicationSections.AsNoTracking().CountAsync(item =>
                    item.ProjectId == projectId && item.EditionId == null
                    && item.SystemRole != PublicationSectionSystemRole.None));
                var publicationSectionJournal = await db.ManuscriptMigrationJournals.AsNoTracking()
                    .SingleAsync(item => item.MigrationName == PublicationSectionMigrationService.MigrationName);
                Assert.Equal(ManuscriptMigrationStatus.Completed, publicationSectionJournal.Status);
                Assert.True(File.Exists(publicationSectionJournal.BackupPath));

                var pictureChapter = await db.Chapters.AsNoTracking()
                    .SingleAsync(item => item.Id == pictureChapterId);
                var designedPage = Assert.Single(pictureChapter.Manuscript.Content);
                Assert.Equal(ManuscriptBlockType.DesignedPage, designedPage.Type);
                var compositionId = Assert.IsType<Guid>(designedPage.PageCompositionId);
                var composition = await db.PageCompositions.AsNoTracking()
                    .SingleAsync(item => item.Id == compositionId && item.ProjectId == pictureProjectId);
                Assert.Null(composition.DetachedAt);
                Assert.Equal(
                    "The lighthouse shone across the water.\n\nA second paragraph remained in the same legacy frame.",
                    ManuscriptCodec.ProjectPlainText(
                        composition.SemanticManuscriptJson,
                        composition.Id,
                        composition.Revision));
                var activeVariantId = Assert.IsType<Guid>(composition.ActiveAuthoringVariantId);
                var variant = await db.PageCompositionVariants.AsNoTracking()
                    .SingleAsync(item => item.Id == activeVariantId && item.CompositionId == composition.Id);
                Assert.Null(variant.DetachedAt);
                var scene = JsonSerializer.Deserialize<CompositionScene>(variant.SceneJson, ManuscriptCodec.JsonOptions);
                Assert.NotNull(scene);
                Assert.Equal(CompositionSurfaceKind.FacingSpread, scene.Surface.Kind);
                Assert.Equal(17 * 72, scene.Surface.WidthPoints);
                Assert.Equal(11 * 72, scene.Surface.HeightPoints);
                var image = Assert.Single(scene.Objects, item => item.Kind == CompositionObjectKind.Image);
                Assert.Equal(pictureImageObjectId, image.Id);
                Assert.Equal(pictureImageId, image.ImageId);
                Assert.Equal(FigureImageFit.Cover, image.ImageFit);
                Assert.Equal("A lighthouse shines across dark water.", image.AltText);
                var text = Assert.Single(scene.Objects, item => item.Kind == CompositionObjectKind.Text);
                Assert.Equal(pictureTextObjectId, text.Id);
                Assert.DoesNotContain(scene.Objects, item => item.Id == emptyPictureTextObjectId);
                Assert.Collection(
                    text.ContentReferences,
                    reference => Assert.Equal(pictureBlockId, reference.BlockId),
                    reference => Assert.Equal(pictureSecondBlockId, reference.BlockId));
                Assert.Equal(32, text.FontSizePoints);
                Assert.Equal(CompositionTextShadow.Soft, text.TextShadow);
                var history = new AuthoringHistoryService(database);
                var historyTarget = new AuthoringHistoryTarget(
                    pictureProjectId,
                    AuthoringHistoryDocumentKind.PageComposition,
                    composition.Id);
                var historyState = await history.RecordManualActionAsync(
                    historyTarget,
                    "{\"state\":\"before\"}",
                    "{\"state\":\"after\"}",
                    "Migration history usability check");
                Assert.True(historyState.CanUndo);
                Assert.Single(await db.AuthoringHistoryStreams.AsNoTracking()
                    .Where(item => item.ProjectId == pictureProjectId)
                    .ToListAsync());
                Assert.Single(await db.AuthoringHistoryEntries.AsNoTracking().ToListAsync());
                Assert.Equal(
                    pictureImageBytes,
                    (await db.PublishAssets.AsNoTracking().SingleAsync(item => item.Id == pictureImageId)).Data);
                Assert.False(await db.CompositionMutationStages.AsNoTracking().AnyAsync(item =>
                    item.TargetKind == "page-composition-seed" && item.TargetId == composition.Id));
                var authoringJournal = await db.ManuscriptMigrationJournals.AsNoTracking()
                    .SingleAsync(item => item.MigrationName == AuthoringPageMigrationService.MigrationName);
                Assert.Contains("\"restoredPicturePages\":1", authoringJournal.ValidationReportJson, StringComparison.Ordinal);
                Assert.False((await recovery.GetStateAsync()).RecoveryRequired);
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
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString)
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking).Options;
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
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
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
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
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
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
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

    private sealed class MigrationManuscriptService(AppDbContext db) : IManuscriptService
    {
        public async Task<ManuscriptSnapshot?> GetManuscriptAsync(
            EditorContentTarget target,
            Guid chapterId,
            CancellationToken cancellationToken = default)
        {
            var chapter = await db.Chapters.AsNoTracking().SingleOrDefaultAsync(
                item => item.Id == chapterId, cancellationToken);
            if (chapter is null)
                return null;
            var chapterOverride = !target.IsCore
                ? await db.PublicationEditionChapterOverrides.AsNoTracking().SingleOrDefaultAsync(
                    item => item.EditionId == target.EditionId && item.ChapterId == chapterId,
                    cancellationToken)
                : null;
            var json = chapterOverride?.ManuscriptJson ?? chapter.ManuscriptJson;
            var revision = chapterOverride?.Revision ?? chapter.ManuscriptRevision;
            var document = ManuscriptCodec.Deserialize(json, chapterId, revision);
            var plainText = ManuscriptCodec.ProjectPlainText(document);
            return new ManuscriptSnapshot(chapterId, revision, ManuscriptCodec.HashPlainText(plainText), plainText, document);
        }

        public async Task<ManuscriptMutationResult> ReplaceDocumentAsync(
            EditorContentTarget target,
            Guid chapterId,
            long expectedRevision,
            ManuscriptDocument document,
            CancellationToken cancellationToken = default)
        {
            var chapter = await db.Chapters.SingleAsync(item => item.Id == chapterId, cancellationToken);
            if (target.IsCore)
            {
                chapter.ManuscriptJson = ManuscriptCodec.Serialize(document);
                chapter.ManuscriptRevision = document.Revision;
            }
            else
            {
                var edition = await db.PublicationEditions.SingleAsync(
                    item => item.Id == target.EditionId, cancellationToken);
                var chapterOverride = await db.PublicationEditionChapterOverrides.SingleOrDefaultAsync(
                    item => item.EditionId == edition.Id && item.ChapterId == chapterId,
                    cancellationToken);
                chapterOverride ??= new PublicationEditionChapterOverride
                {
                    EditionId = edition.Id,
                    ChapterId = chapterId,
                    BaseCoreRevision = chapter.ManuscriptRevision,
                    BaseCoreHash = ManuscriptCodec.HashPlainText(ManuscriptCodec.ProjectPlainText(chapter.Manuscript)),
                };
                if (db.Entry(chapterOverride).State == EntityState.Detached)
                    db.PublicationEditionChapterOverrides.Add(chapterOverride);
                chapterOverride.ManuscriptJson = ManuscriptCodec.Serialize(document);
                chapterOverride.Revision = document.Revision;
                chapterOverride.UpdatedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(cancellationToken);
            var snapshot = await GetManuscriptAsync(target, chapterId, cancellationToken)
                ?? throw new InvalidOperationException("The migrated chapter could not be reloaded.");
            return new ManuscriptMutationResult(snapshot, document.Content.Select(item => item.Id).ToList());
        }

        public Task<ManuscriptMutationResult> ApplyAsync(EditorContentTarget target, Guid chapterId, long expectedRevision,
            IReadOnlyList<ManuscriptOperation> operations, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ManuscriptMutationResult> ApplyUnderProjectMutationLeaseAsync(EditorContentTarget target, Guid chapterId,
            long expectedRevision, IReadOnlyList<ManuscriptOperation> operations, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ValidateDocumentReferencesAsync(EditorContentTarget target, Guid chapterId, ManuscriptDocument document,
            IReadOnlyList<ManuscriptStyleView>? styleCatalog = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
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
