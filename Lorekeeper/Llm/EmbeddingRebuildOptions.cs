namespace Lorekeeper.Llm;

public sealed class EmbeddingRebuildOptions
{
    public const string SectionName = "Embeddings:Rebuild";

    public int BatchSize { get; set; } = 4;
    public int DelayBetweenBatchesMilliseconds { get; set; } = 500;
    public int MaxRetries { get; set; } = 3;
    public int RetryDelayMilliseconds { get; set; } = 1000;
}

