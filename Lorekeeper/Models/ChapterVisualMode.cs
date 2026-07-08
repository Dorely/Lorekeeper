using System.Text.Json.Serialization;

namespace Lorekeeper.Models;

[JsonConverter(typeof(JsonStringEnumConverter<ChapterVisualMode>))]
public enum ChapterVisualMode
{
    Prose,
    IllustratedProse,
    PicturePage,
}

[JsonConverter(typeof(JsonStringEnumConverter<ChapterImageFit>))]
public enum ChapterImageFit
{
    Contain,
    Cover,
    Fill,
}

[JsonConverter(typeof(JsonStringEnumConverter<ChapterImageAlignment>))]
public enum ChapterImageAlignment
{
    Left,
    Center,
    Right,
}

[JsonConverter(typeof(JsonStringEnumConverter<ChapterImageAnchorPosition>))]
public enum ChapterImageAnchorPosition
{
    BeforeParagraph,
    AfterParagraph,
}

[JsonConverter(typeof(JsonStringEnumConverter<ChapterTextVerticalAlign>))]
public enum ChapterTextVerticalAlign
{
    Top,
    Middle,
    Bottom,
}
