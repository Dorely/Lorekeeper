using System.Text.Json.Serialization;

namespace Lorekeeper.Components.Pages.Projects;

/// <summary>
/// A transient, chapter-scoped manuscript position shared by the editor views.
/// The block identity and offsets are the preferred anchor; logical progress and
/// the optional line are only used when a renderer cannot resolve that anchor.
/// </summary>
internal sealed record ManuscriptViewLocation(
    [property: JsonPropertyName("blockId")] string? BlockId = null,
    [property: JsonPropertyName("node")] bool IsNode = false,
    [property: JsonPropertyName("anchorOffset")] int AnchorOffset = 0,
    [property: JsonPropertyName("headOffset")] int HeadOffset = 0,
    [property: JsonPropertyName("logicalProgress")] double LogicalProgress = 0,
    [property: JsonPropertyName("fallbackLine")] int? FallbackLine = null,
    [property: JsonPropertyName("viewportAnchor")] bool IsViewportAnchor = false);
