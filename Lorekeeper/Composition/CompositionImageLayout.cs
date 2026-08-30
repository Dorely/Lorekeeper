using Lorekeeper.Manuscripts;
using Lorekeeper.Models;

namespace Lorekeeper.Composition;

public static class CompositionImageLayout
{
    private const double CoverageTolerance = .01;

    public static bool RetainsAspectRatio(CompositionObject item) =>
        item.ImageFit != FigureImageFit.Stretch;

    public static CompositionObject FillCanvas(CompositionObject item, bool retainAspectRatio)
    {
        if (item.Kind != CompositionObjectKind.Image)
            throw new ArgumentException("Only an image object can fill a composition canvas.", nameof(item));

        return item with
        {
            Bounds = new CompositionBounds(),
            ImageFit = retainAspectRatio ? FigureImageFit.Cover : FigureImageFit.Stretch,
        };
    }

    public static CompositionObject FillRegion(
        CompositionObject item,
        CompositionRegionConstraint region,
        CompositionBounds regionBounds,
        bool retainAspectRatio = true)
    {
        if (item.Kind != CompositionObjectKind.Image)
            throw new ArgumentException("Only an image object can fill a composition region.", nameof(item));
        if (region is not (CompositionRegionConstraint.Back or CompositionRegionConstraint.Spine or CompositionRegionConstraint.Front))
            throw new ArgumentException("Cover region fill supports Back, Spine, or Front.", nameof(region));
        return item with
        {
            Bounds = regionBounds,
            RegionConstraint = region,
            ImageFit = retainAspectRatio ? FigureImageFit.Cover : FigureImageFit.Stretch,
        };
    }

    public static bool FrameCoversCanvas(CompositionObject item)
    {
        if (item.Kind != CompositionObjectKind.Image
            || item.GroupId is not null
            || !HasCanvasAlignedRotation(item.RotationDegrees))
            return false;

        var bounds = item.Bounds;
        return bounds.XPercent <= CoverageTolerance
            && bounds.YPercent <= CoverageTolerance
            && bounds.XPercent + bounds.WidthPercent >= 100 - CoverageTolerance
            && bounds.YPercent + bounds.HeightPercent >= 100 - CoverageTolerance;
    }

    public static bool ImageCoversCanvas(CompositionObject item) =>
        FrameCoversCanvas(item) && item.ImageFit != FigureImageFit.Contain;

    public static string Presentation(CompositionObject item) => item.ImageFit switch
    {
        FigureImageFit.Cover => "crop-to-fill",
        FigureImageFit.Stretch => "stretch-to-fill",
        _ => "show-whole-image",
    };

    private static bool HasCanvasAlignedRotation(double rotationDegrees)
    {
        var normalized = Math.Abs(rotationDegrees % 180);
        return normalized <= CoverageTolerance;
    }
}
