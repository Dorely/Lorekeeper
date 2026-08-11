using System.Text.Json;
using Lorekeeper.Manuscripts;

namespace Lorekeeper.Composition;

public static class CompositionAgentPayloads
{
    public static async Task<string> ReadVariantAsync(
        ICompositionService compositions,
        Guid projectId,
        Guid compositionId,
        Guid variantId,
        int semanticStart,
        int semanticCount,
        int objectStart,
        int objectCount,
        int structureStart,
        int structureCount,
        CancellationToken cancellationToken)
    {
        var variant = await compositions.ReadVariantAsync(projectId, variantId, cancellationToken);
        if (variant.CompositionId != compositionId)
            throw new KeyNotFoundException("The selected variant does not belong to this page composition.");
        var semantic = ManuscriptCodec.Deserialize(variant.Composition.SemanticManuscriptJson);
        var scene = JsonSerializer.Deserialize<Lorekeeper.Models.CompositionScene>(variant.SceneJson, ManuscriptCodec.JsonOptions)
            ?? throw new InvalidDataException("The composition scene is empty.");
        semanticStart = Math.Clamp(semanticStart, 0, semantic.Content.Count);
        semanticCount = Math.Clamp(semanticCount, 1, 40);
        objectStart = Math.Clamp(objectStart, 0, scene.Objects.Count);
        objectCount = Math.Clamp(objectCount, 1, 50);
        structureStart = Math.Max(0, structureStart);
        structureCount = Math.Clamp(structureCount, 1, 50);
        var semanticPage = semantic.Content.Skip(semanticStart).Take(semanticCount).Select(block => new
        {
            block.Id,
            type = block.Type.ToString(),
            block.StyleRole,
            block.HeadingLevel,
            text = Truncate(ManuscriptCodec.Text(block), 800),
        }).ToList();
        var objectPage = scene.Objects.Skip(objectStart).Take(objectCount).ToList();
        var imageLayout = objectPage
            .Where(item => item.Kind == Lorekeeper.Models.CompositionObjectKind.Image)
            .Select(item => new
            {
                objectId = item.Id,
                frameCoversCanvas = CompositionImageLayout.FrameCoversCanvas(item),
                imageCoversCanvas = CompositionImageLayout.ImageCoversCanvas(item),
                retainsAspectRatio = CompositionImageLayout.RetainsAspectRatio(item),
                presentation = CompositionImageLayout.Presentation(item),
            })
            .ToList();
        return JsonSerializer.Serialize(new
        {
            ok = true,
            targetId = variant.Id,
            revision = variant.Revision,
            summary = $"Selected variant contains {scene.Objects.Count} object(s) and {semantic.Content.Count} semantic block(s).",
            composition = new { variant.CompositionId, variant.Composition.Name, revision = variant.Composition.Revision },
            variant.GeometryKey,
            scene = new
            {
                scene.SchemaVersion,
                scene.Surface,
                layers = scene.Layers.Skip(structureStart).Take(structureCount),
                styles = scene.Styles.Skip(structureStart).Take(structureCount),
                objects = objectPage,
                imageLayout,
            },
            semanticBlocks = semanticPage,
            objectContinuation = Continuation(objectStart, objectPage.Count, scene.Objects.Count),
            semanticContinuation = Continuation(semanticStart, semanticPage.Count, semantic.Content.Count),
            structureContinuation = new
            {
                layers = Continuation(structureStart, Math.Min(structureCount, Math.Max(0, scene.Layers.Count - structureStart)), scene.Layers.Count),
                styles = Continuation(structureStart, Math.Min(structureCount, Math.Max(0, scene.Styles.Count - structureStart)), scene.Styles.Count),
            },
        }, ManuscriptCodec.JsonOptions);
    }

    public static async Task<string> PatchElementAsync(
        ICompositionService compositions,
        EditorContentTarget target,
        Guid projectId,
        Guid variantId,
        long expectedRevision,
        string targetKind,
        Guid targetId,
        CompositionElementPatch patch,
        CancellationToken cancellationToken)
    {
        try
        {
            var variant = await compositions.PatchElementAsync(
                target,
                projectId,
                variantId,
                expectedRevision,
                targetKind,
                targetId,
                patch,
                cancellationToken);
            return JsonSerializer.Serialize(new
            {
                ok = true,
                targetId,
                variantId = variant.Id,
                revision = variant.Revision,
                summary = $"Patched composition {targetKind} {targetId:N}.",
                changedIds = new[] { targetId },
                mutation = new { kind = "pageComposition", id = variant.CompositionId, variantId = variant.Id, selectId = targetId },
            });
        }
        catch (CompositionRevisionConflictException exception)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                code = "REVISION_CONFLICT",
                targetId,
                revision = exception.ActualRevision,
                summary = exception.Message,
                recovery = "Reread this selected variant, then retry only the intended fields against its current revision.",
            });
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            return JsonSerializer.Serialize(new { ok = false, code = "PATCH_REJECTED", targetId, summary = exception.Message });
        }
    }

    private static object Continuation(int start, int returned, int total) => new
    {
        start,
        returned,
        total,
        hasMore = start + returned < total,
        nextStart = start + returned < total ? start + returned : (int?)null,
    };

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
