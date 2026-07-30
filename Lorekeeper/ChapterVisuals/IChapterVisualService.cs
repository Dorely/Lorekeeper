using Lorekeeper.Models;

namespace Lorekeeper.ChapterVisuals;

public interface IChapterVisualService
{
    Task<ChapterVisualState?> GetAsync(Guid chapterId, CancellationToken cancellationToken = default);
    Task<ChapterVisualState> SetModeAsync(Guid chapterId, ChapterVisualModeUpdate update, CancellationToken cancellationToken = default);
    Task<ChapterImagePlacementResult> AddImageToChapterAsync(
        Guid projectId,
        Guid chapterId,
        Guid imageId,
        ChapterImagePlacementRequest? placement = null,
        CancellationToken cancellationToken = default);
    Task<ChapterVisualState> SaveIllustrationLayoutAsync(Guid chapterId, IllustratedProseLayout layout, CancellationToken cancellationToken = default);
    Task<ChapterVisualState> SavePageLayoutAsync(Guid chapterId, PicturePageLayout layout, CancellationToken cancellationToken = default);
    Task<PicturePageTextFitResult> FitAndSavePicturePageTextAsync(
        Guid chapterId,
        PicturePageLayout layout,
        Guid textElementId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChapterVisualSnapshot>> RenderSnapshotsAsync(
        Guid chapterId,
        int maxEdge = 1400,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<Guid, ChapterPicturePageSurface>> RenderPicturePageSurfacesAsync(
        IReadOnlyCollection<Guid> chapterIds,
        int physicalPageLongEdgePixels = 2400,
        ChapterPicturePageSurfaceRotation rotation = ChapterPicturePageSurfaceRotation.None,
        CancellationToken cancellationToken = default);
    Task RemoveImageReferencesAsync(Guid projectId, Guid imageId, CancellationToken cancellationToken = default);
    Task RemoveImageReferencesUnderProjectMutationLeaseAsync(
        Guid projectId,
        Guid imageId,
        CancellationToken cancellationToken = default);
    Task<int> RepairTextLayoutsAsync(CancellationToken cancellationToken = default);
    string BuildManifest(
        ChapterVisualState state,
        IReadOnlyDictionary<Guid, string>? imageNames = null,
        IReadOnlyDictionary<string, string>? fontNames = null,
        bool includePicturePageGenerationGuidance = true);
}

public sealed record ChapterPicturePageSurface(
    Guid ChapterId,
    ChapterPageLayoutKind PageLayoutKind,
    int PhysicalPageWidthPixels,
    int PhysicalPageHeightPixels,
    int LeafCount,
    int SurfaceWidthPixels,
    int SurfaceHeightPixels,
    ChapterPicturePageSurfaceRotation Rotation,
    string FileName,
    string ContentType,
    byte[] Data,
    string AccessibleText);

public enum ChapterPicturePageSurfaceRotation
{
    None = 0,
    Clockwise90 = 1,
}

public sealed record ChapterVisualSnapshot(
    int PageNumber,
    string FileName,
    string ContentType,
    byte[] Data)
{
    public IReadOnlyList<ChapterVisualTextFitDiagnostic> TextFitDiagnostics { get; init; } = [];
    public IReadOnlyList<ChapterVisualLayoutDiagnostic> LayoutDiagnostics { get; init; } = [];
}

public sealed record ChapterVisualTextFitDiagnostic(
    Guid ElementId,
    int WrappedLineCount,
    int DrawnLineCount,
    double AvailableHeightPixels,
    double RequiredHeightPixels,
    bool Fits,
    bool FontFaceResolved);

public sealed record ChapterVisualLayoutDiagnostic(
    string Code,
    string Severity,
    string Message,
    Guid? ElementId = null,
    Guid? RelatedElementId = null,
    string? MeasuredValue = null,
    string? Threshold = null,
    string? SuggestedCorrection = null);
