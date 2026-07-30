using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Lorekeeper.Models;

[JsonConverter(typeof(JsonStringEnumConverter<BookKind>))]
public enum BookKind
{
    Unspecified = 0,
    Novel,
    Novella,
    ShortStory,
    StoryCollection,
    NarrativeNonfiction,
    GeneralNonfiction,
    PictureBook,
    IllustratedBook,
    Poetry,
    Other,
}

[JsonConverter(typeof(JsonStringEnumConverter<BookBriefField>))]
public enum BookBriefField
{
    BookKind,
    Premise,
    Genre,
    PrimaryThemes,
    Purpose,
    CreativeConstraints,
    TargetAudience,
    MinimumReaderAge,
    MaximumReaderAge,
    ReadingLevelGuidance,
    TargetWordCount,
    PointOfView,
    Tense,
    VoiceAndTone,
    LanguageLocale,
    HouseStyle,
    ReadAloudPriority,
    AccessibilityGoals,
    VisualDirection,
}

/// <summary>
/// Canonical project-level creative direction. Publication metadata and physical output
/// geometry remain in <see cref="PublicationEdition"/>; story canon remains in the outline,
/// entities, links, beats, and project facts.
/// </summary>
public sealed class BookBrief
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public BookKind BookKind { get; set; } = BookKind.Unspecified;
    public string Premise { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public string PrimaryThemes { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public string CreativeConstraints { get; set; } = string.Empty;
    public string TargetAudience { get; set; } = string.Empty;
    public int? MinimumReaderAge { get; set; }
    public int? MaximumReaderAge { get; set; }
    public string ReadingLevelGuidance { get; set; } = string.Empty;
    public int? TargetWordCount { get; set; }
    public string PointOfView { get; set; } = string.Empty;
    public string Tense { get; set; } = string.Empty;
    public string VoiceAndTone { get; set; } = string.Empty;
    public string LanguageLocale { get; set; } = string.Empty;
    public string HouseStyle { get; set; } = string.Empty;
    public bool? ReadAloudPriority { get; set; }
    public string AccessibilityGoals { get; set; } = string.Empty;
    public string VisualDirection { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Partial Book Brief update. Null properties mean unchanged; names in
/// <see cref="ClearFields"/> explicitly restore a field to its empty value.
/// </summary>
public sealed class BookBriefPatch
{
    public BookKind? BookKind { get; init; }
    public string? Premise { get; init; }
    public string? Genre { get; init; }
    public string? PrimaryThemes { get; init; }
    public string? Purpose { get; init; }
    public string? CreativeConstraints { get; init; }
    public string? TargetAudience { get; init; }
    public int? MinimumReaderAge { get; init; }
    public int? MaximumReaderAge { get; init; }
    public string? ReadingLevelGuidance { get; init; }
    public int? TargetWordCount { get; init; }
    public string? PointOfView { get; init; }
    public string? Tense { get; init; }
    public string? VoiceAndTone { get; init; }
    public string? LanguageLocale { get; init; }
    public string? HouseStyle { get; init; }
    public bool? ReadAloudPriority { get; init; }
    public string? AccessibilityGoals { get; init; }
    public string? VisualDirection { get; init; }
    [Description("Fields to explicitly clear. Omit this property or pass an empty array for an ordinary update. Use only the listed BookBriefField enum values; never pass 'Unspecified'.")]
    public IReadOnlyList<BookBriefField>? ClearFields { get; init; }
}
