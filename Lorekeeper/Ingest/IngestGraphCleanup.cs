using Lorekeeper.Knowledge;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence.Repositories;

namespace Lorekeeper.Ingest;

public sealed class IngestGraphCleanup(
    IGraphStore graphStore,
    IGraphNodeRepository nodes,
    IGraphEdgeRepository edges) : IIngestGraphCleanup
{
    public async Task<IngestGraphCleanupResult> RemoveSourceGraphContributionsAsync(
        Guid projectId,
        Guid sourceId,
        IEnumerable<IngestReportItem> reportItems,
        CancellationToken cancellationToken = default)
    {
        var reportItemList = reportItems.ToList();
        var sourceKey = IngestSourceAssertions.SourceKey(sourceId);
        var projectNodes = await nodes.ListByProjectAsync(projectId, cancellationToken);
        var projectEdges = await edges.ListByProjectAsync(projectId, cancellationToken);
        var nodeActions = BuildNodeActions(reportItemList, projectNodes);
        var edgeActions = BuildEdgeActions(reportItemList);
        var nodeById = projectNodes.ToDictionary(node => node.Id);
        var sourceGraphNodeIds = projectNodes
            .Where(node => IsSourceGraphNodeForSource(node, sourceKey))
            .Select(node => node.Id)
            .ToHashSet();
        var entityIdsToReindex = new HashSet<Guid>();
        var entityIdsToDelete = new HashSet<Guid>();

        var nodesUpdated = 0;
        var nodesDeleted = 0;
        var edgesUpdated = 0;
        var edgesDeleted = 0;
        var extractedFromEdgesDeleted = 0;

        var deletedEdgeIds = new HashSet<long>();
        foreach (var edge in projectEdges.Where(edge => IsSourceOwnedExtractedFromEdge(edge, sourceKey, sourceGraphNodeIds)).ToList())
        {
            AddEdgeEndpointContextEntityIds(edge, nodeById, entityIdsToReindex);
            await graphStore.RemoveEdgeAsync(edge.Id, cancellationToken);
            deletedEdgeIds.Add(edge.Id);
            extractedFromEdgesDeleted++;
        }

        foreach (var edge in projectEdges
            .Where(edge => !deletedEdgeIds.Contains(edge.Id))
            .Where(edge => edgeActions.ContainsKey(edge.Id) || IngestSourceAssertions.ContainsRelationshipSource(edge.Properties, sourceId))
            .ToList())
        {
            var removal = IngestSourceAssertions.ContainsRelationshipSource(edge.Properties, sourceId)
                ? IngestSourceAssertions.RemoveRelationshipSource(edge.Properties, sourceId)
                : new IngestAssertionRemovalResult(false, IngestSourceAssertions.CountRelationshipSources(edge.Properties));

            var graphAction = edgeActions.GetValueOrDefault(edge.Id);
            if (CanRemoveGraphEdgeAfterSourceSubtraction(edge, graphAction))
            {
                AddEdgeEndpointContextEntityIds(edge, nodeById, entityIdsToReindex);
                await graphStore.RemoveEdgeAsync(edge.Id, cancellationToken);
                deletedEdgeIds.Add(edge.Id);
                edgesDeleted++;
            }
            else if (removal.Removed)
            {
                AddEdgeEndpointContextEntityIds(edge, nodeById, entityIdsToReindex);
                edge.UpdatedAt = DateTime.UtcNow;
                edges.Update(edge);
                await edges.SaveChangesAsync(cancellationToken);
                edgesUpdated++;
            }
        }

        foreach (var node in projectNodes
            .Where(node => nodeActions.ContainsKey(node.Id) || IngestSourceAssertions.ContainsEntitySource(node.Properties, sourceId))
            .ToList())
        {
            var removal = IngestSourceAssertions.ContainsEntitySource(node.Properties, sourceId)
                ? IngestSourceAssertions.RemoveEntitySource(node.Properties, sourceId)
                : new IngestAssertionRemovalResult(false, IngestSourceAssertions.CountEntitySources(node.Properties));

            var graphAction = nodeActions.GetValueOrDefault(node.Id);
            if (await CanRemoveGraphNodeAfterSourceSubtractionAsync(node, graphAction, cancellationToken))
            {
                AddContextEntityId(node, entityIdsToDelete);
                await graphStore.RemoveNodeAsync(node.Id, cancellationToken);
                nodesDeleted++;
            }
            else if (removal.Removed)
            {
                AddContextEntityId(node, entityIdsToReindex);
                node.UpdatedAt = DateTime.UtcNow;
                nodes.Update(node);
                await nodes.SaveChangesAsync(cancellationToken);
                nodesUpdated++;
            }
        }

        return new IngestGraphCleanupResult(
            nodesUpdated,
            nodesDeleted,
            edgesUpdated,
            edgesDeleted,
                extractedFromEdgesDeleted,
                entityIdsToReindex.Except(entityIdsToDelete).ToList(),
                entityIdsToDelete.ToList());
    }

    private static Dictionary<long, string?> BuildNodeActions(
        IReadOnlyCollection<IngestReportItem> reportItems,
        IReadOnlyList<GraphNode> projectNodes)
    {
        var actions = new Dictionary<long, string?>();
        foreach (var item in reportItems.Where(item => item.Status != IngestReportItemStatus.Deleted && item.Kind == IngestReportItemKind.Entity))
        {
            var node = FindReportNode(item, projectNodes);
            if (node is null) continue;

            actions[node.Id] = MergeCreatedAction(
                actions.GetValueOrDefault(node.Id),
                IngestSourceAssertions.ReadEntityGraphAction(item.PayloadJson),
                IngestSourceAssertions.CreatedEntityAction);
        }

        return actions;
    }

    private static Dictionary<long, string?> BuildEdgeActions(IReadOnlyCollection<IngestReportItem> reportItems)
    {
        var actions = new Dictionary<long, string?>();
        foreach (var item in reportItems.Where(item => item.Status != IngestReportItemStatus.Deleted && item.Kind == IngestReportItemKind.Relationship))
        {
            if (item.GraphEdgeId is not long edgeId) continue;
            actions[edgeId] = MergeCreatedAction(
                actions.GetValueOrDefault(edgeId),
                IngestSourceAssertions.ReadRelationshipGraphAction(item.PayloadJson),
                IngestSourceAssertions.CreatedEdgeAction);
        }

        return actions;
    }

    private static GraphNode? FindReportNode(IngestReportItem item, IReadOnlyList<GraphNode> projectNodes)
    {
        if (item.GraphNodeId is long graphNodeId)
        {
            var node = projectNodes.FirstOrDefault(candidate => candidate.Id == graphNodeId);
            if (node is not null) return node;
        }

        if (item.EntityId is not Guid entityId) return null;

        var key = entityId.ToString("N");
        if (!string.IsNullOrWhiteSpace(item.ResourceType))
        {
            var typedNode = projectNodes.FirstOrDefault(candidate =>
                string.Equals(candidate.NodeType, item.ResourceType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase));
            if (typedNode is not null) return typedNode;
        }

        return projectNodes.FirstOrDefault(candidate => string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    private static string? MergeCreatedAction(string? existing, string? next, string createdAction)
    {
        if (string.Equals(existing, createdAction, StringComparison.Ordinal)
            || string.Equals(next, createdAction, StringComparison.Ordinal))
        {
            return createdAction;
        }

        return existing ?? next;
    }

    private async Task<bool> CanRemoveGraphNodeAfterSourceSubtractionAsync(
        GraphNode node,
        string? graphAction,
        CancellationToken cancellationToken)
    {
        if (!CanRemovePotentiallyIngestCreatedObject(node.Properties, graphAction, IngestSourceAssertions.CreatedEntityAction))
            return false;
        if (IngestSourceAssertions.CountEntitySources(node.Properties) > 0)
            return false;
        if (HasCanonicalProperties(node.Properties))
            return false;

        var adjacent = await edges.GetAdjacentAsync(node.Id, EdgeDirection.Both, edgeTypes: null, maxResults: null, cancellationToken);
        return adjacent.Count == 0;
    }

    private static bool CanRemoveGraphEdgeAfterSourceSubtraction(GraphEdge edge, string? graphAction)
    {
        if (!CanRemovePotentiallyIngestCreatedObject(edge.Properties, graphAction, IngestSourceAssertions.CreatedEdgeAction))
            return false;
        if (IngestSourceAssertions.CountRelationshipSources(edge.Properties) > 0)
            return false;
        return !HasCanonicalProperties(edge.Properties);
    }

    private static bool CanRemovePotentiallyIngestCreatedObject(
        IReadOnlyDictionary<string, object?> properties,
        string? graphAction,
        string createdAction) =>
        IngestSourceAssertions.IsIngestCreatedGraphObject(properties)
        || string.Equals(graphAction, createdAction, StringComparison.Ordinal);

    private static bool HasCanonicalProperties(IReadOnlyDictionary<string, object?> properties) =>
        properties.Keys.Any(key => !IsInternalProperty(key)
            && !IngestSourceAssertions.IsProtectedProperty(key)
            && !IngestSourceAssertions.IsLegacyIngestProperty(key));

    private static bool IsInternalProperty(string key) =>
        string.Equals(key, "sourceType", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "sourceId", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "sourceChunkId", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "sourceChunkIndex", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "sourceBlockId", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "sourcePageId", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "sourceGraphTargetType", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "structural", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "order", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("vectorIndex", StringComparison.OrdinalIgnoreCase);

    private static bool IsSourceGraphNodeForSource(GraphNode node, string sourceKey) =>
        (string.Equals(node.NodeType, IngestGraphSync.SourceNodeType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(node.Key, sourceKey, StringComparison.OrdinalIgnoreCase))
        || (string.Equals(node.NodeType, IngestGraphSync.SourceChunkNodeType, StringComparison.OrdinalIgnoreCase)
            && HasSourceId(node.Properties, sourceKey))
        || (string.Equals(node.NodeType, IngestGraphSync.SourceBlockNodeType, StringComparison.OrdinalIgnoreCase)
            && HasSourceId(node.Properties, sourceKey));

    private static bool IsSourceOwnedExtractedFromEdge(GraphEdge edge, string sourceKey, HashSet<long> sourceGraphNodeIds) =>
        string.Equals(edge.EdgeType, IngestGraphSync.ExtractedFromEdgeType, StringComparison.OrdinalIgnoreCase)
        && (HasSourceId(edge.Properties, sourceKey)
            || sourceGraphNodeIds.Contains(edge.FromNodeId)
            || sourceGraphNodeIds.Contains(edge.ToNodeId));

    private static bool HasSourceId(IReadOnlyDictionary<string, object?> properties, string sourceKey) =>
        properties.TryGetValue("sourceId", out var value)
        && string.Equals(value?.ToString(), sourceKey, StringComparison.OrdinalIgnoreCase);

    private static void AddEdgeEndpointContextEntityIds(
        GraphEdge edge,
        IReadOnlyDictionary<long, GraphNode> nodeById,
        ISet<Guid> ids)
    {
        if (nodeById.TryGetValue(edge.FromNodeId, out var fromNode))
            AddContextEntityId(fromNode, ids);
        if (nodeById.TryGetValue(edge.ToNodeId, out var toNode))
            AddContextEntityId(toNode, ids);
    }

    private static void AddContextEntityId(GraphNode node, ISet<Guid> ids)
    {
        if (IsContextEntityNode(node) && Guid.TryParseExact(node.Key, "N", out var entityId))
            ids.Add(entityId);
    }

    private static bool IsContextEntityNode(GraphNode node) =>
        Guid.TryParseExact(node.Key, "N", out _)
        && IsContextEntityType(node.NodeType);

    private static bool IsContextEntityType(string type) =>
        !string.Equals(type, EntityTypeService.ProjectNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.ActNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.ChapterNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.ProjectFactNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.SourceNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.SourceChunkNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.SourceBlockNodeType, StringComparison.OrdinalIgnoreCase);
}
