namespace Lorekeeper.AiConsole;

/// <summary>Bound from the <c>AiConsole</c> configuration section.</summary>
public sealed class AiConsoleOptions
{
    /// <summary>
    /// Hard cap on the number of tool-call rounds in a single AI Console turn.
    /// When the cap is hit the entry is recorded as <see cref="Models.AiConsoleEntryStatus.Failed"/>.
    /// </summary>
    public int MaxToolIterations { get; set; } = 8;
}
