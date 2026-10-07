using Lorekeeper.Images;
using Lorekeeper.Llm;
using Lorekeeper.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Lorekeeper.EntityVisuals;

public sealed record EntityVisualContextReference(
    Guid ImageId,
    Guid? EntityId,
    string EntityType,
    string EntityName,
    string Label,
    int SortOrder,
    string FileName,
    string AltText,
    string Prompt,
    bool IsExplicitImage = false,
    EntityVisualExampleOrigin? AssociationOrigin = null,
    PublishAssetSource ImageSource = PublishAssetSource.Uploaded,
    bool FullResolution = false);

public interface IEntityVisualContextService
{
    Task<IReadOnlyList<EntityVisualContextReference>> ListForEntitiesAsync(Guid projectId, IReadOnlyCollection<Guid> entityIds, CancellationToken cancellationToken = default);
    Task<ChatMessage?> BuildVisionMessageAsync(Guid projectId, IReadOnlyList<EntityVisualContextReference> references, bool visionReady, string heading, CancellationToken cancellationToken = default);
}

public sealed class EntityVisualContextService(
    IEntityVisualExampleService visualExamples,
    IProjectImageService images,
    IOptions<EntityVisualContextOptions> options) : IEntityVisualContextService
{
    public async Task<IReadOnlyList<EntityVisualContextReference>> ListForEntitiesAsync(
        Guid projectId,
        IReadOnlyCollection<Guid> entityIds,
        CancellationToken cancellationToken = default)
    {
        var byEntity = await visualExamples.ListForEntitiesAsync(projectId, entityIds, cancellationToken);
        var result = new List<EntityVisualContextReference>();
        foreach (var entityId in entityIds.Distinct())
        {
            if (!byEntity.TryGetValue(entityId, out var examples)) continue;
            result.AddRange(examples.OrderBy(example => example.SortOrder).Select(ToReference));
        }
        return result;
    }

    public async Task<ChatMessage?> BuildVisionMessageAsync(
        Guid projectId,
        IReadOnlyList<EntityVisualContextReference> references,
        bool visionReady,
        string heading,
        CancellationToken cancellationToken = default)
    {
        if (!visionReady || references.Count == 0) return null;

        var perEntityMax = Math.Max(1, options.Value.MaxImagesPerEntity);
        var turnMax = Math.Max(1, options.Value.MaxImagesPerTurn);
        var perEntityCounts = new Dictionary<Guid, int>();
        var selected = new List<EntityVisualContextReference>();
        var seenImages = new HashSet<Guid>();

        foreach (var reference in references.OrderByDescending(reference => reference.IsExplicitImage))
        {
            if (selected.Count >= turnMax) break;
            if (reference.EntityId is Guid entityId)
            {
                var count = perEntityCounts.GetValueOrDefault(entityId);
                if (count >= perEntityMax) continue;
                perEntityCounts[entityId] = count + 1;
            }
            if (!seenImages.Add(reference.ImageId)) continue;
            selected.Add(reference);
        }

        if (selected.Count == 0) return null;
        var contents = new List<AIContent>
        {
            new TextContent(heading.Trim()),
        };
        foreach (var reference in selected)
        {
            var mappings = references.Where(item => item.ImageId == reference.ImageId).ToList();
            var fullResolution = mappings.Any(item => item.FullResolution);
            var data = await images.GetDataAsync(
                projectId,
                reference.ImageId,
                fullResolution ? null : Math.Max(64, options.Value.MaxImageEdge),
                cancellationToken);
            if (data is null) continue;
            var mappingText = string.Join("; ", mappings.Select(item => item.EntityId is null
                ? $"explicit project image ({item.Label}); imageSource={item.ImageSource}"
                : $"canonical reference for {item.EntityType} {item.EntityName} [entityId={item.EntityId:N}] ({item.Label}); associationOrigin={item.AssociationOrigin?.ToString() ?? "unknown"}; imageSource={item.ImageSource}"));
            contents.Add(new TextContent($"\nReferences: {mappingText}; imageId={reference.ImageId:N}; file={reference.FileName}; alt={reference.AltText}; prompt={reference.Prompt}"));
            var content = new DataContent(data.Data, data.ContentType) { Name = data.FileName };
            contents.Add(fullResolution ? ModelImagePayload.MarkFullResolution(content) : content);
        }

        var omitted = references.Select(reference => reference.ImageId).Distinct().Count() - selected.Count;
        if (omitted > 0)
            contents.Add(new TextContent($"\n{omitted} additional canonical visual reference(s) were omitted by the configured context limit. Their metadata remains in text context and they can be loaded explicitly."));
        return new ChatMessage(ChatRole.User, contents);
    }

    public static EntityVisualContextReference ToReference(EntityVisualExampleView example) => new(
        example.Image.Id,
        example.EntityId,
        example.EntityType,
        example.EntityName,
        example.Label,
        example.SortOrder,
        example.Image.FileName,
        example.Image.AltText,
        example.Image.Prompt,
        AssociationOrigin: example.Origin,
        ImageSource: example.Image.Source);
}
