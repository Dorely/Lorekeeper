using Lorekeeper.Chapters;
using Lorekeeper.Ingest;
using Lorekeeper.Knowledge;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence.Repositories;
using Lorekeeper.Projects;

namespace Lorekeeper.Graph;

public sealed class ProjectGraphService(
    IGraphStore graph,
    IGraphNodeRepository nodes,
    IGraphEdgeRepository edges,
    IProjectRepository projects,
    IProjectService projectService,
    IActService actService,
    IChapterService chapterService,
    IProjectFactService projectFacts,
    IEntityService entities,
    IEntityTypeService entityTypes) : IProjectGraphService
{
    private const string HasChildEdgeType = EntityService.HasChildEdgeType;

    private static readonly string[] Palette =
    [
        "#8ab4f8", "#f28b82", "#fdd663", "#81c995", "#c58af9",
        "#78d9ec", "#ffb86c", "#a7d3a6", "#d7aefb", "#aecbfa",
    ];

    public async Task<ProjectGraphSnapshot> GetSnapshotAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var allNodes = await nodes.ListByProjectAsync(projectId, cancellationToken);
        var allEdges = await edges.ListByProjectAsync(projectId, cancellationToken);
        var typeDefinitions = await entityTypes.ListAsync(projectId, includeStructural: true, cancellationToken);
        var typeByName = typeDefinitions.ToDictionary(t => t.Type, StringComparer.Ordinal);
        var colorByType = BuildColorMap(typeDefinitions);

        var degreeByNodeId = new Dictionary<long, int>();
        var parentByNodeId = new Dictionary<long, long>();
        foreach (var edge in allEdges)
        {
            degreeByNodeId[edge.FromNodeId] = degreeByNodeId.GetValueOrDefault(edge.FromNodeId) + 1;
            degreeByNodeId[edge.ToNodeId] = degreeByNodeId.GetValueOrDefault(edge.ToNodeId) + 1;
            if (string.Equals(edge.EdgeType, HasChildEdgeType, StringComparison.OrdinalIgnoreCase))
                parentByNodeId.TryAdd(edge.ToNodeId, edge.FromNodeId);
        }

        var snapshotNodes = allNodes
            .Select(node => ProjectNode(
                node,
                typeByName.TryGetValue(node.NodeType, out var typeDefinition) ? typeDefinition : null,
                parentByNodeId.GetValueOrDefault(node.Id) == 0 ? null : parentByNodeId[node.Id],
                degreeByNodeId.GetValueOrDefault(node.Id),
                colorByType.GetValueOrDefault(node.NodeType, FallbackColor(node.NodeType))))
            .ToList();

        var snapshotEdges = allEdges.Select(ProjectEdge).ToList();
        var knownSnapshotTypes = typeDefinitions
            .Select(type => ProjectType(type, colorByType.GetValueOrDefault(type.Type, FallbackColor(type.Type))))
            .ToList();

        var missingTypes = allNodes
            .Select(n => n.NodeType)
            .Distinct(StringComparer.Ordinal)
            .Where(type => knownSnapshotTypes.All(t => !string.Equals(t.Type, type, StringComparison.Ordinal)))
            .OrderBy(type => type, StringComparer.OrdinalIgnoreCase)
            .Select(type => new ProjectGraphNodeType(
                type,
                HumanizeType(type),
                Pluralize(HumanizeType(type)),
                IsStructuralType(type),
                false,
                CanCreateType(type),
                FallbackColor(type),
                1000,
                new Dictionary<string, string?>()))
            .ToList();

        var edgeTypes = allEdges
            .Select(e => e.EdgeType)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => string.Equals(t, HasChildEdgeType, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ProjectGraphSnapshot(snapshotNodes, snapshotEdges, knownSnapshotTypes.Concat(missingTypes).ToList(), edgeTypes);
    }

    public async Task CreateTypeAsync(Guid projectId, string labelOrType, CancellationToken cancellationToken = default)
    {
        await entityTypes.CreateAsync(projectId, labelOrType, cancellationToken: cancellationToken);
        await TouchProjectAsync(projectId, cancellationToken);
    }

    public async Task CreateNodeAsync(Guid projectId, ProjectGraphNodeCreateRequest request, CancellationToken cancellationToken = default)
    {
        var type = (request.Type ?? string.Empty).Trim();
        if (string.Equals(type, "__new", StringComparison.OrdinalIgnoreCase))
        {
            var createdType = await entityTypes.CreateAsync(projectId, request.NewTypeName ?? string.Empty, cancellationToken: cancellationToken);
            type = createdType.Type;
        }

        if (string.IsNullOrWhiteSpace(type))
            throw new ArgumentException("Node type is required.", nameof(request));
        if (string.Equals(type, EntityTypeService.ProjectNodeType, StringComparison.Ordinal))
            throw new InvalidOperationException("The project root is created automatically.");

        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length == 0)
            throw new ArgumentException("Node name is required.", nameof(request));

        var properties = CleanProperties(request.Properties);
        var parentGuid = await ResolveParentGuidAsync(projectId, request.ParentNodeId, cancellationToken);

        if (string.Equals(type, EntityTypeService.ActNodeType, StringComparison.Ordinal))
        {
            await actService.CreateAsync(projectId, name, Read(properties, "synopsis") ?? string.Empty, cancellationToken: cancellationToken);
            return;
        }

        if (string.Equals(type, EntityTypeService.ChapterNodeType, StringComparison.Ordinal))
        {
            var actId = await ResolveActParentAsync(projectId, request.ParentNodeId, allowProjectParent: true, cancellationToken);
            await chapterService.CreateAsync(projectId, actId, name, Read(properties, "synopsis") ?? string.Empty, cancellationToken: cancellationToken);
            return;
        }

        if (string.Equals(type, EntityTypeService.ProjectFactNodeType, StringComparison.Ordinal))
        {
            var key = Read(properties, "key") ?? name;
            var value = Read(properties, "value") ?? string.Empty;
            await projectFacts.UpsertAsync(projectId, key, value, cancellationToken: cancellationToken);
            return;
        }

        await entities.CreateAsync(projectId, type, name, properties, parentGuid, cancellationToken: cancellationToken);
        await TouchProjectAsync(projectId, cancellationToken);
    }

    public async Task UpdateNodeAsync(Guid projectId, ProjectGraphNodeUpdateRequest request, CancellationToken cancellationToken = default)
    {
        var node = await GetRequiredProjectNodeAsync(projectId, request.NodeId, cancellationToken);
        var nextLabel = string.IsNullOrWhiteSpace(request.Label) ? null : request.Label.Trim();
        var nextProperties = CleanProperties(request.Properties);

        if (string.Equals(node.NodeType, EntityTypeService.ProjectNodeType, StringComparison.Ordinal))
        {
            if (nextLabel is not null && !string.Equals(nextLabel, node.Label, StringComparison.Ordinal))
                await projectService.RenameAsync(projectId, nextLabel, cancellationToken);
            return;
        }

        if (string.Equals(node.NodeType, EntityTypeService.ActNodeType, StringComparison.Ordinal))
        {
            var id = ReadNodeGuid(node);
            await actService.UpdateAsync(id, nextLabel, Read(nextProperties, "synopsis"), cancellationToken);
            return;
        }

        if (string.Equals(node.NodeType, EntityTypeService.ChapterNodeType, StringComparison.Ordinal))
        {
            var id = ReadNodeGuid(node);
            await chapterService.UpdateAsync(id, nextLabel, synopsis: Read(nextProperties, "synopsis"), cancellationToken: cancellationToken);
            return;
        }

        if (string.Equals(node.NodeType, EntityTypeService.ProjectFactNodeType, StringComparison.Ordinal))
        {
            var id = ReadNodeGuid(node);
            var key = Read(nextProperties, "key") ?? Read(node.Properties, "key") ?? node.Label ?? node.Key;
            var value = Read(nextProperties, "value") ?? Read(node.Properties, "value") ?? string.Empty;
            await projectFacts.UpsertAsync(projectId, key, value, id, cancellationToken);
            return;
        }

        if (Guid.TryParseExact(node.Key, "N", out var entityId))
        {
            var currentProperties = ProjectProperties(node.Properties);
            var propertiesToSet = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in nextProperties)
            {
                if (!currentProperties.TryGetValue(kv.Key, out var current) || !string.Equals(current, kv.Value, StringComparison.Ordinal))
                    propertiesToSet[kv.Key] = kv.Value;
            }

            var propertiesToRemove = currentProperties.Keys
                .Where(key => !nextProperties.ContainsKey(key))
                .ToList();

            await entities.UpdateAsync(
                projectId,
                entityId,
                nextLabel,
                propertiesToSet.Count > 0 ? propertiesToSet : null,
                propertiesToRemove.Count > 0 ? propertiesToRemove : null,
                cancellationToken);
            await TouchProjectAsync(projectId, cancellationToken);
            return;
        }

        node.Label = nextLabel ?? node.Label;
        node.Properties = ToObjectDictionary(nextProperties);
        node.UpdatedAt = DateTime.UtcNow;
        nodes.Update(node);
        await nodes.SaveChangesAsync(cancellationToken);
        await TouchProjectAsync(projectId, cancellationToken);
    }

    public async Task DeleteNodeAsync(Guid projectId, long nodeId, CancellationToken cancellationToken = default)
    {
        var node = await GetRequiredProjectNodeAsync(projectId, nodeId, cancellationToken);
        if (string.Equals(node.NodeType, EntityTypeService.ProjectNodeType, StringComparison.Ordinal))
            throw new InvalidOperationException("The project root cannot be deleted from the graph view.");

        if (string.Equals(node.NodeType, EntityTypeService.ActNodeType, StringComparison.Ordinal))
        {
            await actService.DeleteAsync(ReadNodeGuid(node), cancellationToken);
            return;
        }

        if (string.Equals(node.NodeType, EntityTypeService.ChapterNodeType, StringComparison.Ordinal))
        {
            await chapterService.DeleteAsync(ReadNodeGuid(node), cancellationToken);
            return;
        }

        if (string.Equals(node.NodeType, EntityTypeService.ProjectFactNodeType, StringComparison.Ordinal))
        {
            await projectFacts.DeleteAsync(projectId, ReadNodeGuid(node), cancellationToken);
            return;
        }

        if (Guid.TryParseExact(node.Key, "N", out var entityId))
        {
            await entities.DeleteAsync(projectId, entityId, cancellationToken);
            await TouchProjectAsync(projectId, cancellationToken);
            return;
        }

        await graph.RemoveNodeAsync(node.Id, cancellationToken);
        await TouchProjectAsync(projectId, cancellationToken);
    }

    public async Task MoveParentAsync(Guid projectId, ProjectGraphMoveParentRequest request, CancellationToken cancellationToken = default)
    {
        var node = await GetRequiredProjectNodeAsync(projectId, request.NodeId, cancellationToken);
        if (request.ParentNodeId == node.Id)
            throw new InvalidOperationException("A node cannot be its own parent.");

        if (string.Equals(node.NodeType, EntityTypeService.ProjectNodeType, StringComparison.Ordinal)
            || string.Equals(node.NodeType, EntityTypeService.ActNodeType, StringComparison.Ordinal)
            || string.Equals(node.NodeType, EntityTypeService.ProjectFactNodeType, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("This node's parent is managed by the outline system.");
        }

        if (string.Equals(node.NodeType, EntityTypeService.ChapterNodeType, StringComparison.Ordinal))
        {
            var actId = await ResolveActParentAsync(projectId, request.ParentNodeId, allowProjectParent: true, cancellationToken);
            await chapterService.UpdateAsync(ReadNodeGuid(node), actId: new ChapterActAssignment(actId), cancellationToken: cancellationToken);
            return;
        }

        GraphNode? newParent = null;
        if (request.ParentNodeId is long parentNodeId)
            newParent = await GetRequiredProjectNodeAsync(projectId, parentNodeId, cancellationToken);

        var incomingParents = await edges.GetAdjacentAsync(node.Id, EdgeDirection.Incoming, [HasChildEdgeType], null, cancellationToken);
        foreach (var edge in incomingParents.Where(edge => newParent is null || edge.FromNodeId != newParent.Id))
            await graph.RemoveEdgeAsync(edge.Id, cancellationToken);

        if (newParent is not null)
        {
            var siblings = await edges.GetAdjacentAsync(newParent.Id, EdgeDirection.Outgoing, [HasChildEdgeType], null, cancellationToken);
            var sortOrder = siblings.Count == 0 ? 0 : siblings.Max(e => e.SortOrder ?? -1) + 1;
            await graph.UpsertEdgeAsync(newParent.Id, node.Id, HasChildEdgeType, sortOrder: sortOrder, cancellationToken: cancellationToken);
        }

        await TouchProjectAsync(projectId, cancellationToken);
    }

    public async Task CreateRelationshipAsync(Guid projectId, ProjectGraphRelationshipCreateRequest request, CancellationToken cancellationToken = default)
    {
        var edgeType = NormalizeEditableEdgeType(request.EdgeType);
        var from = await GetRequiredProjectNodeAsync(projectId, request.FromNodeId, cancellationToken);
        var to = await GetRequiredProjectNodeAsync(projectId, request.ToNodeId, cancellationToken);
        if (from.Id == to.Id)
            throw new InvalidOperationException("A relationship must connect two different nodes.");

        await graph.UpsertEdgeAsync(from.Id, to.Id, edgeType, ToObjectDictionary(CleanProperties(request.Properties)), cancellationToken: cancellationToken);
        await TouchProjectAsync(projectId, cancellationToken);
    }

    public async Task UpdateRelationshipAsync(Guid projectId, ProjectGraphRelationshipUpdateRequest request, CancellationToken cancellationToken = default)
    {
        var edge = await GetRequiredProjectEdgeAsync(projectId, request.EdgeId, cancellationToken);
        if (string.Equals(edge.EdgeType, HasChildEdgeType, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Managed parent links cannot be edited directly.");

        edge.EdgeType = NormalizeEditableEdgeType(request.EdgeType);
        edge.Properties = ToObjectDictionary(CleanProperties(request.Properties));
        edge.UpdatedAt = DateTime.UtcNow;
        edges.Update(edge);
        await edges.SaveChangesAsync(cancellationToken);
        await TouchProjectAsync(projectId, cancellationToken);
    }

    public async Task DeleteRelationshipAsync(Guid projectId, long edgeId, CancellationToken cancellationToken = default)
    {
        var edge = await GetRequiredProjectEdgeAsync(projectId, edgeId, cancellationToken);
        if (string.Equals(edge.EdgeType, HasChildEdgeType, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Managed parent links cannot be deleted directly.");

        await graph.RemoveEdgeAsync(edge.Id, cancellationToken);
        await TouchProjectAsync(projectId, cancellationToken);
    }

    private static ProjectGraphNode ProjectNode(
        GraphNode node,
        EntityTypeDefinition? typeDefinition,
        long? parentNodeId,
        int degree,
        string color)
    {
        var isProject = string.Equals(node.NodeType, EntityTypeService.ProjectNodeType, StringComparison.Ordinal);
        var isAct = string.Equals(node.NodeType, EntityTypeService.ActNodeType, StringComparison.Ordinal);
        var isChapter = string.Equals(node.NodeType, EntityTypeService.ChapterNodeType, StringComparison.Ordinal);
        var isProjectFact = string.Equals(node.NodeType, EntityTypeService.ProjectFactNodeType, StringComparison.Ordinal);
        var isStructural = typeDefinition?.IsStructural ?? IsStructuralType(node.NodeType);
        var editableKeys = EditablePropertyKeys(node.NodeType, node.Properties);
        var canEditProperties = !isProject && (editableKeys.Count > 0 || AllowsCustomProperties(node.NodeType));

        return new ProjectGraphNode(
            node.Id,
            node.NodeType,
            node.Key,
            node.Label ?? node.Key,
            ProjectProperties(node.Properties),
            parentNodeId,
            isStructural,
            !isProjectFact,
            canEditProperties,
            AllowsCustomProperties(node.NodeType),
            editableKeys,
            isChapter || (!isProject && !isAct && !isProjectFact),
            !isProject,
            degree,
            color,
            IngestSourceAssertions.IsIngestCreatedGraphObject(node.Properties),
            IngestSourceAssertions.CountEntitySources(node.Properties),
            IngestSourceAssertions.CountEntityObservations(node.Properties),
            IngestSourceAssertions.SummarizeEntityAssertions(node.Properties),
            IngestSourceAssertions.ListEntityObservations(node.Properties));
    }

    private static ProjectGraphEdge ProjectEdge(GraphEdge edge)
    {
        var isManaged = string.Equals(edge.EdgeType, HasChildEdgeType, StringComparison.OrdinalIgnoreCase);
        var isExtractedFrom = string.Equals(edge.EdgeType, IngestGraphSync.ExtractedFromEdgeType, StringComparison.OrdinalIgnoreCase);
        return new ProjectGraphEdge(
            edge.Id,
            edge.FromNodeId,
            edge.ToNodeId,
            edge.EdgeType,
            ProjectProperties(edge.Properties),
            edge.SortOrder,
            isManaged,
            !isManaged && !isExtractedFrom,
            !isManaged && !isExtractedFrom,
            IngestSourceAssertions.IsIngestCreatedGraphObject(edge.Properties),
            IngestSourceAssertions.CountRelationshipSources(edge.Properties),
            IngestSourceAssertions.CountRelationshipObservations(edge.Properties),
            IngestSourceAssertions.SummarizeRelationshipAssertions(edge.Properties),
            IngestSourceAssertions.ListRelationshipObservations(edge.Properties));
    }

    private static ProjectGraphNodeType ProjectType(EntityTypeDefinition typeDefinition, string color) =>
        new(
            typeDefinition.Type,
            typeDefinition.SingularLabel,
            typeDefinition.PluralLabel,
            typeDefinition.IsStructural,
            typeDefinition.IsChapterScoped,
            CanCreateType(typeDefinition.Type),
            color,
            typeDefinition.SortOrder,
            typeDefinition.DefaultProperties);

    private static bool CanCreateType(string type) =>
        !string.Equals(type, EntityTypeService.ProjectNodeType, StringComparison.Ordinal);

    private static bool AllowsCustomProperties(string type) =>
        !string.Equals(type, EntityTypeService.ProjectNodeType, StringComparison.Ordinal)
        && !string.Equals(type, EntityTypeService.ActNodeType, StringComparison.Ordinal)
        && !string.Equals(type, EntityTypeService.ChapterNodeType, StringComparison.Ordinal)
        && !string.Equals(type, EntityTypeService.ProjectFactNodeType, StringComparison.Ordinal);

    private static IReadOnlyList<string> EditablePropertyKeys(string type, IReadOnlyDictionary<string, object?> properties)
    {
        if (string.Equals(type, EntityTypeService.ActNodeType, StringComparison.Ordinal)
            || string.Equals(type, EntityTypeService.ChapterNodeType, StringComparison.Ordinal))
            return ["synopsis"];
        if (string.Equals(type, EntityTypeService.ProjectFactNodeType, StringComparison.Ordinal))
            return ["key", "value"];
        if (!AllowsCustomProperties(type)) return [];
        return properties.Keys
            .Where(key => !IsInternalProperty(key))
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsInternalProperty(string key) =>
        string.Equals(key, "sourceType", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "sourceId", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "structural", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "order", StringComparison.OrdinalIgnoreCase)
        || IngestSourceAssertions.IsProtectedProperty(key)
        || key.StartsWith("vectorIndex", StringComparison.OrdinalIgnoreCase);

    private async Task<Guid?> ResolveParentGuidAsync(Guid projectId, long? parentNodeId, CancellationToken cancellationToken)
    {
        if (parentNodeId is not long id) return null;
        var parent = await GetRequiredProjectNodeAsync(projectId, id, cancellationToken);
        return Guid.TryParseExact(parent.Key, "N", out var parentGuid) ? parentGuid : null;
    }

    private async Task<Guid?> ResolveActParentAsync(
        Guid projectId,
        long? parentNodeId,
        bool allowProjectParent,
        CancellationToken cancellationToken)
    {
        if (parentNodeId is null) return null;
        var parent = await GetRequiredProjectNodeAsync(projectId, parentNodeId.Value, cancellationToken);
        if (allowProjectParent && string.Equals(parent.NodeType, EntityTypeService.ProjectNodeType, StringComparison.Ordinal))
            return null;
        if (!string.Equals(parent.NodeType, EntityTypeService.ActNodeType, StringComparison.Ordinal))
            throw new InvalidOperationException("Chapters can only be parented by the project root or an act.");
        return ReadNodeGuid(parent);
    }

    private async Task<GraphNode> GetRequiredProjectNodeAsync(Guid projectId, long nodeId, CancellationToken cancellationToken)
    {
        var node = await nodes.GetByIdAsync(nodeId, cancellationToken)
            ?? throw new InvalidOperationException("Graph node not found.");
        if (node.ProjectId != projectId)
            throw new InvalidOperationException("Graph node belongs to a different project.");
        return node;
    }

    private async Task<GraphEdge> GetRequiredProjectEdgeAsync(Guid projectId, long edgeId, CancellationToken cancellationToken)
    {
        var edge = await edges.GetByIdAsync(edgeId, cancellationToken)
            ?? throw new InvalidOperationException("Graph relationship not found.");
        var from = await GetRequiredProjectNodeAsync(projectId, edge.FromNodeId, cancellationToken);
        var to = await GetRequiredProjectNodeAsync(projectId, edge.ToNodeId, cancellationToken);
        if (from.ProjectId != to.ProjectId)
            throw new InvalidOperationException("Graph relationship endpoints are inconsistent.");
        return edge;
    }

    private static string NormalizeEditableEdgeType(string edgeType)
    {
        var type = (edgeType ?? string.Empty).Trim();
        if (type.Length == 0)
            throw new ArgumentException("Relationship type is required.", nameof(edgeType));
        if (string.Equals(type, HasChildEdgeType, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Managed parent links cannot be edited directly.");
        return type;
    }

    private async Task TouchProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await projects.GetByIdAsync(projectId, cancellationToken);
        if (project is null) return;
        project.UpdatedAt = DateTime.UtcNow;
        projects.Update(project);
        await projects.SaveChangesAsync(cancellationToken);
    }

    private static Guid ReadNodeGuid(GraphNode node) =>
        Guid.TryParseExact(node.Key, "N", out var id)
            ? id
            : throw new InvalidOperationException($"Graph node {node.Id} does not have a GUID key.");

    private static string? Read(IReadOnlyDictionary<string, string?> properties, string key) =>
        properties.TryGetValue(key, out var value) ? value : null;

    private static string? Read(IReadOnlyDictionary<string, object?> properties, string key) =>
        properties.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static Dictionary<string, string?> CleanProperties(IReadOnlyDictionary<string, string?>? properties)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (properties is null) return result;
        foreach (var kv in properties)
        {
            var key = (kv.Key ?? string.Empty).Trim();
            if (key.Length == 0 || IsInternalProperty(key)) continue;
            result[key] = kv.Value;
        }
        return result;
    }

    private static Dictionary<string, object?> ToObjectDictionary(IReadOnlyDictionary<string, string?> properties) =>
        properties.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string?> ProjectProperties(IReadOnlyDictionary<string, object?> properties)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in properties)
        {
            if (IngestSourceAssertions.IsProtectedProperty(kv.Key)) continue;
            result[kv.Key] = kv.Value?.ToString();
        }
        return result;
    }

    private static Dictionary<string, string> BuildColorMap(IReadOnlyList<EntityTypeDefinition> typeDefinitions)
    {
        var colors = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < typeDefinitions.Count; i++)
        {
            var type = typeDefinitions[i];
            colors[type.Type] = Palette[Math.Abs(type.SortOrder + i) % Palette.Length];
        }
        return colors;
    }

    private static string FallbackColor(string type) =>
        Palette[Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode(type) % Palette.Length)];

    private static bool IsStructuralType(string type) =>
        string.Equals(type, EntityTypeService.ProjectNodeType, StringComparison.Ordinal)
        || string.Equals(type, EntityTypeService.ActNodeType, StringComparison.Ordinal)
        || string.Equals(type, EntityTypeService.ChapterNodeType, StringComparison.Ordinal)
        || string.Equals(type, EntityTypeService.ProjectFactNodeType, StringComparison.Ordinal)
        || string.Equals(type, EntityTypeService.EventNodeType, StringComparison.Ordinal);

    private static string HumanizeType(string type)
    {
        if (string.IsNullOrWhiteSpace(type)) return "Entity";
        var chars = new List<char> { type[0] };
        for (var i = 1; i < type.Length; i++)
        {
            var ch = type[i];
            if (char.IsUpper(ch) && !char.IsWhiteSpace(type[i - 1]))
                chars.Add(' ');
            chars.Add(ch);
        }
        return new string(chars.ToArray());
    }

    private static string Pluralize(string singular)
    {
        if (singular.EndsWith("y", StringComparison.OrdinalIgnoreCase) && singular.Length > 1)
            return singular[..^1] + "ies";
        if (singular.EndsWith("s", StringComparison.OrdinalIgnoreCase))
            return singular;
        return singular + "s";
    }
}