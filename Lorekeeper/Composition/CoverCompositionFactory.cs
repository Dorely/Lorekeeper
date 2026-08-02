using Lorekeeper.Models;

namespace Lorekeeper.Composition;

public static class CoverCompositionFactory
{
    public static CompositionScene Create(PublicationEdition edition, PublicationCoverDesign cover, int pageCount = 0)
    {
        var print = edition.Format == PublicationEditionFormat.Paperback;
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
            });
        }
        AddText("title", CompositionRegionConstraint.Front, 12, CompositionSemanticRole.Heading1);
        AddText("subtitle", CompositionRegionConstraint.Front, 30, CompositionSemanticRole.Heading2);
        AddText("author", CompositionRegionConstraint.Front, 78, CompositionSemanticRole.Paragraph);
        if (print)
        {
            AddText("spineText", CompositionRegionConstraint.Spine, 10, CompositionSemanticRole.Paragraph);
            AddText("backCopy", CompositionRegionConstraint.Back, 18, CompositionSemanticRole.Paragraph);
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
        return new CompositionScene
        {
            Surface = new CompositionSurface
            {
                Kind = print ? CompositionSurfaceKind.FacingSpread : CompositionSurfaceKind.SinglePage,
                WidthPoints = geometry.WidthPoints,
                HeightPoints = geometry.HeightPoints,
                BleedPoints = edition.Bleed ? 9 : 0,
                SafeInsetPoints = 18,
                TrimWidthPoints = geometry.TrimWidthPoints,
                TrimHeightPoints = geometry.TrimHeightPoints,
                SpineWidthPoints = geometry.SpineWidthPoints,
            },
            Layers = [new CompositionLayer(layerId, "Cover", 0)], Objects = objects,
        };
    }

    public static CompositionScene Reflow(
        PublicationEdition edition,
        PublicationCoverDesign cover,
        CompositionScene scene,
        int oldPageCount,
        int newPageCount)
    {
        var oldGeometry = scene.Surface.TrimWidthPoints > 0 && scene.Surface.TrimHeightPoints > 0
            ? new CoverGeometry(
                scene.Surface.WidthPoints,
                scene.Surface.HeightPoints,
                scene.Surface.TrimWidthPoints,
                scene.Surface.TrimHeightPoints,
                scene.Surface.BleedPoints,
                scene.Surface.SpineWidthPoints)
            : Geometry(edition, oldPageCount);
        var newGeometry = Geometry(edition, newPageCount);
        var objects = scene.Objects.Select(item =>
        {
            // Group children use percentages local to their parent. Reflow the
            // surface-space group once and preserve each child's local geometry.
            if (item.GroupId is not null)
                return item;
            if (item.RegionConstraint == CompositionRegionConstraint.Page)
            {
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
        return scene with
        {
            Surface = scene.Surface with
            {
                Kind = edition.Format == PublicationEditionFormat.Paperback
                    ? CompositionSurfaceKind.FacingSpread
                    : CompositionSurfaceKind.SinglePage,
                WidthPoints = newGeometry.WidthPoints,
                HeightPoints = newGeometry.HeightPoints,
                BleedPoints = edition.Bleed ? 9 : 0,
                TrimWidthPoints = newGeometry.TrimWidthPoints,
                TrimHeightPoints = newGeometry.TrimHeightPoints,
                SpineWidthPoints = newGeometry.SpineWidthPoints,
            },
            Objects = objects,
        };
    }

    public static CoverGeometry Geometry(PublicationEdition edition, int pageCount)
    {
        var print = edition.Format == PublicationEditionFormat.Paperback;
        var bleed = print && edition.Bleed ? 9d : 0d;
        var trimWidth = edition.PageWidthInches * 72;
        var trimHeight = edition.PageHeightInches * 72;
        var caliper = edition.Paper == PublicationPaper.Cream ? .0025 : .002252;
        var spine = print ? pageCount * caliper * 72 : 0;
        return new CoverGeometry(
            print ? trimWidth * 2 + spine + bleed * 2 : trimWidth,
            print ? trimHeight + bleed * 2 : trimHeight,
            trimWidth,
            trimHeight,
            bleed,
            spine);
    }

    private static CoverRegion Region(CompositionRegionConstraint region, CoverGeometry geometry) => region switch
    {
        CompositionRegionConstraint.Back => new(geometry.BleedPoints, geometry.BleedPoints, geometry.TrimWidthPoints, geometry.TrimHeightPoints),
        CompositionRegionConstraint.Spine => new(geometry.BleedPoints + geometry.TrimWidthPoints, geometry.BleedPoints, Math.Max(geometry.SpineWidthPoints, .01), geometry.TrimHeightPoints),
        CompositionRegionConstraint.Front => new(geometry.WidthPoints - geometry.BleedPoints - geometry.TrimWidthPoints, geometry.BleedPoints, geometry.TrimWidthPoints, geometry.TrimHeightPoints),
        CompositionRegionConstraint.SafeArea => new(18, 18, Math.Max(1, geometry.WidthPoints - 36), Math.Max(1, geometry.HeightPoints - 36)),
        CompositionRegionConstraint.BarcodeReserve => new(geometry.BleedPoints + 18, geometry.HeightPoints - geometry.BleedPoints - 104.4, 144, 86.4),
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

    private sealed record CoverRegion(double X, double Y, double Width, double Height);
}

public sealed record CoverGeometry(
    double WidthPoints,
    double HeightPoints,
    double TrimWidthPoints,
    double TrimHeightPoints,
    double BleedPoints,
    double SpineWidthPoints);
