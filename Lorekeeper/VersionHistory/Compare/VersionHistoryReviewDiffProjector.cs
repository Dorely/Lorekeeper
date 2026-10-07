using System.Text.Json.Nodes;
using Lorekeeper.ImportExport;
using Lorekeeper.Manuscripts;
using Lorekeeper.VersionHistory.Services;

namespace Lorekeeper.VersionHistory.Compare;

/// <summary>
/// Converts target-scoped version-history data into the bounded semantic shape
/// used by model-facing review reads. Manuscript documents remain semantic:
/// only diff hunks and rows are emitted, never a flattened before/after body or
/// the source semantic JSON.
/// </summary>
public static class VersionHistoryReviewDiffProjector
{
    public static JsonObject ProjectChapter(ProjectVersionReviewChapter chapter)
    {
        ArgumentNullException.ThrowIfNull(chapter);

        var result = new JsonObject
        {
            ["hasManuscriptChanges"] = chapter.HasManuscriptChanges,
            ["sections"] = new JsonArray(),
        };

        var before = ToDocument(chapter.Before, chapter.ChapterId);
        var after = ToDocument(chapter.After, chapter.ChapterId);
        if (ManuscriptReviewDiffBuilder.TryBuild(
                before,
                after,
                chapter.After?.Title ?? chapter.Before?.Title ?? "Chapter manuscript",
                out var manuscriptDiff))
        {
            result["sections"] = ProjectSections(manuscriptDiff);
        }

        return result;
    }
    private static JsonArray ProjectSections(ReviewDiff diff)
    {
        var sections = new JsonArray();
        foreach (var section in diff.Sections)
        {
            var hunks = new JsonArray();
            foreach (var hunk in section.Hunks)
            {
                var rows = new JsonArray();
                foreach (var row in hunk.Rows)
                {
                    var segments = new JsonArray();
                    foreach (var segment in row.Segments)
                    {
                        segments.Add(new JsonObject
                        {
                            ["text"] = segment.Text,
                            ["kind"] = segment.Kind.ToString(),
                        });
                    }

                    rows.Add(new JsonObject
                    {
                        ["oldLineNumber"] = row.OldLineNumber,
                        ["newLineNumber"] = row.NewLineNumber,
                        ["marker"] = row.Marker,
                        ["text"] = row.Text,
                        ["kind"] = row.Kind.ToString(),
                        ["pairId"] = row.PairId,
                        ["segments"] = segments,
                    });
                }

                hunks.Add(new JsonObject
                {
                    ["oldStart"] = hunk.OldStart,
                    ["oldLength"] = hunk.OldLength,
                    ["newStart"] = hunk.NewStart,
                    ["newLength"] = hunk.NewLength,
                    ["additions"] = hunk.Additions,
                    ["deletions"] = hunk.Deletions,
                    ["rows"] = rows,
                });
            }

            sections.Add(new JsonObject
            {
                ["key"] = section.Key,
                ["label"] = section.Label,
                ["additions"] = section.Additions,
                ["deletions"] = section.Deletions,
                ["hunks"] = hunks,
            });
        }

        return sections;
    }

    private static ManuscriptDocument ToDocument(ProjectExportChapter? chapter, Guid chapterId)
    {
        if (chapter is null || string.IsNullOrWhiteSpace(chapter.ManuscriptJson))
        {
            var revision = chapter is null ? 0 : Math.Max(0, chapter.ManuscriptRevision);
            return ManuscriptCodec.CreateEmpty(chapter?.Id ?? chapterId, revision);
        }

        return ManuscriptCodec.Deserialize(chapter.ManuscriptJson, chapter.Id, chapter.ManuscriptRevision);
    }

    private static ManuscriptDocument ToDocument(ProjectExportDesignedPage? designedPage, Guid designedPageId)
    {
        var content = designedPage?.Contents.FirstOrDefault(item => item.EditionId is null)
            ?? designedPage?.Contents.FirstOrDefault();
        return content is null || string.IsNullOrWhiteSpace(content.SemanticManuscriptJson)
            ? ManuscriptCodec.CreateEmpty(designedPage?.Id ?? designedPageId, content is null ? 0 : Math.Max(0, content.Revision))
            : ManuscriptCodec.Deserialize(content.SemanticManuscriptJson, content.Id, content.Revision);
    }
}
