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
    IGraphEdgeRepository edges,
    IEntityTypeService entityTypes)
{
    public IList<AITool> Build(IngestAgentContext context) =>
    [
        AIFunctionFactory.Create(
            method: () => ListJobEntitiesAsync(context),
            name: "list_job_entities",
            description: "List entities already touched by this ingest job, including existing project entities linked by source observations."),

        AIFunctionFactory.Create(
            method: (string entityId) => GetJobEntityAsync(context, entityId),
            name: "get_job_entity",
            description: "Get details for one entity already touched by this ingest job. Pass the entity id from list_job_entities."),

        AIFunctionFactory.Create(
            method: (string? type, string? query) => SearchProjectEntitiesAsync(context, type, query),
            name: "search_project_entities",
            description: "Search existing non-structural story entities in the project graph before creating a new entity. Use type when known; search exact names plus variants such as titles removed, aliases, alternate spellings, surnames, epithets, and descriptive terms."),

        AIFunctionFactory.Create(
            method: (string existingEntityId, string? propertiesJson, string? aliasesJson, string? evidence, string? notes) =>
                RecordExistingEntityObservationAsync(context, existingEntityId, propertiesJson, aliasesJson, evidence, notes),
            name: "record_existing_entity_observation",
            description: "Record source-scoped observations on an existing project entity without changing its canonical properties. Use this after search_project_entities finds a match."),

        AIFunctionFactory.Create(
            method: (string type, string name, string? propertiesJson, string? aliasesJson, string? evidence, string? notes) =>
                CreateEntityAsync(context, type, name, propertiesJson, aliasesJson, evidence, notes),
            name: "create_ingest_entity",
            description: "Create a new graph entity with source-scoped observations. Only use after list_job_entities and variant search_project_entities calls find no plausible same subject. propertiesJson is a useful JSON object of readable observations, aliasesJson is a JSON array of strings."),

        AIFunctionFactory.Create(
            method: (string entityId, string? name, string? propertiesToSetJson, string? aliasesJson, string? evidence, string? notes) =>
                UpdateEntityAsync(context, entityId, name, propertiesToSetJson, aliasesJson, evidence, notes),
            name: "update_ingest_entity",
            description: "Update source-scoped observations for an entity already touched by this ingest job. Canonical project properties are not changed; name is only used for entities newly created by this job."),

        AIFunctionFactory.Create(
            method: (string fromEntityId, string toEntityId, string edgeType, string? propertiesJson, string? evidence, string? notes) =>
                LinkEntitiesAsync(context, fromEntityId, toEntityId, edgeType, propertiesJson, evidence, notes),
            name: "link_ingest_entities",
            description: "Record a source-scoped relationship between two entities already touched by this ingest job. Record observations on existing project endpoints before linking them."),

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
                graphAction = IngestSourceAssertions.ReadEntityGraphAction(item.PayloadJson),
                payload = SafeDeserialize(item.PayloadJson),
            });

        return JsonSerializer.Serialize(items);
    }

    private async Task<string> GetJobEntityAsync(IngestAgentContext context, string entityId)
    {
        if (!Guid.TryParse(entityId, out var parsed)) return $"Error: entityId '{entityId}' is not a valid Guid.";
        var item = await FindActiveEntityReportItemAsync(context.JobId, parsed);
        if (item is null) return $"Error: entity {parsed} has not been touched by this ingest job.";

        var node = await nodes.FindByKeyAsync(context.ProjectId, parsed.ToString("N"));
        return JsonSerializer.Serialize(new
        {
            id = parsed,
            type = item.ResourceType,
            name = node?.Label ?? item.Title,
            summary = item.Summary,
            notes = item.Notes,
            evidence = item.Evidence,
            graphAction = IngestSourceAssertions.ReadEntityGraphAction(item.PayloadJson),
            canonicalProperties = node is null ? new Dictionary<string, string?>() : VisibleProperties(node.Properties),
            sourceAssertions = node is null ? Array.Empty<IngestSourceAssertionSummary>() : IngestSourceAssertions.SummarizeEntityAssertions(node.Properties),
            payload = SafeDeserialize(item.PayloadJson),
        });
    }

    private async Task<string> SearchProjectEntitiesAsync(IngestAgentContext context, string? type, string? query)
    {
        var allowedTypes = await GetAllowedEntityTypesAsync(context.ProjectId);
        var requestedType = type?.Trim();
        var searchTypes = allowedTypes;
        if (!string.IsNullOrWhiteSpace(requestedType))
        {
            if (IsDisallowedEntityType(requestedType))
                return $"Error: entity type '{requestedType}' is structural and cannot be searched by ingest.";

            var matchedType = await ResolveEntityTypeAsync(context.ProjectId, requestedType, allowNew: false);
            searchTypes = matchedType is null
                ? []
                : (await GetEquivalentEntityTypeNamesAsync(context.ProjectId, matchedType.Type)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var queryText = query?.Trim() ?? string.Empty;
        var terms = SplitTerms(queryText).ToList();
        var results = new List<ProjectEntityCandidate>();
        foreach (var searchType in searchTypes.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            var typedNodes = await nodes.ListByTypeAsync(context.ProjectId, searchType);
            foreach (var node in typedNodes)
            {
                if (!Guid.TryParseExact(node.Key, "N", out var id)) continue;
                var visibleProperties = VisibleProperties(node.Properties);
                var assertionSummaries = IngestSourceAssertions.SummarizeEntityAssertions(node.Properties);
                var score = ScoreCandidate(node, visibleProperties, assertionSummaries, terms, queryText);
                if (terms.Count > 0 && score == 0) continue;

                results.Add(new ProjectEntityCandidate(
                    id,
                    node.NodeType,
                    node.Label ?? node.Key,
                    BestSummary(visibleProperties),
                    visibleProperties.Take(8).ToDictionary(kv => kv.Key, kv => kv.Value),
                    assertionSummaries,
                    score));
            }
        }

        return JsonSerializer.Serialize(results
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(25));
    }

    private async Task<string> RecordExistingEntityObservationAsync(
        IngestAgentContext context,
        string existingEntityId,
        string? propertiesJson,
        string? aliasesJson,
        string? evidence,
        string? notes)
    {
        if (!Guid.TryParse(existingEntityId, out var parsed)) return $"Error: existingEntityId '{existingEntityId}' is not a valid Guid.";

        if (!TryParsePropertiesJson(propertiesJson, out var parsedProperties, out var propertiesError))
            return $"Error: propertiesJson is not a valid JSON object: {propertiesError}";
        var observedProperties = parsedProperties ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (!TryParseStringArrayJson(aliasesJson, out var parsedAliases, out var aliasesError))
            return $"Error: aliasesJson is not a valid JSON array: {aliasesError}";
        var aliases = parsedAliases ?? [];

        var node = await ResolveAllowedEntityNodeAsync(context.ProjectId, parsed);
        if (node is null) return $"Error: entity {parsed} is not a non-structural project story entity.";

        var item = await FindActiveEntityReportItemAsync(context.JobId, parsed);
        await RecordEntityObservationAsync(
            context,
            node,
            parsed,
            item,
            IngestSourceAssertions.LinkedExistingEntityAction,
            observedProperties,
            aliases,
            evidence,
            notes);

        return JsonSerializer.Serialize(new
        {
            id = parsed,
            type = node.NodeType,
            name = node.Label ?? node.Key,
            graphAction = IngestSourceAssertions.LinkedExistingEntityAction,
            canonicalProperties = VisibleProperties(node.Properties),
            sourceAssertions = IngestSourceAssertions.SummarizeEntityAssertions(node.Properties),
        });
    }

    private async Task<string> CreateEntityAsync(
        IngestAgentContext context,
        [Description("Entity type such as Character, Location, Organization, Concept, Claim, Term, Object, or another meaningful non-structural type.")] string type,
        [Description("Display name for the entity.")] string name,
        string? propertiesJson,
        string? aliasesJson,
        string? evidence,
        string? notes)
    {
        if (string.IsNullOrWhiteSpace(type)) return "Error: type is required.";
        var resolvedType = await ResolveEntityTypeAsync(context.ProjectId, type, allowNew: true);
        if (resolvedType is null) return $"Error: type '{type.Trim()}' is structural or invalid and cannot be created by ingest.";
        if (string.IsNullOrWhiteSpace(name)) return "Error: name is required.";

        if (!TryParsePropertiesJson(propertiesJson, out var parsedProperties, out var propertiesError))
            return $"Error: propertiesJson is not a valid JSON object: {propertiesError}";
        var observedProperties = parsedProperties ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (!TryParseStringArrayJson(aliasesJson, out var parsedAliases, out var aliasesError))
            return $"Error: aliasesJson is not a valid JSON array: {aliasesError}";
        var aliases = parsedAliases ?? [];

        var trimmedName = name.Trim();
        var duplicateNode = await FindDuplicateEntityByNameAsync(context.ProjectId, resolvedType.Type, trimmedName);
        if (duplicateNode is not null && Guid.TryParseExact(duplicateNode.Key, "N", out var duplicateId))
        {
            var duplicateItem = await FindActiveEntityReportItemAsync(context.JobId, duplicateId);
            var action = duplicateItem is null
                ? IngestSourceAssertions.LinkedExistingEntityAction
                : IngestSourceAssertions.ReadEntityGraphAction(duplicateItem.PayloadJson) ?? IngestSourceAssertions.LinkedExistingEntityAction;
            await RecordEntityObservationAsync(
                context,
                duplicateNode,
                duplicateId,
                duplicateItem,
                action,
                observedProperties,
                aliases,
                evidence,
                notes);

            return JsonSerializer.Serialize(new
            {
                id = duplicateId,
                type = duplicateNode.NodeType,
                name = duplicateNode.Label ?? duplicateNode.Key,
                graphAction = action,
                duplicateNameReused = true,
                message = $"Reused existing {duplicateNode.NodeType} named '{duplicateNode.Label ?? duplicateNode.Key}' instead of creating a duplicate.",
                canonicalProperties = VisibleProperties(duplicateNode.Properties),
                sourceAssertions = IngestSourceAssertions.SummarizeEntityAssertions(duplicateNode.Properties),
            });
        }

        var entityProperties = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        entityProperties[IngestSourceAssertions.GraphOriginProperty] = IngestSourceAssertions.GraphOriginIngestValue;
        var assertionInput = BuildAssertionInput(context, BestSummary(observedProperties), observedProperties, aliases, evidence, notes);
        var write = IngestSourceAssertions.UpsertEntityAssertion(entityProperties, assertionInput);

        var created = await entities.CreateAsync(context.ProjectId, resolvedType.Type, trimmedName, entityProperties);
        var node = await nodes.FindByKeyAsync(context.ProjectId, created.Id.ToString("N"));
        if (node is not null)
            await AddExtractedFromAsync(context, node);

        await UpsertEntityReportItemAsync(
            context,
            existing: null,
            node,
            created.Id,
            IngestSourceAssertions.CreatedEntityAction,
            write,
            observedProperties,
            aliases,
            evidence,
            notes);
        context.OnMutated();

        return JsonSerializer.Serialize(new
        {
            id = created.Id,
            type = created.Type,
            name = created.Name,
            graphAction = IngestSourceAssertions.CreatedEntityAction,
            observedProperties,
            sourceAssertions = node is null ? Array.Empty<IngestSourceAssertionSummary>() : IngestSourceAssertions.SummarizeEntityAssertions(node.Properties),
        });
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

        if (!TryParsePropertiesJson(propertiesToSetJson, out var parsedPropertiesToSet, out var propertiesToSetError))
            return $"Error: propertiesToSetJson is not a valid JSON object: {propertiesToSetError}";
        var observedProperties = parsedPropertiesToSet ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (!TryParseStringArrayJson(aliasesJson, out var parsedAliases, out var aliasesError))
            return $"Error: aliasesJson is not a valid JSON array: {aliasesError}";
        var aliases = parsedAliases ?? [];

        var node = await ResolveAllowedEntityNodeAsync(context.ProjectId, parsed);
        if (node is null) return $"Error: entity {parsed} is not a non-structural project story entity.";

        var item = await FindActiveEntityReportItemAsync(context.JobId, parsed);
        var action = item is null
            ? IngestSourceAssertions.LinkedExistingEntityAction
            : IngestSourceAssertions.ReadEntityGraphAction(item.PayloadJson) ?? IngestSourceAssertions.LinkedExistingEntityAction;

        if (string.Equals(action, IngestSourceAssertions.CreatedEntityAction, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(name))
        {
            var duplicate = await FindDuplicateEntityByNameAsync(context.ProjectId, node.NodeType, name.Trim(), excludeNodeId: node.Id);
            if (duplicate is not null)
                return $"Error: another {node.NodeType} named '{duplicate.Label ?? duplicate.Key}' already exists. Use that entity instead of renaming this one.";

            node.Label = name.Trim();
        }

        await RecordEntityObservationAsync(
            context,
            node,
            parsed,
            item,
            action,
            observedProperties,
            aliases,
            evidence,
            notes);

        return JsonSerializer.Serialize(new
        {
            id = parsed,
            type = node.NodeType,
            name = node.Label ?? node.Key,
            graphAction = action,
            canonicalProperties = VisibleProperties(node.Properties),
            sourceAssertions = IngestSourceAssertions.SummarizeEntityAssertions(node.Properties),
        });
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
        var trimmedEdgeType = edgeType.Trim();
        if (string.IsNullOrWhiteSpace(trimmedEdgeType)) return "Error: edgeType is required.";
        if (string.Equals(trimmedEdgeType, EntityService.HasChildEdgeType, StringComparison.OrdinalIgnoreCase))
            return "Error: HasChild is a managed structural relationship and cannot be created by ingest.";

        var fromItem = await FindActiveEntityReportItemAsync(context.JobId, from);
        var toItem = await FindActiveEntityReportItemAsync(context.JobId, to);
        if (fromItem is null || toItem is null)
            return "Error: both relationship endpoints must be entities already touched by this ingest job. Use record_existing_entity_observation for existing project entities first.";

        if (!TryParsePropertiesJson(propertiesJson, out var parsedProperties, out var propertiesError))
            return $"Error: propertiesJson is not a valid JSON object: {propertiesError}";
        var observedProperties = parsedProperties ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var fromNode = await nodes.FindByKeyAsync(context.ProjectId, from.ToString("N"));
        var toNode = await nodes.FindByKeyAsync(context.ProjectId, to.ToString("N"));
        if (fromNode is null || toNode is null) return "Error: one or both relationship endpoint graph nodes were not found.";

        var assertionInput = BuildAssertionInput(context, BestSummary(observedProperties), observedProperties, [], evidence, notes);
        var existingEdge = await edges.FindAsync(fromNode.Id, toNode.Id, trimmedEdgeType);
        var action = existingEdge is null
            ? IngestSourceAssertions.CreatedEdgeAction
            : IngestSourceAssertions.LinkedExistingEdgeAction;

        GraphEdge edge;
        IngestAssertionWriteResult write;
        if (existingEdge is null)
        {
            var edgeProperties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            edgeProperties[IngestSourceAssertions.GraphOriginProperty] = IngestSourceAssertions.GraphOriginIngestValue;
            write = IngestSourceAssertions.UpsertRelationshipAssertion(edgeProperties, assertionInput);
            edge = await graph.UpsertEdgeAsync(fromNode.Id, toNode.Id, trimmedEdgeType, edgeProperties);
        }
        else
        {
            write = IngestSourceAssertions.UpsertRelationshipAssertion(existingEdge.Properties, assertionInput);
            existingEdge.UpdatedAt = DateTime.UtcNow;
            edges.Update(existingEdge);
            await edges.SaveChangesAsync();
            edge = existingEdge;
        }

        var relationshipItem = await FindActiveRelationshipReportItemAsync(context.JobId, edge.Id);
        await UpsertRelationshipReportItemAsync(
            context,
            relationshipItem,
            edge,
            action,
            write,
            from,
            to,
            fromNode.Label ?? fromItem.Title,
            toNode.Label ?? toItem.Title,
            observedProperties,
            evidence,
            notes);
        context.OnMutated();

        return JsonSerializer.Serialize(new
        {
            fromEntityId = from,
            toEntityId = to,
            edgeType = trimmedEdgeType,
            graphEdgeId = edge.Id,
            graphAction = action,
            sourceAssertions = IngestSourceAssertions.SummarizeRelationshipAssertions(edge.Properties),
        });
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
            PayloadJson = JsonSerializer.Serialize(new
            {
                sourceId = context.SourceId.ToString("N"),
                sourceChunkId = context.SourceChunkId,
                sourceChunkIndex = context.SourceChunkIndex,
            }),
        });

        await ingest.SaveChangesAsync();
        context.OnMutated();
        return JsonSerializer.Serialize(new { sourceChunkId = context.SourceChunkId, summary = sourceChunk.Summary, notes = sourceChunk.AgentNotes });
    }

    private async Task RecordEntityObservationAsync(
        IngestAgentContext context,
        GraphNode node,
        Guid entityId,
        IngestReportItem? existing,
        string action,
        IReadOnlyDictionary<string, string?> observedProperties,
        IReadOnlyList<string> aliases,
        string? evidence,
        string? notes)
    {
        var assertionInput = BuildAssertionInput(context, BestSummary(observedProperties), observedProperties, aliases, evidence, notes);
        var write = IngestSourceAssertions.UpsertEntityAssertion(node.Properties, assertionInput);
        node.UpdatedAt = DateTime.UtcNow;
        nodes.Update(node);
        await nodes.SaveChangesAsync();
        await AddExtractedFromAsync(context, node);

        await UpsertEntityReportItemAsync(
            context,
            existing,
            node,
            entityId,
            action,
            write,
            observedProperties,
            aliases,
            evidence,
            notes);
        context.OnMutated();
    }

    private async Task UpsertEntityReportItemAsync(
        IngestAgentContext context,
        IngestReportItem? existing,
        GraphNode? node,
        Guid entityId,
        string action,
        IngestAssertionWriteResult write,
        IReadOnlyDictionary<string, string?> observedProperties,
        IReadOnlyList<string> aliases,
        string? evidence,
        string? notes)
    {
        var summary = BestSummary(observedProperties);
        var title = node?.Label ?? entityId.ToString("N");
        var resourceType = node?.NodeType ?? string.Empty;
        var payloadJson = MergeEntityReportPayload(existing?.PayloadJson, context, action, write, aliases);
        if (existing is null)
        {
            await ingest.AddReportItemAsync(new IngestReportItem
            {
                JobId = context.JobId,
                SourceChunkId = context.SourceChunkId,
                Kind = IngestReportItemKind.Entity,
                Status = IngestReportItemStatus.Active,
                Title = title,
                Summary = summary,
                Notes = notes?.Trim() ?? string.Empty,
                Evidence = evidence?.Trim() ?? string.Empty,
                ResourceType = resourceType,
                EntityId = entityId,
                GraphNodeId = node?.Id,
                PayloadJson = payloadJson,
            });
        }
        else
        {
            existing.Title = title;
            existing.ResourceType = resourceType;
            existing.GraphNodeId = node?.Id ?? existing.GraphNodeId;
            if (!string.IsNullOrWhiteSpace(summary)) existing.Summary = summary;
            existing.Notes = AppendBlock(existing.Notes, context.SourceChunkIndex, notes);
            existing.Evidence = AppendBlock(existing.Evidence, context.SourceChunkIndex, evidence);
            existing.PayloadJson = payloadJson;
            existing.UpdatedAt = DateTime.UtcNow;
            ingest.UpdateReportItem(existing);
        }

        await ingest.SaveChangesAsync();
    }

    private async Task UpsertRelationshipReportItemAsync(
        IngestAgentContext context,
        IngestReportItem? existing,
        GraphEdge edge,
        string action,
        IngestAssertionWriteResult write,
        Guid from,
        Guid to,
        string fromTitle,
        string toTitle,
        IReadOnlyDictionary<string, string?> observedProperties,
        string? evidence,
        string? notes)
    {
        var summary = BestSummary(observedProperties);
        var payloadJson = MergeRelationshipReportPayload(existing?.PayloadJson, context, action, write, from, to);
        if (existing is null)
        {
            await ingest.AddReportItemAsync(new IngestReportItem
            {
                JobId = context.JobId,
                SourceChunkId = context.SourceChunkId,
                Kind = IngestReportItemKind.Relationship,
                Status = IngestReportItemStatus.Active,
                Title = $"{fromTitle} -[{edge.EdgeType}]-> {toTitle}",
                Summary = summary,
                Notes = notes?.Trim() ?? string.Empty,
                Evidence = evidence?.Trim() ?? string.Empty,
                ResourceType = edge.EdgeType,
                GraphEdgeId = edge.Id,
                PayloadJson = payloadJson,
            });
        }
        else
        {
            existing.Title = $"{fromTitle} -[{edge.EdgeType}]-> {toTitle}";
            existing.ResourceType = edge.EdgeType;
            if (!string.IsNullOrWhiteSpace(summary)) existing.Summary = summary;
            existing.Notes = AppendBlock(existing.Notes, context.SourceChunkIndex, notes);
            existing.Evidence = AppendBlock(existing.Evidence, context.SourceChunkIndex, evidence);
            existing.GraphEdgeId = edge.Id;
            existing.PayloadJson = payloadJson;
            existing.UpdatedAt = DateTime.UtcNow;
            ingest.UpdateReportItem(existing);
        }

        await ingest.SaveChangesAsync();
    }

    private async Task<IngestReportItem?> FindActiveEntityReportItemAsync(Guid jobId, Guid entityId) =>
        (await ingest.ListReportItemsAsync(jobId)).FirstOrDefault(item =>
            item.Kind == IngestReportItemKind.Entity
            && item.Status == IngestReportItemStatus.Active
            && item.EntityId == entityId);

    private async Task<IngestReportItem?> FindActiveRelationshipReportItemAsync(Guid jobId, long graphEdgeId) =>
        (await ingest.ListReportItemsAsync(jobId)).FirstOrDefault(item =>
            item.Kind == IngestReportItemKind.Relationship
            && item.Status == IngestReportItemStatus.Active
            && item.GraphEdgeId == graphEdgeId);

    private async Task AddExtractedFromAsync(IngestAgentContext context, GraphNode entityNode)
    {
        var sourceChunkNode = await nodes.FindAsync(context.ProjectId, IngestGraphSync.SourceChunkNodeType, context.SourceChunkId.ToString("N"));
        var targetNode = sourceChunkNode
            ?? await nodes.FindAsync(context.ProjectId, IngestGraphSync.SourceNodeType, context.SourceId.ToString("N"));
        if (targetNode is null) return;

        await graph.UpsertEdgeAsync(
            entityNode.Id,
            targetNode.Id,
            IngestGraphSync.ExtractedFromEdgeType,
            new Dictionary<string, object?>
            {
                ["ingestJobId"] = context.JobId.ToString("N"),
                ["sourceId"] = context.SourceId.ToString("N"),
                ["sourceChunkId"] = context.SourceChunkId.ToString("N"),
                ["sourceChunkIndex"] = context.SourceChunkIndex,
                ["sourceGraphTargetType"] = targetNode.NodeType,
            });
    }

    private async Task<HashSet<string>> GetAllowedEntityTypesAsync(Guid projectId)
    {
        var definitions = await GetAllowedEntityTypeDefinitionsAsync(projectId);
        return definitions
            .Select(definition => definition.Type)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyList<EntityTypeDefinition>> GetAllowedEntityTypeDefinitionsAsync(Guid projectId)
    {
        var definitions = await entityTypes.ListAsync(projectId, includeStructural: false);
        return definitions
            .Where(definition => !definition.IsStructural && !definition.IsChapterScoped && !IsDisallowedEntityType(definition.Type))
            .ToList();
    }

    private async Task<EntityTypeResolution?> ResolveEntityTypeAsync(Guid projectId, string requestedType, bool allowNew)
    {
        var requested = (requestedType ?? string.Empty).Trim();
        if (requested.Length == 0 || IsDisallowedEntityType(requested)) return null;

        var requestedKey = NormalizeComparable(requested);
        var definitions = await GetAllowedEntityTypeDefinitionsAsync(projectId);
        var existing = definitions.FirstOrDefault(definition =>
            string.Equals(NormalizeComparable(definition.Type), requestedKey, StringComparison.Ordinal)
            || string.Equals(NormalizeComparable(definition.SingularLabel), requestedKey, StringComparison.Ordinal)
            || string.Equals(NormalizeComparable(definition.PluralLabel), requestedKey, StringComparison.Ordinal));

        if (existing is not null)
            return new EntityTypeResolution(existing.Type, ExistingType: true);
        if (!allowNew) return null;

        var normalized = NormalizeTypeKey(SingularizeTypeLabel(requested));
        if (normalized.Length == 0 || IsDisallowedEntityType(normalized)) return null;
        return new EntityTypeResolution(normalized, ExistingType: false);
    }

    private async Task<GraphNode?> FindDuplicateEntityByNameAsync(
        Guid projectId,
        string nodeType,
        string name,
        long? excludeNodeId = null)
    {
        var normalizedName = NormalizeEntityName(name);
        if (normalizedName.Length == 0) return null;

        var nodeTypes = await GetEquivalentEntityTypeNamesAsync(projectId, nodeType);
        foreach (var candidateType in nodeTypes)
        {
            var stripTitles = ShouldStripEntityTitles(candidateType);
            var nameVariants = BuildEntityNameVariants(name, stripTitles);
            var existing = await nodes.ListByTypeAsync(projectId, candidateType);
            var duplicate = existing.FirstOrDefault(node =>
                node.Id != excludeNodeId
                && EntityNameVariantsOverlap(nameVariants, node.Label ?? node.Key, stripTitles));
            if (duplicate is not null) return duplicate;
        }

        return null;
    }

    private async Task<IReadOnlyList<string>> GetEquivalentEntityTypeNamesAsync(Guid projectId, string canonicalType)
    {
        var definitions = await GetAllowedEntityTypeDefinitionsAsync(projectId);
        var target = definitions.FirstOrDefault(definition => string.Equals(definition.Type, canonicalType, StringComparison.OrdinalIgnoreCase));
        if (target is null) return [canonicalType];

        var targetKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            NormalizeComparable(target.Type),
            NormalizeComparable(target.SingularLabel),
            NormalizeComparable(target.PluralLabel),
        };

        return definitions
            .Where(definition => new[]
                {
                    NormalizeComparable(definition.Type),
                    NormalizeComparable(definition.SingularLabel),
                    NormalizeComparable(definition.PluralLabel),
                }
                .Any(targetKeys.Contains))
            .Select(definition => definition.Type)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<GraphNode?> ResolveAllowedEntityNodeAsync(Guid projectId, Guid entityId)
    {
        var node = await nodes.FindByKeyAsync(projectId, entityId.ToString("N"));
        if (node is null || IsDisallowedEntityType(node.NodeType)) return null;
        var allowedTypes = await GetAllowedEntityTypesAsync(projectId);
        return allowedTypes.Contains(node.NodeType) ? node : null;
    }

    private static IngestAssertionInput BuildAssertionInput(
        IngestAgentContext context,
        string? summary,
        IReadOnlyDictionary<string, string?> observedProperties,
        IReadOnlyList<string> aliases,
        string? evidence,
        string? notes,
        bool replaceExistingText = false) =>
        new(
            context.JobId,
            context.SourceId,
            context.SourceTitle,
            context.SourceKind,
            context.SourceChunkId,
            context.SourceChunkIndex,
            summary,
            observedProperties,
            aliases,
            evidence,
            notes,
            replaceExistingText);

    private static Dictionary<string, object?> ReadPayload(string? json)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json) || !LooksLikeJsonRoot(json, '{')) return payload;

        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var prop in doc.RootElement.EnumerateObject())
                payload[prop.Name] = prop.Value.Clone();
        }
        catch (JsonException) { }
        return payload;
    }

    private static string MergeEntityReportPayload(
        string? json,
        IngestAgentContext context,
        string action,
        IngestAssertionWriteResult write,
        IReadOnlyList<string> aliases)
    {
        var payload = ReadPayload(json);
        var existingAction = ReadString(payload.GetValueOrDefault(IngestSourceAssertions.EntityGraphActionProperty));
        payload[IngestSourceAssertions.EntityGraphActionProperty] = string.Equals(existingAction, IngestSourceAssertions.CreatedEntityAction, StringComparison.Ordinal)
            ? IngestSourceAssertions.CreatedEntityAction
            : action;
        ApplyCommonPayload(payload, context, write);
        AddStringArrayValues(payload, "aliases", aliases);
        return JsonSerializer.Serialize(payload);
    }

    private static string MergeRelationshipReportPayload(
        string? json,
        IngestAgentContext context,
        string action,
        IngestAssertionWriteResult write,
        Guid from,
        Guid to)
    {
        var payload = ReadPayload(json);
        var existingAction = ReadString(payload.GetValueOrDefault(IngestSourceAssertions.RelationshipGraphActionProperty));
        payload[IngestSourceAssertions.RelationshipGraphActionProperty] = string.Equals(existingAction, IngestSourceAssertions.CreatedEdgeAction, StringComparison.Ordinal)
            ? IngestSourceAssertions.CreatedEdgeAction
            : action;
        ApplyCommonPayload(payload, context, write);
        payload["fromEntityId"] = from;
        payload["toEntityId"] = to;
        return JsonSerializer.Serialize(payload);
    }

    private static void ApplyCommonPayload(
        Dictionary<string, object?> payload,
        IngestAgentContext context,
        IngestAssertionWriteResult write)
    {
        payload["sourceId"] = context.SourceId.ToString("N");
        payload["sourceTitle"] = context.SourceTitle;
        payload["sourceKind"] = context.SourceKind;
        payload["assertionSourceKey"] = write.SourceKey;
        payload["assertionChunkKey"] = write.ChunkKey;
        payload["latestSeenSourceChunkIndex"] = context.SourceChunkIndex;
        AddStringArrayValue(payload, "sourceChunkIds", context.SourceChunkId.ToString("N"));
        AddIntArrayValue(payload, "sourceChunkIndexes", context.SourceChunkIndex);
    }

    private static void AddStringArrayValue(Dictionary<string, object?> payload, string key, string value) =>
        AddStringArrayValues(payload, key, [value]);

    private static void AddStringArrayValues(Dictionary<string, object?> payload, string key, IReadOnlyList<string> values)
    {
        var merged = ReadStringSet(payload.GetValueOrDefault(key));
        foreach (var value in values)
            if (!string.IsNullOrWhiteSpace(value)) merged.Add(value.Trim());
        payload[key] = merged.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void AddIntArrayValue(Dictionary<string, object?> payload, string key, int value)
    {
        var merged = ReadIntSet(payload.GetValueOrDefault(key));
        merged.Add(value);
        payload[key] = merged.Order().ToArray();
    }

    private static HashSet<string> ReadStringSet(object? value)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        switch (value)
        {
            case JsonElement { ValueKind: JsonValueKind.Array } array:
                foreach (var element in array.EnumerateArray())
                {
                    var itemText = element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
                    if (!string.IsNullOrWhiteSpace(itemText)) result.Add(itemText.Trim());
                }
                break;
            case JsonElement { ValueKind: JsonValueKind.String } textElement:
                var singleText = textElement.GetString();
                if (!string.IsNullOrWhiteSpace(singleText)) result.Add(singleText.Trim());
                break;
            case string raw when LooksLikeJsonRoot(raw, '['):
                try
                {
                    foreach (var item in JsonSerializer.Deserialize<string[]>(raw) ?? [])
                        if (!string.IsNullOrWhiteSpace(item)) result.Add(item.Trim());
                }
                catch (JsonException) { }
                break;
            case string raw when !string.IsNullOrWhiteSpace(raw):
                result.Add(raw.Trim());
                break;
        }
        return result;
    }

    private static HashSet<int> ReadIntSet(object? value)
    {
        var result = new HashSet<int>();
        switch (value)
        {
            case JsonElement { ValueKind: JsonValueKind.Array } array:
                foreach (var element in array.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var parsed)) result.Add(parsed);
                    else if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out parsed)) result.Add(parsed);
                }
                break;
            case JsonElement { ValueKind: JsonValueKind.Number } number when number.TryGetInt32(out var parsed):
                result.Add(parsed);
                break;
            case string raw when int.TryParse(raw, out var parsed):
                result.Add(parsed);
                break;
        }
        return result;
    }

    private static int ScoreCandidate(
        GraphNode node,
        IReadOnlyDictionary<string, string?> visibleProperties,
        IReadOnlyList<IngestSourceAssertionSummary> assertionSummaries,
        IReadOnlyList<string> terms,
        string queryText)
    {
        var queryVariants = BuildEntityNameVariants(queryText, ShouldStripEntityTitles(node.NodeType));
        if (terms.Count == 0 && queryVariants.Count == 0) return 1;

        var candidateVariants = CandidateNameInputs(node, visibleProperties, assertionSummaries)
            .SelectMany(name => BuildEntityNameVariants(name, ShouldStripEntityTitles(node.NodeType)))
            .ToHashSet(StringComparer.Ordinal);

        var haystack = string.Join("\n", new[]
        {
            node.Label ?? string.Empty,
            node.NodeType,
            string.Join("\n", visibleProperties.Select(kv => $"{kv.Key}: {kv.Value}")),
            string.Join("\n", assertionSummaries.Select(summary => $"{summary.SourceTitle} {summary.SourceKind} {summary.Summary} {string.Join(' ', summary.Aliases)}")),
        });
        var normalizedHaystack = NormalizeComparable(haystack);

        var score = 0;
        foreach (var queryVariant in queryVariants)
        {
            if (candidateVariants.Contains(queryVariant)) score += 25;
            else if (candidateVariants.Any(candidate => candidate.Contains(queryVariant, StringComparison.Ordinal) || queryVariant.Contains(candidate, StringComparison.Ordinal))) score += 8;
        }

        foreach (var term in terms)
        {
            if (string.Equals(node.Label, term, StringComparison.OrdinalIgnoreCase)) score += 10;
            else if (haystack.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 2;
            else if (normalizedHaystack.Contains(NormalizeComparable(term), StringComparison.Ordinal)) score += 1;
        }
        return score;
    }

    private static IEnumerable<string> CandidateNameInputs(
        GraphNode node,
        IReadOnlyDictionary<string, string?> visibleProperties,
        IReadOnlyList<IngestSourceAssertionSummary> assertionSummaries)
    {
        yield return node.Label ?? node.Key;
        foreach (var alias in assertionSummaries.SelectMany(summary => summary.Aliases))
            yield return alias;
        foreach (var kv in visibleProperties.Where(kv => kv.Key.Contains("alias", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kv.Value)))
            yield return kv.Value!;
    }

    private static IEnumerable<string> SplitTerms(string? query) =>
        (query ?? string.Empty)
            .Split([' ', '\t', '\r', '\n', ',', ';', ':', '.', '"', '\''], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(term => term.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static bool IsDisallowedEntityType(string type) =>
        string.Equals(type, EntityTypeService.ProjectNodeType, StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, EntityTypeService.ActNodeType, StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, EntityTypeService.ChapterNodeType, StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, EntityTypeService.ProjectFactNodeType, StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, EntityTypeService.EventNodeType, StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, EntityTypeService.SourceNodeType, StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, EntityTypeService.SourceChunkNodeType, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeTypeKey(string input)
    {
        var parts = new List<string>();
        var current = new List<char>();
        foreach (var ch in input.Trim())
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Add(ch);
            }
            else if (current.Count > 0)
            {
                parts.Add(new string(current.ToArray()));
                current.Clear();
            }
        }

        if (current.Count > 0)
            parts.Add(new string(current.ToArray()));
        return string.Concat(parts.Select(NormalizeTypePart));
    }

    private static string NormalizeTypePart(string part)
    {
        var rest = part.Length <= 1 ? string.Empty : part[1..];
        if (part.All(char.IsUpper) || part.All(char.IsLower))
            rest = rest.ToLowerInvariant();
        return char.ToUpperInvariant(part[0]) + rest;
    }

    private static string SingularizeTypeLabel(string input)
    {
        var trimmed = input.Trim();
        if (trimmed.EndsWith("ies", StringComparison.OrdinalIgnoreCase) && trimmed.Length > 3)
            return trimmed[..^3] + "y";
        if (trimmed.EndsWith("s", StringComparison.OrdinalIgnoreCase)
            && !trimmed.EndsWith("ss", StringComparison.OrdinalIgnoreCase)
            && !trimmed.EndsWith("is", StringComparison.OrdinalIgnoreCase)
            && !trimmed.EndsWith("us", StringComparison.OrdinalIgnoreCase)
            && trimmed.Length > 3)
        {
            return trimmed[..^1];
        }
        return trimmed;
    }

    private static string NormalizeComparable(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string NormalizeEntityName(string value)
    {
        var normalized = new string(value.Trim().Where(ch => !char.IsPunctuation(ch)).ToArray());
        return string.Join(' ', normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToLowerInvariant();
    }

    private static IReadOnlySet<string> BuildEntityNameVariants(string? value, bool stripTitles)
    {
        var normalized = NormalizeEntityName(value ?? string.Empty);
        if (normalized.Length == 0) return new HashSet<string>(StringComparer.Ordinal);

        var variants = new HashSet<string>(StringComparer.Ordinal) { normalized };
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = 0; index < words.Length - 1; index++)
        {
            if (!CanDropLeadingEntityNameWord(words[index], stripTitles)) break;
            variants.Add(string.Join(' ', words[(index + 1)..]));
        }

        return variants;
    }

    private static bool EntityNameVariantsOverlap(IReadOnlySet<string> sourceVariants, string candidateName, bool stripTitles)
    {
        var candidateVariants = BuildEntityNameVariants(candidateName, stripTitles);
        return sourceVariants.Overlaps(candidateVariants);
    }

    private static bool CanDropLeadingEntityNameWord(string word, bool stripTitles) =>
        string.Equals(word, "the", StringComparison.Ordinal)
        || string.Equals(word, "a", StringComparison.Ordinal)
        || string.Equals(word, "an", StringComparison.Ordinal)
        || (stripTitles && HonorificTitleWords.Contains(word));

    private static bool ShouldStripEntityTitles(string nodeType)
    {
        var normalized = NormalizeComparable(nodeType);
        return normalized.Contains("character", StringComparison.Ordinal)
            || normalized.Contains("person", StringComparison.Ordinal)
            || normalized.Contains("people", StringComparison.Ordinal)
            || normalized.Contains("individual", StringComparison.Ordinal)
            || normalized.Contains("figure", StringComparison.Ordinal)
            || normalized.Contains("npc", StringComparison.Ordinal);
    }

    private static readonly HashSet<string> HonorificTitleWords =
    [
        "admiral",
        "archmage",
        "baron",
        "baroness",
        "captain",
        "chief",
        "chieftain",
        "commander",
        "dame",
        "doctor",
        "dr",
        "duchess",
        "duke",
        "emperor",
        "empress",
        "general",
        "high",
        "highlord",
        "king",
        "lady",
        "lord",
        "magister",
        "master",
        "mistress",
        "prince",
        "princess",
        "professor",
        "queen",
        "saint",
        "sir",
        "st",
        "warchief",
    ];

    private static IReadOnlyDictionary<string, string?> VisibleProperties(IReadOnlyDictionary<string, object?> properties)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in properties)
        {
            if (IsHiddenProperty(kv.Key)) continue;
            result[kv.Key] = kv.Value?.ToString();
        }
        return result;
    }

    private static bool IsHiddenProperty(string key) =>
        IngestSourceAssertions.IsProtectedProperty(key)
        || string.Equals(key, "sourceType", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "sourceId", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "structural", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "order", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("vectorIndex", StringComparison.OrdinalIgnoreCase);

    private static string BestSummary(IReadOnlyDictionary<string, string?> properties)
    {
        foreach (var key in new[] { "description", "summary", "role", "value", "status", "notes" })
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
                if (IngestSourceAssertions.IsProtectedProperty(prop.Name)) continue;
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

    private static string? ReadString(object? value) => value switch
    {
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        JsonElement element => element.GetRawText(),
        string text => text,
        _ => value?.ToString(),
    };

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

    private sealed record ProjectEntityCandidate(
        Guid Id,
        string Type,
        string Name,
        string Summary,
        IReadOnlyDictionary<string, string?> CanonicalProperties,
        IReadOnlyList<IngestSourceAssertionSummary> SourceAssertions,
        int Score);

    private sealed record EntityTypeResolution(string Type, bool ExistingType);
}