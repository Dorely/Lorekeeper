using Lorekeeper.Manuscripts;

namespace Lorekeeper.Components.Pages.Projects.History;

public static class HistoryCompareDiffProjector
{
    private const int MaxRowsPerSection = 400;
    private const long MaxCellsPerPair = 250_000;
    private const long MaxCellsPerSection = 1_000_000;

    public static HistoryCompareDiffViewModel? Prepare(ReviewDiff? diff, CancellationToken cancellationToken = default)
    {
        if (diff is null)
            return null;

        var sections = new List<HistoryCompareDiffSectionViewModel>(diff.Sections.Count);
        foreach (var section in diff.Sections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remainingRows = MaxRowsPerSection;
            var remainingCells = MaxCellsPerSection;
            var hunks = new List<HistoryCompareDiffHunkViewModel>();
            foreach (var hunk in section.Hunks)
            {
                if (remainingRows == 0)
                    break;

                var rows = hunk.Rows.Take(remainingRows).ToList();
                remainingRows -= rows.Count;
                if (rows.Count > 0)
                {
                    hunks.Add(new HistoryCompareDiffHunkViewModel(
                        PrepareRows(rows, ref remainingCells, cancellationToken)));
                }
            }

            sections.Add(new HistoryCompareDiffSectionViewModel(
                section.Label,
                section.Additions,
                section.Deletions,
                hunks,
                section.Hunks.Sum(hunk => hunk.Rows.Count) > MaxRowsPerSection));
        }

        return new HistoryCompareDiffViewModel(sections);
    }

    private static IReadOnlyList<HistoryCompareDiffRowViewModel> PrepareRows(
        IReadOnlyList<DiffRow> rows,
        ref long remainingCells,
        CancellationToken cancellationToken)
    {
        var segments = rows
            .Select(row => row.Kind == DiffRowKind.Context
                ? row.Segments
                : WholeLine(row))
            .ToArray();

        var runStart = 0;
        while (runStart < rows.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rows[runStart].Kind == DiffRowKind.Context)
            {
                runStart++;
                continue;
            }

            var runEnd = runStart + 1;
            while (runEnd < rows.Count && rows[runEnd].Kind != DiffRowKind.Context)
                runEnd++;

            PrepareChangeRun(rows, runStart, runEnd, segments, ref remainingCells, cancellationToken);
            runStart = runEnd;
        }

        return rows.Select((row, index) => new HistoryCompareDiffRowViewModel(
                RowClass(row.Kind),
                LineNumber(row),
                row.Marker,
                segments[index]))
            .ToList();
    }

    private static void PrepareChangeRun(
        IReadOnlyList<DiffRow> rows,
        int start,
        int end,
        IReadOnlyList<DiffSegment>[] segments,
        ref long remainingCells,
        CancellationToken cancellationToken)
    {
        var removed = Enumerable.Range(start, end - start)
            .Where(index => rows[index].Kind == DiffRowKind.Removed)
            .ToList();
        var added = Enumerable.Range(start, end - start)
            .Where(index => rows[index].Kind == DiffRowKind.Added)
            .ToList();
        var similarityProfiles = removed.Concat(added)
            .ToDictionary(index => index, index => BuildSimilarityProfile(rows[index].Text));
        var pairedRemoved = new HashSet<int>();
        var pairedAdded = new HashSet<int>();

        foreach (var removedIndex in removed.Where(index => rows[index].PairId is not null))
        {
            var addedIndex = added.FirstOrDefault(index =>
                !pairedAdded.Contains(index) && rows[index].PairId == rows[removedIndex].PairId, -1);
            if (addedIndex < 0)
                continue;

            PreparePair(rows, removedIndex, addedIndex, segments, ref remainingCells, cancellationToken);
            pairedRemoved.Add(removedIndex);
            pairedAdded.Add(addedIndex);
        }

        foreach (var removedIndex in removed.Where(index => !pairedRemoved.Contains(index)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bestAddedIndex = -1;
            var bestScore = 0.2;
            foreach (var addedIndex in added.Where(index => !pairedAdded.Contains(index)))
            {
                var score = LineSimilarity(similarityProfiles[removedIndex], similarityProfiles[addedIndex])
                    - Math.Abs(removedIndex - addedIndex) * 0.025;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestAddedIndex = addedIndex;
                }
            }

            if (bestAddedIndex < 0)
                continue;

            PreparePair(rows, removedIndex, bestAddedIndex, segments, ref remainingCells, cancellationToken);
            pairedRemoved.Add(removedIndex);
            pairedAdded.Add(bestAddedIndex);
        }
    }

    private static void PreparePair(
        IReadOnlyList<DiffRow> rows,
        int removedIndex,
        int addedIndex,
        IReadOnlyList<DiffSegment>[] segments,
        ref long remainingCells,
        CancellationToken cancellationToken)
    {
        var oldTokens = TokenizeDisplayText(rows[removedIndex].Text);
        var newTokens = TokenizeDisplayText(rows[addedIndex].Text);
        var cells = (long)oldTokens.Count * newTokens.Count;
        if (oldTokens.Count == 0 || newTokens.Count == 0 || cells > MaxCellsPerPair || cells > remainingCells)
            return;

        remainingCells -= cells;
        var prepared = BuildTokenDiffSegments(oldTokens, newTokens, cancellationToken);
        segments[removedIndex] = prepared.Removed;
        segments[addedIndex] = prepared.Added;
    }

    private static PreparedPair BuildTokenDiffSegments(
        IReadOnlyList<DisplayToken> oldTokens,
        IReadOnlyList<DisplayToken> newTokens,
        CancellationToken cancellationToken)
    {
        var table = new int[oldTokens.Count + 1, newTokens.Count + 1];
        for (var oldIndex = oldTokens.Count - 1; oldIndex >= 0; oldIndex--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var newIndex = newTokens.Count - 1; newIndex >= 0; newIndex--)
            {
                table[oldIndex, newIndex] = TokensEquivalent(oldTokens[oldIndex], newTokens[newIndex])
                    ? table[oldIndex + 1, newIndex + 1] + 1
                    : Math.Max(table[oldIndex + 1, newIndex], table[oldIndex, newIndex + 1]);
            }
        }

        var removed = new List<DiffSegment>();
        var added = new List<DiffSegment>();
        var oldCursor = 0;
        var newCursor = 0;
        while (oldCursor < oldTokens.Count && newCursor < newTokens.Count)
        {
            if (TokensEquivalent(oldTokens[oldCursor], newTokens[newCursor]))
            {
                AddSegment(removed, oldTokens[oldCursor].Text, DiffSegmentKind.Unchanged);
                AddSegment(added, newTokens[newCursor].Text, DiffSegmentKind.Unchanged);
                oldCursor++;
                newCursor++;
            }
            else if (table[oldCursor + 1, newCursor] >= table[oldCursor, newCursor + 1])
            {
                AddSegment(removed, oldTokens[oldCursor++].Text, DiffSegmentKind.Removed);
            }
            else
            {
                AddSegment(added, newTokens[newCursor++].Text, DiffSegmentKind.Added);
            }
        }

        while (oldCursor < oldTokens.Count)
            AddSegment(removed, oldTokens[oldCursor++].Text, DiffSegmentKind.Removed);
        while (newCursor < newTokens.Count)
            AddSegment(added, newTokens[newCursor++].Text, DiffSegmentKind.Added);

        return new PreparedPair(removed, added);
    }

    private static IReadOnlyList<DiffSegment> WholeLine(DiffRow row) =>
        string.IsNullOrEmpty(row.Text)
            ? []
            : [new DiffSegment(row.Text, row.Kind == DiffRowKind.Added ? DiffSegmentKind.Added : DiffSegmentKind.Removed)];

    private static void AddSegment(List<DiffSegment> segments, string text, DiffSegmentKind kind)
    {
        if (string.IsNullOrEmpty(text))
            return;
        if (segments.Count > 0 && segments[^1].Kind == kind)
        {
            segments[^1] = segments[^1] with { Text = segments[^1].Text + text };
            return;
        }

        segments.Add(new DiffSegment(text, kind));
    }

    private static IReadOnlyList<DisplayToken> TokenizeDisplayText(string text)
    {
        var tokens = new List<DisplayToken>();
        var index = 0;
        while (index < text.Length)
        {
            var start = index;
            var kind = TokenKind(text[index]);
            index++;
            while (index < text.Length && TokenKind(text[index]) == kind)
                index++;

            var tokenText = text[start..index];
            tokens.Add(new DisplayToken(tokenText, kind == DisplayTokenKind.Word ? tokenText.ToUpperInvariant() : tokenText, kind));
        }

        return tokens;
    }

    private static SimilarityProfile BuildSimilarityProfile(string line)
    {
        var words = TokenizeDisplayText(line)
            .Where(token => token.Kind == DisplayTokenKind.Word)
            .Select(token => token.Key)
            .ToList();
        return new SimilarityProfile(
            line,
            words.Count,
            words.GroupBy(word => word, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal));
    }

    private static double LineSimilarity(SimilarityProfile oldLine, SimilarityProfile newLine)
    {
        if (string.Equals(oldLine.Text, newLine.Text, StringComparison.Ordinal))
            return 1;
        if (oldLine.WordCount == 0 || newLine.WordCount == 0)
            return 0;

        var overlap = oldLine.WordCounts.Sum(item =>
            newLine.WordCounts.TryGetValue(item.Key, out var count) ? Math.Min(item.Value, count) : 0);
        return (2.0 * overlap) / (oldLine.WordCount + newLine.WordCount);
    }

    private static string RowClass(DiffRowKind kind) => kind switch
    {
        DiffRowKind.Added => "review-diff-row review-diff-row--added",
        DiffRowKind.Removed => "review-diff-row review-diff-row--removed",
        _ => "review-diff-row review-diff-row--context",
    };

    private static string LineNumber(DiffRow row) =>
        (row.Kind == DiffRowKind.Removed ? row.OldLineNumber : row.NewLineNumber ?? row.OldLineNumber)?.ToString() ?? string.Empty;

    private static DisplayTokenKind TokenKind(char character) =>
        char.IsWhiteSpace(character)
            ? DisplayTokenKind.Whitespace
            : char.IsLetterOrDigit(character) || character is '_' or '\''
                ? DisplayTokenKind.Word
                : DisplayTokenKind.Punctuation;

    private static bool TokensEquivalent(DisplayToken oldToken, DisplayToken newToken) =>
        oldToken.Kind == newToken.Kind
        && string.Equals(oldToken.Kind == DisplayTokenKind.Word ? oldToken.Key : oldToken.Text,
            newToken.Kind == DisplayTokenKind.Word ? newToken.Key : newToken.Text,
            StringComparison.Ordinal);

    private sealed record DisplayToken(string Text, string Key, DisplayTokenKind Kind);
    private sealed record SimilarityProfile(string Text, int WordCount, IReadOnlyDictionary<string, int> WordCounts);
    private sealed record PreparedPair(IReadOnlyList<DiffSegment> Removed, IReadOnlyList<DiffSegment> Added);

    private enum DisplayTokenKind
    {
        Word,
        Punctuation,
        Whitespace,
    }
}

public sealed record HistoryCompareDiffViewModel(IReadOnlyList<HistoryCompareDiffSectionViewModel> Sections);

public sealed record HistoryCompareDiffSectionViewModel(
    string Label,
    int Additions,
    int Deletions,
    IReadOnlyList<HistoryCompareDiffHunkViewModel> Hunks,
    bool Truncated);

public sealed record HistoryCompareDiffHunkViewModel(IReadOnlyList<HistoryCompareDiffRowViewModel> Rows);

public sealed record HistoryCompareDiffRowViewModel(
    string CssClass,
    string LineNumber,
    string Marker,
    IReadOnlyList<DiffSegment> Segments);
