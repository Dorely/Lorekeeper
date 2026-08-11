using Lorekeeper.Manuscripts;
using Lorekeeper.Knowledge;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Search;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

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
    IReadOnlyList<Guid> ChangedCompositionIds,
    IReadOnlyList<EditionLayoutIssue> LayoutIssues);

public sealed record EditionLayoutIssue(
    string Code,
    string Message,
    Guid CompositionId,
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
    AppDbContext db,
    IProjectMutationCoordinator projectMutations,
    IProjectSearchIndex projectSearch,
    IVectorStore vectors) : IEditionContentService
{
    public async Task<IReadOnlyList<EditionContentReleaseView>> ListReleasesAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
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
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
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
            throw new InvalidOperationException("Disabling edition-specific content will discard every divergent chapter and its edition layouts. Confirmation is required.");
        if (!enabled)
        {
            var activeReview = await db.AiChangeBatches.AsNoTracking().AnyAsync(
                item => item.ProjectId == projectId
                    && item.ContentTargetEditionId == editionId
                    && item.Status == AiChangeBatchStatus.Pending,
                cancellationToken);
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
            if (activeReview || activeContest || activeRevision || activeTurn)
                throw new InvalidOperationException("Resolve or cancel active edition assistant, review, contest, and revision work before discarding edition content.");
            var compositions = await db.PageCompositions
                .Where(item => item.ProjectId == projectId && item.EditionId == editionId)
                .ToListAsync(cancellationToken);
            var scopeKey = Project.ScopeKey(projectId);
            foreach (var chapterOverride in edition.ChapterOverrides)
            {
                var sourceId = chapterOverride.Id.ToString("N");
                await projectSearch.DeleteBySourceAsync(ProjectSearchSourceTypes.EditionChapter, sourceId, scopeKey, cancellationToken);
                await vectors.DeleteBySourceAsync(ProjectSearchSourceTypes.EditionChapter, sourceId, scopeKey, cancellationToken);
            }
            db.PageCompositions.RemoveRange(compositions);
            db.PublicationEditionChapterOverrides.RemoveRange(edition.ChapterOverrides);
        }
        edition.EditionSpecificContentEnabled = enabled;
        edition.Revision = checked(edition.Revision + 1);
        edition.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return new EditionContentReleaseView(edition.Id, edition.Name, enabled, false, enabled ? edition.ChapterOverrides.Count : 0);
    }

    public async Task<EditionChapterContentStatus> GetChapterStatusAsync(
        Guid projectId,
        Guid editionId,
        Guid chapterId,
        CancellationToken cancellationToken = default)
    {
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
        if (!confirmed)
            throw new InvalidOperationException("Resetting this chapter permanently discards its edition manuscript and layouts. Confirmation is required.");
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var edition = await db.PublicationEditions.SingleOrDefaultAsync(
            item => item.Id == editionId && item.ProjectId == projectId && item.EditionSpecificContentEnabled,
            cancellationToken) ?? throw new KeyNotFoundException("The enabled publication release was not found.");
        var chapterOverride = await db.PublicationEditionChapterOverrides.SingleOrDefaultAsync(
            item => item.EditionId == editionId && item.ChapterId == chapterId,
            cancellationToken);
        if (chapterOverride is null)
            return;
        var compositions = await db.PageCompositions
            .Where(item => item.ProjectId == projectId && item.ChapterId == chapterId && item.EditionId == editionId)
            .ToListAsync(cancellationToken);
        var scopeKey = Project.ScopeKey(projectId);
        var sourceId = chapterOverride.Id.ToString("N");
        await projectSearch.DeleteBySourceAsync(ProjectSearchSourceTypes.EditionChapter, sourceId, scopeKey, cancellationToken);
        await vectors.DeleteBySourceAsync(ProjectSearchSourceTypes.EditionChapter, sourceId, scopeKey, cancellationToken);
        db.PageCompositions.RemoveRange(compositions);
        db.PublicationEditionChapterOverrides.Remove(chapterOverride);
        edition.Revision = checked(edition.Revision + 1);
        edition.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EditionChapterDifference>> ReadDifferencesAsync(
        Guid projectId,
        Guid editionId,
        int offset = 0,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
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
            var editionCompositionIds = edition.Content
                .Where(block => block.PageCompositionId.HasValue)
                .Select(block => block.PageCompositionId!.Value)
                .Distinct()
                .ToList();
            var editionCompositions = await db.PageCompositions.AsNoTracking()
                .Include(item => item.Variants)
                .Where(item => item.ProjectId == projectId
                    && item.EditionId == editionId
                    && editionCompositionIds.Contains(item.Id))
                .ToListAsync(cancellationToken);
            var sourceCompositionIds = editionCompositions
                .Where(item => item.SourceCompositionId.HasValue)
                .Select(item => item.SourceCompositionId!.Value)
                .Distinct()
                .ToList();
            var sourceCompositions = await db.PageCompositions.AsNoTracking()
                .Include(item => item.Variants)
                .Where(item => item.ProjectId == projectId
                    && item.EditionId == null
                    && sourceCompositionIds.Contains(item.Id))
                .ToDictionaryAsync(item => item.Id, cancellationToken);
            var sourceByEditionComposition = editionCompositions
                .Where(item => item.SourceCompositionId.HasValue)
                .ToDictionary(item => item.Id, item => item.SourceCompositionId!.Value);
            var changedCompositionIds = editionCompositions
                .Where(item => item.SourceCompositionId is not Guid sourceId
                    || !sourceCompositions.TryGetValue(sourceId, out var source)
                    || !EquivalentComposition(source, item))
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
            var designed = commonIds.Count(id => editionById[id].PageCompositionId is Guid compositionId
                    && changedCompositionIds.Contains(compositionId))
                + addedIds.Count(id => editionById[id].Type == ManuscriptBlockType.DesignedPage)
                + removedIds.Count(id => coreById[id].Type == ManuscriptBlockType.DesignedPage);
            var changed = addedIds.Concat(removedIds)
                .Concat(commonIds.Where(id =>
                {
                    var editionBlock = editionById[id];
                    var comparableEditionBlock = editionBlock.PageCompositionId is Guid compositionId
                        && sourceByEditionComposition.TryGetValue(compositionId, out var sourceId)
                            ? editionBlock with { PageCompositionId = sourceId }
                            : editionBlock;
                    return !Equals(coreById[id], comparableEditionBlock)
                        || editionBlock.PageCompositionId is Guid changedId && changedCompositionIds.Contains(changedId);
                }))
                .Distinct(StringComparer.Ordinal)
                .Take(12)
                .ToList();
            var layoutIssues = await ReadLayoutIssuesAsync(projectId, release, edition, cancellationToken);
            results.Add(new EditionChapterDifference(row.ChapterId, row.Chapter.Title, addedIds.Count, removedIds.Count,
                moved, textEdited, figures, styles, direct, designed, changed, changedCompositionIds.Order().ToList(), layoutIssues));
        }
        return results;
    }

    private async Task<IReadOnlyList<EditionLayoutIssue>> ReadLayoutIssuesAsync(
        Guid projectId,
        PublicationEdition release,
        ManuscriptDocument document,
        CancellationToken cancellationToken)
    {
        var compositionIds = document.Content
            .Where(block => block.Type == ManuscriptBlockType.DesignedPage && block.PageCompositionId.HasValue)
            .Select(block => block.PageCompositionId!.Value)
            .Distinct()
            .ToList();
        if (compositionIds.Count == 0)
            return [];
        var compositions = await db.PageCompositions.AsNoTracking()
            .Include(item => item.Variants)
            .Where(item => item.ProjectId == projectId && item.EditionId == release.Id && compositionIds.Contains(item.Id))
            .ToListAsync(cancellationToken);
        var issues = new List<EditionLayoutIssue>();
        foreach (var compositionId in compositionIds)
        {
            var composition = compositions.SingleOrDefault(item => item.Id == compositionId);
            if (composition is null)
            {
                issues.Add(new("LAYOUT_MISSING", "The edition Designed Page layout is missing.", compositionId));
                continue;
            }
            var expectedLeafWidth = release.PageWidthInches * 72;
            var expectedHeight = release.PageHeightInches * 72;
            var exact = composition.Variants.Select(item => new
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
                    compositionId));
                continue;
            }
            foreach (var image in exact.Scene!.Objects.Where(item => item.Kind == CompositionObjectKind.Image && item.Visible))
            {
                if (image.Bounds.XPercent < -0.01 || image.Bounds.YPercent < -0.01
                    || image.Bounds.XPercent + image.Bounds.WidthPercent > 100.01
                    || image.Bounds.YPercent + image.Bounds.HeightPercent > 100.01)
                {
                    issues.Add(new("LAYOUT_IMAGE_CLIPPED", "Artwork extends beyond the page and will be clipped to the visible canvas.", compositionId, image.Id));
                }
            }
        }
        return issues;
    }

    private static bool EquivalentComposition(PageComposition core, PageComposition edition)
    {
        if (!string.Equals(core.SemanticManuscriptJson, edition.SemanticManuscriptJson, StringComparison.Ordinal))
            return false;
        var coreVariants = core.Variants
            .Select(item => (item.GeometryKey, item.SceneJson))
            .OrderBy(item => item.GeometryKey, StringComparer.Ordinal)
            .ThenBy(item => item.SceneJson, StringComparer.Ordinal)
            .ToList();
        var editionVariants = edition.Variants
            .Select(item => (item.GeometryKey, item.SceneJson))
            .OrderBy(item => item.GeometryKey, StringComparer.Ordinal)
            .ThenBy(item => item.SceneJson, StringComparer.Ordinal)
            .ToList();
        return coreVariants.SequenceEqual(editionVariants);
    }
}

internal static class ManuscriptBlockListExtensions
{
    public static int FindIndex(this IReadOnlyList<ManuscriptBlock> blocks, Func<ManuscriptBlock, bool> predicate)
    {
        for (var index = 0; index < blocks.Count; index++)
            if (predicate(blocks[index]))
                return index;
        return -1;
    }
}
