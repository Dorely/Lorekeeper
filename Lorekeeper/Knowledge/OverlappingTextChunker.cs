namespace Lorekeeper.Knowledge;

/// <summary>
/// Sliding-window chunker. Targets <c>Embeddings:ChunkSize</c> chars per chunk with
/// <c>Embeddings:ChunkOverlap</c> chars of overlap between adjacent chunks. When a
/// natural boundary (paragraph break, then sentence end, then whitespace) sits within
/// ±10% of the target end, splits there; otherwise hard-cuts.
/// </summary>
public class OverlappingTextChunker(IConfiguration configuration) : ITextChunker
{
    private int ChunkSize => Math.Max(100, configuration.GetValue("Embeddings:ChunkSize", 1200));
    private int ChunkOverlap => Math.Clamp(configuration.GetValue("Embeddings:ChunkOverlap", 200), 0, ChunkSize - 1);

    public IReadOnlyList<TextChunk> Chunk(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var size = ChunkSize;
        var overlap = ChunkOverlap;
        var slack = Math.Max(1, size / 10);

        var chunks = new List<TextChunk>();
        var start = 0;
        var index = 0;

        while (start < text.Length)
        {
            var remaining = text.Length - start;
            if (remaining <= size)
            {
                chunks.Add(new TextChunk(index, text[start..].Trim()));
                break;
            }

            var targetEnd = start + size;
            var minEnd = Math.Max(start + 1, targetEnd - slack);
            var maxEnd = Math.Min(text.Length, targetEnd + slack);

            var splitAt = FindBoundary(text, minEnd, maxEnd) ?? targetEnd;

            var content = text[start..splitAt].Trim();
            if (content.Length > 0)
            {
                chunks.Add(new TextChunk(index, content));
                index++;
            }

            var nextStart = splitAt - overlap;
            if (nextStart <= start) nextStart = start + 1;
            start = nextStart;
        }

        return chunks;
    }

    private static int? FindBoundary(string text, int min, int max)
    {
        // Prefer paragraph break.
        var paragraph = text.LastIndexOf("\n\n", max - 1, max - min, StringComparison.Ordinal);
        if (paragraph >= min) return paragraph + 2;

        // Then sentence end followed by whitespace.
        for (var i = max - 1; i >= min; i--)
        {
            var c = text[i];
            if ((c == '.' || c == '!' || c == '?') && i + 1 < text.Length && char.IsWhiteSpace(text[i + 1]))
                return i + 1;
        }

        // Then any whitespace.
        for (var i = max - 1; i >= min; i--)
        {
            if (char.IsWhiteSpace(text[i])) return i + 1;
        }

        return null;
    }
}
