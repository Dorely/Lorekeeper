using Lorekeeper.Citations;
using Lorekeeper.Models;

namespace Lorekeeper.Manuscripts.Import;

/// <summary>Detached, bounded conversion result. No author data is written while parsing.</summary>
public sealed record SemanticImportFragment(
    ManuscriptDocument Document,
    SemanticImportResources Resources,
    IReadOnlyList<string> Report,
    IReadOnlyDictionary<string, Guid> SourceMappings);

public sealed record SemanticImportResources(
    IReadOnlyList<SemanticImportImage> Images,
    IReadOnlyList<SemanticImportStyle> Styles,
    IReadOnlyList<CitationRecord> Bibliography);

public sealed record SemanticImportImage(Guid Id, string FileName, string ContentType, byte[] Data);
public sealed record SemanticImportStyle(Guid Id, string Name, ManuscriptStyleKind Kind,
    string Role, ManuscriptStyleProperties Definition);

public interface ISemanticImportService
{
    Task<SemanticImportFragment> ReadDocxAsync(byte[] bytes, CancellationToken cancellationToken = default);
    Task<SemanticImportFragment> ReadWordHtmlAsync(string html, IReadOnlyList<SemanticImportImage>? clipboardImages = null,
        CancellationToken cancellationToken = default);
    Task AdmitAsync(Lorekeeper.Persistence.AppDbContext db, Guid projectId, SemanticImportResources resources,
        IReadOnlyList<ManuscriptDocument> documents, CancellationToken cancellationToken);
}

public static class SemanticImportLimits
{
    public const int MaximumInputBytes = 32 * 1024 * 1024;
    public const int MaximumHtmlCharacters = 4 * 1024 * 1024;
    public const int MaximumFragmentBytes = 24 * 1024 * 1024;
    public const int MaximumNodes = 100_000;
    public const int MaximumDepth = 64;
    public const int MaximumImages = 200;
    public const int MaximumStyles = 512;
    public const int MaximumBibliography = 2_000;
    public const long MaximumImagePixels = 40_000_000;
}
