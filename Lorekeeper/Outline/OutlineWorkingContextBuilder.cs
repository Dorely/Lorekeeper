using System.Text.Json;
using Lorekeeper.Ingest;
using Lorekeeper.Llm;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Projects;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Outline;

public sealed class OutlineWorkingContextBuilder(
    IAppDatabaseOperationFactory database,
    IEntityTypeService entityTypes,
    IBookBriefService bookBriefs) : IOutlineWorkingContextBuilder
{
    public async Task<IReadOnlyList<SystemPromptSourceSection>> BuildAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        var acts = await operation.Repositories.Acts.ListByProjectAsync(projectId, cancellationToken);
        var chapters = await operation.Repositories.Chapters.ListByProjectAsync(projectId, cancellationToken);
        var nodes = await operation.Repositories.GraphNodes.ListByProjectAsync(projectId, cancellationToken);
        var edges = await operation.Repositories.GraphEdges.ListByProjectAsync(projectId, cancellationToken);
        var nodeById = nodes.ToDictionary(node => node.Id);
        var nodeByStableId = nodes
            .Where(node => Guid.TryParse(node.Key, out _))
            .ToDictionary(node => Guid.Parse(node.Key));

        object Endpoint(long nodeId) => nodeById.TryGetValue(nodeId, out var node)
            ? new { id = StableId(node.Key), type = node.NodeType, name = node.Label ?? node.Key }
            : new { id = string.Empty, type = "Unknown", name = "Unknown" };

        object ChapterPayload(Chapter chapter)
        {
            nodeByStableId.TryGetValue(chapter.Id, out var chapterNode);
            var chapterEdges = chapterNode is null
                ? []
                : edges.Where(edge =>
                        string.Equals(edge.EdgeType, EntityService.RelevantToEdgeType, StringComparison.OrdinalIgnoreCase)
                        && (edge.FromNodeId == chapterNode.Id || edge.ToNodeId == chapterNode.Id))
                    .ToList();
            var attachments = chapterEdges
                .Select(edge => Endpoint(edge.FromNodeId == chapterNode!.Id ? edge.ToNodeId : edge.FromNodeId))
                .ToList();
            var beatNodes = chapterNode is null
                ? []
                : edges.Where(edge => edge.FromNodeId == chapterNode.Id
                        && string.Equals(edge.EdgeType, EntityService.HasChildEdgeType, StringComparison.OrdinalIgnoreCase)
                        && nodeById.TryGetValue(edge.ToNodeId, out var child)
                        && string.Equals(child.NodeType, EntityTypeService.EventNodeType, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(edge => edge.SortOrder)
                    .Select(edge => nodeById[edge.ToNodeId])
                    .ToList();
            var beats = beatNodes.Select(beat => new
            {
                id = StableId(beat.Key),
                name = beat.Label ?? beat.Key,
                summary = IngestWikiSheet.ReadSummary(beat.Properties),
                attachedEntities = edges
                    .Where(edge => (edge.FromNodeId == beat.Id || edge.ToNodeId == beat.Id)
                        && !string.Equals(edge.EdgeType, EntityService.HasChildEdgeType, StringComparison.OrdinalIgnoreCase))
                    .Select(edge => new
                    {
                        relationship = edge.EdgeType,
                        entity = Endpoint(edge.FromNodeId == beat.Id ? edge.ToNodeId : edge.FromNodeId),
                    })
                    .ToList(),
            }).ToList();
            return new
            {
                id = chapter.Id,
                order = chapter.Order,
                chapter.Title,
                chapter.Synopsis,
                relevantEntities = attachments,
                beats,
            };
        }

        var chaptersByAct = chapters.Where(chapter => chapter.ActId is not null)
            .GroupBy(chapter => chapter.ActId!.Value)
            .ToDictionary(group => group.Key, group => group.OrderBy(chapter => chapter.Order).ToList());
        var facts = nodes
            .Where(node => string.Equals(node.NodeType, EntityTypeService.ProjectFactNodeType, StringComparison.OrdinalIgnoreCase))
            .Select(node => new
            {
                id = StableId(node.Key),
                name = node.Label ?? node.Key,
                key = Property(node.Properties, "key"),
                value = Property(node.Properties, "value"),
            });
        var outline = new
        {
            projectFacts = facts,
            acts = acts.OrderBy(act => act.Order).Select(act => new
            {
                id = act.Id,
                order = act.Order,
                act.Title,
                act.Synopsis,
                chapters = (chaptersByAct.TryGetValue(act.Id, out var owned) ? owned : []).Select(ChapterPayload),
            }),
            unassignedChapters = chapters.Where(chapter => chapter.ActId is null)
                .OrderBy(chapter => chapter.Order)
                .Select(ChapterPayload),
        };

        var typeDefinitions = await entityTypes.ListAsync(projectId, includeStructural: false, cancellationToken);
        var inventory = typeDefinitions.Select(type =>
        {
            var categoryNodes = nodes
                .Where(node => string.Equals(node.NodeType, type.Type, StringComparison.OrdinalIgnoreCase))
                .OrderBy(node => node.Label ?? node.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var sourceDerived = categoryNodes.Count(node => IngestSourceAssertions.IsIngestCreatedGraphObject(node.Properties));
            return new
            {
                type = type.Type,
                total = categoryNodes.Count,
                projectOwned = categoryNodes.Count - sourceDerived,
                sourceDerived,
                sample = categoryNodes.Take(8).Select(node => new { id = StableId(node.Key), name = node.Label ?? node.Key }),
            };
        });

        var allSources = await operation.Db.IngestSources
            .AsNoTracking()
            .Where(source => source.ProjectId == projectId)
            .OrderBy(source => source.Title)
            .Select(source => new { source.Id, source.Title, source.SourceKind })
            .ToListAsync(cancellationToken);
        var selected = await bookBriefs.ListCanonSourcesAsync(projectId, cancellationToken);
        var selectedIds = selected.Select(source => source.SourceId).ToHashSet();
        var sourceInventory = new
        {
            total = allSources.Count,
            canonical = selected.Take(20),
            additional = allSources.Where(source => !selectedIds.Contains(source.Id)).Take(10),
            continuation = "Use list_ingested_sources to continue discovery, read_project_source for full text, and search_project for focused evidence. Selected canonical sources are grounding; unselected sources are evidence and must not be treated as canon unless the user says so.",
        };

        return
        [
            new("outline-snapshot", "Current Outline and Chapter Entity Attachments", JsonSerializer.Serialize(outline)),
            new("entity-inventory", "Project Entity Inventory", JsonSerializer.Serialize(inventory)),
            new("ingested-source-inventory", "Ingested Source Inventory", JsonSerializer.Serialize(sourceInventory)),
        ];
    }

    private static string StableId(string value) => Guid.TryParse(value, out var id) ? id.ToString() : value;

    private static string Property(IReadOnlyDictionary<string, object?> properties, string key) =>
        properties.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty;
}
