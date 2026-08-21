using Lorekeeper.Knowledge;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Persistence.Repositories;
using Lorekeeper.Context;

namespace Lorekeeper.Outline;

public sealed class ProjectFactService(
IAppDatabaseOperationFactory database, IGraphStore graph, IOutlineGraphSync outlineGraphSync, IEntityService entities, IContextIndexingService contextIndexing) : IProjectFactService
{
    private const string KeyProperty = "key";
    private const string ValueProperty = "value";

    public async Task<IReadOnlyList<ProjectFact>> ListAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var nodes = databaseOperation.Repositories.GraphNodes;
        var factNodes = await nodes.ListByTypeAsync(projectId, EntityTypeService.ProjectFactNodeType, cancellationToken);
        var facts = new List<ProjectFact>(factNodes.Count);
        foreach (var node in factNodes)
        {
            if (!TryReadNodeGuid(node, out var factId)) continue;
            facts.Add(await ProjectAsync(projectId, node, factId, cancellationToken));
        }

        return facts
            .OrderBy(fact => fact.Key.StartsWith("outline.", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(fact => fact.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<ProjectFact?> GetByKeyAsync(Guid projectId, string key, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeKey(key);
        var existing = await FindByFactKeyAsync(projectId, normalized, cancellationToken);
        if (existing is null || !TryReadNodeGuid(existing, out var factId)) return null;
        return await ProjectAsync(projectId, existing, factId, cancellationToken);
    }

    public async Task<ProjectFact> UpsertAsync(
        Guid projectId,
        string key,
        string? value,
        Guid? id = null,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var nodes = databaseOperation.Repositories.GraphNodes;
        var projects = databaseOperation.Repositories.Projects;
        var normalized = NormalizeKey(key);
        var project = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");
        await outlineGraphSync.EnsureProjectAsync(project, cancellationToken);

        var existing = id is Guid factId
            ? await nodes.FindByKeyAsync(projectId, factId.ToString("N"), cancellationToken)
            : await FindByFactKeyAsync(projectId, normalized, cancellationToken);

        GraphNode node;
        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [KeyProperty] = normalized,
            [ValueProperty] = value ?? string.Empty,
        };

        if (existing is null)
        {
            node = await graph.UpsertNodeAsync(
                projectId,
                EntityTypeService.ProjectFactNodeType,
                (id ?? Guid.NewGuid()).ToString("N"),
                LabelFromKey(normalized),
                properties,
                cancellationToken);
        }
        else
        {
            if (existing.NodeType != EntityTypeService.ProjectFactNodeType)
                throw new InvalidOperationException($"Entity {existing.Key} is not a ProjectFact.");

            existing.Label = LabelFromKey(normalized);
            existing.Properties[KeyProperty] = normalized;
            existing.Properties[ValueProperty] = value ?? string.Empty;
            existing.UpdatedAt = DateTime.UtcNow;
            nodes.Update(existing);
            await databaseOperation.SaveChangesAsync(cancellationToken);
            node = existing;
        }

        await EnsureProjectParentEdgeAsync(project, node, cancellationToken);
        TouchProject(project);
        await databaseOperation.SaveChangesAsync(cancellationToken);

        var nodeId = Guid.ParseExact(node.Key, "N");
        await contextIndexing.ReindexEntityAsync(projectId, nodeId, cancellationToken);
        return await ProjectAsync(projectId, node, nodeId, cancellationToken);
    }

    public async Task DeleteAsync(Guid projectId, Guid factId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var nodes = databaseOperation.Repositories.GraphNodes;
        var projects = databaseOperation.Repositories.Projects;
        var node = await nodes.FindByKeyAsync(projectId, factId.ToString("N"), cancellationToken);
        if (node is null || node.NodeType != EntityTypeService.ProjectFactNodeType) return;

        var project = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");
        await graph.RemoveNodeAsync(node.Id, cancellationToken);
        TouchProject(project);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        await contextIndexing.DeleteEntityAsync(projectId, factId, cancellationToken);
    }

    private async Task<GraphNode?> FindByFactKeyAsync(Guid projectId, string key, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var nodes = databaseOperation.Repositories.GraphNodes;
        var factNodes = await nodes.ListByTypeAsync(projectId, EntityTypeService.ProjectFactNodeType, cancellationToken);
        return factNodes.FirstOrDefault(node =>
            string.Equals(ReadString(node.Properties, KeyProperty), key, StringComparison.OrdinalIgnoreCase));
    }

    private async Task EnsureProjectParentEdgeAsync(Project project, GraphNode factNode, CancellationToken cancellationToken)
    {
        var projectNode = await graph.FindNodeAsync(
                project.Id,
                EntityTypeService.ProjectNodeType,
                project.Id.ToString("N"),
                cancellationToken)
            ?? throw new InvalidOperationException($"Project graph node {project.Id:N} not found.");

        var existingFacts = await ListAsync(project.Id, cancellationToken);
        var sortOrder = existingFacts.ToList().FindIndex(fact => fact.Id.ToString("N") == factNode.Key);
        if (sortOrder < 0) sortOrder = existingFacts.Count;

        await graph.UpsertEdgeAsync(
            projectNode.Id,
            factNode.Id,
            EntityService.HasChildEdgeType,
            properties: null,
            sortOrder: sortOrder,
            cancellationToken: cancellationToken);
    }

    private async Task<ProjectFact> ProjectAsync(
        Guid projectId,
        GraphNode node,
        Guid factId,
        CancellationToken cancellationToken)
    {
        var links = await entities.ListLinksAsync(projectId, factId, cancellationToken);
        var visibleLinks = links
            .Where(link =>
                !string.Equals(link.EdgeType, EntityService.HasChildEdgeType, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(link.OtherEntityType, EntityTypeService.ProjectNodeType, StringComparison.OrdinalIgnoreCase))
            .Select(link => new ProjectFactLink(
                link.EdgeType,
                link.Direction,
                link.OtherEntityId,
                link.OtherEntityName,
                link.OtherEntityType))
            .ToList();

        var key = ReadString(node.Properties, KeyProperty) ?? node.Key;
        var name = string.IsNullOrWhiteSpace(node.Label) || string.Equals(node.Label, key, StringComparison.OrdinalIgnoreCase)
            ? LabelFromKey(key)
            : node.Label;

        return new ProjectFact(
            factId,
            key,
            name,
            ReadString(node.Properties, ValueProperty) ?? string.Empty,
            visibleLinks);
    }

    private static bool TryReadNodeGuid(GraphNode node, out Guid id) =>
        Guid.TryParseExact(node.Key, "N", out id);

    private static string NormalizeKey(string key)
    {
        var trimmed = (key ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("Project fact key is required.", nameof(key));
        return trimmed;
    }

    private static string LabelFromKey(string key)
    {
        var display = key.StartsWith("outline.", StringComparison.OrdinalIgnoreCase) ? key[8..] : key;
        if (display.Length == 0) return key;
        return char.ToUpperInvariant(display[0]) + display[1..];
    }

    private static string? ReadString(IDictionary<string, object?> source, string key) =>
        source.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static void TouchProject(Project project)
    {
        project.UpdatedAt = DateTime.UtcNow;
    }
}
