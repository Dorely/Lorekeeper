using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class ProjectReferenceMigrationTests
{
    private const string PreviousMigration = "20260820230218_AddLatestAssistantReviewBaseline";

    [Fact]
    public async Task ProjectReferenceMigrationPreservesPopulatedProjectsAndAddsValidLinks()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "project-references.db")}")
                .Options;
            var firstProjectId = Guid.NewGuid();
            var secondProjectId = Guid.NewGuid();
            var firstActId = Guid.NewGuid();
            var secondActId = Guid.NewGuid();
            var firstChapterId = Guid.NewGuid();
            var secondChapterId = Guid.NewGuid();

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);

                var firstProject = new Project
                {
                    Id = firstProjectId,
                    Name = "First volume",
                    Slug = "first-volume",
                    ProjectGuidance = "Preserve the first volume's canon.",
                };
                var secondProject = new Project
                {
                    Id = secondProjectId,
                    Name = "Second volume",
                    Slug = "second-volume",
                    ProjectGuidance = "Continue the established world.",
                };
                db.Projects.AddRange(firstProject, secondProject);
                db.BookBriefs.AddRange(
                    new BookBrief { ProjectId = firstProjectId, Premise = "The first volume premise." },
                    new BookBrief { ProjectId = secondProjectId, Premise = "The second volume premise." });
                db.Acts.AddRange(
                    new Act { Id = firstActId, ProjectId = firstProjectId, Title = "First act" },
                    new Act { Id = secondActId, ProjectId = secondProjectId, Title = "Second act" });
                db.Chapters.AddRange(
                    Chapter(firstProjectId, firstActId, firstChapterId, "First chapter"),
                    Chapter(secondProjectId, secondActId, secondChapterId, "Second chapter"));
                db.GraphNodes.AddRange(
                    new GraphNode
                    {
                        ProjectId = firstProjectId,
                        NodeType = "Character",
                        Key = "mara",
                        Label = "Mara",
                        Properties = new Dictionary<string, object?> { ["role"] = "protagonist" },
                    },
                    new GraphNode
                    {
                        ProjectId = secondProjectId,
                        NodeType = "Character",
                        Key = "oren",
                        Label = "Oren",
                        Properties = new Dictionary<string, object?> { ["role"] = "successor" },
                    });
                await db.SaveChangesAsync();
            }

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
                await db.Database.MigrateAsync();

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                Assert.Equal(2, await db.Projects.CountAsync());
                Assert.Equal("First volume", (await db.Projects.AsNoTracking().SingleAsync(project => project.Id == firstProjectId)).Name);
                Assert.Equal("The first volume premise.", await db.BookBriefs.AsNoTracking().Where(brief => brief.ProjectId == firstProjectId).Select(brief => brief.Premise).SingleAsync());
                Assert.Equal("First chapter", await db.Chapters.AsNoTracking().Where(chapter => chapter.ProjectId == firstProjectId).Select(chapter => chapter.Title).SingleAsync());
                Assert.Equal("Mara", await db.GraphNodes.AsNoTracking().Where(node => node.ProjectId == firstProjectId).Select(node => node.Label).SingleAsync());
                Assert.Empty(await db.ProjectReferences.AsNoTracking().ToListAsync());

                db.ProjectReferences.Add(new ProjectReference
                {
                    ReferencingProjectId = firstProjectId,
                    ReferencedProjectId = secondProjectId,
                });
                await db.SaveChangesAsync();

                var reference = await db.ProjectReferences.AsNoTracking().SingleAsync();
                Assert.Equal(firstProjectId, reference.ReferencingProjectId);
                Assert.Equal(secondProjectId, reference.ReferencedProjectId);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static Chapter Chapter(Guid projectId, Guid actId, Guid chapterId, string title)
    {
        var manuscript = ManuscriptCodec.CreateEmpty(chapterId);
        return new Chapter
        {
            Id = chapterId,
            ProjectId = projectId,
            ActId = actId,
            Title = title,
            ManuscriptJson = ManuscriptCodec.Serialize(manuscript),
            ManuscriptRevision = manuscript.Revision,
        };
    }
}
