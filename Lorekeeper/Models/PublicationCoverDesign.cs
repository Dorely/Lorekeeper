using System.Text.Json.Serialization;

namespace Lorekeeper.Models;

[JsonConverter(typeof(JsonStringEnumConverter<PublicationBarcodeMode>))]
public enum PublicationBarcodeMode
{
    LorekeeperBarcode,
    VendorOverlay,
}

public class PublicationCoverDesign
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EditionId { get; set; }
    public PublicationEdition Edition { get; set; } = null!;
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string SpineText { get; set; } = string.Empty;
    public string BackCopy { get; set; } = string.Empty;
    public string BackgroundColor { get; set; } = "#5c7ca5";
    public PublicationBarcodeMode BarcodeMode { get; set; } = PublicationBarcodeMode.LorekeeperBarcode;
    public double ImageFocalXPercent { get; set; } = 50;
    public double ImageFocalYPercent { get; set; } = 50;
    public string AcknowledgedTemplateFingerprint { get; set; } = string.Empty;
    public long Revision { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
