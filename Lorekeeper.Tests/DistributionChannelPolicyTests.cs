using Lorekeeper.Desktop;

namespace Lorekeeper.Tests;

public sealed class DistributionChannelPolicyTests
{
    [Fact]
    public void DevelopmentHostSuppressesEveryUpdaterRegardlessOfBuildMetadata()
    {
        var policy = DistributionChannelPolicy.Resolve(isDevelopment: true, nameof(DistributionChannel.Store));

        Assert.Equal(DistributionChannel.Development, policy.Channel);
        Assert.Equal(DesktopUpdatePolicy.Disabled, policy.UpdatePolicy);
        Assert.False(policy.UsesGitHubReleaseChecks);
        Assert.Null(policy.ValidationError);
    }

    [Fact]
    public void FreeReleaseUsesOnlyManualGitHubReleaseDiscovery()
    {
        var policy = DistributionChannelPolicy.Resolve(isDevelopment: false, nameof(DistributionChannel.Free));

        Assert.Equal(DistributionChannel.Free, policy.Channel);
        Assert.Equal(DesktopUpdatePolicy.ManualGitHubRelease, policy.UpdatePolicy);
        Assert.True(policy.UsesGitHubReleaseChecks);
        Assert.Null(policy.ValidationError);
    }

    [Fact]
    public void StoreReleaseSuppressesGitHubAndElectronUpdatePaths()
    {
        var policy = DistributionChannelPolicy.Resolve(isDevelopment: false, nameof(DistributionChannel.Store));

        Assert.Equal(DistributionChannel.Store, policy.Channel);
        Assert.Equal(DesktopUpdatePolicy.StoreManaged, policy.UpdatePolicy);
        Assert.False(policy.UsesGitHubReleaseChecks);
        Assert.Null(policy.ValidationError);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("free")]
    [InlineData("Store ")]
    [InlineData("Preview")]
    public void MalformedReleaseMetadataFailsClosed(string? buildMetadata)
    {
        var policy = DistributionChannelPolicy.Resolve(isDevelopment: false, buildMetadata);

        Assert.Null(policy.Channel);
        Assert.Equal(DesktopUpdatePolicy.Disabled, policy.UpdatePolicy);
        Assert.False(policy.UsesGitHubReleaseChecks);
        Assert.NotNull(policy.ValidationError);
    }
}
