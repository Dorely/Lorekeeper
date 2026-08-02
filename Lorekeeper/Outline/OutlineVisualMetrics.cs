using System.Text.Json;
using Lorekeeper.Composition;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Outline;

internal sealed record OutlineVisualMetric(
    int FigureCount,
    int DesignedPageCount,
    int DesignedSpreadCount,
    int LayoutDiagnosticCount,
    string? Summary);

internal static class OutlineVisualMetrics
{
    public static async Task<IReadOnlyDictionary<Guid, OutlineVisualMetric>> ReadAsync(
        AppDbContext db,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var chapters = await db.Chapters.AsNoTracking()
            .Where(item => item.ProjectId == projectId)
            .Select(item => new { item.Id, item.ManuscriptJson, item.ManuscriptRevision })
            .ToListAsync(cancellationToken);
        var compositionIds = chapters
            .SelectMany(chapter => ManuscriptCodec.Deserialize(chapter.ManuscriptJson, chapter.Id, chapter.ManuscriptRevision).Content)
            .Where(block => block.Type == ManuscriptBlockType.DesignedPage && block.PageCompositionId is not null)
            .Select(block => block.PageCompositionId!.Value)
            .Distinct()
            .ToArray();
        var compositions = await db.PageCompositions.AsNoTracking()
            .Include(item => item.Variants)
            .Where(item => item.ProjectId == projectId && compositionIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var result = new Dictionary<Guid, OutlineVisualMetric>();
        foreach (var chapter in chapters)
        {
            var manuscript = ManuscriptCodec.Deserialize(chapter.ManuscriptJson, chapter.Id, chapter.ManuscriptRevision);
            var figures = manuscript.Content.Count(block => block.Type == ManuscriptBlockType.Figure);
            var designed = manuscript.Content.Where(block => block.Type == ManuscriptBlockType.DesignedPage).ToList();
            var spreads = 0;
            var diagnostics = 0;
            foreach (var block in designed)
            {
                if (block.PageCompositionId is not Guid compositionId
                    || !compositions.TryGetValue(compositionId, out var composition))
                {
                    diagnostics++;
                    continue;
                }
                if (composition.Variants.Count == 0) diagnostics++;
                var semantic = ManuscriptCodec.Deserialize(composition.SemanticManuscriptJson, composition.Id, composition.Revision);
                var isSpread = false;
                foreach (var variant in composition.Variants)
                {
                    var scene = JsonSerializer.Deserialize<CompositionScene>(variant.SceneJson, ManuscriptCodec.JsonOptions);
                    if (scene is null)
                    {
                        diagnostics++;
                        continue;
                    }
                    isSpread |= scene.Surface.Kind == CompositionSurfaceKind.FacingSpread;
                    var visibleLayers = scene.Layers.Where(layer => layer.Visible).Select(layer => layer.Id).ToHashSet();
                    var outputObjects = CompositionSceneResolver.Flatten(scene);
                    diagnostics += ManuscriptRangeResolver.ValidateCoverage(
                        semantic,
                        outputObjects.Where(item => item.Kind == CompositionObjectKind.Text
                                && item.Visible && visibleLayers.Contains(item.LayerId))
                            .Select(item => item.ContentReferences)).Count;
                    diagnostics += outputObjects.Count(item => item.Kind == CompositionObjectKind.Image
                        && item.Visible && visibleLayers.Contains(item.LayerId)
                        && !item.Decorative
                        && (item.AccessibilityDecisionPending || string.IsNullOrWhiteSpace(item.AltText)));
                }
                if (isSpread) spreads++;
            }
            var summaryParts = new List<string>();
            if (figures > 0) summaryParts.Add($"{figures} flowing Figure{(figures == 1 ? string.Empty : "s")}");
            if (designed.Count > 0) summaryParts.Add($"{designed.Count} Designed Page{(designed.Count == 1 ? string.Empty : "s")}");
            if (spreads > 0) summaryParts.Add($"{spreads} facing spread{(spreads == 1 ? string.Empty : "s")}");
            result[chapter.Id] = new OutlineVisualMetric(
                figures,
                designed.Count,
                spreads,
                diagnostics,
                summaryParts.Count == 0 ? null : string.Join("; ", summaryParts));
        }
        return result;
    }
}
