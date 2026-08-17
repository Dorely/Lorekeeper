using Lorekeeper.Models;

namespace Lorekeeper.Llm;

public interface IModelCatalogService
{
    /// <summary>
    /// Lists chat model IDs advertised by an OpenAI-compatible provider's
    /// <c>GET {base}/models</c> endpoint. Providers without a usable models
    /// endpoint make this fail; callers degrade gracefully to manual entry.
    /// </summary>
    Task<IReadOnlyList<string>> ListChatModelsAsync(LlmProvider provider, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists embedding model IDs for a connection: Ollama uses the native
    /// <c>GET /api/tags</c> endpoint; OpenAI-compatible connections (including
    /// the Codex platform endpoint) use <c>GET {base}/models</c>.
    /// </summary>
    Task<IReadOnlyList<string>> ListEmbeddingModelsAsync(LlmProvider provider, EmbeddingApiKind apiKind, CancellationToken cancellationToken = default);
}
