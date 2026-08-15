using System.Text.RegularExpressions;
using Lorekeeper.Ingest;
using Lorekeeper.Knowledge;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence;
using Lorekeeper.Persistence.Repositories;
using Lorekeeper.Search;

namespace Lorekeeper.Graph;

public sealed partial class GraphAutoLinkService(
IAppDatabaseOperationFactory database, IGraphStore graph, IProjectSearchService search, ILogger<GraphAutoLinkService> logger) : IGraphAutoLinkService
{
    public const string AutoMentionEdgeType = "AutoMention";
    public const string AutoFlagProperty = "autoLink";
    public const string SourceTypeProperty = "autoLinkSourceType";
    public const string SourceIdProperty = "autoLinkSourceId";
    public const string MatchedLabelProperty = "autoLinkMatchedLabel";
    public const string EvidenceProperty = "autoLinkEvidence";
    public const string ConfidenceProperty = "autoLinkConfidence";
    public const string VersionProperty = "autoLinkVersion";
    public const string RefreshedAtProperty = "autoLinkRefreshedAt";

    private const string Version = "1";
    private const int EvidenceRadius = 110;

    private static readonly HashSet<string> WeakTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "as", "at", "be", "been", "being", "book", "by",
        "chapter", "character", "for", "from", "he", "her", "hers", "him", "his",
        "i", "in", "is", "it", "location", "me", "none", "null", "of", "on",
        "or", "our", "part", "scene", "she", "story", "that", "the", "their",
        "them", "they", "this", "to", "unknown", "us", "was", "we", "were",
        "with", "you", "your",
    };

    public async Task<IReadOnlyList<GraphAutoMentionLink>> RefreshEntityAsync(
        Guid projectId,
        Guid entityId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var nodes = databaseOperation.Repositories.GraphNodes;
        var targetNode = await nodes.FindByKeyAsync(projectId, entityId.ToString("N"), cancellationToken);
        if (targetNode is null || !IsMentionTargetNode(targetNode)) return [];

        var labels = BuildMentionLabels(targetNode);
        if (labels.Count == 0)
        {
            await RemoveIncomingAutoMentionsAsync(targetNode.Id, cancellationToken);
            return [];
        }

        await RemoveIncomingAutoMentionsAsync(targetNode.Id, cancellationToken);

        var created = new Dictionary<long, GraphAutoMentionLink>();
        var sources = await ListProjectMentionSourcesAsync(projectId, cancellationToken);
        foreach (var source in sources)
        {
            if (source.Node.Id == targetNode.Id) continue;

            var content = await ReadAllSourceTextAsync(projectId, source.SourceType, source.SourceId, cancellationToken);
            if (string.IsNullOrWhiteSpace(content)) continue;
            if (!TryFindMention(content, labels, out var mention)) continue;

            var link = await UpsertAutoMentionAsync(
                source.Node,
                targetNode,
                source.SourceType,
                source.SourceId,
                mention,
                cancellationToken);
            created[source.Node.Id] = link;
        }

        return created.Values
            .OrderBy(link => link.SourceType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(link => link.SourceName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<GraphAutoMentionLink>> RefreshSourceAsync(
        Guid projectId,
        string sourceType,
        Guid sourceId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var nodes = databaseOperation.Repositories.GraphNodes;
        var normalizedType = ProjectSearchSourceTypes.Normalize(sourceType);
        var sourceNode = await ResolveSourceNodeAsync(projectId, normalizedType, sourceId, null, cancellationToken);
        if (sourceNode is null) return [];

        var content = await ReadAllSourceTextAsync(projectId, normalizedType, sourceId, cancellationToken);
        await RemoveOutgoingAutoMentionsAsync(sourceNode.Id, cancellationToken);
        if (string.IsNullOrWhiteSpace(content)) return [];

        var targetNodes = (await nodes.ListByProjectAsync(projectId, cancellationToken))
            .Where(node => node.Id != sourceNode.Id && IsMentionTargetNode(node))
            .ToList();

        var created = new List<GraphAutoMentionLink>();
        foreach (var targetNode in targetNodes)
        {
            var labels = BuildMentionLabels(targetNode);
            if (labels.Count == 0) continue;
            if (!TryFindMention(content, labels, out var mention)) continue;

            created.Add(await UpsertAutoMentionAsync(
                sourceNode,
                targetNode,
                normalizedType,
                sourceId,
                mention,
                cancellationToken));
        }

        return created
            .OrderBy(link => link.TargetEntityType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(link => link.TargetEntityName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<GraphAutoMentionLink>> ListEntityAutoMentionLinksAsync(
        Guid projectId,
        Guid entityId,
        int maxResults = 12,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var nodes = databaseOperation.Repositories.GraphNodes;
        var edges = databaseOperation.Repositories.GraphEdges;
        var node = await nodes.FindByKeyAsync(projectId, entityId.ToString("N"), cancellationToken);
        if (node is null) return [];

        var adjacent = await edges.GetAdjacentAsync(node.Id, EdgeDirection.Both, [AutoMentionEdgeType], maxResults, cancellationToken);
        if (adjacent.Count == 0) return [];

        var endpointIds = adjacent
            .Select(edge => edge.FromNodeId == node.Id ? edge.ToNodeId : edge.FromNodeId)
            .Distinct()
            .ToList();
        var byId = (await nodes.GetByIdsAsync(endpointIds, cancellationToken)).ToDictionary(item => item.Id);

        var result = new List<GraphAutoMentionLink>();
        foreach (var edge in adjacent.Where(IsAutoMentionEdge))
        {
            if (!byId.TryGetValue(edge.FromNodeId == node.Id ? edge.ToNodeId : edge.FromNodeId, out var other)) continue;
            var sourceNode = edge.FromNodeId == node.Id ? node : other;
            var targetNode = edge.ToNodeId == node.Id ? node : other;
            result.Add(ProjectAutoMention(edge, sourceNode, targetNode, edge.FromNodeId == node.Id ? "outgoing" : "incoming"));
        }

        return result
            .OrderBy(link => link.Direction, StringComparer.OrdinalIgnoreCase)
            .ThenBy(link => link.TargetEntityType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(link => link.TargetEntityName, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(maxResults, 1, 50))
            .ToList();
    }

    public static bool IsAutoMentionEdge(GraphEdge edge) =>
        string.Equals(edge.EdgeType, AutoMentionEdgeType, StringComparison.OrdinalIgnoreCase)
        || IsAutoLinkProperties(edge.Properties);

    public static bool IsAutoLinkProperties(IReadOnlyDictionary<string, object?> properties) =>
        properties.TryGetValue(AutoFlagProperty, out var value)
        && bool.TryParse(value?.ToString(), out var isAuto)
        && isAuto;

    public static bool IsAutoLinkProperties(IReadOnlyDictionary<string, string?> properties) =>
        properties.TryGetValue(AutoFlagProperty, out var value)
        && bool.TryParse(value, out var isAuto)
        && isAuto;

    public static bool IsProtectedAutoLinkProperty(string key) =>
        key.StartsWith("autoLink", StringComparison.OrdinalIgnoreCase);

    private async Task RemoveIncomingAutoMentionsAsync(long targetNodeId, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var edges = databaseOperation.Repositories.GraphEdges;
        var incoming = await edges.GetAdjacentAsync(targetNodeId, EdgeDirection.Incoming, [AutoMentionEdgeType], null, cancellationToken);
        foreach (var edge in incoming.Where(IsAutoMentionEdge))
            await graph.RemoveEdgeAsync(edge.Id, cancellationToken);
    }

    private async Task RemoveOutgoingAutoMentionsAsync(long sourceNodeId, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var edges = databaseOperation.Repositories.GraphEdges;
        var outgoing = await edges.GetAdjacentAsync(sourceNodeId, EdgeDirection.Outgoing, [AutoMentionEdgeType], null, cancellationToken);
        foreach (var edge in outgoing.Where(IsAutoMentionEdge))
            await graph.RemoveEdgeAsync(edge.Id, cancellationToken);
    }

    private async Task<GraphAutoMentionLink> UpsertAutoMentionAsync(
        GraphNode sourceNode,
        GraphNode targetNode,
        string sourceType,
        Guid sourceId,
        MentionMatch mention,
        CancellationToken cancellationToken)
    {
        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [AutoFlagProperty] = true,
            [SourceTypeProperty] = sourceType,
            [SourceIdProperty] = sourceId.ToString("N"),
            [MatchedLabelProperty] = mention.Label.Text,
            [EvidenceProperty] = mention.EvidenceExcerpt,
            [ConfidenceProperty] = mention.Label.IsAlias ? "0.85" : "1.00",
            [VersionProperty] = Version,
            [RefreshedAtProperty] = DateTime.UtcNow.ToString("o"),
            [IngestWikiSheet.SummaryProperty] = $"Auto mention of {targetNode.Label ?? targetNode.Key}",
        };

        var edge = await graph.UpsertEdgeAsync(
            sourceNode.Id,
            targetNode.Id,
            AutoMentionEdgeType,
            properties,
            cancellationToken: cancellationToken);

        return ProjectAutoMention(edge, sourceNode, targetNode, "outgoing");
    }

    private async Task<GraphNode?> ResolveSourceNodeAsync(
        Guid projectId,
        string sourceType,
        Guid sourceId,
        Guid? containerSourceId,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var nodes = databaseOperation.Repositories.GraphNodes;
        var ingest = databaseOperation.Repositories.Ingest;
        var key = sourceId.ToString("N");
        var normalizedType = ProjectSearchSourceTypes.Normalize(sourceType);
        var node = normalizedType switch
        {
            ProjectSearchSourceTypes.Chapter or ProjectSearchSourceTypes.ContextChapter
                => await nodes.FindAsync(projectId, EntityTypeService.ChapterNodeType, key, cancellationToken),
            ProjectSearchSourceTypes.Act
                => await nodes.FindAsync(projectId, EntityTypeService.ActNodeType, key, cancellationToken),
            ProjectSearchSourceTypes.Entity
                => await nodes.FindByKeyAsync(projectId, key, cancellationToken),
            ProjectSearchSourceTypes.RawIngestSource or ProjectSearchSourceTypes.IngestSource
                => await nodes.FindAsync(projectId, EntityTypeService.SourceNodeType, key, cancellationToken),
            ProjectSearchSourceTypes.IngestSourceChunk
                => await nodes.FindAsync(projectId, EntityTypeService.SourceChunkNodeType, key, cancellationToken),
            _ => null,
        };

        if (node is not null) return node;
        if (!string.Equals(normalizedType, ProjectSearchSourceTypes.IngestSourceChunk, StringComparison.OrdinalIgnoreCase))
            return null;

        var containerId = containerSourceId;
        if (containerId is null)
        {
            var chunk = await ingest.GetSourceChunkAsync(sourceId, cancellationToken);
            containerId = chunk?.SourceId;
        }

        return containerId is Guid source
            ? await nodes.FindAsync(projectId, EntityTypeService.SourceNodeType, source.ToString("N"), cancellationToken)
            : null;
    }

    private async Task<string> ReadAllSourceTextAsync(
        Guid projectId,
        string sourceType,
        Guid sourceId,
        CancellationToken cancellationToken)
    {
        var pages = new List<string>();
        var pageNumber = 1;
        while (true)
        {
            var page = await search.ReadSourceAsync(projectId, sourceType, sourceId, pageNumber, cancellationToken);
            if (page is null) return string.Empty;
            pages.Add(page.Content);
            if (!page.HasNextPage) break;
            pageNumber++;
            if (pageNumber > page.PageCount + 1)
            {
                logger.LogWarning("Stopped auto-link source read loop for {SourceType}/{SourceId}", sourceType, sourceId);
                break;
            }
        }

        return string.Join('\n', pages);
    }

    private async Task<IReadOnlyList<AutoLinkSourceRef>> ListProjectMentionSourcesAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var nodes = databaseOperation.Repositories.GraphNodes;
        var result = new List<AutoLinkSourceRef>();
        foreach (var node in await nodes.ListByProjectAsync(projectId, cancellationToken))
        {
            if (!Guid.TryParseExact(node.Key, "N", out var sourceId)) continue;
            var sourceType = SourceTypeForMentionSourceNode(node);
            if (sourceType is null) continue;
            result.Add(new AutoLinkSourceRef(node, sourceType, sourceId));
        }

        return result;
    }

    private static string? SourceTypeForMentionSourceNode(GraphNode node)
    {
        if (string.Equals(node.NodeType, EntityTypeService.ChapterNodeType, StringComparison.OrdinalIgnoreCase))
            return ProjectSearchSourceTypes.Chapter;
        if (string.Equals(node.NodeType, EntityTypeService.ActNodeType, StringComparison.OrdinalIgnoreCase))
            return ProjectSearchSourceTypes.Act;
        if (string.Equals(node.NodeType, EntityTypeService.SourceNodeType, StringComparison.OrdinalIgnoreCase))
            return ProjectSearchSourceTypes.RawIngestSource;
        if (string.Equals(node.NodeType, EntityTypeService.SourceChunkNodeType, StringComparison.OrdinalIgnoreCase))
            return ProjectSearchSourceTypes.IngestSourceChunk;
        if (IsMentionSourceEntityNode(node))
            return ProjectSearchSourceTypes.Entity;

        return null;
    }

    private static IReadOnlyList<MentionLabel> BuildMentionLabels(GraphNode node)
    {
        var labels = new List<MentionLabel>();
        AddLabel(labels, node.Label ?? node.Key, isAlias: false);
        foreach (var alias in IngestWikiSheet.ReadAliases(node.Properties))
            AddLabel(labels, alias, isAlias: true);

        return labels
            .GroupBy(label => label.Text, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(label => label.IsAlias).First())
            .OrderByDescending(label => label.Text.Length)
            .ToList();
    }

    private static void AddLabel(ICollection<MentionLabel> labels, string? value, bool isAlias)
    {
        var label = NormalizeMentionLabel(value);
        if (label.Length == 0) return;
        if (isAlias && label.Length < 3) return;
        if (WeakTerms.Contains(label)) return;
        labels.Add(new MentionLabel(label, isAlias));
    }

    private static string NormalizeMentionLabel(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : WhitespaceRegex().Replace(value.Trim(), " ");

    private static bool TryFindMention(string content, IReadOnlyList<MentionLabel> labels, out MentionMatch mention)
    {
        foreach (var label in labels)
        {
            var pattern = $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(label.Text)}(?![\p{{L}}\p{{N}}])";
            var match = Regex.Match(content, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
            if (!match.Success) continue;
            mention = new MentionMatch(label, BuildEvidenceExcerpt(content, match.Index, match.Length));
            return true;
        }

        mention = default;
        return false;
    }

    private static string BuildEvidenceExcerpt(string text, int index, int length)
    {
        var start = Math.Max(0, index - EvidenceRadius);
        var end = Math.Min(text.Length, index + length + EvidenceRadius);
        var excerpt = text[start..end].Replace('\r', ' ').Replace('\n', ' ').Trim();
        return excerpt.Length <= 260 ? excerpt : excerpt[..260] + "...";
    }

    private static GraphAutoMentionLink ProjectAutoMention(
        GraphEdge edge,
        GraphNode sourceNode,
        GraphNode targetNode,
        string direction)
    {
        _ = Guid.TryParseExact(sourceNode.Key, "N", out var sourceId);
        _ = Guid.TryParseExact(targetNode.Key, "N", out var targetId);
        var confidence = double.TryParse(Read(edge.Properties, ConfidenceProperty), out var parsed) ? parsed : 1d;
        return new GraphAutoMentionLink(
            edge.Id,
            direction,
            sourceId,
            sourceNode.NodeType,
            sourceNode.Label ?? sourceNode.Key,
            targetId,
            targetNode.NodeType,
            targetNode.Label ?? targetNode.Key,
            Read(edge.Properties, MatchedLabelProperty) ?? string.Empty,
            Read(edge.Properties, EvidenceProperty) ?? string.Empty,
            confidence);
    }

    private static string? Read(IReadOnlyDictionary<string, object?> properties, string key) =>
        properties.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static bool IsMentionTargetNode(GraphNode node) =>
        Guid.TryParseExact(node.Key, "N", out _)
        && !string.Equals(node.NodeType, EntityTypeService.ProjectNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(node.NodeType, EntityTypeService.ActNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(node.NodeType, EntityTypeService.ChapterNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(node.NodeType, EntityTypeService.ProjectFactNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(node.NodeType, EntityTypeService.EventNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(node.NodeType, EntityTypeService.SourceNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(node.NodeType, EntityTypeService.SourceChunkNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(node.NodeType, EntityTypeService.SourceBlockNodeType, StringComparison.OrdinalIgnoreCase);

    private static bool IsMentionSourceEntityNode(GraphNode node) =>
        Guid.TryParseExact(node.Key, "N", out _)
        && !string.Equals(node.NodeType, EntityTypeService.ProjectNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(node.NodeType, EntityTypeService.ActNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(node.NodeType, EntityTypeService.ChapterNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(node.NodeType, EntityTypeService.ProjectFactNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(node.NodeType, EntityTypeService.SourceNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(node.NodeType, EntityTypeService.SourceChunkNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(node.NodeType, EntityTypeService.SourceBlockNodeType, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    private readonly record struct MentionLabel(string Text, bool IsAlias);

    private readonly record struct MentionMatch(MentionLabel Label, string EvidenceExcerpt);

    private sealed record AutoLinkSourceRef(GraphNode Node, string SourceType, Guid SourceId);
}
