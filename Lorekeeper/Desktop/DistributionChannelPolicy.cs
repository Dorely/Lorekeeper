namespace Lorekeeper.Desktop;

public enum DistributionChannel
{
    Development,
    Free,
    Store,
}

public enum DesktopUpdatePolicy
{
    Disabled,
    ManualGitHubRelease,
    StoreManaged,
}

/// <summary>
/// Resolves the update boundary from immutable build metadata and the host mode.
/// No user setting can select a distribution channel.
/// </summary>
public sealed record DistributionChannelPolicy(
    DistributionChannel? Channel,
    DesktopUpdatePolicy UpdatePolicy,
    string? ValidationError = null)
{
    public const string BuildMetadataKey = "LorekeeperDistributionChannel";

    public bool UsesGitHubReleaseChecks => UpdatePolicy == DesktopUpdatePolicy.ManualGitHubRelease;

    public static DistributionChannelPolicy Resolve(bool isDevelopment, string? buildMetadata)
    {
        if (isDevelopment)
            return new DistributionChannelPolicy(DistributionChannel.Development, DesktopUpdatePolicy.Disabled);

        return buildMetadata switch
        {
            nameof(DistributionChannel.Free) => new DistributionChannelPolicy(
                DistributionChannel.Free,
                DesktopUpdatePolicy.ManualGitHubRelease),
            nameof(DistributionChannel.Store) => new DistributionChannelPolicy(
                DistributionChannel.Store,
                DesktopUpdatePolicy.StoreManaged),
            _ => new DistributionChannelPolicy(
                null,
                DesktopUpdatePolicy.Disabled,
                $"Build metadata '{BuildMetadataKey}' must be exactly '{nameof(DistributionChannel.Free)}' or '{nameof(DistributionChannel.Store)}'.")
        };
    }
}
