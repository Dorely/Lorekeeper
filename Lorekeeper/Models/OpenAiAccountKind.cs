namespace Lorekeeper.Models;

/// <summary>
/// How an <see cref="OpenAiAccount"/> signs in and which inference route its
/// models use.
/// </summary>
public enum OpenAiAccountKind
{
    /// <summary>Codex OAuth login; chat, vision, and images use the ChatGPT Codex backend.</summary>
    Codex,

    /// <summary>Sign in with ChatGPT plan usage; chat and vision use the public Responses API.</summary>
    ChatGptPlan,
}
