using System.Security.Cryptography;
using System.Text;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;

namespace Lorekeeper.Publish;

internal sealed record PublicationSemanticDocument(
    string Id, string Title, ManuscriptDocument Manuscript, IReadOnlyList<PublishDesignedPageDocument> DesignedPages);

internal static class PublicationSemanticDocuments
{
    public static IEnumerable<PublicationSemanticDocument> Enumerate(PublishDocument document)
    {
        foreach (var item in Sections(PublicationSectionAnchor.Front)) yield return item;
        foreach (var act in document.Sections)
        {
            foreach (var item in Sections(PublicationSectionAnchor.BeforeAct, PublishOutlineTargetKind.Act, act.ActId)) yield return item;
            foreach (var chapter in act.Chapters)
            {
                foreach (var item in Sections(PublicationSectionAnchor.BeforeChapter, PublishOutlineTargetKind.Chapter, chapter.Id)) yield return item;
                yield return new($"chapter:{chapter.Id:D}", chapter.Title, chapter.Manuscript, chapter.DesignedPages);
                foreach (var item in Sections(PublicationSectionAnchor.AfterChapter, PublishOutlineTargetKind.Chapter, chapter.Id)) yield return item;
            }
            foreach (var item in Sections(PublicationSectionAnchor.AfterAct, PublishOutlineTargetKind.Act, act.ActId)) yield return item;
        }
        foreach (var item in Sections(PublicationSectionAnchor.Back)) yield return item;

        IEnumerable<PublicationSemanticDocument> Sections(PublicationSectionAnchor anchor, PublishOutlineTargetKind? kind = null, Guid? id = null) =>
            document.PublicationSections.Where(section => section.Anchor == anchor && section.TargetKind == kind && section.TargetId == id)
                .OrderBy(section => section.LocalOrder).ThenBy(section => section.Id)
                .Select(section => new PublicationSemanticDocument($"publication-section:{section.Id:D}", section.Title, section.Manuscript, section.DesignedPages));
    }
}

internal sealed record PublicationNoteOccurrence(ManuscriptNote Note, int Number, IReadOnlyList<string> PlacementPath);

/// <summary>Output-only note identities and numbering for a complete top-level document, including repeated pages.</summary>
internal sealed class PublicationNotes
{
    public ManuscriptDocument Manuscript { get; private set; } = null!;
    public Dictionary<string, ManuscriptDocument> Placements { get; } = new(StringComparer.Ordinal);
    public List<PublicationNoteOccurrence> Occurrences { get; } = [];
    public Dictionary<string, int> Numbers { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> SourceNoteIds { get; } = new(StringComparer.Ordinal);

    public static PublicationNotes Create(PublishDocument publication, PublicationSemanticDocument container)
    {
        var result = new PublicationNotes();
        var counters = new Dictionary<ManuscriptNoteKind, int>();
        result.Manuscript = Project(container.Manuscript, []);
        return result;

        ManuscriptDocument Project(ManuscriptDocument owner, IReadOnlyList<string> placementPath)
        {
            owner = ManuscriptLists.Resolve(owner);
            var projectedNotes = new List<ManuscriptNote>();
            var content = Blocks(owner.Content);
            return owner with { Content = content, Notes = projectedNotes };

            List<ManuscriptBlock> Blocks(IEnumerable<ManuscriptBlock> blocks) => blocks.Select(block =>
            {
                if (block.Type == ManuscriptBlockType.DesignedPage && block.DesignedPageId is Guid pageId)
                {
                    var page = container.DesignedPages.Single(item => item.Id == pageId);
                    result.Placements.Add(block.Id, Project(page.SemanticManuscript, [.. placementPath, $"placement:{block.Id}"]));
                }
                if (block.Table is { } table)
                    return block with { Table = table with { Rows = table.Rows.Select(row => row with
                    { Cells = row.Cells.Select(cell => cell with { Content = Blocks(cell.Content) }).ToList() }).ToList() } };
                return block with { Content = block.Content.Select(inline =>
                {
                    if (inline.Type != ManuscriptInlineType.NoteReference) return inline;
                    var note = owner.Notes.Single(note => note.Id == inline.NoteId);
                    var identity = $"{publication.EditionId:D}|{container.Id}|{string.Join('/', placementPath)}|{inline.Id}|{note.Id}";
                    var anchor = "n" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24].ToLowerInvariant();
                    var projected = note with
                    {
                        Id = anchor,
                        Content = note.Content.Select(noteBlock => noteBlock with
                        {
                            Id = anchor + "_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(noteBlock.Id)))[..16].ToLowerInvariant(),
                        }).ToList(),
                    };
                    var number = counters.GetValueOrDefault(note.Kind) + 1;
                    counters[note.Kind] = number;
                    result.Numbers.Add(anchor, number);
                    result.SourceNoteIds.Add(anchor, note.Id);
                    result.Occurrences.Add(new(projected, number, placementPath));
                    projectedNotes.Add(projected);
                    return inline with { NoteId = anchor };
                }).ToList() };
            }).ToList();
        }
    }

}

internal static class PublicationTextBackMatter
{
    public static string Notes(PublishDocument publication, bool markdown)
    {
        var result = new StringBuilder();
        var citations = new PublicationCitationResolver(publication.Citations.Occurrences);
        var hasHeading = false;
        foreach (var container in PublicationSemanticDocuments.Enumerate(publication))
        {
            var notes = PublicationNotes.Create(publication, container);
            var citationNotes = publication.Citations.Occurrences.Where(citation => citation.Identity.TopLevelContainer == container.Id
                && citation.NoteRuns is not null).ToList();
            if (notes.Occurrences.Count == 0 && citationNotes.Count == 0) continue;
            if (!hasHeading)
            {
                result.AppendLine(markdown ? "## Endnotes" : "Endnotes").AppendLine();
                hasHeading = true;
            }
            result.AppendLine(markdown ? "### " + Escape(container.Title) : container.Title).AppendLine();
            foreach (var kind in notes.Occurrences.GroupBy(note => note.Note.Kind))
            {
                var label = kind.Key == ManuscriptNoteKind.Footnote ? "Footnotes" : "Author notes";
                result.AppendLine(markdown ? "#### " + label : label).AppendLine();
                foreach (var occurrence in kind)
                {
                    var resolve = citations.ForDocument(container.Id, occurrence.PlacementPath);
                    if (markdown)
                        result.AppendLine(SemanticPublishFormatting.MarkdownNote(occurrence.Note, occurrence.Number, Asset, resolve)).AppendLine();
                    else
                        result.Append(occurrence.Number).Append(". ").AppendLine(string.Join(Environment.NewLine,
                            occurrence.Note.Content.Select(block => SemanticPublishFormatting.PlainTextBlock(block, Asset, notes.Numbers, resolve)))).AppendLine();
                }
            }
            if (citationNotes.Count > 0)
            {
                result.AppendLine(markdown ? "#### Citations" : "Citations").AppendLine();
                foreach (var citation in citationNotes)
                {
                    if (markdown)
                        result.Append("<a id=\"citation-note-").Append(PublicationCitationResolver.Anchor(citation)).AppendLine("\"></a>").AppendLine();
                    result.Append(citation.NoteNumber).Append(". ").Append(markdown
                        ? SemanticPublishFormatting.CitationMarkdown(citation.NoteRuns!) : citation.NoteText);
                    if (markdown)
                        result.Append(" [↩](#citation-ref-").Append(PublicationCitationResolver.Anchor(citation)).Append(')');
                    result.AppendLine().AppendLine();
                }
            }
        }
        return result.ToString().TrimEnd();

        PublishAssetDocument? Asset(Guid id) => publication.Assets.FirstOrDefault(asset => asset.Id == id);
        static string Escape(string value) => SemanticPublishFormatting.CitationMarkdown([new(value)]);
    }
}
