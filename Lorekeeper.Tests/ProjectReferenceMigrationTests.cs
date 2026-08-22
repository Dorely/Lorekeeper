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
    private const string PreviousMigration = "20260821052001_AddProjectReferences";

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

                // Seed the legacy composite-key row before the forward
                // migration so the cutover is proved to preserve links rather
                // than merely creating a fresh table.
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "ProjectReferences" ("ReferencingProjectId", "ReferencedProjectId", "CreatedAt")
                    VALUES ({firstProjectId}, {secondProjectId}, {DateTime.UtcNow});
                    """);
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
                var reference = await db.ProjectReferences.AsNoTracking().SingleAsync();
                Assert.Equal(firstProjectId, reference.ReferencingProjectId);
                Assert.NotEqual(Guid.Empty, reference.Id);
                Assert.NotEqual(Guid.Empty, reference.ReferencedRepositoryId);
                Assert.Equal(secondProjectId, reference.ReferencedProjectId);
                Assert.Equal(secondProjectId, reference.ResolvedProjectId);
                Assert.Equal("Second volume", reference.ReferencedProjectName);
                Assert.Equal("second-volume", reference.ReferencedProjectSlug);
                Assert.Equal(reference.Id, await db.ProjectReferences
                    .Where(item => item.Id == reference.Id)
                    .Select(item => item.Id)
                    .SingleAsync());
                var referencedRepositoryId = await db.ProjectVersionRepositories
                    .Where(item => item.ProjectId == secondProjectId)
                    .Select(item => item.Id)
                    .SingleAsync();
                Assert.Equal(referencedRepositoryId, await db.ProjectVersionRepositories
                    .Where(item => item.Id == referencedRepositoryId)
                    .Select(item => item.Id)
                    .SingleAsync());
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
    public async Task ProjectReferenceIdentityRejectsSelfLinksAndUnknownResolvedProjects()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance);
        await db.Database.MigrateAsync();

        var project = new Project { Name = "Only project", Slug = "only-project" };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        var repository = new ProjectVersionRepository { ProjectId = project.Id };
        db.ProjectVersionRepositories.Add(repository);
        await db.SaveChangesAsync();

        db.ProjectReferences.Add(new ProjectReference
        {
            ReferencingProjectId = project.Id,
            ReferencedRepositoryId = repository.Id,
            ReferencedProjectId = project.Id,
            ReferencedProjectName = project.Name,
            ReferencedProjectSlug = project.Slug,
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        db.ProjectReferences.Add(new ProjectReference
        {
            ReferencingProjectId = project.Id,
            ReferencedRepositoryId = Guid.NewGuid(),
            ReferencedProjectId = Guid.NewGuid(),
            ResolvedProjectId = Guid.NewGuid(),
            ReferencedProjectName = "Unavailable",
            ReferencedProjectSlug = "unavailable",
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
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
