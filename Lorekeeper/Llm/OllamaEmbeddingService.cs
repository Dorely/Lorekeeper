using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Lorekeeper.Llm;

/// <summary>
/// Embedding service backed by an Ollama server (defaults to <c>nomic-embed-text</c>, 768d).
/// <para>
/// TODO: chunk-and-average oversized inputs once a real document chunker is in place. For
/// now we hard-truncate at <c>Embeddings:MaxChunkChars</c> and warn — sufficient for the
/// short payloads needed by the provider-test smoke flow.
/// </para>
/// </summary>
public class OllamaEmbeddingService(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<OllamaEmbeddingService> logger) : IEmbeddingService
{
    private string OllamaUrl => configuration["Embeddings:OllamaUrl"] ?? "http://localhost:11434";
    private string ModelName => configuration["Embeddings:ModelName"] ?? "nomic-embed-text";
    private int MaxChunkChars => configuration.GetValue("Embeddings:MaxChunkChars", 6000);

    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        var results = await GenerateEmbeddingsAsync([text], cancellationToken);
        return results[0];
    }

    public async Task<IList<float[]>> GenerateEmbeddingsAsync(IList<string> texts, CancellationToken cancellationToken = default)
    {
        var prepared = new List<string>(texts.Count);
        for (var i = 0; i < texts.Count; i++)
        {
            var text = texts[i];
            if (text.Length > MaxChunkChars)
            {
                logger.LogWarning(
                    "Embedding input #{Index} is {Length} chars (>{Max}); truncating. Wire up a chunker before relying on long-form embeddings.",
                    i, text.Length, MaxChunkChars);
                prepared.Add(text[..MaxChunkChars]);
            }
            else
            {
                prepared.Add(text);
            }
        }

        var client = httpClientFactory.CreateClient();
        var requestUrl = $"{OllamaUrl.TrimEnd('/')}/api/embed";

        var request = new OllamaEmbedRequest
        {
            Model = ModelName,
            Input = prepared
        };

        logger.LogDebug("Generating embeddings for {Count} texts via Ollama ({Model})", prepared.Count, ModelName);

        var response = await client.PostAsJsonAsync(requestUrl, request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError("Ollama embed failed ({Status}): {Error}", response.StatusCode, errorBody);
            throw new HttpRequestException(
                $"Ollama embed failed ({response.StatusCode}): {errorBody}",
                null, response.StatusCode);
        }

        var result = await response.Content.ReadFromJsonAsync<OllamaEmbedResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Ollama returned null response");

        if (result.Embeddings is null || result.Embeddings.Count != prepared.Count)
            throw new InvalidOperationException(
                $"Expected {prepared.Count} embeddings, got {result.Embeddings?.Count ?? 0}");

        logger.LogDebug("Generated {Count} embeddings ({Dimensions}d)", result.Embeddings.Count, result.Embeddings[0].Length);
        return result.Embeddings;
    }

    private sealed class OllamaEmbedRequest
    {
        [JsonPropertyName("model")]
        public required string Model { get; set; }

        [JsonPropertyName("input")]
        public required List<string> Input { get; set; }
    }

    private sealed class OllamaEmbedResponse
    {
        [JsonPropertyName("embeddings")]
        public List<float[]>? Embeddings { get; set; }
    }
}
