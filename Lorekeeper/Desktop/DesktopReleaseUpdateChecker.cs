using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lorekeeper.Desktop;

public sealed record DesktopReleaseUpdate(string Version, Uri ReleaseUri);

public interface IDesktopReleaseUpdateChecker
{
    Task<DesktopReleaseUpdate?> CheckAsync(string currentVersion, CancellationToken cancellationToken = default);
}

public sealed class GitHubDesktopReleaseUpdateChecker(
    HttpClient httpClient,
    IConfiguration configuration,
    ILogger<GitHubDesktopReleaseUpdateChecker> logger) : IDesktopReleaseUpdateChecker
{
    private const string DefaultReleaseApiUrl =
        "https://api.github.com/repos/Dorely/Lorekeeper/releases/latest";
    private const string AllowedReleasePathPrefix = "/Dorely/Lorekeeper/releases/";
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private EntityTagHeaderValue? _etag;
    private GitHubRelease? _lastRelease;

    public async Task<DesktopReleaseUpdate?> CheckAsync(
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        if (!DesktopReleaseVersion.TryParse(currentVersion, out var installedVersion))
        {
            logger.LogWarning("Skipping desktop update check because installed version {Version} is not valid SemVer.", currentVersion);
            return null;
        }

        await _checkLock.WaitAsync(cancellationToken);
        try
        {
            var release = await GetLatestReleaseAsync(cancellationToken);
            if (release is null
                || release.Draft
                || release.Prerelease
                || !DesktopReleaseVersion.TryParse(release.TagName, out var latestVersion)
                || latestVersion.Prerelease.Count > 0
                || latestVersion.CompareTo(installedVersion) <= 0
                || !HasCompatibleAsset(release, latestVersion)
                || !TryValidateReleaseUri(release.HtmlUrl, out var releaseUri))
            {
                return null;
            }

            return new DesktopReleaseUpdate(latestVersion.ToString(), releaseUri);
        }
        finally
        {
            _checkLock.Release();
        }
    }

    private async Task<GitHubRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        var configuredUrl = configuration["Desktop:ReleaseApiUrl"];
        var releaseApiUrl = string.IsNullOrWhiteSpace(configuredUrl) ? DefaultReleaseApiUrl : configuredUrl.Trim();
        if (!Uri.TryCreate(releaseApiUrl, UriKind.Absolute, out var releaseApiUri)
            || releaseApiUri.Scheme != Uri.UriSchemeHttps
            || !releaseApiUri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
            || !releaseApiUri.IsDefaultPort
            || !string.IsNullOrEmpty(releaseApiUri.UserInfo)
            || !string.IsNullOrEmpty(releaseApiUri.Query)
            || !string.IsNullOrEmpty(releaseApiUri.Fragment)
            || !releaseApiUri.AbsolutePath.Equals(
                "/repos/Dorely/Lorekeeper/releases/latest",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Desktop:ReleaseApiUrl must be the Lorekeeper GitHub latest-release API URL.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, releaseApiUri);
        if (_etag is not null)
            request.Headers.IfNoneMatch.Add(_etag);

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified)
            return _lastRelease;

        response.EnsureSuccessStatusCode();
        var release = await response.Content.ReadFromJsonAsync(
            DesktopReleaseJsonContext.Default.GitHubRelease,
            cancellationToken);
        if (release is null)
            throw new InvalidOperationException("GitHub returned an empty latest-release response.");

        _etag = response.Headers.ETag;
        _lastRelease = release;
        return release;
    }

    private static bool TryValidateReleaseUri(string? value, out Uri releaseUri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var candidate)
            && candidate.Scheme == Uri.UriSchemeHttps
            && candidate.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && candidate.IsDefaultPort
            && string.IsNullOrEmpty(candidate.UserInfo)
            && candidate.AbsolutePath.StartsWith($"{AllowedReleasePathPrefix}tag/", StringComparison.Ordinal))
        {
            releaseUri = candidate;
            return true;
        }

        releaseUri = null!;
        return false;
    }

    private static bool HasCompatibleAsset(GitHubRelease release, DesktopReleaseVersion version)
    {
        if (release.Assets is null || release.Assets.Length == 0)
            return false;

        var versionText = version.ToString();
        if (OperatingSystem.IsWindows())
        {
            return HasAsset(release, $"Lorekeeper-Setup-{versionText}-x64.exe")
                && HasAsset(release, $"Lorekeeper-Portable-{versionText}-x64.exe");
        }

        if (OperatingSystem.IsMacOS())
        {
            var architecture = RuntimeInformation.OSArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X64 => "x64",
                _ => null,
            };
            return architecture is not null
                && HasAsset(release, $"Lorekeeper-{versionText}-{architecture}.dmg");
        }

        if (OperatingSystem.IsLinux() && RuntimeInformation.OSArchitecture == Architecture.X64)
        {
            return HasAsset(release, $"Lorekeeper-{versionText}-x86_64.AppImage")
                && HasAsset(release, $"Lorekeeper-{versionText}-amd64.deb");
        }

        return false;
    }

    private static bool HasAsset(GitHubRelease release, string expectedName) =>
        release.Assets?.Any(asset => asset.Name.Equals(expectedName, StringComparison.Ordinal)) == true;
}

public sealed record GitHubRelease(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("html_url")] string HtmlUrl,
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("prerelease")] bool Prerelease,
    [property: JsonPropertyName("assets")] GitHubReleaseAsset[]? Assets);

public sealed record GitHubReleaseAsset(
    [property: JsonPropertyName("name")] string Name);

[JsonSerializable(typeof(GitHubRelease))]
internal sealed partial class DesktopReleaseJsonContext : JsonSerializerContext;

internal sealed class DesktopReleaseVersion : IComparable<DesktopReleaseVersion>
{
    private DesktopReleaseVersion(string major, string minor, string patch, string[] prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
    }

    public string Major { get; }
    public string Minor { get; }
    public string Patch { get; }
    public IReadOnlyList<string> Prerelease { get; }

    public static bool TryParse(string? value, out DesktopReleaseVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var normalized = value.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
            normalized = normalized[1..];

        var buildSeparator = normalized.IndexOf('+');
        if (buildSeparator >= 0)
        {
            if (!ValidIdentifiers(normalized[(buildSeparator + 1)..], allowLeadingZeroes: true)) return false;
            normalized = normalized[..buildSeparator];
        }

        var prerelease = Array.Empty<string>();
        var prereleaseSeparator = normalized.IndexOf('-');
        if (prereleaseSeparator >= 0)
        {
            var prereleaseText = normalized[(prereleaseSeparator + 1)..];
            if (!ValidIdentifiers(prereleaseText, allowLeadingZeroes: false)) return false;
            prerelease = prereleaseText.Split('.');
            normalized = normalized[..prereleaseSeparator];
        }

        var core = normalized.Split('.');
        if (core.Length != 3
            || !TryParseCore(core[0], out var major)
            || !TryParseCore(core[1], out var minor)
            || !TryParseCore(core[2], out var patch))
        {
            return false;
        }

        version = new DesktopReleaseVersion(major, minor, patch, prerelease);
        return true;
    }

    public int CompareTo(DesktopReleaseVersion? other)
    {
        if (other is null) return 1;

        var coreComparison = CompareNumericIdentifier(Major, other.Major);
        if (coreComparison == 0) coreComparison = CompareNumericIdentifier(Minor, other.Minor);
        if (coreComparison == 0) coreComparison = CompareNumericIdentifier(Patch, other.Patch);
        if (coreComparison != 0) return coreComparison;

        if (Prerelease.Count == 0) return other.Prerelease.Count == 0 ? 0 : 1;
        if (other.Prerelease.Count == 0) return -1;

        for (var index = 0; index < Math.Min(Prerelease.Count, other.Prerelease.Count); index++)
        {
            var comparison = CompareIdentifier(Prerelease[index], other.Prerelease[index]);
            if (comparison != 0) return comparison;
        }

        return Prerelease.Count.CompareTo(other.Prerelease.Count);
    }

    public override string ToString()
    {
        var core = $"{Major}.{Minor}.{Patch}";
        return Prerelease.Count == 0 ? core : $"{core}-{string.Join('.', Prerelease)}";
    }

    private static bool TryParseCore(string value, out string result)
    {
        result = value;
        return value.Length > 0
            && value.All(char.IsAsciiDigit)
            && (value.Length == 1 || value[0] != '0');
    }

    private static bool ValidIdentifiers(string value, bool allowLeadingZeroes)
    {
        var identifiers = value.Split('.');
        return identifiers.Length > 0 && identifiers.All(identifier =>
            identifier.Length > 0
            && identifier.All(character => char.IsAsciiLetterOrDigit(character) || character == '-')
            && (allowLeadingZeroes
                || !identifier.All(char.IsAsciiDigit)
                || identifier.Length == 1
                || identifier[0] != '0'));
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = left.All(char.IsAsciiDigit);
        var rightNumeric = right.All(char.IsAsciiDigit);
        if (leftNumeric && rightNumeric)
            return CompareNumericIdentifier(left, right);
        if (leftNumeric) return -1;
        if (rightNumeric) return 1;
        return string.CompareOrdinal(left, right);
    }

    private static int CompareNumericIdentifier(string left, string right)
    {
        var lengthComparison = left.Length.CompareTo(right.Length);
        return lengthComparison != 0 ? lengthComparison : string.CompareOrdinal(left, right);
    }
}
