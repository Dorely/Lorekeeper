using System.Text;
using System.Text.Json;

namespace Lorekeeper.Manuscripts;

/// <summary>
/// Builds the semantic manuscript review projection from two rich documents.
/// This deliberately has no assistant-change or staging dependency: the
/// Review page can compare durable version-history snapshots and contest
/// drafts using the same renderer.
/// </summary>
public static class ManuscriptReviewDiffBuilder
{
    private const int MaxDiffCells = 1_000_000;

    public static bool TryBuild(
        ManuscriptDocument before,
        ManuscriptDocument current,
        string title,
        out ReviewDiff diff)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(current);

        var sections = new List<DiffSection>();
        AddSection(sections, "Body", "Body", ManuscriptCodec.ProjectPlainText(before), ManuscriptCodec.ProjectPlainText(current));
        AddSection(sections, "Structure", "Structure and formatting", FormatStructure(before), FormatStructure(current));
        AddSection(sections, "Visuals", "Figures and designed pages", FormatVisuals(before), FormatVisuals(current));

        diff = new ReviewDiff(
            string.IsNullOrWhiteSpace(title) ? "Chapter body" : title,
            null,
            sections);
        return sections.Count > 0;
    }

    public static DiffSection CreateSection(
        string key,
        string label,
        string oldText,
        string newText,
        Guid? ownerChangeId = null) =>
        new(
            key,
            label,
            oldText,
            newText,
            BuildDiffHunks(BuildDiffRows(oldText, newText)),
            ownerChangeId);

    private static void AddSection(
        ICollection<DiffSection> sections,
        string key,
        string label,
        string oldText,
        string newText)
    {
        if (string.Equals(oldText, newText, StringComparison.Ordinal))
            return;

        sections.Add(CreateSection(key, label, oldText, newText));
    }

    private static string FormatStructure(ManuscriptDocument document)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < document.Content.Count; index++)
        {
            var block = document.Content[index];
            if (index > 0)
                builder.Append('\n');

            builder.Append("Block ").Append(index + 1).Append(": ")
                .Append(block.Type).Append(" | style=").Append(block.StyleRole)
                .Append(" | heading=").Append(block.HeadingLevel?.ToString() ?? "none")
                .Append(" | publicationField=").Append(block.PublicationField?.ToString() ?? "none")
                .Append(" | designedPage=").Append(block.DesignedPageId?.ToString("N") ?? "none");
            if (block.ParagraphPresentation is { } paragraph)
            {
                builder.Append(" | paragraph=")
                    .Append(JsonSerializer.Serialize(paragraph));
            }

            var marks = block.Content
                .SelectMany(inline => inline.Marks)
                .Select(mark => mark.Type + (string.IsNullOrWhiteSpace(mark.Value) ? string.Empty : $"={mark.Value}"))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(mark => mark, StringComparer.Ordinal);
            var markText = string.Join(", ", marks);
            if (markText.Length > 0)
                builder.Append(" | marks=").Append(markText);

            if (block.Type == ManuscriptBlockType.Figure)
            {
                builder.Append(" | image=").Append(block.ImageId?.ToString("N") ?? "none")
                    .Append(" | alt=").Append(block.AltText ?? "none")
                    .Append(" | decorative=").Append(block.Decorative)
                    .Append(" | language=").Append(block.Language ?? "none")
                    .Append(" | accessibility=").Append(block.AccessibilityRole?.ToString() ?? "none")
                    .Append(" | figure=").Append(JsonSerializer.Serialize(block.FigurePresentation));
            }
        }

        return builder.ToString();
    }

    private static string FormatVisuals(ManuscriptDocument document)
    {
        var builder = new StringBuilder();
        foreach (var (block, index) in document.Content
                     .Select((block, index) => (block, index))
                     .Where(item => item.block.Type is ManuscriptBlockType.Figure or ManuscriptBlockType.DesignedPage
                         || item.block.ImageId is not null
                         || item.block.DesignedPageId is not null))
        {
            if (builder.Length > 0)
                builder.Append('\n');

            builder.Append("Block ").Append(index + 1).Append(": ").Append(block.Type)
                .Append(" | placementBlock=").Append(block.Id)
                .Append(" | image=").Append(block.ImageId?.ToString("N") ?? "none")
                .Append(" | page=").Append(block.DesignedPageId?.ToString("N") ?? "none")
                .Append(" | alt=").Append(block.AltText ?? "none")
                .Append(" | decorative=").Append(block.Decorative)
                .Append(" | language=").Append(block.Language ?? "none")
                .Append(" | accessibility=").Append(block.AccessibilityRole?.ToString() ?? "none")
                .Append(" | figure=").Append(JsonSerializer.Serialize(block.FigurePresentation))
                .Append(" | publicationField=").Append(block.PublicationField?.ToString() ?? "none");
        }

        return builder.ToString();
    }

    private static IReadOnlyList<DiffRow> BuildDiffRows(string oldText, string newText)
    {
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);
        if ((long)oldLines.Count * newLines.Count > MaxDiffCells)
        {
            return oldLines.Select((line, index) => RemovedRow(index + 1, line))
                .Concat(newLines.Select((line, index) => AddedRow(index + 1, line)))
                .ToList();
        }

        var table = new int[oldLines.Count + 1, newLines.Count + 1];
        for (var oldIndex = oldLines.Count - 1; oldIndex >= 0; oldIndex--)
        {
            for (var newIndex = newLines.Count - 1; newIndex >= 0; newIndex--)
            {
                table[oldIndex, newIndex] = string.Equals(oldLines[oldIndex], newLines[newIndex], StringComparison.Ordinal)
                    ? table[oldIndex + 1, newIndex + 1] + 1
                    : Math.Max(table[oldIndex + 1, newIndex], table[oldIndex, newIndex + 1]);
            }
        }

        var rows = new List<DiffRow>();
        var oldCursor = 0;
        var newCursor = 0;
        var nextPairId = 1;
        while (oldCursor < oldLines.Count || newCursor < newLines.Count)
        {
            if (oldCursor < oldLines.Count && newCursor < newLines.Count
                && string.Equals(oldLines[oldCursor], newLines[newCursor], StringComparison.Ordinal))
            {
                rows.Add(ContextRow(oldCursor + 1, newCursor + 1, oldLines[oldCursor]));
                oldCursor++;
                newCursor++;
                continue;
            }

            if (oldCursor < oldLines.Count && newCursor < newLines.Count
                && table[oldCursor + 1, newCursor + 1] == table[oldCursor, newCursor])
            {
                var pairId = nextPairId++;
                rows.Add(RemovedRow(oldCursor + 1, oldLines[oldCursor], pairId));
                rows.Add(AddedRow(newCursor + 1, newLines[newCursor], pairId));
                oldCursor++;
                newCursor++;
                continue;
            }

            var canRemove = oldCursor < oldLines.Count
                && (newCursor >= newLines.Count || table[oldCursor + 1, newCursor] >= table[oldCursor, newCursor + 1]);
            if (canRemove)
            {
                rows.Add(RemovedRow(oldCursor + 1, oldLines[oldCursor]));
                oldCursor++;
            }
            else
            {
                rows.Add(AddedRow(newCursor + 1, newLines[newCursor]));
                newCursor++;
            }
        }

        return rows;
    }

    private static IReadOnlyList<DiffHunk> BuildDiffHunks(IReadOnlyList<DiffRow> rows)
    {
        const int contextLines = 3;
        var changedIndexes = rows
            .Select((row, index) => (row, index))
            .Where(item => item.row.Kind != DiffRowKind.Context)
            .Select(item => item.index)
            .ToList();
        if (changedIndexes.Count == 0)
            return [];

        var ranges = new List<(int Start, int End)>();
        foreach (var changedIndex in changedIndexes)
        {
            var start = Math.Max(0, changedIndex - contextLines);
            var end = Math.Min(rows.Count - 1, changedIndex + contextLines);
            if (ranges.Count == 0 || start > ranges[^1].End + 1)
                ranges.Add((start, end));
            else
                ranges[^1] = (ranges[^1].Start, Math.Max(ranges[^1].End, end));
        }

        return ranges.Select(range =>
        {
            var hunkRows = rows.Skip(range.Start).Take(range.End - range.Start + 1).ToList();
            return new DiffHunk(
                FindHunkStart(rows, range.Start, oldLine: true),
                hunkRows.Count(row => row.OldLineNumber.HasValue),
                FindHunkStart(rows, range.Start, oldLine: false),
                hunkRows.Count(row => row.NewLineNumber.HasValue),
                hunkRows);
        }).ToList();
    }

    private static int FindHunkStart(IReadOnlyList<DiffRow> rows, int start, bool oldLine)
    {
        for (var index = start; index < rows.Count; index++)
        {
            var lineNumber = oldLine ? rows[index].OldLineNumber : rows[index].NewLineNumber;
            if (lineNumber.HasValue)
                return lineNumber.Value;
        }

        for (var index = start - 1; index >= 0; index--)
        {
            var lineNumber = oldLine ? rows[index].OldLineNumber : rows[index].NewLineNumber;
            if (lineNumber.HasValue)
                return lineNumber.Value + 1;
        }

        return 1;
    }

    private static IReadOnlyList<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .ToList();

    private static DiffRow ContextRow(int oldLineNumber, int newLineNumber, string text) =>
        new(oldLineNumber, newLineNumber, " ", text, DiffRowKind.Context, UnchangedSegments(text), null);

    private static DiffRow AddedRow(int newLineNumber, string text, int? pairId = null) =>
        new(null, newLineNumber, "+", text, DiffRowKind.Added, UnchangedSegments(text), pairId);

    private static DiffRow RemovedRow(int oldLineNumber, string text, int? pairId = null) =>
        new(oldLineNumber, null, "-", text, DiffRowKind.Removed, UnchangedSegments(text), pairId);

    private static IReadOnlyList<DiffSegment> UnchangedSegments(string text) =>
        string.IsNullOrEmpty(text) ? [] : [new DiffSegment(text, DiffSegmentKind.Unchanged)];
}

public sealed record ReviewDiff(string Title, string? Subtitle, IReadOnlyList<DiffSection> Sections)
{
    public int Additions => Sections.Sum(section => section.Additions);
    public int Deletions => Sections.Sum(section => section.Deletions);
}

public sealed record DiffSection(
    string Key,
    string Label,
    string OldText,
    string NewText,
    IReadOnlyList<DiffHunk> Hunks,
    Guid? OwnerChangeId = null)
{
    public int Additions => Hunks.Sum(hunk => hunk.Additions);
    public int Deletions => Hunks.Sum(hunk => hunk.Deletions);
}

public sealed record DiffHunk(int OldStart, int OldLength, int NewStart, int NewLength, IReadOnlyList<DiffRow> Rows)
{
    public int Additions => Rows.Count(row => row.Kind == DiffRowKind.Added);
    public int Deletions => Rows.Count(row => row.Kind == DiffRowKind.Removed);
}

public sealed record DiffRow(
    int? OldLineNumber,
    int? NewLineNumber,
    string Marker,
    string Text,
    DiffRowKind Kind,
    IReadOnlyList<DiffSegment> Segments,
    int? PairId);

public sealed record DiffSegment(string Text, DiffSegmentKind Kind);

public sealed record DiffFieldInput(string Key, string Label, string OldText, string NewText, Guid? OwnerChangeId = null)
{
    public DiffFieldInput(string label, string oldText, string newText, Guid? ownerChangeId = null)
        : this(label, label, oldText, newText, ownerChangeId)
    {
    }
}

public enum DiffRowKind
{
    Context,
    Added,
    Removed,
}

public enum DiffSegmentKind
{
    Unchanged,
    Added,
    Removed,
}

public enum ChapterBodyReviewLineAction
{
    Keep,
    Reject,
    Edit,
}
