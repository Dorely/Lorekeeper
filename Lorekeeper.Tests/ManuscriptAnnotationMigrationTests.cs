using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class ManuscriptAnnotationMigrationTests
{
    private const string PreviousMigration = "20260818064325_AddLlmProviderMaxTokens";

    [Fact]
    public async Task AdditiveMigrationPreservesProjectManuscriptPublicationAndArtifactData()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "annotations.db")}")
                .Options;
            var projectId = Guid.NewGuid();
            var chapterId = Guid.NewGuid();
            var editionId = Guid.NewGuid();
            var artifactId = Guid.NewGuid();
            var manuscript = new ManuscriptDocument
            {
                ManuscriptId = chapterId,
                Revision = 7,
                Content =
                [
                    new ManuscriptBlock
                    {
                        Id = "body",
                        Type = ManuscriptBlockType.Paragraph,
                        Content = [new ManuscriptInline { Text = "Protected manuscript Ω" }],
                    },
                ],
            };

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
                db.Projects.Add(new Project { Id = projectId, Name = "Protected project", Slug = $"protected-{projectId:N}" });
                db.Chapters.Add(new Chapter
                {
                    Id = chapterId,
                    ProjectId = projectId,
                    Title = "Protected chapter",
                    ManuscriptJson = ManuscriptCodec.Serialize(manuscript),
                    ManuscriptRevision = manuscript.Revision,
                });
                db.PublicationEditions.Add(new PublicationEdition
                {
                    Id = editionId,
                    ProjectId = projectId,
                    Name = "Protected edition",
                });
                db.PublicationArtifacts.Add(new PublicationArtifact
                {
                    Id = artifactId,
                    ProjectId = projectId,
                    EditionId = editionId,
                    Kind = PublicationArtifactKind.ReadingPdf,
                    FileName = "protected.pdf",
                    MediaType = "application/pdf",
                    Data = [1, 2, 3, 4],
                    Sha256 = "preserved",
                    ByteLength = 4,
                    SourceFingerprint = "source-fingerprint",
                    PaginationFingerprint = "pagination-fingerprint",
                    RendererVersion = "test",
                    ProfileId = "preview-1",
                });
                await db.SaveChangesAsync();
            }

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
                await db.Database.MigrateAsync();

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                Assert.Equal("Protected project", (await db.Projects.AsNoTracking().SingleAsync()).Name);
                var chapter = await db.Chapters.AsNoTracking().SingleAsync();
                Assert.Equal(7, chapter.ManuscriptRevision);
                Assert.Equal("Protected manuscript Ω", ManuscriptCodec.ProjectPlainText(chapter.Manuscript));
                Assert.Equal("Protected edition", (await db.PublicationEditions.AsNoTracking().SingleAsync()).Name);
                var artifact = await db.PublicationArtifacts.AsNoTracking().SingleAsync();
                Assert.Equal(artifactId, artifact.Id);
                Assert.Equal([1, 2, 3, 4], artifact.Data);
                Assert.Equal("source-fingerprint", artifact.SourceFingerprint);
                Assert.Empty(await db.ManuscriptAnnotations.AsNoTracking().ToListAsync());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}
