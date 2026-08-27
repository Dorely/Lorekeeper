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
            ["hasVisualChanges"] = chapter.HasVisualChanges,
            ["sections"] = new JsonArray(),
            ["compositions"] = new JsonArray(),
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

        var compositions = (JsonArray)result["compositions"]!;
        foreach (var composition in chapter.CompositionChanges)
            compositions.Add(ProjectComposition(composition));

        return result;
    }

    public static JsonObject ProjectComposition(ProjectVersionReviewComposition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);

        var before = ToDocument(composition.Before, composition.CompositionId);
        var after = ToDocument(composition.After, composition.CompositionId);
        var result = new JsonObject
        {
            ["compositionId"] = composition.CompositionId,
            ["chapterId"] = composition.ChapterId,
            ["editionId"] = composition.EditionId,
            ["name"] = composition.After?.Name ?? composition.Before?.Name ?? "Designed Page",
            ["hasChanges"] = composition.HasChanges,
            ["before"] = ProjectCompositionPreview(composition.Before),
            ["after"] = ProjectCompositionPreview(composition.After),
            ["sections"] = new JsonArray(),
        };

        if (ManuscriptReviewDiffBuilder.TryBuild(
                before,
                after,
                composition.After?.Name ?? composition.Before?.Name ?? "Designed Page",
                out var diff))
        {
            result["sections"] = ProjectSections(diff);
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

    private static JsonObject? ProjectCompositionPreview(ProjectExportPageComposition? composition)
    {
        if (composition is null)
            return null;

        return new JsonObject
        {
            ["id"] = composition.Id,
            ["chapterId"] = composition.ChapterId,
            ["editionId"] = composition.EditionId,
            ["name"] = composition.Name,
            ["revision"] = composition.Revision,
            ["activeAuthoringVariantId"] = composition.ActiveAuthoringVariantId,
            ["variantCount"] = composition.Variants.Count,
            ["variants"] = ProjectCompositionVariants(composition.Variants),
        };
    }

    private static JsonArray ProjectCompositionVariants(
        IEnumerable<ProjectExportPageCompositionVariant> variants)
    {
        var result = new JsonArray();
        foreach (var variant in variants.OrderBy(variant => variant.Id))
        {
            result.Add(new JsonObject
            {
                ["id"] = variant.Id,
                ["geometryKey"] = variant.GeometryKey,
                ["revision"] = variant.Revision,
            });
        }

        return result;
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

    private static ManuscriptDocument ToDocument(ProjectExportPageComposition? composition, Guid compositionId)
    {
        if (composition is null || string.IsNullOrWhiteSpace(composition.SemanticManuscriptJson))
        {
            var revision = composition is null ? 0 : Math.Max(0, composition.Revision);
            return ManuscriptCodec.CreateEmpty(composition?.Id ?? compositionId, revision);
        }

        return ManuscriptCodec.Deserialize(
            composition.SemanticManuscriptJson,
            composition.Id,
            composition.Revision);
    }
}
