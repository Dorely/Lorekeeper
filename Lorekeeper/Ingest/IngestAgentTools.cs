using System.ComponentModel;
using System.Text.Json;
using Lorekeeper.Context;
using Lorekeeper.EntityVisuals;
using Lorekeeper.Images;
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
    IEntityTypeService entityTypes,
    IContextIndexingService contextIndexing,
    IEntityVisualExampleService entityVisualExamples,
    IProjectImageService projectImages)
{
    public IList<AITool> Build(IngestAgentContext context) =>
    [
        AIFunctionFactory.Create(
            method: (string? type = null, string? cursor = null, int? limit = null) => ListProjectEntityIndexAsync(context, type, cursor, limit),
            name: "list_project_entity_index",
            description: "List a compact identity index of existing non-structural project entities. Returns only id, type, name, and aliases plus pagination metadata; no summaries, wiki bodies, properties, or relationship details."),

        AIFunctionFactory.Create(
            method: (IngestEntityMentionInput[] mentions) => ResolveProjectEntityMentionsAsync(context, mentions),
            name: "resolve_project_entity_mentions",
            description: "Bulk resolve source mentions against existing non-structural project entities using names and aliases. Returns compact candidates only; use before creating entities."),

        AIFunctionFactory.Create(
            method: (
                string summary,
                string? entityId = null,
                string? type = null,
                string? name = null,
                string[]? aliasesObserved = null,
                IngestWikiSectionInput[]? sections = null,
                string? notes = null) => AppendIngestEntityObservationAsync(context, entityId, type, name, summary, aliasesObserved, sections, notes),
            name: "append_ingest_entity_observation",
            description: "Append a temporary staging observation for one entity from the current chunk. Supply entityId for an existing entity, or type plus name to create/reuse a minimal identity shell. Final canon markdown is written later during source finalization."),

        AIFunctionFactory.Create(
            method: (
                string fromEntityId,
                string toEntityId,
                string edgeType) => AppendIngestRelationshipObservationAsync(context, fromEntityId, toEntityId, edgeType),
            name: "append_ingest_relationship_observation",
            description: "Append one temporary relationship marker with only endpoints and edge type. Relationship markers are promoted to simple graph edges during finalization; put all narrative detail in entity observations."),

        AIFunctionFactory.Create(
            method: (Guid candidateId, Guid entityId, string? label = null, ProjectImageCropRegion? crop = null, string? cropAltText = null) =>
                PromoteIngestVisualAsync(context, candidateId, entityId, label, crop, cropAltText),
            name: "promote_ingest_visual_candidate",
            description: "Promote a source visual candidate and attach it to a resolved entity only when the visual clearly depicts that exact entity. Pass a percentage-based crop plus subject-only cropAltText when the entity occupies only part of a broader scene. Never infer identity from proximity alone or attach ambiguous/decorative images."),

        AIFunctionFactory.Create(
            method: (Guid candidateId, string reason) => SkipIngestVisualAsync(context, candidateId, reason),
            name: "skip_ingest_visual_candidate",
            description: "Mark a source visual as skipped only when it is clearly decorative, a mask/logo, unusable, or too ambiguous to serve as any entity example. Give a concise reason."),

        AIFunctionFactory.Create(
            method: (string chunkSummary, string sourceSynopsis, string? notes = null) =>
                UpdateIngestSourceProgressAsync(context, chunkSummary, sourceSynopsis, notes),
            name: "update_ingest_source_progress",
            description: "Record a concise completed chunk summary and rolling source synopsis. Call exactly once after finishing each source chunk. Keep sourceSynopsis around 1500 words and notes short/operational."),
    ];

    private async Task<string> PromoteIngestVisualAsync(
        IngestAgentContext context,
        Guid candidateId,
        Guid entityId,
        string? label,
        ProjectImageCropRegion? crop,
        string? cropAltText)
    {
        try
        {
            var candidate = await entityVisualExamples.GetCandidateAsync(context.ProjectId, candidateId);
            if (candidate is null || candidate.IngestSourceId != context.SourceId)
                return "Error: visual candidate was not found for this ingest source.";
            if (candidate.StartChar is int start && candidate.EndChar is int end && (start < 0 || end <= start))
                return "Error: visual candidate has invalid source bounds.";
            var sourceImage = await entityVisualExamples.PromoteCandidateAsync(context.ProjectId, candidateId);
            var referenceImage = crop is null
                ? sourceImage
                : await projectImages.CropAsync(context.ProjectId, sourceImage.Id, new ProjectImageCropRequest(
                    crop,
                    string.Empty,
                    cropAltText?.Trim() ?? string.Empty));
            var example = await entityVisualExamples.AttachAsync(
                context.ProjectId,
                entityId,
                referenceImage.Id,
                label,
                EntityVisualExampleOrigin.Ingest,
                candidateId);
            context.OnMutated();
            return JsonSerializer.Serialize(new
            {
                status = "promoted_and_attached",
                example.Id,
                example.EntityId,
                example.EntityName,
                example.Label,
                image = new { example.Image.Id, example.Image.FileName, example.Image.AltText, example.Image.PreviewUrl },
            });
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    private async Task<string> SkipIngestVisualAsync(IngestAgentContext context, Guid candidateId, string reason)
    {
        try
        {
            var candidate = await entityVisualExamples.GetCandidateAsync(context.ProjectId, candidateId);
            if (candidate is null || candidate.IngestSourceId != context.SourceId)
                return "Error: visual candidate was not found for this ingest source.";
            var updated = await entityVisualExamples.SetCandidateStatusAsync(
                context.ProjectId, candidateId, SourceVisualCandidateStatus.Skipped, reason);
            context.OnMutated();
            return JsonSerializer.Serialize(new { status = updated.Status, updated.Id, reason });
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    public IList<AITool> BuildFinalReview(IngestFinalReviewContext context) =>
    [
        AIFunctionFactory.Create(
            method: () => ReadIngestEntitySourceObservationsAsync(context),
            name: "read_ingest_entity_source_observations",
            description: "Read this entity's temporary staging observations and related relationship markers for the current ingest source. Returns staging data only, not canonical project wiki content."),

        AIFunctionFactory.Create(
            method: (
                string body,
                string? notes = null) => WriteIngestSourceWikiSectionAsync(context, body, notes),
            name: "write_ingest_source_wiki_section",
            description: "Write exactly one source-specific canon markdown page for this entity. Replaces only the canonSource property for this ingest source and preserves other source/manual canon."),
    ];

    private async Task<string> ListProjectEntityIndexAsync(IngestAgentContext context, string? type, string? cursor, int? limit)
    {
        var searchTypes = await ResolveSearchTypesAsync(context.ProjectId, type);
        var offset = int.TryParse(cursor, out var parsedCursor) ? Math.Max(0, parsedCursor) : 0;
        var take = Math.Clamp(limit ?? 100, 1, 200);

        var rows = new List<ProjectEntityIdentity>();
        foreach (var searchType in searchTypes.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            var typedNodes = await nodes.ListByTypeAsync(context.ProjectId, searchType);
            rows.AddRange(typedNodes
                .Where(node => Guid.TryParseExact(node.Key, "N", out _))
                .Select(node => new ProjectEntityIdentity(
                    Guid.ParseExact(node.Key, "N"),
                    node.NodeType,
                    node.Label ?? node.Key,
                    IngestWikiSheet.ReadAliases(node.Properties))));
        }

        var page = rows
            .OrderBy(row => row.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .Skip(offset)
            .Take(take)
            .ToList();

        return JsonSerializer.Serialize(new
        {
            items = page.Select(row => new
            {
                id = row.Id,
                type = row.Type,
                name = row.Name,
                aliases = row.Aliases,
            }),
            nextCursor = offset + page.Count < rows.Count ? (offset + page.Count).ToString() : null,
            returned = page.Count,
            total = rows.Count,
        });
    }

    private async Task<string> ResolveProjectEntityMentionsAsync(IngestAgentContext context, IngestEntityMentionInput[]? mentions)
    {
        var requestedMentions = (mentions ?? [])
            .Where(mention => !string.IsNullOrWhiteSpace(mention.Mention)
                || mention.Variants is { Length: > 0 }
                || !string.IsNullOrWhiteSpace(mention.Type))
            .Take(40)
            .ToList();

        var results = new List<object>();
        foreach (var mention in requestedMentions)
        {
            var queries = new[] { mention.Mention }
                .Concat(mention.Variants ?? [])
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => NormalizeText(value).Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var searchTypes = await ResolveSearchTypesAsync(context.ProjectId, mention.Type);
            var candidates = new List<ProjectEntityIdentityCandidate>();
            foreach (var searchType in searchTypes)
            {
                foreach (var node in await nodes.ListByTypeAsync(context.ProjectId, searchType))
                {
                    if (!Guid.TryParseExact(node.Key, "N", out var id)) continue;
                    var score = queries.Count == 0
                        ? 1
                        : queries.Max(query => ScoreIdentityCandidate(node, query));
                    if (score == 0) continue;

                    candidates.Add(new ProjectEntityIdentityCandidate(
                        id,
                        node.NodeType,
                        node.Label ?? node.Key,
                        IngestWikiSheet.ReadAliases(node.Properties),
                        score));
                }
            }

            results.Add(new
            {
                mention = mention.Mention,
                type = mention.Type,
                candidates = candidates
                    .OrderByDescending(candidate => candidate.Score)
                    .ThenBy(candidate => candidate.Type, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(8)
                    .Select(candidate => new
                    {
                        id = candidate.Id,
                        type = candidate.Type,
                        name = candidate.Name,
                        aliases = candidate.Aliases,
                        score = candidate.Score,
                    }),
            });
        }

        return JsonSerializer.Serialize(results);
    }

    private async Task<string> AppendIngestEntityObservationAsync(
        IngestAgentContext context,
        string? entityId,
        string? type,
        string? name,
        string summary,
        string[]? aliasesObserved,
        IngestWikiSectionInput[]? sections,
        string? notes)
    {
        var sectionList = (sections ?? []).ToList();
        var validationError = ValidateEntityObservation(summary, sectionList);
        if (validationError is not null) return validationError;

        var resolution = await ResolveOrCreateObservationEntityAsync(context, entityId, type, name);
        if (resolution.Error is not null) return resolution.Error;
        var node = resolution.Node!;
        var parsed = resolution.EntityId;

        var existingForChunk = await FindActiveEntityStagingRecordAsync(context.JobId, parsed, context.SourceChunkId);
        await UpsertEntityStagingRecordAsync(
            context,
            existingForChunk,
            node,
            parsed,
            resolution.Action,
            summary,
            aliasesObserved,
            sectionList,
            notes);
        context.OnMutated();
        await contextIndexing.ReindexEntityAsync(context.ProjectId, parsed);

        return JsonSerializer.Serialize(new
        {
            id = parsed,
            type = node.NodeType,
            name = node.Label ?? node.Key,
            action = resolution.Action,
            created = resolution.Created,
            exactNameMatch = resolution.ExactNameMatch,
            stagedObservationCount = await CountActiveEntityStagingRecordsAsync(context.JobId, parsed),
            sourceSectionCount = sectionList.Count,
            canonicalUpdated = false,
        });
    }

    private async Task<ObservationEntityResolution> ResolveOrCreateObservationEntityAsync(
        IngestAgentContext context,
        string? entityId,
        string? type,
        string? name)
    {
        if (!string.IsNullOrWhiteSpace(entityId))
        {
            if (!Guid.TryParse(entityId, out var parsed))
                return ObservationEntityResolution.Failed($"Error: entityId '{entityId}' is not a valid Guid.");

            var existingNode = await ResolveAllowedEntityNodeAsync(context.ProjectId, parsed);
            return existingNode is null
                ? ObservationEntityResolution.Failed($"Error: entity {parsed} is not a non-structural project entity.")
                : ObservationEntityResolution.Resolved(parsed, existingNode, IngestSourceAssertions.ObservedEntityAction, created: false, exactNameMatch: false);
        }

        var normalizedType = NormalizeText(type).Trim();
        if (string.IsNullOrWhiteSpace(normalizedType))
            return ObservationEntityResolution.Failed("Error: type is required when entityId is not supplied.");

        var resolvedType = await EnsureEntityTypeAsync(context.ProjectId, normalizedType);
        if (resolvedType is null)
            return ObservationEntityResolution.Failed($"Error: type '{normalizedType}' is structural or invalid and cannot be created by ingest.");

        var trimmedName = NormalizeText(name).Trim();
        if (string.IsNullOrWhiteSpace(trimmedName))
            return ObservationEntityResolution.Failed("Error: name is required when entityId is not supplied.");

        var duplicateNode = await FindDuplicateEntityByNameAsync(context.ProjectId, resolvedType.Type, trimmedName);
        if (duplicateNode is not null && Guid.TryParseExact(duplicateNode.Key, "N", out var duplicateId))
        {
            return ObservationEntityResolution.Resolved(
                duplicateId,
                duplicateNode,
                IngestSourceAssertions.ObservedEntityAction,
                created: false,
                exactNameMatch: true);
        }

        var entityProperties = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [IngestSourceAssertions.GraphOriginProperty] = IngestSourceAssertions.GraphOriginIngestValue,
        };
        var created = await entities.CreateAsync(context.ProjectId, resolvedType.Type, trimmedName, entityProperties);
        var createdNode = await nodes.FindByKeyAsync(context.ProjectId, created.Id.ToString("N"));
        return createdNode is null
            ? ObservationEntityResolution.Failed($"Error: created entity {created.Id} could not be loaded.")
            : ObservationEntityResolution.Resolved(
                created.Id,
                createdNode,
                IngestSourceAssertions.CreatedEntityAction,
                created: true,
                exactNameMatch: false);
    }

    private async Task<string> AppendIngestRelationshipObservationAsync(
        IngestAgentContext context,
        string fromEntityId,
        string toEntityId,
        string edgeType)
    {
        if (!Guid.TryParse(fromEntityId, out var from)) return $"Error: fromEntityId '{fromEntityId}' is not a valid Guid.";
        if (!Guid.TryParse(toEntityId, out var to)) return $"Error: toEntityId '{toEntityId}' is not a valid Guid.";

        var fromNode = await ResolveAllowedEntityNodeAsync(context.ProjectId, from);
        if (fromNode is null) return $"Error: from entity {from} is not a non-structural project entity.";
        var toNode = await ResolveAllowedEntityNodeAsync(context.ProjectId, to);
        if (toNode is null) return $"Error: to entity {to} is not a non-structural project entity.";

        if (!await IsEntityTouchedByJobAsync(context.JobId, from))
            return $"Error: from entity {from} must be observed before recording relationship observations.";
        if (!await IsEntityTouchedByJobAsync(context.JobId, to))
            return $"Error: to entity {to} must be observed before recording relationship observations.";

        var normalizedEdgeType = NormalizeText(edgeType).Trim();
        if (string.IsNullOrWhiteSpace(normalizedEdgeType))
            return "Error: edgeType is required for relationship observations.";
        if (string.Equals(normalizedEdgeType, EntityService.HasChildEdgeType, StringComparison.OrdinalIgnoreCase))
            return "Error: HasChild is a managed structural relationship and cannot be recorded by ingest.";

        var existing = await FindActiveRelationshipObservationStagingRecordAsync(context.JobId, context.SourceChunkId, from, to, normalizedEdgeType);
        await UpsertRelationshipObservationStagingRecordAsync(
            context,
            existing,
            normalizedEdgeType,
            from,
            to,
            fromNode.Label ?? fromNode.Key,
            toNode.Label ?? toNode.Key);
        context.OnMutated();
        await contextIndexing.ReindexEntityAsync(context.ProjectId, from);
        await contextIndexing.ReindexEntityAsync(context.ProjectId, to);

        return JsonSerializer.Serialize(new
        {
            fromEntityId = from,
            toEntityId = to,
            edgeType = normalizedEdgeType,
            action = IngestSourceAssertions.ObservedRelationshipAction,
            canonicalEdgeUpdated = false,
        });
    }

    private async Task<string> ReadIngestEntitySourceObservationsAsync(IngestFinalReviewContext context)
    {
        var node = await ResolveAllowedEntityNodeAsync(context.ProjectId, context.EntityId);
        if (node is null) return $"Error: entity {context.EntityId} is not a non-structural project entity.";

        var stagingRecords = await ingest.ListStagingRecordsAsync(context.JobId);
        var observations = stagingRecords
            .Where(item => item.Status == IngestStagingRecordStatus.Active
                && item.Kind == IngestStagingRecordKind.Entity
                && item.SourceId == context.SourceId
                && item.EntityId == context.EntityId)
            .OrderBy(item => item.SourceChunkIndex ?? int.MaxValue)
            .ToList();

        var relationshipObservations = new List<object>();
        foreach (var item in stagingRecords)
        {
            if (item.Status != IngestStagingRecordStatus.Active || item.Kind != IngestStagingRecordKind.Relationship)
                continue;
            if (item.SourceId != context.SourceId)
                continue;
            if (!TryReadRelationshipEndpoints(item, out var from, out var to)
                || (from != context.EntityId && to != context.EntityId))
            {
                continue;
            }

            relationshipObservations.Add(new
            {
                sourceChunkId = item.SourceChunkId,
                sourceChunkIndex = item.SourceChunkIndex ?? ReadIntPayload(item.PayloadJson, "sourceChunkIndex"),
                edgeType = item.EdgeType,
                title = item.Title,
                fromEntityId = from,
                toEntityId = to,
            });
        }

        return JsonSerializer.Serialize(new
        {
            entity = new
            {
                id = context.EntityId,
                type = node.NodeType,
                name = node.Label ?? node.Key,
            },
            source = new
            {
                id = context.SourceId,
                title = context.SourceTitle,
                kind = context.SourceKind,
            },
            observations = observations.Select(observation => new
            {
                observation.SourceChunkId,
                observation.SourceChunkIndex,
                observation.Summary,
                Aliases = ReadAliasesJson(observation.AliasesJson),
                WikiSections = ReadWikiSectionInputsJson(observation.WikiSectionsJson).Select(section => new
                {
                    id = section.Id,
                    title = section.Title,
                    body = section.Body,
                }),
                observation.Notes,
            }),
            relationshipObservations,
        });
    }

    private async Task<string> WriteIngestSourceWikiSectionAsync(
        IngestFinalReviewContext context,
        string body,
        string? notes)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "Error: body is required.";

        var node = await ResolveAllowedEntityNodeAsync(context.ProjectId, context.EntityId);
        if (node is null) return $"Error: entity {context.EntityId} is not a non-structural project entity.";

        IngestWikiSheet.UpsertCanonSourceMarkdown(
            node.Properties,
            context.SourceId,
            context.SourceTitle,
            context.SourceKind,
            context.JobId,
            body);
        node.UpdatedAt = DateTime.UtcNow;
        nodes.Update(node);
        await nodes.SaveChangesAsync();
        context.OnMutated();
        await contextIndexing.ReindexEntityAsync(context.ProjectId, context.EntityId);

        return JsonSerializer.Serialize(new
        {
            entityId = context.EntityId,
            sourceId = context.SourceId,
            sourceTitle = context.SourceTitle,
            bodyChars = body.Trim().Length,
            canonicalSummaryUpdated = false,
            canonSourceUpdated = true,
        });
    }

    private async Task<IReadOnlyCollection<string>> ResolveSearchTypesAsync(Guid projectId, string? type)
    {
        var allowedTypes = await GetAllowedEntityTypesAsync(projectId);
        var requestedType = NormalizeText(type).Trim();
        if (string.IsNullOrWhiteSpace(requestedType))
            return allowedTypes;

        if (IsDisallowedEntityType(requestedType))
            return [];

        var matchedType = await ResolveEntityTypeAsync(projectId, requestedType, allowNew: false);
        return matchedType is null
            ? []
            : await GetEquivalentEntityTypeNamesAsync(projectId, matchedType.Type);
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

        source.Synopsis = TruncateWords(NormalizeText(sourceSynopsis).Trim(), 1500);
        source.UpdatedAt = DateTime.UtcNow;
        ingest.UpdateSource(source);

        sourceChunk.Summary = NormalizeSourceChunkSummary(chunkSummary);
        sourceChunk.AgentNotes = NormalizeSourceChunkNotes(notes);
        sourceChunk.UpdatedAt = DateTime.UtcNow;
        ingest.UpdateSourceChunk(sourceChunk);

        var payloadJson = JsonSerializer.Serialize(new
        {
            sourceId = context.SourceId.ToString("N"),
            sourceChunkId = context.SourceChunkId.ToString("N"),
            sourceChunkIndex = context.SourceChunkIndex,
            sourceSynopsisChars = source.Synopsis.Length,
        });
        var existingNote = (await ingest.ListStagingRecordsAsync(context.JobId)).FirstOrDefault(item =>
            item.Kind == IngestStagingRecordKind.SourceChunkNote
            && item.Status == IngestStagingRecordStatus.Active
            && item.SourceChunkId == context.SourceChunkId);
        if (existingNote is null)
        {
            await ingest.AddStagingRecordAsync(new IngestStagingRecord
            {
                JobId = context.JobId,
                SourceId = context.SourceId,
                SourceChunkId = context.SourceChunkId,
                SourceChunkIndex = context.SourceChunkIndex,
                Kind = IngestStagingRecordKind.SourceChunkNote,
                Status = IngestStagingRecordStatus.Active,
                Title = context.SourceChunkTitle,
                Summary = sourceChunk.Summary,
                Notes = sourceChunk.AgentNotes,
                EntityType = "SourceChunk",
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
            ingest.UpdateStagingRecord(existingNote);
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

    private async Task UpsertEntityStagingRecordAsync(
        IngestAgentContext context,
        IngestStagingRecord? existing,
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
        var aliasesForPayload = node is null
            ? aliases
            : IngestWikiSheet.ReadAliases(node.Properties)
                .Concat(aliases ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        var payloadJson = BuildEntityReportPayload(existing?.PayloadJson, context, action, aliasesForPayload, wikiSections);
        if (existing is null)
        {
            await ingest.AddStagingRecordAsync(new IngestStagingRecord
            {
                JobId = context.JobId,
                SourceId = context.SourceId,
                SourceChunkId = context.SourceChunkId,
                SourceChunkIndex = context.SourceChunkIndex,
                Kind = IngestStagingRecordKind.Entity,
                Status = IngestStagingRecordStatus.Active,
                Title = title,
                Summary = Truncate(summary, 800),
                Notes = notes?.Trim() ?? string.Empty,
                EntityType = resourceType,
                EntityId = entityId,
                GraphNodeId = node?.Id,
                AliasesJson = SerializeAliases(aliases),
                WikiSectionsJson = SerializeWikiSections(wikiSections),
                PayloadJson = payloadJson,
            });
        }
        else
        {
            existing.Title = title;
            existing.EntityType = resourceType;
            existing.GraphNodeId = node?.Id ?? existing.GraphNodeId;
            existing.Summary = Truncate(summary, 800);
            existing.Notes = notes?.Trim() ?? string.Empty;
            existing.AliasesJson = SerializeAliases(aliases);
            existing.WikiSectionsJson = SerializeWikiSections(wikiSections);
            existing.PayloadJson = payloadJson;
            existing.UpdatedAt = DateTime.UtcNow;
            ingest.UpdateStagingRecord(existing);
        }

        await ingest.SaveChangesAsync();
    }

    private async Task UpsertRelationshipObservationStagingRecordAsync(
        IngestAgentContext context,
        IngestStagingRecord? existing,
        string edgeType,
        Guid from,
        Guid to,
        string fromTitle,
        string toTitle)
    {
        var payloadJson = BuildRelationshipReportPayload(existing?.PayloadJson, context, IngestSourceAssertions.ObservedRelationshipAction, from, to);
        if (existing is null)
        {
            await ingest.AddStagingRecordAsync(new IngestStagingRecord
            {
                JobId = context.JobId,
                SourceId = context.SourceId,
                SourceChunkId = context.SourceChunkId,
                SourceChunkIndex = context.SourceChunkIndex,
                Kind = IngestStagingRecordKind.Relationship,
                Status = IngestStagingRecordStatus.Active,
                Title = $"{fromTitle} -[{edgeType}]-> {toTitle}",
                Summary = string.Empty,
                Notes = string.Empty,
                EdgeType = edgeType,
                FromEntityId = from,
                ToEntityId = to,
                PayloadJson = payloadJson,
            });
        }
        else
        {
            existing.Title = $"{fromTitle} -[{edgeType}]-> {toTitle}";
            existing.EdgeType = edgeType;
            existing.FromEntityId = from;
            existing.ToEntityId = to;
            existing.Summary = string.Empty;
            existing.Notes = string.Empty;
            existing.PayloadJson = payloadJson;
            existing.UpdatedAt = DateTime.UtcNow;
            ingest.UpdateStagingRecord(existing);
        }

        await ingest.SaveChangesAsync();
    }

    private async Task<IngestStagingRecord?> FindActiveEntityStagingRecordAsync(Guid jobId, Guid entityId, Guid? sourceChunkId = null) =>
        (await ingest.ListStagingRecordsAsync(jobId)).FirstOrDefault(item =>
            item.Kind == IngestStagingRecordKind.Entity
            && item.Status == IngestStagingRecordStatus.Active
            && item.EntityId == entityId
            && (sourceChunkId is null || item.SourceChunkId == sourceChunkId));

    private async Task<IngestStagingRecord?> FindActiveRelationshipObservationStagingRecordAsync(
        Guid jobId,
        Guid sourceChunkId,
        Guid from,
        Guid to,
        string edgeType) =>
        (await ingest.ListStagingRecordsAsync(jobId)).FirstOrDefault(item =>
            item.Kind == IngestStagingRecordKind.Relationship
            && item.Status == IngestStagingRecordStatus.Active
            && item.SourceChunkId == sourceChunkId
            && string.Equals(item.EdgeType, edgeType, StringComparison.OrdinalIgnoreCase)
            && TryReadRelationshipEndpoints(item, out var existingFrom, out var existingTo)
            && existingFrom == from
            && existingTo == to);

    private async Task<bool> IsEntityTouchedByJobAsync(Guid jobId, Guid entityId) =>
        (await ingest.ListStagingRecordsAsync(jobId)).Any(item =>
            item.Kind == IngestStagingRecordKind.Entity
            && item.Status == IngestStagingRecordStatus.Active
            && item.EntityId == entityId);

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

    private static string? ValidateEntityObservation(
        string? summary,
        IReadOnlyList<IngestWikiSectionInput>? sections)
    {
        if (string.IsNullOrWhiteSpace(summary) && CountNonEmptySections(sections) == 0)
            return "Error: at least a summary or one non-empty source observation section is required.";

        return null;
    }

    private static int CountNonEmptySections(IEnumerable<IngestWikiSectionInput>? sections) =>
        (sections ?? Enumerable.Empty<IngestWikiSectionInput>())
            .Count(section => section is not null && (!string.IsNullOrWhiteSpace(section.Title) || !string.IsNullOrWhiteSpace(section.Body)));

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
        Guid to)
    {
        var payload = ReadPayload(json);
        var existingAction = ReadString(payload.GetValueOrDefault(IngestSourceAssertions.RelationshipGraphActionProperty));
        payload[IngestSourceAssertions.RelationshipGraphActionProperty] = string.Equals(existingAction, IngestSourceAssertions.CreatedEdgeAction, StringComparison.Ordinal)
            ? IngestSourceAssertions.CreatedEdgeAction
            : action;
        ApplyCommonPayload(payload, context);
        payload["fromEntityId"] = from;
        payload["toEntityId"] = to;
        payload.Remove("citationCount");
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

    private static string SerializeAliases(IEnumerable<string>? aliases) =>
        JsonSerializer.Serialize((aliases ?? [])
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Select(alias => alias.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray());

    private static string SerializeWikiSections(IEnumerable<IngestWikiSectionInput>? sections) =>
        JsonSerializer.Serialize((sections ?? [])
            .Where(section => section is not null
                && (!string.IsNullOrWhiteSpace(section.Title) || !string.IsNullOrWhiteSpace(section.Body)))
            .ToArray());

    private static IReadOnlyList<string> ReadAliasesJson(string? aliasesJson)
    {
        if (string.IsNullOrWhiteSpace(aliasesJson) || !LooksLikeJsonRoot(aliasesJson, '[')) return [];
        try
        {
            return (JsonSerializer.Deserialize<string[]>(aliasesJson) ?? [])
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .Select(alias => alias.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<IngestWikiSectionInput> ReadWikiSectionInputsJson(string? sectionsJson)
    {
        if (string.IsNullOrWhiteSpace(sectionsJson) || !LooksLikeJsonRoot(sectionsJson, '[')) return [];
        try
        {
            return (JsonSerializer.Deserialize<List<IngestWikiSectionInput>>(sectionsJson) ?? [])
                .Where(section => section is not null
                    && (!string.IsNullOrWhiteSpace(section.Title) || !string.IsNullOrWhiteSpace(section.Body)))
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task<int> CountActiveEntityStagingRecordsAsync(Guid jobId, Guid entityId) =>
        (await ingest.ListStagingRecordsAsync(jobId))
            .Count(item => item.Kind == IngestStagingRecordKind.Entity
                && item.Status == IngestStagingRecordStatus.Active
                && item.EntityId == entityId);

    private static bool TryReadRelationshipEndpoints(IngestStagingRecord item, out Guid from, out Guid to)
    {
        from = item.FromEntityId ?? Guid.Empty;
        to = item.ToEntityId ?? Guid.Empty;
        return (from != Guid.Empty && to != Guid.Empty)
            || TryReadRelationshipEndpoints(item.PayloadJson, out from, out to);
    }

    private static bool TryReadRelationshipEndpoints(string payloadJson, out Guid from, out Guid to)
    {
        from = Guid.Empty;
        to = Guid.Empty;
        if (string.IsNullOrWhiteSpace(payloadJson) || !LooksLikeJsonRoot(payloadJson, '{')) return false;

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty("fromEntityId", out var fromProperty)
                && document.RootElement.TryGetProperty("toEntityId", out var toProperty)
                && Guid.TryParse(fromProperty.ValueKind == JsonValueKind.String ? fromProperty.GetString() : fromProperty.GetRawText(), out from)
                && Guid.TryParse(toProperty.ValueKind == JsonValueKind.String ? toProperty.GetString() : toProperty.GetRawText(), out to);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool PayloadContainsSource(string payloadJson, Guid sourceId)
    {
        if (string.IsNullOrWhiteSpace(payloadJson) || !LooksLikeJsonRoot(payloadJson, '{')) return false;
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty("sourceId", out var sourceProperty)
                && Guid.TryParse(sourceProperty.ValueKind == JsonValueKind.String ? sourceProperty.GetString() : sourceProperty.GetRawText(), out var parsed)
                && parsed == sourceId;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int ReadIntPayload(string payloadJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(payloadJson) || !LooksLikeJsonRoot(payloadJson, '{')) return -1;
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
                ? value
                : -1;
        }
        catch (JsonException)
        {
            return -1;
        }
    }

    private static string[] SectionTitles(IReadOnlyList<IngestWikiSectionInput>? sections) =>
        (sections ?? [])
            .Where(section => section is not null)
            .Select(section => NormalizeText(section.Title).Trim())
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static int ScoreIdentityCandidate(GraphNode node, string queryText)
    {
        var queryVariants = BuildEntityNameVariants(queryText, ShouldStripEntityTitles(node.NodeType));
        if (queryVariants.Count == 0) return 0;

        var candidateInputs = CandidateNameInputs(node).ToList();
        var candidateVariants = candidateInputs
            .SelectMany(name => BuildEntityNameVariants(name, ShouldStripEntityTitles(node.NodeType)))
            .ToHashSet(StringComparer.Ordinal);

        var score = 0;
        foreach (var queryVariant in queryVariants)
        {
            if (candidateVariants.Contains(queryVariant)) score += 25;
            else if (candidateVariants.Any(candidate => candidate.Contains(queryVariant, StringComparison.Ordinal) || queryVariant.Contains(candidate, StringComparison.Ordinal))) score += 8;
        }

        var identityHaystack = NormalizeComparable(string.Join("\n", candidateInputs));
        foreach (var term in SplitSearchTerms(queryText))
        {
            if (identityHaystack.Contains(NormalizeComparable(term), StringComparison.Ordinal))
                score += 2;
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

    private static string TruncateWords(string value, int maxWords)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var words = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length <= maxWords) return value.Trim();
        return string.Join(' ', words.Take(maxWords)) + "...";
    }

    private static string NormalizeSourceChunkSummary(string? value) =>
        Truncate(TruncateWords(NormalizeText(value).Trim(), 120), 900);

    private static string NormalizeSourceChunkNotes(string? value)
    {
        var normalized = NormalizeText(value).Trim();
        if (normalized.Length == 0) return string.Empty;

        var firstLine = normalized
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;
        if (firstLine.Length == 0) return string.Empty;

        var sentenceEnd = firstLine.IndexOfAny(['.', '!', '?']);
        var sentence = sentenceEnd >= 0 ? firstLine[..(sentenceEnd + 1)] : firstLine;
        return Truncate(TruncateWords(sentence, 40), 280);
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

    private sealed record ProjectEntityIdentity(
        Guid Id,
        string Type,
        string Name,
        IReadOnlyList<string> Aliases);

    private sealed record ProjectEntityIdentityCandidate(
        Guid Id,
        string Type,
        string Name,
        IReadOnlyList<string> Aliases,
        int Score);

    private sealed record ObservationEntityResolution(
        Guid EntityId,
        GraphNode? Node,
        string Action,
        bool Created,
        bool ExactNameMatch,
        string? Error)
    {
        public static ObservationEntityResolution Resolved(Guid entityId, GraphNode node, string action, bool created, bool exactNameMatch) =>
            new(entityId, node, action, created, exactNameMatch, Error: null);

        public static ObservationEntityResolution Failed(string error) =>
            new(Guid.Empty, Node: null, string.Empty, Created: false, ExactNameMatch: false, error);
    }

    private sealed record EntityTypeResolution(string Type, bool ExistingType);
}

public sealed class IngestEntityMentionInput
{
    [Description("Likely broad entity type such as Character, Location, Faction, Concept, Artifact, Lore, Person, Event, Claim, Term, or Method.")]
    public string? Type { get; set; }

    [Description("Exact entity mention from the source chunk.")]
    public string? Mention { get; set; }

    [Description("Optional search variants such as base name without titles, surnames, epithets, aliases, or alternate spellings.")]
    public string[]? Variants { get; set; }
}
