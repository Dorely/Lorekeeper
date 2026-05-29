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
    public int MinDelayBetweenHostRequestsMilliseconds { get; set; } = 4_000;
    public int HostRequestJitterMilliseconds { get; set; } = 750;
    public int BlockedHostCooldownSeconds { get; set; } = 300;
    public int RepeatedFailureCooldownSeconds { get; set; } = 60;
    public int FailedHostCooldownThreshold { get; set; } = 3;
    public int FailedCandidateRetryCooldownMinutes { get; set; } = 30;
    public bool HonorRobotsTxt { get; set; } = true;
    public int RobotsTxtCacheMinutes { get; set; } = 60;
    public int MaxFollowLinksPerPage { get; set; } = 12;
    public int MaxLinksReturnedToModel { get; set; } = 40;
}
