namespace Lorekeeper.Models;

/// <summary>
/// Controls which request-field name carries the output-token budget for an
/// OpenAI-compatible chat request. Several open-weight providers accept only
/// the legacy <c>max_tokens</c> field, while OpenAI-style endpoints use
/// <c>max_completion_tokens</c>.
/// </summary>
public enum LlmMaxTokensField
{
    /// <summary>Let the endpoint-based wire-compat classification decide.</summary>
    Default,

    /// <summary>Always send <c>max_completion_tokens</c> (OpenAI standard).</summary>
    Standard,

    /// <summary>Always send <c>max_tokens</c> (legacy open-weight gateway field).</summary>
    Legacy,
}