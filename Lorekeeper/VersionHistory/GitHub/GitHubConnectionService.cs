using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lorekeeper.VersionHistory.GitHub;

/// <summary>
/// Owns GitHub's device flow, account repository discovery, and the app-level
/// credential rows used by version-control remotes. Git transport is kept out
/// of this service; callers receive a LibGit2Sharp credential callback only.
/// </summary>
public sealed class GitHubConnectionService(
    HttpClient httpClient,
    IOptions<GitHubConnectionOptions> options,
    IAppDatabaseOperationFactory database) : IGitHubConnectionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly GitHubConnectionOptions _options = options.Value;

    public async Task<GitHubDeviceAuthorization> StartDeviceAuthorizationAsync(
        string? scope = null,
        CancellationToken cancellationToken = default)
    {
        ValidateOptions();

        var requestedScope = string.IsNullOrWhiteSpace(scope)
            ? _options.DefaultOAuthScope.Trim()
            : scope.Trim();

        if (requestedScope.Length == 0)
            throw InvalidRequest("GitHub OAuth scope must not be empty.");

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.DeviceCodeEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _options.ClientId.Trim(),
                ["scope"] = requestedScope,
            }),
        };
        AddOAuthHeaders(request);

        using var response = await SendAsync(request, "start GitHub device authorization", cancellationToken);
        var payload = await DeserializeAsync<DeviceCodeResponse>(response, "device authorization", cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw CreateOAuthHttpException(response, "start GitHub device authorization", payload?.Error);

        if (payload is null || string.IsNullOrWhiteSpace(payload.DeviceCode) ||
            string.IsNullOrWhiteSpace(payload.UserCode) || string.IsNullOrWhiteSpace(payload.VerificationUri) ||
            payload.ExpiresIn <= 0)
            throw InvalidResponse("GitHub returned an incomplete device authorization response.");

        if (!Uri.TryCreate(payload.VerificationUri, UriKind.Absolute, out var verificationUri) ||
            !IsAllowedVerificationUri(verificationUri))
            throw InvalidResponse("GitHub returned an invalid device verification URI.");

        Uri? verificationUriComplete = null;
        if (!string.IsNullOrWhiteSpace(payload.VerificationUriComplete))
        {
            if (!Uri.TryCreate(payload.VerificationUriComplete, UriKind.Absolute, out verificationUriComplete) ||
                !IsAllowedVerificationUri(verificationUriComplete))
                throw InvalidResponse("GitHub returned an invalid complete device verification URI.");
        }

        var issuedAt = DateTimeOffset.UtcNow;
        var pollInterval = TimeSpan.FromSeconds(Math.Max(1, payload.Interval));
        return new GitHubDeviceAuthorization(
            payload.DeviceCode,
            payload.UserCode,
            verificationUri,
            verificationUriComplete,
            issuedAt,
            issuedAt.AddSeconds(payload.ExpiresIn),
            pollInterval,
            requestedScope);
    }

    public async Task<GitHubConnectionSummary> CompleteDeviceAuthorizationAsync(
        GitHubDeviceAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        ValidateOptions();
        ArgumentNullException.ThrowIfNull(authorization);

        if (string.IsNullOrWhiteSpace(authorization.DeviceCode))
            throw InvalidRequest("The GitHub device authorization is missing its device code.");

        var interval = authorization.PollInterval <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(5)
            : authorization.PollInterval;
        var nextPollAt = DateTimeOffset.UtcNow;

        while (true)
        {
            var now = DateTimeOffset.UtcNow;
            if (now >= authorization.ExpiresAt)
                throw new GitHubConnectionException(
                    GitHubConnectionErrorCode.ExpiredToken,
                    "The GitHub device authorization expired. Start a new authorization flow.",
                    oauthError: "expired_token");

            if (now < nextPollAt)
                await Task.Delay(nextPollAt - now, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            now = DateTimeOffset.UtcNow;
            if (now >= authorization.ExpiresAt)
                throw new GitHubConnectionException(
                    GitHubConnectionErrorCode.ExpiredToken,
                    "The GitHub device authorization expired while waiting to poll. Start a new authorization flow.",
                    oauthError: "expired_token");

            using var request = new HttpRequestMessage(HttpMethod.Post, _options.AccessTokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = _options.ClientId.Trim(),
                    ["device_code"] = authorization.DeviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                }),
            };
            AddOAuthHeaders(request);

            using var response = await SendAsync(request, "poll GitHub device authorization", cancellationToken);
            var payload = await DeserializeAsync<DeviceTokenResponse>(response, "device token", cancellationToken);

            if (response.IsSuccessStatusCode && payload is not null && !string.IsNullOrWhiteSpace(payload.AccessToken))
            {
                var token = CreateToken(payload, authorization.Scope);
                var user = await GetAuthenticatedUserAsync(token.AccessToken, cancellationToken);
                return await PersistConnectionAsync(user, token, cancellationToken);
            }

            var oauthError = payload?.Error?.Trim().ToLowerInvariant();
            switch (oauthError)
            {
                case "authorization_pending":
                    nextPollAt = DateTimeOffset.UtcNow.Add(interval);
                    continue;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    nextPollAt = DateTimeOffset.UtcNow.Add(interval);
                    continue;
                case "access_denied":
                    throw new GitHubConnectionException(
                        GitHubConnectionErrorCode.AccessDenied,
                        "GitHub authorization was denied. Approve the request or start a new authorization flow.",
                        oauthError: oauthError);
                case "expired_token":
                    throw new GitHubConnectionException(
                        GitHubConnectionErrorCode.ExpiredToken,
                        "The GitHub device authorization expired. Start a new authorization flow.",
                        oauthError: oauthError);
                default:
                    if (!response.IsSuccessStatusCode)
                        throw CreateOAuthHttpException(response, "poll GitHub device authorization", oauthError);

                    throw new GitHubConnectionException(
                        GitHubConnectionErrorCode.InvalidResponse,
                        "GitHub returned an unexpected device token response.",
                        oauthError: oauthError);
            }
        }
    }

    public async Task<GitHubConnectionSummary> ValidateConnectionAsync(
        Guid connectionId,
        CancellationToken cancellationToken = default)
    {
        ValidateOptions();
        var credential = await LoadCredentialAsync(connectionId, cancellationToken);
        var user = await GetAuthenticatedUserAsync(credential.AccessToken, cancellationToken);

        await using var operation = await database.OpenWriteAsync(cancellationToken);
        var connection = await operation.Db.GitHubConnections
            .SingleOrDefaultAsync(item => item.Id == connectionId, cancellationToken);
        if (connection is null)
            throw ConnectionNotFound(connectionId);

        if (connection.GitHubUserId != user.Id)
            throw new GitHubConnectionException(
                GitHubConnectionErrorCode.Unauthorized,
                "The saved GitHub credential belongs to a different account. Disconnect it and authorize the intended account.",
                HttpStatusCode.Unauthorized);

        connection.AccountLogin = user.Login;
        connection.LastValidatedAt = DateTime.UtcNow;
        connection.RevokedAt = null;
        connection.UpdatedAt = DateTime.UtcNow;
        await operation.SaveChangesAsync(cancellationToken);
        return ToSummary(connection);
    }

    public async Task<IReadOnlyList<GitHubConnectionSummary>> ListConnectionsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        return await operation.Db.GitHubConnections
            .AsNoTracking()
            .OrderBy(item => item.AccountLogin)
            .ThenBy(item => item.CreatedAt)
            .Select(item => new GitHubConnectionSummary(
                item.Id,
                item.GitHubUserId,
                item.AccountLogin,
                item.Scope,
                item.AccessTokenExpiresAt,
                item.LastValidatedAt,
                item.RevokedAt,
                item.CreatedAt,
                item.UpdatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task DisconnectAsync(
        Guid connectionId,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenWriteAsync(cancellationToken);
        var connection = await operation.Db.GitHubConnections
            .SingleOrDefaultAsync(item => item.Id == connectionId, cancellationToken);
        if (connection is null)
            throw ConnectionNotFound(connectionId);

        var inUse = await operation.Db.ProjectGitRemotes
            .AnyAsync(remote => remote.GitHubConnectionId == connectionId, cancellationToken);
        if (inUse)
            throw new GitHubConnectionException(
                GitHubConnectionErrorCode.ConnectionInUse,
                "This GitHub connection is still assigned to a project remote. Remove or relink those project remotes before disconnecting the account.");

        operation.Db.GitHubConnections.Remove(connection);
        await operation.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GitHubRepositoryInfo>> ListRepositoriesAsync(
        Guid connectionId,
        int pageSize = 100,
        CancellationToken cancellationToken = default)
    {
        ValidateOptions();
        if (pageSize is < 1 or > 100)
            throw InvalidRequest("GitHub repository page size must be between 1 and 100.");

        var credential = await LoadCredentialAsync(connectionId, cancellationToken);
        var repositories = new List<GitHubRepositoryInfo>();
        var page = 1;
        var pagesRead = 0;
        var visitedUris = new HashSet<string>(StringComparer.Ordinal);
        var uri = CreateApiUri("user/repos", new Dictionary<string, string?>
        {
            ["per_page"] = pageSize.ToString(CultureInfo.InvariantCulture),
            ["page"] = page.ToString(CultureInfo.InvariantCulture),
            ["sort"] = "updated",
            ["direction"] = "desc",
        });

        while (uri is not null)
        {
            if (++pagesRead > _options.MaximumRepositoryPages)
                throw InvalidResponse("GitHub returned more repository pages than the configured safety limit.");

            if (!visitedUris.Add(GetCanonicalUri(uri)))
                throw InvalidResponse("GitHub returned a repeated repository pagination link.");

            var currentUri = uri;
            using var response = await SendApiAsync(HttpMethod.Get, uri, credential.AccessToken, null, "list GitHub repositories", cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw CreateApiHttpException(response, "list GitHub repositories");
            var payload = await DeserializeAsync<GitHubRepositoryResponse[]>(response, "GitHub repository list", cancellationToken);
            if (payload is null)
                throw InvalidResponse("GitHub returned an empty repository-list response.");

            foreach (var repository in payload)
                repositories.Add(ToRepositoryInfo(repository));

            var nextUri = GetNextLink(response);
            if (nextUri is null && payload.Length >= pageSize)
            {
                page = GetPageNumber(currentUri) + 1;
                nextUri = CreateApiUri("user/repos", new Dictionary<string, string?>
                {
                    ["per_page"] = pageSize.ToString(CultureInfo.InvariantCulture),
                    ["page"] = page.ToString(CultureInfo.InvariantCulture),
                    ["sort"] = "updated",
                    ["direction"] = "desc",
                });
            }

            uri = nextUri;
        }

        return repositories;
    }

    public async Task<GitHubRepositoryInfo> CreateRepositoryAsync(
        Guid connectionId,
        GitHubRepositoryCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateOptions();
        ArgumentNullException.ThrowIfNull(request);
        ValidateRepositoryName(request.Name);

        var credential = await LoadCredentialAsync(connectionId, cancellationToken);
        var payload = new
        {
            name = request.Name.Trim(),
            description = request.Description,
            @private = request.IsPrivate,
            has_issues = request.HasIssues,
            has_projects = request.HasProjects,
            has_wiki = request.HasWiki,
            auto_init = request.AutoInit,
        };
        using var content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await SendApiAsync(
            HttpMethod.Post,
            CreateApiUri("user/repos"),
            credential.AccessToken,
            content,
            "create GitHub repository",
            cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw CreateApiHttpException(response, "create GitHub repository");
        var repository = await DeserializeAsync<GitHubRepositoryResponse>(response, "created GitHub repository", cancellationToken);
        if (repository is null)
            throw InvalidResponse("GitHub returned an empty repository-creation response.");

        return ToRepositoryInfo(repository);
    }

    public async Task<CredentialsHandler> CreateCredentialsHandlerAsync(
        Guid connectionId,
        CancellationToken cancellationToken = default)
    {
        ValidateOptions();
        var credential = await LoadCredentialAsync(connectionId, cancellationToken);

        // The token is supplied only through libgit2's credential callback. It
        // is never embedded in a clone URL, remote URL, exception, or log.
        return (_, _, types) =>
        {
            if ((types & SupportedCredentialTypes.UsernamePassword) == 0)
                throw new GitHubConnectionException(
                    GitHubConnectionErrorCode.UnsupportedCredentials,
                    "GitHub remote authentication requires username/password credentials from the HTTPS transport.");

            return new UsernamePasswordCredentials
            {
                Username = "x-access-token",
                Password = credential.AccessToken,
            };
        };
    }

    private async Task<GitHubUser> GetAuthenticatedUserAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var response = await SendApiAsync(
            HttpMethod.Get,
            CreateApiUri("user"),
            accessToken,
            null,
            "validate GitHub account",
            cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw CreateApiHttpException(response, "validate GitHub account");
        var payload = await DeserializeAsync<GitHubUserResponse>(response, "GitHub account", cancellationToken);
        if (payload is null || payload.Id <= 0 || string.IsNullOrWhiteSpace(payload.Login))
            throw InvalidResponse("GitHub returned an incomplete authenticated-account response.");

        return new GitHubUser(payload.Id, payload.Login.Trim());
    }

    private async Task<GitHubConnectionSummary> PersistConnectionAsync(
        GitHubUser user,
        GitHubOAuthToken token,
        CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenWriteAsync(cancellationToken);
        var connection = await operation.Db.GitHubConnections
            .SingleOrDefaultAsync(item => item.GitHubUserId == user.Id, cancellationToken);

        var now = DateTime.UtcNow;
        if (connection is null)
        {
            connection = new GitHubConnection
            {
                GitHubUserId = user.Id,
                AccountLogin = user.Login,
                AccessToken = token.AccessToken,
                AccessTokenExpiresAt = token.AccessTokenExpiresAt,
                Scope = token.Scope,
                LastValidatedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            };
            operation.Db.GitHubConnections.Add(connection);
        }
        else
        {
            connection.AccountLogin = user.Login;
            connection.AccessToken = token.AccessToken;
            connection.AccessTokenExpiresAt = token.AccessTokenExpiresAt;
            connection.Scope = token.Scope;
            connection.LastValidatedAt = now;
            connection.RevokedAt = null;
            connection.UpdatedAt = now;
        }

        await operation.SaveChangesAsync(cancellationToken);
        return ToSummary(connection);
    }

    private async Task<GitHubCredential> LoadCredentialAsync(
        Guid connectionId,
        CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        var connection = await operation.Db.GitHubConnections
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == connectionId, cancellationToken);
        if (connection is null)
            throw ConnectionNotFound(connectionId);
        if (connection.RevokedAt is not null)
            throw new GitHubConnectionException(
                GitHubConnectionErrorCode.Unauthorized,
                "This GitHub connection is revoked. Authorize GitHub again before using it.",
                HttpStatusCode.Unauthorized);
        if (string.IsNullOrWhiteSpace(connection.AccessToken))
            throw new GitHubConnectionException(
                GitHubConnectionErrorCode.Unauthorized,
                "This GitHub connection has no access token. Authorize GitHub again before using it.",
                HttpStatusCode.Unauthorized);
        if (connection.AccessTokenExpiresAt is { } expiresAt && expiresAt <= DateTime.UtcNow)
            throw new GitHubConnectionException(
                GitHubConnectionErrorCode.ExpiredToken,
                "This GitHub access token has expired. Authorize GitHub again before using the connection.",
                HttpStatusCode.Unauthorized,
                oauthError: "expired_token");

        return new GitHubCredential(connection.AccessToken);
    }

    private async Task<HttpResponseMessage> SendApiAsync(
        HttpMethod method,
        Uri uri,
        string accessToken,
        HttpContent? content,
        string operation,
        CancellationToken cancellationToken)
    {
        EnsureAllowedApiUri(uri);
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        AddApiHeaders(request, accessToken);
        return await SendAsync(request, operation, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        string operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new GitHubConnectionException(
                GitHubConnectionErrorCode.TransportFailure,
                $"GitHub did not complete the request to {operation}. Retry the operation.",
                innerException: exception);
        }
        catch (HttpRequestException exception)
        {
            throw new GitHubConnectionException(
                GitHubConnectionErrorCode.TransportFailure,
                $"GitHub could not be reached to {operation}. Check the network connection and retry.",
                innerException: exception);
        }
    }

    private static async Task<T?> DeserializeAsync<T>(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
                return default;
            return JsonSerializer.Deserialize<T>(body, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new GitHubConnectionException(
                GitHubConnectionErrorCode.InvalidResponse,
                $"GitHub returned invalid JSON while trying to {operation}.",
                innerException: exception);
        }
        catch (NotSupportedException exception)
        {
            throw new GitHubConnectionException(
                GitHubConnectionErrorCode.InvalidResponse,
                $"GitHub returned an unsupported response while trying to {operation}.",
                innerException: exception);
        }
    }

    private void AddOAuthHeaders(HttpRequestMessage request)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd(_options.UserAgent.Trim());
    }

    private void AddApiHeaders(HttpRequestMessage request, string accessToken)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd(_options.UserAgent.Trim());
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", _options.ApiVersion.Trim());
        // Keep credentials in an HTTP header only. TryAddWithoutValidation
        // avoids an exception whose formatted value could expose a malformed
        // stored token through an outer logger.
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
    }

    private Uri CreateApiUri(string relativePath, IReadOnlyDictionary<string, string?>? query = null)
    {
        var baseUri = _options.ApiBaseUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? _options.ApiBaseUri
            : new Uri(_options.ApiBaseUri.AbsoluteUri + "/", UriKind.Absolute);
        var uri = new Uri(baseUri, relativePath.TrimStart('/'));
        if (query is null || query.Count == 0)
            return uri;

        var builder = new UriBuilder(uri);
        builder.Query = string.Join(
            "&",
            query.Where(item => item.Value is not null)
                .Select(item => $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value!)}"));
        return builder.Uri;
    }

    private Uri? GetNextLink(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values))
            return null;

        foreach (var value in values.SelectMany(item => item.Split(',')))
        {
            if (!value.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase))
                continue;

            var start = value.IndexOf('<');
            var end = value.IndexOf('>', start + 1);
            if (start < 0 || end <= start)
                throw InvalidResponse("GitHub returned a malformed repository pagination link.");

            var rawUri = value[(start + 1)..end];
            if (!Uri.TryCreate(rawUri, UriKind.Absolute, out var next))
            {
                if (!Uri.TryCreate(_options.ApiBaseUri, rawUri, out next))
                    throw InvalidResponse("GitHub returned an invalid repository pagination link.");
            }

            EnsureAllowedApiUri(next);
            return next;
        }

        return null;
    }

    private void EnsureAllowedApiUri(Uri uri)
    {
        if (!IsAllowedApiUri(uri))
            throw InvalidResponse("GitHub returned a repository pagination link outside the configured API base URI.");
    }

    private bool IsAllowedApiUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.IsFile || !string.IsNullOrWhiteSpace(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) || ContainsCredentialQuery(uri))
            return false;

        var configured = _options.ApiBaseUri;
        if (!string.Equals(uri.Scheme, configured.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, configured.Host, StringComparison.OrdinalIgnoreCase) ||
            uri.Port != configured.Port)
            return false;

        var basePath = NormalizeApiPath(configured.AbsolutePath);
        var candidatePath = NormalizeApiPath(uri.AbsolutePath);
        return candidatePath.Equals(basePath, StringComparison.Ordinal) ||
            candidatePath.StartsWith(basePath, StringComparison.Ordinal);
    }

    private static string GetCanonicalUri(Uri uri) =>
        uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped);

    private static int GetPageNumber(Uri uri)
    {
        var pageParameter = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(pair => pair.Length == 2)
            .FirstOrDefault(pair => string.Equals(pair[0], "page", StringComparison.OrdinalIgnoreCase));
        return pageParameter is not null &&
            int.TryParse(Uri.UnescapeDataString(pageParameter[1]), NumberStyles.Integer, CultureInfo.InvariantCulture, out var page) &&
            page > 0
            ? page
            : 1;
    }

    private static string NormalizeApiPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/")
            return "/";

        return path.EndsWith("/", StringComparison.Ordinal) ? path : path + "/";
    }

    private static bool ContainsCredentialQuery(Uri uri)
    {
        foreach (var pair in uri.Query.TrimStart('?')
                     .Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var key = pair.Split('=', 2)[0];
            key = Uri.UnescapeDataString(key).Trim();
            if (key.Equals("access_token", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("refresh_token", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("client_secret", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("password", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("authorization", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("credential", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("token", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsAllowedVerificationUri(Uri uri) =>
        !uri.IsFile && string.IsNullOrWhiteSpace(uri.UserInfo) &&
        (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
         (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && uri.IsLoopback));

    private static GitHubOAuthToken CreateToken(DeviceTokenResponse payload, string defaultScope)
    {
        DateTimeOffset createdAt;
        try
        {
            createdAt = payload.CreatedAt is { } seconds
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : DateTimeOffset.UtcNow;
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new GitHubConnectionException(
                GitHubConnectionErrorCode.InvalidResponse,
                "GitHub returned an invalid token creation timestamp.",
                innerException: exception);
        }
        var accessExpiry = ToExpiry(createdAt, payload.ExpiresIn);
        return new GitHubOAuthToken(
            payload.AccessToken!,
            accessExpiry?.UtcDateTime,
            string.IsNullOrWhiteSpace(payload.Scope) ? defaultScope : payload.Scope.Trim());
    }

    private static DateTimeOffset? ToExpiry(DateTimeOffset createdAt, long? seconds)
    {
        if (seconds is null or <= 0)
            return null;

        try
        {
            return createdAt.AddSeconds(seconds.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw InvalidResponse("GitHub returned an invalid token expiry value.");
        }
    }

    private static GitHubConnectionSummary ToSummary(GitHubConnection connection) => new(
        connection.Id,
        connection.GitHubUserId,
        connection.AccountLogin,
        connection.Scope,
        connection.AccessTokenExpiresAt,
        connection.LastValidatedAt,
        connection.RevokedAt,
        connection.CreatedAt,
        connection.UpdatedAt);

    private static GitHubRepositoryInfo ToRepositoryInfo(GitHubRepositoryResponse repository)
    {
        if (repository.Id <= 0 || string.IsNullOrWhiteSpace(repository.Name))
            throw InvalidResponse("GitHub returned an incomplete repository response.");

        var fullName = string.IsNullOrWhiteSpace(repository.FullName)
            ? repository.Name
            : repository.FullName.Trim();
        var owner = repository.Owner?.Login?.Trim();
        if (string.IsNullOrWhiteSpace(owner))
            owner = fullName.Split('/', 2)[0].Trim();
        if (string.IsNullOrWhiteSpace(owner))
            throw InvalidResponse("GitHub returned a repository without an owner.");

        return new GitHubRepositoryInfo(
            repository.Id,
            owner,
            repository.Name.Trim(),
            fullName,
            repository.IsPrivate,
            repository.HtmlUrl,
            repository.CloneUrl,
            repository.SshUrl,
            repository.DefaultBranch);
    }

    private static void ValidateRepositoryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100 ||
            name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal))
            throw InvalidRequest("GitHub repository names must be 1–100 characters and may not contain path separators.");
    }

    private static GitHubConnectionException CreateOAuthHttpException(
        HttpResponseMessage response,
        string operation,
        string? oauthError)
    {
        var code = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => GitHubConnectionErrorCode.Unauthorized,
            HttpStatusCode.Forbidden => GitHubConnectionErrorCode.Forbidden,
            HttpStatusCode.TooManyRequests => GitHubConnectionErrorCode.RateLimited,
            _ => GitHubConnectionErrorCode.InvalidResponse,
        };
        return new GitHubConnectionException(
            code,
            $"GitHub rejected the request to {operation} ({(int)response.StatusCode}). Verify the OAuth app configuration and retry.",
            response.StatusCode,
            GetRetryAfter(response),
            oauthError: oauthError);
    }

    private static GitHubConnectionException CreateApiHttpException(HttpResponseMessage response, string operation)
    {
        var code = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => GitHubConnectionErrorCode.Unauthorized,
            HttpStatusCode.Forbidden => response.StatusCode == HttpStatusCode.Forbidden && GetRetryAfter(response) is not null
                ? GitHubConnectionErrorCode.RateLimited
                : GitHubConnectionErrorCode.Forbidden,
            HttpStatusCode.NotFound => GitHubConnectionErrorCode.NotFound,
            HttpStatusCode.Conflict => GitHubConnectionErrorCode.Conflict,
            HttpStatusCode.TooManyRequests => GitHubConnectionErrorCode.RateLimited,
            _ => GitHubConnectionErrorCode.InvalidResponse,
        };
        var action = code switch
        {
            GitHubConnectionErrorCode.Unauthorized => "Authorize GitHub again",
            GitHubConnectionErrorCode.Forbidden => "check the GitHub account permissions",
            GitHubConnectionErrorCode.RateLimited => "wait for the rate limit to reset",
            GitHubConnectionErrorCode.NotFound => "verify the GitHub account and repository access",
            _ => "retry the operation",
        };
        return new GitHubConnectionException(
            code,
            $"GitHub could not {operation} ({(int)response.StatusCode}); {action}.",
            response.StatusCode,
            GetRetryAfter(response));
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
            return delta;
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values) &&
            long.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds))
        {
            try
            {
                var deltaFromReset = DateTimeOffset.FromUnixTimeSeconds(unixSeconds) - DateTimeOffset.UtcNow;
                if (deltaFromReset > TimeSpan.Zero)
                    return deltaFromReset;
            }
            catch (ArgumentOutOfRangeException)
            {
                // A malformed rate-limit header should not mask the typed
                // HTTP failure that the caller needs to handle.
            }
        }

        return null;
    }

    private void ValidateOptions() => _options.Validate();

    private static GitHubConnectionException InvalidRequest(string message) => new(
        GitHubConnectionErrorCode.InvalidRequest,
        message);

    private static GitHubConnectionException InvalidResponse(string message) => new(
        GitHubConnectionErrorCode.InvalidResponse,
        message);

    private static GitHubConnectionException ConnectionNotFound(Guid connectionId) => new(
        GitHubConnectionErrorCode.ConnectionNotFound,
        $"GitHub connection '{connectionId}' was not found. Authorize GitHub before using it.");

    private sealed record GitHubUser(long Id, string Login);
    private sealed record GitHubCredential(string AccessToken);
    private sealed record GitHubOAuthToken(
        string AccessToken,
        DateTime? AccessTokenExpiresAt,
        string? Scope);

    private sealed record DeviceCodeResponse(
        [property: JsonPropertyName("device_code")] string? DeviceCode,
        [property: JsonPropertyName("user_code")] string? UserCode,
        [property: JsonPropertyName("verification_uri")] string? VerificationUri,
        [property: JsonPropertyName("verification_uri_complete")] string? VerificationUriComplete,
        [property: JsonPropertyName("expires_in")] long ExpiresIn,
        [property: JsonPropertyName("interval")] long Interval,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("error_description")] string? ErrorDescription);

    private sealed record DeviceTokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("token_type")] string? TokenType,
        [property: JsonPropertyName("scope")] string? Scope,
        [property: JsonPropertyName("expires_in")] long? ExpiresIn,
        [property: JsonPropertyName("created_at")] long? CreatedAt,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("error_description")] string? ErrorDescription);

    private sealed record GitHubUserResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("login")] string? Login);

    private sealed record GitHubRepositoryResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("full_name")] string? FullName,
        [property: JsonPropertyName("private")] bool IsPrivate,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("clone_url")] string? CloneUrl,
        [property: JsonPropertyName("ssh_url")] string? SshUrl,
        [property: JsonPropertyName("default_branch")] string? DefaultBranch,
        [property: JsonPropertyName("owner")] GitHubRepositoryOwnerResponse? Owner);

    private sealed record GitHubRepositoryOwnerResponse(
        [property: JsonPropertyName("login")] string? Login);
}
