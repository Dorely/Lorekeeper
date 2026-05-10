namespace Lorekeeper.Ingest;

public sealed class IngestSourceStructureOptions
{
    public const string SectionName = "Ingest:Sectioning";

    public int TargetTokens { get; set; } = 10_000;
    public double SoftMaxRatio { get; set; } = 1.5;
    public double SmallSectionRatio { get; set; } = 0.15;
}
