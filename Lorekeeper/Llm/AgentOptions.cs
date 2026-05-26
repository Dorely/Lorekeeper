namespace Lorekeeper.Llm;

public sealed class AgentOptions
{
    public const string SectionName = "Agents";

    public int MaxToolIterations { get; set; } = 100;

    public int IngestMaxTransientRetries { get; set; } = 3;

    public int IngestRetryBaseDelayMs { get; set; } = 1_000;

    public int IngestRetryMaxDelayMs { get; set; } = 8_000;
}
