using System.ComponentModel;
using System.Text.Json;
using Lorekeeper.Context;
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
    IEntityTypeService entityTypes,
    IContextIndexingService contextIndexing)
{
    public IList<AITool> Build(IngestAgentContext context) =>
    [
        AIFunctionFactory.Create(
            method: (string? type = null, string? query = null) => SearchProjectEntitiesAsync(context, type, query),
            name: "search_project_entities",
            description: "Search existing non-structural project entities. Returns concise candidates only: id, type, name, summary, and aliases. Use read_entity_sheet for full details before updating."),

        AIFunctionFactory.Create(
            method: (string entityId) => ReadEntitySheetAsync(context, entityId),
            name: "read_entity_sheet",
            description: "Read one entity's full wiki sheet and compact relationship list by entity id."),

        AIFunctionFactory.Create(
            method: (
                string type,
                string name,
                string summary,
                string[]? aliases = null,
                IngestWikiSectionInput[]? wikiSections = null,
                string? notes = null) => CreateEntityAsync(context, type, name, summary, aliases, wikiSections, notes),
            name: "create_ingest_entity",
            description: "Create a new graph entity with a durable wiki sheet. Use only after search_project_entities finds no plausible same subject. summary is required. wikiSections must be complete revised sections with compact citations, not raw observations."),

        AIFunctionFactory.Create(
            method: (
                string entityId,
                string summary,
                string[]? aliases = null,
                IngestWikiSectionInput[]? wikiSections = null,
                string? notes = null) => UpdateEntitySheetAsync(context, entityId, summary, aliases, wikiSections, notes),
            name: "update_ingest_entity_sheet",
            description: "Replace an entity's wiki sheet with a complete revised sheet that integrates the current chunk. Call read_entity_sheet first for existing entities. summary is required."),

        AIFunctionFactory.Create(
            method: (
                string fromEntityId,
                string toEntityId,
                string edgeType,
                string summary,
                IngestWikiCitationInput[]? citations = null,
                string? notes = null) => LinkEntitiesAsync(context, fromEntityId, toEntityId, edgeType, summary, citations, notes),
            name: "link_ingest_entities",
            description: "Create or update a concise relationship between two entities already touched by this ingest job. Stores summary and compact citations only."),

        AIFunctionFactory.Create(
            method: (string chunkSummary, string sourceSynopsis, string? notes = null) =>
                UpdateIngestSourceProgressAsync(context, chunkSummary, sourceSynopsis, notes),
            name: "update_ingest_source_progress",
            description: "Record the completed chunk summary and rolling source synopsis. Call exactly once after finishing each source chunk. Keep sourceSynopsis around 1500 words."),
    ];

    private async Task<string> SearchProjectEntitiesAsync(IngestAgentContext context, string? type, string? query)
    {
        var allowedTypes = await GetAllowedEntityTypesAsync(context.ProjectId);
        var requestedType = NormalizeText(type).Trim();
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

        var queryText = NormalizeText(query).Trim();
        var terms = SplitSearchTerms(queryText).ToList();
        var results = new List<ProjectEntityCandidate>();
        foreach (var searchType in searchTypes.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            var typedNodes = await nodes.ListByTypeAsync(context.ProjectId, searchType);
            foreach (var node in typedNodes)
            {
                if (!Guid.TryParseExact(node.Key, "N", out var id)) continue;
                var score = ScoreCandidate(node, terms, queryText);
                if (terms.Count > 0 && score == 0) continue;

                results.Add(new ProjectEntityCandidate(
                    id,
                    node.NodeType,
                    node.Label ?? node.Key,
                    Truncate(IngestWikiSheet.ReadSummary(node.Properties), 260),
                    IngestWikiSheet.ReadAliases(node.Properties),
                    score));
            }
        }

        var payload = results
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(25)
            .Select(candidate => new
            {
                id = candidate.Id,
                type = candidate.Type,
                name = candidate.Name,
                summary = candidate.Summary,
                aliases = candidate.Aliases,
                score = candidate.Score,
            });

        return JsonSerializer.Serialize(payload);
    }

    private async Task<string> ReadEntitySheetAsync(IngestAgentContext context, string entityId)
    {
        if (!Guid.TryParse(entityId, out var parsed)) return $"Error: entityId '{entityId}' is not a valid Guid.";
        var node = await ResolveAllowedEntityNodeAsync(context.ProjectId, parsed);
        if (node is null) return $"Error: entity {parsed} is not a non-structural project entity.";

        var adjacent = await edges.GetAdjacentAsync(node.Id, EdgeDirection.Both, edgeTypes: null, maxResults: 30);
        var otherIds = adjacent.Select(edge => edge.FromNodeId == node.Id ? edge.ToNodeId : edge.FromNodeId).Distinct().ToList();
        var otherNodes = otherIds.Count == 0
            ? new Dictionary<long, GraphNode>()
            : (await nodes.GetByIdsAsync(otherIds)).ToDictionary(other => other.Id);

        return JsonSerializer.Serialize(new
        {
            id = parsed,
            type = node.NodeType,
            name = node.Label ?? node.Key,
            summary = IngestWikiSheet.ReadSummary(node.Properties),
            aliases = IngestWikiSheet.ReadAliases(node.Properties),
            wikiSections = IngestWikiSheet.ReadSections(node.Properties),
            properties = IngestWikiSheet.VisibleProperties(node.Properties),
            relationships = adjacent
                .Where(edge => !string.Equals(edge.EdgeType, EntityService.HasChildEdgeType, StringComparison.OrdinalIgnoreCase))
                .Select(edge =>
                {
                    var isOutgoing = edge.FromNodeId == node.Id;
                    var otherNodeId = isOutgoing ? edge.ToNodeId : edge.FromNodeId;
                    otherNodes.TryGetValue(otherNodeId, out var other);
                    return new
                    {
                        edgeId = edge.Id,
                        direction = isOutgoing ? "outgoing" : "incoming",
                        edgeType = edge.EdgeType,
                        otherEntityId = other is not null && Guid.TryParseExact(other.Key, "N", out var otherGuid) ? otherGuid : Guid.Empty,
                        otherName = other?.Label ?? other?.Key ?? otherNodeId.ToString(),
                        otherType = other?.NodeType ?? string.Empty,
                        summary = ReadProperty(edge.Properties, IngestWikiSheet.SummaryProperty),
                        citations = IngestWikiSheet.ReadRelationshipCitations(edge.Properties).Take(6),
                    };
                })
                .Take(20),
        });
    }

    private async Task<string> CreateEntityAsync(
        IngestAgentContext context,
        [Description("Broad reusable entity type. Prefer an existing project type when one reasonably fits.")] string type,
        [Description("Display name for the entity.")] string name,
        string summary,
        string[]? aliases,
        IngestWikiSectionInput[]? wikiSections,
        string? notes)
    {
        var normalizedType = NormalizeText(type).Trim();
        if (string.IsNullOrWhiteSpace(normalizedType)) return "Error: type is required.";
        var resolvedType = await EnsureEntityTypeAsync(context.ProjectId, normalizedType);
        if (resolvedType is null) return $"Error: type '{normalizedType}' is structural or invalid and cannot be created by ingest.";

        var trimmedName = NormalizeText(name).Trim();
        if (string.IsNullOrWhiteSpace(trimmedName)) return "Error: name is required.";

        var validationError = ValidateEntitySheet(summary, aliases, wikiSections, notes);
        if (validationError is not null) return validationError;

        var duplicateNode = await FindDuplicateEntityByNameAsync(context.ProjectId, resolvedType.Type, trimmedName);
        if (duplicateNode is not null && Guid.TryParseExact(duplicateNode.Key, "N", out var duplicateId))
        {
            return JsonSerializer.Serialize(new
            {
                created = false,
                id = duplicateId,
                type = duplicateNode.NodeType,
                name = duplicateNode.Label ?? duplicateNode.Key,
                summary = IngestWikiSheet.ReadSummary(duplicateNode.Properties),
                aliases = IngestWikiSheet.ReadAliases(duplicateNode.Properties),
                exactNameMatch = true,
                message = $"An existing {duplicateNode.NodeType} named '{duplicateNode.Label ?? duplicateNode.Key}' already exists. If this is the same subject, call read_entity_sheet and update_ingest_entity_sheet with this id.",
            });
        }

        var entityObjectProperties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [IngestSourceAssertions.GraphOriginProperty] = IngestSourceAssertions.GraphOriginIngestValue,
        };
        IngestWikiSheet.ApplyEntitySheet(entityObjectProperties, summary, aliases, wikiSections, context);
        var entityProperties = entityObjectProperties.ToDictionary(
            kv => kv.Key,
            kv => kv.Value?.ToString(),
            StringComparer.OrdinalIgnoreCase);

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
            summary,
            aliases,
            wikiSections,
            notes);
        context.OnMutated();
        await contextIndexing.ReindexEntityAsync(context.ProjectId, created.Id);

        return JsonSerializer.Serialize(new
        {
            id = created.Id,
            type = created.Type,
            name = created.Name,
            action = IngestSourceAssertions.CreatedEntityAction,
            updatedSections = SectionTitles(wikiSections),
        });
    }

    private async Task<string> UpdateEntitySheetAsync(
        IngestAgentContext context,
        string entityId,
        string summary,
        string[]? aliases,
        IngestWikiSectionInput[]? wikiSections,
        string? notes)
    {
        if (!Guid.TryParse(entityId, out var parsed)) return $"Error: entityId '{entityId}' is not a valid Guid.";
        var validationError = ValidateEntitySheet(summary, aliases, wikiSections, notes);
        if (validationError is not null) return validationError;

        var node = await ResolveAllowedEntityNodeAsync(context.ProjectId, parsed);
        if (node is null) return $"Error: entity {parsed} is not a non-structural project entity.";

        IngestWikiSheet.ApplyEntitySheet(node.Properties, summary, aliases, wikiSections, context);
        node.UpdatedAt = DateTime.UtcNow;
        nodes.Update(node);
        await nodes.SaveChangesAsync();
        await AddExtractedFromAsync(context, node);

        var existingForChunk = await FindActiveEntityReportItemAsync(context.JobId, parsed, context.SourceChunkId);
        var action = await WasEntityCreatedByJobAsync(context.JobId, parsed)
            ? IngestSourceAssertions.CreatedEntityAction
            : IngestSourceAssertions.LinkedExistingEntityAction;
        await UpsertEntityReportItemAsync(
            context,
            existingForChunk,
            node,
            parsed,
            action,
            summary,
            aliases,
            wikiSections,
            notes);
        context.OnMutated();
        await contextIndexing.ReindexEntityAsync(context.ProjectId, parsed);

        return JsonSerializer.Serialize(new
        {
            id = parsed,
            type = node.NodeType,
            name = node.Label ?? node.Key,
            action,
            updatedSections = SectionTitles(wikiSections),
        });
    }

    private async Task<string> LinkEntitiesAsync(
        IngestAgentContext context,
        string fromEntityId,
        string toEntityId,
        string edgeType,
        string summary,
        IngestWikiCitationInput[]? citations,
        string? notes)
    {
        if (!Guid.TryParse(fromEntityId, out var from)) return $"Error: fromEntityId '{fromEntityId}' is not a valid Guid.";
        if (!Guid.TryParse(toEntityId, out var to)) return $"Error: toEntityId '{toEntityId}' is not a valid Guid.";
        var trimmedEdgeType = NormalizeText(edgeType).Trim();
        if (string.IsNullOrWhiteSpace(trimmedEdgeType)) return "Error: edgeType is required.";
        if (string.Equals(trimmedEdgeType, EntityService.HasChildEdgeType, StringComparison.OrdinalIgnoreCase))
            return "Error: HasChild is a managed structural relationship and cannot be created by ingest.";

        var validationError = ValidateRelationship(summary, citations, notes);
        if (validationError is not null) return validationError;

        var fromItem = await FindActiveEntityReportItemAsync(context.JobId, from);
        var toItem = await FindActiveEntityReportItemAsync(context.JobId, to);
        if (fromItem is null || toItem is null)
            return "Error: both relationship endpoints must be entities already touched by this ingest job. Update the endpoint wiki sheets first.";

        var fromNode = await nodes.FindByKeyAsync(context.ProjectId, from.ToString("N"));
        var toNode = await nodes.FindByKeyAsync(context.ProjectId, to.ToString("N"));
        if (fromNode is null || toNode is null) return "Error: one or both relationship endpoint graph nodes were not found.";

        var existingEdge = await edges.FindAsync(fromNode.Id, toNode.Id, trimmedEdgeType);
        var action = existingEdge is null
            ? IngestSourceAssertions.CreatedEdgeAction
            : IngestSourceAssertions.LinkedExistingEdgeAction;

        GraphEdge edge;
        if (existingEdge is null)
        {
            var edgeProperties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                [IngestSourceAssertions.GraphOriginProperty] = IngestSourceAssertions.GraphOriginIngestValue,
                [IngestWikiSheet.SummaryProperty] = NormalizeText(summary),
            };
            IngestWikiSheet.ApplyRelationshipCitations(edgeProperties, citations, context, mergeExisting: false);
            edge = await graph.UpsertEdgeAsync(fromNode.Id, toNode.Id, trimmedEdgeType, edgeProperties);
        }
        else
        {
            existingEdge.Properties[IngestWikiSheet.SummaryProperty] = NormalizeText(summary);
            IngestWikiSheet.ApplyRelationshipCitations(existingEdge.Properties, citations, context, mergeExisting: true);
            existingEdge.UpdatedAt = DateTime.UtcNow;
            edges.Update(existingEdge);
            await edges.SaveChangesAsync();
            edge = existingEdge;
        }

        var relationshipItem = await FindActiveRelationshipReportItemAsync(context.JobId, edge.Id, context.SourceChunkId);
        await UpsertRelationshipReportItemAsync(
            context,
            relationshipItem,
            edge,
            action,
            from,
            to,
            fromNode.Label ?? fromItem.Title,
            toNode.Label ?? toItem.Title,
            summary,
            citations,
            notes);
        context.OnMutated();
        await contextIndexing.ReindexEntityAsync(context.ProjectId, from);
        await contextIndexing.ReindexEntityAsync(context.ProjectId, to);

        return JsonSerializer.Serialize(new
        {
            fromEntityId = from,
            toEntityId = to,
            edgeType = trimmedEdgeType,
            graphEdgeId = edge.Id,
            action,
        });
    }

    private async Task<string> UpdateIngestSourceProgressAsync(
        IngestAgentContext context,
        string chunkSummary,
        string sourceSynopsis,
        string? notes)
    {
        var source = await ingest.GetSourceAsync(context.SourceId);
        if (source is null) return $"Error: source {context.SourceId} not found.";
        var sourceChunk = await ingest.GetSourceChunkAsync(context.SourceChunkId);
        if (sourceChunk is null) return $"Error: source chunk {context.SourceChunkId} not found.";

        source.Synopsis = NormalizeText(sourceSynopsis).Trim();
        source.UpdatedAt = DateTime.UtcNow;
        ingest.UpdateSource(source);

        sourceChunk.Summary = NormalizeText(chunkSummary).Trim();
        sourceChunk.AgentNotes = NormalizeText(notes).Trim();
        sourceChunk.UpdatedAt = DateTime.UtcNow;
        ingest.UpdateSourceChunk(sourceChunk);

        var payloadJson = JsonSerializer.Serialize(new
        {
            sourceId = context.SourceId.ToString("N"),
            sourceChunkId = context.SourceChunkId.ToString("N"),
            sourceChunkIndex = context.SourceChunkIndex,
            sourceSynopsisChars = source.Synopsis.Length,
        });
        var existingNote = (await ingest.ListReportItemsAsync(context.JobId)).FirstOrDefault(item =>
            item.Kind == IngestReportItemKind.SourceChunkNote
            && item.Status == IngestReportItemStatus.Active
            && item.SourceChunkId == context.SourceChunkId);
        if (existingNote is null)
        {
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
                PayloadJson = payloadJson,
            });
        }
        else
        {
            existingNote.Title = context.SourceChunkTitle;
            existingNote.Summary = sourceChunk.Summary;
            existingNote.Notes = sourceChunk.AgentNotes;
            existingNote.PayloadJson = payloadJson;
            existingNote.UpdatedAt = DateTime.UtcNow;
            ingest.UpdateReportItem(existingNote);
        }

        await ingest.SaveChangesAsync();
        context.OnMutated();
        await contextIndexing.ReindexIngestSourceChunkAsync(context.SourceChunkId);
        return JsonSerializer.Serialize(new
        {
            sourceChunkId = context.SourceChunkId,
            summary = sourceChunk.Summary,
            sourceSynopsisChars = source.Synopsis.Length,
        });
    }

    private async Task UpsertEntityReportItemAsync(
        IngestAgentContext context,
        IngestReportItem? existing,
        GraphNode? node,
        Guid entityId,
        string action,
        string summary,
        IReadOnlyList<string>? aliases,
        IReadOnlyList<IngestWikiSectionInput>? wikiSections,
        string? notes)
    {
        var title = node?.Label ?? entityId.ToString("N");
        var resourceType = node?.NodeType ?? string.Empty;
        var payloadJson = BuildEntityReportPayload(existing?.PayloadJson, context, action, aliases, wikiSections);
        var evidence = BuildEvidencePreview(wikiSections, context);
        if (existing is null)
        {
            await ingest.AddReportItemAsync(new IngestReportItem
            {
                JobId = context.JobId,
                SourceChunkId = context.SourceChunkId,
                Kind = IngestReportItemKind.Entity,
                Status = IngestReportItemStatus.Active,
                Title = title,
                Summary = Truncate(summary, 800),
                Notes = notes?.Trim() ?? string.Empty,
                Evidence = evidence,
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
            existing.Summary = Truncate(summary, 800);
            existing.Notes = notes?.Trim() ?? string.Empty;
            existing.Evidence = evidence;
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
        Guid from,
        Guid to,
        string fromTitle,
        string toTitle,
        string summary,
        IReadOnlyList<IngestWikiCitationInput>? citations,
        string? notes)
    {
        var payloadJson = BuildRelationshipReportPayload(existing?.PayloadJson, context, action, from, to, citations);
        var evidence = BuildCitationEvidencePreview(citations, context);
        if (existing is null)
        {
            await ingest.AddReportItemAsync(new IngestReportItem
            {
                JobId = context.JobId,
                SourceChunkId = context.SourceChunkId,
                Kind = IngestReportItemKind.Relationship,
                Status = IngestReportItemStatus.Active,
                Title = $"{fromTitle} -[{edge.EdgeType}]-> {toTitle}",
                Summary = Truncate(summary, 800),
                Notes = notes?.Trim() ?? string.Empty,
                Evidence = evidence,
                ResourceType = edge.EdgeType,
                GraphEdgeId = edge.Id,
                PayloadJson = payloadJson,
            });
        }
        else
        {
            existing.Title = $"{fromTitle} -[{edge.EdgeType}]-> {toTitle}";
            existing.ResourceType = edge.EdgeType;
            existing.Summary = Truncate(summary, 800);
            existing.Notes = notes?.Trim() ?? string.Empty;
            existing.Evidence = evidence;
            existing.GraphEdgeId = edge.Id;
            existing.PayloadJson = payloadJson;
            existing.UpdatedAt = DateTime.UtcNow;
            ingest.UpdateReportItem(existing);
        }

        await ingest.SaveChangesAsync();
    }

    private async Task<IngestReportItem?> FindActiveEntityReportItemAsync(Guid jobId, Guid entityId, Guid? sourceChunkId = null) =>
        (await ingest.ListReportItemsAsync(jobId)).FirstOrDefault(item =>
            item.Kind == IngestReportItemKind.Entity
            && item.Status == IngestReportItemStatus.Active
            && item.EntityId == entityId
            && (sourceChunkId is null || item.SourceChunkId == sourceChunkId));

    private async Task<IngestReportItem?> FindActiveRelationshipReportItemAsync(Guid jobId, long graphEdgeId, Guid? sourceChunkId = null) =>
        (await ingest.ListReportItemsAsync(jobId)).FirstOrDefault(item =>
            item.Kind == IngestReportItemKind.Relationship
            && item.Status == IngestReportItemStatus.Active
            && item.GraphEdgeId == graphEdgeId
            && (sourceChunkId is null || item.SourceChunkId == sourceChunkId));

    private async Task<bool> WasEntityCreatedByJobAsync(Guid jobId, Guid entityId) =>
        (await ingest.ListReportItemsAsync(jobId)).Any(item =>
            item.Kind == IngestReportItemKind.Entity
            && item.Status == IngestReportItemStatus.Active
            && item.EntityId == entityId
            && string.Equals(IngestSourceAssertions.ReadEntityGraphAction(item.PayloadJson), IngestSourceAssertions.CreatedEntityAction, StringComparison.Ordinal));

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

    private async Task<EntityTypeResolution?> EnsureEntityTypeAsync(Guid projectId, string requestedType)
    {
        var resolved = await ResolveEntityTypeAsync(projectId, requestedType, allowNew: true);
        if (resolved is null || resolved.ExistingType) return resolved;

        var created = await entityTypes.CreateAsync(projectId, resolved.Type);
        return new EntityTypeResolution(created.Type, ExistingType: true);
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
            var existing = await nodes.ListByTypeAsync(projectId, candidateType);
            var duplicate = existing.FirstOrDefault(node =>
                node.Id != excludeNodeId
                && string.Equals(NormalizeEntityName(node.Label ?? node.Key), normalizedName, StringComparison.Ordinal));
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

    private static string? ValidateEntitySheet(
        string? summary,
        IReadOnlyList<string>? aliases,
        IReadOnlyList<IngestWikiSectionInput>? sections,
        string? notes)
    {
        if (string.IsNullOrWhiteSpace(summary)) return "Error: summary is required for every entity wiki sheet.";
        if (IngestSourceAssertions.ContainsDisallowedExtractionRationale(summary))
            return "Error: summary must be source-grounded entity knowledge, not extraction process rationale.";
        if ((aliases ?? []).Any(IngestSourceAssertions.ContainsDisallowedExtractionRationale))
            return "Error: aliases must be source-mentioned names, not extraction process rationale.";
        if (IngestSourceAssertions.ContainsDisallowedExtractionRationale(notes))
            return "Error: notes must not record unsupported-update rationale.";

        foreach (var section in sections ?? [])
        {
            if (IngestSourceAssertions.ContainsDisallowedExtractionRationale(section.Title)
                || IngestSourceAssertions.ContainsDisallowedExtractionRationale(section.Body))
            {
                return "Error: wiki sections must contain source-grounded knowledge, not extraction process rationale.";
            }

            foreach (var citation in section.Citations ?? [])
            {
                if (IngestSourceAssertions.ContainsDisallowedExtractionRationale(citation.Snippet))
                    return "Error: citation snippets must quote or closely summarize source support.";
            }
        }

        return null;
    }

    private static string? ValidateRelationship(
        string? summary,
        IReadOnlyList<IngestWikiCitationInput>? citations,
        string? notes)
    {
        if (string.IsNullOrWhiteSpace(summary)) return "Error: summary is required for every relationship.";
        if (IngestSourceAssertions.ContainsDisallowedExtractionRationale(summary))
            return "Error: relationship summary must be source-grounded, not extraction process rationale.";
        if (IngestSourceAssertions.ContainsDisallowedExtractionRationale(notes))
            return "Error: notes must not record unsupported-update rationale.";
        foreach (var citation in citations ?? [])
        {
            if (IngestSourceAssertions.ContainsDisallowedExtractionRationale(citation.Snippet))
                return "Error: citation snippets must quote or closely summarize source support.";
        }
        return null;
    }

    private static string BuildEntityReportPayload(
        string? json,
        IngestAgentContext context,
        string action,
        IReadOnlyList<string>? aliases,
        IReadOnlyList<IngestWikiSectionInput>? wikiSections)
    {
        var payload = ReadPayload(json);
        var existingAction = ReadString(payload.GetValueOrDefault(IngestSourceAssertions.EntityGraphActionProperty));
        payload[IngestSourceAssertions.EntityGraphActionProperty] = string.Equals(existingAction, IngestSourceAssertions.CreatedEntityAction, StringComparison.Ordinal)
            ? IngestSourceAssertions.CreatedEntityAction
            : action;
        ApplyCommonPayload(payload, context);
        payload["aliases"] = (aliases ?? []).Where(alias => !string.IsNullOrWhiteSpace(alias)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        payload["wikiSectionTitles"] = SectionTitles(wikiSections);
        return JsonSerializer.Serialize(payload);
    }

    private static string BuildRelationshipReportPayload(
        string? json,
        IngestAgentContext context,
        string action,
        Guid from,
        Guid to,
        IReadOnlyList<IngestWikiCitationInput>? citations)
    {
        var payload = ReadPayload(json);
        var existingAction = ReadString(payload.GetValueOrDefault(IngestSourceAssertions.RelationshipGraphActionProperty));
        payload[IngestSourceAssertions.RelationshipGraphActionProperty] = string.Equals(existingAction, IngestSourceAssertions.CreatedEdgeAction, StringComparison.Ordinal)
            ? IngestSourceAssertions.CreatedEdgeAction
            : action;
        ApplyCommonPayload(payload, context);
        payload["fromEntityId"] = from;
        payload["toEntityId"] = to;
        payload["citationCount"] = (citations ?? []).Count;
        return JsonSerializer.Serialize(payload);
    }

    private static void ApplyCommonPayload(Dictionary<string, object?> payload, IngestAgentContext context)
    {
        payload["sourceId"] = context.SourceId.ToString("N");
        payload["sourceTitle"] = context.SourceTitle;
        payload["sourceKind"] = context.SourceKind;
        payload["sourceChunkId"] = context.SourceChunkId.ToString("N");
        payload["sourceChunkIndex"] = context.SourceChunkIndex;
        payload["sourceChunkIds"] = new[] { context.SourceChunkId.ToString("N") };
        payload["sourceChunkIndexes"] = new[] { context.SourceChunkIndex };
    }

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

    private static string[] SectionTitles(IReadOnlyList<IngestWikiSectionInput>? sections) =>
        (sections ?? [])
            .Select(section => NormalizeText(section.Title).Trim())
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string BuildEvidencePreview(IReadOnlyList<IngestWikiSectionInput>? sections, IngestAgentContext context)
    {
        var snippets = (sections ?? [])
            .SelectMany(section => IngestWikiSheet.BuildCitations(section.Citations, context))
            .Select(IngestWikiSheet.CitationPreview)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6);
        return Truncate(string.Join("\n", snippets), 1200);
    }

    private static string BuildCitationEvidencePreview(IReadOnlyList<IngestWikiCitationInput>? citations, IngestAgentContext context) =>
        Truncate(string.Join("\n", IngestWikiSheet.BuildCitations(citations, context)
            .Select(IngestWikiSheet.CitationPreview)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)), 1200);

    private static int ScoreCandidate(
        GraphNode node,
        IReadOnlyList<string> terms,
        string queryText)
    {
        var queryVariants = BuildEntityNameVariants(queryText, ShouldStripEntityTitles(node.NodeType));
        if (terms.Count == 0 && queryVariants.Count == 0) return 1;

        var candidateVariants = CandidateNameInputs(node)
            .SelectMany(name => BuildEntityNameVariants(name, ShouldStripEntityTitles(node.NodeType)))
            .ToHashSet(StringComparer.Ordinal);

        var haystack = string.Join("\n", new[]
        {
            node.Label ?? string.Empty,
            node.NodeType,
            IngestWikiSheet.BuildEntitySearchText(node.Properties),
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

    private static IEnumerable<string> CandidateNameInputs(GraphNode node)
    {
        yield return node.Label ?? node.Key;
        foreach (var alias in IngestWikiSheet.ReadAliases(node.Properties))
            yield return alias;
        foreach (var kv in IngestWikiSheet.VisibleProperties(node.Properties).Where(kv => kv.Key.Contains("alias", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kv.Value)))
            yield return kv.Value!;
    }

    private static IEnumerable<string> SplitSearchTerms(string? query)
    {
        var terms = SplitTerms(query).ToList();
        if (terms.Count <= 1) return terms;

        var strongTerms = terms.Where(term => !IsWeakEntitySearchTerm(term)).ToList();
        return strongTerms.Count > 0 ? strongTerms : terms;
    }

    private static IEnumerable<string> SplitTerms(string? query) =>
        (query ?? string.Empty)
            .Split([' ', '\t', '\r', '\n', ',', ';', ':', '.', '"', '\''], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(term => term.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static bool IsWeakEntitySearchTerm(string term)
    {
        var normalized = NormalizeEntityName(term);
        return HonorificTitleWords.Contains(normalized)
            || WeakSearchTerms.Contains(normalized);
    }

    private static bool IsDisallowedEntityType(string type) =>
        string.Equals(type, EntityTypeService.ProjectNodeType, StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, EntityTypeService.ActNodeType, StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, EntityTypeService.ChapterNodeType, StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, EntityTypeService.ProjectFactNodeType, StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, EntityTypeService.EventNodeType, StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, EntityTypeService.SourceNodeType, StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, EntityTypeService.SourceChunkNodeType, StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, EntityTypeService.SourceBlockNodeType, StringComparison.OrdinalIgnoreCase);

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

    private static string? ReadString(object? value) => value switch
    {
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        JsonElement element => element.GetRawText(),
        string text => text,
        _ => value?.ToString(),
    };

    private static string ReadProperty(IReadOnlyDictionary<string, object?> properties, string key) =>
        properties.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty;

    private static bool LooksLikeJsonRoot(string json, char rootChar)
    {
        foreach (var ch in json)
        {
            if (char.IsWhiteSpace(ch)) continue;
            return ch == rootChar;
        }
        return false;
    }

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max] + "...";
    }

    private static string NormalizeText(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var buffer = new char[value.Length];
        var index = 0;
        foreach (var ch in value)
        {
            buffer[index++] = ch switch
            {
                '\u0018' or '\u0019' => '\'',
                _ when char.IsControl(ch) && ch is not '\r' and not '\n' and not '\t' => ' ',
                _ => ch,
            };
        }
        return new string(buffer, 0, index);
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

    private static readonly HashSet<string> WeakSearchTerms =
    [
        "and",
        "from",
        "of",
        "the",
        "with",
    ];

    private sealed record ProjectEntityCandidate(
        Guid Id,
        string Type,
        string Name,
        string Summary,
        IReadOnlyList<string> Aliases,
        int Score);

    private sealed record EntityTypeResolution(string Type, bool ExistingType);
}
