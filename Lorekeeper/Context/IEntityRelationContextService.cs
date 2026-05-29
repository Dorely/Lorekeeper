using System.Text.Json.Serialization;

namespace Lorekeeper.Context;

public sealed class EntityRelationContextOptions
{
    public int Depth { get; init; } = 2;
    public int MaxDirectLinks { get; init; } = 12;
    public int MaxTraversalPaths { get; init; } = 24;
    public int MaxLinksPerNode { get; init; } = 10;
}

public sealed record EntityRelationContext(
    [property: JsonPropertyName("directLinks")] IReadOnlyList<EntityDirectLinkContext> DirectLinks,
    [property: JsonPropertyName("traversalMap")] IReadOnlyList<EntityTraversalPathContext> TraversalMap,
    [property: JsonPropertyName("autoMentionLinks")] IReadOnlyList<EntityDirectLinkContext> AutoMentionLinks)
{
    public EntityRelationContext(
        IReadOnlyList<EntityDirectLinkContext> directLinks,
        IReadOnlyList<EntityTraversalPathContext> traversalMap)
        : this(directLinks, traversalMap, [])
    {
    }

    public static EntityRelationContext Empty { get; } = new([], [], []);
}

public sealed record EntityDirectLinkContext(
    [property: JsonPropertyName("edgeId")] long EdgeId,
    [property: JsonPropertyName("edgeType")] string EdgeType,
    [property: JsonPropertyName("direction")] string Direction,
    [property: JsonPropertyName("otherEntityId")] Guid OtherEntityId,
    [property: JsonPropertyName("otherEntityName")] string OtherEntityName,
    [property: JsonPropertyName("otherEntityType")] string OtherEntityType,
    [property: JsonPropertyName("sortOrder")] int? SortOrder,
    [property: JsonPropertyName("properties")] IReadOnlyDictionary<string, string?> Properties,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("isAutoLink")] bool IsAutoLink = false);

public sealed record EntityTraversalPathContext(
    [property: JsonPropertyName("depth")] int Depth,
    [property: JsonPropertyName("terminalEntityId")] Guid TerminalEntityId,
    [property: JsonPropertyName("terminalEntityType")] string TerminalEntityType,
    [property: JsonPropertyName("terminalEntityName")] string TerminalEntityName,
    [property: JsonPropertyName("path")] string Path);

public interface IEntityRelationContextService
{
    Task<EntityRelationContext> BuildForEntityAsync(
        Guid projectId,
        Guid entityId,
        EntityRelationContextOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EntityTraversalPathContext>> BuildTraversalMapAsync(
        Guid projectId,
        IReadOnlyCollection<Guid> rootEntityIds,
        EntityRelationContextOptions? options = null,
        CancellationToken cancellationToken = default);

    string FormatTraversalMap(IReadOnlyList<EntityTraversalPathContext> traversalMap);
}
