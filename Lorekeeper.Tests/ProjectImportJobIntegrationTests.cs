using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Lorekeeper.Chapters;
using Lorekeeper.Context;
using Lorekeeper.EntityVisuals;
using Lorekeeper.Fonts;
using Lorekeeper.ImportExport;
using Lorekeeper.Knowledge;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence;
using Lorekeeper.Persistence.Repositories;
using Lorekeeper.Projects;
using Lorekeeper.Publish;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace Lorekeeper.Tests;

public sealed class ProjectImportJobIntegrationTests
{
    private static readonly ConditionalWeakTable<AppDbContext, IAppDatabaseOperationFactory> Databases = new();

    private sealed class TestDbContextFactory(AppDbContext source) : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(source.Database.GetDbConnection())
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options;

        public AppDbContext CreateDbContext() => new(_options, NullLogger<AppDbContext>.Instance);
    }

    private static IAppDatabaseOperationFactory Database(
        AppDbContext db,
        IProjectMutationCoordinator? projectMutations = null) => projectMutations is null
            ? Databases.GetValue(
                db,
                source => new AppDatabaseOperationFactory(
                    new TestDbContextFactory(source),
                    new AppDatabaseWriteCoordinator(),
                    new ProjectMutationCoordinator()))
            : new AppDatabaseOperationFactory(
                new TestDbContextFactory(db),
                new AppDatabaseWriteCoordinator(),
                projectMutations);

    private sealed class TestWebHostEnvironment(string webRootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Lorekeeper.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new PhysicalFileProvider(webRootPath);
        public string WebRootPath { get; set; } = webRootPath;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = Directory.GetParent(webRootPath)!.FullName;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(Directory.GetParent(webRootPath)!.FullName);
    }

    [Fact]
    public async Task V12CoverImportPreservesLegacyBodyExclusionAndClearsAmbiguousArtwork()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking).Options;
        await using var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance);
        await db.Database.MigrateAsync();
        var project = new Project { Name = "Legacy import", Slug = $"legacy-{Guid.NewGuid():N}" };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        var imageId = Guid.NewGuid();
        var secondImageId = Guid.NewGuid();
        var validChapterId = Guid.NewGuid();
        var ambiguousChapterId = Guid.NewGuid();
        var validManuscript = ManuscriptCodec.CreateEmpty(validChapterId);
        var ambiguousManuscript = ManuscriptCodec.CreateEmpty(ambiguousChapterId);
        var imageBytes = TinyPng();
        var document = new ProjectExportDocument
        {
            FormatVersion = 12,
            ExportKind = ProjectExportKind.Full,
            Project = new ProjectExportProject(Guid.NewGuid(), "Legacy", "legacy", string.Empty, true, true),
            Images =
            [
                ExportImage(imageId, "cover.png", imageBytes),
                ExportImage(secondImageId, "alternate.png", imageBytes),
            ],
            Chapters =
            [
                ExportPicturePage(validChapterId, "Legacy cover", validManuscript,
                    new PicturePageLayout([PictureElement(imageId)], [])),
                ExportPicturePage(ambiguousChapterId, "Ambiguous cover", ambiguousManuscript,
                    new PicturePageLayout([PictureElement(imageId), PictureElement(secondImageId)], [])),
            ],
            PublicationEditions =
            [
                ExportEdition(
                    "Converted",
                    null,
                    validChapterId,
                    [
                        new ProjectExportEditionOutlineItem(Guid.NewGuid(), PublishOutlineTargetKind.Chapter, validChapterId, true, 0),
                        new ProjectExportEditionOutlineItem(Guid.NewGuid(), PublishOutlineTargetKind.Chapter, ambiguousChapterId, true, 1),
                    ]),
                ExportEdition(
                    "Ambiguous",
                    null,
                    ambiguousChapterId,
                    [
                        new ProjectExportEditionOutlineItem(Guid.NewGuid(), PublishOutlineTargetKind.Chapter, validChapterId, true, 0),
                        new ProjectExportEditionOutlineItem(Guid.NewGuid(), PublishOutlineTargetKind.Chapter, ambiguousChapterId, true, 1),
                    ]),
            ],
        };
        var job = AddImportJob(db, project.Id, document);
        await db.SaveChangesAsync();

        var processor = await CreateProcessorAsync(db, project);
        await processor.RunAsync(job.Id);

        db.ChangeTracker.Clear();
        var completed = await db.ProjectImportJobs.AsNoTracking().SingleAsync();
        Assert.True(
            completed.Status == ProjectImportJobStatus.Completed,
            completed.ErrorMessage);
        var images = await db.PublishAssets.AsNoTracking().ToListAsync();
        var converted = await db.PublicationEditions.AsNoTracking().SingleAsync(edition => edition.Name == "Converted");
        var ambiguous = await db.PublicationEditions.AsNoTracking().SingleAsync(edition => edition.Name == "Ambiguous");
        Assert.Equal(images.Single(image => image.FileName == "cover.png").Id, converted.SelectedCoverImageId);
        Assert.Null(ambiguous.SelectedCoverImageId);
        Assert.All(images, image => Assert.Equal(imageBytes, image.Data));
        var importedValidChapter = await db.Chapters.AsNoTracking().SingleAsync(chapter => chapter.Title == "Legacy cover");
        var importedAmbiguousChapter = await db.Chapters.AsNoTracking().SingleAsync(chapter => chapter.Title == "Ambiguous cover");
        var resolver = new PublicationEffectiveConfigurationResolver(Database(db));
        var convertedOutline = (await resolver.ResolveReleaseAsync(project.Id, converted.Id)).OutlineItems;
        var ambiguousOutline = (await resolver.ResolveReleaseAsync(project.Id, ambiguous.Id)).OutlineItems;
        Assert.False(convertedOutline.Single(item => item.ChapterId == importedValidChapter.Id).IsIncluded);
        Assert.True(convertedOutline.Single(item => item.ChapterId == importedAmbiguousChapter.Id).IsIncluded);
        Assert.False(ambiguousOutline.Single(item => item.ChapterId == importedAmbiguousChapter.Id).IsIncluded);
        Assert.True(ambiguousOutline.Single(item => item.ChapterId == importedValidChapter.Id).IsIncluded);
        Assert.DoesNotContain(convertedOutline, item =>
            item.ChapterId == importedValidChapter.Id && item.IsIncluded);
    }

    [Fact]
    public async Task V13CoverImageImportsItsBinaryAndRemapsTheEditionReference()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking).Options;
        await using var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance);
        await db.Database.MigrateAsync();
        var project = new Project { Name = "Current import", Slug = $"current-{Guid.NewGuid():N}" };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        var imageId = Guid.NewGuid();
        var imageBytes = TinyPng();
        var document = new ProjectExportDocument
        {
            FormatVersion = 13,
            ExportKind = ProjectExportKind.Full,
            Project = new ProjectExportProject(Guid.NewGuid(), "Current", "current", string.Empty, true, true),
            Images = [ExportImage(imageId, "cover.png", imageBytes)],
            PublicationEditions = [ExportEdition("Current", imageId, null, [])],
        };
        var job = AddImportJob(db, project.Id, document);
        await db.SaveChangesAsync();

        var processor = await CreateProcessorAsync(db, project);
        await processor.RunAsync(job.Id);

        db.ChangeTracker.Clear();
        var completed = await db.ProjectImportJobs.AsNoTracking().SingleAsync();
        Assert.True(
            completed.Status == ProjectImportJobStatus.Completed,
            completed.ErrorMessage);
        var importedImage = await db.PublishAssets.AsNoTracking().SingleAsync();
        var importedEdition = await db.PublicationEditions.AsNoTracking().SingleAsync();
        Assert.NotEqual(imageId, importedImage.Id);
        Assert.Equal(imageBytes, importedImage.Data);
        Assert.Equal(importedImage.Id, importedEdition.SelectedCoverImageId);
    }

    [Fact]
    public async Task V9ProfileCoverImportKeepsItsFallbackOutlineRowExcluded()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking).Options;
        await using var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance);
        await db.Database.MigrateAsync();
        var project = new Project { Name = "V9 import", Slug = $"v9-{Guid.NewGuid():N}" };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        var imageId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var manuscript = ManuscriptCodec.CreateEmpty(chapterId);
        var document = new ProjectExportDocument
        {
            FormatVersion = 9,
            ExportKind = ProjectExportKind.Full,
            Project = new ProjectExportProject(Guid.NewGuid(), "V9", "v9", string.Empty, true, true),
            Images = [ExportImage(imageId, "cover.png", TinyPng())],
            Chapters =
            [
                ExportPicturePage(
                    chapterId,
                    "Old cover",
                    manuscript,
                    new PicturePageLayout([PictureElement(imageId)], [])),
            ],
            LegacyPublishProfiles =
            [
                new ProjectExportLegacyPublishProfile(
                    Guid.NewGuid(), string.Empty, string.Empty, "Author", "en", string.Empty,
                    string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
                    true, true, false, false, true, true, false, false,
                    PublishTitlePageMode.Automatic, PrintPicturePageSpreadMode.WholeSpread,
                    EpubPicturePageSpreadMode.RequestLandscape, 6, 9, 0.75, 11, 1.3, chapterId),
            ],
        };
        var job = AddImportJob(db, project.Id, document);
        await db.SaveChangesAsync();

        var processor = await CreateProcessorAsync(db, project);
        await processor.RunAsync(job.Id);

        db.ChangeTracker.Clear();
        var completed = await db.ProjectImportJobs.AsNoTracking().SingleAsync();
        Assert.True(completed.Status == ProjectImportJobStatus.Completed, completed.ErrorMessage);
        var edition = await db.PublicationEditions.AsNoTracking().SingleAsync();
        var outline = Assert.Single((await new PublicationEffectiveConfigurationResolver(Database(db))
            .ResolveReleaseAsync(project.Id, edition.Id)).OutlineItems);
        Assert.NotNull(edition.SelectedCoverImageId);
        Assert.False(outline.IsIncluded);
    }

    [Fact]
    public async Task IncompatibleImportedIsbnRollsBackTheEntireImport()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking).Options;
        await using var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance);
        await db.Database.MigrateAsync();

        var project = new Project
        {
            Name = "Import target",
            Slug = $"import-atomic-{Guid.NewGuid():N}",
        };
        var existingEdition = new PublicationEdition
        {
            ProjectId = project.Id,
            Name = "Existing paperback",
            Format = PublicationEditionFormat.Paperback,
            Vendor = PublicationVendor.Generic,
            VendorProfileVersion = "preview-1",
            TitleOverride = "Existing",
            Author = "Author",
            Language = "en",
            Isbn = "9780306406157",
            PrintRegistryVersion = "2026.08.1",
            PrintProductKey = "generic-perfectbound-template",
            PrintFinish = PrintFinish.Matte,
            PrintCoverMode = PrintCoverMode.Simplex,
            PageWidthInches = 6,
            PageHeightInches = 9,
            PageMarginInches = 0.75,
            BodyFontSizePoints = 11,
            BodyLineHeight = 1.4,
        };
        db.AddRange(project, existingEdition);
        await db.SaveChangesAsync();

        var database = Database(db);
        var graph = new RelationalGraphStore(database);
        var entityTypeService = new EntityTypeService(database);
        var outline = new OutlineGraphSync(database, graph, entityTypeService);
        await outline.EnsureProjectAsync(project);
        var imageId = Guid.NewGuid();
        var export = new ProjectExportDocument
        {
            FormatVersion = 15,
            ExportKind = ProjectExportKind.Full,
            Project = new ProjectExportProject(
                Guid.NewGuid(),
                "Exported",
                "exported",
                "This guidance must roll back.",
                true,
                true),
            Images =
            [
                new ProjectExportImage(
                    imageId,
                    "map.png",
                    "image/png",
                    [1, 2, 3, 4],
                    "Map",
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
            PublicationEditions =
            [
                new ProjectExportPublicationEdition(
                    Id: Guid.NewGuid(),
                    Name: "Conflicting EPUB",
                    Format: PublicationEditionFormat.Epub,
                    Vendor: PublicationVendor.Generic,
                    VendorProfileVersion: "preview-1",
                    Status: PublicationEditionStatus.Draft,
                    IsDefault: false,
                    Revision: 0,
                    TitleOverride: "Imported",
                    Subtitle: string.Empty,
                    Author: "Author",
                    Language: "en",
                    Publisher: string.Empty,
                    Copyright: string.Empty,
                    Isbn: "9780306406157",
                    Description: string.Empty,
                    IncludeTableOfContents: true,
                    IncludeVisibleTableOfContents: true,
                    IncludeActSynopses: false,
                    IncludeChapterSynopses: false,
                    IncludeActHeadings: true,
                    IncludeChapterHeadings: true,
                    NumberActs: false,
                    NumberChapters: false,
                    TitlePageMode: PublishTitlePageMode.Automatic,
                    PageWidthInches: 8.5,
                    PageHeightInches: 11,
                    PageMarginInches: 0.75,
                    SelectedCoverImageId: null,
                    Binding: LegacyPublicationBinding.Digital,
                    Paper: LegacyPublicationPaper.Digital,
                    Ink: LegacyPublicationInk.Digital,
                    Bleed: false,
                    AllowDesignedPageOverrides: false,
                    OutlineItems: [],
                    CoverDesign: null)
                {
                    BodyFontSizePoints = 12,
                    BodyLineHeight = 1.55,
                },
            ],
        };
        var job = new ProjectImportJob
        {
            ProjectId = project.Id,
            FileName = "atomic-fixture.lorekeeper.json",
            ContentJson = JsonSerializer.Serialize(
                export,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        };
        db.ProjectImportJobs.Add(job);
        await db.SaveChangesAsync();
        var mutations = new ProjectMutationCoordinator();
        var indexWork = new VectorIndexWorkCoordinator(NullLogger<VectorIndexWorkCoordinator>.Instance);
        var processor = new ProjectImportJobProcessor(
            database,
            graph,
            DefaultProxy<IActService>(),
            new ImportChapterService(database),
            DefaultProxy<IProjectFactService>(),
            entityTypeService,
            outline,
            DefaultProxy<IContextIndexingService>(),
            DefaultProxy<IEntityVisualExampleService>(),
            new BookBriefService(database),
            new ManuscriptStyleService(database),
            indexWork,
            new ProjectImportJobNotifier(),
            NullLogger<ProjectImportJobProcessor>.Instance);

        await processor.RunAsync(job.Id);

        db.ChangeTracker.Clear();
        var failed = await db.ProjectImportJobs.AsNoTracking().SingleAsync();
        Assert.Equal(ProjectImportJobStatus.Failed, failed.Status);
        Assert.Contains("ISBN-13 conflicts", failed.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(await db.PublishAssets.AsNoTracking().ToListAsync());
        Assert.Empty(await db.Chapters.AsNoTracking().ToListAsync());
        Assert.Single(await db.PublicationEditions.AsNoTracking().ToListAsync());
        Assert.Equal(
            string.Empty,
            await db.Projects.AsNoTracking()
                .Where(candidate => candidate.Id == project.Id)
                .Select(candidate => candidate.ProjectGuidance)
                .SingleAsync());
    }

    [Fact]
    public async Task V9JobImportsMarkedFigureManuscriptStylesAndRemapsTheAsset()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking).Options;
        await using var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance);
        await db.Database.MigrateAsync();

        var project = new Project
        {
            Name = "Import target",
            Slug = $"import-target-{Guid.NewGuid():N}",
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        var mutations = new ProjectMutationCoordinator();
        var gatedMutations = new GateMutationCoordinator(mutations);
        var database = Database(db, gatedMutations);
        var graph = new RelationalGraphStore(database);
        var entityTypeService = new EntityTypeService(database);
        var outline = new OutlineGraphSync(database, graph, entityTypeService);
        await outline.EnsureProjectAsync(project);
        var indexWork = new VectorIndexWorkCoordinator(NullLogger<VectorIndexWorkCoordinator>.Instance);
        var contextIndexing = new ObservingContextIndexingService(indexWork, db);

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
                    FigurePresentation = new FigurePresentation(),
                    Content = [new ManuscriptInline { Text = "Eastern road" }],
                },
            ],
        };
        var export = new ProjectExportDocument
        {
            FormatVersion = 9,
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
            database,
            graph,
            DefaultProxy<IActService>(),
            new ImportChapterService(database, contextIndexing),
            DefaultProxy<IProjectFactService>(),
            entityTypeService,
            outline,
            contextIndexing,
            DefaultProxy<IEntityVisualExampleService>(),
            new BookBriefService(database),
            new ManuscriptStyleService(database),
            indexWork,
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
        Assert.True(completed.WarningCount > 0);
        Assert.True(contextIndexing.ExecutionCount > 0);
        Assert.DoesNotContain(true, contextIndexing.ExecutedDuringTransaction);
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
        var styles = await new ManuscriptStyleService(Database(db)).ListAsync(project.Id);
        Assert.Equal(2, styles.Count);
        ManuscriptStyleService.ValidateDocumentReferences(imported.Manuscript, styles);
        Assert.Contains(
            imported.Manuscript.Content.SelectMany(block => block.Content).SelectMany(inline => inline.Marks),
            mark => mark.Type == ManuscriptMarkType.CharacterStyle && mark.Value == "lead-in");
    }

    private static T DefaultProxy<T>() where T : class =>
        DispatchProxy.Create<T, DefaultDispatchProxy>();

    private static async Task<ProjectImportJobProcessor> CreateProcessorAsync(AppDbContext db, Project project)
    {
        var database = Database(db);
        var graph = new RelationalGraphStore(database);
        var entityTypes = new EntityTypeService(database);
        var outline = new OutlineGraphSync(database, graph, entityTypes);
        await outline.EnsureProjectAsync(project);
        var mutations = new ProjectMutationCoordinator();
        return new ProjectImportJobProcessor(
            database,
            graph,
            DefaultProxy<IActService>(),
            new ImportChapterService(database),
            DefaultProxy<IProjectFactService>(),
            entityTypes,
            outline,
            DefaultProxy<IContextIndexingService>(),
            DefaultProxy<IEntityVisualExampleService>(),
            new BookBriefService(database),
            new ManuscriptStyleService(database),
            new VectorIndexWorkCoordinator(NullLogger<VectorIndexWorkCoordinator>.Instance),
            new ProjectImportJobNotifier(),
            NullLogger<ProjectImportJobProcessor>.Instance);
    }

    private static ProjectImportJob AddImportJob(
        AppDbContext db,
        Guid projectId,
        ProjectExportDocument document)
    {
        var job = new ProjectImportJob
        {
            ProjectId = projectId,
            FileName = "cover-fixture.lorekeeper.json",
            ContentJson = JsonSerializer.Serialize(document, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        };
        db.ProjectImportJobs.Add(job);
        return job;
    }

    private static ProjectExportImage ExportImage(Guid id, string fileName, byte[] data) =>
        new(
            id, fileName, "image/png", data, "Cover artwork", PublishAssetSource.Uploaded,
            string.Empty, string.Empty, "{}", null, null, null, null, null,
            DateTime.UtcNow, DateTime.UtcNow);

    private static ProjectExportChapter ExportPicturePage(
        Guid id,
        string title,
        ManuscriptDocument manuscript,
        PicturePageLayout layout) =>
        new()
        {
            Id = id,
            Title = title,
            ManuscriptJson = ManuscriptCodec.Serialize(manuscript),
            ManuscriptRevision = manuscript.Revision,
            VisualMode = ChapterVisualMode.PicturePage,
            PageLayoutJson = JsonSerializer.Serialize(layout, ManuscriptCodec.JsonOptions),
        };

    private static PicturePageImageElement PictureElement(Guid imageId) =>
        new(Guid.NewGuid(), imageId, 0, 0, 100, 100, ChapterImageFit.Cover, 1, 0, string.Empty);

    private static ProjectExportPublicationEdition ExportEdition(
        string name,
        Guid? selectedCoverImageId,
        Guid? selectedCoverChapterId,
        List<ProjectExportEditionOutlineItem> outline) =>
        new(
            Guid.NewGuid(), name, PublicationEditionFormat.Paperback, PublicationVendor.Generic,
            "preview-1", PublicationEditionStatus.Draft, false, 0, string.Empty, string.Empty,
            "Author", "en", string.Empty, string.Empty, string.Empty, string.Empty, true, true,
            false, false, true, true, false, false, PublishTitlePageMode.Automatic,
            6, 9, 0.75, selectedCoverImageId,
            LegacyPublicationBinding.PerfectBound, LegacyPublicationPaper.White, LegacyPublicationInk.BlackAndWhite,
            false, false, outline, null)
        {
            BodyFontSizePoints = 11,
            BodyLineHeight = 1.3,
            PrintPicturePageSpreadMode = PrintPicturePageSpreadMode.WholeSpread,
            EpubPicturePageSpreadMode = EpubPicturePageSpreadMode.RequestLandscape,
            SelectedCoverChapterId = selectedCoverChapterId,
        };

    private static byte[] TinyPng()
    {
        using var bitmap = new SKBitmap(2, 2);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

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

    private sealed class ObservingContextIndexingService(
        IVectorIndexWorkCoordinator indexWork,
        AppDbContext db) : IContextIndexingService
    {
        public int ExecutionCount { get; private set; }
        public List<bool> ExecutedDuringTransaction { get; } = [];

        public Task ReindexEntityAsync(
            Guid projectId,
            Guid entityId,
            CancellationToken cancellationToken = default) =>
            QueueAsync(
                VectorIndexWorkKind.ContextEntity,
                $"{projectId:N}:{entityId:N}",
                cancellationToken);

        public Task DeleteEntityAsync(
            Guid projectId,
            Guid entityId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ReindexChapterAsync(
            Guid chapterId,
            CancellationToken cancellationToken = default) =>
            QueueAsync(
                VectorIndexWorkKind.ContextChapter,
                chapterId.ToString("N"),
                cancellationToken);

        public Task DeleteChapterAsync(
            Guid projectId,
            Guid chapterId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ReindexActAsync(
            Guid actId,
            CancellationToken cancellationToken = default) =>
            QueueAsync(
                VectorIndexWorkKind.ContextAct,
                actId.ToString("N"),
                cancellationToken);

        public Task DeleteActAsync(
            Guid projectId,
            Guid actId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ReindexIngestSourceAsync(
            Guid sourceId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteIngestSourceAsync(
            Guid projectId,
            Guid sourceId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ReindexIngestSourceChunkAsync(
            Guid sourceChunkId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteIngestSourceChunkAsync(
            Guid projectId,
            Guid sourceChunkId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        private Task QueueAsync(
            VectorIndexWorkKind kind,
            string resourceKey,
            CancellationToken cancellationToken) =>
            indexWork.QueueOrRunAsync(
                kind,
                resourceKey,
                _ =>
                {
                    ExecutionCount++;
                    ExecutedDuringTransaction.Add(db.Database.CurrentTransaction is not null);
                    throw new InvalidOperationException("Injected post-commit indexing failure.");
                },
                cancellationToken);
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

    private sealed class ImportChapterService(
        IAppDatabaseOperationFactory database,
        IContextIndexingService? contextIndexing = null) : IChapterService
    {
        public Task<IReadOnlyList<Chapter>> ListAsync(
            Guid projectId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<Chapter?> GetAsync(Guid chapterId, CancellationToken cancellationToken = default)
        {
            await using var operation = await database.OpenReadAsync(cancellationToken);
            return await operation.Repositories.Chapters.GetByIdAsync(chapterId, cancellationToken);
        }

        public async Task<Chapter?> ReloadFromStoreAsync(
            Guid chapterId,
            CancellationToken cancellationToken = default)
        {
            await using var operation = await database.OpenReadAsync(cancellationToken);
            return await operation.Repositories.Chapters.ReloadFromStoreAsync(chapterId, cancellationToken);
        }

        public async Task<Chapter> CreateAsync(
            Guid projectId,
            Guid? actId = null,
            string? title = null,
            string? synopsis = null,
            Guid? id = null,
            CancellationToken cancellationToken = default)
        {
            await using var operation = await database.OpenWriteAsync(cancellationToken);
            var chapters = operation.Repositories.Chapters;
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
            await operation.SaveChangesAsync(cancellationToken);
            if (contextIndexing is not null)
                await contextIndexing.ReindexChapterAsync(chapter.Id, cancellationToken);
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
