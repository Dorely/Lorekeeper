using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class ProjectImagePartialMigrationTests
{
    private const string PreviousMigration = "20260826073737_AddProjectImagePartials";

    [Fact]
    public async Task ExistingImageJobsAndPartialsSurviveCascadeMigration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "image-partials.db")}")
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
                .Options;
            var projectId = Guid.NewGuid();
            var jobId = Guid.NewGuid();
            var partialId = Guid.NewGuid();
            var orphanPartialId = Guid.NewGuid();
            var finalImageId = Guid.NewGuid();

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
                db.Projects.Add(new Project
                {
                    Id = projectId,
                    Name = "Image partial migration fixture",
                    Slug = $"image-partial-migration-{projectId:N}",
                });
                db.ProjectImageGenerationJobs.Add(new ProjectImageGenerationJob
                {
                    Id = jobId,
                    ProjectId = projectId,
                    Label = "Preserved generation job",
                    Prompt = "Preserve this prompt",
                    Count = 1,
                    OutputStatesJson = "[]",
                });
                db.PublishAssets.Add(new PublishAsset
                {
                    Id = finalImageId,
                    ProjectId = projectId,
                    Source = PublishAssetSource.Generated,
                    FileName = "final.png",
                    ContentType = "image/png",
                    Data = [4, 5, 6],
                });
                db.ProjectImagePartials.Add(new ProjectImagePartial
                {
                    Id = partialId,
                    ProjectId = projectId,
                    JobId = jobId,
                    OutputIndex = 0,
                    Attempt = 1,
                    PartialImageIndex = 0,
                    FileName = "partial.png",
                    ContentType = "image/png",
                    Data = [1, 2, 3],
                    Width = 1,
                    Height = 1,
                    FinalOutputImageId = finalImageId,
                });
                db.ProjectImagePartials.Add(new ProjectImagePartial
                {
                    Id = orphanPartialId,
                    ProjectId = projectId,
                    JobId = jobId,
                    OutputIndex = 0,
                    Attempt = 2,
                    PartialImageIndex = 0,
                    FileName = "orphan-partial.png",
                    ContentType = "image/png",
                    Data = [7, 8, 9],
                    Width = 1,
                    Height = 1,
                });
                await db.SaveChangesAsync();
            }

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
                await db.Database.MigrateAsync();

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                var job = await db.ProjectImageGenerationJobs.AsNoTracking().SingleAsync(item => item.Id == jobId);
                Assert.Equal("Preserved generation job", job.Label);
                Assert.Equal("Preserve this prompt", job.Prompt);

                var partial = await db.ProjectImagePartials.AsNoTracking().SingleAsync(item => item.Id == partialId);
                Assert.Equal(projectId, partial.ProjectId);
                Assert.Equal(jobId, partial.JobId);
                Assert.Equal(finalImageId, partial.FinalOutputImageId);
            }

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                db.ProjectImagePartials.Add(new ProjectImagePartial
                {
                    ProjectId = projectId,
                    JobId = jobId,
                    OutputIndex = 0,
                    Attempt = 1,
                    PartialImageIndex = 0,
                    FileName = "duplicate.png",
                    ContentType = "image/png",
                    Data = [7, 8, 9],
                    Width = 1,
                    Height = 1,
                });
                await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            }

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.PublishAssets.Where(item => item.Id == finalImageId).ExecuteDeleteAsync();
                Assert.False(await db.ProjectImagePartials.AsNoTracking().AnyAsync(item => item.Id == partialId));
                Assert.True(await db.ProjectImagePartials.AsNoTracking().AnyAsync(item => item.Id == orphanPartialId));
            }

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.ProjectImageGenerationJobs.Where(item => item.Id == jobId).ExecuteDeleteAsync();
                Assert.False(await db.ProjectImagePartials.AsNoTracking().AnyAsync(item => item.Id == orphanPartialId));
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
