using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class SourceEvidenceMigrationTests
{
    private const string PreviousMigration = "20260814202943_AddPersistentAuthoringHistoryV29";

    [Fact]
    public async Task CurrentMigrationPreservesAndRenamesSourceEvidenceAndChapterLinks()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "source-evidence.db")}")
                .Options;
            var projectId = Guid.NewGuid();
            var sourceId = Guid.NewGuid();
            var chapterId = Guid.NewGuid();
            var characterId = Guid.NewGuid();
            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
                await LegacyProjectSeed.InsertAsync(
                    db,
                    projectId,
                    "Migration fixture",
                    $"fixture-{projectId:N}");
                db.BookBriefs.Add(new BookBrief { ProjectId = projectId });
                var now = DateTime.UtcNow;
                await db.Database.ExecuteSqlInterpolatedAsync($$"""
                    INSERT INTO IngestSources
                        (Id, ProjectId, Title, SourceKind, Description, UserInstructions,
                         SourceText, SourceHash, VectorIndexState, CreatedAt, UpdatedAt)
                    VALUES
                        ({{sourceId}}, {{projectId}}, {{"Canon notes"}}, {{string.Empty}}, {{string.Empty}}, {{string.Empty}},
                         {{"Preserve me."}}, {{"legacy-hash"}}, {{"Stale"}}, {{now}}, {{now}})
                    """);
                var chapter = new GraphNode
                {
                    ProjectId = projectId,
                    NodeType = "Chapter",
                    Key = chapterId.ToString("N"),
                    Label = "Chapter One",
                };
                var character = new GraphNode
                {
                    ProjectId = projectId,
                    NodeType = "Character",
                    Key = characterId.ToString("N"),
                    Label = "Mara",
                    Properties = new Dictionary<string, object?>
                    {
                        [$"canonSource.notes"] = "Preserve this evidence.",
                        ["canonSourceMetaJson"] = $"{{\"notes\":{{\"sourceId\":\"{sourceId:N}\",\"sourceTitle\":\"Canon notes\"}}}}",
                    },
                };
                db.GraphNodes.AddRange(chapter, character);
                await db.SaveChangesAsync();
                db.GraphEdges.Add(new GraphEdge
                {
                    FromNodeId = character.Id,
                    ToNodeId = chapter.Id,
                    EdgeType = "AppearsIn",
                });
                await db.SaveChangesAsync();
            }

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
                await db.Database.MigrateAsync();

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                var character = await db.GraphNodes.AsNoTracking().SingleAsync(node => node.NodeType == "Character");
                Assert.Equal("Preserve this evidence.", character.Properties["sourceEvidence.notes"]?.ToString());
                Assert.True(character.Properties.ContainsKey("sourceEvidenceMetaJson"));
                Assert.DoesNotContain(character.Properties.Keys, key => key.StartsWith("canonSource", StringComparison.OrdinalIgnoreCase));
                Assert.Equal("RelevantTo", (await db.GraphEdges.AsNoTracking().SingleAsync()).EdgeType);

                var brief = await db.BookBriefs.SingleAsync();
                db.BookBriefCanonSources.Add(new BookBriefCanonSource
                {
                    BookBriefId = brief.Id,
                    IngestSourceId = sourceId,
                });
                await db.SaveChangesAsync();
                Assert.Single(await db.BookBriefCanonSources.AsNoTracking().ToListAsync());
                db.IngestSources.Remove(await db.IngestSources.SingleAsync());
                await db.SaveChangesAsync();
                Assert.Empty(await db.BookBriefCanonSources.AsNoTracking().ToListAsync());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}
