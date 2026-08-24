using Lorekeeper.Manuscripts;

namespace Lorekeeper.SpeedReading;

/// <summary>
/// Projects a manuscript v4 document into the ordered speed-reading playlist.
/// Word offsets are UTF-16 offsets into <see cref="ManuscriptCodec.Text"/> of
/// the owning block, so they align with annotation anchors and
/// <c>ManuscriptViewLocation</c>. Designed Pages are represented as skip
/// placeholders rather than expanded; their content is not editable from the
/// prose editor that pause-to-edit targets.
/// </summary>
public static class SpeedReadingPlaylistBuilder
{
    private const double SentenceEndWeight = 1.6;
    private const double ParagraphEndWeight = 2.2;
    private const double PhrasePauseWeight = 1.3;
    private const double QuoteEndBonus = 0.4;
    private const double LongWordBonus = 0.3;
    private const double VeryLongWordBonus = 0.6;
    private const int LongWordLength = 9;
    private const int VeryLongWordLength = 13;
    private const int MaxChunkWords = 4;
    private const int MaxChunkLength = 18;

    public static SpeedReadingPlaylist Build(ManuscriptDocument document)
    {
        var items = new List<SpeedReadingItem>();
        var blockIds = new List<string>(document.Content.Count);

        foreach (var block in document.Content)
        {
            var blockIndex = blockIds.Count;
            blockIds.Add(block.Id);
            switch (block.Type)
            {
                case ManuscriptBlockType.Paragraph:
                case ManuscriptBlockType.Heading:
                case ManuscriptBlockType.BlockQuote:
                case ManuscriptBlockType.ListItem:
                    AppendWords(items, ManuscriptCodec.Text(block), blockIndex);
                    break;
                case ManuscriptBlockType.Figure:
                    items.Add(new SpeedReadingItem(
                        SpeedReadingItemKind.Figure,
                        ManuscriptCodec.Text(block),
                        blockIndex,
                        Offset: 0,
                        Weight: 1,
                        (int)SpeedReadingItemFlags.ParagraphEnd));
                    break;
                case ManuscriptBlockType.DesignedPage:
                    items.Add(new SpeedReadingItem(
                        SpeedReadingItemKind.DesignedPage,
                        string.Empty,
                        blockIndex,
                        Offset: 0,
                        Weight: 1,
                        (int)SpeedReadingItemFlags.ParagraphEnd));
                    break;
                case ManuscriptBlockType.SceneBreak:
                    items.Add(new SpeedReadingItem(
                        SpeedReadingItemKind.SceneBreak,
                        string.Empty,
                        blockIndex,
                        Offset: 0,
                        Weight: 1,
                        (int)SpeedReadingItemFlags.ParagraphEnd));
                    break;
            }
        }

        return new SpeedReadingPlaylist(items, blockIds, BuildChunks(items));
    }

    private static void AppendWords(List<SpeedReadingItem> items, string text, int blockIndex)
    {
        var blockStart = items.Count;
        var sentenceStart = true;
        var position = 0;
        while (position < text.Length)
        {
            if (char.IsWhiteSpace(text[position]))
            {
                position++;
                continue;
            }

            var start = position;
            while (position < text.Length && !char.IsWhiteSpace(text[position]))
                position++;
            var word = text[start..position];

            var flags = SpeedReadingItemFlags.None;
            if (sentenceStart)
                flags |= SpeedReadingItemFlags.SentenceStart;
            var sentenceEnd = EndsSentence(word);
            if (sentenceEnd)
                flags |= SpeedReadingItemFlags.SentenceEnd;
            if (EndsQuote(word))
                flags |= SpeedReadingItemFlags.QuoteEnd;

            items.Add(new SpeedReadingItem(
                SpeedReadingItemKind.Word,
                word,
                blockIndex,
                start,
                Weight(word, flags),
                (int)flags));
            sentenceStart = sentenceEnd;
        }

        if (items.Count > blockStart)
        {
            var last = items[^1];
            var lastFlags = (SpeedReadingItemFlags)last.Flags
                | SpeedReadingItemFlags.SentenceEnd
                | SpeedReadingItemFlags.ParagraphEnd;
            items[^1] = last with
            {
                Flags = (int)lastFlags,
                Weight = Weight(last.Text, lastFlags),
            };
        }
    }

    private static double Weight(string word, SpeedReadingItemFlags flags)
    {
        var weight = 1.0;
        if (word.Length >= VeryLongWordLength)
            weight += VeryLongWordBonus;
        else if (word.Length >= LongWordLength)
            weight += LongWordBonus;

        if (flags.HasFlag(SpeedReadingItemFlags.ParagraphEnd))
            weight *= ParagraphEndWeight;
        else if (flags.HasFlag(SpeedReadingItemFlags.SentenceEnd))
            weight *= SentenceEndWeight;
        else if (EndsPhrase(word))
            weight *= PhrasePauseWeight;

        if (flags.HasFlag(SpeedReadingItemFlags.QuoteEnd))
            weight += QuoteEndBonus;
        return Math.Round(weight, 2);
    }

    private static IReadOnlyList<SpeedReadingChunk> BuildChunks(List<SpeedReadingItem> items)
    {
        var chunks = new List<SpeedReadingChunk>();
        var index = 0;
        while (index < items.Count)
        {
            var first = items[index];
            if (first.Kind != SpeedReadingItemKind.Word)
            {
                chunks.Add(new SpeedReadingChunk(index, 1, first.Weight));
                index++;
                continue;
            }

            var count = 1;
            var length = first.Text.Length;
            var weight = first.Weight;
            while (count < MaxChunkWords && index + count < items.Count)
            {
                var current = items[index + count - 1];
                var next = items[index + count];
                if (next.Kind != SpeedReadingItemKind.Word
                    || next.BlockIndex != first.BlockIndex
                    || IsChunkBoundary(current)
                    || length + 1 + next.Text.Length > MaxChunkLength)
                {
                    break;
                }

                length += 1 + next.Text.Length;
                weight += next.Weight;
                count++;
            }

            chunks.Add(new SpeedReadingChunk(index, count, Math.Round(weight, 2)));
            index += count;
        }

        return chunks;
    }

    private static bool IsChunkBoundary(SpeedReadingItem item) =>
        ((SpeedReadingItemFlags)item.Flags).HasFlag(SpeedReadingItemFlags.SentenceEnd)
        || EndsPhrase(item.Text);

    private static bool EndsSentence(string word)
    {
        var end = TrimClosing(word);
        return end >= 0 && word[end] is '.' or '!' or '?' or '…';
    }

    private static bool EndsPhrase(string word)
    {
        var end = TrimClosing(word);
        return end >= 0 && word[end] is ',' or ';' or ':' or '—' or '–';
    }

    private static bool EndsQuote(string word) =>
        word.Length > 0 && word[^1] is '"' or '”' or '»';

    private static int TrimClosing(string word)
    {
        var end = word.Length - 1;
        while (end >= 0 && word[end] is '"' or '”' or '’' or '\'' or ')' or ']' or '»')
            end--;
        return end;
    }
}
