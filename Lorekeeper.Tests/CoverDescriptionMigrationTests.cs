using System.Text.Json;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class CoverDescriptionMigrationTests
{
    private const string PreviousMigration = "20260831182415_PermanentPublicationImageUpscalingV35";

    [Fact]
    public async Task MigrationRewritesCoverBindingsAndDropsDuplicateCopy()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "cover-description.db")}")
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
                .Options;
            var projectId = Guid.NewGuid();
            var editionId = Guid.NewGuid();
            var coverId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            const string sceneJson = "{\"textBinding\":\"Back {{backCopy}}\"}";
            var surfaceScenesJson = JsonSerializer.Serialize(
                new Dictionary<string, string>
                {
                    ["perfect-bound-outside"] = "{\"textBinding\":\"backCopy\"}",
                },
                ManuscriptCodec.JsonOptions);

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO Projects
                        (Id, ReviewEditsEnabled, ContestModeEnabled, CreatedAt,
                         IncludeCurrentChapterInContext, Name, ProjectGuidance, Slug, UpdatedAt)
                    VALUES
                        ({projectId}, 1, 0, {now}, 1, 'Cover description migration fixture', '',
                         {$"cover-description-{projectId:N}"}, {now});
                    """);
                db.PublicationEditions.Add(new PublicationEdition
                {
                    Id = editionId,
                    ProjectId = projectId,
                    Name = "Paperback",
                    Format = PublicationEditionFormat.Paperback,
                    Description = "Visible description",
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                await db.SaveChangesAsync();
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO PublicationCoverDesigns
                        (Id, EditionId, InheritsCoreFront, Title, Subtitle, Author, SpineText, BackCopy,
                         SpineReadingDirection, BackgroundColor, BarcodeMode, ImageCropXPercent,
                         ImageCropYPercent, AcknowledgedTemplateFingerprint, CompositionSceneJson,
                         SurfaceScenesJson, Revision, CreatedAt, UpdatedAt)
                    VALUES
                        ({coverId}, {editionId}, 0, 'Book', '', 'Author', '', 'Invisible copy',
                         'TopToBottom', '#ffffff', 'VendorOverlay', 50, 50, '', {sceneJson},
                         {surfaceScenesJson}, 1, {now}, {now});
                    """);
            }

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
                await db.Database.MigrateAsync();

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                var cover = await db.PublicationCoverDesigns.AsNoTracking().SingleAsync();
                Assert.Contains("{{description}}", cover.CompositionSceneJson, StringComparison.Ordinal);
                Assert.Contains("description", cover.SurfaceScenesJson, StringComparison.Ordinal);
                Assert.DoesNotContain("backCopy", cover.CompositionSceneJson, StringComparison.Ordinal);
                Assert.DoesNotContain("backCopy", cover.SurfaceScenesJson, StringComparison.Ordinal);

                var columns = new HashSet<string>(StringComparer.Ordinal);
                var connection = db.Database.GetDbConnection();
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA table_info('PublicationCoverDesigns');";
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
                Assert.DoesNotContain("BackCopy", columns);
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
