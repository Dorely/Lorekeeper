using System.Text.Json.Serialization;

namespace Lorekeeper.Models;

[JsonConverter(typeof(JsonStringEnumConverter<ChapterVisualMode>))]
public enum ChapterVisualMode
{
    Prose,
    IllustratedProse,
    PicturePage,
}

[JsonConverter(typeof(JsonStringEnumConverter<ChapterPageLayoutKind>))]
public enum ChapterPageLayoutKind
{
    SinglePortrait,
    SingleLandscape,
    DoublePortrait,
    DoubleLandscape,
}

internal static class LegacyPicturePageGeometry
{
    private const double PointsPerInch = 72;

    public static (double WidthPoints, double HeightPoints) SurfacePoints(ChapterPageLayoutKind kind)
    {
        var isLandscape = kind is ChapterPageLayoutKind.SingleLandscape or ChapterPageLayoutKind.DoubleLandscape;
        var isSpread = kind is ChapterPageLayoutKind.DoublePortrait or ChapterPageLayoutKind.DoubleLandscape;
        var leafWidthInches = isLandscape ? 11d : 8.5d;
        var leafHeightInches = isLandscape ? 8.5d : 11d;
        return (
            leafWidthInches * (isSpread ? 2 : 1) * PointsPerInch,
            leafHeightInches * PointsPerInch);
    }
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
