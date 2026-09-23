using Lorekeeper.Models;

namespace Lorekeeper.Llm;

public sealed record LlmProviderPreset(
    string Slug,
    string DisplayName,
    string EndpointUrl,
    AuthType AuthType,
    IReadOnlyList<string> SeededModels,
    string? KeyManagementUrl = null,
    string? Note = null);

/// <summary>
/// Catalog of built-in OpenAI-compatible provider presets used to pre-fill new
/// connections in Settings > Providers. Presets only supply defaults; every
/// connection still uses the same OpenAI-compatible chat-completions protocol.
/// </summary>
public static class LlmProviderCatalog
{
    public const string CustomPresetSlug = "custom";
    public const string OpenAiDefaultMainlineModel = "gpt-6-sol";

    public static readonly IReadOnlyList<LlmProviderPreset> Presets =
    [
        new("openai", "OpenAI", "https://api.openai.com/v1", AuthType.ApiKey,
        [
            OpenAiDefaultMainlineModel,
            "gpt-6-astra",
            "gpt-6-luna",
            "gpt-5.6-sol",
            "gpt-5.6-terra",
            "gpt-5.6-luna",
        ],
            KeyManagementUrl: "https://platform.openai.com/api-keys"),
        // OpenAI first-party is resolved as OpenAiFirstParty: no auto budget, standard field.
        new("cline", "Cline (usage billing)", "https://api.cline.bot/api/v1", AuthType.ApiKey,
        [
            "anthropic/claude-sonnet-4.5",
            "anthropic/claude-opus-4.5",
            "openai/gpt-5.1",
            "google/gemini-2.5-pro",
            "deepseek/deepseek-chat",
        ],
            KeyManagementUrl: "https://app.cline.bot/settings",
            Note: "Usage-billed Cline API. Create an API key in the Cline app under Settings > API Keys; model IDs use provider/model form."),
        new("cline-pass", "Cline (ClinePass)", "https://api.cline.bot/api/v1", AuthType.ApiKey,
        [
            "cline-pass/kimi-k3",
        ],
            KeyManagementUrl: "https://app.cline.bot/settings",
            Note: "Quota-based ClinePass plans use the same endpoint and API key with cline-pass/<model> slugs (no vendor prefix); see the Cline documentation for the current catalog."),
        new("anthropic", "Anthropic (OpenAI-compatible)", "https://api.anthropic.com/v1", AuthType.ApiKey,
        [
            "claude-sonnet-4-5",
            "claude-opus-4-5",
            "claude-haiku-4-5",
        ],
            KeyManagementUrl: "https://console.anthropic.com/settings/keys"),
        new("gemini", "Google Gemini (OpenAI-compatible)", "https://generativelanguage.googleapis.com/v1beta/openai", AuthType.ApiKey,
        [
            "gemini-2.5-pro",
            "gemini-2.5-flash",
        ],
            KeyManagementUrl: "https://aistudio.google.com/apikey"),
        new("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", AuthType.ApiKey,
        [
            "openai/gpt-5.1",
            "anthropic/claude-sonnet-4.5",
            "google/gemini-2.5-pro",
            "deepseek/deepseek-chat",
        ],
            KeyManagementUrl: "https://openrouter.ai/settings/keys"),
        new("groq", "Groq", "https://api.groq.com/openai/v1", AuthType.ApiKey,
        [
            "llama-3.3-70b-versatile",
            "openai/gpt-oss-120b",
        ],
            KeyManagementUrl: "https://console.groq.com/keys"),
        new("deepseek", "DeepSeek", "https://api.deepseek.com/v1", AuthType.ApiKey,
        [
            "deepseek-chat",
            "deepseek-reasoner",
        ],
            KeyManagementUrl: "https://platform.deepseek.com/api_keys"),
        new("xai", "xAI", "https://api.x.ai/v1", AuthType.ApiKey,
        [
            "grok-4",
            "grok-4-fast",
        ],
            KeyManagementUrl: "https://console.x.ai"),
        new("mistral", "Mistral AI", "https://api.mistral.ai/v1", AuthType.ApiKey,
        [
            "mistral-large-latest",
            "mistral-medium-latest",
            "mistral-small-latest",
        ],
            KeyManagementUrl: "https://console.mistral.ai/api-keys"),
        new("together", "Together AI", "https://api.together.xyz/v1", AuthType.ApiKey,
        [
            "meta-llama/Llama-3.3-70B-Instruct-Turbo",
            "deepseek-ai/DeepSeek-V3",
        ],
            KeyManagementUrl: "https://api.together.ai/settings/api-keys"),
        new("commandcode", "Command Code", "https://api.commandcode.ai/provider/v1", AuthType.ApiKey,
        [
            "deepseek/deepseek-v4-flash",
            "claude-sonnet-5",
            "google/gemini-3.7-flash",
            "moonshotai/Kimi-K3",
            "xiaomi/mimo-v2.5",
            "MiniMaxAI/MiniMax-M3",
        ],
            KeyManagementUrl: "https://commandcode.ai/settings/keys",
            Note: "OpenAI-compatible gateway for every top model (OpenAI, Anthropic, Gemini, DeepSeek, Kimi, etc.). Create an API key in Studio > Settings > API Keys and point any OpenAI client at https://api.commandcode.ai/provider/v1. Also exposes Anthropic Messages at /v1/messages and a models list at /v1/models; add x-cmdc-zdr: 1 for zero-data-retention when supported."),
        new("ollama", "Ollama (local)", "http://localhost:11434/v1", AuthType.None,
        [
            "llama3.2",
            "qwen3",
            "mistral",
        ]),
        new("lm-studio", "LM Studio (local)", "http://localhost:1234/v1", AuthType.None,
        [
        ]),
        // Local endpoints resolve as Local: no auto budget; model capability is unknown.
        new(CustomPresetSlug, "Custom / manual entry", "https://", AuthType.ApiKey,
        [
        ]),
    ];

    public static LlmProviderPreset? Find(string slug) =>
        Presets.FirstOrDefault(preset => string.Equals(preset.Slug, slug, StringComparison.OrdinalIgnoreCase));
}
