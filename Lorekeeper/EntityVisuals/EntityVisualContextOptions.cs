namespace Lorekeeper.EntityVisuals;

public sealed class EntityVisualContextOptions
{
    public const string SectionName = "Agents:EntityVisuals";

    public int MaxImagesPerEntity { get; set; } = 4;
    public int MaxImagesPerTurn { get; set; } = 12;
    public int MaxImageEdge { get; set; } = 1024;
    public int MaxSourceVisualsPerIngestChunk { get; set; } = 8;
}
