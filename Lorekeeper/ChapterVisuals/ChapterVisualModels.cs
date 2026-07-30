using Lorekeeper.Models;

namespace Lorekeeper.ChapterVisuals;

public sealed record ChapterVisualState(
    Guid ChapterId,
    long ManuscriptRevision,
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
    Guid? TargetPictureImageElementId = null,
    double? XPercent = null,
    double? YPercent = null,
    double? WidthPercent = null,
    double? HeightPercent = null,
    ChapterImageFit? Fit = null,
    int? ZIndex = null);

public sealed record ChapterImagePlacementResult(
    ChapterVisualState State,
    Guid ElementId);

public sealed record PicturePageTextFitResult(
    ChapterVisualState State,
    Guid ElementId,
    double PreviousFontSizePoints,
    double FontSizePoints,
    ChapterVisualTextFitDiagnostic Diagnostic,
    bool HitMinimum,
    bool HitMaximum);

public sealed class ChapterVisualRevisionConflictException(
    string layoutKind,
    long expected,
    long actual)
    : InvalidOperationException(
        $"{layoutKind} layout revision conflict: expected {expected}, current revision is {actual}. "
        + "Reload the chapter visual layout before saving.")
{
    public long ExpectedRevision { get; } = expected;
    public long ActualRevision { get; } = actual;
}
