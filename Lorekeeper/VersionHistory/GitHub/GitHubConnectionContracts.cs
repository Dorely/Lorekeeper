using System.Net;
using System.Text.Json.Serialization;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;

namespace Lorekeeper.VersionHistory.GitHub;

public enum GitHubConnectionErrorCode
{
    InvalidConfiguration,
    InvalidRequest,
    TransportFailure,
    InvalidResponse,
    OAuthAuthorizationPending,
    OAuthSlowDown,
    AccessDenied,
    ExpiredToken,
    Unauthorized,
    Forbidden,
    NotFound,
    Conflict,
    RateLimited,
    ConnectionNotFound,
    ConnectionInUse,
    UnsupportedCredentials,
}

/// <summary>
/// An actionable, non-secret failure from GitHub authorization or account
/// management. Token values and response bodies are intentionally omitted.
/// </summary>
public sealed class GitHubConnectionException : Exception
{
    public GitHubConnectionException(
        GitHubConnectionErrorCode code,
        string message,
        HttpStatusCode? statusCode = null,
        TimeSpan? retryAfter = null,
        Exception? innerException = null,
        string? oauthError = null)
        : base(message, innerException)
    {
        Code = code;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
        OAuthError = oauthError;
    }

    public GitHubConnectionErrorCode Code { get; }
    public HttpStatusCode? StatusCode { get; }
    public TimeSpan? RetryAfter { get; }

    /// <summary>
    /// The documented OAuth error identifier, when GitHub returned one. It is
    /// safe for callers to use as a machine-readable branch key.
    /// </summary>
    public string? OAuthError { get; }
}

/// <summary>
/// Device authorization returned by GitHub. DeviceCode is required to finish
/// the flow but is excluded from JSON serialization and from ToString output.
/// </summary>
public sealed class GitHubDeviceAuthorization
{
    [JsonConstructor]
    public GitHubDeviceAuthorization(
        string deviceCode,
        string userCode,
        Uri verificationUri,
        Uri? verificationUriComplete,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        TimeSpan pollInterval,
        string scope)
    {
        DeviceCode = deviceCode;
        UserCode = userCode;
        VerificationUri = verificationUri;
        VerificationUriComplete = verificationUriComplete;
        IssuedAt = issuedAt;
        ExpiresAt = expiresAt;
        PollInterval = pollInterval;
        Scope = scope;
    }

    [JsonIgnore]
    public string DeviceCode { get; }

    public string UserCode { get; }
    public Uri VerificationUri { get; }
    public Uri? VerificationUriComplete { get; }
    public DateTimeOffset IssuedAt { get; }
    public DateTimeOffset ExpiresAt { get; }
    public TimeSpan PollInterval { get; }
    public string Scope { get; }

    public override string ToString() =>
        $"GitHub device authorization for {UserCode}; expires {ExpiresAt:O}.";
}

public sealed record GitHubConnectionSummary(
    Guid Id,
    long GitHubUserId,
    string AccountLogin,
    string? Scope,
    DateTime? AccessTokenExpiresAt,
    DateTime? LastValidatedAt,
    DateTime? RevokedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record GitHubRepositoryInfo(
    long Id,
    string Owner,
    string Name,
    string FullName,
    bool IsPrivate,
    string? HtmlUrl,
    string? CloneUrl,
    string? SshUrl,
    string? DefaultBranch);

public sealed record GitHubRepositoryCreateRequest(
    string Name,
    string? Description = null,
    bool IsPrivate = true,
    bool HasIssues = true,
    bool HasProjects = true,
    bool HasWiki = true,
    bool AutoInit = false);

public interface IGitHubConnectionService
{
    Task<GitHubDeviceAuthorization> StartDeviceAuthorizationAsync(
        string? scope = null,
        CancellationToken cancellationToken = default);

    Task<GitHubConnectionSummary> CompleteDeviceAuthorizationAsync(
        GitHubDeviceAuthorization authorization,
        CancellationToken cancellationToken = default);

    Task<GitHubConnectionSummary> ValidateConnectionAsync(
        Guid connectionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitHubConnectionSummary>> ListConnectionsAsync(
        CancellationToken cancellationToken = default);

    Task DisconnectAsync(
        Guid connectionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitHubRepositoryInfo>> ListRepositoriesAsync(
        Guid connectionId,
        int pageSize = 100,
        CancellationToken cancellationToken = default);

    Task<GitHubRepositoryInfo> CreateRepositoryAsync(
        Guid connectionId,
        GitHubRepositoryCreateRequest request,
        CancellationToken cancellationToken = default);

    Task<CredentialsHandler> CreateCredentialsHandlerAsync(
        Guid connectionId,
        CancellationToken cancellationToken = default);
}
