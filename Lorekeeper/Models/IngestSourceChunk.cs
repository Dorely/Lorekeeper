namespace Lorekeeper.Models;

public class IngestSourceChunk
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SourceId { get; set; }
    public IngestSource Source { get; set; } = null!;

    public int Index { get; set; }
    public string Title { get; set; } = string.Empty;
    public string HeadingPath { get; set; } = string.Empty;
    public int StartChar { get; set; }
    public int EndChar { get; set; }

    public int EstimatedTokenCount { get; set; }
    public string TokenCountMethod { get; set; } = string.Empty;
    public string? TokenEncodingName { get; set; }
    public bool TokenCountIsExact { get; set; }

    public string Summary { get; set; } = string.Empty;
    public string AgentNotes { get; set; } = string.Empty;
    public IngestSourceChunkStructureStatus StructureStatus { get; set; } = IngestSourceChunkStructureStatus.Generated;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<IngestJobChunk> JobChunks { get; set; } = [];
    public ICollection<IngestReportItem> ReportItems { get; set; } = [];
}

public enum IngestSourceChunkStructureStatus
{
    Generated,
    Edited,
    Approved,
}