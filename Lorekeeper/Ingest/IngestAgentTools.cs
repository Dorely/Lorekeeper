using System.ComponentModel;
using System.Text.Json;
using Lorekeeper.Knowledge;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence.Repositories;
using Microsoft.Extensions.AI;

namespace Lorekeeper.Ingest;

public sealed class IngestAgentTools(
    IIngestRepository ingest,
    IEntityService entities,
    IGraphStore graph,
    IGraphNodeRepository nodes,
    IGraphEdgeRepository edges)
{
    public IList<AITool> Build(IngestAgentContext context) =>
    [
        AIFunctionFactory.Create(
            method: () => ListJobEntitiesAsync(context),
            name: "list_job_entities",
            description: "List entities created by this ingest job only. Use this before creating entities so recurring people, places, concepts, and objects are updated rather than duplicated."),

        AIFunctionFactory.Create(
            method: (string entityId) => GetJobEntityAsync(context, entityId),
            name: "get_job_entity",
            description: "Get details for one entity created by this ingest job. Pass the entity id from list_job_entities."),

        AIFunctionFactory.Create(
            method: (string type, string name, string? propertiesJson, string? aliasesJson, string? evidence, string? notes) =>
                CreateEntityAsync(context, type, name, propertiesJson, aliasesJson, evidence, notes),
            name: "create_ingest_entity",
            description: "Create a new graph entity from this source chunk. Only use when no same-job entity matches. propertiesJson is a JSON object, aliasesJson is a JSON array of strings."),

        AIFunctionFactory.Create(
            method: (string entityId, string? name, string? propertiesToSetJson, string? aliasesJson, string? evidence, string? notes) =>
                UpdateEntityAsync(context, entityId, name, propertiesToSetJson, aliasesJson, evidence, notes),
            name: "update_ingest_entity",
            description: "Update an existing entity created by this ingest job with new details from the current source chunk. Prefer this over creating duplicates when an entity reappears."),

        AIFunctionFactory.Create(
            method: (string fromEntityId, string toEntityId, string edgeType, string? propertiesJson, string? evidence, string? notes) =>
                LinkEntitiesAsync(context, fromEntityId, toEntityId, edgeType, propertiesJson, evidence, notes),
            name: "link_ingest_entities",
            description: "Create or refresh a typed relationship between two entities created by this ingest job. Endpoints must both come from list_job_entities."),

        AIFunctionFactory.Create(
            method: (string summary, string? notes) => RecordSourceChunkNotesAsync(context, summary, notes),
            name: "record_source_chunk_notes",
            description: "Record a concise summary and optional extraction notes for the current source chunk."),
    ];

    private async Task<string> ListJobEntitiesAsync(IngestAgentContext context)
    {
        var items = (await ingest.ListReportItemsAsync(context.JobId))
            .Where(item => item.Kind == IngestReportItemKind.Entity && item.Status == IngestReportItemStatus.Active)
            .OrderBy(item => item.ResourceType)
            .ThenBy(item => item.Title)
            .Select(item => new
            {
                id = item.EntityId,
                type = item.ResourceType,
                name = item.Title,
                summary = item.Summary,
                notes = item.Notes,
                evidence = Truncate(item.Evidence, 600),
                payload = SafeDeserialize(item.PayloadJson),
            });

        return JsonSerializer.Serialize(items);
    }

    private async Task<string> GetJobEntityAsync(IngestAgentContext context, string entityId)
    {
        if (!Guid.TryParse(entityId, out var parsed)) return $"Error: entityId '{entityId}' is not a valid Guid.";
        var item = await FindActiveEntityReportItemAsync(context.JobId, parsed);
        if (item is null) return $"Error: entity {parsed} was not created by this ingest job.";

        var node = await nodes.FindByKeyAsync(context.ProjectId, parsed.ToString("N"));
        return JsonSerializer.Serialize(new
        {
            id = parsed,
            type = item.ResourceType,
            name = item.Title,
            summary = item.Summary,
            notes = item.Notes,
            evidence = item.Evidence,
            properties = node?.Properties ?? [],
            payload = SafeDeserialize(item.PayloadJson),
        });
    }

    private async Task<string> CreateEntityAsync(
        IngestAgentContext context,
        [Description("Entity type such as Character, Location, Organization, Concept, Claim, Event, Term, Object, or another meaningful type.")] string type,
        [Description("Display name for the entity.")] string name,
        string? propertiesJson,
        string? aliasesJson,
        string? evidence,
        string? notes)
    {
        if (string.IsNullOrWhiteSpace(type)) return "Error: type is required.";
        if (string.IsNullOrWhiteSpace(name)) return "Error: name is required.";

        if (!TryParsePropertiesJson(propertiesJson, out var parsedProperties, out var propertiesError))
            return $"Error: propertiesJson is not a valid JSON object: {propertiesError}";
        var properties = parsedProperties ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (!TryParseStringArrayJson(aliasesJson, out var parsedAliases, out var aliasesError))
            return $"Error: aliasesJson is not a valid JSON array: {aliasesError}";
        var aliases = parsedAliases ?? [];

        ApplyEntityProvenance(properties, context, aliases, evidence, notes, firstSeen: true);

        var created = await entities.CreateAsync(context.ProjectId, type.Trim(), name.Trim(), properties);
        var node = await nodes.FindByKeyAsync(context.ProjectId, created.Id.ToString("N"));
        if (node is not null)
            await AddExtractedFromAsync(context, node);

        var reportItem = new IngestReportItem
        {
            JobId = context.JobId,
            SourceChunkId = context.SourceChunkId,
            Kind = IngestReportItemKind.Entity,
            Status = IngestReportItemStatus.Active,
            Title = created.Name,
            Summary = BestSummary(created.Properties),
            Notes = notes?.Trim() ?? string.Empty,
            Evidence = evidence?.Trim() ?? string.Empty,
            ResourceType = created.Type,
            EntityId = created.Id,
            GraphNodeId = node?.Id,
            PayloadJson = JsonSerializer.Serialize(new
            {
                sourceChunkIndexes = new[] { context.SourceChunkIndex },
                aliases,
                firstSeenSourceChunkIndex = context.SourceChunkIndex,
                latestSeenSourceChunkIndex = context.SourceChunkIndex,
            }),
        };
        await ingest.AddReportItemAsync(reportItem);
        await ingest.SaveChangesAsync();
        context.OnMutated();

        return JsonSerializer.Serialize(new { id = created.Id, type = created.Type, name = created.Name, properties = created.Properties });
    }

    private async Task<string> UpdateEntityAsync(
        IngestAgentContext context,
        string entityId,
        string? name,
        string? propertiesToSetJson,
        string? aliasesJson,
        string? evidence,
        string? notes)
    {
        if (!Guid.TryParse(entityId, out var parsed)) return $"Error: entityId '{entityId}' is not a valid Guid.";
        var item = await FindActiveEntityReportItemAsync(context.JobId, parsed);
        if (item is null) return $"Error: entity {parsed} was not created by this ingest job.";

        if (!TryParsePropertiesJson(propertiesToSetJson, out var parsedPropertiesToSet, out var propertiesToSetError))
            return $"Error: propertiesToSetJson is not a valid JSON object: {propertiesToSetError}";
        var propertiesToSet = parsedPropertiesToSet ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (!TryParseStringArrayJson(aliasesJson, out var parsedAliases, out var aliasesError))
            return $"Error: aliasesJson is not a valid JSON array: {aliasesError}";
        var aliases = parsedAliases ?? [];

        var node = await nodes.FindByKeyAsync(context.ProjectId, parsed.ToString("N"));
        if (node is null) return $"Error: graph node for entity {parsed} was not found.";

        var currentProperties = ToStringProperties(node.Properties);
        ApplyEntityProvenance(propertiesToSet, context, aliases, evidence, notes, firstSeen: false, currentProperties);

        var updated = await entities.UpdateAsync(
            context.ProjectId,
            parsed,
            string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            propertiesToSet.Count == 0 ? null : propertiesToSet);

        var updatedNode = await nodes.FindByKeyAsync(context.ProjectId, parsed.ToString("N"));
        if (updatedNode is not null)
            await AddExtractedFromAsync(context, updatedNode);

        item.Title = updated.Name;
        item.Summary = BestSummary(updated.Properties);
        item.Notes = AppendBlock(item.Notes, context.SourceChunkIndex, notes);
        item.Evidence = AppendBlock(item.Evidence, context.SourceChunkIndex, evidence);
        item.PayloadJson = MergeReportPayload(item.PayloadJson, context.SourceChunkIndex, aliases);
        item.UpdatedAt = DateTime.UtcNow;
        ingest.UpdateReportItem(item);
        await ingest.SaveChangesAsync();
        context.OnMutated();

        return JsonSerializer.Serialize(new { id = updated.Id, type = updated.Type, name = updated.Name, properties = updated.Properties });
    }

    private async Task<string> LinkEntitiesAsync(
        IngestAgentContext context,
        string fromEntityId,
        string toEntityId,
        string edgeType,
        string? propertiesJson,
        string? evidence,
        string? notes)
    {
        if (!Guid.TryParse(fromEntityId, out var from)) return $"Error: fromEntityId '{fromEntityId}' is not a valid Guid.";
        if (!Guid.TryParse(toEntityId, out var to)) return $"Error: toEntityId '{toEntityId}' is not a valid Guid.";
        if (string.IsNullOrWhiteSpace(edgeType)) return "Error: edgeType is required.";

        var fromItem = await FindActiveEntityReportItemAsync(context.JobId, from);
        var toItem = await FindActiveEntityReportItemAsync(context.JobId, to);
        if (fromItem is null || toItem is null)
            return "Error: both relationship endpoints must be active entities created by this ingest job.";

        if (!TryParsePropertiesJson(propertiesJson, out var parsedProperties, out var propertiesError))
            return $"Error: propertiesJson is not a valid JSON object: {propertiesError}";
        var properties = parsedProperties ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        properties["ingestJobId"] = context.JobId.ToString("N");
        properties["sourceChunkIndexes"] = JsonSerializer.Serialize(new[] { context.SourceChunkIndex });
        properties["latestSeenSourceChunkIndex"] = context.SourceChunkIndex.ToString();
        if (!string.IsNullOrWhiteSpace(evidence)) properties["evidence"] = evidence.Trim();
        if (!string.IsNullOrWhiteSpace(notes)) properties["notes"] = notes.Trim();

        await entities.LinkAsync(context.ProjectId, from, to, edgeType.Trim(), properties);

        var fromNode = await nodes.FindByKeyAsync(context.ProjectId, from.ToString("N"));
        var toNode = await nodes.FindByKeyAsync(context.ProjectId, to.ToString("N"));
        var edge = fromNode is null || toNode is null
            ? null
            : await edges.FindAsync(fromNode.Id, toNode.Id, edgeType.Trim());

        await ingest.AddReportItemAsync(new IngestReportItem
        {
            JobId = context.JobId,
            SourceChunkId = context.SourceChunkId,
            Kind = IngestReportItemKind.Relationship,
            Status = IngestReportItemStatus.Active,
            Title = $"{fromItem.Title} -[{edgeType.Trim()}]-> {toItem.Title}",
            Summary = notes?.Trim() ?? string.Empty,
            Notes = notes?.Trim() ?? string.Empty,
            Evidence = evidence?.Trim() ?? string.Empty,
            ResourceType = edgeType.Trim(),
            GraphEdgeId = edge?.Id,
            PayloadJson = JsonSerializer.Serialize(new { fromEntityId = from, toEntityId = to, sourceChunkIndexes = new[] { context.SourceChunkIndex } }),
        });
        await ingest.SaveChangesAsync();
        context.OnMutated();

        return JsonSerializer.Serialize(new { fromEntityId = from, toEntityId = to, edgeType = edgeType.Trim(), graphEdgeId = edge?.Id });
    }

    private async Task<string> RecordSourceChunkNotesAsync(IngestAgentContext context, string summary, string? notes)
    {
        var sourceChunk = await ingest.GetSourceChunkAsync(context.SourceChunkId);
        if (sourceChunk is null) return $"Error: source chunk {context.SourceChunkId} not found.";

        sourceChunk.Summary = summary?.Trim() ?? string.Empty;
        sourceChunk.AgentNotes = notes?.Trim() ?? string.Empty;
        sourceChunk.UpdatedAt = DateTime.UtcNow;
        ingest.UpdateSourceChunk(sourceChunk);

        await ingest.AddReportItemAsync(new IngestReportItem
        {
            JobId = context.JobId,
            SourceChunkId = context.SourceChunkId,
            Kind = IngestReportItemKind.SourceChunkNote,
            Status = IngestReportItemStatus.Active,
            Title = context.SourceChunkTitle,
            Summary = sourceChunk.Summary,
            Notes = sourceChunk.AgentNotes,
            ResourceType = "SourceChunk",
            PayloadJson = JsonSerializer.Serialize(new { sourceChunkId = context.SourceChunkId, sourceChunkIndex = context.SourceChunkIndex }),
        });

        await ingest.SaveChangesAsync();
        context.OnMutated();
        return JsonSerializer.Serialize(new { sourceChunkId = context.SourceChunkId, summary = sourceChunk.Summary, notes = sourceChunk.AgentNotes });
    }

    private async Task<IngestReportItem?> FindActiveEntityReportItemAsync(Guid jobId, Guid entityId) =>
        (await ingest.ListReportItemsAsync(jobId)).FirstOrDefault(item =>
            item.Kind == IngestReportItemKind.Entity
            && item.Status == IngestReportItemStatus.Active
            && item.EntityId == entityId);

    private async Task AddExtractedFromAsync(IngestAgentContext context, GraphNode entityNode)
    {
        var sourceChunkNode = await nodes.FindAsync(context.ProjectId, IngestGraphSync.SourceChunkNodeType, context.SourceChunkId.ToString("N"));
        if (sourceChunkNode is null) return;

        await graph.UpsertEdgeAsync(
            entityNode.Id,
            sourceChunkNode.Id,
            IngestGraphSync.ExtractedFromEdgeType,
            new Dictionary<string, object?>
            {
                ["ingestJobId"] = context.JobId.ToString("N"),
                ["sourceChunkIndex"] = context.SourceChunkIndex,
            });
    }

    private static void ApplyEntityProvenance(
        IDictionary<string, string?> properties,
        IngestAgentContext context,
        IReadOnlyList<string> aliases,
        string? evidence,
        string? notes,
        bool firstSeen,
        IReadOnlyDictionary<string, string?>? currentProperties = null)
    {
        properties["ingestJobId"] = context.JobId.ToString("N");
        properties["latestSeenSourceChunkIndex"] = context.SourceChunkIndex.ToString();
        properties["sourceChunkIndexes"] = MergeJsonStringArray(Read(currentProperties, "sourceChunkIndexes"), context.SourceChunkIndex.ToString());
        properties["aliases"] = MergeJsonStringArray(Read(currentProperties, "aliases"), aliases);
        properties["evidence"] = AppendBlock(Read(currentProperties, "evidence"), context.SourceChunkIndex, evidence);
        properties["extractionNotes"] = AppendBlock(Read(currentProperties, "extractionNotes"), context.SourceChunkIndex, notes);
        if (firstSeen || string.IsNullOrWhiteSpace(Read(currentProperties, "firstSeenSourceChunkIndex")))
            properties["firstSeenSourceChunkIndex"] = context.SourceChunkIndex.ToString();
    }

    private static string MergeReportPayload(string json, int sourceChunkIndex, IReadOnlyList<string> aliases)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (LooksLikeJsonRoot(json, '{'))
        {
            try
            {
                var existing = JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? [];
                foreach (var kv in existing) payload[kv.Key] = kv.Value;
            }
            catch (JsonException) { }
        }

        payload["latestSeenSourceChunkIndex"] = sourceChunkIndex;
        payload["sourceChunkIndexes"] = MergeJsonStringArray(payload.TryGetValue("sourceChunkIndexes", out var existingIndexes) ? existingIndexes?.ToString() : null, sourceChunkIndex.ToString());
        payload["aliases"] = MergeJsonStringArray(payload.TryGetValue("aliases", out var existingAliases) ? existingAliases?.ToString() : null, aliases);
        return JsonSerializer.Serialize(payload);
    }

    private static string BestSummary(IReadOnlyDictionary<string, string?> properties)
    {
        foreach (var key in new[] { "description", "summary", "role", "value", "extractionNotes" })
        {
            if (properties.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return Truncate(value, 240);
        }
        return string.Empty;
    }

    private static string AppendBlock(string? existing, int sourceChunkIndex, string? next)
    {
        if (string.IsNullOrWhiteSpace(next)) return existing ?? string.Empty;
        var block = $"[Source chunk {sourceChunkIndex}] {next.Trim()}";
        return string.IsNullOrWhiteSpace(existing) ? block : existing.TrimEnd() + "\n" + block;
    }

    private static string MergeJsonStringArray(string? existingJson, params string[] additions) =>
        MergeJsonStringArray(existingJson, (IReadOnlyList<string>)additions);

    private static string MergeJsonStringArray(string? existingJson, IReadOnlyList<string> additions)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(existingJson))
        {
            if (LooksLikeJsonRoot(existingJson, '['))
            {
                try
                {
                    foreach (var value in JsonSerializer.Deserialize<List<string>>(existingJson) ?? [])
                        if (!string.IsNullOrWhiteSpace(value)) values.Add(value.Trim());
                }
                catch (JsonException)
                {
                    foreach (var value in existingJson.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        values.Add(value);
                }
            }
            else
            {
                foreach (var value in existingJson.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    values.Add(value);
            }
        }

        foreach (var addition in additions)
            if (!string.IsNullOrWhiteSpace(addition)) values.Add(addition.Trim());

        return JsonSerializer.Serialize(values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
    }

    private static bool TryParsePropertiesJson(string? json, out Dictionary<string, string?>? properties, out string? error)
    {
        properties = null;
        error = null;
        if (string.IsNullOrWhiteSpace(json)) return true;

        if (!LooksLikeJsonRoot(json, '{'))
        {
            error = "expected a JSON object at the root.";
            return false;
        }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "expected a JSON object at the root.";
                return false;
            }

            var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.String => prop.Value.GetString(),
                    _ => prop.Value.GetRawText(),
                };
            }
            properties = dict;
            return true;
        }
    }

    private static bool TryParseStringArrayJson(string? json, out string[]? values, out string? error)
    {
        values = null;
        error = null;
        if (string.IsNullOrWhiteSpace(json)) return true;

        if (!LooksLikeJsonRoot(json, '['))
        {
            error = "expected a JSON array at the root.";
            return false;
        }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                error = "expected a JSON array at the root.";
                return false;
            }
            values = doc.RootElement.EnumerateArray()
                .Select(element => element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.GetRawText())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
            return true;
        }
    }

    private static bool LooksLikeJsonRoot(string json, char rootChar)
    {
        foreach (var ch in json)
        {
            if (char.IsWhiteSpace(ch)) continue;
            return ch == rootChar;
        }
        return false;
    }

    private static IReadOnlyDictionary<string, string?> ToStringProperties(IDictionary<string, object?> properties)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in properties)
            result[kv.Key] = kv.Value?.ToString();
        return result;
    }

    private static string? Read(IReadOnlyDictionary<string, string?>? properties, string key) =>
        properties is not null && properties.TryGetValue(key, out var value) ? value : null;

    private static object? SafeDeserialize(string json)
    {
        if (!LooksLikeJsonRoot(json, '{') && !LooksLikeJsonRoot(json, '[')) return json;
        try { return JsonSerializer.Deserialize<object>(json); }
        catch (JsonException) { return json; }
    }

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max] + "...";
    }
}