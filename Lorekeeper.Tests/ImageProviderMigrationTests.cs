using Lorekeeper.Composition;
using Lorekeeper.Images;
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
    private const string PreDirectEndpointsMigration = "20261023065219_AddWorldWorkingNotes";
    private const string GptImageConstraintsJson =
        "{\"sizeMultiple\":16,\"minimumPixels\":655360,\"maximumPixels\":8294400,\"maximumEdge\":3840,\"maximumAspectRatio\":3,\"targetPixels\":1572864}";

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

                Assert.Single(provider.Models);
                // The full upgrade keeps the established default and its direct Codex capabilities.
                Assert.All(provider.Models, model =>
                {
                    Assert.Equal(ImageProviderModelOrigin.Bundled, model.Origin);
                    AssertCodexBundledCapabilities(model);
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

    [Fact]
    public async Task Upgrade_moves_chatgpt_bundled_models_to_direct_endpoint_capabilities_and_keeps_manual_rows_and_job_history()
    {
        await WithDatabaseAsync(async options =>
        {
            Guid jobId;
            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.Database.MigrateAsync();
                var codex = new OpenAiAccount
                {
                    Kind = OpenAiAccountKind.Codex,
                    DisplayName = "Codex fixture",
                    ExternalAccountId = "codex-account-fixture",
                };
                db.OpenAiAccounts.Add(codex);
                db.ImageProviders.AddRange(
                    new ImageProvider
                    {
                        Name = "chatgpt-account",
                        Kind = ImageProviderKind.ChatGptAccount,
                        OpenAiAccount = codex,
                        IsDefault = true,
                        Models =
                        [
                            LegacyGptImageModel("gpt-image-2.5-flare", ImageProviderModelOrigin.Bundled, isDefault: true),
                            LegacyGptImageModel("gpt-image-custom", ImageProviderModelOrigin.Manual, isDefault: false),
                        ],
                    },
                    new ImageProvider
                    {
                        Name = "openai-images",
                        Kind = ImageProviderKind.OpenAiImagesCompatible,
                        EndpointUrl = "https://api.openai.com/v1",
                        Models = [LegacyGptImageModel("gpt-image-2.5-flare", ImageProviderModelOrigin.Bundled, isDefault: true)],
                    });
                jobId = SeedJob(db);
                await db.SaveChangesAsync();
                await db.GetService<IMigrator>().MigrateAsync(PreDirectEndpointsMigration);
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE ProjectImageGenerationJobs SET MainlineModel = 'gpt-6-sol'; UPDATE ImageProviders SET MainlineModelId = 'gpt-6-sol' WHERE Kind = 'ChatGptAccount';");
            }

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.Database.MigrateAsync();

                var providers = await db.ImageProviders.AsNoTracking().Include(row => row.Models).ToListAsync();
                var chatGpt = Assert.Single(providers, row => row.Kind == ImageProviderKind.ChatGptAccount);
                AssertCodexBundledCapabilities(Assert.Single(chatGpt.Models, row => row.Origin == ImageProviderModelOrigin.Bundled));
                var manual = Assert.Single(chatGpt.Models, row => row.Origin == ImageProviderModelOrigin.Manual);
                Assert.True(manual.SupportsStreaming);
                Assert.Equal(GptImageConstraintsJson, manual.RasterConstraintsJson);
                var compatible = Assert.Single(Assert.Single(providers, row => row.Kind == ImageProviderKind.OpenAiImagesCompatible).Models);
                Assert.True(compatible.SupportsStreaming);
                Assert.Equal(GptImageConstraintsJson, compatible.RasterConstraintsJson);

                var job = await db.ProjectImageGenerationJobs.AsNoTracking().SingleAsync(row => row.Id == jobId);
                Assert.Equal("openai-account", job.Provider);
                Assert.Equal("gpt-image-2.5-flare", job.ImageModel);
                Assert.Equal("A lighthouse at dusk", job.Prompt);
            }
        });
    }

    [Fact]
    public async Task Upgrade_removes_automatically_added_models_but_preserves_defaults_manual_configuration_and_job_history()
    {
        await WithDatabaseAsync(async options =>
        {
            int providerId;
            int defaultModelId;
            int manualModelId;
            Guid jobId;
            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.GetService<IMigrator>().MigrateAsync("20261023204840_RemoveImageMasks");
                var provider = new ImageProvider
                {
                    Name = "openrouter",
                    Kind = ImageProviderKind.OpenRouter,
                    ApiKey = "image-key-fixture",
                    ConfigurationJson = "{\"fixture\":true}",
                    IsDefault = true,
                    Models =
                    [
                        LegacyGptImageModel("selected", ImageProviderModelOrigin.Discovered, isDefault: true),
                        LegacyGptImageModel("manually-saved", ImageProviderModelOrigin.Manual, isDefault: false),
                        LegacyGptImageModel("auto-discovered", ImageProviderModelOrigin.Discovered, isDefault: false),
                        LegacyGptImageModel("auto-bundled", ImageProviderModelOrigin.Bundled, isDefault: false),
                    ],
                };
                db.ImageProviders.Add(provider);
                db.ImageProviders.Add(new ImageProvider
                {
                    Name = "no-default",
                    Kind = ImageProviderKind.OpenAiImagesCompatible,
                    EndpointUrl = "http://localhost:9999/v1",
                    Models = [LegacyGptImageModel("saved-without-default", ImageProviderModelOrigin.Manual, isDefault: false)],
                });
                jobId = SeedJob(db);
                await db.SaveChangesAsync();
                providerId = provider.Id;
                defaultModelId = provider.Models.Single(model => model.IsDefault).Id;
                manualModelId = provider.Models.Single(model => model.Origin == ImageProviderModelOrigin.Manual).Id;
                var job = await db.ProjectImageGenerationJobs.AsTracking().SingleAsync(row => row.Id == jobId);
                job.ImageProviderId = providerId;
                job.ImageModel = "auto-discovered";
                job.RawProviderResponseJson = "{\"audit\":\"preserve\"}";
                await db.SaveChangesAsync();
            }

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.Database.MigrateAsync();
                await db.Database.MigrateAsync();

                var provider = await db.ImageProviders.AsNoTracking().Include(row => row.Models).SingleAsync(row => row.Id == providerId);
                Assert.True(provider.IsDefault);
                Assert.Equal("image-key-fixture", provider.ApiKey);
                Assert.Equal("{\"fixture\":true}", provider.ConfigurationJson);
                Assert.Equal(2, provider.Models.Count);
                var selected = Assert.Single(provider.Models, row => row.IsDefault);
                Assert.Equal(defaultModelId, selected.Id);
                Assert.Equal("selected", selected.ModelId);
                var manual = Assert.Single(provider.Models, row => row.Id == manualModelId);
                Assert.False(manual.IsDefault);
                Assert.Equal(GptImageConstraintsJson, manual.RasterConstraintsJson);
                Assert.True(manual.SupportsStreaming);
                var noDefault = await db.ImageProviders.AsNoTracking().Include(row => row.Models).SingleAsync(row => row.Name == "no-default");
                Assert.False(noDefault.IsDefault);
                Assert.False(Assert.Single(noDefault.Models).IsDefault);

                var job = await db.ProjectImageGenerationJobs.AsNoTracking().SingleAsync(row => row.Id == jobId);
                Assert.Equal(providerId, job.ImageProviderId);
                Assert.Equal("auto-discovered", job.ImageModel);
                Assert.Equal(ProjectImageGenerationJobStatus.Succeeded, job.Status);
                Assert.Equal("A lighthouse at dusk", job.Prompt);
                Assert.Equal("{\"audit\":\"preserve\"}", job.RawProviderResponseJson);
            }
        });
    }

    private static ImageProviderModel LegacyGptImageModel(string modelId, ImageProviderModelOrigin origin, bool isDefault) => new()
    {
        ModelId = modelId,
        Origin = origin,
        QualitiesJson = "[\"auto\",\"low\",\"medium\",\"high\",\"xhigh\",\"max\"]",
        OutputFormatsJson = "[\"png\",\"jpeg\",\"webp\"]",
        SupportsGenerate = true,
        SupportsEdit = true,
        MaxReferenceImages = 16,
        SupportsTransparentBackground = true,
        SupportsStreaming = true,
        RasterConstraintsJson = GptImageConstraintsJson,
        IsDefault = isDefault,
    };

    /// <summary>The migrated row must describe exactly what the ChatGPT adapter now lists for its bundled models.</summary>
    private static void AssertCodexBundledCapabilities(ImageProviderModel model)
    {
        var expected = ImageProviderService.ToModel(ProjectImageModelCatalog.CodexImageModels[0]);
        Assert.Equal(expected.QualitiesJson, model.QualitiesJson);
        Assert.Equal(expected.OutputFormatsJson, model.OutputFormatsJson);
        Assert.True(model.SupportsGenerate && model.SupportsEdit && model.SupportsTransparentBackground);
        Assert.False(model.SupportsStreaming);
        Assert.Equal(16, model.MaxReferenceImages);
        Assert.Equal(expected.RasterConstraintsJson, model.RasterConstraintsJson);
        Assert.Equal(ImageRasterConstraints.CodexImage, ImageRasterConstraints.FromJson(model.RasterConstraintsJson));
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
