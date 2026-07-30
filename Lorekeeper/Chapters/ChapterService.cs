using Lorekeeper.Knowledge;
using Lorekeeper.Llm;
using Lorekeeper.Context;
using Lorekeeper.ChapterVisuals;
using Lorekeeper.Graph;
using Lorekeeper.Images;
using Lorekeeper.Models;
using Lorekeeper.Manuscripts;
using Lorekeeper.Outline;
using Lorekeeper.Persistence;
using Lorekeeper.Persistence.Repositories;
using Lorekeeper.Search;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Chapters;

public class ChapterService(
    IChapterRepository repo,
    IProjectRepository projects,
    IVectorStore vectors,
    IEmbeddingService embeddings,
    ITextChunker chunker,
    IProjectSearchIndex projectSearch,
    IGraphAutoLinkService autoLinks,
    IOutlineGraphSync outlineGraphSync,
    IContextIndexingService contextIndexing,
    IVectorIndexWorkCoordinator indexWork,
    IProjectImageService projectImages,
    IManuscriptStyleService manuscriptStyles,
    IProjectMutationCoordinator projectMutations,
    ILogger<ChapterService> logger) : IChapterService, IManuscriptService
{
    public async Task<IReadOnlyList<Chapter>> ListAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await repo.ListByProjectAsync(projectId, cancellationToken);

    public Task<Chapter?> GetAsync(Guid chapterId, CancellationToken cancellationToken = default) =>
        repo.GetByIdAsync(chapterId, cancellationToken);

    public Task<Chapter?> ReloadFromStoreAsync(Guid chapterId, CancellationToken cancellationToken = default) =>
        repo.ReloadFromStoreAsync(chapterId, cancellationToken);

    public async Task<Chapter> CreateAsync(Guid projectId, Guid? actId = null, string? title = null, string? synopsis = null, Guid? id = null, CancellationToken cancellationToken = default)
    {
        var project = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");

        var nextOrder = await repo.GetMaxOrderAsync(projectId, actId, cancellationToken) + 1;
        var resolvedTitle = string.IsNullOrWhiteSpace(title)
            ? $"Chapter {nextOrder + 1}"
            : title.Trim();

        var chapter = new Chapter
        {
            Id = id ?? Guid.NewGuid(),
            ProjectId = projectId,
            ActId = actId,
            Title = resolvedTitle,
            ManuscriptRevision = 0,
            Synopsis = synopsis ?? string.Empty,
            Order = nextOrder,
            VectorIndexState = VectorIndexState.UpToDate, // empty body == nothing to index
        };
        chapter.ManuscriptJson = ManuscriptCodec.Serialize(
            ManuscriptCodec.CreateEmpty(chapter.Id, chapter.ManuscriptRevision));

        await repo.AddAsync(chapter, cancellationToken);
        project.UpdatedAt = DateTime.UtcNow;
        projects.Update(project);
        await repo.SaveChangesAsync(cancellationToken);
        await outlineGraphSync.EnsureChapterAsync(chapter, cancellationToken);
        await contextIndexing.ReindexChapterAsync(chapter.Id, cancellationToken);
        if (chapter.ActId is Guid createdActId)
            await contextIndexing.ReindexActAsync(createdActId, cancellationToken);
        return chapter;
    }

    public async Task<Chapter> UpdateAsync(
        Guid chapterId,
        string? title = null,
        string? synopsis = null,
        ChapterActAssignment? actId = null,
        CancellationToken cancellationToken = default)
    {
        var chapter = await repo.GetByIdAsync(chapterId, cancellationToken)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found.");
        var previousActId = chapter.ActId;

        var titleOrSynopsisChanged = false;
        var actChanged = false;
        if (title is not null && title != chapter.Title)
        {
            chapter.Title = title;
            titleOrSynopsisChanged = true;
        }
        if (synopsis is not null && synopsis != chapter.Synopsis)
        {
            chapter.Synopsis = synopsis;
            titleOrSynopsisChanged = true;
        }
        if (actId is { } assignment && assignment.Value != chapter.ActId)
        {
            chapter.ActId = assignment.Value;
            // Append to the end of the destination bucket so we don't collide with existing orders.
            chapter.Order = await repo.GetMaxOrderAsync(chapter.ProjectId, assignment.Value, cancellationToken) + 1;
            actChanged = true;
        }

        if (!titleOrSynopsisChanged && !actChanged)
            return chapter;

        chapter.UpdatedAt = DateTime.UtcNow;
        repo.Update(chapter);

        var project = await projects.GetByIdAsync(chapter.ProjectId, cancellationToken);
        if (project is not null)
        {
            project.UpdatedAt = DateTime.UtcNow;
            projects.Update(project);
        }

        await repo.SaveChangesAsync(cancellationToken);

        await outlineGraphSync.EnsureChapterAsync(chapter, cancellationToken);

        await contextIndexing.ReindexChapterAsync(chapter.Id, cancellationToken);

        var actIdsToReindex = new HashSet<Guid>();
        if (titleOrSynopsisChanged && chapter.ActId is Guid currentActId)
            actIdsToReindex.Add(currentActId);
        if (actChanged && previousActId is Guid oldActId)
            actIdsToReindex.Add(oldActId);
        if (actChanged && chapter.ActId is Guid newActId)
            actIdsToReindex.Add(newActId);

        foreach (var actToReindex in actIdsToReindex)
            await contextIndexing.ReindexActAsync(actToReindex, cancellationToken);

        return chapter;
    }

    public async Task<ManuscriptSnapshot?> GetManuscriptAsync(
        Guid chapterId,
        CancellationToken cancellationToken = default)
    {
        var chapter = await repo.GetByIdAsync(chapterId, cancellationToken);
        return chapter is null ? null : Snapshot(chapter);
    }

    public async Task<ManuscriptMutationResult> ReplaceDocumentAsync(
        Guid chapterId,
        long expectedRevision,
        ManuscriptDocument document,
        CancellationToken cancellationToken = default)
    {
        var chapter = await RequireRevisionAsync(chapterId, expectedRevision, cancellationToken);
        var replacement = document with
        {
            ManuscriptId = chapter.Id,
            Revision = checked(chapter.ManuscriptRevision + 1),
        };
        ManuscriptCodec.Validate(replacement, chapter.Id, replacement.Revision);
        return await SaveManuscriptAsync(
            chapter,
            replacement,
            replacement.Content.Select(block => block.Id).ToList(),
            cancellationToken);
    }

    public async Task<ManuscriptMutationResult> ApplyAsync(
        Guid chapterId,
        long expectedRevision,
        IReadOnlyList<ManuscriptOperation> operations,
        CancellationToken cancellationToken = default)
    {
        var chapter = await RequireRevisionAsync(chapterId, expectedRevision, cancellationToken);
        var source = ManuscriptCodec.Deserialize(
            chapter.ManuscriptJson,
            chapter.Id,
            chapter.ManuscriptRevision);
        var (document, changed) = ManuscriptOperations.Apply(source, operations);
        return await SaveManuscriptAsync(chapter, document, changed, cancellationToken);
    }

    public async Task<ManuscriptMutationResult> ApplyUnderProjectMutationLeaseAsync(
        Guid chapterId,
        long expectedRevision,
        IReadOnlyList<ManuscriptOperation> operations,
        CancellationToken cancellationToken = default)
    {
        var chapter = await RequireRevisionAsync(chapterId, expectedRevision, cancellationToken);
        var source = ManuscriptCodec.Deserialize(
            chapter.ManuscriptJson,
            chapter.Id,
            chapter.ManuscriptRevision);
        var (document, changed) = ManuscriptOperations.Apply(source, operations);
        return await SaveManuscriptUnderLeaseAsync(
            chapter,
            document,
            changed,
            cancellationToken);
    }

    public async Task ValidateDocumentReferencesAsync(
        Guid chapterId,
        ManuscriptDocument document,
        IReadOnlyList<ManuscriptStyleView>? styleCatalog = null,
        CancellationToken cancellationToken = default)
    {
        var chapter = await repo.GetByIdAsync(chapterId, cancellationToken)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found.");
        await ValidateFigureAssetsAsync(chapter.ProjectId, document, cancellationToken);
        await ValidateStyleReferencesAsync(
            chapter.ProjectId,
            document,
            styleCatalog,
            cancellationToken);
    }

    private async Task<Chapter> RequireRevisionAsync(
        Guid chapterId,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        var chapter = await repo.GetByIdAsync(chapterId, cancellationToken)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found.");
        if (chapter.ManuscriptRevision != expectedRevision)
            throw new ManuscriptRevisionConflictException(expectedRevision, chapter.ManuscriptRevision);
        return chapter;
    }

    private async Task<ManuscriptMutationResult> SaveManuscriptAsync(
        Chapter chapter,
        ManuscriptDocument document,
        IReadOnlyList<string> changedBlockIds,
        CancellationToken cancellationToken)
    {
        await using var mutation = await projectMutations.AcquireAsync(
            chapter.ProjectId,
            cancellationToken);
        return await SaveManuscriptUnderLeaseAsync(
            chapter,
            document,
            changedBlockIds,
            cancellationToken);
    }

    private async Task<ManuscriptMutationResult> SaveManuscriptUnderLeaseAsync(
        Chapter chapter,
        ManuscriptDocument document,
        IReadOnlyList<string> changedBlockIds,
        CancellationToken cancellationToken)
    {
        ChapterTextLayoutSynchronizer.ValidateIllustrationReferences(chapter, document);
        await ValidateFigureAssetsAsync(chapter.ProjectId, document, cancellationToken);
        await ValidateStyleReferencesAsync(chapter.ProjectId, document, null, cancellationToken);
        chapter.ManuscriptJson = ManuscriptCodec.Serialize(document);
        chapter.ManuscriptRevision = document.Revision;
        chapter.UpdatedAt = DateTime.UtcNow;
        chapter.VectorIndexState = VectorIndexState.Stale;
        ChapterTextLayoutSynchronizer.SynchronizeFromManuscript(chapter, document);
        repo.Update(chapter);

        var project = await projects.GetByIdAsync(chapter.ProjectId, cancellationToken);
        if (project is not null)
        {
            project.UpdatedAt = DateTime.UtcNow;
            projects.Update(project);
        }

        try
        {
            await repo.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            var current = await repo.ReloadFromStoreAsync(chapter.Id, cancellationToken);
            throw new ManuscriptRevisionConflictException(
                checked(document.Revision - 1),
                current?.ManuscriptRevision ?? document.Revision);
        }
        try
        {
            await outlineGraphSync.EnsureChapterAsync(chapter, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Chapter {ChapterId} manuscript committed at revision {Revision}, but graph synchronization failed.",
                chapter.Id,
                chapter.ManuscriptRevision);
        }
        try
        {
            await TryReindexBodyAsync(chapter.Id, cancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            logger.LogWarning(
                exception,
                "Chapter {ChapterId} manuscript committed at revision {Revision}, but post-commit indexing was cancelled.",
                chapter.Id,
                chapter.ManuscriptRevision);
        }
        return new ManuscriptMutationResult(Snapshot(chapter), changedBlockIds);
    }

    private async Task ValidateFigureAssetsAsync(
        Guid projectId,
        ManuscriptDocument document,
        CancellationToken cancellationToken)
    {
        var imageIds = document.Content
            .Where(block => block.Type == ManuscriptBlockType.Figure)
            .Select(block => block.ImageId!.Value)
            .Distinct()
            .ToList();
        if (imageIds.Count == 0)
            return;
        var found = (await projectImages.ListByIdsAsync(projectId, imageIds, cancellationToken))
            .Select(image => image.Id)
            .ToHashSet();
        var missing = imageIds.Where(imageId => !found.Contains(imageId)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Figure image {missing[0]:N} was not found in this project.");
        }
    }

    private async Task ValidateStyleReferencesAsync(
        Guid projectId,
        ManuscriptDocument document,
        IReadOnlyList<ManuscriptStyleView>? styleCatalog,
        CancellationToken cancellationToken)
    {
        var styles = styleCatalog
            ?? await manuscriptStyles.ListAsync(projectId, cancellationToken);
        ManuscriptStyleService.ValidateDocumentReferences(document, styles);
    }

    private static ManuscriptSnapshot Snapshot(Chapter chapter)
    {
        var document = ManuscriptCodec.Deserialize(
            chapter.ManuscriptJson,
            chapter.Id,
            chapter.ManuscriptRevision);
        var plainText = ManuscriptCodec.ProjectPlainText(document);
        return new ManuscriptSnapshot(
            chapter.Id,
            chapter.ManuscriptRevision,
            ManuscriptCodec.HashPlainText(plainText),
            plainText,
            document);
    }

    private async Task TryReindexBodyAsync(Guid chapterId, CancellationToken cancellationToken)
    {
        try
        {
            await indexWork.QueueOrRunAsync(
                VectorIndexWorkKind.ChapterBody,
                chapterId.ToString("N"),
                ct => ReindexAsync(chapterId, ct),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // ReindexAsync already records failure details on the chapter.
        }
    }

    public async Task DeleteAsync(Guid chapterId, CancellationToken cancellationToken = default)
    {
        var chapter = await repo.GetByIdAsync(chapterId, cancellationToken);
        if (chapter is null) return;
        var projectId = chapter.ProjectId;
        var actId = chapter.ActId;

        try
        {
            await vectors.DeleteBySourceAsync("chapter", chapter.VectorSourceId, Project.ScopeKey(chapter.ProjectId), cancellationToken);
        }
        catch (Exception ex)
        {
            // Don't block the EF delete; vector orphans are tolerable, missing rows are not.
            logger.LogWarning(ex, "Failed to delete vector chunks for chapter {ChapterId}", chapter.Id);
        }

        // Wipe the chapter's graph footprint (its lazily-upserted Chapter node + any HasChild
        // beat children) so the entity layer doesn't leak orphans. Tolerate failures.
        try
        {
            await outlineGraphSync.RemoveChapterAsync(chapter.ProjectId, chapter.Id, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clean graph entries for chapter {ChapterId}", chapter.Id);
        }

        await contextIndexing.DeleteChapterAsync(projectId, chapter.Id, cancellationToken);

        repo.Remove(chapter);
        await repo.SaveChangesAsync(cancellationToken);
        if (actId is Guid deletedFromActId)
            await contextIndexing.ReindexActAsync(deletedFromActId, cancellationToken);
    }

    public async Task ReorderAsync(Guid projectId, Guid? actId, IReadOnlyList<Guid> orderedIds, CancellationToken cancellationToken = default)
    {
        await repo.ReorderAsync(projectId, actId, orderedIds, cancellationToken);

        var project = await projects.GetByIdAsync(projectId, cancellationToken);
        if (project is not null)
        {
            project.UpdatedAt = DateTime.UtcNow;
            projects.Update(project);
        }

        await repo.SaveChangesAsync(cancellationToken);
        await outlineGraphSync.RepairProjectAsync(projectId, cancellationToken);
        foreach (var chapterId in orderedIds)
            await contextIndexing.ReindexChapterAsync(chapterId, cancellationToken);
        if (actId is Guid reorderedActId)
            await contextIndexing.ReindexActAsync(reorderedActId, cancellationToken);
    }

    public async Task ReindexAsync(Guid chapterId, CancellationToken cancellationToken = default)
    {
        var chapter = await repo.GetByIdAsync(chapterId, cancellationToken);
        if (chapter is null) return;

        var scopeKey = Project.ScopeKey(chapter.ProjectId);
        var sourceId = chapter.VectorSourceId;

        try
        {
            await vectors.DeleteBySourceAsync("chapter", sourceId, scopeKey, cancellationToken);
            await projectSearch.DeleteBySourceAsync(ProjectSearchSourceTypes.Chapter, sourceId, scopeKey, cancellationToken);

            var searchText = BuildChapterSearchText(chapter);
            var searchChunks = chunker.Chunk(searchText);
            await projectSearch.StoreManyAsync(searchChunks.Select(chunk => new ProjectSearchIndexChunk(
                chunk.Content,
                ProjectSearchSourceTypes.Chapter,
                scopeKey,
                sourceId,
                null,
                $"Chapter {chapter.Order + 1} {chapter.Title}",
                $"Chapter {chapter.Order + 1} - {chapter.Title}",
                chunk.Index)), cancellationToken);

            if (!await embeddings.IsAvailableAsync(cancellationToken))
            {
                chapter.VectorIndexState = VectorIndexState.Disabled;
                chapter.VectorIndexedAt = null;
                chapter.VectorIndexError = null;
                repo.Update(chapter);
                await repo.SaveChangesAsync(cancellationToken);
                await outlineGraphSync.EnsureChapterAsync(chapter, cancellationToken);
                await autoLinks.RefreshSourceAsync(chapter.ProjectId, ProjectSearchSourceTypes.Chapter, chapter.Id, cancellationToken);
                await contextIndexing.ReindexChapterAsync(chapter.Id, cancellationToken);
                logger.LogDebug("Skipped chapter vector indexing for {ChapterId}; no embedding model is configured.", chapter.Id);
                return;
            }

            var chunks = chunker.Chunk(chapter.PlainText);
            if (chunks.Count > 0)
            {
                var contents = chunks.Select(c => c.Content).ToList();
                var vectorsList = await embeddings.GenerateEmbeddingsAsync(contents, cancellationToken);

                var total = chunks.Count;
                for (var i = 0; i < total; i++)
                {
                    var metadata = $"Chapter {chapter.Order + 1} • Part {i + 1}/{total} • {chapter.Title}";
                    await vectors.StoreAsync(
                        content: chunks[i].Content,
                        embedding: vectorsList[i],
                        sourceType: "chapter",
                        scopeKey: scopeKey,
                        sourceId: sourceId,
                        metadata: metadata,
                        chunkIndex: i,
                        cancellationToken: cancellationToken);
                }
            }

            chapter.VectorIndexState = VectorIndexState.UpToDate;
            chapter.VectorIndexedAt = DateTime.UtcNow;
            chapter.VectorIndexError = null;
            repo.Update(chapter);
            await repo.SaveChangesAsync(cancellationToken);
            await outlineGraphSync.EnsureChapterAsync(chapter, cancellationToken);
            await autoLinks.RefreshSourceAsync(chapter.ProjectId, ProjectSearchSourceTypes.Chapter, chapter.Id, cancellationToken);
            await contextIndexing.ReindexChapterAsync(chapter.Id, cancellationToken);

            logger.LogDebug("Reindexed chapter {ChapterId} with {Count} chunks", chapter.Id, chunks.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to reindex chapter {ChapterId}", chapter.Id);
            chapter.VectorIndexState = VectorIndexState.Failed;
            chapter.VectorIndexError = ex.Message;
            repo.Update(chapter);
            try { await repo.SaveChangesAsync(cancellationToken); }
            catch (Exception saveEx) { logger.LogError(saveEx, "Failed to persist reindex failure for chapter {ChapterId}", chapter.Id); }
            throw;
        }
    }

    private static string BuildChapterSearchText(Chapter chapter)
    {
        var parts = new List<string>
        {
            $"Type: Chapter",
            $"Title: {chapter.Title}",
        };
        if (!string.IsNullOrWhiteSpace(chapter.Synopsis))
            parts.Add($"Synopsis: {chapter.Synopsis}");
        if (!string.IsNullOrWhiteSpace(chapter.PlainText))
            parts.Add($"Body: {chapter.PlainText}");
        return string.Join('\n', parts);
    }
}
