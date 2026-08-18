namespace Lorekeeper.Llm;

public sealed class AgentOptions
{
    public const string SectionName = "Agents";

    public int MaxToolIterations { get; set; } = 100;

    public int IngestMaxTransientRetries { get; set; } = 3;

    public int IngestRetryBaseDelayMs { get; set; } = 1_000;

    public int IngestRetryMaxDelayMs { get; set; } = 8_000;

    public int CodexRequestTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// Per-request timeout for OpenAI-compatible chat requests through the
    /// shared connection pool, in seconds. The default matches the previous
    /// hard-coded 10-minute transport limit.
    /// </summary>
    public int ChatRequestTimeoutSeconds { get; set; } = 600;
}
