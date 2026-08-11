using Lorekeeper.Composition;
using Lorekeeper.Context;
using Lorekeeper.EntityVisuals;
using Lorekeeper.Fonts;
using Lorekeeper.Images;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Publish;
using Lorekeeper.Llm;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Lorekeeper.Tests;

public sealed class LayoutImageWorkflowTests
{
    [Fact]
    public void EditorWorkflowRequiresDirectAnnotatedAndCleanCanvasInspection()
    {
        Assert.Contains("preview_page_canvas", AssistantWorkflowInstructions.CompositionDesign);
        Assert.Contains("annotated", AssistantWorkflowInstructions.CompositionDesign);
        Assert.Contains("final clean preview", AssistantWorkflowInstructions.CompositionDesign);
        Assert.Contains("LAYOUT_IMAGE_GEOMETRY_MISMATCH", AssistantWorkflowInstructions.ImageGeneration);
        Assert.Contains("remains a usable unattached project image", AssistantWorkflowInstructions.ImageGeneration);
        Assert.Contains("Treat copy, typography, illustration, and negative space as one composition", AssistantWorkflowInstructions.BookDesignCraft);
        Assert.Contains("no more than two font families", AssistantWorkflowInstructions.BookDesignCraft);
        Assert.Contains("4.5:1 contrast", AssistantWorkflowInstructions.BookDesignCraft);
        Assert.Contains("durable cross-turn work log", AssistantWorkflowInstructions.NonReplayedToolHistory);
        Assert.Contains("Before each meaningful inspection or change phase", AssistantWorkflowInstructions.NonReplayedToolHistory);
    }

    [Theory]
    [InlineData(17, 11, 1632, 1056)]
    [InlineData(3, 2, 1536, 1024)]
    [InlineData(2, 3, 1024, 1536)]
    [InlineData(1, 1, 1248, 1248)]
    public void LayoutRasterUsesModerateExactAspect(
        double width,
        double height,
        int expectedWidth,
        int expectedHeight)
    {
        var result = LayoutImageSizeResolver.Resolve(width, height);

        Assert.Equal(expectedWidth, result.Width);
        Assert.Equal(expectedHeight, result.Height);
        LayoutImageSizeResolver.Validate(result.Width, result.Height);
    }

    [Fact]
    public async Task DirectCanvasPreviewRendersOneFacingSurfaceWithoutCreatingAssets()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var databaseOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(databaseOptions, NullLogger<AppDbContext>.Instance);
        await db.Database.EnsureCreatedAsync();
        var project = new Project { Name = "Canvas test", Slug = "canvas-test" };
        var image = new PublishAsset
        {
            Project = project,
            ProjectId = project.Id,
            Source = PublishAssetSource.Uploaded,
            FileName = "art.png",
            ContentType = "image/png",
            Data = TestPng(),
        };
        db.Projects.Add(project);
        db.PublishAssets.Add(image);
        await db.SaveChangesAsync();

        var layerId = Guid.NewGuid();
        var hiddenLayerId = Guid.NewGuid();
        var scene = new CompositionScene
        {
            Surface = new CompositionSurface
            {
                Kind = CompositionSurfaceKind.FacingSpread,
                WidthPoints = 1224,
                HeightPoints = 792,
                SafeInsetPoints = 36,
            },
            Layers =
            [
                new(layerId, "Content", 0),
                new(hiddenLayerId, "Hidden", 1, Visible: false),
            ],
            Objects =
            [
                new CompositionObject
                {
                    Id = Guid.NewGuid(), LayerId = layerId, Kind = CompositionObjectKind.Image,
                    ImageId = image.Id, ImageFit = FigureImageFit.Cover,
                    Bounds = new CompositionBounds { WidthPercent = 50, HeightPercent = 100 },
                },
                new CompositionObject
                {
                    Id = Guid.NewGuid(), LayerId = layerId, Kind = CompositionObjectKind.Image,
                    ImageId = image.Id, ImageFit = FigureImageFit.Stretch,
                    Bounds = new CompositionBounds { XPercent = 50, WidthPercent = 52, HeightPercent = 100 },
                },
                new CompositionObject
                {
                    Id = Guid.NewGuid(), LayerId = layerId, Kind = CompositionObjectKind.Text,
                    TextBinding = "A complete wide authoring canvas with deliberately overflowing text for annotation.",
                    FontFamilyKey = PublicationBuiltInFonts.DefaultKey,
                    FontSizePoints = 24, LineHeight = 1.2, RotationDegrees = 2,
                    Bounds = new CompositionBounds { XPercent = 8, YPercent = 8, WidthPercent = 18, HeightPercent = 3 },
                },
                new CompositionObject
                {
                    Id = Guid.NewGuid(), LayerId = layerId, Kind = CompositionObjectKind.Rectangle,
                    RotationDegrees = 8, Opacity = .5, FillColor = "#336699",
                    Bounds = new CompositionBounds { XPercent = 70, YPercent = 65, WidthPercent = 20, HeightPercent = 15 },
                },
                new CompositionObject
                {
                    Id = Guid.NewGuid(), LayerId = hiddenLayerId, Kind = CompositionObjectKind.Ellipse,
                    FillColor = "#ff0000",
                },
            ],
        };
        var environment = new TestWebHostEnvironment(FindWebRoot());
        var service = new CompositionCanvasPreviewService(
            db,
            new ProjectFontService(db, environment),
            Options.Create(new PublicationPressOptions { PreviewImageMaxEdge = 1200 }));

        var annotated = await service.RenderSceneAsync(
            project.Id, Guid.NewGuid(), 7, scene, CompositionCanvasPreviewMode.Annotated);
        var clean = await service.RenderSceneAsync(
            project.Id, annotated.CompositionId, 7, scene, CompositionCanvasPreviewMode.Clean);

        Assert.Equal(7, annotated.VariantRevision);
        Assert.Equal(annotated.PixelWidth, clean.PixelWidth);
        Assert.Equal(annotated.PixelHeight, clean.PixelHeight);
        Assert.True(annotated.PixelWidth > annotated.PixelHeight);
        Assert.Equal(4, annotated.VisibleObjectCount);
        Assert.Equal(1, annotated.HiddenObjectCount);
        Assert.Contains(annotated.Diagnostics, item => item.Code == "TEXT_OVERFLOW");
        Assert.Contains(annotated.Diagnostics, item => item.Code == "OBJECT_CLIPPED");
        Assert.NotEqual(annotated.Data, clean.Data);
        Assert.Equal(1, await db.PublishAssets.CountAsync());
    }

    [Fact]
    public async Task MismatchedLayoutOutputIsSavedWithWarningInsteadOfRejected()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var databaseOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(databaseOptions, NullLogger<AppDbContext>.Instance);
        await db.Database.EnsureCreatedAsync();
        var project = new Project { Name = "Mismatch test", Slug = "mismatch-test" };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        var visualExamples = new EntityVisualExampleService(db, new NoopContextIndexingService());
        var jobs = new ProjectImageJobService(
            db,
            visualExamples,
            Options.Create(new ProjectImageGenerationOptions()));
        var job = await jobs.CreateGenerateJobAsync(
            project.Id,
            new ProjectImageGenerateJobRequest(
                "Test prompt",
                "1536x1024",
                "medium",
                "png",
                null,
                "Test image",
                1,
                [],
                TargetGeometryJson: """{"targetKind":"page-surface","widthInches":17,"heightInches":11,"aspectRatio":"17:11"}"""));
        var providerResult = new ProjectImageProviderResult(
            [new ProjectImageProviderImage(TestPng(), "image/png", "png", null, "response", "call")],
            "test",
            "mainline",
            "image",
            "{}");

        var saved = await jobs.SaveGeneratedOutputAsync(
            project.Id,
            job.Id,
            0,
            providerResult,
            providerResult.Images[0]);

        Assert.NotEqual(Guid.Empty, saved.Id);
        var stored = await db.PublishAssets.AsNoTracking().SingleAsync(item => item.Id == saved.Id);
        using var metadata = System.Text.Json.JsonDocument.Parse(stored.SourceMetadataJson);
        var validation = metadata.RootElement.GetProperty("geometryValidation");
        Assert.False(validation.GetProperty("geometryMatched").GetBoolean());
        Assert.Equal("1536x1024", validation.GetProperty("requestedRaster").GetString());
        Assert.Equal("320x180", validation.GetProperty("actualRaster").GetString());
        Assert.Equal("LAYOUT_IMAGE_GEOMETRY_MISMATCH", validation.GetProperty("warningCode").GetString());
    }

    private static byte[] TestPng()
    {
        using var bitmap = new SKBitmap(320, 180);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(42, 70, 115));
        using var paint = new SKPaint { Color = new SKColor(230, 180, 75), Style = SKPaintStyle.Fill };
        canvas.DrawCircle(230, 90, 55, paint);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static string FindWebRoot()
    {
        var candidate = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "Lorekeeper", "wwwroot"));
        Assert.True(Directory.Exists(candidate), $"Lorekeeper web root was not found at {candidate}.");
        return candidate;
    }

    private sealed class TestWebHostEnvironment(string webRootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Lorekeeper.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new PhysicalFileProvider(webRootPath);
        public string WebRootPath { get; set; } = webRootPath;
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = Directory.GetParent(webRootPath)!.FullName;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(Directory.GetParent(webRootPath)!.FullName);
    }

    private sealed class NoopContextIndexingService : IContextIndexingService
    {
        public Task ReindexEntityAsync(Guid projectId, Guid entityId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteEntityAsync(Guid projectId, Guid entityId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReindexChapterAsync(Guid chapterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteChapterAsync(Guid projectId, Guid chapterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReindexActAsync(Guid actId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteActAsync(Guid projectId, Guid actId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReindexIngestSourceAsync(Guid sourceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteIngestSourceAsync(Guid projectId, Guid sourceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReindexIngestSourceChunkAsync(Guid sourceChunkId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteIngestSourceChunkAsync(Guid projectId, Guid sourceChunkId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
