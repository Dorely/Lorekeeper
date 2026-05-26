namespace Lorekeeper.Models;

public class IngestSourceBlock
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SourceId { get; set; }
    public IngestSource Source { get; set; } = null!;

    public Guid? SourcePageId { get; set; }
    public IngestSourcePage? SourcePage { get; set; }

    public int Index { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Locator { get; set; } = string.Empty;
    public int? PageNumber { get; set; }
    public int StartChar { get; set; }
    public int EndChar { get; set; }
    public string MetadataJson { get; set; } = "{}";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
