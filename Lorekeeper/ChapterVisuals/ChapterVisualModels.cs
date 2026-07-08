using Lorekeeper.Models;

namespace Lorekeeper.ChapterVisuals;

public sealed record ChapterVisualState(
    Guid ChapterId,
    ChapterVisualMode VisualMode,
    double PicturePageWidthInches,
    double PicturePageHeightInches,
    bool PicturePageIsSpread,
    IllustratedProseLayout IllustrationLayout,
    PicturePageLayout PageLayout,
    string Body);

public sealed record ChapterVisualModeUpdate(
    ChapterVisualMode VisualMode,
    double? PicturePageWidthInches = null,
    double? PicturePageHeightInches = null,
    bool? PicturePageIsSpread = null);

public sealed record ChapterImagePlacementResult(
    ChapterVisualState State,
    Guid ElementId);
