using Lorekeeper.VersionHistory.Snapshots;

namespace Lorekeeper.VersionHistory.Compare;

public enum VersionHistorySnapshotChangeKind
{
    Added,
    Removed,
    Changed,
}

public sealed record VersionHistorySnapshotAreaSummary(
    string Area,
    int Added,
    int Removed,
    int Changed,
    int Unchanged)
{
    public int Total => Added + Removed + Changed + Unchanged;

    public bool HasChanges => Added != 0 || Removed != 0 || Changed != 0;
}

public sealed record VersionHistorySnapshotChangeEntry(
    string Category,
    string Key,
    VersionHistorySnapshotChangeKind Change,
    string Label,
    string? BeforeLabel,
    string? AfterLabel,
    string? BeforeHash,
    string? AfterHash,
    bool ManuscriptChanged,
    bool MetadataChanged,
    bool BinaryChanged)
{
    /// <summary>
    /// Bounded readable text from the baseline item when the compared item has
    /// text content. This is never raw semantic JSON or binary data.
    /// </summary>
    public string? BeforeText { get; init; }

    /// <summary>
    /// Bounded readable text from the candidate item when the compared item has
    /// text content. This is never raw semantic JSON or binary data.
    /// </summary>
    public string? AfterText { get; init; }

    /// <summary>
    /// Indicates that <see cref="BeforeText"/> was bounded to the comparer
    /// limit and is therefore only a prefix of the baseline text.
    /// </summary>
    public bool BeforeTextTruncated { get; init; }

    /// <summary>
    /// Indicates that <see cref="AfterText"/> was bounded to the comparer
    /// limit and is therefore only a prefix of the candidate text.
    /// </summary>
    public bool AfterTextTruncated { get; init; }
}

public sealed record VersionHistorySnapshotAreaComparison(
    string Area,
    VersionHistorySnapshotAreaSummary Summary,
    IReadOnlyList<VersionHistorySnapshotChangeEntry> Entries);

public enum VersionHistoryRestoreScope
{
    WholeProject,
    MajorAreas,
    SelectedChapters,
}

public enum VersionHistoryAnnotationRestoreMode
{
    Exclude,
    SelectedChapterAnnotations,
    AllAnnotations,
}

/// <summary>
/// A restore request that can be passed to a later restorer without coupling
/// comparison to database mutation. Chapter selection includes explicit
/// annotation behavior so annotations are never restored accidentally.
/// </summary>
public sealed record VersionHistoryRestoreSelection(
    VersionHistoryRestoreScope Scope,
    IReadOnlyList<string> Areas,
    IReadOnlyList<Guid> ChapterIds,
    VersionHistoryAnnotationRestoreMode AnnotationMode)
{
    public static VersionHistoryRestoreSelection ForWholeProject() =>
        new(
            VersionHistoryRestoreScope.WholeProject,
            VersionHistorySnapshotContract.IncludedAreas.ToList(),
            [],
            VersionHistoryAnnotationRestoreMode.AllAnnotations);

    public static VersionHistoryRestoreSelection ForMajorAreas(IEnumerable<string> areas)
    {
        ArgumentNullException.ThrowIfNull(areas);
        var selected = NormalizeAreas(areas);
        if (selected.Count == 0)
            throw new ArgumentException("At least one major area is required.", nameof(areas));

        return new(
            VersionHistoryRestoreScope.MajorAreas,
            selected,
            [],
            selected.Contains("narrative", StringComparer.Ordinal)
                ? VersionHistoryAnnotationRestoreMode.AllAnnotations
                : VersionHistoryAnnotationRestoreMode.Exclude);
    }

    public static VersionHistoryRestoreSelection ForSelectedChapters(
        IEnumerable<Guid> chapterIds,
        VersionHistoryAnnotationRestoreMode annotationMode = VersionHistoryAnnotationRestoreMode.SelectedChapterAnnotations)
    {
        ArgumentNullException.ThrowIfNull(chapterIds);
        var selected = chapterIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
        if (selected.Count == 0)
            throw new ArgumentException("At least one chapter ID is required.", nameof(chapterIds));
        if (annotationMode == VersionHistoryAnnotationRestoreMode.AllAnnotations)
            throw new ArgumentException("Selected-chapter restores cannot select all annotations.", nameof(annotationMode));

        return new(
            VersionHistoryRestoreScope.SelectedChapters,
            ["narrative"],
            selected,
            annotationMode);
    }

    private static List<string> NormalizeAreas(IEnumerable<string> areas)
    {
        var known = VersionHistorySnapshotContract.IncludedAreas;
        var selected = areas
            .Where(area => !string.IsNullOrWhiteSpace(area))
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        var unknown = selected.Except(known, StringComparer.Ordinal).OrderBy(area => area, StringComparer.Ordinal).FirstOrDefault();
        if (unknown is not null)
            throw new ArgumentException($"Unknown snapshot area '{unknown}'.", nameof(areas));

        return known.Where(selected.Contains).ToList();
    }
}

public sealed record VersionHistorySnapshotComparison(
    Guid RepositoryId,
    Guid ProjectId,
    bool IsIdentical,
    IReadOnlyList<VersionHistorySnapshotAreaComparison> Areas,
    VersionHistoryRestoreSelection DefaultRestoreSelection)
{
    public VersionHistorySnapshotAreaComparison GetArea(string area) =>
        Areas.FirstOrDefault(item => string.Equals(item.Area, area, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"Snapshot comparison area '{area}' was not found.");
}
