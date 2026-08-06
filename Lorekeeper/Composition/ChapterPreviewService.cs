using System.Diagnostics;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lorekeeper.Fonts;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Publish;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Lorekeeper.Composition;

public sealed record ChapterPreviewResult(
    IReadOnlyList<ChapterPreviewPage> Pages,
    string CacheKey,
    IReadOnlyList<ChapterPreviewDiagnostic> Diagnostics,
    IReadOnlyList<ChapterPreviewFont> Fonts,
    IReadOnlyList<ChapterPreviewPageMap> PageMap);

public sealed record ChapterPreviewSource(
    ManuscriptDocument Document,
    IReadOnlyList<ManuscriptStyleView> Styles);

public sealed record ChapterPreviewPageTarget(
    string? BlockId = null,
    int? ChapterPageNumber = null);

public sealed record ChapterPreviewImageResult(
    ChapterPreviewPage Page,
    int ChapterPageNumber,
    int PhysicalPage,
    int PageCount,
    byte[] Data,
    int PixelWidth,
    int PixelHeight,
    IReadOnlyList<ChapterPreviewDiagnostic> Diagnostics);

public sealed record ChapterPreviewPageMap(
    Guid ChapterId,
    string BlockId,
    int PhysicalPage,
    int ChapterPageNumber);

public sealed record ChapterPreviewFont(
    string Face,
    string FamilyKey,
    Guid FaceId,
    int Weight,
    bool Italic,
    string ContentType,
    string ContentUrl);

public sealed record ChapterPreviewPage(
    int PhysicalPage,
    string Kind,
    double WidthPoints,
    double HeightPoints,
    string? PageLabel,
    string? Bookmark,
    IReadOnlyList<ChapterPreviewLine> Lines,
    IReadOnlyList<ChapterPreviewImage> Images,
    IReadOnlyList<ChapterPreviewShape> Shapes);

public sealed record ChapterPreviewLine(
    string Text,
    double Size,
    double X,
    double Y,
    double RotationDegrees,
    double? RotationOriginX,
    double? RotationOriginY,
    double Opacity,
    double[]? FillRgb,
    string? SemanticRole,
    string? Link,
    string? Face,
    IReadOnlyList<ChapterPreviewRun> Runs,
    double WordSpacing,
    double CharacterSpacing,
    int ZIndex);

public sealed record ChapterPreviewRun(
    string Text,
    string Face,
    bool Underline,
    bool Strikethrough,
    double BaselineShiftEm,
    double SizeScale);

public sealed record ChapterPreviewImage(
    Guid AssetId,
    double X,
    double Y,
    double Width,
    double Height,
    double RotationDegrees,
    double Opacity,
    string Fit,
    double CropX,
    double CropY,
    double SourceLeftFraction,
    double SourceWidthFraction,
    string? AltText,
    bool Decorative,
    int ZIndex);

public sealed record ChapterPreviewShape(
    string Kind,
    double X,
    double Y,
    double Width,
    double Height,
    double RotationDegrees,
    double Opacity,
    double[]? FillRgb,
    double[]? StrokeRgb,
    double StrokeWidth,
    int ZIndex);

public sealed record ChapterPreviewDiagnostic(string Severity, string Code, string Message);

public interface IChapterPreviewService
{
    Task<ChapterPreviewResult> LayoutAsync(Guid projectId, Guid chapterId, CancellationToken cancellationToken = default);
    Task<ChapterPreviewImageResult> RenderPageAsync(
        Guid projectId,
        Guid chapterId,
        ChapterPreviewSource? source,
        ChapterPreviewPageTarget target,
        CancellationToken cancellationToken = default);
}

public sealed class ChapterPreviewService(
    AppDbContext db,
    IPublicationPressRuntime press,
    IProjectFontService projectFonts,
    IOptions<PublicationPressOptions> options) : IChapterPreviewService
{
    private static readonly ConcurrentDictionary<string, ChapterPreviewResult> Cache = new(StringComparer.Ordinal);
    private static readonly ConcurrentQueue<string> CacheOrder = new();
    private const int MaximumCachedPreviews = 64;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<ChapterPreviewResult> LayoutAsync(
        Guid projectId,
        Guid chapterId,
        CancellationToken cancellationToken = default)
        => await LayoutCoreAsync(projectId, chapterId, source: null, cancellationToken);

    public async Task<ChapterPreviewImageResult> RenderPageAsync(
        Guid projectId,
        Guid chapterId,
        ChapterPreviewSource? source,
        ChapterPreviewPageTarget target,
        CancellationToken cancellationToken = default)
    {
        if ((string.IsNullOrWhiteSpace(target.BlockId)) == (target.ChapterPageNumber is null))
            throw new ArgumentException("Specify exactly one blockId or chapter page number.", nameof(target));
        if (target.ChapterPageNumber is <= 0)
            throw new ArgumentOutOfRangeException(nameof(target), "Chapter page number must be greater than zero.");

        var layout = await LayoutCoreAsync(projectId, chapterId, source, cancellationToken);
        var orderedPages = layout.Pages.OrderBy(page => page.PhysicalPage).ToArray();
        if (orderedPages.Length == 0)
            throw new InvalidDataException("The chapter preview contains no typeset pages.");

        ChapterPreviewPage page;
        int chapterPageNumber;
        if (!string.IsNullOrWhiteSpace(target.BlockId))
        {
            var targetBlockId = target.BlockId.Trim();
            var mapping = layout.PageMap.FirstOrDefault(item =>
                string.Equals(item.BlockId, targetBlockId, StringComparison.Ordinal));
            if (mapping is null)
            {
                var equivalentMappings = layout.PageMap
                    .Where(item => ManuscriptOperations.AreEquivalentBlockIds(item.BlockId, targetBlockId))
                    .ToArray();
                if (equivalentMappings.Length > 1)
                    throw new InvalidOperationException($"Block '{targetBlockId}' is ambiguous in the chapter page map.");
                mapping = equivalentMappings.SingleOrDefault();
            }
            if (mapping is null)
                throw new KeyNotFoundException($"Block '{targetBlockId}' was not found in the chapter page map.");

            chapterPageNumber = mapping.ChapterPageNumber;
            page = orderedPages.FirstOrDefault(item => item.PhysicalPage == mapping.PhysicalPage)
                ?? throw new InvalidDataException($"The page mapped for block '{targetBlockId}' was not returned by Press.");
        }
        else
        {
            chapterPageNumber = target.ChapterPageNumber!.Value;
            if (chapterPageNumber > orderedPages.Length)
                throw new ArgumentOutOfRangeException(nameof(target), $"Chapter page {chapterPageNumber} is outside the preview's {orderedPages.Length} page(s).");
            page = orderedPages[chapterPageNumber - 1];
        }

        var data = await RasterizePageAsync(projectId, layout, page, cancellationToken);
        return new ChapterPreviewImageResult(
            page,
            chapterPageNumber,
            page.PhysicalPage,
            orderedPages.Length,
            data.Data,
            data.Width,
            data.Height,
            layout.Diagnostics);
    }

    private async Task<ChapterPreviewResult> LayoutCoreAsync(
        Guid projectId,
        Guid chapterId,
        ChapterPreviewSource? source,
        CancellationToken cancellationToken)
    {
        var project = await db.Projects.AsNoTracking().SingleOrDefaultAsync(item => item.Id == projectId, cancellationToken)
            ?? throw new KeyNotFoundException("Project was not found.");
        var setup = await db.ProjectPageSetups.AsNoTracking().SingleOrDefaultAsync(item => item.ProjectId == projectId, cancellationToken)
            ?? new ProjectPageSetup { ProjectId = projectId };
        var acts = await db.Acts.AsNoTracking().Where(item => item.ProjectId == projectId)
            .OrderBy(item => item.Order).ThenBy(item => item.Id).ToListAsync(cancellationToken);
        var chapter = await db.Chapters.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == chapterId && item.ProjectId == projectId, cancellationToken)
            ?? throw new KeyNotFoundException("Chapter was not found in this project.");
        var chapters = new List<Chapter> { chapter };

        if (source is not null
            && (source.Document.ManuscriptId != chapterId
                || source.Styles is null))
            throw new InvalidDataException("The chapter preview source does not match the requested chapter.");

        var documents = new Dictionary<Guid, ManuscriptDocument>
        {
            [chapterId] = source?.Document
                ?? ManuscriptCodec.Deserialize(chapter.ManuscriptJson, chapter.Id, chapter.ManuscriptRevision),
        };
        var compositionIds = documents.Values.SelectMany(document => document.Content)
            .Where(block => block.Type == ManuscriptBlockType.DesignedPage && block.PageCompositionId is not null)
            .Select(block => block.PageCompositionId!.Value).Distinct().ToArray();
        var compositions = await db.PageCompositions.AsNoTracking()
            .Where(item => item.ProjectId == projectId && compositionIds.Contains(item.Id))
            .ToListAsync(cancellationToken);
        var variants = await db.PageCompositionVariants.AsNoTracking()
            .Where(item => compositionIds.Contains(item.CompositionId))
            .ToListAsync(cancellationToken);
        var compositionPayloads = new Dictionary<Guid, object>();
        var usedFontKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fontKey in documents.Values.SelectMany(document => document.Content)
            .Select(block => block.ParagraphPresentation?.FontFamilyKey)
            .Where(key => !string.IsNullOrWhiteSpace(key)))
            usedFontKeys.Add(fontKey!);
        var imageIds = documents.Values.SelectMany(document => document.Content)
            .Where(block => block.ImageId is not null).Select(block => block.ImageId!.Value).ToHashSet();
        foreach (var composition in compositions)
        {
            var variant = variants.FirstOrDefault(item => item.Id == composition.ActiveAuthoringVariantId)
                ?? variants.Where(item => item.CompositionId == composition.Id).OrderByDescending(item => item.UpdatedAt).FirstOrDefault();
            if (variant is null)
                throw new InvalidDataException($"Designed Page '{composition.Name}' has no authoring layout.");
            var scene = JsonSerializer.Deserialize<CompositionScene>(variant.SceneJson, ManuscriptCodec.JsonOptions)
                ?? throw new InvalidDataException($"Designed Page '{composition.Name}' has an empty authoring layout.");
            foreach (var fontKey in scene.Objects.Select(item => item.FontFamilyKey)
                .Concat(scene.Styles.Select(item => item.FontFamilyKey))
                .Where(item => !string.IsNullOrWhiteSpace(item)))
                usedFontKeys.Add(fontKey);
            foreach (var imageId in scene.Objects.Where(item => item.ImageId is not null).Select(item => item.ImageId!.Value))
                imageIds.Add(imageId);
            var semantic = ManuscriptCodec.Deserialize(composition.SemanticManuscriptJson, composition.Id, composition.Revision);
            compositionPayloads[composition.Id] = new
            {
                id = composition.Id,
                composition.Name,
                revision = composition.Revision,
                semanticBlocks = semantic.Content.Select(BlockPayload).ToArray(),
                variants = new[] { new { id = variant.Id, variant.GeometryKey, variant.Revision, scene } },
            };
        }

        var assets = await db.PublishAssets.AsNoTracking()
            .Where(item => item.ProjectId == projectId && imageIds.Contains(item.Id))
            .ToListAsync(cancellationToken);
        if (assets.Count != imageIds.Count)
            throw new InvalidDataException("The chapter references a missing project image.");

        var styles = source is null
            ? (await db.ManuscriptStyleDefinitions.AsNoTracking()
                    .Where(item => item.ProjectId == projectId)
                    .OrderBy(item => item.NameKey)
                    .ToListAsync(cancellationToken))
                .Select(item => new PreviewStyle(
                    item.Id,
                    item.Name,
                    item.Kind,
                    item.SemanticRole,
                    item.Revision,
                    JsonSerializer.Deserialize<ManuscriptStyleProperties>(item.DefinitionJson, ManuscriptCodec.JsonOptions)
                        ?? new ManuscriptStyleProperties()))
                .ToList()
            : source.Styles
                .Select(item => new PreviewStyle(
                    item.Id,
                    item.Name,
                    item.Kind,
                    item.SemanticRole,
                    item.Revision,
                    item.Definition))
                .ToList();
        foreach (var style in styles)
        {
            if (!string.IsNullOrWhiteSpace(style.Definition.FontFamilyKey))
                usedFontKeys.Add(style.Definition.FontFamilyKey);
        }
        var customFontIds = usedFontKeys
            .Where(key => key.StartsWith("project:", StringComparison.OrdinalIgnoreCase))
            .Select(key => Guid.TryParse(key["project:".Length..], out var id) ? id : Guid.Empty)
            .ToHashSet();
        if (customFontIds.Contains(Guid.Empty))
            throw new InvalidDataException("A paragraph, Book Text Style, or Designed Page references an invalid project font key.");
        var fontFamilies = await db.ProjectFontFamilies.AsNoTracking().Include(item => item.Faces)
            .Where(item => item.ProjectId == projectId && customFontIds.Contains(item.Id))
            .ToListAsync(cancellationToken);
        if (fontFamilies.Count != customFontIds.Count)
            throw new InvalidDataException("The chapter preview references a missing project font family.");
        if (fontFamilies.Any(item => !item.EmbeddingRightsConfirmed || item.Faces.Count == 0))
            throw new InvalidDataException("Confirm embedding rights and provide a usable face for every project font used by this preview.");
        var previewFonts = fontFamilies.SelectMany(family => family.Faces.Select(face => new PreviewFont(
            face.Id,
            ProjectFontService.CustomKey(family.Id),
            face.Weight,
            face.Italic,
            face.ContentType == "font/otf" ? $"fonts/{face.Id:N}.otf" : $"fonts/{face.Id:N}.ttf",
            face.ContentType,
            $"/projects/{projectId:N}/fonts/{face.Id:N}/content",
            face.Data,
            Convert.ToHexStringLower(SHA256.HashData(face.Data))))).ToList();
        foreach (var familyKey in usedFontKeys
            .Where(key => key.StartsWith("builtin:", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase))
        {
            var family = PublicationBuiltInFonts.Find(familyKey)
                ?? throw new InvalidDataException($"The chapter preview references unsupported bundled font '{familyKey}'.");
            foreach (var faceView in family.Faces)
            {
                var face = await projectFonts.ResolveFaceAsync(
                    projectId,
                    family.Key,
                    faceView.Weight,
                    faceView.Italic,
                    requireExact: true,
                    cancellationToken)
                    ?? throw new InvalidDataException($"Bundled font '{family.Name}' is missing {faceView.SubfamilyName}.");
                var faceId = DeterministicFontId($"{family.Key}|{face.Weight}|{face.Italic}");
                previewFonts.Add(new PreviewFont(
                    faceId,
                    family.Key,
                    face.Weight,
                    face.Italic,
                    $"fonts/{faceId:N}.ttf",
                    face.ContentType,
                    faceView.ContentUrl,
                    face.Data,
                    Convert.ToHexStringLower(SHA256.HashData(face.Data))));
            }
        }
        var cacheKey = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            setup.Revision,
            Chapters = chapters.Select(item => new { item.Id, item.ManuscriptRevision, item.UpdatedAt }),
            Compositions = compositions.Select(item => new { item.Id, item.Revision, item.UpdatedAt, item.ActiveAuthoringVariantId }),
            Variants = variants.Select(item => new { item.Id, item.Revision, item.UpdatedAt }),
            Assets = assets.Select(item => new { item.Id, item.UpdatedAt }),
            Documents = documents.Select(item => new
            {
                item.Key,
                item.Value.Revision,
                Hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ManuscriptCodec.Serialize(item.Value)))),
            }),
            Styles = styles.Select(item => new { item.Id, item.Revision, item.Name, item.Kind, item.SemanticRole, item.Definition }),
            Fonts = previewFonts.Select(item => new { item.Id, item.Sha256 }),
        }, JsonOptions)));
        if (Cache.TryGetValue(cacheKey, out var cached))
            return cached;
        var jobId = Guid.NewGuid();
        var jobRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Lorekeeper", "press-previews", jobId.ToString("N")));
        var inputRoot = Path.Combine(jobRoot, "input");
        Directory.CreateDirectory(Path.Combine(inputRoot, "assets"));
        Directory.CreateDirectory(Path.Combine(inputRoot, "fonts"));
        try
        {
            var declarations = new List<object>();
            foreach (var asset in assets)
            {
                var extension = asset.ContentType == "image/jpeg" ? ".jpg" : ".png";
                var relativePath = $"assets/{asset.Id:N}{extension}";
                await File.WriteAllBytesAsync(Path.Combine(inputRoot, relativePath), asset.Data, cancellationToken);
                using var codec = SKCodec.Create(new SKMemoryStream(asset.Data))
                    ?? throw new InvalidDataException($"Project image '{asset.FileName}' is not a supported raster image.");
                declarations.Add(new
                {
                    id = asset.Id,
                    relativePath,
                    mediaType = asset.ContentType,
                    byteLength = asset.Data.LongLength,
                    sha256 = Convert.ToHexStringLower(SHA256.HashData(asset.Data)),
                    widthPixels = codec.Info.Width,
                    heightPixels = codec.Info.Height,
                });
            }
            foreach (var font in previewFonts)
            {
                _ = ProjectFontBinary.Normalize(font.Data, Path.GetFileName(font.RelativePath));
                await File.WriteAllBytesAsync(Path.Combine(inputRoot, font.RelativePath), font.Data, cancellationToken);
            }

            var sections = chapters.GroupBy(chapter => chapter.ActId)
                .Select((group, index) =>
                {
                    var act = group.Key is Guid actId ? acts.FirstOrDefault(item => item.Id == actId) : null;
                    return new
                    {
                        id = act?.Id ?? GuidUtility(projectId, index),
                        title = act?.Title ?? "Chapters",
                        synopsis = act?.Synopsis ?? string.Empty,
                        includePage = false,
                        includeHeading = false,
                        chapters = group.Select(chapter => new
                        {
                            id = chapter.Id,
                            chapter.Title,
                            chapter.Synopsis,
                            includeHeading = true,
                            blocks = documents[chapter.Id].Content.Select(BlockPayload).ToArray(),
                            pageCompositions = documents[chapter.Id].Content
                                .Where(block => block.PageCompositionId is not null)
                                .Select(block => compositionPayloads[block.PageCompositionId!.Value])
                                .Distinct().ToArray(),
                        }).ToArray(),
                    };
                }).ToArray();
            var payload = new
            {
                protocolVersion = 5,
                jobId = jobId.ToString("N"),
                profile = "generic-digital-pdf-v1",
                ink = "Color",
                layoutTraceMode = "browser-preview",
                document = new
                {
                    title = project.Name,
                    subtitle = string.Empty,
                    author = string.Empty,
                    language = "en",
                    matter = Array.Empty<object>(),
                    includeTitlePage = false,
                    includeVisibleTableOfContents = false,
                    includeActHeadings = false,
                    includeChapterHeadings = true,
                    numberActs = false,
                    numberChapters = false,
                    sections,
                    styles = styles.Select(style => new
                    {
                        style.Name,
                        kind = style.Kind.ToString(),
                        style.SemanticRole,
                        definition = style.Definition,
                    }).ToArray(),
                    placements = Array.Empty<object>(),
                    outputMode = "DigitalPdf",
                    allowDesignedPageOverrides = true,
                },
                trim = new
                {
                    widthInches = setup.PageWidthInches,
                    heightInches = setup.PageHeightInches,
                    marginInches = setup.PageMarginInches,
                    bodyFontSizePoints = setup.BodyFontSizePoints,
                    bodyLineHeight = setup.BodyLineHeight,
                    bleedInches = 0,
                    mirrorMargins = true,
                    rectoChapterStarts = true,
                    minimumWidowLines = 2,
                    minimumOrphanLines = 2,
                },
                cover = (object?)null,
                assets = declarations,
                fonts = previewFonts.Select(font => new
                {
                    id = font.Id.ToString("N"),
                    font.FamilyKey,
                    font.Weight,
                    font.Italic,
                    font.RelativePath,
                    mediaType = font.ContentType,
                    byteLength = font.Data.LongLength,
                    font.Sha256,
                    embeddingRightsConfirmed = true,
                }).ToArray(),
            };
            await File.WriteAllBytesAsync(Path.Combine(inputRoot, "request.json"), JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions), cancellationToken);

            var start = press.CreateStartInfo(jobId, jobRoot);
            start.ArgumentList[0] = "layout";
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Lorekeeper Press could not be started.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(options.Value.RenderTimeoutSeconds, 10, 1800)));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            var stdoutTask = ReadBoundedAsync(process.StandardOutput, 16 * 1024 * 1024, linked.Token);
            var stderrTask = ReadBoundedAsync(process.StandardError, 64 * 1024, linked.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                TryTerminate(process);
                throw;
            }
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Press preview failed. {Limit(stderr)} {Limit(stdout)}".Trim());
            var response = JsonSerializer.Deserialize<LayoutResponse>(stdout, JsonOptions)
                ?? throw new InvalidDataException("Lorekeeper Press returned an empty layout response.");
            if (response.ProtocolVersion != 5 || response.JobId != jobId.ToString("N"))
                throw new InvalidDataException("Lorekeeper Press returned the wrong preview protocol or job identity.");
            var firstPage = response.PageMap.Where(item => Guid.TryParse(item.ChapterId, out var mapped) && mapped == chapterId)
                .Select(item => item.PageNumber).DefaultIfEmpty(1).Min();
            var pages = response.Pages.Select((page, index) => ToPage(page, index + 1))
                .Where(page => page.PhysicalPage >= firstPage).ToArray();
            var pageMap = response.PageMap
                .Where(item => Guid.TryParse(item.ChapterId, out var mapped)
                    && mapped == chapterId
                    && item.PageNumber >= firstPage
                    && !string.IsNullOrWhiteSpace(item.BlockId))
                .Select(item => new ChapterPreviewPageMap(
                    chapterId,
                    item.BlockId,
                    item.PageNumber,
                    item.PageNumber - firstPage + 1))
                .OrderBy(item => item.PhysicalPage)
                .ThenBy(item => item.BlockId, StringComparer.Ordinal)
                .ToArray();
            var result = new ChapterPreviewResult(
                pages,
                cacheKey,
                (response.Diagnostics ?? []).Select(diagnostic => new ChapterPreviewDiagnostic(
                    diagnostic.Severity,
                    diagnostic.Code,
                    diagnostic.Message)).ToArray(),
                previewFonts.Select((font, index) => new ChapterPreviewFont(
                    $"Custom{index}", font.FamilyKey, font.Id, font.Weight, font.Italic, font.ContentType, font.ContentUrl)).ToArray(),
                pageMap);
            Cache[cacheKey] = result;
            CacheOrder.Enqueue(cacheKey);
            while (Cache.Count > MaximumCachedPreviews && CacheOrder.TryDequeue(out var expired))
                Cache.TryRemove(expired, out _);
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Chapter preview timed out.");
        }
        finally
        {
            try { if (Directory.Exists(jobRoot)) Directory.Delete(jobRoot, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task<RasterizedPage> RasterizePageAsync(
        Guid projectId,
        ChapterPreviewResult layout,
        ChapterPreviewPage page,
        CancellationToken cancellationToken)
    {
        var maxEdge = Math.Clamp(options.Value.PreviewImageMaxEdge, 320, 4096);
        var scale = Math.Min(1d, maxEdge / Math.Max(page.WidthPoints, page.HeightPoints));
        scale = Math.Max(scale, 0.25d);
        var width = Math.Max(1, (int)Math.Round(page.WidthPoints * scale));
        var height = Math.Max(1, (int)Math.Round(page.HeightPoints * scale));

        var assetIds = page.Images.Select(image => image.AssetId).Distinct().ToArray();
        var assets = await db.PublishAssets.AsNoTracking()
            .Where(asset => asset.ProjectId == projectId && assetIds.Contains(asset.Id))
            .ToDictionaryAsync(asset => asset.Id, cancellationToken);
        if (assets.Count != assetIds.Length)
            throw new InvalidDataException("The selected preview page references a missing project image.");

        var bitmaps = new Dictionary<Guid, SKBitmap>();
        var typefaces = await LoadTypefacesAsync(projectId, layout, page, cancellationToken);
        try
        {
            foreach (var asset in assets.Values)
            {
                var bitmap = SKBitmap.Decode(asset.Data)
                    ?? throw new InvalidDataException($"Project image '{asset.FileName}' could not be decoded for the page preview.");
                if (bitmap.Width <= 0 || bitmap.Height <= 0)
                {
                    bitmap.Dispose();
                    throw new InvalidDataException($"Project image '{asset.FileName}' has invalid dimensions.");
                }
                bitmaps.Add(asset.Id, bitmap);
            }

            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
                ?? throw new InvalidOperationException("The page preview surface could not be created.");
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.White);
            canvas.Scale((float)scale, (float)scale);

            foreach (var item in page.Shapes.Select(shape => new PaintItem(shape.ZIndex, shape))
                .Concat(page.Images.Select(image => new PaintItem(image.ZIndex, image)))
                .Concat(page.Lines.Select(line => new PaintItem(line.ZIndex, line)))
                .OrderBy(item => item.ZIndex))
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (item.Value)
                {
                    case ChapterPreviewShape shape:
                        DrawShape(canvas, page, shape);
                        break;
                    case ChapterPreviewImage image:
                        DrawImage(canvas, page, image, bitmaps[image.AssetId]);
                        break;
                    case ChapterPreviewLine line:
                        DrawLine(canvas, page, line, typefaces);
                        break;
                }
            }

            using var snapshot = surface.Snapshot();
            using var encoded = snapshot.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new InvalidOperationException("The page preview could not be encoded as PNG.");
            return new RasterizedPage(encoded.ToArray(), width, height);
        }
        finally
        {
            foreach (var bitmap in bitmaps.Values)
                bitmap.Dispose();
            foreach (var typeface in typefaces.Values)
                typeface.Dispose();
        }
    }

    private async Task<Dictionary<string, SKTypeface>> LoadTypefacesAsync(
        Guid projectId,
        ChapterPreviewResult layout,
        ChapterPreviewPage page,
        CancellationToken cancellationToken)
    {
        var faces = page.Lines
            .SelectMany(line => line.Runs.Count > 0
                ? line.Runs.Select(run => run.Face)
                : [line.Face ?? "SerifRegular"])
            .Where(face => !string.IsNullOrWhiteSpace(face))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var typefaces = new Dictionary<string, SKTypeface>(StringComparer.Ordinal);
        try
        {
            foreach (var face in faces)
            {
                var descriptor = ResolveFontDescriptor(face, layout.Fonts);
                var data = await projectFonts.ResolveFaceAsync(
                    projectId,
                    descriptor.FamilyKey,
                    descriptor.Weight,
                    descriptor.Italic,
                    requireExact: true,
                    cancellationToken);
                if (data is null)
                    throw new InvalidDataException($"The page preview font '{descriptor.FamilyKey}' {descriptor.Weight}{(descriptor.Italic ? " italic" : string.Empty)} could not be resolved.");

                using var skData = SKData.CreateCopy(data.Data);
                var typeface = SKTypeface.FromData(skData)
                    ?? throw new InvalidDataException($"The page preview font '{descriptor.FamilyKey}' could not be decoded.");
                typefaces.Add(face, typeface);
            }
            return typefaces;
        }
        catch
        {
            foreach (var typeface in typefaces.Values)
                typeface.Dispose();
            throw;
        }
    }

    private static FontDescriptor ResolveFontDescriptor(
        string? face,
        IReadOnlyList<ChapterPreviewFont> fonts)
    {
        face = string.IsNullOrWhiteSpace(face) ? "SerifRegular" : face;
        var declared = fonts.FirstOrDefault(item => string.Equals(item.Face, face, StringComparison.Ordinal));
        if (declared is not null)
            return new FontDescriptor(declared.FamilyKey, declared.Weight, declared.Italic);

        if (face.StartsWith("Custom", StringComparison.Ordinal))
            throw new InvalidDataException($"The page preview returned an undeclared custom font face '{face}'.");

        var familyKey = face.StartsWith("Sans", StringComparison.OrdinalIgnoreCase)
            ? "builtin:nunito"
            : face.StartsWith("Mono", StringComparison.OrdinalIgnoreCase)
                ? "builtin:roboto-mono"
                : "builtin:lora";
        return new FontDescriptor(
            familyKey,
            face.Contains("Bold", StringComparison.OrdinalIgnoreCase) ? 700 : 400,
            face.Contains("Italic", StringComparison.OrdinalIgnoreCase));
    }

    private static void DrawLine(
        SKCanvas canvas,
        ChapterPreviewPage page,
        ChapterPreviewLine line,
        IReadOnlyDictionary<string, SKTypeface> typefaces)
    {
        var originX = line.RotationOriginX ?? line.X;
        var originY = page.HeightPoints - (line.RotationOriginY ?? line.Y);
        canvas.Save();
        canvas.RotateDegrees((float)line.RotationDegrees, (float)originX, (float)originY);
        var baseline = page.HeightPoints - line.Y;
        var x = line.X;
        var runs = line.Runs.Count > 0
            ? line.Runs
            : [new ChapterPreviewRun(line.Text, line.Face ?? "SerifRegular", false, false, 0, 1)];
        foreach (var run in runs)
        {
            if (string.IsNullOrEmpty(run.Text))
                continue;
            if (!typefaces.TryGetValue(run.Face, out var typeface))
                throw new InvalidDataException($"The page preview returned an unrenderable font face '{run.Face}'.");

            using var font = new SKFont(typeface, (float)Math.Max(1, line.Size * run.SizeScale));
            using var paint = new SKPaint
            {
                Color = TextColor(line.FillRgb, line.Opacity),
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
            };
            var runBaseline = baseline - run.BaselineShiftEm * line.Size;
            var runStart = x;
            x += DrawRun(canvas, run.Text, x, runBaseline, font, paint, line.WordSpacing, line.CharacterSpacing);
            if (run.Underline || run.Strikethrough)
            {
                using var decoration = new SKPaint
                {
                    Color = paint.Color,
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = (float)Math.Max(0.5, line.Size * 0.045),
                    StrokeCap = SKStrokeCap.Butt,
                };
                if (run.Underline)
                {
                    var y = runBaseline + line.Size * 0.08;
                    canvas.DrawLine((float)runStart, (float)y, (float)x, (float)y, decoration);
                }
                if (run.Strikethrough)
                {
                    var y = runBaseline - line.Size * 0.3;
                    canvas.DrawLine((float)runStart, (float)y, (float)x, (float)y, decoration);
                }
            }
        }
        canvas.Restore();
    }

    private static double DrawRun(
        SKCanvas canvas,
        string text,
        double x,
        double baseline,
        SKFont font,
        SKPaint paint,
        double wordSpacing,
        double characterSpacing)
    {
        if (Math.Abs(wordSpacing) < 0.001 && Math.Abs(characterSpacing) < 0.001)
        {
            canvas.DrawText(text, (float)x, (float)baseline, font, paint);
            return font.MeasureText(text);
        }

        var start = x;
        foreach (var rune in text.EnumerateRunes())
        {
            var glyph = rune.ToString();
            canvas.DrawText(glyph, (float)x, (float)baseline, font, paint);
            x += font.MeasureText(glyph)
                + characterSpacing
                + (rune.Value is ' ' or '\t' ? wordSpacing : 0);
        }
        return x - start;
    }

    private static void DrawShape(SKCanvas canvas, ChapterPreviewPage page, ChapterPreviewShape shape)
    {
        var top = page.HeightPoints - shape.Y - shape.Height;
        var rect = new SKRect((float)shape.X, (float)top, (float)(shape.X + shape.Width), (float)(top + shape.Height));
        var centerX = rect.MidX;
        var centerY = rect.MidY;
        canvas.Save();
        canvas.RotateDegrees((float)shape.RotationDegrees, centerX, centerY);
        if (shape.FillRgb is not null)
        {
            using var fill = new SKPaint
            {
                Color = TextColor(shape.FillRgb, shape.Opacity),
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
            };
            DrawShapeGeometry(canvas, shape.Kind, rect, fill);
        }
        if (shape.StrokeRgb is not null && shape.StrokeWidth > 0)
        {
            using var stroke = new SKPaint
            {
                Color = TextColor(shape.StrokeRgb, shape.Opacity),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)shape.StrokeWidth,
            };
            DrawShapeGeometry(canvas, shape.Kind, rect, stroke);
        }
        canvas.Restore();
    }

    private static void DrawShapeGeometry(SKCanvas canvas, string kind, SKRect rect, SKPaint paint)
    {
        if (kind.Equals("Ellipse", StringComparison.OrdinalIgnoreCase))
        {
            canvas.DrawOval(rect, paint);
            return;
        }
        if (kind.Equals("Line", StringComparison.OrdinalIgnoreCase))
        {
            canvas.DrawLine(rect.Left, rect.MidY, rect.Right, rect.MidY, paint);
            return;
        }
        canvas.DrawRect(rect, paint);
    }

    private static void DrawImage(
        SKCanvas canvas,
        ChapterPreviewPage page,
        ChapterPreviewImage image,
        SKBitmap bitmap)
    {
        var sourceWidth = image.SourceWidthFraction is > 0 and <= 1
            ? image.SourceWidthFraction
            : 1;
        var sourceLeft = Math.Clamp(image.SourceLeftFraction, 0, 1 - sourceWidth);
        var source = new SKRect(
            (float)(sourceLeft * bitmap.Width),
            0,
            (float)((sourceLeft + sourceWidth) * bitmap.Width),
            bitmap.Height);
        var top = page.HeightPoints - image.Y - image.Height;
        var frame = new SKRect((float)image.X, (float)top, (float)(image.X + image.Width), (float)(top + image.Height));
        var scale = image.Fit.Equals("Cover", StringComparison.OrdinalIgnoreCase)
            ? Math.Max(frame.Width / source.Width, frame.Height / source.Height)
            : Math.Min(frame.Width / source.Width, frame.Height / source.Height);
        var destinationWidth = source.Width * scale;
        var destinationHeight = source.Height * scale;
        var cropX = image.Fit.Equals("Cover", StringComparison.OrdinalIgnoreCase)
            ? (float)Math.Clamp(image.CropX, 0, 1)
            : 0.5f;
        var cropY = image.Fit.Equals("Cover", StringComparison.OrdinalIgnoreCase)
            ? (float)Math.Clamp(image.CropY, 0, 1)
            : 0.5f;
        var destinationLeft = frame.Left + (frame.Width - destinationWidth) * cropX;
        var destinationTop = frame.Top + (frame.Height - destinationHeight) * cropY;
        var destination = new SKRect(
            (float)destinationLeft,
            (float)destinationTop,
            (float)(destinationLeft + destinationWidth),
            (float)(destinationTop + destinationHeight));

        canvas.Save();
        canvas.RotateDegrees((float)image.RotationDegrees, frame.MidX, frame.MidY);
        canvas.ClipRect(frame);
        using var paint = new SKPaint
        {
            Color = SKColors.White.WithAlpha((byte)Math.Clamp(Math.Round(image.Opacity * 255), 0, 255)),
            IsAntialias = true,
        };
        canvas.DrawBitmap(bitmap, source, destination, paint);
        canvas.Restore();
    }

    private static SKColor TextColor(double[]? rgb, double opacity)
    {
        var red = rgb is { Length: > 0 } ? rgb[0] : 0.125;
        var green = rgb is { Length: > 1 } ? rgb[1] : 0.145;
        var blue = rgb is { Length: > 2 } ? rgb[2] : 0.204;
        return new SKColor(
            (byte)Math.Clamp(Math.Round(Math.Clamp(red, 0, 1) * 255), 0, 255),
            (byte)Math.Clamp(Math.Round(Math.Clamp(green, 0, 1) * 255), 0, 255),
            (byte)Math.Clamp(Math.Round(Math.Clamp(blue, 0, 1) * 255), 0, 255),
            (byte)Math.Clamp(Math.Round(opacity * 255), 0, 255));
    }

    private sealed record PreviewStyle(
        Guid Id,
        string Name,
        ManuscriptStyleKind Kind,
        string SemanticRole,
        long Revision,
        ManuscriptStyleProperties Definition);

    private sealed record FontDescriptor(string FamilyKey, int Weight, bool Italic);
    private sealed record RasterizedPage(byte[] Data, int Width, int Height);
    private sealed record PaintItem(int ZIndex, object Value);

    private static object BlockPayload(ManuscriptBlock block) => new
    {
        id = block.Id,
        type = block.Type.ToString(),
        block.StyleRole,
        block.HeadingLevel,
        assetId = block.ImageId,
        caption = ManuscriptCodec.Text(block),
        block.Decorative,
        block.AltText,
        language = string.IsNullOrWhiteSpace(block.Language) ? null : block.Language.Trim(),
        accessibilityRole = block.AccessibilityRole.ToString(),
        presentation = block.FigurePresentation,
        paragraphPresentation = block.ParagraphPresentation,
        pageCompositionId = block.PageCompositionId,
        content = block.Content.Select(inline => new
        {
            type = inline.Type.ToString(),
            inline.Text,
            marks = inline.Marks
                .Where(mark => mark.Type != ManuscriptMarkType.Language || !string.IsNullOrWhiteSpace(mark.Value))
                .Select(mark => new
                {
                    type = mark.Type.ToString(),
                    value = mark.Type == ManuscriptMarkType.Language ? mark.Value?.Trim() : mark.Value,
                }).ToArray(),
        }).ToArray(),
    };

    private static ChapterPreviewPage ToPage(LayoutPage page, int physicalPage)
    {
        var lineOrder = PaintOrder(page.PaintOrder, "line");
        var imageOrder = PaintOrder(page.PaintOrder, "image");
        var shapeOrder = PaintOrder(page.PaintOrder, "shape");
        return new(
            physicalPage,
            page.Kind,
            page.WidthPoints,
            page.HeightPoints,
            page.PageLabel,
            page.Bookmark,
            page.Lines.Select((line, index) => new ChapterPreviewLine(
                line.Text, line.Size, line.X, line.Y, line.RotationDegrees, line.RotationOriginX, line.RotationOriginY, line.Opacity,
                line.FillRgb, line.SemanticRole, line.LinkPage is int target ? $"#page-{target}" : null,
                line.Runs.FirstOrDefault()?.Face,
                line.Runs.Select(run => new ChapterPreviewRun(run.Text, run.Face, run.Underline, run.Strikethrough, run.BaselineShiftEm, run.SizeScale)).ToArray(),
                line.WordSpacing, line.CharacterSpacing,
                lineOrder.GetValueOrDefault(index, page.PaintOrder.Length + index))).ToArray(),
            page.Images.Select((image, index) => (image, index))
                .Where(item => Guid.TryParse(item.image.AssetId, out _))
                .Select(item => new ChapterPreviewImage(
                    Guid.Parse(item.image.AssetId), item.image.X, item.image.Y, item.image.Width, item.image.Height,
                    item.image.RotationDegrees, item.image.Opacity, item.image.Fit, item.image.CropX,
                    item.image.CropY, item.image.SourceLeftFraction, item.image.SourceWidthFraction,
                    item.image.AltText, item.image.Decorative,
                    imageOrder.GetValueOrDefault(item.index, page.PaintOrder.Length + item.index))).ToArray(),
            page.Shapes.Select((shape, index) => new ChapterPreviewShape(
                shape.Kind, shape.X, shape.Y, shape.Width, shape.Height, shape.RotationDegrees,
                shape.Opacity, shape.FillRgb, shape.StrokeRgb, shape.StrokeWidth,
                shapeOrder.GetValueOrDefault(index, page.PaintOrder.Length + index))).ToArray());
    }

    private static Dictionary<int, int> PaintOrder(IEnumerable<LayoutPaint> paints, string kind) => paints
        .Select((paint, order) => (paint, order))
        .Where(item => item.paint.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))
        .ToDictionary(item => item.paint.Index, item => item.order);

    private static Guid GuidUtility(Guid projectId, int index)
    {
        var bytes = projectId.ToByteArray();
        BitConverter.GetBytes(index).CopyTo(bytes, 0);
        return new Guid(bytes);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximum, CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        var buffer = new char[8192];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) return result.ToString();
            if (result.Length + read > maximum) throw new InvalidDataException("Lorekeeper Press exceeded the preview response limit.");
            result.Append(buffer, 0, read);
        }
    }

    private static string Limit(string value) => value.Length <= 2000 ? value : value[..2000];

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        catch (NotSupportedException) { }
    }

    private sealed record LayoutResponse(int ProtocolVersion, string JobId, LayoutPage[] Pages, LayoutPageMap[] PageMap, LayoutDiagnostic[]? Diagnostics);
    private sealed record LayoutDiagnostic(string Severity, string Code, string Message);
    private sealed record LayoutPage(string Kind, double WidthPoints, double HeightPoints, string? PageLabel, string? Bookmark, LayoutPaint[] PaintOrder, LayoutLine[] Lines, LayoutImage[] Images, LayoutShape[] Shapes);
    private sealed record LayoutPaint(string Kind, int Index);
    private sealed record LayoutLine(string Text, double Size, double X, double Y, double WordSpacing, double CharacterSpacing, double RotationDegrees, double? RotationOriginX, double? RotationOriginY, double Opacity, double[]? FillRgb, string? SemanticRole, int? LinkPage, LayoutRun[] Runs);
    private sealed record LayoutRun(string Text, string Face, bool Underline, bool Strikethrough, double BaselineShiftEm, double SizeScale);
    private sealed record LayoutImage(string AssetId, double X, double Y, double Width, double Height, double RotationDegrees, double Opacity, string Fit, double CropX, double CropY, double SourceLeftFraction, double SourceWidthFraction, string? AltText, bool Decorative);
    private sealed record LayoutShape(string Kind, double X, double Y, double Width, double Height, double RotationDegrees, double Opacity, double[]? FillRgb, double[]? StrokeRgb, double StrokeWidth);
    private sealed record LayoutPageMap(string ChapterId, string BlockId, int PageNumber);
    private sealed record PreviewFont(Guid Id, string FamilyKey, int Weight, bool Italic, string RelativePath, string ContentType, string ContentUrl, byte[] Data, string Sha256);

    private static Guid DeterministicFontId(string value) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(value))[..16]);
}
