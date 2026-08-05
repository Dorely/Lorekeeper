using Lorekeeper.Models;

namespace Lorekeeper.Composition;

public static class CompositionSceneResolver
{
    public static CompositionReadingOrderNormalization NormalizeLogicalReadingOrder(CompositionScene scene)
    {
        var normalizedOrders = new int?[scene.Objects.Count];
        var semanticObjects = scene.Objects
            .Select((item, index) => new { Item = item, Index = index })
            .Where(entry => !entry.Item.Decorative
                && entry.Item.SemanticRole != CompositionSemanticRole.Artifact)
            .OrderBy(entry => entry.Item.ReadingOrder is null)
            .ThenBy(entry => entry.Item.ReadingOrder)
            .ThenBy(entry => entry.Index)
            .ToList();

        for (var index = 0; index < semanticObjects.Count; index++)
            normalizedOrders[semanticObjects[index].Index] = index + 1;

        var changedObjectCount = 0;
        var objects = scene.Objects.Select((item, index) =>
        {
            var readingOrder = normalizedOrders[index];
            if (item.ReadingOrder != readingOrder)
                changedObjectCount++;
            return item with { ReadingOrder = readingOrder };
        }).ToList();

        return new CompositionReadingOrderNormalization(
            scene with { Objects = objects },
            changedObjectCount);
    }

    public static IReadOnlyList<CompositionObject> Flatten(CompositionScene scene)
    {
        var groups = scene.Objects
            .Where(item => item.Kind == CompositionObjectKind.Group)
            .ToDictionary(item => item.Id);
        var result = new List<CompositionObject>();
        foreach (var item in scene.Objects.Where(item => item.Kind != CompositionObjectKind.Group))
        {
            if (item.GroupId is not Guid groupId)
            {
                result.Add(item);
                continue;
            }

            if (!groups.TryGetValue(groupId, out var group))
                throw new InvalidDataException($"Composition object '{item.Id}' references a missing group.");

            var bounds = ResolveBounds(scene.Surface, item.Bounds, group.Bounds, group.RotationDegrees);
            result.Add(item with
            {
                Bounds = bounds,
                RotationDegrees = item.RotationDegrees + group.RotationDegrees,
                Opacity = item.Opacity * group.Opacity,
                Visible = item.Visible && group.Visible,
                Locked = item.Locked || group.Locked,
                ZIndex = item.ZIndex + group.ZIndex,
                GroupId = null,
            });
        }
        return result;
    }

    public static CompositionBounds ResolveBounds(
        CompositionSurface surface,
        CompositionBounds child,
        CompositionBounds group,
        double groupRotationDegrees)
    {
        var surfaceWidth = Math.Max(surface.WidthPoints, 1);
        var surfaceHeight = Math.Max(surface.HeightPoints, 1);
        var width = child.WidthPercent / 100 * group.WidthPercent;
        var height = child.HeightPercent / 100 * group.HeightPercent;
        var childCenterX = (group.XPercent + (child.XPercent + child.WidthPercent / 2) / 100 * group.WidthPercent)
            / 100 * surfaceWidth;
        var childCenterY = (group.YPercent + (child.YPercent + child.HeightPercent / 2) / 100 * group.HeightPercent)
            / 100 * surfaceHeight;
        var groupCenterX = (group.XPercent + group.WidthPercent / 2) / 100 * surfaceWidth;
        var groupCenterY = (group.YPercent + group.HeightPercent / 2) / 100 * surfaceHeight;
        var angle = groupRotationDegrees * Math.PI / 180;
        var deltaX = childCenterX - groupCenterX;
        var deltaY = childCenterY - groupCenterY;
        var rotatedCenterX = groupCenterX + deltaX * Math.Cos(angle) - deltaY * Math.Sin(angle);
        var rotatedCenterY = groupCenterY + deltaX * Math.Sin(angle) + deltaY * Math.Cos(angle);
        return new CompositionBounds
        {
            XPercent = (rotatedCenterX - width / 200 * surfaceWidth) / surfaceWidth * 100,
            YPercent = (rotatedCenterY - height / 200 * surfaceHeight) / surfaceHeight * 100,
            WidthPercent = width,
            HeightPercent = height,
        };
    }

    public static CompositionOpacityOverlap? FindPdfxTransparencyOverlap(CompositionScene scene)
    {
        var visibleLayers = scene.Layers.Where(layer => layer.Visible).ToDictionary(layer => layer.Id, layer => layer.Order);
        var painted = new List<(Guid Id, CompositionBounds Bounds)>();
        foreach (var item in Flatten(scene)
            .Where(item => item.Visible && visibleLayers.ContainsKey(item.LayerId))
            .OrderBy(item => visibleLayers[item.LayerId])
            .ThenBy(item => item.ZIndex)
            .ThenBy(item => item.Id))
        {
            var bounds = RotationBounds(scene.Surface, item.Bounds, item.RotationDegrees);
            if (item.Opacity < .999
                && painted.FirstOrDefault(lower => Intersects(bounds, lower.Bounds)) is var lower
                && lower.Id != Guid.Empty)
                return new(item.Id, lower.Id);
            if (item.Opacity > .001)
                painted.Add((item.Id, bounds));
        }
        return null;
    }

    private static CompositionBounds RotationBounds(CompositionSurface surface, CompositionBounds bounds, double degrees)
    {
        if (Math.Abs(degrees) <= .001) return bounds;
        var radians = degrees * Math.PI / 180;
        var surfaceWidth = Math.Max(1, surface.WidthPoints);
        var surfaceHeight = Math.Max(1, surface.HeightPoints);
        var widthPoints = bounds.WidthPercent / 100 * surfaceWidth;
        var heightPoints = bounds.HeightPercent / 100 * surfaceHeight;
        var rotatedWidthPoints = widthPoints * Math.Abs(Math.Cos(radians))
            + heightPoints * Math.Abs(Math.Sin(radians));
        var rotatedHeightPoints = widthPoints * Math.Abs(Math.Sin(radians))
            + heightPoints * Math.Abs(Math.Cos(radians));
        var width = rotatedWidthPoints / surfaceWidth * 100;
        var height = rotatedHeightPoints / surfaceHeight * 100;
        return bounds with
        {
            XPercent = bounds.XPercent + (bounds.WidthPercent - width) / 2,
            YPercent = bounds.YPercent + (bounds.HeightPercent - height) / 2,
            WidthPercent = width,
            HeightPercent = height,
        };
    }

    private static bool Intersects(CompositionBounds left, CompositionBounds right) =>
        left.XPercent < right.XPercent + right.WidthPercent
        && right.XPercent < left.XPercent + left.WidthPercent
        && left.YPercent < right.YPercent + right.HeightPercent
        && right.YPercent < left.YPercent + left.HeightPercent;
}

public sealed record CompositionOpacityOverlap(Guid TransparentObjectId, Guid LowerObjectId);

public sealed record CompositionReadingOrderNormalization(
    CompositionScene Scene,
    int ChangedObjectCount);
