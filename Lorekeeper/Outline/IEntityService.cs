using Lorekeeper.Knowledge;
using Lorekeeper.Models;
using Lorekeeper.Persistence.Repositories;

namespace Lorekeeper.Outline;

/// <summary>
/// One service for every story-graph entity (Characters, Locations, Events/beats, ...).
/// All operations are project-scoped. Entities are persisted as <see cref="GraphNode"/>s
/// via <see cref="IGraphStore"/>; conventional shape:
/// <list type="bullet">
///   <item><c>NodeType</c> = the entity type (e.g. <c>"Character"</c>, <c>"Location"</c>, <c>"Event"</c>).</item>
///   <item><c>Key</c> = a generated GUID-N (rename-safe; tools refer to entities by this id).</item>
///   <item><c>Label</c> = the entity's display name.</item>
///   <item><c>Properties</c> = free-form bag (description, role, ...). When a parent is set, the
///     entity is wired to the parent via a <c>HasChild</c> outgoing edge from the parent node and
///     ordering is stored on the entity as the integer property <c>order</c>.</item>
/// </list>
/// Chapter parent nodes (<c>NodeType="Chapter"</c>, <c>Key=chapterId.ToString("N")</c>) are
/// upserted lazily the first time an entity is given that chapter as a parent.
/// </summary>
public interface IEntityService
{
    Task<IReadOnlyList<StoryEntity>> ListAsync(
        Guid projectId,
        string nodeType,
        Guid? parentId = null,
        CancellationToken cancellationToken = default);

    Task<StoryEntity> CreateAsync(
        Guid projectId,
        string nodeType,
        string name,
        IDictionary<string, string?>? properties = null,
        Guid? parentId = null,
        int? order = null,
        CancellationToken cancellationToken = default);

    Task<StoryEntity> UpdateAsync(
        Guid projectId,
        Guid entityId,
        string? name = null,
        IDictionary<string, string?>? propertiesToSet = null,
        IReadOnlyCollection<string>? propertiesToRemove = null,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid projectId, Guid entityId, CancellationToken cancellationToken = default);

    Task ReorderAsync(
        Guid projectId,
        string nodeType,
        Guid parentId,
        IReadOnlyList<Guid> orderedEntityIds,
        CancellationToken cancellationToken = default);

    Task LinkAsync(
        Guid projectId,
        Guid fromEntityId,
        Guid toEntityId,
        string edgeType,
        IDictionary<string, string?>? properties = null,
        CancellationToken cancellationToken = default);

    /// <summary>Counts a parent's <c>HasChild</c> children of the given type. Used by <c>list_outline</c>.</summary>
    Task<int> CountChildrenAsync(
        Guid projectId,
        Guid parentId,
        string childNodeType,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Project-scoped projection of a <see cref="GraphNode"/> exposed to UI + chat tools.
/// </summary>
/// <param name="Id">The entity's project-unique GUID (== node.Key).</param>
/// <param name="Type">The entity type (== node.NodeType).</param>
/// <param name="Name">Display name (== node.Label, falls back to Key).</param>
/// <param name="Order">Position within its parent's child list, when applicable.</param>
/// <param name="ParentId">Parent entity id (null for project-scoped entities).</param>
/// <param name="Properties">Free-form properties (excludes the internal <c>order</c> key).</param>
public sealed record StoryEntity(
    Guid Id,
    string Type,
    string Name,
    int? Order,
    Guid? ParentId,
    IReadOnlyDictionary<string, string?> Properties);
