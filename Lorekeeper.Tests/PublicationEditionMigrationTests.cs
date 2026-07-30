using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Publish;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class PublicationEditionMigrationTests
{
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
                NullLogger<PublicationEditionMigrationService>.Instance);
            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
                await migration.ApplyPendingAsync(db);

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
}
