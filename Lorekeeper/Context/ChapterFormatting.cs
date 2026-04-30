namespace Lorekeeper.Context;

/// <summary>
/// Renders chapter bodies with line numbers using the format <c>NNNN: line</c>.
/// Shared between the editor's gutter, the Context Feed preview, and the AI tool
/// reads so the LLM and the user always reference identical line numbers.
/// </summary>
public static class ChapterFormatting
{
    /// <summary>
    /// Returns <paramref name="body"/> with each line prefixed by a 1-based,
    /// zero-padded line number followed by <c>": "</c>. An empty body returns an
    /// empty string. Trailing newlines are preserved as numbered empty lines so
    /// the model can target them with insert/replace operations.
    /// </summary>
    public static string WithLineNumbers(string body)
    {
        if (string.IsNullOrEmpty(body)) return string.Empty;

        var lines = SplitLines(body);
        var width = Math.Max(4, lines.Count.ToString().Length);

        var sb = new System.Text.StringBuilder(body.Length + lines.Count * (width + 2));
        for (var i = 0; i < lines.Count; i++)
        {
            sb.Append((i + 1).ToString().PadLeft(width, '0'));
            sb.Append(": ");
            sb.Append(lines[i]);
            if (i < lines.Count - 1) sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Split into logical lines, normalizing CRLF/CR to LF.</summary>
    public static List<string> SplitLines(string body)
    {
        if (string.IsNullOrEmpty(body)) return [];
        var normalized = body.Replace("\r\n", "\n").Replace('\r', '\n');
        return [.. normalized.Split('\n')];
    }

    /// <summary>Join lines back into a body using LF line endings.</summary>
    public static string JoinLines(IEnumerable<string> lines) => string.Join("\n", lines);
}
