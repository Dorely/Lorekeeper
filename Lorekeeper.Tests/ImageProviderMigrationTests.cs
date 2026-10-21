using Lorekeeper.Composition;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class ImageProviderMigrationTests
{
    private const string PreviousMigration = "20261021001554_ChatGptPlanAccounts";

    [Fact]
    public async Task Upgrade_creates_default_chatgpt_image_provider_for_signed_in_codex_account_and_backfills_jobs()
    {
        await WithDatabaseAsync(async options =>
        {
            int codexAccountId;
            Guid jobId;
            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.Database.MigrateAsync();
                var plan = new OpenAiAccount
                {
                    Kind = OpenAiAccountKind.ChatGptPlan,
                    DisplayName = "Plan fixture",
                    ExternalAccountId = "plan-subject-fixture",
                    IssuedClientId = "oaiapp_fixture",
                    PlanUsageGranted = true,
                };
                var codex = new OpenAiAccount
                {
                    Kind = OpenAiAccountKind.Codex,
                    DisplayName = "Codex fixture",
                    ExternalAccountId = "codex-account-fixture",
                };
                db.OpenAiAccounts.AddRange(plan, codex);
                db.OAuthTokens.AddRange(
                    new OAuthToken
                    {
                        OpenAiAccount = plan,
                        AccessToken = "plan-access-fixture",
                        RefreshToken = "plan-refresh-fixture",
                        ExpiresAt = DateTime.UtcNow.AddHours(1),
                    },
                    new OAuthToken
                    {
                        OpenAiAccount = codex,
                        AccessToken = "codex-access-fixture",
                        RefreshToken = "codex-refresh-fixture",
                        ExpiresAt = DateTime.UtcNow.AddHours(1),
                    });
                jobId = SeedJob(db);
                await db.SaveChangesAsync();
                codexAccountId = codex.Id;
                await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            }

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.Database.MigrateAsync();

                var provider = await db.ImageProviders.AsNoTracking().Include(row => row.Models).SingleAsync();
                Assert.Equal("chatgpt-account", provider.Name);
                Assert.Equal(ImageProviderKind.ChatGptAccount, provider.Kind);
                Assert.Equal(codexAccountId, provider.OpenAiAccountId);
                Assert.True(provider.IsDefault);
                Assert.Null(provider.ApiKey);
                Assert.Null(provider.EndpointUrl);

                Assert.Equal(6, provider.Models.Count);
                Assert.All(provider.Models, model =>
                {
                    Assert.Equal(ImageProviderModelOrigin.Bundled, model.Origin);
                    Assert.True(model.SupportsGenerate && model.SupportsEdit && model.SupportsRegionalGuide);
                    Assert.Equal(ImageRasterConstraints.GptImage, ImageRasterConstraints.FromJson(model.RasterConstraintsJson));
                });
                var defaultModel = Assert.Single(provider.Models, model => model.IsDefault);
                Assert.Equal("gpt-image-2.5-flare", defaultModel.ModelId);

                var job = await db.ProjectImageGenerationJobs.AsNoTracking().SingleAsync(row => row.Id == jobId);
                Assert.Equal(provider.Id, job.ImageProviderId);
            }
        });
    }

    [Fact]
    public async Task Upgrade_without_signed_in_codex_account_creates_no_image_provider()
    {
        await WithDatabaseAsync(async options =>
        {
            Guid jobId;
            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.Database.MigrateAsync();
                db.OpenAiAccounts.Add(new OpenAiAccount
                {
                    Kind = OpenAiAccountKind.Codex,
                    DisplayName = "Signed-out Codex fixture",
                    ExternalAccountId = "codex-signed-out-fixture",
                });
                jobId = SeedJob(db);
                await db.SaveChangesAsync();
                await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            }

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.Database.MigrateAsync();

                Assert.Empty(await db.ImageProviders.AsNoTracking().ToListAsync());
                Assert.Empty(await db.ImageProviderModels.AsNoTracking().ToListAsync());
                var job = await db.ProjectImageGenerationJobs.AsNoTracking().SingleAsync(row => row.Id == jobId);
                Assert.Null(job.ImageProviderId);
            }
        });
    }

    private static Guid SeedJob(AppDbContext db)
    {
        var project = new Project { Name = "Image provider fixture", Slug = $"image-provider-{Guid.NewGuid():N}" };
        var job = new ProjectImageGenerationJob
        {
            Project = project,
            Prompt = "A lighthouse at dusk",
            Status = ProjectImageGenerationJobStatus.Succeeded,
            Provider = "openai-account",
            ImageModel = "gpt-image-2.5-flare",
        };
        db.Projects.Add(project);
        db.ProjectImageGenerationJobs.Add(job);
        return job.Id;
    }

    private static async Task WithDatabaseAsync(Func<DbContextOptions<AppDbContext>, Task> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(directory, "image-providers.db")};Pooling=False")
            .Options;
        try
        {
            await test(options);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
