using Lorekeeper.Composition;
using Lorekeeper.Authoring;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using System.Text.Json;

namespace Lorekeeper.Tests;

public sealed class CompositionCanvasDeltaAdapterTests
{
    [Fact]
    public void MoveAndPropertyPatchRoundTripsAgainstTheCanonicalPreState()
    {
        var first = Object(1, "before", 1);
        var second = Object(2, "second", 2);
        var before = Scene(first, second);
        var after = Scene(second, first with { Name = "after", Opacity = .5 });

        var delta = CompositionCanvasDeltaAdapter.Create(before, after);
        var applied = CompositionCanvasDeltaAdapter.Apply(before, delta.Forward);
        var restored = CompositionCanvasDeltaAdapter.Apply(applied, delta.Inverse);

        AssertScene(after, applied);
        AssertScene(before, restored);
    }

    [Fact]
    public void InsertAndRemoveRoundTripHeterogeneousObjectsAndZOrder()
    {
        var text = Object(1, "text", 3) with { Kind = CompositionObjectKind.Text };
        var image = Object(2, "image", 1) with { Kind = CompositionObjectKind.Image };
        var shape = Object(3, "shape", 2) with { Kind = CompositionObjectKind.Rectangle };
        var before = Scene(text, image);
        var after = Scene(shape, text);

        var delta = CompositionCanvasDeltaAdapter.Create(before, after);
        var applied = CompositionCanvasDeltaAdapter.Apply(before, delta.Forward);
        var restored = CompositionCanvasDeltaAdapter.Apply(applied, delta.Inverse);

        AssertScene(after, applied);
        AssertScene(before, restored);
    }

    [Fact]
    public void StyleAndLayerDeltasPreserveObjectReferences()
    {
        var layer = new CompositionLayer(Guid.Parse("00000000-0000-0000-0000-000000000010"), "Copy", 0);
        var replacementLayer = new CompositionLayer(Guid.Parse("00000000-0000-0000-0000-000000000011"), "Artwork", 1);
        var style = new CompositionObjectStyle { Id = Guid.Parse("00000000-0000-0000-0000-000000000020"), Name = "Body", FontFamilyKey = "project:00000000-0000-0000-0000-000000000021" };
        var replacementStyle = style with { Id = Guid.Parse("00000000-0000-0000-0000-000000000022"), Name = "Heading", FontSizePoints = 24 };
        var objectWithStyle = Object(1, "styled", 1) with { LayerId = replacementLayer.Id, StyleId = replacementStyle.Id };
        var before = new CompositionScene { Layers = [layer], Styles = [style], Objects = [Object(1, "styled", 1) with { LayerId = layer.Id, StyleId = style.Id }] };
        var after = new CompositionScene { Layers = [replacementLayer], Styles = [replacementStyle], Objects = [objectWithStyle] };

        var delta = CompositionCanvasDeltaAdapter.Create(before, after);
        var applied = CompositionCanvasDeltaAdapter.Apply(before, delta.Forward);
        var restored = CompositionCanvasDeltaAdapter.Apply(applied, delta.Inverse);

        AssertScene(after, applied);
        AssertScene(before, restored);
        Assert.Contains(delta.Forward, item => item.Kind.Equals("insertCanvasStyle", StringComparison.OrdinalIgnoreCase));

        var projectId = Guid.Parse("00000000-0000-0000-0000-000000000030");
        var targetId = "designed-page-content:00000000-0000-0000-0000-000000000031";
        var history = new AuthoringDeltaHistoryRuntime();
        var stage = history.Stage(projectId, [targetId], new Dictionary<string, long> { [targetId] = 0 },
            "Style", delta.Forward, delta.Inverse, null, null);
        _ = history.Confirm(stage.StageId);
        Assert.Contains(targetId, history.FindDependentTargets(projectId,
            AuthoringHistoryDependencyKind.ProjectFont,
            Guid.Parse("00000000-0000-0000-0000-000000000021")));
    }

    [Fact]
    public void CoverPropertiesRoundTripWithoutSerializingTheCoverAggregate()
    {
        var before = new CompositionCoverProperties("Old", "", "Author", "", "#000000", "None", "TopToBottom");
        var after = before with { Title = "New", BackgroundColor = "#ffffff", BarcodeMode = "VendorOverlay" };

        var coverDelta = CompositionCanvasDeltaAdapter.CreateCoverProperties(before, after);
        var sceneDelta = CompositionCanvasDeltaAdapter.Create(
            Scene(Object(1, "before", 1)),
            Scene(Object(1, "after", 1)));
        var forward = sceneDelta.Forward.Concat(coverDelta.Forward).ToList();
        var inverse = sceneDelta.Inverse.Concat(coverDelta.Inverse).ToList();
        var appliedScene = CompositionCanvasDeltaAdapter.Apply(Scene(Object(1, "before", 1)), forward);
        var restoredScene = CompositionCanvasDeltaAdapter.Apply(appliedScene, inverse);
        var applied = CompositionCanvasDeltaAdapter.ApplyCoverProperties(before, forward);
        var restored = CompositionCanvasDeltaAdapter.ApplyCoverProperties(applied, inverse);

        Assert.Equal(after, applied);
        Assert.Equal(before, restored);
        Assert.Equal("after", appliedScene.Objects.Single().Name);
        Assert.Equal("before", restoredScene.Objects.Single().Name);
        Assert.All(coverDelta.Forward, operation => Assert.Equal("setCanvasCoverProperties", operation.Kind));
    }

    private static CompositionScene Scene(params CompositionObject[] objects) => new()
    {
        Objects = objects,
    };

    private static CompositionObject Object(int id, string name, int zIndex) => new()
    {
        Id = Guid.Parse($"00000000-0000-0000-0000-{id:D12}"),
        LayerId = Guid.Parse("00000000-0000-0000-0000-000000000010"),
        Kind = CompositionObjectKind.Line,
        Name = name,
        ZIndex = zIndex,
        Bounds = new CompositionBounds { XPercent = id, YPercent = id, WidthPercent = 10, HeightPercent = 10 },
    };

    private static void AssertScene(CompositionScene expected, CompositionScene actual) =>
        Assert.Equal(
            JsonSerializer.Serialize(expected, ManuscriptCodec.JsonOptions),
            JsonSerializer.Serialize(actual, ManuscriptCodec.JsonOptions));
}
