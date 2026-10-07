using System.Text.Json;
using Lorekeeper.Authoring;
using Lorekeeper.EditorChat;
using Lorekeeper.Knowledge;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Search;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Publish;

public sealed record EditionContentReleaseView(
    Guid EditionId,
    string Name,
    bool Enabled,
    bool Archived,
    int DivergentChapterCount);

public sealed record EditionChapterContentStatus(
    Guid EditionId,
    Guid ChapterId,
    bool IsDivergent,
    long EffectiveRevision,
    long? BaseCoreRevision,
    long CoreRevision,
    bool CoreChangedSinceDivergence);

public sealed record EditionChapterDifference(
    Guid ChapterId,
    string ChapterTitle,
    int Added,
    int Removed,
    int Moved,
    int TextEdited,
    int Figures,
    int StyleReferences,
    int DirectFormatting,
    int DesignedPages,
    IReadOnlyList<string> ChangedBlockIds,
    IReadOnlyList<Guid> ChangedDesignedPageIds,
    IReadOnlyList<EditionLayoutIssue> LayoutIssues);

public sealed record EditionLayoutIssue(
    string Code,
    string Message,
    Guid DesignedPageId,
    Guid? ObjectId = null);

public interface IEditionContentService
{
    Task<IReadOnlyList<EditionContentReleaseView>> ListReleasesAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<EditionContentReleaseView> SetEnabledAsync(Guid projectId, Guid editionId, long expectedEditionRevision, bool enabled, bool confirmDiscard, CancellationToken cancellationToken = default);
    Task<EditionChapterContentStatus> GetChapterStatusAsync(Guid projectId, Guid editionId, Guid chapterId, CancellationToken cancellationToken = default);
    Task ResetChapterAsync(Guid projectId, Guid editionId, Guid chapterId, bool confirmed, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EditionChapterDifference>> ReadDifferencesAsync(Guid projectId, Guid editionId, int offset = 0, int limit = 50, CancellationToken cancellationToken = default);
}

public sealed class EditionContentService(
    IAppDatabaseOperationFactory database,
    IProjectSearchIndex projectSearch,
    IVectorStore vectors,
    IManuscriptAnnotationService annotations,
    IAuthoringDeltaHistoryRuntime authoringHistory,
    IEditorContestMutationGuard contestGuard) : IEditionContentService
{
    public async Task<IReadOnlyList<EditionContentReleaseView>> ListReleasesAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var rows = await db.PublicationEditions.AsNoTracking()
            .Where(item => item.ProjectId == projectId)
            .OrderBy(item => item.CreatedAt)
            .Select(item => new
            {
                item.Id,
                item.Name,
                item.EditionSpecificContentEnabled,
                item.Status,
                Count = item.ChapterOverrides.Count,
            })
            .ToListAsync(cancellationToken);
        return rows.Select(item => new EditionContentReleaseView(
            item.Id,
            item.Name,
            item.EditionSpecificContentEnabled,
            item.Status == PublicationEditionStatus.Archived,
            item.Count)).ToList();
    }

    public async Task<EditionContentReleaseView> SetEnabledAsync(
        Guid projectId,
        Guid editionId,
        long expectedEditionRevision,
        bool enabled,
        bool confirmDiscard,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var edition = await db.PublicationEditions
            .Include(item => item.ChapterOverrides)
            .SingleOrDefaultAsync(item => item.Id == editionId && item.ProjectId == projectId, cancellationToken)
            ?? throw new KeyNotFoundException("The publication release was not found in this project.");
        if (edition.Revision != expectedEditionRevision)
            throw new InvalidOperationException($"Release revision conflict: expected {expectedEditionRevision}, current {edition.Revision}.");
        if (edition.Status == PublicationEditionStatus.Archived)
            throw new InvalidOperationException("Archived releases cannot enable edition-specific content.");
        if (edition.EditionSpecificContentEnabled == enabled)
            return new EditionContentReleaseView(edition.Id, edition.Name, enabled, false, edition.ChapterOverrides.Count);
        if (!enabled && edition.ChapterOverrides.Count > 0 && !confirmDiscard)
            throw new InvalidOperationException("Disabling edition-specific content will discard every divergent chapter. Confirmation is required.");
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var discardedHistoryTargets = new List<string>();
        if (!enabled)
        {
            var activeContest = await db.ContestBatches.AsNoTracking().AnyAsync(
                item => item.ProjectId == projectId
                    && item.ContentTargetEditionId == editionId
                    && (item.Status == ContestBatchStatus.Running || item.Status == ContestBatchStatus.Completed),
                cancellationToken);
            var activeRevision = await db.EditorRevisionJobs.AsNoTracking().AnyAsync(
                item => item.ProjectId == projectId
                    && item.ContentTargetEditionId == editionId
                    && (item.Status == EditorRevisionJobStatus.Queued || item.Status == EditorRevisionJobStatus.Running),
                cancellationToken);
            var activeTurn = await db.EditorMessages.AsNoTracking().AnyAsync(
                item => item.Conversation.ProjectId == projectId
                    && item.ContentTargetEditionId == editionId
                    && item.Status == EditorMessageStatus.Pending,
                cancellationToken);
            if (activeContest || activeRevision || activeTurn)
                throw new InvalidOperationException("Resolve or cancel active edition assistant, review, contest, and revision work before discarding edition content.");
            var scopeKey = Project.ScopeKey(projectId);
            foreach (var chapterOverride in edition.ChapterOverrides)
            {
                var sourceId = chapterOverride.Id.ToString("N");
                await projectSearch.DeleteBySourceAsync(ProjectSearchSourceTypes.EditionChapter, sourceId, scopeKey, cancellationToken);
                await vectors.DeleteBySourceAsync(ProjectSearchSourceTypes.EditionChapter, sourceId, scopeKey, cancellationToken);
                discardedHistoryTargets.Add($"release:{editionId:D}:chapter:{chapterOverride.ChapterId:D}");
            }
            var placementReferences = await db.DesignedPagePlacementReferences
                .Where(item => item.ProjectId == projectId
                    && item.ContainerKind == DesignedPageContainerKind.Chapter
                    && item.EditionId == editionId)
                .ToListAsync(cancellationToken);
            db.DesignedPagePlacementReferences.RemoveRange(placementReferences);
            db.PublicationEditionChapterOverrides.RemoveRange(edition.ChapterOverrides);
            var editionAnnotations = await db.ManuscriptAnnotations
                .Where(item => item.ProjectId == projectId && item.EditionId == editionId)
                .ToListAsync(cancellationToken);
            db.ManuscriptAnnotations.RemoveRange(editionAnnotations);
        }
        edition.EditionSpecificContentEnabled = enabled;
        edition.Revision = checked(edition.Revision + 1);
        edition.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        foreach (var historyTarget in discardedHistoryTargets)
            authoringHistory.Clear(historyTarget);
        return new EditionContentReleaseView(edition.Id, edition.Name, enabled, false, enabled ? edition.ChapterOverrides.Count : 0);
    }

    public async Task<EditionChapterContentStatus> GetChapterStatusAsync(
        Guid projectId,
        Guid editionId,
        Guid chapterId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var edition = await db.PublicationEditions.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == editionId && item.ProjectId == projectId && item.EditionSpecificContentEnabled,
            cancellationToken) ?? throw new KeyNotFoundException("The enabled publication release was not found.");
        var chapter = await db.Chapters.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == chapterId && item.ProjectId == projectId,
            cancellationToken) ?? throw new KeyNotFoundException("The chapter was not found in this project.");
        var chapterOverride = await db.PublicationEditionChapterOverrides.AsNoTracking().SingleOrDefaultAsync(
            item => item.EditionId == edition.Id && item.ChapterId == chapter.Id,
            cancellationToken);
        return new EditionChapterContentStatus(
            edition.Id,
            chapter.Id,
            chapterOverride is not null,
            chapterOverride?.Revision ?? chapter.ManuscriptRevision,
            chapterOverride?.BaseCoreRevision,
            chapter.ManuscriptRevision,
            chapterOverride is not null && chapterOverride.BaseCoreRevision != chapter.ManuscriptRevision);
    }

    public async Task ResetChapterAsync(
        Guid projectId,
        Guid editionId,
        Guid chapterId,
        bool confirmed,
        CancellationToken cancellationToken = default)
    {
        await contestGuard.EnsureMutationAllowedAsync(projectId, cancellationToken);
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        if (!confirmed)
            throw new InvalidOperationException("Resetting this chapter permanently discards its edition manuscript. Confirmation is required.");
        var edition = await db.PublicationEditions.SingleOrDefaultAsync(
            item => item.Id == editionId && item.ProjectId == projectId && item.EditionSpecificContentEnabled,
            cancellationToken) ?? throw new KeyNotFoundException("The enabled publication release was not found.");
        var chapterOverride = await db.PublicationEditionChapterOverrides.SingleOrDefaultAsync(
            item => item.EditionId == editionId && item.ChapterId == chapterId,
            cancellationToken);
        if (chapterOverride is null)
            return;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var scopeKey = Project.ScopeKey(projectId);
        var sourceId = chapterOverride.Id.ToString("N");
        await projectSearch.DeleteBySourceAsync(ProjectSearchSourceTypes.EditionChapter, sourceId, scopeKey, cancellationToken);
        await vectors.DeleteBySourceAsync(ProjectSearchSourceTypes.EditionChapter, sourceId, scopeKey, cancellationToken);
        var discardedHistoryTargets = new[] { $"release:{editionId:D}:chapter:{chapterId:D}" };
        var placementReferences = await db.DesignedPagePlacementReferences
            .Where(item => item.ProjectId == projectId
                && item.ContainerKind == DesignedPageContainerKind.Chapter
                && item.ContainerId == chapterId
                && item.EditionId == editionId)
            .ToListAsync(cancellationToken);
        db.DesignedPagePlacementReferences.RemoveRange(placementReferences);
        db.PublicationEditionChapterOverrides.Remove(chapterOverride);
        var chapter = await db.Chapters.AsNoTracking().SingleAsync(
            item => item.Id == chapterId && item.ProjectId == projectId,
            cancellationToken);
        var inherited = ManuscriptCodec.Deserialize(chapter.ManuscriptJson, chapter.Id, chapter.ManuscriptRevision);
        await annotations.RebaseForManuscriptMutationAsync(
            projectId, EditorContentTarget.ForEdition(editionId), chapterId, inherited, chapter.ManuscriptRevision, cancellationToken);
        edition.Revision = checked(edition.Revision + 1);
        edition.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        foreach (var historyTarget in discardedHistoryTargets)
            authoringHistory.Clear(historyTarget);
    }

    public async Task<IReadOnlyList<EditionChapterDifference>> ReadDifferencesAsync(
        Guid projectId,
        Guid editionId,
        int offset = 0,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        limit = Math.Clamp(limit, 1, 100);
        offset = Math.Max(0, offset);
        var rows = await db.PublicationEditionChapterOverrides.AsNoTracking()
            .Where(item => item.EditionId == editionId && item.Edition.ProjectId == projectId)
            .Include(item => item.Chapter)
            .OrderBy(item => item.Chapter.Order)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);
        var release = await db.PublicationEditions.AsNoTracking()
            .SingleAsync(item => item.Id == editionId && item.ProjectId == projectId, cancellationToken);
        var results = new List<EditionChapterDifference>();
        foreach (var row in rows)
        {
            var core = row.Chapter.Manuscript;
            var edition = ManuscriptCodec.Deserialize(row.ManuscriptJson, row.ChapterId, row.Revision);
            var editionPageIds = edition.Content
                .Where(block => block.DesignedPageId.HasValue)
                .Select(block => block.DesignedPageId!.Value)
                .Distinct()
                .ToList();
            var pages = await db.DesignedPages.AsNoTracking()
                .Include(item => item.Contents)
                    .ThenInclude(item => item.Variants)
                .Where(item => item.ProjectId == projectId && editionPageIds.Contains(item.Id))
                .ToListAsync(cancellationToken);
            var changedPageIds = pages
                .Where(page =>
                {
                    var releaseContent = page.Contents.SingleOrDefault(content => content.EditionId == editionId);
                    var coreContent = page.Contents.SingleOrDefault(content => content.EditionId == null);
                    return releaseContent is not null
                        && (coreContent is null || !EquivalentContent(coreContent, releaseContent));
                })
                .Select(item => item.Id)
                .ToHashSet();
            var coreById = core.Content.ToDictionary(block => block.Id, StringComparer.Ordinal);
            var editionById = edition.Content.ToDictionary(block => block.Id, StringComparer.Ordinal);
            var addedIds = editionById.Keys.Except(coreById.Keys, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
            var removedIds = coreById.Keys.Except(editionById.Keys, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
            var commonIds = coreById.Keys.Intersect(editionById.Keys, StringComparer.Ordinal).ToList();
            var moved = commonIds.Count(id => core.Content.FindIndex(block => block.Id == id) != edition.Content.FindIndex(block => block.Id == id));
            var textEdited = commonIds.Count(id => !string.Equals(ManuscriptCodec.Text(coreById[id]), ManuscriptCodec.Text(editionById[id]), StringComparison.Ordinal));
            var figures = commonIds.Count(id => coreById[id].ImageId != editionById[id].ImageId || coreById[id].FigurePresentation != editionById[id].FigurePresentation)
                + addedIds.Count(id => editionById[id].Type == ManuscriptBlockType.Figure)
                + removedIds.Count(id => coreById[id].Type == ManuscriptBlockType.Figure);
            var styles = commonIds.Count(id => !string.Equals(coreById[id].StyleRole, editionById[id].StyleRole, StringComparison.Ordinal));
            var direct = commonIds.Count(id =>
                coreById[id].ParagraphPresentation != editionById[id].ParagraphPresentation
                || !coreById[id].Content.SequenceEqual(editionById[id].Content));
            var designed = commonIds.Count(id => editionById[id].DesignedPageId is Guid pageId
                    && changedPageIds.Contains(pageId))
                + addedIds.Count(id => editionById[id].Type == ManuscriptBlockType.DesignedPage)
                + removedIds.Count(id => coreById[id].Type == ManuscriptBlockType.DesignedPage);
            var changed = addedIds.Concat(removedIds)
                .Concat(commonIds.Where(id =>
                {
                    var editionBlock = editionById[id];
                    return !Equals(coreById[id], editionBlock)
                        || editionBlock.DesignedPageId is Guid changedId && changedPageIds.Contains(changedId);
                }))
                .Distinct(StringComparer.Ordinal)
                .Take(12)
                .ToList();
            var layoutIssues = await ReadLayoutIssuesAsync(projectId, release, edition, cancellationToken);
            results.Add(new EditionChapterDifference(row.ChapterId, row.Chapter.Title, addedIds.Count, removedIds.Count,
                moved, textEdited, figures, styles, direct, designed, changed, changedPageIds.Order().ToList(), layoutIssues));
        }
        return results;
    }

    private async Task<IReadOnlyList<EditionLayoutIssue>> ReadLayoutIssuesAsync(
        Guid projectId,
        PublicationEdition release,
        ManuscriptDocument document,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var pageIds = document.Content
            .Where(block => block.Type == ManuscriptBlockType.DesignedPage && block.DesignedPageId.HasValue)
            .Select(block => block.DesignedPageId!.Value)
            .Distinct()
            .ToList();
        if (pageIds.Count == 0)
            return [];
        var pages = await db.DesignedPages.AsNoTracking()
            .Include(item => item.Contents)
                .ThenInclude(item => item.Variants)
            .Where(item => item.ProjectId == projectId && pageIds.Contains(item.Id))
            .ToListAsync(cancellationToken);
        var issues = new List<EditionLayoutIssue>();
        foreach (var pageId in pageIds)
        {
            var page = pages.SingleOrDefault(item => item.Id == pageId);
            var content = page?.Contents.SingleOrDefault(item => item.EditionId == release.Id)
                ?? page?.Contents.SingleOrDefault(item => item.EditionId == null);
            if (content is null)
            {
                issues.Add(new("LAYOUT_MISSING", "The release Designed Page layout is missing.", pageId));
                continue;
            }
            var expectedLeafWidth = release.PageWidthInches * 72;
            var expectedHeight = release.PageHeightInches * 72;
            var exact = content.Variants.Select(item => new
            {
                Variant = item,
                Scene = JsonSerializer.Deserialize<CompositionScene>(item.SceneJson, ManuscriptCodec.JsonOptions),
            })
                .FirstOrDefault(item => item.Scene is { } scene
                    && Math.Abs(scene.Surface.HeightPoints - expectedHeight) < 0.5
                    && Math.Abs(scene.Surface.WidthPoints - expectedLeafWidth * (scene.Surface.Kind == CompositionSurfaceKind.FacingSpread ? 2 : 1)) < 0.5);
            if (exact is null)
            {
                issues.Add(new(
                    "LAYOUT_GEOMETRY_MISMATCH",
                    $"Create or adjust this Designed Page for the release's {release.PageWidthInches:0.##} × {release.PageHeightInches:0.##} in geometry in Editor.",
                    pageId));
                continue;
            }
            foreach (var image in exact.Scene!.Objects.Where(item => item.Kind == CompositionObjectKind.Image && item.Visible))
            {
                if (image.Bounds.XPercent < -0.01 || image.Bounds.YPercent < -0.01
                    || image.Bounds.XPercent + image.Bounds.WidthPercent > 100.01
                    || image.Bounds.YPercent + image.Bounds.HeightPercent > 100.01)
                {
                    issues.Add(new("LAYOUT_IMAGE_CLIPPED", "Artwork extends beyond the page and will be clipped to the visible canvas.", pageId, image.Id));
                }
            }
        }
        return issues;
    }

    private static bool EquivalentContent(DesignedPageContent core, DesignedPageContent release)
    {
        if (!string.Equals(core.SemanticManuscriptJson, release.SemanticManuscriptJson, StringComparison.Ordinal))
            return false;
        var coreVariants = core.Variants
            .Select(item => (item.GeometryKey, item.SceneJson))
            .OrderBy(item => item.GeometryKey, StringComparer.Ordinal)
            .ThenBy(item => item.SceneJson, StringComparer.Ordinal)
            .ToList();
        var releaseVariants = release.Variants
            .Select(item => (item.GeometryKey, item.SceneJson))
            .OrderBy(item => item.GeometryKey, StringComparer.Ordinal)
            .ThenBy(item => item.SceneJson, StringComparer.Ordinal)
            .ToList();
        return coreVariants.SequenceEqual(releaseVariants);
    }
}
