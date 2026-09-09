using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class ProjectImageJobBackgroundMigrationTests
{
    private const string PreviousMigration = "20260902203800_PurgeTerminalJobsWithChildren";

    [Fact]
    public async Task ExistingImageJobsAndAssetsSurviveBackgroundColumnMigration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "image-job-background.db")}")
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
                .Options;
            var projectId = Guid.NewGuid();
            var jobId = Guid.NewGuid();
            var imageId = Guid.NewGuid();
            var unrelatedProjectId = Guid.NewGuid();

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
                db.Projects.Add(new Project
                {
                    Id = projectId,
                    Name = "Background migration fixture",
                    Slug = $"background-{projectId:N}",
                });
                db.Projects.Add(new Project
                {
                    Id = unrelatedProjectId,
                    Name = "Unrelated project",
                    Slug = $"unrelated-{unrelatedProjectId:N}",
                });
                await db.SaveChangesAsync();
                db.PublishAssets.Add(new PublishAsset
                {
                    Id = imageId,
                    ProjectId = projectId,
                    Source = PublishAssetSource.Generated,
                    FileName = "existing.png",
                    ContentType = "image/png",
                    Data = [137, 80, 78, 71],
                    GenerationModel = "gpt-image-2",
                });
                var outputImageIds = $"[\"{imageId}\"]";
                await db.Database.ExecuteSqlInterpolatedAsync($$"""
                    INSERT INTO ProjectImageGenerationJobs
                    (Id, ProjectId, Kind, Status, Label, Prompt, BriefJson, ReferenceManifestJson,
                     TargetGeometryJson, ProviderRevisedPromptsJson, Size, Quality, OutputFormat,
                     OutputCompression, Count, AltText, SourceImageId, MaskId, ReferenceImageIdsJson,
                     EntityVisualTargetsJson, InheritSourceEntityTargets, OutputImageIdsJson,
                     OutputStatesJson, OutputErrorsJson, Provider, MainlineModel, ImageModel,
                     RawProviderResponseJson, Error, CreatedAt, UpdatedAt, StartedAt, CompletedAt)
                    VALUES ({{jobId}}, {{projectId}}, 'Generate', 'Succeeded', 'Existing image job',
                            'Preserve this prompt', '{}', '[]', '{}', '[]', '1024x1024', 'high',
                            'png', NULL, 1, '', NULL, NULL, '[]', '[]', 0,
                            {{outputImageIds}}, '[]', '[]', 'Codex', 'gpt-5.6-sol',
                            'gpt-image-2', '', '', datetime('now'), datetime('now'), NULL, NULL)
                    """);
                await db.SaveChangesAsync();
            }

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
                await db.Database.MigrateAsync();

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                var job = await db.ProjectImageGenerationJobs.AsNoTracking().SingleAsync(item => item.Id == jobId);
                Assert.Equal(projectId, job.ProjectId);
                Assert.Equal("gpt-image-2", job.ImageModel);
                Assert.Equal("high", job.Quality);
                Assert.Equal("png", job.OutputFormat);
                Assert.Equal("auto", job.Background);
                Assert.Equal("Preserve this prompt", job.Prompt);
                Assert.Equal($"[\"{imageId}\"]", job.OutputImageIdsJson);

                var image = await db.PublishAssets.AsNoTracking().SingleAsync(item => item.Id == imageId);
                Assert.Equal(projectId, image.ProjectId);
                Assert.Equal("gpt-image-2", image.GenerationModel);
                Assert.Equal("existing.png", image.FileName);
                Assert.Equal("image/png", image.ContentType);
                Assert.Equal(new byte[] { 137, 80, 78, 71 }, image.Data);
                Assert.True(await db.Projects.AsNoTracking().AnyAsync(item => item.Id == unrelatedProjectId));
                Assert.Equal("Unrelated project", await db.Projects.AsNoTracking().Where(item => item.Id == unrelatedProjectId).Select(item => item.Name).SingleAsync());
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
