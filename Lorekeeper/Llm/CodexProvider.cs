using Lorekeeper.Models;

namespace Lorekeeper.Llm;

public static class CodexProvider
{
    public const string AccountProviderKey = "openai-account";
    public const string ResponsesEndpoint = "https://chatgpt.com/backend-api/codex/responses";

    public static bool IsAccountBacked(LlmProvider provider) => provider.OpenAiAccountId is not null;
}
