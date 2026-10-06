using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Lorekeeper.Composition;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Publish;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class DigitalReleaseCoverTemplateTests
{
    [Theory]
    [InlineData(PublicationEditionFormat.DigitalPdf)]
    [InlineData(PublicationEditionFormat.Epub)]
    public async Task DigitalReleaseInheritingCoreCoverDoesNotRequirePrintArtifactProfile(PublicationEditionFormat format)
    {
        var directory = Path.Combine(Path.GetTempPath(), "lorekeeper-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "digital-cover.db")}")
                .Options;
            var project = new Project { Name = "Digital cover", Slug = "digital-cover" };
            var edition = new PublicationEdition
            {
                ProjectId = project.Id,
                Name = "Ebook",
                Format = format,
                PageWidthInches = 6,
                PageHeightInches = 9,
                PrintArtifactProfileKey = string.Empty,
                InheritsCoreCover = true,
            };
            var coreScene = CoverCompositionFactory.Create(edition, new PublicationCoverDesign { Title = "Core title" });
            await using (var setup = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await setup.Database.MigrateAsync();
                setup.AddRange(
                    project,
                    new PublicationBook { ProjectId = project.Id, Title = "Core title" },
                    new ProjectPageSetup { ProjectId = project.Id },
                    edition,
                    new PublicationBookCoverDesign
                    {
                        ProjectId = project.Id,
                        CompositionSceneJson = JsonSerializer.Serialize(coreScene, ManuscriptCodec.JsonOptions),
                    });
                await setup.SaveChangesAsync();
            }

            var database = new AppDatabaseOperationFactory(
                new TestDbContextFactory(options),
                new AppDatabaseWriteCoordinator(),
                new ProjectMutationCoordinator());
            // Digital templates never consult interior pagination or the Press runtime.
            var service = new PublicationCoverService(
                database,
                DispatchProxy.Create<IPublicationEditionService, UnexpectedCallProxy>(),
                new UnavailablePressRuntime());

            var view = await service.GetAsync(project.Id, edition.Id);

            Assert.Equal(0, view.Template.PageCount);
            Assert.Equal(0, view.Template.SpineWidthInches);
            Assert.Equal(0, view.Template.BleedInches);
            Assert.Equal(6, view.Template.FullWidthInches);
            Assert.Equal(9, view.Template.FullHeightInches);
            Assert.False(string.IsNullOrEmpty(view.Template.Fingerprint));
            var scene = JsonSerializer.Deserialize<CompositionScene>(view.CompositionSceneJson, ManuscriptCodec.JsonOptions)!;
            Assert.Equal(6 * 72, scene.Surface.WidthPoints, 3);
            Assert.Equal(9 * 72, scene.Surface.HeightPoints, 3);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options, NullLogger<AppDbContext>.Instance);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    public class UnexpectedCallProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException($"Digital cover templates must not call {targetMethod?.Name}.");
    }

    private sealed class UnavailablePressRuntime : IPublicationPressRuntime
    {
        public PublicationPressRuntimeReadiness GetReadiness() => throw new InvalidOperationException("Press is unavailable.");

        public PublicationPressDescription GetDescription() => throw new InvalidOperationException("Press is unavailable.");

        public ProcessStartInfo CreateStartInfo(Guid jobId, string jobRoot) => throw new InvalidOperationException("Press is unavailable.");
    }
}
