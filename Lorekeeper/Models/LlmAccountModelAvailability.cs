namespace Lorekeeper.Models;

/// <summary>
/// Whether the owning ChatGPT account's own model list offered this
/// account-backed model at the last successful refresh.
/// </summary>
public enum LlmAccountModelAvailability
{
    Unknown,
    Available,
    Unavailable,
}
