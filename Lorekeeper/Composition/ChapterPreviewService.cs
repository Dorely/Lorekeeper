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
    IReadOnlyList<ChapterPreviewFont> Fonts);

public sealed record ChapterPreviewFont(string Face, Guid FaceId, int Weight, bool Italic, string ContentType);

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
}

public sealed class ChapterPreviewService(
    AppDbContext db,
    IPublicationPressRuntime press,
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

        var documents = chapters.ToDictionary(
            chapter => chapter.Id,
            chapter => ManuscriptCodec.Deserialize(chapter.ManuscriptJson, chapter.Id, chapter.ManuscriptRevision));
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

        var styles = await db.ManuscriptStyleDefinitions.AsNoTracking()
            .Where(item => item.ProjectId == projectId).OrderBy(item => item.NameKey).ToListAsync(cancellationToken);
        foreach (var style in styles)
        {
            var definition = JsonSerializer.Deserialize<ManuscriptStyleProperties>(style.DefinitionJson, ManuscriptCodec.JsonOptions);
            if (!string.IsNullOrWhiteSpace(definition?.FontFamilyKey))
                usedFontKeys.Add(definition.FontFamilyKey);
        }
        var customFontIds = usedFontKeys
            .Where(key => key.StartsWith("project:", StringComparison.OrdinalIgnoreCase))
            .Select(key => Guid.TryParse(key["project:".Length..], out var id) ? id : Guid.Empty)
            .ToHashSet();
        if (customFontIds.Contains(Guid.Empty))
            throw new InvalidDataException("A Book Text Style or Designed Page references an invalid project font key.");
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
            face.Data,
            Convert.ToHexStringLower(SHA256.HashData(face.Data))))).ToArray();
        var cacheKey = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            setup.Revision,
            Chapters = chapters.Select(item => new { item.Id, item.ManuscriptRevision, item.UpdatedAt }),
            Compositions = compositions.Select(item => new { item.Id, item.Revision, item.UpdatedAt, item.ActiveAuthoringVariantId }),
            Variants = variants.Select(item => new { item.Id, item.Revision, item.UpdatedAt }),
            Assets = assets.Select(item => new { item.Id, item.UpdatedAt }),
            Styles = styles.Select(item => new { item.Id, item.Revision }),
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
                        definition = JsonSerializer.Deserialize<JsonElement>(style.DefinitionJson, ManuscriptCodec.JsonOptions),
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
            var result = new ChapterPreviewResult(
                pages,
                cacheKey,
                (response.Diagnostics ?? []).Select(diagnostic => new ChapterPreviewDiagnostic(
                    diagnostic.Severity,
                    diagnostic.Code,
                    diagnostic.Message)).ToArray(),
                previewFonts.Select((font, index) => new ChapterPreviewFont(
                    $"Custom{index}", font.Id, font.Weight, font.Italic, font.ContentType)).ToArray());
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
                    item.image.CropY, item.image.AltText, item.image.Decorative,
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
    private sealed record LayoutImage(string AssetId, double X, double Y, double Width, double Height, double RotationDegrees, double Opacity, string Fit, double CropX, double CropY, string? AltText, bool Decorative);
    private sealed record LayoutShape(string Kind, double X, double Y, double Width, double Height, double RotationDegrees, double Opacity, double[]? FillRgb, double[]? StrokeRgb, double StrokeWidth);
    private sealed record LayoutPageMap(string ChapterId, string BlockId, int PageNumber);
    private sealed record PreviewFont(Guid Id, string FamilyKey, int Weight, bool Italic, string RelativePath, string ContentType, byte[] Data, string Sha256);
}
