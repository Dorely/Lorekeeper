namespace Lorekeeper.Ingest;

public sealed class BookArtifactIngestOptions
{
    public const string SectionName = "Ingest:Artifacts";

    public int MaxFileBytes { get; set; } = 100 * 1024 * 1024;
    public int MaxPdfPages { get; set; } = 500;
    public int PdfVisionDpi { get; set; } = 144;
    public int MaxImagePixels { get; set; } = 4_000_000;
    public int EmbeddedTextMinCharsPerPage { get; set; } = 40;
    public int VisionPageMaxOutputTokens { get; set; } = 6000;
}

public sealed record PdfArtifactIngestOptions(
    bool ForceVision = false,
    int? MaxPages = null,
    int? VisionDpi = null,
    int? MaxImagePixels = null);
