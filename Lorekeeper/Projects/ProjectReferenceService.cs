using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence;
using Lorekeeper.Context;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Projects;

public sealed class ProjectReferenceService(
    IAppDatabaseOperationFactory database,
    IContextIndexingService contextIndexing,
    ILogger<ProjectReferenceService> logger) : IProjectReferenceService
{
    public async Task<IReadOnlyList<ProjectReferenceSummary>> ListAsync(
        Guid referencingProjectId,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        _ = await operation.Repositories.Projects.GetSnapshotByIdAsync(referencingProjectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {referencingProjectId} not found.");
        var references = await operation.Repositories.ProjectReferences
            .ListByReferencingProjectAsync(referencingProjectId, cancellationToken);

        return references
            .Select(ToSummary)
            .ToList();
    }

    public async Task<IReadOnlyList<ProjectReferenceCandidate>> ListCandidatesAsync(
        Guid referencingProjectId,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        var source = await operation.Repositories.Projects.GetSnapshotByIdAsync(referencingProjectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {referencingProjectId} not found.");
        var projects = await operation.Repositories.Projects.ListAsync(cancellationToken);
        var referencedIds = (await operation.Repositories.ProjectReferences
                .ListByReferencingProjectAsync(referencingProjectId, cancellationToken))
            .Select(reference => reference.ReferencedProjectId)
            .ToHashSet();

        return projects
            .Where(project => project.Id != source.Id)
            .Select(project => new ProjectReferenceCandidate(
                project.Id,
                project.Name,
                project.Slug,
                project.UpdatedAt,
                referencedIds.Contains(project.Id)))
            .ToList();
    }

    public async Task<ProjectReferenceSummary> AddAsync(
        Guid referencingProjectId,
        Guid referencedProjectId,
        CancellationToken cancellationToken = default)
    {
        if (referencingProjectId == Guid.Empty)
            throw new ArgumentException("Referencing project id is required.", nameof(referencingProjectId));
        if (referencedProjectId == Guid.Empty)
            throw new ArgumentException("Referenced project id is required.", nameof(referencedProjectId));
        if (referencingProjectId == referencedProjectId)
            throw new InvalidOperationException("A project cannot reference itself.");

        await using var operation = await database.OpenWriteAsync(referencingProjectId, cancellationToken);
        operation.ShareWithNestedOperations();
        var projects = operation.Repositories.Projects;
        var references = operation.Repositories.ProjectReferences;
        var source = await projects.GetByIdAsync(referencingProjectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {referencingProjectId} not found.");
        var target = await projects.GetSnapshotByIdAsync(referencedProjectId, cancellationToken)
            ?? throw new InvalidOperationException($"Referenced project {referencedProjectId} not found.");
        if (await references.GetAsync(referencingProjectId, referencedProjectId, cancellationToken) is not null)
            throw new InvalidOperationException($"Project {referencingProjectId} already references project {referencedProjectId}.");

        var reference = new ProjectReference
        {
            ReferencingProjectId = referencingProjectId,
            ReferencedProjectId = referencedProjectId,
        };
        await references.AddAsync(reference, cancellationToken);
        source.UpdatedAt = DateTime.UtcNow;
        projects.Update(source);
        await operation.SaveChangesAsync(cancellationToken);

        await BackfillReferencedIndexesAsync(target.Id, operation, cancellationToken);

        return new ProjectReferenceSummary(
            target.Id,
            target.Name,
            target.Slug,
            target.UpdatedAt,
            reference.CreatedAt);
    }

    public async Task RemoveAsync(
        Guid referencingProjectId,
        Guid referencedProjectId,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenWriteAsync(referencingProjectId, cancellationToken);
        operation.ShareWithNestedOperations();
        var references = operation.Repositories.ProjectReferences;
        var reference = await references.GetAsync(referencingProjectId, referencedProjectId, cancellationToken);
        if (reference is null)
            return;

        references.Remove(reference);
        var source = await operation.Repositories.Projects.GetByIdAsync(referencingProjectId, cancellationToken);
        if (source is not null)
        {
            source.UpdatedAt = DateTime.UtcNow;
            operation.Repositories.Projects.Update(source);
        }

        await operation.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ProjectReadableScope>> ListReadableScopesAsync(
        Guid referencingProjectId,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        var source = await operation.Repositories.Projects.GetSnapshotByIdAsync(referencingProjectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {referencingProjectId} not found.");
        var references = await operation.Repositories.ProjectReferences
            .ListByReferencingProjectAsync(referencingProjectId, cancellationToken);

        return [
            new ProjectReadableScope(source.Id, source.Name, source.Slug, IsReferenced: false),
            .. references.Select(reference => new ProjectReadableScope(
                reference.ReferencedProjectId,
                reference.ReferencedProject.Name,
                reference.ReferencedProject.Slug,
                IsReferenced: true)),
        ];
    }

    public async Task<IReadOnlyList<ProjectReferenceManifest>> ListReferenceManifestsAsync(
        Guid referencingProjectId,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        _ = await operation.Repositories.Projects.GetSnapshotByIdAsync(referencingProjectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {referencingProjectId} not found.");

        var references = await operation.Repositories.ProjectReferences
            .ListByReferencingProjectAsync(referencingProjectId, cancellationToken);
        if (references.Count == 0)
            return [];

        var referencedIds = references
            .Select(reference => reference.ReferencedProjectId)
            .ToArray();
        var db = operation.Db;
        var excludedStructuralTypes = new[]
        {
            EntityTypeService.ProjectNodeType,
            EntityTypeService.ActNodeType,
            EntityTypeService.ChapterNodeType,
            EntityTypeService.ProjectFactNodeType,
            EntityTypeService.SourceNodeType,
            EntityTypeService.SourceChunkNodeType,
            EntityTypeService.SourceBlockNodeType,
        };

        var actCounts = await db.Acts
            .AsNoTracking()
            .Where(act => referencedIds.Contains(act.ProjectId))
            .GroupBy(act => act.ProjectId)
            .Select(group => new { ProjectId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.ProjectId, item => item.Count, cancellationToken);
        var chapterCounts = await db.Chapters
            .AsNoTracking()
            .Where(chapter => referencedIds.Contains(chapter.ProjectId))
            .GroupBy(chapter => chapter.ProjectId)
            .Select(group => new { ProjectId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.ProjectId, item => item.Count, cancellationToken);
        var searchableEntityCounts = await db.GraphNodes
            .AsNoTracking()
            .Where(node => referencedIds.Contains(node.ProjectId) && !excludedStructuralTypes.Contains(node.NodeType))
            .GroupBy(node => node.ProjectId)
            .Select(group => new { ProjectId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.ProjectId, item => item.Count, cancellationToken);
        var factCounts = await db.GraphNodes
            .AsNoTracking()
            .Where(node => referencedIds.Contains(node.ProjectId)
                && node.NodeType == EntityTypeService.ProjectFactNodeType)
            .GroupBy(node => node.ProjectId)
            .Select(group => new { ProjectId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.ProjectId, item => item.Count, cancellationToken);
        var writingSampleCounts = await db.WritingSamples
            .AsNoTracking()
            .Where(sample => referencedIds.Contains(sample.ProjectId))
            .GroupBy(sample => sample.ProjectId)
            .Select(group => new { ProjectId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.ProjectId, item => item.Count, cancellationToken);
        var canonicalSourceCounts = await db.BookBriefCanonSources
            .AsNoTracking()
            .Where(selection => referencedIds.Contains(selection.BookBrief.ProjectId))
            .GroupBy(selection => selection.BookBrief.ProjectId)
            .Select(group => new { ProjectId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.ProjectId, item => item.Count, cancellationToken);
        var canonicalVisualCounts = await db.EntityVisualExamples
            .AsNoTracking()
            .Where(example => referencedIds.Contains(example.ProjectId))
            .GroupBy(example => example.ProjectId)
            .Select(group => new { ProjectId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.ProjectId, item => item.Count, cancellationToken);
        var briefs = await db.BookBriefs
            .AsNoTracking()
            .Where(brief => referencedIds.Contains(brief.ProjectId))
            .ToDictionaryAsync(brief => brief.ProjectId, cancellationToken);

        return references
            .Select(reference =>
            {
                var project = reference.ReferencedProject;
                briefs.TryGetValue(project.Id, out var brief);
                return new ProjectReferenceManifest(
                    project.Id,
                    project.Name,
                    project.Slug,
                    project.UpdatedAt,
                    brief?.Premise ?? string.Empty,
                    brief?.Genre ?? string.Empty,
                    actCounts.GetValueOrDefault(project.Id),
                    chapterCounts.GetValueOrDefault(project.Id),
                    searchableEntityCounts.GetValueOrDefault(project.Id),
                    factCounts.GetValueOrDefault(project.Id),
                    writingSampleCounts.GetValueOrDefault(project.Id),
                    canonicalSourceCounts.GetValueOrDefault(project.Id),
                    canonicalVisualCounts.GetValueOrDefault(project.Id));
            })
            .ToList();
    }

    public async Task<IReadOnlyList<ProjectReferenceImpact>> ListIncomingAsync(
        Guid referencedProjectId,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        _ = await operation.Repositories.Projects.GetSnapshotByIdAsync(referencedProjectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {referencedProjectId} not found.");
        var references = await operation.Repositories.ProjectReferences
            .ListByReferencedProjectAsync(referencedProjectId, cancellationToken);

        return references
            .Select(reference => new ProjectReferenceImpact(
                reference.ReferencingProjectId,
                reference.ReferencingProject.Name,
                reference.ReferencingProject.Slug,
                reference.CreatedAt))
            .ToList();
    }

    private static ProjectReferenceSummary ToSummary(ProjectReference reference) => new(
        reference.ReferencedProjectId,
        reference.ReferencedProject.Name,
        reference.ReferencedProject.Slug,
        reference.ReferencedProject.UpdatedAt,
        reference.CreatedAt);

    private async Task BackfillReferencedIndexesAsync(
        Guid referencedProjectId,
        AppDatabaseWriteOperation operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await contextIndexing.ReindexProjectProfileAsync(referencedProjectId, cancellationToken);
            var samples = await operation.Repositories.WritingSamples
                .ListByProjectAsync(referencedProjectId, cancellationToken);
            foreach (var sample in samples)
                await contextIndexing.ReindexWritingSampleAsync(sample.Id, cancellationToken);

            var facts = await operation.Repositories.GraphNodes.ListByTypeAsync(
                referencedProjectId,
                EntityTypeService.ProjectFactNodeType,
                cancellationToken);
            foreach (var fact in facts)
            {
                if (Guid.TryParseExact(fact.Key, "N", out var factId))
                    await contextIndexing.ReindexEntityAsync(referencedProjectId, factId, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Best-effort reference index backfill failed for referenced project {ProjectId}",
                referencedProjectId);
        }
    }
}
