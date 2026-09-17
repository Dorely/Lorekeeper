using System.Security.Cryptography;
using Lorekeeper.Context;
using Lorekeeper.Images;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.EntityVisuals;

public sealed class EntityVisualExampleService(IAppDatabaseOperationFactory database, IContextIndexingService contextIndexing) : IEntityVisualExampleService
{
    public async Task<EntityVisualExampleView?> GetAsync(Guid projectId, Guid exampleId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var example = await QueryExamples(databaseOperation.Db, projectId)
            .FirstOrDefaultAsync(item => item.Id == exampleId, cancellationToken);
        return example is null ? null : ToView(projectId, example);
    }

    public async Task<IReadOnlyList<EntityVisualExampleView>> ListForEntityAsync(Guid projectId, Guid entityId, CancellationToken cancellationToken = default)
    {
        var node = await FindEntityNodeAsync(projectId, entityId, cancellationToken);
        if (node is null) return [];
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var examples = await QueryExamples(databaseOperation.Db, projectId)
            .Where(example => example.GraphNodeId == node.Id)
            .ToListAsync(cancellationToken);
        return examples.Select(example => ToView(projectId, example)).ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<EntityVisualExampleView>>> ListForEntitiesAsync(
        Guid projectId,
        IReadOnlyCollection<Guid> entityIds,
        CancellationToken cancellationToken = default)
    {
        if (entityIds.Count == 0) return new Dictionary<Guid, IReadOnlyList<EntityVisualExampleView>>();
        var keys = entityIds.Distinct().Select(id => id.ToString("N")).ToList();
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var rows = await QueryExamples(databaseOperation.Db, projectId)
            .Where(example => keys.Contains(example.GraphNode.Key))
            .ToListAsync(cancellationToken);
        var examples = rows.Select(example => ToView(projectId, example)).ToList();
        return examples.GroupBy(example => example.EntityId).ToDictionary(group => group.Key, group => (IReadOnlyList<EntityVisualExampleView>)group.OrderBy(item => item.SortOrder).ToList());
    }

    public async Task<IReadOnlyList<EntityVisualExampleView>> ListForImageAsync(Guid projectId, Guid imageId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var examples = await QueryExamples(databaseOperation.Db, projectId)
            .Where(example => example.ImageId == imageId)
            .ToListAsync(cancellationToken);
        return examples.Select(example => ToView(projectId, example)).ToList();
    }

    public async Task<EntityVisualTargetValidationResult> ValidateTargetsAsync(
        Guid projectId,
        IReadOnlyCollection<EntityVisualTarget>? targets,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var normalized = (targets ?? [])
            .Where(target => target.EntityId != Guid.Empty)
            .Select(target => new EntityVisualTarget(target.EntityId, target.Label?.Trim() ?? string.Empty))
            .DistinctBy(target => target.EntityId)
            .ToList();
        var errors = new List<string>();
        foreach (var target in normalized)
        {
            var key = target.EntityId.ToString("N");
            var node = await db.GraphNodes.AsNoTracking().FirstOrDefaultAsync(
                candidate => candidate.ProjectId == projectId && candidate.Key == key,
                cancellationToken);
            if (node is null)
            {
                errors.Add($"Entity target {target.EntityId} was not found in this project.");
                continue;
            }
            if (!IsEligible(node))
                errors.Add($"Entity target {target.EntityId} ({node.NodeType}) is not eligible for canonical visual references.");
        }

        return new EntityVisualTargetValidationResult(
            normalized,
            errors.Count == 0 ? null : string.Join(" ", errors));
    }

    public async Task<EntityVisualExampleView> AttachAsync(
        Guid projectId,
        Guid entityId,
        Guid imageId,
        string? label = null,
        EntityVisualExampleOrigin origin = EntityVisualExampleOrigin.Manual,
        Guid? sourceVisualCandidateId = null,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var node = await GetRequiredEntityNodeAsync(projectId, entityId, cancellationToken);
        var image = await db.PublishAssets.FirstOrDefaultAsync(asset => asset.ProjectId == projectId && asset.Id == imageId, cancellationToken)
            ?? throw new InvalidOperationException($"Image {imageId} was not found in this project.");
        if (sourceVisualCandidateId is Guid candidateId
            && !await db.SourceVisualCandidates.AnyAsync(candidate => candidate.ProjectId == projectId && candidate.Id == candidateId, cancellationToken))
            throw new InvalidOperationException("Source visual candidate was not found in this project.");

        var existing = await db.EntityVisualExamples.Include(example => example.GraphNode).Include(example => example.Image)
            .FirstOrDefaultAsync(example => example.GraphNodeId == node.Id && example.ImageId == imageId, cancellationToken);
        if (existing is not null)
        {
            if (!string.IsNullOrWhiteSpace(label)) existing.Label = label.Trim();
            if (origin == EntityVisualExampleOrigin.Manual || existing.Origin != EntityVisualExampleOrigin.Manual)
                existing.Origin = origin;
            existing.SourceVisualCandidateId = sourceVisualCandidateId ?? existing.SourceVisualCandidateId;
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await contextIndexing.ReindexEntityAsync(projectId, entityId, cancellationToken);
            return ToView(projectId, existing);
        }

        var nextOrder = await db.EntityVisualExamples.Where(example => example.GraphNodeId == node.Id).Select(example => (int?)example.SortOrder).MaxAsync(cancellationToken) ?? -1;
        var created = new EntityVisualExample
        {
            ProjectId = projectId,
            GraphNodeId = node.Id,
            GraphNode = node,
            ImageId = imageId,
            Image = image,
            Label = CleanLabel(label, image),
            SortOrder = nextOrder + 1,
            Origin = origin,
            SourceVisualCandidateId = sourceVisualCandidateId,
        };
        await db.EntityVisualExamples.AddAsync(created, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await contextIndexing.ReindexEntityAsync(projectId, entityId, cancellationToken);
        return ToView(projectId, created);
    }

    public async Task<EntityVisualExampleView> UpdateAsync(Guid projectId, Guid exampleId, string label, int? sortOrder = null, EntityVisualExampleOrigin? origin = null, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var example = await QueryExamples(db, projectId, tracking: true)
            .FirstOrDefaultAsync(item => item.Id == exampleId, cancellationToken)
            ?? throw new InvalidOperationException("Entity canonical visual reference was not found.");
        example.Label = label?.Trim() ?? string.Empty;
        if (sortOrder is int requestedOrder)
        {
            var siblings = await db.EntityVisualExamples
                .Where(item => item.GraphNodeId == example.GraphNodeId)
                .OrderBy(item => item.SortOrder)
                .ThenBy(item => item.CreatedAt)
                .ToListAsync(cancellationToken);
            siblings.RemoveAll(item => item.Id == example.Id);
            siblings.Insert(Math.Clamp(requestedOrder, 0, siblings.Count), example);
            for (var index = 0; index < siblings.Count; index++) siblings[index].SortOrder = index;
        }
        if (origin is not null) example.Origin = origin.Value;
        example.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        var entityId = Guid.ParseExact(example.GraphNode.Key, "N");
        await contextIndexing.ReindexEntityAsync(projectId, entityId, cancellationToken);
        return ToView(projectId, example);
    }

    public async Task ReorderAsync(Guid projectId, Guid entityId, IReadOnlyList<Guid> orderedExampleIds, bool markManual = true, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var node = await GetRequiredEntityNodeAsync(projectId, entityId, cancellationToken);
        var examples = await db.EntityVisualExamples.Where(example => example.GraphNodeId == node.Id).ToListAsync(cancellationToken);
        if (!orderedExampleIds.ToHashSet().SetEquals(examples.Select(example => example.Id)))
            throw new InvalidOperationException("Reorder list must contain every canonical visual reference exactly once.");
        var byId = examples.ToDictionary(example => example.Id);
        for (var index = 0; index < orderedExampleIds.Count; index++)
        {
            var example = byId[orderedExampleIds[index]];
            example.SortOrder = index;
            if (markManual) example.Origin = EntityVisualExampleOrigin.Manual;
            example.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);
        await contextIndexing.ReindexEntityAsync(projectId, entityId, cancellationToken);
    }

    public async Task DetachAsync(Guid projectId, Guid exampleId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var example = await db.EntityVisualExamples.Include(item => item.GraphNode).FirstOrDefaultAsync(item => item.ProjectId == projectId && item.Id == exampleId, cancellationToken);
        if (example is null) return;
        var entityId = Guid.ParseExact(example.GraphNode.Key, "N");
        var remaining = await db.EntityVisualExamples
            .Where(item => item.GraphNodeId == example.GraphNodeId && item.Id != example.Id)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.CreatedAt)
            .ToListAsync(cancellationToken);
        for (var index = 0; index < remaining.Count; index++)
            remaining[index].SortOrder = index;
        db.EntityVisualExamples.Remove(example);
        await db.SaveChangesAsync(cancellationToken);
        await contextIndexing.ReindexEntityAsync(projectId, entityId, cancellationToken);
    }

    public async Task<SourceVisualCandidateView> CreateCandidateAsync(SourceVisualCandidateCreateRequest request, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        _ = await db.Projects.AsNoTracking().FirstOrDefaultAsync(project => project.Id == request.ProjectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {request.ProjectId} was not found.");
        var normalized = ProjectImageBinary.Normalize(request.Data, request.ContentType, request.FileName);
        var hash = Convert.ToHexString(SHA256.HashData(normalized.Data)).ToLowerInvariant();
        var existing = await db.SourceVisualCandidates.FirstOrDefaultAsync(candidate =>
            candidate.ProjectId == request.ProjectId
            && candidate.Kind == request.Kind
            && candidate.IngestSourceId == request.IngestSourceId
            && candidate.WebIngestCandidateId == request.WebIngestCandidateId
            && candidate.ContentHash == hash,
            cancellationToken);
        if (existing is not null) return ToView(existing);

        var candidate = new SourceVisualCandidate
        {
            ProjectId = request.ProjectId,
            Kind = request.Kind,
            Status = SourceVisualCandidateStatus.Inspected,
            IngestSourceId = request.IngestSourceId,
            WebIngestCandidateId = request.WebIngestCandidateId,
            FileName = normalized.FileName,
            ContentType = normalized.ContentType,
            Data = normalized.Data,
            AltText = request.AltText.Trim(),
            Caption = request.Caption.Trim(),
            SourceUrl = request.SourceUrl.Trim(),
            Locator = request.Locator.Trim(),
            MetadataJson = string.IsNullOrWhiteSpace(request.MetadataJson) ? "{}" : request.MetadataJson,
            ContentHash = hash,
            StartChar = request.StartChar,
            EndChar = request.EndChar,
        };
        await db.SourceVisualCandidates.AddAsync(candidate, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return ToView(candidate);
    }

    public async Task<SourceVisualCandidateView?> GetCandidateAsync(Guid projectId, Guid candidateId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var candidate = await db.SourceVisualCandidates.AsNoTracking().Include(item => item.EntityVisualExamples).FirstOrDefaultAsync(item => item.ProjectId == projectId && item.Id == candidateId, cancellationToken);
        return candidate is null ? null : ToView(candidate);
    }

    public async Task<SourceVisualCandidateData?> GetCandidateDataAsync(Guid projectId, Guid candidateId, int? maxEdge = null, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var candidate = await db.SourceVisualCandidates.AsNoTracking().FirstOrDefaultAsync(item => item.ProjectId == projectId && item.Id == candidateId, cancellationToken);
        if (candidate is null) return null;
        var data = maxEdge is int edge && edge > 0 ? ProjectImageResize.Resize(candidate.Data, candidate.ContentType, edge) : candidate.Data;
        return new SourceVisualCandidateData(candidate.Id, candidate.FileName, candidate.ContentType, data, candidate.AltText);
    }

    public async Task<IReadOnlyList<SourceVisualCandidateView>> ListIngestCandidatesAsync(Guid projectId, Guid ingestSourceId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var candidates = await db.SourceVisualCandidates.AsNoTracking().Include(candidate => candidate.EntityVisualExamples).Where(candidate => candidate.ProjectId == projectId && candidate.IngestSourceId == ingestSourceId).OrderBy(candidate => candidate.StartChar).ThenBy(candidate => candidate.CreatedAt).ToListAsync(cancellationToken);
        return candidates.Select(ToView).ToList();
    }

    public async Task<IReadOnlyList<SourceVisualCandidateView>> ListWebCandidatesAsync(Guid projectId, Guid webCandidateId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var candidates = await db.SourceVisualCandidates.AsNoTracking().Include(candidate => candidate.EntityVisualExamples).Where(candidate => candidate.ProjectId == projectId && candidate.WebIngestCandidateId == webCandidateId).OrderBy(candidate => candidate.CreatedAt).ToListAsync(cancellationToken);
        return candidates.Select(ToView).ToList();
    }

    public async Task<ProjectImageView> PromoteCandidateAsync(Guid projectId, Guid candidateId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var candidate = await db.SourceVisualCandidates.FirstOrDefaultAsync(item => item.ProjectId == projectId && item.Id == candidateId, cancellationToken)
            ?? throw new InvalidOperationException("Source visual candidate was not found.");
        if (candidate.Data.Length == 0) throw new InvalidOperationException("Source visual candidate has no cached image data.");
        var imageId = candidate.PromotedImageId;
        var image = imageId is Guid existingImageId
            ? await db.PublishAssets.FirstOrDefaultAsync(asset => asset.ProjectId == projectId && asset.Id == existingImageId, cancellationToken)
            : null;
        if (image is null)
        {
            image = new PublishAsset
            {
                ProjectId = projectId,
                Source = PublishAssetSource.Imported,
                FileName = candidate.FileName,
                ContentType = candidate.ContentType,
                Data = candidate.Data,
                AltText = string.IsNullOrWhiteSpace(candidate.AltText) ? candidate.Caption : candidate.AltText,
                SourceMetadataJson = candidate.MetadataJson,
            };
            await db.PublishAssets.AddAsync(image, cancellationToken);
            candidate.PromotedImageId = image.Id;
            candidate.Status = SourceVisualCandidateStatus.Promoted;
            candidate.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }

        return ProjectImageService.ToView(projectId, image);
    }

    public async Task<SourceVisualCandidateView> SetCandidateStatusAsync(
        Guid projectId,
        Guid candidateId,
        SourceVisualCandidateStatus status,
        string? errorMessage = null,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var candidate = await db.SourceVisualCandidates.Include(item => item.EntityVisualExamples)
            .FirstOrDefaultAsync(item => item.ProjectId == projectId && item.Id == candidateId, cancellationToken)
            ?? throw new InvalidOperationException("Source visual candidate was not found.");
        candidate.Status = status;
        candidate.ErrorMessage = errorMessage?.Trim() ?? string.Empty;
        candidate.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return ToView(candidate);
    }

    public async Task RemoveIngestOwnedAsync(Guid projectId, Guid ingestSourceId, bool deleteCandidates = true, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var candidates = await db.SourceVisualCandidates.Where(candidate => candidate.ProjectId == projectId && candidate.IngestSourceId == ingestSourceId).ToListAsync(cancellationToken);
        var candidateIds = candidates.Select(candidate => candidate.Id).ToList();
        var examples = await db.EntityVisualExamples.Include(example => example.GraphNode)
            .Where(example => example.ProjectId == projectId && example.Origin == EntityVisualExampleOrigin.Ingest && example.SourceVisualCandidateId != null && candidateIds.Contains(example.SourceVisualCandidateId.Value))
            .ToListAsync(cancellationToken);
        var entityIds = examples.Select(example => Guid.ParseExact(example.GraphNode.Key, "N")).Distinct().ToList();
        var promotedImageIds = candidates
            .Where(candidate => candidate.PromotedImageId is not null)
            .Select(candidate => candidate.PromotedImageId!.Value)
            .Distinct()
            .ToList();
        var derivedCropImageIds = await db.PublishAssets
            .AsNoTracking()
            .Where(asset => asset.ProjectId == projectId
                && asset.DerivedFromImageId != null
                && promotedImageIds.Contains(asset.DerivedFromImageId.Value))
            .Select(asset => asset.Id)
            .ToListAsync(cancellationToken);
        var candidateImageIds = examples
            .Select(example => example.ImageId)
            .Concat(promotedImageIds)
            .Concat(derivedCropImageIds)
            .Distinct()
            .ToList();
        db.EntityVisualExamples.RemoveRange(examples);
        await db.SaveChangesAsync(cancellationToken);

        var candidateAssets = await db.PublishAssets
            .Where(asset => asset.ProjectId == projectId && candidateImageIds.Contains(asset.Id))
            .OrderByDescending(asset => asset.DerivedFromImageId != null)
            .ToListAsync(cancellationToken);
        foreach (var asset in candidateAssets)
        {
            if (!await IsImageOtherwiseReferencedAsync(projectId, asset.Id, cancellationToken))
            {
                db.PublishAssets.Remove(asset);
                foreach (var candidate in candidates.Where(candidate => candidate.PromotedImageId == asset.Id))
                {
                    candidate.PromotedImageId = null;
                    candidate.Status = SourceVisualCandidateStatus.Inspected;
                    candidate.UpdatedAt = DateTime.UtcNow;
                }
            }
        }
        if (deleteCandidates) db.SourceVisualCandidates.RemoveRange(candidates);
        await db.SaveChangesAsync(cancellationToken);
        foreach (var entityId in entityIds)
            await contextIndexing.ReindexEntityAsync(projectId, entityId, cancellationToken);
    }

    private async Task<bool> IsImageOtherwiseReferencedAsync(Guid projectId, Guid imageId, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        if (await db.EntityVisualExamples.AnyAsync(example => example.ProjectId == projectId && example.ImageId == imageId, cancellationToken)) return true;
        if (await db.ProjectImageMasks.AnyAsync(mask => mask.ProjectId == projectId && mask.ImageId == imageId, cancellationToken)) return true;
        var idN = imageId.ToString("N");
        var idD = imageId.ToString();
        if (await db.EditorContextPreferences.AnyAsync(preference => preference.ProjectId == projectId && (preference.Key.Contains(idN) || preference.Key.Contains(idD)), cancellationToken)) return true;
        if (await db.Chapters.AnyAsync(chapter => chapter.ProjectId == projectId
            && (chapter.ManuscriptJson.Contains(idN) || chapter.ManuscriptJson.Contains(idD)), cancellationToken)) return true;
        if (await db.DesignedPageVariants.AnyAsync(variant => variant.Content.ProjectId == projectId
            && (variant.SceneJson.Contains(idN) || variant.SceneJson.Contains(idD)), cancellationToken)) return true;
        if (await db.PublicationCoverDesigns.AnyAsync(cover => cover.Edition.ProjectId == projectId
            && (cover.CompositionSceneJson.Contains(idN) || cover.CompositionSceneJson.Contains(idD)), cancellationToken)) return true;
        if (await db.PublicationMatter.AnyAsync(matter => matter.Edition.ProjectId == projectId
            && (matter.ManuscriptJson.Contains(idN) || matter.ManuscriptJson.Contains(idD)), cancellationToken)) return true;
        if (await db.PublicationImagePlacements.AnyAsync(placement =>
            placement.Edition.ProjectId == projectId && placement.AssetId == imageId,
            cancellationToken)) return true;
        return await db.ProjectImageGenerationJobs.AnyAsync(job => job.ProjectId == projectId
            && (job.SourceImageId == imageId || job.OutputImageIdsJson.Contains(idN) || job.OutputImageIdsJson.Contains(idD)
                || job.ReferenceImageIdsJson.Contains(idN) || job.ReferenceImageIdsJson.Contains(idD)), cancellationToken);
    }

    private static IQueryable<EntityVisualExample> QueryExamples(
        AppDbContext db,
        Guid projectId,
        bool tracking = false)
    {
        var query = db.EntityVisualExamples.Include(example => example.GraphNode).Include(example => example.Image).Where(example => example.ProjectId == projectId).OrderBy(example => example.SortOrder).ThenBy(example => example.CreatedAt);
        return tracking ? query : query.AsNoTracking();
    }

    private async Task<GraphNode?> FindEntityNodeAsync(Guid projectId, Guid entityId, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var node = await db.GraphNodes.FirstOrDefaultAsync(candidate => candidate.ProjectId == projectId && candidate.Key == entityId.ToString("N"), cancellationToken);
        return node is not null && IsEligible(node) ? node : null;
    }

    private async Task<GraphNode> GetRequiredEntityNodeAsync(Guid projectId, Guid entityId, CancellationToken cancellationToken) =>
        await FindEntityNodeAsync(projectId, entityId, cancellationToken)
        ?? throw new InvalidOperationException($"Entity {entityId} is not an eligible non-structural project entity.");

    public static bool IsEligible(GraphNode node) =>
        Guid.TryParseExact(node.Key, "N", out _)
        && !string.Equals(node.NodeType, EntityTypeService.ProjectNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(node.NodeType, EntityTypeService.ActNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(node.NodeType, EntityTypeService.ChapterNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(node.NodeType, EntityTypeService.ProjectFactNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(node.NodeType, EntityTypeService.SourceNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(node.NodeType, EntityTypeService.SourceChunkNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(node.NodeType, EntityTypeService.SourceBlockNodeType, StringComparison.OrdinalIgnoreCase);

    private static string CleanLabel(string? label, PublishAsset image) =>
        !string.IsNullOrWhiteSpace(label) ? label.Trim() : !string.IsNullOrWhiteSpace(image.AltText) ? image.AltText.Trim() : image.FileName;

    private static EntityVisualExampleView ToView(Guid projectId, EntityVisualExample example) => new(
        example.Id,
        Guid.ParseExact(example.GraphNode.Key, "N"),
        example.GraphNode.NodeType,
        example.GraphNode.Label ?? example.GraphNode.Key,
        example.Label,
        example.SortOrder,
        example.Origin,
        example.SourceVisualCandidateId,
        ProjectImageService.ToView(projectId, example.Image));

    private static SourceVisualCandidateView ToView(SourceVisualCandidate candidate) => new(
        candidate.Id,
        candidate.ProjectId,
        candidate.Kind,
        candidate.Status,
        candidate.IngestSourceId,
        candidate.WebIngestCandidateId,
        candidate.FileName,
        candidate.ContentType,
        $"/projects/{candidate.ProjectId:N}/source-visuals/{candidate.Id:N}/content?maxEdge=640",
        candidate.AltText,
        candidate.Caption,
        candidate.SourceUrl,
        candidate.Locator,
        candidate.MetadataJson,
        candidate.ContentHash,
        candidate.StartChar,
        candidate.EndChar,
        candidate.PromotedImageId,
        candidate.EntityVisualExamples.Count,
        candidate.ErrorMessage,
        candidate.CreatedAt,
        candidate.UpdatedAt);
}
