using System.Text.Json;
using System.Text.Json.Nodes;
using Lorekeeper.EntityVisuals;
using Lorekeeper.Outline;

namespace Lorekeeper.Context;

internal static class ContextEntityPayloadFormatter
{
    public static string Serialize(
        StoryEntity entity,
        IReadOnlyList<EntityVisualExampleView> visualExamples)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(visualExamples);

        var detail = new JsonObject();
        var data = DataPayload(entity, visualExamples);
        if (data.Count > 0)
            detail["data"] = data;

        var identity = new JsonObject
        {
            ["id"] = entity.Id,
            ["type"] = entity.Type,
            ["name"] = entity.Name,
            ["contextFeed"] = true,
            ["origin"] = entity.IsIngestCreated ? "source-derived" : "project-owned",
            ["isIngestCreated"] = entity.IsIngestCreated,
        };
        if (entity.Order is { } order)
            identity["order"] = order;
        if (entity.ParentId is { } parentId)
            identity["parentId"] = parentId;

        return AgentPayloadPaginator.SerializeCompactObjectPage(
            identity,
            detail,
            "read_entity",
            new JsonObject { ["entityId"] = entity.Id },
            pageNumber: 1);
    }

    private static JsonObject DataPayload(
        StoryEntity entity,
        IReadOnlyList<EntityVisualExampleView> visualExamples)
    {
        var data = new JsonObject();
        var properties = entity.Properties
            .Where(property => !string.IsNullOrWhiteSpace(property.Key)
                && !string.IsNullOrWhiteSpace(property.Value))
            .OrderBy(property => property.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(property => property.Key, property => property.Value, StringComparer.OrdinalIgnoreCase);
        if (properties.Count > 0)
            data["properties"] = JsonSerializer.SerializeToNode(properties, ContextPayloadJson.Options);

        if (!string.IsNullOrWhiteSpace(entity.Summary))
            data["summary"] = entity.Summary.Trim();

        var aliases = entity.Aliases
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Select(alias => alias.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (aliases.Count > 0)
            data["aliases"] = JsonSerializer.SerializeToNode(aliases, ContextPayloadJson.Options);

        var wikiSections = entity.WikiSections
            .Where(section => !string.IsNullOrWhiteSpace(section.Title)
                || !string.IsNullOrWhiteSpace(section.Body))
            .Select(section => new
            {
                section.Id,
                section.Title,
                section.Body,
                citations = section.Citations.Count > 0 ? section.Citations : null,
            })
            .ToList();
        if (wikiSections.Count > 0)
            data["wikiSections"] = JsonSerializer.SerializeToNode(wikiSections, ContextPayloadJson.Options);

        var sourceEvidence = entity.SourceEvidence
            .Where(source => !string.IsNullOrWhiteSpace(source.SourceTitle)
                || !string.IsNullOrWhiteSpace(source.Markdown))
            .Select(source => new
            {
                source.PropertyKey,
                source.Slug,
                source.SourceId,
                source.SourceTitle,
                source.SourceKind,
                source.Markdown,
            })
            .ToList();
        if (sourceEvidence.Count > 0)
            data["sourceEvidence"] = JsonSerializer.SerializeToNode(sourceEvidence, ContextPayloadJson.Options);

        var visualReferences = visualExamples
            .OrderBy(example => example.SortOrder)
            .Select(example => new
            {
                example.Id,
                example.Label,
                example.SortOrder,
                example.Origin,
                image = new
                {
                    example.Image.Id,
                    example.Image.FileName,
                    example.Image.AltText,
                },
            })
            .ToList();
        if (visualReferences.Count > 0)
            data["visualReferences"] = JsonSerializer.SerializeToNode(visualReferences, ContextPayloadJson.Options);

        return data;
    }
}
