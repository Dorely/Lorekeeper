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
        IEnumerable<IngestStagingRecord> stagingRecords,
        CancellationToken cancellationToken = default)
    {
        var stagingRecordList = stagingRecords.ToList();
        var sourceKey = IngestSourceAssertions.SourceKey(sourceId);
        var projectNodes = await nodes.ListByProjectAsync(projectId, cancellationToken);
        var projectEdges = await edges.ListByProjectAsync(projectId, cancellationToken);
        var nodeActions = BuildNodeActions(stagingRecordList, projectNodes);
        var edgeActions = BuildEdgeActions(stagingRecordList);
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
            .Where(edge => edgeActions.ContainsKey(edge.Id)
                || IngestSourceAssertions.ContainsRelationshipSource(edge.Properties, sourceId)
                || IngestWikiSheet.ContainsCanonSource(edge.Properties, sourceId))
            .ToList())
        {
            var canonChanged = IngestWikiSheet.RemoveCanonSource(edge.Properties, sourceId);
            var wikiChanged = IngestWikiSheet.RemoveSourceCitations(edge.Properties, sourceId);
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
            else if (removal.Removed || wikiChanged || canonChanged)
            {
                AddEdgeEndpointContextEntityIds(edge, nodeById, entityIdsToReindex);
                edge.UpdatedAt = DateTime.UtcNow;
                edges.Update(edge);
                await edges.SaveChangesAsync(cancellationToken);
                edgesUpdated++;
            }
        }

        foreach (var node in projectNodes
            .Where(node => nodeActions.ContainsKey(node.Id)
                || IngestSourceAssertions.ContainsEntitySource(node.Properties, sourceId)
                || IngestWikiSheet.ContainsCanonSource(node.Properties, sourceId))
            .ToList())
        {
            var canonChanged = IngestWikiSheet.RemoveCanonSource(node.Properties, sourceId);
            var wikiChanged = IngestWikiSheet.RemoveSourceCitations(node.Properties, sourceId);
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
            else if (removal.Removed || wikiChanged || canonChanged)
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
        IReadOnlyCollection<IngestStagingRecord> reportItems,
        IReadOnlyList<GraphNode> projectNodes)
    {
        var actions = new Dictionary<long, string?>();
        foreach (var item in reportItems.Where(item => item.Status != IngestStagingRecordStatus.Deleted && item.Kind == IngestStagingRecordKind.Entity))
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

    private static Dictionary<long, string?> BuildEdgeActions(IReadOnlyCollection<IngestStagingRecord> reportItems)
    {
        var actions = new Dictionary<long, string?>();
        foreach (var item in reportItems.Where(item => item.Status != IngestStagingRecordStatus.Deleted && item.Kind == IngestStagingRecordKind.Relationship))
        {
            if (item.GraphEdgeId is not long edgeId) continue;
            actions[edgeId] = MergeCreatedAction(
                actions.GetValueOrDefault(edgeId),
                IngestSourceAssertions.ReadRelationshipGraphAction(item.PayloadJson),
                IngestSourceAssertions.CreatedEdgeAction);
        }

        return actions;
    }

    private static GraphNode? FindReportNode(IngestStagingRecord item, IReadOnlyList<GraphNode> projectNodes)
    {
        if (item.GraphNodeId is long graphNodeId)
        {
            var node = projectNodes.FirstOrDefault(candidate => candidate.Id == graphNodeId);
            if (node is not null) return node;
        }

        if (item.EntityId is not Guid entityId) return null;

        var key = entityId.ToString("N");
        if (!string.IsNullOrWhiteSpace(item.EntityType))
        {
            var typedNode = projectNodes.FirstOrDefault(candidate =>
                string.Equals(candidate.NodeType, item.EntityType, StringComparison.OrdinalIgnoreCase)
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
        if (IngestWikiSheet.HasCanonSources(node.Properties))
            return false;
        if (IngestWikiSheet.HasCitations(node.Properties))
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
        if (IngestWikiSheet.HasCanonSources(edge.Properties))
            return false;
        if (IngestWikiSheet.HasCitations(edge.Properties))
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
            && !IngestSourceAssertions.IsLegacyIngestProperty(key)
            && !IngestWikiSheet.IsWikiStorageProperty(key));

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
