using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lorekeeper.Chapters;
using Lorekeeper.Context;
using Lorekeeper.ImportExport;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Projects;
using Lorekeeper.VersionHistory.Compare;
using Lorekeeper.VersionHistory.Services;

namespace Lorekeeper.EditorChat;

/// <summary>
/// Provides compact, read-only review projections for Editor turns. The
/// projection is deliberately stateless: every operation rereads the current
/// review and returns an opaque revision that callers must carry through
/// pagination.
/// </summary>
public interface IEditorPendingReviewInspector
{
    Task<string?> GetAutomaticSummaryAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);

    Task<string> ListPendingTargetsAsync(
        Guid projectId,
        int pageNumber = 1,
        string? reviewRevision = null,
        CancellationToken cancellationToken = default);

    Task<string> ReadPendingDiffAsync(
        Guid projectId,
        string targetKind,
        string targetKey,
        int pageNumber = 1,
        string? reviewRevision = null,
        CancellationToken cancellationToken = default);
}

public sealed class EditorPendingReviewInspector(
    IProjectVersionHistoryService history,
    IProjectService projects,
    IChapterService chapters,
    IActService acts) : IEditorPendingReviewInspector
{
    private const int TargetPageSize = 20;
    private const int SummaryTargetLimit = 20;
    private const int MaxSummaryChars = 8_000;
    private const int MaxSummaryLabelChars = 240;
    private const char TargetKeySeparator = '\u001f';

    public async Task<string?> GetAutomaticSummaryAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var review = await LoadReviewAsync(projectId, cancellationToken);
            if (review is null || review.Comparison is null || review.Comparison.IsIdentical)
                return null;

            var targets = await BuildPendingTargetsAsync(review, projectId, cancellationToken);
            if (targets.Count == 0)
                return null;

            var builder = new StringBuilder("Pending review changes are available for the selected project.");
            var chapterTargets = targets.Where(target => target.Kind == "chapter").ToList();
            var entityTargets = targets
                .Where(target => target.Kind is "entity" or "relationship")
                .ToList();
            var otherCount = targets.Count(target => target.Kind is not "chapter" and not "entity" and not "relationship");

            builder.Append("\nChapters (").Append(chapterTargets.Count).AppendLine("):");
            AppendSummaryTargets(builder, chapterTargets);
            builder.Append("Entities and relationships (").Append(entityTargets.Count).AppendLine("):");
            AppendSummaryTargets(builder, entityTargets);
            builder.Append("Other changes: ").Append(otherCount).AppendLine(" target(s).");
            builder.Append("Use list_pending_review_changes for paginated discovery and read_pending_review_diff for a target's semantic diff.");
            builder.Append(" Review revision: ").Append(GetReviewRevision(review.Token)).Append('.');
            return BoundSummary(builder.ToString());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedReviewFailure(exception))
        {
            return null;
        }
    }

    public async Task<string> ListPendingTargetsAsync(
        Guid projectId,
        int pageNumber = 1,
        string? reviewRevision = null,
        CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1)
            return Error("INVALID_PAGE", "pageNumber must be at least 1.");

        try
        {
            var review = await LoadReviewAsync(projectId, cancellationToken);
            if (review is null)
                return Error("REVIEW_UNAVAILABLE", "Review state is unavailable for this project.");

            var currentRevision = GetReviewRevision(review.Token);
            if (!RevisionMatches(reviewRevision, currentRevision))
                return StaleError(currentRevision);

            var targets = review.Comparison is null || review.Comparison.IsIdentical
                ? []
                : await BuildPendingTargetsAsync(review, projectId, cancellationToken);
            var pageCount = Math.Max(1, (targets.Count + TargetPageSize - 1) / TargetPageSize);
            if (pageNumber > pageCount)
                return Error("INVALID_PAGE", $"pageNumber {pageNumber} is beyond the available {pageCount} page(s).", currentRevision);

            var pageTargets = targets
                .Skip((pageNumber - 1) * TargetPageSize)
                .Take(TargetPageSize)
                .ToList();
            var hasMore = pageNumber < pageCount;
            var previousArguments = pageNumber > 1
                ? new JsonObject
                {
                    ["pageNumber"] = pageNumber - 1,
                    ["reviewRevision"] = currentRevision,
                }
                : null;
            var nextArguments = hasMore
                ? new JsonObject
                {
                    ["pageNumber"] = pageNumber + 1,
                    ["reviewRevision"] = currentRevision,
                }
                : null;

            var targetJson = new JsonArray();
            foreach (var target in pageTargets)
                targetJson.Add(ProjectTarget(target, currentRevision));

            return new JsonObject
            {
                ["ok"] = true,
                ["schema"] = "pending-review-targets-v1",
                ["reviewRevision"] = currentRevision,
                ["targets"] = targetJson,
                ["pagination"] = new JsonObject
                {
                    ["pageNumber"] = pageNumber,
                    ["pageSize"] = TargetPageSize,
                    ["pageCount"] = pageCount,
                    ["totalTargets"] = targets.Count,
                    ["returned"] = pageTargets.Count,
                    ["hasMore"] = hasMore,
                    ["hasPreviousPage"] = pageNumber > 1,
                    ["hasNextPage"] = hasMore,
                    ["isComplete"] = pageCount == 1,
                },
                ["previousPageArguments"] = previousArguments,
                ["nextPageArguments"] = nextArguments,
            }.ToJsonString();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedReviewFailure(exception))
        {
            return Error(
                exception is ReviewInspectionException reviewException
                    ? reviewException.Code
                    : "REVIEW_UNAVAILABLE",
                exception.Message);
        }
    }

    public async Task<string> ReadPendingDiffAsync(
        Guid projectId,
        string targetKind,
        string targetKey,
        int pageNumber = 1,
        string? reviewRevision = null,
        CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1)
            return Error("INVALID_PAGE", "pageNumber must be at least 1.");
        if (string.IsNullOrWhiteSpace(targetKind) || string.IsNullOrWhiteSpace(targetKey))
            return Error("INVALID_TARGET", "targetKind and targetKey are required.");

        try
        {
            var review = await LoadReviewAsync(projectId, cancellationToken);
            if (review is null)
                return Error("REVIEW_UNAVAILABLE", "Review state is unavailable for this project.");

            var currentRevision = GetReviewRevision(review.Token);
            if (!RevisionMatches(reviewRevision, currentRevision))
                return StaleError(currentRevision);

            var targets = review.Comparison is null || review.Comparison.IsIdentical
                ? []
                : await BuildPendingTargetsAsync(review, projectId, cancellationToken);
            var normalizedKind = targetKind.Trim().ToLowerInvariant();
            var target = targets.SingleOrDefault(item =>
                string.Equals(item.Kind, normalizedKind, StringComparison.Ordinal)
                && string.Equals(item.Key, targetKey.Trim(), StringComparison.Ordinal));
            if (target is null)
                return Error("INVALID_TARGET", "The requested pending review target is no longer available.", currentRevision);

            var identity = new JsonObject
            {
                ["ok"] = true,
                ["schema"] = "pending-review-diff-v1",
                ["reviewRevision"] = currentRevision,
                ["target"] = ProjectTarget(target, currentRevision),
            };
            var continuationArguments = new JsonObject
            {
                ["targetKind"] = target.Kind,
                ["targetKey"] = target.Key,
                ["pageNumber"] = 1,
                ["reviewRevision"] = currentRevision,
            };
            var detail = target.Chapter is not null
                ? VersionHistoryReviewDiffProjector.ProjectChapter(target.Chapter)
                : ProjectOtherDetail(target);
            var result = AgentPayloadPaginator.SerializeCompactObjectPage(
                identity,
                detail,
                "read_pending_review_diff",
                continuationArguments,
                pageNumber);
            return result.StartsWith("Error:", StringComparison.Ordinal)
                ? Error("INVALID_PAGE", result, currentRevision)
                : result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedReviewFailure(exception))
        {
            return Error(
                exception is ReviewInspectionException reviewException
                    ? reviewException.Code
                    : "REVIEW_UNAVAILABLE",
                exception.Message);
        }
    }

    private async Task<ProjectVersionReviewView?> LoadReviewAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        if (projectId == Guid.Empty)
            throw new ReviewInspectionException("INVALID_PROJECT", "A project ID is required.");

        var project = await projects.GetByIdAsync(projectId, cancellationToken);
        if (project is null)
            throw new ReviewInspectionException("INVALID_PROJECT", "The requested project was not found.");
        if (!project.ReviewEditsEnabled)
            throw new ReviewInspectionException("REVIEW_EDITS_DISABLED", "Review Edits is disabled for this project.");

        var review = await history.GetReviewAsync(projectId, cancellationToken: cancellationToken);
        var currentProject = await projects.GetByIdAsync(projectId, cancellationToken);
        if (currentProject is null)
            throw new ReviewInspectionException("INVALID_PROJECT", "The requested project was not found.");
        if (!currentProject.ReviewEditsEnabled)
            throw new ReviewInspectionException("REVIEW_EDITS_DISABLED", "Review Edits is disabled for this project.");
        return review;
    }

    private async Task<IReadOnlyList<PendingReviewTarget>> BuildPendingTargetsAsync(
        ProjectVersionReviewView review,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var pendingChapters = review.Chapters
            .Where(chapter => chapter.HasChanges)
            .ToList();
        var outline = pendingChapters.Count == 0
            ? OutlineOrder.Empty
            : await LoadOutlineOrderAsync(projectId, pendingChapters, cancellationToken);

        var targets = pendingChapters
            .Select(chapter =>
            {
                var outlinePosition = outline.Get(chapter);
                return new PendingReviewTarget(
                    "chapter",
                    ChapterTargetKey(chapter.ChapterId, chapter.ContentTarget.StorageKey),
                    chapter.After?.Title ?? chapter.Before?.Title ?? chapter.ChapterId.ToString("N"),
                    chapter,
                    null,
                    null,
                    outlinePosition.ActOrder,
                    outlinePosition.ChapterOrder);
            })
            .OrderBy(target => target.ActOrder)
            .ThenBy(target => target.ChapterOrder)
            .ThenBy(target => target.Chapter?.ChapterId)
            .ThenBy(target => target.Chapter is { } chapter && chapter.ContentTarget.IsCore ? 0 : 1)
            .ThenBy(target => target.Chapter?.ContentTarget.EditionId ?? Guid.Empty)
            .ToList();

        foreach (var group in review.DependencyGroups.Where(group => !group.IsManuscriptScoped))
        {
            foreach (var entry in group.Entries.Where(entry => !IsChapterReviewableEntry(review, group, entry)))
            {
                var kind = group.Key switch
                {
                    "graph/nodes" => "entity",
                    "graph/edges" => "relationship",
                    _ => "other",
                };
                targets.Add(new PendingReviewTarget(
                    kind,
                    OtherTargetKey(group.Key, entry.Category, entry.Key),
                    entry.Label,
                    null,
                    group,
                    entry,
                    int.MaxValue,
                    int.MaxValue));
            }
        }

        return targets;
    }

    private async Task<OutlineOrder> LoadOutlineOrderAsync(
        Guid projectId,
        IReadOnlyList<ProjectVersionReviewChapter> pendingChapters,
        CancellationToken cancellationToken)
    {
        var liveChapters = await chapters.ListAsync(projectId, cancellationToken);
        var liveActs = await acts.ListAsync(projectId, cancellationToken);
        var actOrders = liveActs.ToDictionary(act => act.Id, act => act.Order);
        var chapterPositions = liveChapters.ToDictionary(
            chapter => chapter.Id,
            chapter => new OutlinePosition(
                chapter.ActId is Guid actId && actOrders.TryGetValue(actId, out var actOrder)
                    ? actOrder
                    : actOrders.Count,
                chapter.Order));
        var fallback = pendingChapters
            .Select(chapter => chapter.After ?? chapter.Before)
            .Where(chapter => chapter is not null)
            .Select(chapter => chapter!)
            .GroupBy(chapter => chapter.Id)
            .Select(group => group
                .OrderBy(chapter => chapter.ActId ?? Guid.Empty)
                .ThenBy(chapter => chapter.Order)
                .First())
            .ToDictionary(
                chapter => chapter.Id,
                chapter => new OutlinePosition(
                    chapter.ActId is Guid actId && actOrders.TryGetValue(actId, out var actOrder)
                        ? actOrder
                        : actOrders.Count,
                    chapter.Order));
        return new OutlineOrder(chapterPositions, fallback, actOrders.Count);
    }

    private static JsonObject ProjectTarget(PendingReviewTarget target, string reviewRevision)
    {
        var result = new JsonObject
        {
            ["kind"] = target.Kind,
            ["key"] = target.Key,
            ["label"] = target.Label,
            ["reviewRevision"] = reviewRevision,
            ["detailReadTool"] = "read_pending_review_diff",
            ["detailReadArguments"] = new JsonObject
            {
                ["targetKind"] = target.Kind,
                ["targetKey"] = target.Key,
                ["pageNumber"] = 1,
                ["reviewRevision"] = reviewRevision,
            },
        };

        if (target.Chapter is { } chapter)
        {
            result["chapterId"] = chapter.ChapterId;
            result["contentTarget"] = chapter.ContentTarget.StorageKey;
            result["manuscriptChanged"] = chapter.HasManuscriptChanges;
            result["metadataReviewedSeparately"] = true;
            result["entryCount"] = chapter.HasManuscriptChanges ? 1 : 0;
        }
        else if (target.DependencyGroup is { } group && target.Entry is { } entry)
        {
            result["dependencyGroupKey"] = group.Key;
            result["dependencyGroupLabel"] = group.Label;
            result["isAtomic"] = group.IsAtomic;
            result["isManuscriptScoped"] = group.IsManuscriptScoped;
            result["areas"] = new JsonArray(group.Areas.Select(area => JsonValue.Create(area)).ToArray());
            result["categories"] = new JsonArray(group.Categories.Select(category => JsonValue.Create(category)).ToArray());
            result["entryCategory"] = entry.Category;
            result["entryKey"] = entry.Key;
            result["manuscriptChanged"] = entry.ManuscriptChanged;
            result["metadataChanged"] = entry.MetadataChanged;
            result["binaryChanged"] = entry.BinaryChanged;
        }

        return result;
    }

    private static JsonObject ProjectOtherDetail(PendingReviewTarget target)
    {
        var group = target.DependencyGroup!;
        var entry = target.Entry!;
        var groupEntries = new JsonArray(group.Entries.Select(ProjectEntrySummary).ToArray());
        return new JsonObject
        {
            ["dependencyGroup"] = new JsonObject
            {
                ["key"] = group.Key,
                ["label"] = group.Label,
                ["isAtomic"] = group.IsAtomic,
                ["isManuscriptScoped"] = group.IsManuscriptScoped,
                ["areas"] = new JsonArray(group.Areas.Select(area => JsonValue.Create(area)).ToArray()),
                ["categories"] = new JsonArray(group.Categories.Select(category => JsonValue.Create(category)).ToArray()),
                ["entries"] = groupEntries,
            },
            ["selectedChange"] = ProjectEntry(entry),
        };
    }

    private static JsonObject ProjectEntrySummary(VersionHistorySnapshotChangeEntry entry) => new()
    {
        ["category"] = entry.Category,
        ["key"] = entry.Key,
        ["change"] = entry.Change.ToString(),
        ["label"] = entry.Label,
        ["manuscriptChanged"] = entry.ManuscriptChanged,
        ["metadataChanged"] = entry.MetadataChanged,
        ["binaryChanged"] = entry.BinaryChanged,
    };

    private static JsonObject ProjectEntry(VersionHistorySnapshotChangeEntry entry)
    {
        var result = ProjectEntrySummary(entry);
        result["beforeLabel"] = entry.BeforeLabel;
        result["afterLabel"] = entry.AfterLabel;
        result["beforeText"] = entry.BeforeText;
        result["afterText"] = entry.AfterText;
        result["beforeTextTruncated"] = entry.BeforeTextTruncated;
        result["afterTextTruncated"] = entry.AfterTextTruncated;
        return result;
    }

    private static void AppendSummaryTargets(StringBuilder builder, IReadOnlyList<PendingReviewTarget> targets)
    {
        foreach (var target in targets.Take(SummaryTargetLimit))
        {
            builder.Append("- ").Append(BoundSummaryLabel(target.Label));
            if (target.Chapter is { } chapter)
                builder.Append(" [").Append(chapter.ContentTarget.StorageKey).Append(']');
            else if (target.Kind is "entity" or "relationship")
                builder.Append(" [").Append(target.Kind).Append(']');
            builder.AppendLine();
        }

        if (targets.Count > SummaryTargetLimit)
            builder.Append("- … ").Append(targets.Count - SummaryTargetLimit).AppendLine(" more; use the paginated review tool.");
    }

    private static string BoundSummary(string value)
    {
        if (value.Length <= MaxSummaryChars)
            return value;

        const string suffix = "\nAdditional targets are available through the paginated review tool.";
        var length = MaxSummaryChars - suffix.Length;
        if (length > 0 && char.IsHighSurrogate(value[length - 1]))
            length--;
        return value[..length] + suffix;
    }

    private static string BoundSummaryLabel(string value)
    {
        if (value.Length <= MaxSummaryLabelChars)
            return value;

        var length = MaxSummaryLabelChars - 1;
        if (length > 0 && char.IsHighSurrogate(value[length - 1]))
            length--;
        return value[..length] + "…";
    }

    private static bool IsChapterReviewableEntry(
        ProjectVersionReviewView review,
        ProjectVersionReviewDependencyGroup group,
        VersionHistorySnapshotChangeEntry entry)
    {
        if (group.Key.StartsWith("composition/designed-pages", StringComparison.Ordinal)
            && Guid.TryParse(entry.Key, out var designedPageId))
        {
            return review.DesignedPageChanges.Any(page => page.DesignedPageId == designedPageId);
        }

        if (group.Key.StartsWith("publication/editions", StringComparison.Ordinal)
            && entry.ManuscriptChanged
            && !entry.MetadataChanged
            && Guid.TryParse(entry.Key, out var editionId))
        {
            return review.Chapters.Any(chapter =>
                chapter.ContentTarget.EditionId == editionId
                && chapter.HasManuscriptChanges);
        }

        return false;
    }

    private static string ChapterTargetKey(Guid chapterId, string contentTarget) =>
        $"chapter:{chapterId:N}:{contentTarget}";

    private static string OtherTargetKey(string groupKey, string category, string entryKey)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Join(TargetKeySeparator, groupKey, category, entryKey));
        return $"other:{Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
    }

    private static string GetReviewRevision(ProjectVersionReviewConcurrencyToken token)
    {
        var value = string.Join(
            "\n",
            token.RepositoryId.ToString("N"),
            token.HeadCommitSha ?? string.Empty,
            token.HeadContentHash ?? string.Empty,
            token.CurrentContentHash);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static bool RevisionMatches(string? expected, string current) =>
        string.IsNullOrWhiteSpace(expected)
        || string.Equals(expected, current, StringComparison.Ordinal);

    private static string StaleError(string currentRevision) =>
        Error("REVIEW_STALE", "Review state changed. Relist pending review targets before continuing.", currentRevision);

    private static string Error(string code, string message, string? reviewRevision = null) =>
        new JsonObject
        {
            ["ok"] = false,
            ["code"] = code,
            ["error"] = message,
            ["reviewRevision"] = reviewRevision,
        }.ToJsonString();

    private static bool IsExpectedReviewFailure(Exception exception) =>
        exception is ReviewInspectionException
            or ProjectVersionReviewConcurrencyException
            or InvalidOperationException
            or InvalidDataException
            or ArgumentException
            or KeyNotFoundException;

    private sealed class ReviewInspectionException(string code, string message) : InvalidOperationException(message)
    {
        public string Code { get; } = code;
    }

    private sealed record PendingReviewTarget(
        string Kind,
        string Key,
        string Label,
        ProjectVersionReviewChapter? Chapter,
        ProjectVersionReviewDependencyGroup? DependencyGroup,
        VersionHistorySnapshotChangeEntry? Entry,
        int ActOrder,
        int ChapterOrder);

    private sealed record OutlinePosition(int ActOrder, int ChapterOrder);

    private sealed class OutlineOrder(
        IReadOnlyDictionary<Guid, OutlinePosition> live,
        IReadOnlyDictionary<Guid, OutlinePosition> fallback,
        int actCount)
    {
        public static OutlineOrder Empty { get; } = new(
            new Dictionary<Guid, OutlinePosition>(),
            new Dictionary<Guid, OutlinePosition>(),
            0);

        public OutlinePosition Get(ProjectVersionReviewChapter chapter)
        {
            if (live.TryGetValue(chapter.ChapterId, out var livePosition))
                return livePosition;
            if (fallback.TryGetValue(chapter.ChapterId, out var fallbackPosition))
                return fallbackPosition;
            return new(actCount, int.MaxValue);
        }
    }
}
