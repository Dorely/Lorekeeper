namespace Lorekeeper.Models;

public class IngestVectorFragment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SourceId { get; set; }
    public IngestSource Source { get; set; } = null!;

    public int Index { get; set; }
    public long? VectorRowId { get; set; }
    public int StartChar { get; set; }
    public int EndChar { get; set; }
    public string Metadata { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}