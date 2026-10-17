using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class PublicationBlockIdentifierMigrationTests
{
    [Fact]
    public async Task UpgradePreservesExistingMapsAndRoundTripsOpaqueBlockIdentifiers()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(directory, "page-map.db")};Pooling=False")
            .Options;
        var projectId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var entryId = Guid.NewGuid();
        var oldBlockId = Guid.NewGuid();
        string[] newBlockIds = ["opening-paragraph", "段落-é", "Case-Sensitive", "case-sensitive", Guid.NewGuid().ToString("N")];
        try
        {
            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.GetService<IMigrator>().MigrateAsync("20261014234340_RemoveOpenAiAccountEmbeddings");
                db.Projects.Add(new Project { Id = projectId, Name = "Page map migration", Slug = "page-map-migration" });
                db.PublicationRenderJobs.Add(new PublicationRenderJob
                {
                    Id = jobId,
                    ProjectId = projectId,
                    TargetKind = PublicationTargetKind.CoreBook,
                    Status = PublicationRenderStatus.Completed,
                });
                await db.SaveChangesAsync();
                // Use the predecessor's GUID parameter mapping, not the new string model.
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO PublicationPageMapEntries (Id, RenderJobId, ChapterId, BlockId, PageNumber)
                    VALUES ({entryId}, {jobId}, {chapterId}, {oldBlockId}, 17);
                    """);
            }

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                var before = await db.PublicationPageMapEntries.AsNoTracking().SingleAsync();
                await db.Database.MigrateAsync();
                var after = await db.PublicationPageMapEntries.AsNoTracking().SingleAsync();
                Assert.Equal(entryId, after.Id);
                Assert.Equal(jobId, after.RenderJobId);
                Assert.Equal(chapterId, after.ChapterId);
                Assert.Equal(before.BlockId, after.BlockId);
                Assert.Equal(oldBlockId, Guid.Parse(after.BlockId));
                Assert.Equal(17, after.PageNumber);
                foreach (var blockId in newBlockIds)
                    db.PublicationPageMapEntries.Add(new PublicationPageMapEntry
                    {
                        RenderJobId = jobId,
                        ChapterId = chapterId,
                        BlockId = blockId,
                        PageNumber = 18,
                    });
                await db.SaveChangesAsync();
            }

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                var saved = await db.PublicationPageMapEntries.AsNoTracking().ToListAsync();
                Assert.Equal(newBlockIds.Length + 1, saved.Count);
                foreach (var blockId in newBlockIds)
                    Assert.Contains(saved, entry => entry.BlockId == blockId && entry.PageNumber == 18);
                Assert.False(db.Database.HasPendingModelChanges());
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
