namespace Lorekeeper.VersionHistory.GitHub;

/// <summary>
/// Configuration for Lorekeeper's GitHub device authorization and REST API
/// integration. The defaults point at GitHub.com; the URI properties remain
/// configurable so request handlers can be substituted by tests.
/// </summary>
public sealed class GitHubConnectionOptions
{
    public const string SectionName = "VersionHistory:GitHub";

    public const string DefaultApiVersion = "2022-11-28";
    public const string DefaultUserAgent = "Lorekeeper-VersionHistory";
    public const string DefaultScope = "repo";

    public string ClientId { get; set; } = string.Empty;

    public Uri DeviceCodeEndpoint { get; set; } =
        new("https://github.com/login/device/code", UriKind.Absolute);

    public Uri AccessTokenEndpoint { get; set; } =
        new("https://github.com/login/oauth/access_token", UriKind.Absolute);

    public Uri ApiBaseUri { get; set; } =
        new("https://api.github.com/", UriKind.Absolute);

    public string ApiVersion { get; set; } = DefaultApiVersion;

    public string UserAgent { get; set; } = DefaultUserAgent;

    public string DefaultOAuthScope { get; set; } = DefaultScope;

    /// <summary>
    /// Hard upper bound for one repository-list operation. GitHub's normal
    /// Link pagination is finite; this guard also protects against a broken or
    /// malicious server returning an endless sequence of pages.
    /// </summary>
    public int MaximumRepositoryPages { get; set; } = 1_000;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ClientId))
            throw new GitHubConnectionException(
                GitHubConnectionErrorCode.InvalidConfiguration,
                "GitHub OAuth is not configured. Set VersionHistory:GitHub:ClientId to the GitHub OAuth app client ID.");

        ValidateEndpoint(DeviceCodeEndpoint, nameof(DeviceCodeEndpoint));
        ValidateEndpoint(AccessTokenEndpoint, nameof(AccessTokenEndpoint));
        ValidateEndpoint(ApiBaseUri, nameof(ApiBaseUri));

        if (!string.IsNullOrWhiteSpace(ApiBaseUri.UserInfo))
            throw new GitHubConnectionException(
                GitHubConnectionErrorCode.InvalidConfiguration,
                "VersionHistory:GitHub:ApiBaseUri must not contain credentials.");

        if (!string.IsNullOrEmpty(ApiBaseUri.Query) || !string.IsNullOrEmpty(ApiBaseUri.Fragment))
            throw new GitHubConnectionException(
                GitHubConnectionErrorCode.InvalidConfiguration,
                "VersionHistory:GitHub:ApiBaseUri must contain only the API origin and base path.");

        if (string.IsNullOrWhiteSpace(ApiVersion))
            throw new GitHubConnectionException(
                GitHubConnectionErrorCode.InvalidConfiguration,
                "VersionHistory:GitHub:ApiVersion must not be empty.");

        if (string.IsNullOrWhiteSpace(UserAgent))
            throw new GitHubConnectionException(
                GitHubConnectionErrorCode.InvalidConfiguration,
                "VersionHistory:GitHub:UserAgent must not be empty.");

        if (string.IsNullOrWhiteSpace(DefaultOAuthScope))
            throw new GitHubConnectionException(
                GitHubConnectionErrorCode.InvalidConfiguration,
                "VersionHistory:GitHub:DefaultOAuthScope must not be empty.");

        if (MaximumRepositoryPages is < 1 or > 10_000)
            throw new GitHubConnectionException(
                GitHubConnectionErrorCode.InvalidConfiguration,
                "VersionHistory:GitHub:MaximumRepositoryPages must be between 1 and 10,000.");
    }

    private static void ValidateEndpoint(Uri? endpoint, string propertyName)
    {
        if (endpoint is null || !endpoint.IsAbsoluteUri || endpoint.IsFile)
            throw new GitHubConnectionException(
                GitHubConnectionErrorCode.InvalidConfiguration,
                $"VersionHistory:GitHub:{propertyName} must be an absolute HTTP(S) URI.");

        if (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new GitHubConnectionException(
                GitHubConnectionErrorCode.InvalidConfiguration,
                $"VersionHistory:GitHub:{propertyName} must use HTTP or HTTPS.");

        if (!string.IsNullOrWhiteSpace(endpoint.UserInfo))
            throw new GitHubConnectionException(
                GitHubConnectionErrorCode.InvalidConfiguration,
                $"VersionHistory:GitHub:{propertyName} must not contain credentials in its URI.");
    }
}
