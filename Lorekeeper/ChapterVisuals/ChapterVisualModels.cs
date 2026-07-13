using Lorekeeper.Models;

namespace Lorekeeper.ChapterVisuals;

public sealed record ChapterVisualState(
    Guid ChapterId,
    ChapterVisualMode VisualMode,
    ChapterPageLayoutKind PageLayoutKind,
    IllustratedProseLayout IllustrationLayout,
    PicturePageLayout PageLayout,
    string Body);

public sealed record ChapterVisualModeUpdate(
    ChapterVisualMode VisualMode,
    ChapterPageLayoutKind? PageLayoutKind = null);

public sealed record ChapterImagePlacementRequest(
    PicturePageImagePlacementRole PicturePageRole = PicturePageImagePlacementRole.Freeform,
    Guid? TargetPictureImageElementId = null);

public sealed record ChapterImagePlacementResult(
    ChapterVisualState State,
    Guid ElementId);
