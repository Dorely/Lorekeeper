namespace Lorekeeper.Models;

public enum LlmReasoningEffort
{
    None,
    Minimal,
    Low,
    Medium,
    High,
    ExtraHigh,
    Maximum,
}

public static class LlmReasoningEffortExtensions
{
    public static string ToWireValue(this LlmReasoningEffort effort) => effort switch
    {
        LlmReasoningEffort.None => "none",
        LlmReasoningEffort.Minimal => "minimal",
        LlmReasoningEffort.Low => "low",
        LlmReasoningEffort.Medium => "medium",
        LlmReasoningEffort.High => "high",
        LlmReasoningEffort.ExtraHigh => "xhigh",
        LlmReasoningEffort.Maximum => "max",
        _ => throw new ArgumentOutOfRangeException(nameof(effort), effort, "Unsupported reasoning effort."),
    };
}
