using Lorekeeper.Manuscripts;

namespace Lorekeeper.ImportExport;

/// <summary>Rewrites external citation identities before imported manuscripts reach their owners.</summary>
public static class ProjectCitationRemapping
{
    public static IEnumerable<ManuscriptDocument> Manuscripts(ProjectExportDocument document)
    {
        var json = document.Chapters.Select(item => item.ManuscriptJson)
            .Concat(document.PublicationSections.Select(item => item.ManuscriptJson))
            .Concat(document.DesignedPages.SelectMany(page => page.Contents).Select(item => item.SemanticManuscriptJson))
            .Concat(document.PublicationEditions.SelectMany(edition => edition.ChapterOverrides).Select(item => item.ManuscriptJson))
            .Concat(document.PublicationEditions.SelectMany(edition => edition.Matter ?? []).Select(item => item.ManuscriptJson))
            .Concat((document.PublicationBook?.Matter ?? []).Select(item => item.ManuscriptJson));
        foreach (var value in json.Where(value => !string.IsNullOrWhiteSpace(value)))
            yield return System.Text.Json.JsonSerializer.Deserialize<ManuscriptDocument>(value, ManuscriptCodec.JsonOptions)
                ?? throw new InvalidDataException("Imported manuscript is missing.");
    }

    public static ProjectExportDocument Remap(
        ProjectExportDocument document,
        IReadOnlyDictionary<Guid, Guid> bibliography,
        IReadOnlyDictionary<Guid, Guid> locations) => RewriteManuscripts(document, json => Rewrite(json, bibliography, locations));

    public static ProjectExportDocument OmitSourceEvidence(ProjectExportDocument document) =>
        RewriteManuscripts(document, json => RewriteItems(json, item => item with { SourceLocationId = null }));

    private static ProjectExportDocument RewriteManuscripts(ProjectExportDocument document, Func<string, string> rewrite) => document with
    {
        Chapters = document.Chapters.Select(item => item with { ManuscriptJson = rewrite(item.ManuscriptJson) }).ToList(),
        PublicationSections = document.PublicationSections.Select(item => item with { ManuscriptJson = rewrite(item.ManuscriptJson) }).ToList(),
        DesignedPages = document.DesignedPages.Select(page => page with
        {
            Contents = page.Contents.Select(item => item with { SemanticManuscriptJson = rewrite(item.SemanticManuscriptJson) }).ToList(),
        }).ToList(),
        PublicationEditions = document.PublicationEditions.Select(edition => edition with
        {
            ChapterOverrides = edition.ChapterOverrides.Select(item => item with { ManuscriptJson = rewrite(item.ManuscriptJson) }).ToList(),
            Matter = edition.Matter?.Select(item => item with { ManuscriptJson = rewrite(item.ManuscriptJson) }).ToList(),
        }).ToList(),
        PublicationBook = document.PublicationBook is not { } book ? null : book with
        {
            Matter = book.Matter?.Select(item => item with { ManuscriptJson = rewrite(item.ManuscriptJson) }).ToList(),
        },
    };

    public static string Rewrite(string json, IReadOnlyDictionary<Guid, Guid> bibliography, IReadOnlyDictionary<Guid, Guid> locations)
        => RewriteItems(json, item => item with
        {
            BibliographicRecordId = Required(bibliography, item.BibliographicRecordId, "bibliography"),
            SourceLocationId = item.SourceLocationId is Guid id ? Required(locations, id, "source location") : null,
        });

    private static string RewriteItems(string json, Func<ManuscriptCitationItem, ManuscriptCitationItem> rewrite)
    {
        if (string.IsNullOrWhiteSpace(json)) return json;
        var document = System.Text.Json.JsonSerializer.Deserialize<ManuscriptDocument>(json, ManuscriptCodec.JsonOptions)
            ?? throw new InvalidDataException("Imported manuscript is missing.");
        return ManuscriptCodec.Serialize(document with
        {
            Content = document.Content.Select(Block).ToList(),
            Notes = document.Notes.Select(note => note with { Content = note.Content.Select(Block).ToList() }).ToList(),
        });

        ManuscriptBlock Block(ManuscriptBlock block) => block with
        {
            Content = block.Content.Select(inline => inline.Citation is not { } citation ? inline : inline with
            {
                Citation = citation with
                {
                    Items = citation.Items.Select(rewrite).ToList(),
                },
            }).ToList(),
            Table = block.Table is not { } table ? null : table with
            {
                Rows = table.Rows.Select(row => row with
                {
                    Cells = row.Cells.Select(cell => cell with { Content = cell.Content.Select(Block).ToList() }).ToList(),
                }).ToList(),
            },
        };
    }

    private static Guid Required(IReadOnlyDictionary<Guid, Guid> map, Guid id, string kind) =>
        map.TryGetValue(id, out var mapped) && mapped != Guid.Empty
            ? mapped : throw new InvalidDataException($"Imported citation has a missing {kind} dependency {id:D}.");
}
