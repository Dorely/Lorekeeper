using System.Reflection;
using System.Text.Json;
using Lorekeeper.Chapters;
using Lorekeeper.Context;
using Lorekeeper.EntityVisuals;
using Lorekeeper.ImportExport;
using Lorekeeper.Knowledge;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence;
using Lorekeeper.Persistence.Repositories;
using Lorekeeper.Projects;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class ProjectImportJobIntegrationTests
{
    [Fact]
    public async Task V9JobImportsMarkedFigureManuscriptStylesAndRemapsTheAsset()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance);
        await db.Database.MigrateAsync();

        var project = new Project
        {
            Name = "Import target",
            Slug = $"import-target-{Guid.NewGuid():N}",
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        var projectRepo = new ProjectRepository(db);
        var chapterRepo = new ChapterRepository(db);
        var nodeRepo = new GraphNodeRepository(db);
        var edgeRepo = new GraphEdgeRepository(db);
        var entityTypeRepo = new GraphEntityTypeRepository(db);
        var actRepo = new ActRepository(db);
        var graph = new RelationalGraphStore(nodeRepo, edgeRepo);
        var entityTypeService = new EntityTypeService(entityTypeRepo, nodeRepo);
        var outline = new OutlineGraphSync(
            graph,
            nodeRepo,
            edgeRepo,
            projectRepo,
            actRepo,
            chapterRepo,
            entityTypeService);
        await outline.EnsureProjectAsync(project);
        var mutations = new ProjectMutationCoordinator();
        var gatedMutations = new GateMutationCoordinator(mutations);

        var exportedChapterId = Guid.NewGuid();
        var exportedImageId = Guid.NewGuid();
        var manuscript = new ManuscriptDocument
        {
            ManuscriptId = exportedChapterId,
            Revision = 3,
            Content =
            [
                new ManuscriptBlock
                {
                    Id = "opening",
                    Type = ManuscriptBlockType.Paragraph,
                    StyleRole = "opening-paragraph",
                    Content =
                    [
                        new ManuscriptInline
                        {
                            Text = "Marked opening",
                            Marks =
                            [
                                new ManuscriptMark { Type = ManuscriptMarkType.Strong },
                                new ManuscriptMark
                                {
                                    Type = ManuscriptMarkType.CharacterStyle,
                                    Value = "lead-in",
                                },
                            ],
                        },
                    ],
                },
                new ManuscriptBlock
                {
                    Id = "figure",
                    Type = ManuscriptBlockType.Figure,
                    StyleRole = ManuscriptStyleRoles.FigureCaption,
                    ImageId = exportedImageId,
                    AltText = "A regional map",
                    Content = [new ManuscriptInline { Text = "Eastern road" }],
                },
            ],
        };
        var export = new ProjectExportDocument
        {
            ExportKind = ProjectExportKind.Full,
            Project = new ProjectExportProject(
                Guid.NewGuid(),
                "Exported",
                "exported",
                string.Empty,
                true,
                true),
            Images =
            [
                new ProjectExportImage(
                    exportedImageId,
                    "map.png",
                    "image/png",
                    [1, 2, 3, 4],
                    "A regional map",
                    PublishAssetSource.Uploaded,
                    string.Empty,
                    string.Empty,
                    "{}",
                    null,
                    null,
                    null,
                    null,
                    null,
                    DateTime.UtcNow,
                    DateTime.UtcNow),
            ],
            ManuscriptStyles =
            [
                new ProjectExportManuscriptStyle(
                    Guid.NewGuid(),
                    "Opening paragraph",
                    ManuscriptStyleKind.Paragraph,
                    "opening-paragraph",
                    new ManuscriptStyleProperties(SpaceAfterPoints: 6),
                    1),
                new ProjectExportManuscriptStyle(
                    Guid.NewGuid(),
                    "Lead in",
                    ManuscriptStyleKind.Character,
                    "lead-in",
                    new ManuscriptStyleProperties(SmallCaps: true),
                    1),
            ],
            Chapters =
            [
                new ProjectExportChapter
                {
                    Id = exportedChapterId,
                    Title = "Imported chapter",
                    ManuscriptJson = ManuscriptCodec.Serialize(manuscript),
                    ManuscriptRevision = manuscript.Revision,
                },
            ],
        };
        var job = new ProjectImportJob
        {
            ProjectId = project.Id,
            FileName = "fixture.lorekeeper.json",
            ContentJson = JsonSerializer.Serialize(
                export,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        };
        db.ProjectImportJobs.Add(job);
        await db.SaveChangesAsync();

        var processor = new ProjectImportJobProcessor(
            new ProjectImportRepository(db),
            db,
            projectRepo,
            nodeRepo,
            edgeRepo,
            entityTypeRepo,
            graph,
            DefaultProxy<IActService>(),
            new ImportChapterService(chapterRepo),
            chapterRepo,
            DefaultProxy<IProjectFactService>(),
            entityTypeService,
            outline,
            DefaultProxy<IContextIndexingService>(),
            DefaultProxy<IEntityVisualExampleService>(),
            new BookBriefService(db),
            new ManuscriptStyleService(db, gatedMutations),
            gatedMutations,
            new ProjectImportJobNotifier(),
            NullLogger<ProjectImportJobProcessor>.Instance);

        var importTask = processor.RunAsync(job.Id);
        await gatedMutations.Acquired.WaitAsync(TimeSpan.FromSeconds(5));
        var competingMutations = new ProjectMutationCoordinator();
        var competingLeaseTask = competingMutations.AcquireAsync(project.Id).AsTask();
        await Task.Delay(150);
        Assert.False(competingLeaseTask.IsCompleted);
        gatedMutations.Continue();
        await importTask;
        await using var competingLease = await competingLeaseTask.WaitAsync(TimeSpan.FromSeconds(5));

        db.ChangeTracker.Clear();
        var completed = await db.ProjectImportJobs.AsNoTracking().SingleAsync();
        Assert.True(
            completed.Status == ProjectImportJobStatus.Completed,
            completed.ErrorMessage);
        var imported = await db.Chapters.AsNoTracking().SingleAsync();
        var importedFigure = Assert.Single(
            imported.Manuscript.Content,
            block => block.Type == ManuscriptBlockType.Figure);
        Assert.NotEqual(exportedImageId, importedFigure.ImageId);
        Assert.Equal(
            (await db.PublishAssets.AsNoTracking().SingleAsync()).Id,
            importedFigure.ImageId);
        Assert.Equal("A regional map", importedFigure.AltText);
        Assert.Equal("Eastern road", ManuscriptCodec.Text(importedFigure));
        var styles = await new ManuscriptStyleService(db, mutations).ListAsync(project.Id);
        Assert.Equal(2, styles.Count);
        ManuscriptStyleService.ValidateDocumentReferences(imported.Manuscript, styles);
        Assert.Contains(
            imported.Manuscript.Content.SelectMany(block => block.Content).SelectMany(inline => inline.Marks),
            mark => mark.Type == ManuscriptMarkType.CharacterStyle && mark.Value == "lead-in");
    }

    private static T DefaultProxy<T>() where T : class =>
        DispatchProxy.Create<T, DefaultDispatchProxy>();

    private sealed class GateMutationCoordinator(
        IProjectMutationCoordinator inner) : IProjectMutationCoordinator
    {
        private readonly TaskCompletionSource _acquired =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _continue =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _gated;

        public Task Acquired => _acquired.Task;

        public void Continue() => _continue.TrySetResult();

        public async ValueTask<IAsyncDisposable> AcquireAsync(
            Guid projectId,
            CancellationToken cancellationToken = default)
        {
            var lease = await inner.AcquireAsync(projectId, cancellationToken);
            if (Interlocked.Exchange(ref _gated, 1) != 0)
                return lease;

            _acquired.TrySetResult();
            try
            {
                await _continue.Task.WaitAsync(cancellationToken);
                return lease;
            }
            catch
            {
                await lease.DisposeAsync();
                throw;
            }
        }
    }

    public class DefaultDispatchProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var returnType = targetMethod?.ReturnType ?? typeof(void);
            if (returnType == typeof(Task))
                return Task.CompletedTask;
            if (returnType.IsGenericType
                && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var valueType = returnType.GetGenericArguments()[0];
                return typeof(Task)
                    .GetMethod(nameof(Task.FromResult))!
                    .MakeGenericMethod(valueType)
                    .Invoke(null, [valueType.IsValueType ? Activator.CreateInstance(valueType) : null]);
            }
            return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
        }
    }

    private sealed class ImportChapterService(IChapterRepository chapters) : IChapterService
    {
        public Task<IReadOnlyList<Chapter>> ListAsync(
            Guid projectId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Chapter?> GetAsync(Guid chapterId, CancellationToken cancellationToken = default) =>
            chapters.GetByIdAsync(chapterId, cancellationToken);

        public Task<Chapter?> ReloadFromStoreAsync(
            Guid chapterId,
            CancellationToken cancellationToken = default) =>
            chapters.ReloadFromStoreAsync(chapterId, cancellationToken);

        public async Task<Chapter> CreateAsync(
            Guid projectId,
            Guid? actId = null,
            string? title = null,
            string? synopsis = null,
            Guid? id = null,
            CancellationToken cancellationToken = default)
        {
            var order = await chapters.GetMaxOrderAsync(projectId, actId, cancellationToken) + 1;
            var chapter = new Chapter
            {
                Id = id ?? Guid.NewGuid(),
                ProjectId = projectId,
                ActId = actId,
                Title = title ?? "Imported chapter",
                Synopsis = synopsis ?? string.Empty,
                Order = order,
                ManuscriptRevision = 0,
                VectorIndexState = VectorIndexState.UpToDate,
            };
            chapter.ManuscriptJson = ManuscriptCodec.Serialize(
                ManuscriptCodec.CreateEmpty(chapter.Id, chapter.ManuscriptRevision));
            await chapters.AddAsync(chapter, cancellationToken);
            await chapters.SaveChangesAsync(cancellationToken);
            return chapter;
        }

        public Task<Chapter> UpdateAsync(
            Guid chapterId,
            string? title = null,
            string? synopsis = null,
            ChapterActAssignment? actId = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(Guid chapterId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ReorderAsync(
            Guid projectId,
            Guid? actId,
            IReadOnlyList<Guid> orderedIds,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ReindexAsync(Guid chapterId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
