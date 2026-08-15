using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Persistence.Repositories;

namespace Lorekeeper.Llm;

/// <summary>
/// OAuth2 PKCE flow for OpenAI's Codex Responses API. Reuses the official Codex CLI
/// client_id, so the configured redirect URI must stay accepted by that client.
/// The default remains <c>http://localhost:1455/auth/callback</c> to match local
/// dev and the desktop shell's default port.
/// </summary>
public class CodexAuthService(
IAppDatabaseOperationFactory database, IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<CodexAuthService> logger) : ICodexAuthService
{
    private const string AuthEndpoint = "https://auth.openai.com/oauth/authorize";
    private const string TokenEndpoint = "https://auth.openai.com/oauth/token";
    private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string Scope = "openid profile email offline_access";
    private readonly string _redirectUri = configuration["Auth:Codex:RedirectUri"]
        ?? throw new InvalidOperationException("Auth:Codex:RedirectUri must be configured for Codex OAuth.");

    // In-process PKCE state. Single-user POC; if Lorekeeper ever runs multi-instance
    // this needs to move to a shared cache.
    private static readonly ConcurrentDictionary<string, PkceState> _pendingFlows = new();
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> _providerLocks = new();
    private static readonly ConcurrentDictionary<RejectedRefreshToken, byte> _rejectedRefreshTokens = new();

    private sealed record PkceState(int ProviderId, string CodeVerifier, DateTime CreatedAt);
    private sealed record RejectedRefreshToken(
        int ProviderId,
        int TokenId,
        DateTime CreatedAt,
        DateTime ExpiresAt,
        string RefreshTokenFingerprint)
    {
        public static RejectedRefreshToken From(OAuthToken token) =>
            new(
                token.ProviderId,
                token.Id,
                token.CreatedAt,
                token.ExpiresAt,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token.RefreshToken!))));
    }

    public (string AuthorizationUrl, string State) StartPkceFlow(int providerId)
    {
        var codeVerifier = GenerateCodeVerifier();
        var codeChallenge = GenerateCodeChallenge(codeVerifier);
        var state = Guid.NewGuid().ToString("N");

        _pendingFlows[state] = new PkceState(providerId, codeVerifier, DateTime.UtcNow);

        var url = $"{AuthEndpoint}?" +
            $"response_type=code&" +
            $"client_id={Uri.EscapeDataString(ClientId)}&" +
            $"redirect_uri={Uri.EscapeDataString(_redirectUri)}&" +
            $"scope={Uri.EscapeDataString(Scope)}&" +
            $"state={Uri.EscapeDataString(state)}&" +
            $"code_challenge={Uri.EscapeDataString(codeChallenge)}&" +
            $"code_challenge_method=S256&" +
            $"id_token_add_organizations=true&" +
            $"codex_cli_simplified_flow=true&" +
            $"originator=pi";

        return (url, state);
    }

    public async Task<int> HandleCallbackAsync(string code, string state, CancellationToken cancellationToken = default)
    {
        if (!_pendingFlows.TryRemove(state, out var pkce))
            throw new InvalidOperationException("Invalid or expired OAuth state.");

        if (DateTime.UtcNow - pkce.CreatedAt > TimeSpan.FromMinutes(10))
            throw new InvalidOperationException("OAuth flow has expired.");

        var providerLock = _providerLocks.GetOrAdd(pkce.ProviderId, _ => new SemaphoreSlim(1, 1));
        await providerLock.WaitAsync(cancellationToken);
        try
        {
            var client = httpClientFactory.CreateClient();

            var tokenRequest = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = ClientId,
                ["code"] = code,
                ["redirect_uri"] = _redirectUri,
                ["code_verifier"] = pkce.CodeVerifier
            };

            using var response = await client.PostAsync(TokenEndpoint, new FormUrlEncodedContent(tokenRequest), cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

            var accessToken = json.GetProperty("access_token").GetString()!;
            var expiresIn = json.GetProperty("expires_in").GetInt32();
            var refreshToken = json.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
            var scope = json.TryGetProperty("scope", out var sc) ? sc.GetString() : null;

            await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
            var tokens = databaseOperation.Repositories.OAuthTokens;
            await tokens.ReplaceForProviderAsync(pkce.ProviderId, new OAuthToken
            {
                ProviderId = pkce.ProviderId,
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                ExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn),
                Scope = scope
            }, cancellationToken);
            await databaseOperation.SaveChangesAsync(cancellationToken);
            ClearRejectedRefreshTokens(pkce.ProviderId);

            return pkce.ProviderId;
        }
        finally
        {
            providerLock.Release();
        }
    }

    public async Task<string?> GetValidTokenAsync(int providerId, CancellationToken cancellationToken = default)
    {
        var providerLock = _providerLocks.GetOrAdd(providerId, _ => new SemaphoreSlim(1, 1));
        await providerLock.WaitAsync(cancellationToken);
        try
        {
            OAuthToken? token;
            await using (var readOperation = await database.OpenReadAsync(cancellationToken))
            {
                token = await readOperation.Repositories.OAuthTokens.GetLatestForProviderAsync(providerId, cancellationToken);
            }
            if (token is null)
                return null;
            if (token.ExpiresAt > DateTime.UtcNow)
                return token.AccessToken;
            if (token.RefreshToken is null)
                return null;

            var rejected = RejectedRefreshToken.From(token);
            ClearRejectedRefreshTokens(providerId, rejected);
            if (_rejectedRefreshTokens.ContainsKey(rejected))
                return null;

            var refreshed = await RefreshTokenAsync(token, cancellationToken);
            if (refreshed is null)
            {
                _rejectedRefreshTokens.TryAdd(rejected, 0);
                return null;
            }

            _rejectedRefreshTokens.TryRemove(rejected, out _);
            return refreshed.ExpiresAt > DateTime.UtcNow ? refreshed.AccessToken : null;
        }
        finally
        {
            providerLock.Release();
        }
    }

    public async Task RevokeTokenAsync(int providerId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var tokens = databaseOperation.Repositories.OAuthTokens;
        var providerLock = _providerLocks.GetOrAdd(providerId, _ => new SemaphoreSlim(1, 1));
        await providerLock.WaitAsync(cancellationToken);
        try
        {
            await tokens.DeleteForProviderAsync(providerId, cancellationToken);
            await databaseOperation.SaveChangesAsync(cancellationToken);
            ClearRejectedRefreshTokens(providerId);
        }
        finally
        {
            providerLock.Release();
        }
    }

    private async Task<OAuthToken?> RefreshTokenAsync(OAuthToken token, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();

        var refreshRequest = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = ClientId,
            ["refresh_token"] = token.RefreshToken!
        };

        using var response = await client.PostAsync(TokenEndpoint, new FormUrlEncodedContent(refreshRequest), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorCode = await ReadOAuthErrorCodeAsync(response, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                || (response.StatusCode == System.Net.HttpStatusCode.BadRequest
                    && errorCode is "invalid_grant" or "invalid_token"))
            {
                logger.LogWarning(
                    "Codex OAuth refresh was rejected for provider {ProviderId}; reconnect is required. StatusCode={StatusCode}, OAuthError={OAuthError}",
                    token.ProviderId,
                    (int)response.StatusCode,
                    errorCode ?? "unavailable");
                return null;
            }
        }
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        var refreshToken = token.RefreshToken;
        if (json.TryGetProperty("refresh_token", out var rt)
            && rt.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(rt.GetString()))
        {
            refreshToken = rt.GetString();
        }

        var refreshed = new OAuthToken
        {
            ProviderId = token.ProviderId,
            AccessToken = json.GetProperty("access_token").GetString()!,
            RefreshToken = refreshToken,
            ExpiresAt = DateTime.UtcNow.AddSeconds(json.GetProperty("expires_in").GetInt32()),
            Scope = json.TryGetProperty("scope", out var scope)
                && scope.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(scope.GetString())
                    ? scope.GetString()
                    : token.Scope,
            CreatedAt = DateTime.UtcNow,
        };
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        var tokens = databaseOperation.Repositories.OAuthTokens;
        await tokens.ReplaceForProviderAsync(token.ProviderId, refreshed, cancellationToken);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        return refreshed;
    }

    private static void ClearRejectedRefreshTokens(
        int providerId,
        RejectedRefreshToken? except = null)
    {
        foreach (var rejected in _rejectedRefreshTokens.Keys.Where(item =>
            item.ProviderId == providerId && item != except))
        {
            _rejectedRefreshTokens.TryRemove(rejected, out _);
        }
    }

    private static async Task<string?> ReadOAuthErrorCodeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                    ? error.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string GenerateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string GenerateCodeChallenge(string codeVerifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Convert.ToBase64String(hash)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
