using Lorekeeper.Models;

namespace Lorekeeper.Llm;

/// <summary>
/// Max-tokens request field name used by an OpenAI-compatible chat request.
/// </summary>
public enum WireMaxTokensField
{
    /// <summary>OpenAI-standard <c>max_completion_tokens</c>.</summary>
    Standard,

    /// <summary>Legacy <c>max_tokens</c> accepted by several open-weight gateways.</summary>
    Legacy,
}

/// <summary>
/// Endpoint-keyed wire-compatibility classification for OpenAI-compatible chat
/// providers. The resolver derives safe defaults from the endpoint host alone
/// (mirroring pi's <c>detectCompat</c> model) rather than maintaining a
/// per-provider model matrix; per-row user overrides on <c>LlmProvider</c> take
/// precedence over anything resolved here. Anything that is not the OpenAI first
/// party (or a bounded set of known non-first-class endpoints) is treated as a
/// generic OpenAI-compatible gateway.
/// </summary>
internal static class LlmWireCompatResolver
{
    /// <summary>
    /// Output-token-budget default for generic OpenAI-compatible gateways. Pi
    /// uses 16 384 for unknown models, but with the ~38k-token Editor system
    /// prompt Qwen/Kimi generate at ~17 tok/s so 16k implies ~16 min of
    /// streaming — well beyond the Cline gateway's ~10 min stream lifetime.
    /// Bisection: full system no-tools without a budget dies at exactly
    /// 10 min (B02), with 4096 it finishes in ~53 s (B13); with 40 tools
    /// standard max_completion_tokens is ignored by the gateway (B12 dies at
    /// 10 min) while legacy max_tokens is respected (B15 finishes in ~6 min
    /// with 48k reasoning chars and no transport error). Default is doubled to
    /// 8192 per user tuning — still safely under the ~10 min gateway cut for
    /// the measured prompt/throughput, while giving more headroom; set a
    /// per-row MaxOutputTokens override in Settings > Providers to tune further.
    /// </summary>
    public const int DefaultMaxOutputTokens = 8192;

    public enum EndpointClass
    {
        /// <summary>OpenAI first-party: OpenAI-standard field name, no auto budget.</summary>
        OpenAiFirstParty,

        /// <summary>Open-weight gateway that uses the legacy <c>max_tokens</c> field.</summary>
        LegacyField,

        /// <summary>Local server (Ollama, LM Studio): legacy field name, no auto budget
        /// because local model output capability is unknown and over-constraining it
        /// would break generation.</summary>
        Local,

        /// <summary>OpenAI-standard field name with the universal output-token budget.</summary>
        GenericCompat,
    }

    public static EndpointClass Classify(string? endpointUrl)
    {
        if (string.IsNullOrWhiteSpace(endpointUrl)
            || !Uri.TryCreate(endpointUrl, UriKind.Absolute, out var uri))
        {
            return EndpointClass.GenericCompat;
        }

        var host = uri.Host;

        if (host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase))
            return EndpointClass.OpenAiFirstParty;

        if (IsLocalHost(host))
            return EndpointClass.Local;

        if (IsLegacyFieldHost(host))
            return EndpointClass.LegacyField;

        return EndpointClass.GenericCompat;
    }

    public static WireMaxTokensField ResolveMaxTokensField(string? endpointUrl, LlmMaxTokensField overrideField) =>
        overrideField switch
        {
            LlmMaxTokensField.Default => Classify(endpointUrl) switch
            {
                EndpointClass.OpenAiFirstParty => WireMaxTokensField.Standard,
                EndpointClass.LegacyField => WireMaxTokensField.Legacy,
                EndpointClass.Local => WireMaxTokensField.Legacy,
                _ => WireMaxTokensField.Standard,
            },
            LlmMaxTokensField.Standard => WireMaxTokensField.Standard,
            LlmMaxTokensField.Legacy => WireMaxTokensField.Legacy,
            _ => WireMaxTokensField.Standard,
        };

    /// <summary>
    /// Resolves the effective output-token budget for a provider. Explicit row
    /// values always win; otherwise OpenAI first-party and local servers send
    /// none (so provider/model defaults apply), while every other
    /// non-first-class endpoint gets the universal default.
    /// </summary>
    public static int? ResolveMaxOutputTokens(string? endpointUrl, LlmMaxTokensField overrideField, int? rowMaxOutputTokens)
    {
        if (rowMaxOutputTokens is > 0)
            return rowMaxOutputTokens;

        if (Classify(endpointUrl) is EndpointClass.OpenAiFirstParty or EndpointClass.Local)
            return null;

        return DefaultMaxOutputTokens;
    }

    private static bool IsLocalHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
        || host.Equals("::1", StringComparison.OrdinalIgnoreCase);

    private static bool IsLegacyFieldHost(string host) =>
        host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("api.together.xyz", StringComparison.OrdinalIgnoreCase)
        || host.Equals("api.together.ai", StringComparison.OrdinalIgnoreCase)
        || host.Equals("api.cline.bot", StringComparison.OrdinalIgnoreCase);
}