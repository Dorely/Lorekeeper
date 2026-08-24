using System.Text.Json.Serialization;

namespace Lorekeeper.SpeedReading;

/// <summary>
/// Item kinds shared with the speed-reading JavaScript module. Values are part
/// of the payload contract and must match SpeedReadOverlay.razor.js.
/// </summary>
public static class SpeedReadingItemKind
{
    public const int Word = 0;
    public const int Figure = 1;
    public const int DesignedPage = 2;
    public const int SceneBreak = 3;
}

/// <summary>
/// Item flags shared with the speed-reading JavaScript module, encoded as a
/// bitmask. Values are part of the payload contract and must match
/// SpeedReadOverlay.razor.js.
/// </summary>
[Flags]
public enum SpeedReadingItemFlags
{
    None = 0,
    SentenceStart = 1,
    SentenceEnd = 2,
    ParagraphEnd = 4,
    QuoteEnd = 8,
}

/// <summary>
/// One playback item: a word with its UTF-16 start offset into
/// <c>ManuscriptCodec.Text</c> of its block, or an interstitial card for a
/// non-prose block. <c>Weight</c> multiplies the base per-word interval.
/// </summary>
public sealed record SpeedReadingItem(
    [property: JsonPropertyName("k")] int Kind,
    [property: JsonPropertyName("t")] string Text,
    [property: JsonPropertyName("b")] int BlockIndex,
    [property: JsonPropertyName("o")] int Offset,
    [property: JsonPropertyName("w")] double Weight,
    [property: JsonPropertyName("f")] int Flags);

/// <summary>
/// A precomputed chunk-pacing range: <c>Count</c> consecutive items starting at
/// <c>StartItem</c>, displayed together with an aggregate dwell weight.
/// Interstitial items always form single-item chunks.
/// </summary>
public sealed record SpeedReadingChunk(
    [property: JsonPropertyName("s")] int StartItem,
    [property: JsonPropertyName("n")] int Count,
    [property: JsonPropertyName("w")] double Weight);

/// <summary>
/// The complete word stream for one chapter revision, sent to the browser once
/// per playback session. Every item maps back to a stable manuscript block id
/// through <see cref="BlockIds"/> so a paused word converts losslessly into an
/// editor location.
/// </summary>
public sealed record SpeedReadingPlaylist(
    [property: JsonPropertyName("items")] IReadOnlyList<SpeedReadingItem> Items,
    [property: JsonPropertyName("blocks")] IReadOnlyList<string> BlockIds,
    [property: JsonPropertyName("chunks")] IReadOnlyList<SpeedReadingChunk> Chunks)
{
    [JsonIgnore]
    public bool HasWords => Items.Any(item => item.Kind == SpeedReadingItemKind.Word);
}
