namespace Lorekeeper.Models;

/// <summary>
/// Application-wide speed-reading preferences for the chapter Read view. A
/// single row (<see cref="SingletonId"/>) stores the selected pacing style, one
/// words-per-minute preset per style so switching styles keeps each preset, and
/// whether playback advances to the next chapter automatically.
/// </summary>
public class SpeedReadingSettings
{
    public const int SingletonId = 1;

    public const string ChunkStyle = "Chunks";
    public const string RsvpStyle = "Rsvp";

    public const int MinimumWordsPerMinute = 150;
    public const int MaximumWordsPerMinute = 700;
    public const int DefaultChunkWordsPerMinute = 300;
    public const int DefaultRsvpWordsPerMinute = 350;

    public int Id { get; set; } = SingletonId;

    public string Style { get; set; } = ChunkStyle;

    public int ChunkWordsPerMinute { get; set; } = DefaultChunkWordsPerMinute;

    public int RsvpWordsPerMinute { get; set; } = DefaultRsvpWordsPerMinute;

    public bool AutoAdvance { get; set; }
}
