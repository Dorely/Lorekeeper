using Lorekeeper.Models;
using Lorekeeper.Publish;

namespace Lorekeeper.Composition;

public static class CoverCompositionFactory
{
    public static CompositionScene KeepArtworkBehindCopy(CompositionScene scene)
    {
        var groups = scene.Objects
            .Where(item => item.Kind == CompositionObjectKind.Group)
            .ToDictionary(item => item.Id);
        static long EffectiveZIndex(CompositionObject item, IReadOnlyDictionary<Guid, CompositionObject> parents) =>
            (long)item.ZIndex + (item.GroupId is Guid groupId && parents.TryGetValue(groupId, out var group) ? group.ZIndex : 0);
        var images = scene.Objects
            .Where(item => item.Kind == CompositionObjectKind.Image)
            .OrderBy(item => EffectiveZIndex(item, groups))
            .ThenBy(item => item.Id)
            .ToList();
        var copy = scene.Objects
            .Where(item => item.Kind == CompositionObjectKind.Text)
            .OrderBy(item => EffectiveZIndex(item, groups))
            .ThenBy(item => item.Id)
            .ToList();
        if (images.Count == 0 || copy.Count == 0)
            return scene;

        if (scene.Layers.GroupBy(item => item.Id).Any(group => group.Count() > 1))
            return scene;
        var layerOrder = scene.Layers.ToDictionary(item => item.Id, item => item.Order);
        if (images.Concat(copy).Any(item => !layerOrder.ContainsKey(item.LayerId)))
            return scene;
        var highestArtworkLayer = images.Select(item => layerOrder[item.LayerId]).Max();
        var copyLayerIds = copy.Select(item => item.LayerId).ToHashSet();
        var layersNeedNormalization = scene.Layers.Any(layer =>
            copyLayerIds.Contains(layer.Id) && layer.Order < highestArtworkLayer);
        var zIndexesNeedNormalization = EffectiveZIndex(images[^1], groups) >= EffectiveZIndex(copy[0], groups);
        if (!layersNeedNormalization && !zIndexesNeedNormalization)
            return scene;

        var highestArtworkZIndex = scene.Objects
            .Where(item => item.Kind != CompositionObjectKind.Text)
            .Select(item => EffectiveZIndex(item, groups))
            .DefaultIfEmpty(0)
            .Max();
        var copyOrder = zIndexesNeedNormalization
            ? copy.Select((item, index) =>
                {
                    var parentZIndex = item.GroupId is Guid groupId && groups.TryGetValue(groupId, out var group)
                        ? group.ZIndex
                        : 0;
                    return (item.Id, ZIndex: checked((int)(highestArtworkZIndex + index + 1 - parentZIndex)));
                })
                .ToDictionary(item => item.Id, item => item.ZIndex)
            : new Dictionary<Guid, int>();
        return scene with
        {
            Layers = layersNeedNormalization
                ? scene.Layers.Select(layer => copyLayerIds.Contains(layer.Id) && layer.Order < highestArtworkLayer
                    ? layer with { Order = highestArtworkLayer }
                    : layer).ToList()
                : scene.Layers,
            Objects = scene.Objects.Select(item =>
                copyOrder.TryGetValue(item.Id, out var copyZIndex)
                        ? item with { ZIndex = copyZIndex }
                        : item).ToList(),
        };
    }

    public static CompositionScene CreateCoreFrontFromRelease(
        PublicationEdition sourceEdition,
        CompositionScene sourceScene,
        PublicationEdition coreEdition)
    {
        if (sourceEdition.Format is not (PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover))
            return Reflow(coreEdition, new PublicationCoverDesign { EditionId = Guid.Empty }, sourceScene, 0, 0);
        var sourceGeometry = new CoverGeometry(
            sourceScene.Surface.WidthPoints,
            sourceScene.Surface.HeightPoints,
            sourceScene.Surface.TrimWidthPoints > 0 ? sourceScene.Surface.TrimWidthPoints : sourceEdition.PageWidthInches * 72,
            sourceScene.Surface.TrimHeightPoints > 0 ? sourceScene.Surface.TrimHeightPoints : sourceEdition.PageHeightInches * 72,
            sourceScene.Surface.BleedPoints,
            sourceScene.Surface.SpineWidthPoints)
        {
            SafeInsetPoints = sourceScene.Surface.SafeInsetPoints,
            BackRegionWidthPoints = sourceScene.Surface.BackRegionWidthPoints,
            FrontRegionWidthPoints = sourceScene.Surface.FrontRegionWidthPoints,
            CoverRegionYPoints = sourceScene.Surface.CoverRegionYPoints,
            CoverRegionHeightPoints = sourceScene.Surface.CoverRegionHeightPoints,
        };
        var front = Region(CompositionRegionConstraint.Front, sourceGeometry);
        var frontPercent = RegionBoundsPercent(CompositionRegionConstraint.Front, sourceGeometry);
        var selectedTopLevel = sourceScene.Objects.Where(item => item.GroupId is null
            && !PublicationTextBindings.UsesBinding(item.TextBinding, "spineText")
            && !PublicationTextBindings.UsesBinding(item.TextBinding, "description")
            && item.RegionConstraint is not (CompositionRegionConstraint.Back or CompositionRegionConstraint.Spine or CompositionRegionConstraint.BarcodeReserve)
            && Intersects(item.Bounds, frontPercent)).ToList();
        var selectedIds = selectedTopLevel.Select(item => item.Id).ToHashSet();
        var objects = sourceScene.Objects.Where(item => selectedIds.Contains(item.Id)
                || item.GroupId is Guid groupId && selectedIds.Contains(groupId))
            .Select(item => item.GroupId is not null
                ? item
                : item with
                {
                    Bounds = Clamp(FromSurfaceBounds(item.Bounds, front, sourceGeometry)),
                    RegionConstraint = CompositionRegionConstraint.Front,
                }).ToList();
        var coreGeometry = Geometry(coreEdition, 0);
        return KeepArtworkBehindCopy(sourceScene with
        {
            Surface = sourceScene.Surface with
            {
                Kind = CompositionSurfaceKind.SinglePage,
                WidthPoints = coreGeometry.WidthPoints,
                HeightPoints = coreGeometry.HeightPoints,
                BleedPoints = 0,
                SafeInsetPoints = coreGeometry.SafeInsetPoints,
                TrimWidthPoints = coreGeometry.TrimWidthPoints,
                TrimHeightPoints = coreGeometry.TrimHeightPoints,
                SpineWidthPoints = 0,
            },
            Objects = objects,
        });
    }

    public static CompositionScene CreateReleaseFromCore(
        PublicationEdition edition,
        PublicationCoverDesign cover,
        CompositionScene coreScene,
        int pageCount = 0,
        bool lockCoreLayers = false)
    {
        var geometry = Geometry(edition, pageCount);
        var targetRegion = Region(CompositionRegionConstraint.Front, geometry);
        var coreLayerIds = coreScene.Layers.Select(layer => layer.Id).ToHashSet();
        var coreLayers = coreScene.Layers.Select(layer => layer with
        {
            Name = $"Core · {layer.Name}",
            Locked = lockCoreLayers || layer.Locked,
        }).ToList();
        var coreObjects = coreScene.Objects.Select(item => item.GroupId is not null
            ? item
            : item with
            {
                Bounds = ToSurfaceBounds(item.Bounds, targetRegion, geometry),
                RegionConstraint = CompositionRegionConstraint.Front,
                Locked = lockCoreLayers || item.Locked,
            }).ToList();

        if (edition.Format is not (PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover))
        {
            return KeepArtworkBehindCopy(coreScene with
            {
                Surface = coreScene.Surface with
                {
                    Kind = CompositionSurfaceKind.SinglePage,
                    WidthPoints = geometry.WidthPoints,
                    HeightPoints = geometry.HeightPoints,
                    BleedPoints = 0,
                    SafeInsetPoints = geometry.SafeInsetPoints,
                    TrimWidthPoints = geometry.TrimWidthPoints,
                    TrimHeightPoints = geometry.TrimHeightPoints,
                    SpineWidthPoints = 0,
                },
                Layers = coreLayers,
                Objects = coreObjects,
            });
        }

        var additions = Create(edition, cover, pageCount);
        var additionObjects = additions.Objects
            .Where(item => PublicationTextBindings.UsesBinding(item.TextBinding, "spineText")
                || PublicationTextBindings.UsesBinding(item.TextBinding, "description"))
            .Select(item => item with { ReadingOrder = (item.ReadingOrder ?? 0) + coreObjects.Count })
            .ToList();
        return KeepArtworkBehindCopy(additions with
        {
            Layers = coreLayers.Concat(additions.Layers.Where(layer => !coreLayerIds.Contains(layer.Id))).ToList(),
            Styles = coreScene.Styles.Concat(additions.Styles).GroupBy(style => style.Id).Select(group => group.First()).ToList(),
            Objects = coreObjects.Concat(additionObjects).ToList(),
        });
    }

    public static CompositionScene Create(PublicationEdition edition, PublicationCoverDesign cover, int pageCount = 0)
    {
        var print = edition.Format is PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover;
        var geometry = Geometry(edition, pageCount);
        var layerId = Guid.NewGuid();
        var objects = new List<CompositionObject>();
        void AddText(string binding, CompositionRegionConstraint region, double y, CompositionSemanticRole role)
        {
            var local = new CompositionBounds { XPercent = 10, YPercent = y, WidthPercent = 80, HeightPercent = 12 };
            objects.Add(new CompositionObject
            {
                Id = Guid.NewGuid(), LayerId = layerId, Kind = CompositionObjectKind.Text,
                TextBinding = binding, Name = binding, Bounds = ToSurfaceBounds(local, Region(region, geometry), geometry),
                RegionConstraint = region, SemanticRole = role, ReadingOrder = objects.Count + 1,
                FillColor = "#ffffff", FontFamilyKey = "builtin:nunito",
                FontWeight = role == CompositionSemanticRole.Heading1 ? 700 : 400,
                FontSizePoints = role == CompositionSemanticRole.Heading1 ? 28 : 13,
                RotationDegrees = binding == "spineText" ? SpineRotation(cover.SpineReadingDirection) : 0,
            });
        }
        AddText("title", CompositionRegionConstraint.Front, 12, CompositionSemanticRole.Heading1);
        AddText("subtitle", CompositionRegionConstraint.Front, 30, CompositionSemanticRole.Heading2);
        AddText("author", CompositionRegionConstraint.Front, 78, CompositionSemanticRole.Paragraph);
        if (print)
        {
            AddText("spineText", CompositionRegionConstraint.Spine, 10, CompositionSemanticRole.Paragraph);
            AddText("description", CompositionRegionConstraint.Back, 18, CompositionSemanticRole.Paragraph);
        }
        if (edition.SelectedCoverImageId is { } imageId)
        {
            objects = objects.Select(item => item.ReadingOrder is int order
                ? item with { ReadingOrder = order + 1 }
                : item).ToList();
            objects.Insert(0, new CompositionObject
            {
                Id = Guid.NewGuid(), LayerId = layerId, Kind = CompositionObjectKind.Image,
                ImageId = imageId, Name = "Cover artwork", Bounds = new CompositionBounds(),
                Decorative = false, SemanticRole = CompositionSemanticRole.Figure,
                ReadingOrder = 1, AccessibilityDecisionPending = true,
                RegionConstraint = print ? CompositionRegionConstraint.Page : CompositionRegionConstraint.Front,
                ZIndex = -1,
            });
        }
        return KeepArtworkBehindCopy(new CompositionScene
        {
            Surface = new CompositionSurface
            {
                Kind = print ? CompositionSurfaceKind.FacingSpread : CompositionSurfaceKind.SinglePage,
                WidthPoints = geometry.WidthPoints,
                HeightPoints = geometry.HeightPoints,
                BleedPoints = edition.Bleed ? 9 : 0,
                SafeInsetPoints = geometry.SafeInsetPoints,
                TrimWidthPoints = geometry.TrimWidthPoints,
                TrimHeightPoints = geometry.TrimHeightPoints,
                SpineWidthPoints = geometry.SpineWidthPoints,
                BackRegionWidthPoints = geometry.BackRegionWidthPoints,
                FrontRegionWidthPoints = geometry.FrontRegionWidthPoints,
                CoverRegionYPoints = geometry.CoverRegionYPoints,
                CoverRegionHeightPoints = geometry.CoverRegionHeightPoints,
            },
            Layers = [new CompositionLayer(layerId, "Cover", 0)], Objects = objects,
        });
    }

    public static CompositionScene Reflow(
        PublicationEdition edition,
        PublicationCoverDesign cover,
        CompositionScene scene,
        int oldPageCount,
        int newPageCount,
        string? surfaceRole = null)
    {
        var oldGeometry = scene.Surface.TrimWidthPoints > 0 && scene.Surface.TrimHeightPoints > 0
            ? new CoverGeometry(
                scene.Surface.WidthPoints,
                scene.Surface.HeightPoints,
                scene.Surface.TrimWidthPoints,
                scene.Surface.TrimHeightPoints,
                scene.Surface.BleedPoints,
                scene.Surface.SpineWidthPoints)
            {
                SafeInsetPoints = scene.Surface.SafeInsetPoints,
                BackRegionWidthPoints = scene.Surface.BackRegionWidthPoints,
                FrontRegionWidthPoints = scene.Surface.FrontRegionWidthPoints,
                CoverRegionYPoints = scene.Surface.CoverRegionYPoints,
                CoverRegionHeightPoints = scene.Surface.CoverRegionHeightPoints,
            }
            : Geometry(edition, oldPageCount, surfaceRole);
        var newGeometry = Geometry(edition, newPageCount, surfaceRole);
        var objects = scene.Objects.Select(item =>
        {
            // Group children use percentages local to their parent. Reflow the
            // surface-space group once and preserve each child's local geometry.
            if (item.GroupId is not null)
                return item;
            if (item.RegionConstraint == CompositionRegionConstraint.Page)
            {
                if (CompositionImageLayout.ImageCoversCanvas(item))
                    return item with { Bounds = new CompositionBounds() };

                var widthPoints = item.Bounds.WidthPercent / 100 * oldGeometry.WidthPoints;
                var heightPoints = item.Bounds.HeightPercent / 100 * oldGeometry.HeightPoints;
                var xPoints = item.Bounds.XPercent / 100 * oldGeometry.WidthPoints;
                var yPoints = item.Bounds.YPercent / 100 * oldGeometry.HeightPoints;
                return item with { Bounds = new CompositionBounds
                {
                    XPercent = xPoints / newGeometry.WidthPoints * 100,
                    YPercent = yPoints / newGeometry.HeightPoints * 100,
                    WidthPercent = widthPoints / newGeometry.WidthPoints * 100,
                    HeightPercent = heightPoints / newGeometry.HeightPoints * 100,
                }};
            }
            var oldRegion = Region(item.RegionConstraint, oldGeometry);
            var newRegion = Region(item.RegionConstraint, newGeometry);
            var local = FromSurfaceBounds(item.Bounds, oldRegion, oldGeometry);
            return item with { Bounds = ToSurfaceBounds(local, newRegion, newGeometry) };
        }).ToList();
        return KeepArtworkBehindCopy(scene with
        {
            Surface = scene.Surface with
            {
                Kind = edition.Format is PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover
                    ? CompositionSurfaceKind.FacingSpread
                    : CompositionSurfaceKind.SinglePage,
                WidthPoints = newGeometry.WidthPoints,
                HeightPoints = newGeometry.HeightPoints,
                BleedPoints = edition.Bleed ? 9 : 0,
                SafeInsetPoints = newGeometry.SafeInsetPoints,
                TrimWidthPoints = newGeometry.TrimWidthPoints,
                TrimHeightPoints = newGeometry.TrimHeightPoints,
                SpineWidthPoints = newGeometry.SpineWidthPoints,
                BackRegionWidthPoints = newGeometry.BackRegionWidthPoints,
                FrontRegionWidthPoints = newGeometry.FrontRegionWidthPoints,
                CoverRegionYPoints = newGeometry.CoverRegionYPoints,
                CoverRegionHeightPoints = newGeometry.CoverRegionHeightPoints,
            },
            Objects = objects,
        });
    }

    public static CoverGeometry Geometry(PublicationEdition edition, int pageCount, string? surfaceRole = null)
    {
        var trimWidth = edition.PageWidthInches * 72;
        var trimHeight = edition.PageHeightInches * 72;
        var print = edition.Format is PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover;
        if (!print)
            return new CoverGeometry(trimWidth, trimHeight, trimWidth, trimHeight, 0, 0);
        var registry = new PrintArtifactProfileRegistry();
        var product = registry.GetRequired(edition.PrintArtifactProfileKey);
        var effectivePages = PrintGeometryService.DesignPageCount(product, pageCount);
        var physical = new PrintGeometryService(registry).Calculate(edition, effectivePages, surfaceRole);
        return new CoverGeometry(
            (double)physical.SurfaceWidthInches * 72,
            (double)physical.SurfaceHeightInches * 72,
            trimWidth,
            trimHeight,
            (double)physical.BleedInches * 72,
            (double)physical.SpineWidthInches * 72)
        {
            SafeInsetPoints = (double)product.CoverSafetyInches * 72,
            BarcodeWidthPoints = (double)product.BarcodeWidthInches * 72,
            BarcodeHeightPoints = (double)product.BarcodeHeightInches * 72,
            BarcodeInsetPoints = (double)product.BarcodeInsetInches * 72,
            BackRegionWidthPoints = (double)physical.BackRegionWidthInches * 72,
            FrontRegionWidthPoints = (double)physical.FrontRegionWidthInches * 72,
            CoverRegionYPoints = (double)physical.CoverRegionYInches * 72,
            CoverRegionHeightPoints = (double)physical.CoverRegionHeightInches * 72,
        };
    }

    private static double MeasuredBackRegionX(CoverGeometry geometry) =>
        Math.Max(0, (geometry.WidthPoints
            - geometry.BackRegionWidthPoints
            - geometry.SpineWidthPoints
            - geometry.FrontRegionWidthPoints) / 2);

    private static CoverRegion BarcodeRegion(CoverGeometry geometry)
    {
        var back = geometry.HasMeasuredRegions
            ? new CoverRegion(MeasuredBackRegionX(geometry), geometry.CoverRegionYPoints, geometry.BackRegionWidthPoints, geometry.CoverRegionHeightPoints)
            : new CoverRegion(geometry.BleedPoints, geometry.BleedPoints, geometry.TrimWidthPoints, geometry.TrimHeightPoints);
        return new(
            Math.Max(back.X, back.X + back.Width - geometry.BarcodeInsetPoints - geometry.BarcodeWidthPoints),
            Math.Max(back.Y, back.Y + back.Height - geometry.BarcodeInsetPoints - geometry.BarcodeHeightPoints),
            Math.Min(geometry.BarcodeWidthPoints, back.Width),
            Math.Min(geometry.BarcodeHeightPoints, back.Height));
    }

    private static CoverRegion Region(CompositionRegionConstraint region, CoverGeometry geometry) => region switch
    {
        CompositionRegionConstraint.Back when geometry.HasMeasuredRegions => new(MeasuredBackRegionX(geometry), geometry.CoverRegionYPoints, geometry.BackRegionWidthPoints, geometry.CoverRegionHeightPoints),
        CompositionRegionConstraint.Spine when geometry.HasMeasuredRegions => new(MeasuredBackRegionX(geometry) + geometry.BackRegionWidthPoints, geometry.CoverRegionYPoints, Math.Max(geometry.SpineWidthPoints, .01), geometry.CoverRegionHeightPoints),
        CompositionRegionConstraint.Front when geometry.HasMeasuredRegions => new(geometry.WidthPoints - MeasuredBackRegionX(geometry) - geometry.FrontRegionWidthPoints, geometry.CoverRegionYPoints, geometry.FrontRegionWidthPoints, geometry.CoverRegionHeightPoints),
        CompositionRegionConstraint.Back => new(geometry.BleedPoints, geometry.BleedPoints, geometry.TrimWidthPoints, geometry.TrimHeightPoints),
        CompositionRegionConstraint.Spine => new(geometry.BleedPoints + geometry.TrimWidthPoints, geometry.BleedPoints, Math.Max(geometry.SpineWidthPoints, .01), geometry.TrimHeightPoints),
        CompositionRegionConstraint.Front => new(geometry.WidthPoints - geometry.BleedPoints - geometry.TrimWidthPoints, geometry.BleedPoints, geometry.TrimWidthPoints, geometry.TrimHeightPoints),
        CompositionRegionConstraint.SafeArea => new(
            geometry.BleedPoints + geometry.SafeInsetPoints,
            geometry.BleedPoints + geometry.SafeInsetPoints,
            Math.Max(1, geometry.WidthPoints - (geometry.BleedPoints + geometry.SafeInsetPoints) * 2),
            Math.Max(1, geometry.HeightPoints - (geometry.BleedPoints + geometry.SafeInsetPoints) * 2)),
        CompositionRegionConstraint.BarcodeReserve => BarcodeRegion(geometry),
        _ => new(0, 0, geometry.WidthPoints, geometry.HeightPoints),
    };

    public static CompositionBounds RegionBoundsPercent(CompositionRegionConstraint region, CoverGeometry geometry)
    {
        var value = Region(region, geometry);
        return new CompositionBounds
        {
            XPercent = value.X / geometry.WidthPoints * 100,
            YPercent = value.Y / geometry.HeightPoints * 100,
            WidthPercent = value.Width / geometry.WidthPoints * 100,
            HeightPercent = value.Height / geometry.HeightPoints * 100,
        };
    }

    public static CompositionBounds RegionBoundsPercent(CompositionRegionConstraint region, CompositionSurface surface) =>
        RegionBoundsPercent(region, new CoverGeometry(
            surface.WidthPoints,
            surface.HeightPoints,
            surface.TrimWidthPoints,
            surface.TrimHeightPoints,
            surface.BleedPoints,
            surface.SpineWidthPoints)
        {
            SafeInsetPoints = surface.SafeInsetPoints,
            BackRegionWidthPoints = surface.BackRegionWidthPoints,
            FrontRegionWidthPoints = surface.FrontRegionWidthPoints,
            CoverRegionYPoints = surface.CoverRegionYPoints,
            CoverRegionHeightPoints = surface.CoverRegionHeightPoints,
        });

    public static CompositionBounds SafeRegionBoundsPercent(
        CompositionRegionConstraint region,
        CoverGeometry geometry)
    {
        var bounds = RegionBoundsPercent(region, geometry);
        var insetX = region switch
        {
            CompositionRegionConstraint.SafeArea => 0,
            CompositionRegionConstraint.Spine => bounds.WidthPercent * .05,
            CompositionRegionConstraint.Page => (geometry.BleedPoints + geometry.SafeInsetPoints) / geometry.WidthPoints * 100,
            _ => geometry.SafeInsetPoints / geometry.WidthPoints * 100,
        };
        var insetY = region switch
        {
            CompositionRegionConstraint.SafeArea => 0,
            CompositionRegionConstraint.Page => (geometry.BleedPoints + geometry.SafeInsetPoints) / geometry.HeightPoints * 100,
            _ => geometry.SafeInsetPoints / geometry.HeightPoints * 100,
        };
        return bounds with
        {
            XPercent = bounds.XPercent + insetX,
            YPercent = bounds.YPercent + insetY,
            WidthPercent = Math.Max(0, bounds.WidthPercent - insetX * 2),
            HeightPercent = Math.Max(0, bounds.HeightPercent - insetY * 2),
        };
    }

    public static CompositionBounds SafeRegionBoundsPercent(
        CompositionRegionConstraint region,
        CompositionSurface surface) =>
        SafeRegionBoundsPercent(region, new CoverGeometry(
            surface.WidthPoints,
            surface.HeightPoints,
            surface.TrimWidthPoints,
            surface.TrimHeightPoints,
            surface.BleedPoints,
            surface.SpineWidthPoints)
        {
            SafeInsetPoints = surface.SafeInsetPoints,
            BackRegionWidthPoints = surface.BackRegionWidthPoints,
            FrontRegionWidthPoints = surface.FrontRegionWidthPoints,
            CoverRegionYPoints = surface.CoverRegionYPoints,
            CoverRegionHeightPoints = surface.CoverRegionHeightPoints,
        });

    public static CompositionScene ApplySpineReadingDirection(
        CompositionScene scene,
        SpineReadingDirection direction) => scene with
        {
            Objects = scene.Objects.Select(item => PublicationTextBindings.UsesBinding(item.TextBinding, "spineText")
                ? item with { RotationDegrees = SpineRotation(direction) }
                : item).ToList(),
        };

    public static double SpineRotation(SpineReadingDirection direction) => direction switch
    {
        SpineReadingDirection.TopToBottom => 90,
        SpineReadingDirection.BottomToTop => -90,
        SpineReadingDirection.Horizontal => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(direction)),
    };

    private static CompositionBounds ToSurfaceBounds(CompositionBounds local, CoverRegion region, CoverGeometry surface) => new()
    {
        XPercent = (region.X + local.XPercent / 100 * region.Width) / surface.WidthPoints * 100,
        YPercent = (region.Y + local.YPercent / 100 * region.Height) / surface.HeightPoints * 100,
        WidthPercent = local.WidthPercent / 100 * region.Width / surface.WidthPoints * 100,
        HeightPercent = local.HeightPercent / 100 * region.Height / surface.HeightPoints * 100,
    };

    private static CompositionBounds FromSurfaceBounds(CompositionBounds bounds, CoverRegion region, CoverGeometry surface) => new()
    {
        XPercent = (bounds.XPercent / 100 * surface.WidthPoints - region.X) / region.Width * 100,
        YPercent = (bounds.YPercent / 100 * surface.HeightPoints - region.Y) / region.Height * 100,
        WidthPercent = bounds.WidthPercent / 100 * surface.WidthPoints / region.Width * 100,
        HeightPercent = bounds.HeightPercent / 100 * surface.HeightPoints / region.Height * 100,
    };

    private static bool Intersects(CompositionBounds left, CompositionBounds right) =>
        left.XPercent < right.XPercent + right.WidthPercent
        && right.XPercent < left.XPercent + left.WidthPercent
        && left.YPercent < right.YPercent + right.HeightPercent
        && right.YPercent < left.YPercent + left.HeightPercent;

    private static CompositionBounds Clamp(CompositionBounds value)
    {
        var left = Math.Clamp(value.XPercent, 0, 100);
        var top = Math.Clamp(value.YPercent, 0, 100);
        var right = Math.Clamp(value.XPercent + value.WidthPercent, 0, 100);
        var bottom = Math.Clamp(value.YPercent + value.HeightPercent, 0, 100);
        return new CompositionBounds
        {
            XPercent = left,
            YPercent = top,
            WidthPercent = Math.Max(.01, right - left),
            HeightPercent = Math.Max(.01, bottom - top),
        };
    }

    private sealed record CoverRegion(double X, double Y, double Width, double Height);
}

public sealed record CoverGeometry(
    double WidthPoints,
    double HeightPoints,
    double TrimWidthPoints,
    double TrimHeightPoints,
    double BleedPoints,
    double SpineWidthPoints)
{
    public double SafeInsetPoints { get; init; } = 18;
    public double BarcodeWidthPoints { get; init; } = 144;
    public double BarcodeHeightPoints { get; init; } = 86.4;
    public double BarcodeInsetPoints { get; init; } = 18;
    public double BackRegionWidthPoints { get; init; }
    public double FrontRegionWidthPoints { get; init; }
    public double CoverRegionYPoints { get; init; }
    public double CoverRegionHeightPoints { get; init; }
    public bool HasMeasuredRegions => BackRegionWidthPoints > 0
        && FrontRegionWidthPoints > 0
        && CoverRegionHeightPoints > 0;
}
