using System.Data.Common;
using Lorekeeper.Models;
using Lorekeeper.Manuscripts;
using Lorekeeper.Persistence;
using Lorekeeper.Publish;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class PublicationEditionMigrationTests
{
    [Fact]
    public async Task InterruptedV14HistoryWriteRollsBackAndCanRetryCleanly()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var connectionString = $"Data Source={Path.Combine(directory, "fixture.db")}";
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
                await db.GetService<IMigrator>().MigrateAsync("20260801003844_PublishConversationsV13");

            var interruptedOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connectionString)
                .AddInterceptors(new FailV14HistoryWriteInterceptor())
                .Options;
            await using (var db = new AppDbContext(interruptedOptions, NullLogger<AppDbContext>.Instance))
                await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.MigrateAsync());

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                var columns = await PublicationEditionColumnsAsync(db);
                Assert.DoesNotContain("SelectedCoverChapterId", columns);
                Assert.Contains("SelectedCoverImageId", columns);
                Assert.DoesNotContain(
                    "20260801022548_PublicationCoverImagesV14",
                    await db.Database.GetAppliedMigrationsAsync());

                await PublicationEditionMigrationService.ReconcileInterruptedCoverMigrationAsync(db);
                await db.Database.MigrateAsync();
                columns = await PublicationEditionColumnsAsync(db);
                Assert.DoesNotContain("SelectedCoverChapterId", columns);
                Assert.Contains("SelectedCoverImageId", columns);
                Assert.Contains(
                    "20260801022548_PublicationCoverImagesV14",
                    await db.Database.GetAppliedMigrationsAsync());
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
    public async Task PicturePageCoverMigratesToItsSingleProjectImageWithoutChangingSourceData()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var connectionString = $"Data Source={Path.Combine(directory, "fixture.db")}";
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
            var projectId = Guid.NewGuid();
            var chapterId = Guid.NewGuid();
            var imageId = Guid.NewGuid();
            var editionId = Guid.NewGuid();
            var outlineItemId = Guid.NewGuid();
            var missingAssetOutlineItemId = Guid.NewGuid();
            var malformedChapterId = Guid.NewGuid();
            var malformedEditionId = Guid.NewGuid();
            var malformedElementChapterId = Guid.NewGuid();
            var malformedElementEditionId = Guid.NewGuid();
            var missingAssetChapterId = Guid.NewGuid();
            var missingAssetEditionId = Guid.NewGuid();
            var imageBytes = "cover-image-bytes"u8.ToArray();
            var artifactBytes = "%PDF-preserved"u8.ToArray();
            var artifactHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(artifactBytes));
            var manuscript = ManuscriptCodec.FromPlainText(chapterId, "Cover source text");
            var pageLayout = new PicturePageLayout(
                [new PicturePageImageElement(
                    Guid.NewGuid(), imageId, 0, 0, 100, 100, ChapterImageFit.Cover, 1, 0, string.Empty)],
                []);
            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.GetService<IMigrator>().MigrateAsync("20260801003844_PublishConversationsV13");
                var project = new Project { Id = projectId, Name = "Cover migration", Slug = $"cover-{projectId:N}" };
                var image = new PublishAsset
                {
                    Id = imageId,
                    ProjectId = projectId,
                    FileName = "cover.png",
                    ContentType = "image/png",
                    Data = imageBytes,
                };
                var chapter = new Chapter
                {
                    Id = chapterId,
                    ProjectId = projectId,
                    Title = "Legacy cover",
                    ManuscriptJson = ManuscriptCodec.Serialize(manuscript),
                    ManuscriptRevision = manuscript.Revision,
                    VisualMode = ChapterVisualMode.PicturePage,
                    PageLayoutJson = System.Text.Json.JsonSerializer.Serialize(
                        pageLayout,
                        ManuscriptCodec.JsonOptions),
                };
                var malformedManuscript = ManuscriptCodec.FromPlainText(
                    malformedChapterId,
                    "Preserve this too");
                var malformedChapter = new Chapter
                {
                    Id = malformedChapterId,
                    ProjectId = projectId,
                    Title = "Malformed legacy cover",
                    ManuscriptJson = ManuscriptCodec.Serialize(malformedManuscript),
                    ManuscriptRevision = malformedManuscript.Revision,
                    VisualMode = ChapterVisualMode.PicturePage,
                    PageLayoutJson = "Oops, not JSON",
                };
                var malformedElementManuscript = ManuscriptCodec.FromPlainText(
                    malformedElementChapterId,
                    "Preserve this too");
                var malformedElementChapter = new Chapter
                {
                    Id = malformedElementChapterId,
                    ProjectId = projectId,
                    Title = "Malformed legacy cover element",
                    ManuscriptJson = ManuscriptCodec.Serialize(malformedElementManuscript),
                    ManuscriptRevision = malformedElementManuscript.Revision,
                    VisualMode = ChapterVisualMode.PicturePage,
                    PageLayoutJson = "{\"images\":[\"oops\"]}",
                };
                var missingAssetManuscript = ManuscriptCodec.FromPlainText(
                    missingAssetChapterId,
                    "Preserve a partially dangling layout");
                var missingAssetChapter = new Chapter
                {
                    Id = missingAssetChapterId,
                    ProjectId = projectId,
                    Title = "Partially dangling legacy cover",
                    ManuscriptJson = ManuscriptCodec.Serialize(missingAssetManuscript),
                    ManuscriptRevision = missingAssetManuscript.Revision,
                    VisualMode = ChapterVisualMode.PicturePage,
                    PageLayoutJson = System.Text.Json.JsonSerializer.Serialize(
                        new PicturePageLayout(
                        [
                            new PicturePageImageElement(
                                Guid.NewGuid(), imageId, 0, 0, 50, 100, ChapterImageFit.Cover, 1, 0, string.Empty),
                            new PicturePageImageElement(
                                Guid.NewGuid(), Guid.NewGuid(), 50, 0, 50, 100, ChapterImageFit.Cover, 1, 0, string.Empty),
                        ],
                        []),
                        ManuscriptCodec.JsonOptions),
                };
                db.AddRange(
                    project,
                    image,
                    chapter,
                    malformedChapter,
                    malformedElementChapter,
                    missingAssetChapter);
                await db.SaveChangesAsync();
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
                         SelectedCoverChapterId, CreatedAt, UpdatedAt)
                     VALUES (
                         {editionId}, {projectId}, 'Paperback', 'Paperback', 'Generic', 'preview-1',
                         'Draft', 1, 0, '', '', '', 'en', '', '', '', '', 1, 1, 0, 0, 1, 1,
                         0, 0, 'Automatic', 'WholeSpread', 'RequestLandscape', 'PerfectBound',
                         'White', 'BlackAndWhite', 0, 6, 9, 0.75, 11, 1.3,
                         {chapterId}, {DateTime.UtcNow}, {DateTime.UtcNow}),
                     (
                         {malformedEditionId}, {projectId}, 'Malformed', 'Paperback', 'Generic', 'preview-1',
                         'Draft', 0, 0, '', '', '', 'en', '', '', '', '', 1, 1, 0, 0, 1, 1,
                         0, 0, 'Automatic', 'WholeSpread', 'RequestLandscape', 'PerfectBound',
                         'White', 'BlackAndWhite', 0, 6, 9, 0.75, 11, 1.3,
                         {malformedChapterId}, {DateTime.UtcNow}, {DateTime.UtcNow}),
                     (
                         {malformedElementEditionId}, {projectId}, 'Malformed element', 'Paperback', 'Generic', 'preview-1',
                         'Draft', 0, 0, '', '', '', 'en', '', '', '', '', 1, 1, 0, 0, 1, 1,
                         0, 0, 'Automatic', 'WholeSpread', 'RequestLandscape', 'PerfectBound',
                         'White', 'BlackAndWhite', 0, 6, 9, 0.75, 11, 1.3,
                         {malformedElementChapterId}, {DateTime.UtcNow}, {DateTime.UtcNow}),
                     (
                         {missingAssetEditionId}, {projectId}, 'Missing asset', 'Paperback', 'Generic', 'preview-1',
                         'Draft', 0, 0, '', '', '', 'en', '', '', '', '', 1, 1, 0, 0, 1, 1,
                         0, 0, 'Automatic', 'WholeSpread', 'RequestLandscape', 'PerfectBound',
                         'White', 'BlackAndWhite', 0, 6, 9, 0.75, 11, 1.3,
                         {missingAssetChapterId}, {DateTime.UtcNow}, {DateTime.UtcNow});
                     """);
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     INSERT INTO PublicationEditionOutlineItems (
                         Id, EditionId, TargetKind, TargetId, ActId, ChapterId, IsIncluded,
                         SortOrder, CreatedAt, UpdatedAt)
                     VALUES (
                         {outlineItemId}, {editionId}, 'Chapter', {chapterId}, NULL, {chapterId},
                         1, 0, {DateTime.UtcNow}, {DateTime.UtcNow}),
                     (
                         {missingAssetOutlineItemId}, {missingAssetEditionId}, 'Chapter',
                         {missingAssetChapterId}, NULL, {missingAssetChapterId}, 1, 0,
                         {DateTime.UtcNow}, {DateTime.UtcNow});
                     """);
                db.PublicationArtifacts.Add(new PublicationArtifact
                {
                    EditionId = editionId,
                    Kind = PublicationArtifactKind.InteriorPdf,
                    FileName = "preserved.pdf",
                    MediaType = "application/pdf",
                    Data = artifactBytes,
                    Sha256 = artifactHash,
                    ByteLength = artifactBytes.Length,
                    SourceFingerprint = "preserved-source",
                    RendererVersion = "0.2.0",
                    ProfileId = "fixture",
                });
                await db.SaveChangesAsync();
            }

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
                await db.Database.MigrateAsync();

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                var edition = await db.PublicationEditions.AsNoTracking().SingleAsync(candidate => candidate.Id == editionId);
                var malformedEdition = await db.PublicationEditions.AsNoTracking()
                    .SingleAsync(candidate => candidate.Id == malformedEditionId);
                var malformedElementEdition = await db.PublicationEditions.AsNoTracking()
                    .SingleAsync(candidate => candidate.Id == malformedElementEditionId);
                var missingAssetEdition = await db.PublicationEditions.AsNoTracking()
                    .SingleAsync(candidate => candidate.Id == missingAssetEditionId);
                var image = await db.PublishAssets.AsNoTracking().SingleAsync();
                var chapter = await db.Chapters.AsNoTracking().SingleAsync(candidate => candidate.Id == chapterId);
                var malformedChapter = await db.Chapters.AsNoTracking()
                    .SingleAsync(candidate => candidate.Id == malformedChapterId);
                var malformedElementChapter = await db.Chapters.AsNoTracking()
                    .SingleAsync(candidate => candidate.Id == malformedElementChapterId);
                var artifact = await db.PublicationArtifacts.AsNoTracking().SingleAsync();
                Assert.Equal(imageId, edition.SelectedCoverImageId);
                Assert.False((await db.PublicationEditionOutlineItems.AsNoTracking()
                    .SingleAsync(item => item.Id == outlineItemId)).IsIncluded);
                Assert.Equal(imageBytes, image.Data);
                Assert.Equal(ManuscriptCodec.Serialize(manuscript), chapter.ManuscriptJson);
                Assert.Equal(artifactHash, artifact.Sha256);
                Assert.Equal(artifactBytes, artifact.Data);
                Assert.Null(malformedEdition.SelectedCoverImageId);
                Assert.Equal("Oops, not JSON", malformedChapter.PageLayoutJson);
                Assert.False((await db.PublicationEditionOutlineItems.AsNoTracking()
                    .SingleAsync(item => item.EditionId == malformedEditionId
                        && item.ChapterId == malformedChapterId)).IsIncluded);
                Assert.Null(malformedElementEdition.SelectedCoverImageId);
                Assert.Equal("{\"images\":[\"oops\"]}", malformedElementChapter.PageLayoutJson);
                Assert.False((await db.PublicationEditionOutlineItems.AsNoTracking()
                    .SingleAsync(item => item.EditionId == malformedElementEditionId
                        && item.ChapterId == malformedElementChapterId)).IsIncluded);
                Assert.Null(missingAssetEdition.SelectedCoverImageId);
                Assert.False((await db.PublicationEditionOutlineItems.AsNoTracking()
                    .SingleAsync(item => item.Id == missingAssetOutlineItemId)).IsIncluded);
                Assert.Equal(pageLayout.Images.Single().ImageId,
                    System.Text.Json.JsonSerializer.Deserialize<PicturePageLayout>(
                        chapter.PageLayoutJson,
                        ManuscriptCodec.JsonOptions)!.Images.Single().ImageId);
                var rendered = await new PublishService(db, null!, null!, []).GetDocumentAsync(projectId, editionId);
                Assert.DoesNotContain(
                    rendered.Sections.SelectMany(section => section.Chapters),
                    candidate => candidate.Id == chapterId);
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
    public async Task LegacyProfileMigratesWithMatterBackupAndEquivalentOwnershipHash()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var connectionString = $"Data Source={Path.Combine(directory, "fixture.db")}";
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
            var projectId = Guid.NewGuid();
            var profileId = Guid.NewGuid();
            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.GetService<IMigrator>().MigrateAsync(
                    Manuscripts.ManuscriptMigrationService.SchemaV2EfMigrationId);
                db.Projects.Add(new Project
                {
                    Id = projectId,
                    Name = "Migrated book",
                    Slug = $"migrated-{projectId:N}",
                });
                await db.SaveChangesAsync();
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     INSERT INTO PublishProfiles (
                         Id, ProjectId, TitleOverride, Subtitle, Author, Language, Publisher,
                         Copyright, Isbn, Description, Dedication, Acknowledgments, "References",
                         IncludeTableOfContents, IncludeVisibleTableOfContents, IncludeActSynopses,
                         IncludeChapterSynopses, IncludeActHeadings, IncludeChapterHeadings,
                         NumberActs, NumberChapters, TitlePageMode, PrintPicturePageSpreadMode,
                         EpubPicturePageSpreadMode, PageWidthInches, PageHeightInches,
                         PageMarginInches, BodyFontSizePoints, BodyLineHeight,
                         SelectedCoverChapterId, CreatedAt, UpdatedAt)
                     VALUES (
                         {profileId}, {projectId}, 'Legacy title', '', 'Author', 'en', '', '', '', '',
                         'For family', 'With thanks', '', 1, 1, 0, 0, 1, 1, 0, 0,
                         'Automatic', 'WholeSpread', 'RequestLandscape', 6, 9, 0.75, 11, 1.3,
                         NULL, {DateTime.UtcNow}, {DateTime.UtcNow});
                     """);
            }

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = connectionString,
                })
                .Build();
            var migration = new PublicationEditionMigrationService(
                configuration,
                new DatabaseMigrationRecoveryService(
                    configuration,
                    NullLogger<DatabaseMigrationRecoveryService>.Instance),
                NullLogger<PublicationEditionMigrationService>.Instance);
            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
                await migration.ApplyPendingAsync(db);

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
                await db.Database.MigrateAsync();

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                var edition = await db.PublicationEditions.AsNoTracking().SingleAsync();
                Assert.Equal(profileId, edition.Id);
                Assert.Equal(6, edition.PageWidthInches);
                Assert.True(edition.IsDefault);
                var matter = await db.PublicationMatter.AsNoTracking().OrderBy(item => item.Kind).ToListAsync();
                Assert.Equal(2, matter.Count);
                Assert.All(matter, item =>
                    _ = Manuscripts.ManuscriptCodec.Deserialize(item.ManuscriptJson, item.Id, item.Revision));
                var journal = await db.PublicationEditionMigrationJournals.AsNoTracking().SingleAsync();
                Assert.Equal("Completed", journal.Status);
                Assert.Equal(journal.SourceHash, journal.TargetHash);
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

    private static async Task<HashSet<string>> PublicationEditionColumnsAsync(AppDbContext db)
    {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA table_info('PublicationEditions');";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(1));
        return columns;
    }

    private sealed class FailV14HistoryWriteInterceptor : DbCommandInterceptor
    {
        private int _armed = 1;

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            ThrowIfV14HistoryWrite(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfV14HistoryWrite(command);
            return ValueTask.FromResult(result);
        }

        private void ThrowIfV14HistoryWrite(DbCommand command)
        {
            if (Volatile.Read(ref _armed) == 0
                || !command.CommandText.Contains("INSERT INTO \"__EFMigrationsHistory\"", StringComparison.Ordinal)
                || !command.CommandText.Contains("20260801022548_PublicationCoverImagesV14", StringComparison.Ordinal))
            {
                return;
            }

            Interlocked.Exchange(ref _armed, 0);
            throw new InvalidOperationException("Injected interruption before the V14 history write.");
        }
    }
}
