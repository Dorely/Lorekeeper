namespace Lorekeeper.Knowledge;

public record TextChunk(int Index, string Content);

/// <summary>
/// Splits long text into overlapping chunks suitable for embedding. Empty/whitespace
/// input yields an empty list. Implementations should prefer paragraph/sentence
/// boundaries when one exists near the target chunk size.
/// </summary>
public interface ITextChunker
{
    IReadOnlyList<TextChunk> Chunk(string text);
}
