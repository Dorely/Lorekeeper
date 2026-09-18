namespace Lorekeeper.Citations;

public sealed record CitationRun(string Text, bool Italic = false);

/// <summary>Semantic citation text; punctuation operations preserve run formatting.</summary>
internal sealed class CitationText
{
    public IReadOnlyList<CitationRun> Runs { get; }
    public string Text => string.Concat(Runs.Select(run => run.Text));

    private CitationText(IEnumerable<CitationRun> runs)
    {
        var normalized = new List<CitationRun>();
        foreach (var run in runs.Where(run => run.Text.Length > 0))
        {
            if (normalized.Count > 0 && normalized[^1].Italic == run.Italic)
                normalized[^1] = normalized[^1] with { Text = normalized[^1].Text + run.Text };
            else
                normalized.Add(run);
        }
        Runs = normalized;
    }

    public static CitationText Italic(string text) => new([new(text, true)]);
    public static CitationText FromRuns(IEnumerable<CitationRun> runs) => new(runs);
    public static implicit operator CitationText(string text) => new([new(text)]);
    public static CitationText operator +(CitationText left, CitationText right) => new(left.Runs.Concat(right.Runs));

    public static CitationText Join(string separator, params CitationText[] values)
    {
        CitationText result = string.Empty;
        foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value.Text)))
        {
            if (result.Runs.Count > 0)
                result += separator;
            result += value.Trim();
        }
        return result;
    }

    public CitationText Trim() => Slice(Text.Length - Text.TrimStart().Length, Text.Trim().Length);
    public CitationText TrimStart(params char[] characters)
    {
        var trimmed = Text.TrimStart(characters);
        return Slice(Text.Length - trimmed.Length, trimmed.Length);
    }
    public CitationText Period()
    {
        var trimmed = Text.TrimEnd();
        return trimmed.EndsWith('.') || trimmed.EndsWith('?') || trimmed.EndsWith('!')
            || trimmed.EndsWith(".\"", StringComparison.Ordinal) || trimmed.EndsWith("?\"", StringComparison.Ordinal)
            || trimmed.EndsWith("!\"", StringComparison.Ordinal)
            ? Slice(0, trimmed.Length) : Slice(0, trimmed.Length) + ".";
    }

    public CitationText WithoutTerminalPeriod() => Text.EndsWith('.') ? Slice(0, Text.Length - 1) : this;

    private CitationText Slice(int start, int length)
    {
        var result = new List<CitationRun>();
        var offset = 0;
        foreach (var run in Runs)
        {
            var first = Math.Max(start, offset);
            var end = Math.Min(start + length, offset + run.Text.Length);
            if (end > first)
                result.Add(run with { Text = run.Text.Substring(first - offset, end - first) });
            offset += run.Text.Length;
        }
        return new(result);
    }
}
