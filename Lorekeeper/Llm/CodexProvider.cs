using Lorekeeper.Models;

namespace Lorekeeper.Llm;

public static class CodexProvider
{
    public const string AccountProviderKey = "openai-account";
    public const string ResponsesEndpoint = "https://chatgpt.com/backend-api/codex/responses";
    public const string PlatformEmbeddingsEndpoint = "https://api.openai.com/v1/embeddings";
    public const string PlatformModelsEndpoint = "https://api.openai.com/v1/models";
    public const string DefaultEmbeddingModel = "text-embedding-3-small";

    public static bool IsAccountBacked(LlmProvider provider) => provider.OpenAiAccountId is not null;
}
