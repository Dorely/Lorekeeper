using Lorekeeper.Knowledge;
using Lorekeeper.Llm;
using Lorekeeper.Models;
using Lorekeeper.Persistence.Repositories;

namespace Lorekeeper.Chapters;

public class ChapterService(
    IChapterRepository repo,
    IProjectRepository projects,
    IVectorStore vectors,
    IEmbeddingService embeddings,
    ITextChunker chunker,
    IStaleChapterNotifier staleNotifier,
    ILogger<ChapterService> logger) : IChapterService
{
    public async Task<IReadOnlyList<Chapter>> ListAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await repo.ListByProjectAsync(projectId, cancellationToken);

    public Task<Chapter?> GetAsync(Guid chapterId, CancellationToken cancellationToken = default) =>
        repo.GetByIdAsync(chapterId, cancellationToken);

    public async Task<Chapter> CreateAsync(Guid projectId, string? title = null, CancellationToken cancellationToken = default)
    {
        var project = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");

        var nextOrder = await repo.GetMaxOrderAsync(projectId, cancellationToken) + 1;
        var resolvedTitle = string.IsNullOrWhiteSpace(title)
            ? $"Chapter {nextOrder + 1}"
            : title.Trim();

        var chapter = new Chapter
        {
            ProjectId = projectId,
            Title = resolvedTitle,
            Order = nextOrder,
            VectorIndexState = VectorIndexState.UpToDate, // empty body == nothing to index
        };

        await repo.AddAsync(chapter, cancellationToken);
        project.UpdatedAt = DateTime.UtcNow;
        projects.Update(project);
        await repo.SaveChangesAsync(cancellationToken);
        return chapter;
    }

    public async Task<Chapter> UpdateAsync(Guid chapterId, string? title = null, string? body = null, string? synopsis = null, CancellationToken cancellationToken = default)
    {
        var chapter = await repo.GetByIdAsync(chapterId, cancellationToken)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found.");

        var bodyChanged = false;
        if (title is not null && title != chapter.Title) chapter.Title = title;
        if (synopsis is not null && synopsis != chapter.Synopsis) chapter.Synopsis = synopsis;
        if (body is not null && body != chapter.Body)
        {
            chapter.Body = body;
            bodyChanged = true;
            chapter.VectorIndexState = VectorIndexState.Stale;
        }

        chapter.UpdatedAt = DateTime.UtcNow;
        repo.Update(chapter);

        var project = await projects.GetByIdAsync(chapter.ProjectId, cancellationToken);
        if (project is not null)
        {
            project.UpdatedAt = DateTime.UtcNow;
            projects.Update(project);
        }

        await repo.SaveChangesAsync(cancellationToken);

        if (bodyChanged) staleNotifier.Notify(chapter.Id);

        return chapter;
    }

    public async Task DeleteAsync(Guid chapterId, CancellationToken cancellationToken = default)
    {
        var chapter = await repo.GetByIdAsync(chapterId, cancellationToken);
        if (chapter is null) return;

        try
        {
            await vectors.DeleteBySourceAsync("chapter", chapter.VectorSourceId, Project.ScopeKey(chapter.ProjectId), cancellationToken);
        }
        catch (Exception ex)
        {
            // Don't block the EF delete; vector orphans are tolerable, missing rows are not.
            logger.LogWarning(ex, "Failed to delete vector chunks for chapter {ChapterId}", chapter.Id);
        }

        repo.Remove(chapter);
        await repo.SaveChangesAsync(cancellationToken);
    }

    public async Task ReorderAsync(Guid projectId, IReadOnlyList<Guid> orderedIds, CancellationToken cancellationToken = default)
    {
        await repo.ReorderAsync(projectId, orderedIds, cancellationToken);

        var project = await projects.GetByIdAsync(projectId, cancellationToken);
        if (project is not null)
        {
            project.UpdatedAt = DateTime.UtcNow;
            projects.Update(project);
        }

        await repo.SaveChangesAsync(cancellationToken);
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

            logger.LogDebug("Reindexed chapter {ChapterId} with {Count} chunks", chapter.Id, chunks.Count);
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
}
