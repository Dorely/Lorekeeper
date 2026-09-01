using System.Text.Json.Serialization;

namespace Lorekeeper.Models;

[JsonConverter(typeof(JsonStringEnumConverter<PublicationBarcodeMode>))]
public enum PublicationBarcodeMode
{
    None,
    LorekeeperBarcode,
    VendorOverlay,
}

[JsonConverter(typeof(JsonStringEnumConverter<SpineReadingDirection>))]
public enum SpineReadingDirection
{
    TopToBottom,
    BottomToTop,
    Horizontal,
}

public class PublicationCoverDesign
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EditionId { get; set; }
    public PublicationEdition Edition { get; set; } = null!;
    public bool InheritsCoreFront { get; set; } = true;
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string SpineText { get; set; } = string.Empty;
    public SpineReadingDirection SpineReadingDirection { get; set; } = SpineReadingDirection.TopToBottom;
    public string BackgroundColor { get; set; } = "#5c7ca5";
    public PublicationBarcodeMode BarcodeMode { get; set; } = PublicationBarcodeMode.None;
    public double ImageCropXPercent { get; set; } = 50;
    public double ImageCropYPercent { get; set; } = 50;
    public string AcknowledgedTemplateFingerprint { get; set; } = string.Empty;
    public string CompositionSceneJson { get; set; } = string.Empty;
    public string SurfaceScenesJson { get; set; } = "{}";
    public long Revision { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
