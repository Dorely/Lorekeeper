using Lorekeeper.Citations;
using Lorekeeper.Manuscripts;

namespace Lorekeeper.Publish;

internal static class PublicationCitationTraversal
{
    public static IReadOnlyList<CitationClusterOccurrence> Enumerate(PublishDocument document)
    {
        var result = new List<CitationClusterOccurrence>();
        var target = document.EditionId == Guid.Empty ? "core" : $"release:{document.EditionId:D}";

        foreach (var container in PublicationSemanticDocuments.Enumerate(document))
            AddDocument(container.Manuscript, container.DesignedPages, container.Id, []);
        return result;

        void AddDocument(
            ManuscriptDocument manuscript,
            IReadOnlyList<PublishDesignedPageDocument> designedPages,
            string topLevel,
            IReadOnlyList<string> placementPath)
        {
            AddBlocks(manuscript, manuscript.Content, designedPages, topLevel, placementPath);
        }

        void AddBlocks(
            ManuscriptDocument manuscript,
            IEnumerable<ManuscriptBlock> blocks,
            IReadOnlyList<PublishDesignedPageDocument> designedPages,
            string topLevel,
            IReadOnlyList<string> placementPath)
        {
            foreach (var block in blocks)
            {
                if (block.Type == ManuscriptBlockType.DesignedPage
                    && block.DesignedPageId is Guid pageId)
                {
                    var page = designedPages.SingleOrDefault(item => item.Id == pageId)
                        ?? throw new InvalidDataException($"Designed Page {pageId:D} is missing from citation traversal.");
                    AddDocument(page.SemanticManuscript, [], topLevel, [.. placementPath, $"placement:{block.Id}"]);
                    continue;
                }
                if (block.Table is { } table)
                {
                    foreach (var row in table.Rows)
                    foreach (var cell in row.Cells)
                    {
                        AddBlocks(
                            manuscript,
                            cell.Content,
                            designedPages,
                            topLevel,
                            [.. placementPath, $"table:{table.Id}", $"row:{row.Id}", $"cell:{cell.Id}"]);
                    }
                    continue;
                }
                foreach (var inline in block.Content)
                {
                    if (inline.Type == ManuscriptInlineType.Citation)
                        result.Add(new(
                            new(target, topLevel, placementPath.ToArray(), inline.Id!),
                            inline.Citation!));
                    else if (inline.Type == ManuscriptInlineType.NoteReference)
                    {
                        var note = manuscript.Notes.Single(item => item.Id == inline.NoteId);
                        // Notes belong to their document occurrence, including when their
                        // reference appears inside a table cell.
                        AddBlocks(manuscript, note.Content, designedPages, topLevel,
                            [.. placementPath.Where(segment => segment.StartsWith("placement:", StringComparison.Ordinal)), $"note:{note.Id}"]);
                    }
                }
            }
        }
    }
}
