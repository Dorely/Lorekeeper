using Lorekeeper.Manuscripts;
using Lorekeeper.Models;

namespace Lorekeeper.Composition;

public static class CompositionObjectLayout
{
    public static CompositionObject FitWidth(CompositionObject item, CompositionBounds target) =>
        item with
        {
            Bounds = item.Bounds with
            {
                XPercent = target.XPercent,
                WidthPercent = target.WidthPercent,
            },
            ImageFit = item.Kind == CompositionObjectKind.Image
                ? FigureImageFit.Contain
                : item.ImageFit,
        };

    public static CompositionObject Center(CompositionObject item, CompositionBounds target) =>
        item with
        {
            Bounds = item.Bounds with
            {
                XPercent = target.XPercent + (target.WidthPercent - item.Bounds.WidthPercent) / 2,
                YPercent = target.YPercent + (target.HeightPercent - item.Bounds.HeightPercent) / 2,
            },
        };

    public static CompositionObject RotateQuarterTurn(CompositionObject item) =>
        item with { RotationDegrees = NormalizeRotation(item.RotationDegrees + 90) };

    public static CompositionObject ResetRotation(CompositionObject item) =>
        item with { RotationDegrees = 0 };

    private static double NormalizeRotation(double value)
    {
        var normalized = value % 360;
        return normalized > 180 ? normalized - 360 : normalized <= -180 ? normalized + 360 : normalized;
    }
}
