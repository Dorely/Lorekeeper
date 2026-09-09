using System.Globalization;
using Lorekeeper.Composition;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Publish;
using Microsoft.Extensions.Options;
using SkiaSharp;
using UglyToad.PdfPig;

namespace Lorekeeper.Printing;

public sealed class PrintSessionStore : IDisposable
{
    private readonly Dictionary<Guid, PrintSession> _sessions = [];
    private readonly object _gate = new();
    private readonly Timer _sweeper;
    private readonly PrintingOptions _options;
    private long _bytes;

    public PrintSessionStore(IOptions<PrintingOptions> options)
    {
        _options = options.Value;
        _sweeper = new(_ => Sweep(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public void Add(PrintPreparedJob job, IReadOnlyList<byte[]> pages)
    {
        var bytes = pages.Sum(page => page.LongLength);
        lock (_gate)
        {
            SweepLocked();
            if (_sessions.Count >= _options.MaxSessions || bytes > _options.MaxSessionBytes - _bytes)
                throw new InvalidOperationException("Print preview capacity is full. Close another preview or select fewer pages.");
            _sessions.Add(job.Id, new(job, pages, DateTimeOffset.UtcNow.AddMinutes(_options.SessionMinutes), bytes));
            _bytes += bytes;
        }
    }

    public bool TryGet(Guid id, out PrintSession? session)
    {
        lock (_gate)
        {
            SweepLocked();
            return _sessions.TryGetValue(id, out session);
        }
    }

    public void Remove(Guid id)
    {
        lock (_gate)
        {
            if (_sessions.Remove(id, out var removed)) _bytes -= removed.ByteLength;
        }
    }

    private void Sweep() { lock (_gate) SweepLocked(); }

    private void SweepLocked()
    {
        foreach (var pair in _sessions.Where(item => item.Value.Expires <= DateTimeOffset.UtcNow).ToArray())
        {
            _sessions.Remove(pair.Key);
            _bytes -= pair.Value.ByteLength;
        }
    }

    public void Dispose()
    {
        _sweeper.Dispose();
        lock (_gate) { _sessions.Clear(); _bytes = 0; }
    }
}

public sealed record PrintSession(PrintPreparedJob Job, IReadOnlyList<byte[]> Pages, DateTimeOffset Expires, long ByteLength);

public sealed class PrintPreparationService(
    IPublicationRenderService renders,
    ICompositionCanvasPreviewService compositions,
    PrintSessionStore sessions,
    IOptions<PrintingOptions> options) : IPrintPreparationService
{
    private const int TargetDpi = 300;
    private static readonly SemaphoreSlim RenderGate = new(1, 1);
    private static readonly IReadOnlyDictionary<string, (double W, double H)> PaperSizes =
        new Dictionary<string, (double, double)>(StringComparer.Ordinal)
        {
            ["Letter"] = (8.5, 11), ["A4"] = (210 / 25.4, 297 / 25.4),
            ["Legal"] = (8.5, 14), ["A3"] = (297 / 25.4, 420 / 25.4),
        };

    public async Task<PrintSource> CreateImageAsync(Guid projectId, string title, Stream bytes, CancellationToken cancellationToken = default)
    {
        var data = await ReadBoundedAsync(bytes, cancellationToken);
        using var codec = SKCodec.Create(new MemoryStream(data, writable: false))
            ?? throw new InvalidDataException("The image could not be decoded.");
        ValidateRaster(codec.Info.Width, codec.Info.Height);
        var rotated = codec.EncodedOrigin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var width = rotated ? codec.Info.Height : codec.Info.Width;
        var height = rotated ? codec.Info.Width : codec.Info.Height;
        var sourceId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));
        return new(projectId, title, sourceId, sourceId, data,
            [new(width * 72d / TargetDpi, height * 72d / TargetDpi, false)], 1);
    }

    public async Task<PrintSource> CreatePdfAsync(Guid projectId, Guid artifactId, int initialPage, CancellationToken cancellationToken = default)
    {
        var artifact = await renders.GetArtifactAsync(projectId, artifactId, cancellationToken)
            ?? throw new KeyNotFoundException("The print artifact was not found in this project.");
        if (!artifact.MediaType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected artifact is not a PDF.");
        if (artifact.Data.LongLength > options.Value.MaxSourceBytes)
            throw new InvalidDataException("The PDF source exceeds the print size limit.");
        var data = artifact.Data.ToArray();
        var pages = await Task.Run(() =>
        {
            using var pdf = PdfDocument.Open(data);
            var result = new List<PrintSourcePage>();
            foreach (var page in pdf.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var geometry = new PrintSourcePage((double)page.Width, (double)page.Height);
                ValidateGeometry(geometry);
                result.Add(geometry);
            }
            return result.ToArray();
        }, cancellationToken);
        if (initialPage < 1 || initialPage > pages.Length)
            throw new InvalidDataException("The selected PDF page is unavailable.");
        return new(projectId, artifact.FileName, artifact.Sha256, artifactId.ToString("N"), data, pages, initialPage, true);
    }

    public async Task<PrintSource> CreateCompositionAsync(Guid projectId, Guid targetId, long revision, string title,
        CompositionScene scene, ManuscriptDocument? semantic = null,
        IReadOnlyDictionary<string, string>? textBindings = null, CancellationToken cancellationToken = default,
        string? backgroundColor = null)
    {
        ValidateGeometry(new(scene.Surface.WidthPoints, scene.Surface.HeightPoints));
        await RenderGate.WaitAsync(cancellationToken);
        try
        {
            var edge = Math.Min(6000, (int)Math.Sqrt(options.Value.MaxRasterPixels));
            var preview = await compositions.RenderSceneAtResolutionAsync(projectId, targetId, revision, scene,
                semantic ?? ManuscriptCodec.CreateEmpty(targetId), CompositionCanvasPreviewMode.Clean, edge, cancellationToken, textBindings, backgroundColor);
            cancellationToken.ThrowIfCancellationRequested();
            if (preview.Diagnostics.Any(item => item.Severity.Equals("error", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The composition contains errors that prevent printing.");
            if (preview.Data.LongLength > options.Value.MaxSourceBytes)
                throw new InvalidDataException("The composed page exceeds the print size limit.");
            return new(projectId, title, revision.ToString(CultureInfo.InvariantCulture), targetId.ToString("N"), preview.Data,
                [new(preview.SurfaceWidthPoints, preview.SurfaceHeightPoints)], 1);
        }
        finally { RenderGate.Release(); }
    }

    public async Task<PrintPreparedJob> PrepareAsync(PrintSource source, PrintSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!PaperSizes.TryGetValue(settings.Paper, out var paper) || !Enum.IsDefined(settings.Fit) || !Enum.IsDefined(settings.Selection))
            throw new InvalidDataException("Choose a valid paper size and fitting option.");
        if (!double.IsFinite(settings.MarginInches) || settings.MarginInches < 0 || settings.MarginInches > 2)
            throw new InvalidDataException("Margins must be between 0 and 2 inches.");
        if (settings.Fit == PrintFit.ActualSize && !source.HasPhysicalSize)
            throw new InvalidDataException("This source has no physical size. Choose Fit or Fill.");
        if (settings.Landscape) (paper.W, paper.H) = (paper.H, paper.W);
        var selected = SelectPages(source, settings);
        var bytes = source.Bytes;
        await RenderGate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => PrepareSheets(source, bytes, selected, settings, paper, cancellationToken), cancellationToken);
        }
        finally { RenderGate.Release(); }
    }

    private PrintPreparedJob PrepareSheets(PrintSource source, byte[] bytes, IReadOnlyList<int> selected,
        PrintSettings settings, (double W, double H) paper, CancellationToken cancellationToken)
    {
        var pageBytes = new List<byte[]>(selected.Count);
        var warnings = new List<string>();
        long totalBytes = 0;
        var pixelsW = (int)Math.Round(paper.W * TargetDpi);
        var pixelsH = (int)Math.Round(paper.H * TargetDpi);
        ValidateRaster(pixelsW, pixelsH);
        if ((long)pixelsW * pixelsH * 4 * selected.Count > options.Value.MaxSessionBytes)
            throw new InvalidDataException("This selection exceeds the print preview memory limit. Select a smaller page range.");
        var availableW = paper.W - settings.MarginInches * 2;
        var availableH = paper.H - settings.MarginInches * 2;
        foreach (var index in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var geometry = source.Pages[index];
            ValidateGeometry(geometry);
            var w = geometry.WidthPoints / 72;
            var h = geometry.HeightPoints / 72;
            var scale = settings.Fit switch
            {
                PrintFit.ActualSize => 1,
                PrintFit.Fill => Math.Max(availableW / w, availableH / h),
                _ => Math.Min(availableW / w, availableH / h),
            };
            var drawW = w * scale;
            var drawH = h * scale;
            using var raster = RenderSource(bytes, source.IsPdf, index, drawW, drawH);
            using var sheet = SKSurface.Create(new SKImageInfo(pixelsW, pixelsH, SKColorType.Rgba8888, SKAlphaType.Premul))
                ?? throw new InvalidOperationException("The print sheet could not be created.");
            sheet.Canvas.Clear(SKColors.White);
            sheet.Canvas.ClipRect(new((float)(settings.MarginInches * TargetDpi), (float)(settings.MarginInches * TargetDpi),
                (float)((paper.W - settings.MarginInches) * TargetDpi), (float)((paper.H - settings.MarginInches) * TargetDpi)));
            var x = (paper.W - drawW) / 2;
            var y = (paper.H - drawH) / 2;
            using var paint = new SKPaint { IsAntialias = true };
            using var sourceImage = SKImage.FromBitmap(raster);
            sheet.Canvas.DrawImage(sourceImage,
                new SKRect((float)(x * TargetDpi), (float)(y * TargetDpi), (float)((x + drawW) * TargetDpi), (float)((y + drawH) * TargetDpi)),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
            var dpi = Math.Min(raster.Width / drawW, raster.Height / drawH);
            if (dpi < TargetDpi - 1)
                warnings.Add($"Page {index + 1}: source detail is approximately {dpi:0} DPI at this size (target {TargetDpi} DPI).");
            if (settings.Fit == PrintFit.ActualSize && (drawW > availableW || drawH > availableH))
                warnings.Add($"Page {index + 1}: actual size extends beyond the printable area and will be clipped.");
            using var image = sheet.Snapshot();
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            var data = encoded.ToArray();
            totalBytes += data.LongLength;
            if (totalBytes > options.Value.MaxSessionBytes)
                throw new InvalidDataException("This print job is too large. Select fewer pages.");
            pageBytes.Add(data);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var id = Guid.NewGuid();
        var path = $"/projects/{source.ProjectId:D}/print-sessions/{id:N}";
        var urls = Enumerable.Range(1, pageBytes.Count).Select(i => $"{path}/sheets/{i}.png").ToArray();
        var job = new PrintPreparedJob(id, source.ProjectId, source.Title, paper.W, paper.H, urls, $"{path}/document", warnings);
        sessions.Add(job, pageBytes);
        return job;
    }

    private SKBitmap RenderSource(byte[] bytes, bool isPdf, int index, double widthInches, double heightInches)
    {
        if (!isPdf) return DecodeImage(bytes);
        var width = widthInches * TargetDpi;
        var height = heightInches * TargetDpi;
        var limit = Math.Min(1, Math.Min(12000 / Math.Max(width, height), Math.Sqrt(options.Value.MaxRasterPixels / (width * height))));
        return PublicationArtifactPreviewService.RenderBitmap(bytes, index,
            Math.Max(1, (int)Math.Floor(width * limit)), Math.Max(1, (int)Math.Floor(height * limit)), options.Value.MaxRasterPixels);
    }

    public void Release(PrintPreparedJob job) => sessions.Remove(job.Id);

    private static SKBitmap DecodeImage(byte[] bytes)
    {
        using var codec = SKCodec.Create(new MemoryStream(bytes, writable: false))
            ?? throw new InvalidDataException("The image could not be decoded.");
        var bitmap = SKBitmap.Decode(codec) ?? throw new InvalidDataException("The image could not be decoded.");
        if (codec.EncodedOrigin == SKEncodedOrigin.TopLeft) return bitmap;
        using (bitmap)
        {
            var w = bitmap.Width;
            var h = bitmap.Height;
            var rotated = codec.EncodedOrigin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
            var output = new SKBitmap(rotated ? h : w, rotated ? w : h, SKColorType.Rgba8888, SKAlphaType.Premul);
            try
            {
                using var canvas = new SKCanvas(output);
                var matrix = codec.EncodedOrigin switch
                {
                    SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
                    SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
                    SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
                    SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
                    SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
                    SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
                    SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
                    _ => SKMatrix.Identity,
                };
                canvas.SetMatrix(matrix);
                canvas.DrawBitmap(bitmap, 0, 0);
                return output;
            }
            catch { output.Dispose(); throw; }
        }
    }

    private IReadOnlyList<int> SelectPages(PrintSource source, PrintSettings settings)
    {
        if (source.Pages.Count == 0) throw new InvalidDataException("The print source contains no pages.");
        if (settings.Selection == PrintSelection.Current)
        {
            if (settings.CurrentPage < 1 || settings.CurrentPage > source.Pages.Count)
                throw new InvalidDataException("Choose a current page within the document.");
            return [settings.CurrentPage - 1];
        }
        if (settings.Selection == PrintSelection.All)
        {
            if (source.Pages.Count > options.Value.MaxPagesPerJob) throw new InvalidDataException("Select a smaller page range for this print job.");
            return Enumerable.Range(0, source.Pages.Count).ToArray();
        }
        var result = new SortedSet<int>();
        if (string.IsNullOrWhiteSpace(settings.Range) || settings.Range.Length > 4096)
            throw new InvalidDataException("Enter a page range such as 1-3, 5.");
        foreach (var token in settings.Range.Split(','))
        {
            var parts = token.Trim().Split('-');
            if (parts.Length is < 1 or > 2 || !int.TryParse(parts[0], out var start) || !int.TryParse(parts[^1], out var end)
                || start < 1 || end < start || end > source.Pages.Count)
                throw new InvalidDataException("Enter valid page numbers or ranges, such as 1-3, 5.");
            if (end - start >= options.Value.MaxPagesPerJob) throw new InvalidDataException("Select a smaller page range for this print job.");
            for (var i = start; i <= end; i++) result.Add(i - 1);
            if (result.Count > options.Value.MaxPagesPerJob) throw new InvalidDataException("Select a smaller page range for this print job.");
        }
        return result.ToArray();
    }

    private void ValidateRaster(int width, int height)
    {
        if (width < 1 || height < 1 || width > 12000 || height > 12000 || (long)width * height > options.Value.MaxRasterPixels)
            throw new InvalidDataException("The image exceeds the supported print raster dimensions.");
    }

    private static void ValidateGeometry(PrintSourcePage page)
    {
        if (!double.IsFinite(page.WidthPoints) || !double.IsFinite(page.HeightPoints)
            || page.WidthPoints <= 0 || page.HeightPoints <= 0 || page.WidthPoints > 72000 || page.HeightPoints > 72000)
            throw new InvalidDataException("The print source has invalid page dimensions.");
    }

    private async Task<byte[]> ReadBoundedAsync(Stream input, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + read > options.Value.MaxSourceBytes) throw new InvalidDataException("The print source exceeds the size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
