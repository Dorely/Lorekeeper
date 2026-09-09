using Lorekeeper.Composition;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;

namespace Lorekeeper.Printing;

public sealed record PrintSourcePage(double WidthPoints, double HeightPoints, bool PhysicalSizeKnown = true)
{
    public bool HasPhysicalSize => PhysicalSizeKnown && WidthPoints > 0 && HeightPoints > 0;
}

public sealed class PrintSource : IDisposable
{
    private byte[]? _bytes;
    public Guid ProjectId { get; }
    public string Title { get; }
    public string Revision { get; }
    public string SourceId { get; }
    public IReadOnlyList<PrintSourcePage> Pages { get; }
    public int InitialPage { get; }
    internal bool IsPdf { get; }
    public bool HasPhysicalSize => Pages.Count > 0 && Pages.All(page => page.HasPhysicalSize);
    internal byte[] Bytes => _bytes ?? throw new ObjectDisposedException(nameof(PrintSource));

    internal PrintSource(Guid projectId, string title, string revision, string sourceId, byte[] bytes, IReadOnlyList<PrintSourcePage> pages, int initialPage, bool isPdf = false)
    {
        ProjectId = projectId; Title = title; Revision = revision; SourceId = sourceId; _bytes = bytes;
        Pages = pages; InitialPage = initialPage; IsPdf = isPdf;
    }

    public void Dispose() => Interlocked.Exchange(ref _bytes, null);
}

public enum PrintFit { Fit, Fill, ActualSize }
public enum PrintSelection { Current, All, Range }

public sealed class PrintSettings
{
    public string Paper { get; set; } = "Letter";
    public bool Landscape { get; set; }
    public double MarginInches { get; set; } = .25;
    public PrintFit Fit { get; set; } = PrintFit.Fit;
    public PrintSelection Selection { get; set; } = PrintSelection.Current;
    public string Range { get; set; } = "";
    public int CurrentPage { get; set; } = 1;
}

public sealed class PrintingOptions
{
    public const string SectionName = "Printing";
    public long MaxSourceBytes { get; set; } = 256L * 1024 * 1024;
    public int MaxPagesPerJob { get; set; } = 200;
    public int MaxSessions { get; set; } = 32;
    public int SessionMinutes { get; set; } = 15;
    public long MaxSessionBytes { get; set; } = 512L * 1024 * 1024;
    public int MaxRasterPixels { get; set; } = 36_000_000;
}

public sealed record PrintPreparedJob(Guid Id, Guid ProjectId, string Title, double WidthInches, double HeightInches, IReadOnlyList<string> PageUrls, string DocumentUrl, IReadOnlyList<string> Warnings);

public interface IPrintPreparationService
{
    Task<PrintSource> CreateImageAsync(Guid projectId, string title, Stream bytes, CancellationToken cancellationToken = default);
    Task<PrintSource> CreatePdfAsync(Guid projectId, Guid artifactId, int initialPage, CancellationToken cancellationToken = default);
    Task<PrintSource> CreateCompositionAsync(Guid projectId, Guid targetId, long revision, string title, CompositionScene scene, ManuscriptDocument? semantic = null, IReadOnlyDictionary<string, string>? textBindings = null, CancellationToken cancellationToken = default, string? backgroundColor = null);
    Task<PrintPreparedJob> PrepareAsync(PrintSource source, PrintSettings settings, CancellationToken cancellationToken = default);
    void Release(PrintPreparedJob job);
}
