using Lorekeeper.Models;
using Lorekeeper.Persistence.Repositories;

namespace Lorekeeper.Llm;

public sealed class ProviderEmbeddingService(
    IEmbeddingConfigurationRepository configurations,
    IEmbeddingClient client,
    IConfiguration configuration,
    ILogger<ProviderEmbeddingService> logger) : IEmbeddingService
{
    private int MaxChunkChars => configuration.GetValue("Embeddings:MaxChunkChars", 6000);
    private int RequestBatchSize => Math.Max(1, configuration.GetValue("Embeddings:RequestBatchSize", 8));
    private int DelayBetweenRequestsMilliseconds => Math.Max(0, configuration.GetValue("Embeddings:DelayBetweenRequestsMilliseconds", 0));

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        var active = await configurations.GetAsync(cancellationToken);
        return active is not null
            && active.Provider is not null
            && active.Dimensions > 0
            && !string.IsNullOrWhiteSpace(active.ModelId);
    }

    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        var results = await GenerateEmbeddingsAsync([text], cancellationToken);
        return results[0];
    }

    public async Task<IList<float[]>> GenerateEmbeddingsAsync(IList<string> texts, CancellationToken cancellationToken = default)
    {
        var active = await configurations.GetAsync(cancellationToken)
            ?? throw new InvalidOperationException("No active embedding model is configured.");

        var prepared = new List<string>(texts.Count);
        for (var i = 0; i < texts.Count; i++)
        {
            var text = texts[i] ?? string.Empty;
            if (text.Length > MaxChunkChars)
            {
                logger.LogWarning(
                    "Embedding input #{Index} is {Length} chars (>{Max}); truncating before sending to the configured embedding model.",
                    i,
                    text.Length,
                    MaxChunkChars);
                prepared.Add(text[..MaxChunkChars]);
            }
            else
            {
                prepared.Add(text);
            }
        }

        var embeddings = new List<float[]>(prepared.Count);
        var batchSize = RequestBatchSize;
        var delay = DelayBetweenRequestsMilliseconds;
        for (var index = 0; index < prepared.Count; index += batchSize)
        {
            var batch = prepared.Skip(index).Take(batchSize).ToList();
            var batchEmbeddings = await client.GenerateAsync(active.Provider, active.ApiKind, active.ModelId, batch, cancellationToken);
            embeddings.AddRange(batchEmbeddings);
            if (delay > 0 && index + batchSize < prepared.Count)
                await Task.Delay(delay, cancellationToken);
        }

        if (embeddings.Any(embedding => embedding.Length != active.Dimensions))
            throw new InvalidOperationException($"Embedding endpoint returned vectors that do not match the configured {active.Dimensions} dimensions.");
        return embeddings;
    }
}
