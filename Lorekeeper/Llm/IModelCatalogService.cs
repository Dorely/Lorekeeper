using Lorekeeper.Models;

namespace Lorekeeper.Llm;

/// <summary>
/// One model advertised by a user-configured provider catalog.
/// <see cref="ContextLengthTokens"/> carries provider-advertised input-context
/// metadata when available and may be used as an editable prefill.
/// </summary>
public sealed record LlmDiscoveredModel(string Id, long? ContextLengthTokens);

public interface IModelCatalogService
{
    /// <summary>
    /// Lists chat models advertised by an OpenAI-compatible provider's
    /// <c>GET {base}/models</c> endpoint. Providers without a usable models
    /// endpoint make this fail; callers degrade gracefully to manual entry.
    /// OpenAI account rows are intentionally rejected because their catalog is
    /// bundled and versioned with Lorekeeper.
    /// </summary>
    Task<IReadOnlyList<LlmDiscoveredModel>> ListChatModelsAsync(LlmProvider provider, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists embedding model IDs for a connection: Ollama uses the native
    /// <c>GET /api/tags</c> endpoint; user-configured OpenAI-compatible
    /// connections use <c>GET {base}/models</c>. OpenAI account connections are
    /// intentionally rejected and require direct model entry.
    /// </summary>
    Task<IReadOnlyList<string>> ListEmbeddingModelsAsync(LlmProvider provider, EmbeddingApiKind apiKind, CancellationToken cancellationToken = default);
}
