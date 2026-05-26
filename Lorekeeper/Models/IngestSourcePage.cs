namespace Lorekeeper.Models;

public class IngestSourcePage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SourceId { get; set; }
    public IngestSource Source { get; set; } = null!;

    public int PageNumber { get; set; }
    public string Text { get; set; } = string.Empty;
    public int StartChar { get; set; }
    public int EndChar { get; set; }
    public string ExtractionMethod { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public string ImageHash { get; set; } = string.Empty;
    public string RenderSettingsJson { get; set; } = "{}";
    public int? VisionProviderId { get; set; }
    public string VisionModelName { get; set; } = string.Empty;
    public string Diagnostics { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<IngestSourceBlock> Blocks { get; set; } = [];
}
