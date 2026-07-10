using Lorekeeper.Models;

namespace Lorekeeper.ChapterVisuals;

public interface IChapterVisualService
{
    Task<ChapterVisualState?> GetAsync(Guid chapterId, CancellationToken cancellationToken = default);
    Task<ChapterVisualState> SetModeAsync(Guid chapterId, ChapterVisualModeUpdate update, CancellationToken cancellationToken = default);
    Task<ChapterImagePlacementResult> AddImageToChapterAsync(Guid projectId, Guid chapterId, Guid imageId, CancellationToken cancellationToken = default);
    Task<ChapterVisualState> SaveIllustrationLayoutAsync(Guid chapterId, IllustratedProseLayout layout, CancellationToken cancellationToken = default);
    Task<ChapterVisualState> SavePageLayoutAsync(Guid chapterId, PicturePageLayout layout, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChapterVisualSnapshot>> RenderSnapshotsAsync(
        Guid chapterId,
        int maxEdge = 1400,
        bool includeGuides = true,
        CancellationToken cancellationToken = default);
    Task RemoveImageReferencesAsync(Guid projectId, Guid imageId, CancellationToken cancellationToken = default);
    string BuildManifest(ChapterVisualState state, IReadOnlyDictionary<Guid, string>? imageNames = null);
}

public sealed record ChapterVisualSnapshot(
    int PageNumber,
    string FileName,
    string ContentType,
    byte[] Data)
{
    public IReadOnlyList<ChapterVisualTextFitDiagnostic> TextFitDiagnostics { get; init; } = [];
}

public sealed record ChapterVisualTextFitDiagnostic(
    Guid ElementId,
    int WrappedLineCount,
    int DrawnLineCount,
    double AvailableHeightPixels,
    double RequiredHeightPixels,
    bool Fits);
