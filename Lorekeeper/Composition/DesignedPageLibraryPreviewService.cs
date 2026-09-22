using System.Text.Json;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Publish;

namespace Lorekeeper.Composition;

/// <summary>Read-only previews of effective library content; never materializes a variant.</summary>
public sealed class DesignedPageLibraryPreviewService(
    ICompositionCanvasPreviewService previews,
    IPublicationSectionService sections)
{
    public async Task<CompositionCanvasPreviewResult> RenderAsync(
        Guid projectId, EditorContentTarget target, DesignedPageView page, CancellationToken cancellationToken)
    {
        var variant = page.Content.Variants.SingleOrDefault(item => item.Id == page.Content.ActiveVariantId)
            ?? throw new InvalidOperationException("This page has no active layout.");
        var scene = JsonSerializer.Deserialize<CompositionScene>(variant.SceneJson, ManuscriptCodec.JsonOptions)
            ?? throw new InvalidOperationException("This page's layout is unavailable.");
        var document = ManuscriptCodec.Deserialize(page.Content.SemanticManuscriptJson, page.Content.Id, page.Content.Revision);
        var blocks = document.Content.ToList();
        foreach (var field in blocks.Where(item => item.PublicationField is not null).Select(item => item.PublicationField!.Value).Distinct())
        {
            var value = PublicationTextBindings.NormalizeSemanticText(await sections.ResolveBoundFieldAsync(
                new PublicationSectionTarget(projectId, target.EditionId), field, cancellationToken));
            for (var index = 0; index < blocks.Count; index++)
                if (blocks[index].PublicationField == field)
                    blocks[index] = blocks[index] with { Content = [new ManuscriptInline { Text = value }] };
        }
        return await previews.RenderSceneAtResolutionAsync(projectId, variant.Id, variant.Revision, scene,
            document with { Content = blocks }, CompositionCanvasPreviewMode.Clean, 720, cancellationToken);
    }
}
