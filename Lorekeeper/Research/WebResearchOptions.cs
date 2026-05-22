namespace Lorekeeper.Research;

public sealed class WebResearchOptions
{
    public const string SectionName = "Research:Web";

    public string UserAgent { get; set; } = "LorekeeperResearch/1.0";
    public int RequestTimeoutSeconds { get; set; } = 30;
    public int MaxPageBytes { get; set; } = 2_000_000;
    public int MaxLinksReturned { get; set; } = 80;
    public int ReadPageMaxChars { get; set; } = 10_000;
    public int RetryAttempts { get; set; } = 1;
    public int RetryDelayMilliseconds { get; set; } = 1_500;
    public bool BlockPrivateNetworkTargets { get; set; } = true;
}
