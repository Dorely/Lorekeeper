namespace Lorekeeper.ImportExport;

public sealed record ManuscriptExportOptions(bool IncludeSynopses = false);

public sealed record ManuscriptExportDocument(
    Guid ProjectId,
    string ProjectName,
    string ProjectSlug,
    DateTime ExportedAtUtc,
    ManuscriptExportOptions Options,
    IReadOnlyList<ManuscriptExportSection> Sections)
{
    public IEnumerable<ManuscriptExportChapter> OrderedChapters() =>
        Sections.SelectMany(section => section.Chapters);
}

public sealed record ManuscriptExportSection(
    Guid? ActId,
    string Title,
    string Synopsis,
    bool IsUnassigned,
    IReadOnlyList<ManuscriptExportChapter> Chapters);

public sealed record ManuscriptExportChapter(
    Guid Id,
    Guid? ActId,
    string Title,
    string Body,
    string Synopsis,
    int Order);
