namespace Lorekeeper.Models;

public class OpenAiAccount
{
    public int Id { get; set; }
    public OpenAiAccountKind Kind { get; set; } = OpenAiAccountKind.Codex;
    public string DisplayName { get; set; } = "OpenAI account";

    /// <summary>
    /// The verified account identity: the ChatGPT account ID for Codex, or the
    /// validated ID-token subject for ChatGPT plan usage.
    /// </summary>
    public string? ExternalAccountId { get; set; }

    /// <summary>Client ID issued by ChatGPT plan registration; reused for every later sign-in, refresh, and revocation.</summary>
    public string? IssuedClientId { get; set; }

    /// <summary>Stable opaque <c>ext_agent_host_id</c> sent on ChatGPT plan authorization; kept after sign-out.</summary>
    public string? HostId { get; set; }

    /// <summary>Email from the validated ChatGPT plan ID token, used only as a reauthorization login hint.</summary>
    public string? Email { get; set; }

    /// <summary>True when the latest ChatGPT plan grant includes <c>chatgpt.tokens.use.direct</c>.</summary>
    public bool PlanUsageGranted { get; set; }

    /// <summary>When ChatGPT plan inference last reported that this user or plan is not eligible.</summary>
    public DateTime? PlanIneligibleAt { get; set; }

    /// <summary>When the first-sign-in ChatGPT plan notice was dismissed.</summary>
    public DateTime? WelcomeAcknowledgedAt { get; set; }

    public DateTime? RequiresReauthenticationAt { get; set; }
    public string? LastAuthenticationError { get; set; }
    public DateTime? LastModelRefreshAt { get; set; }
    public string? LastModelRefreshError { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<LlmProvider> Models { get; set; } = [];
    public ICollection<OAuthToken> OAuthTokens { get; set; } = [];
}
