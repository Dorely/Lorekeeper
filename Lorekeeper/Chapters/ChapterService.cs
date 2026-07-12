using Lorekeeper.Knowledge;
using Lorekeeper.Llm;
using Lorekeeper.Context;
using Lorekeeper.ChapterVisuals;
using Lorekeeper.Graph;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence.Repositories;
using Lorekeeper.Search;

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
    ILogger<ChapterService> logger) : IChapterService
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
            Synopsis = synopsis ?? string.Empty,
            Order = nextOrder,
            VectorIndexState = VectorIndexState.UpToDate, // empty body == nothing to index
        };

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
        string? body = null,
        string? synopsis = null,
        ChapterActAssignment? actId = null,
        CancellationToken cancellationToken = default)
    {
        var chapter = await repo.GetByIdAsync(chapterId, cancellationToken)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found.");
        var previousActId = chapter.ActId;

        var bodyChanged = false;
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
        if (body is not null && body != chapter.Body)
        {
            chapter.Body = body;
            ChapterTextLayoutSynchronizer.SynchronizeFromBody(chapter, body);
            bodyChanged = true;
            chapter.VectorIndexState = VectorIndexState.Stale;
        }
        if (actId is { } assignment && assignment.Value != chapter.ActId)
        {
            chapter.ActId = assignment.Value;
            // Append to the end of the destination bucket so we don't collide with existing orders.
            chapter.Order = await repo.GetMaxOrderAsync(chapter.ProjectId, assignment.Value, cancellationToken) + 1;
            actChanged = true;
        }

        if (!bodyChanged && !titleOrSynopsisChanged && !actChanged)
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

        if (bodyChanged)
            await TryReindexBodyAsync(chapter.Id, cancellationToken);
        else
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

            var chunks = chunker.Chunk(chapter.Body);
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
        if (!string.IsNullOrWhiteSpace(chapter.Body))
            parts.Add($"Body: {chapter.Body}");
        return string.Join('\n', parts);
    }
}
