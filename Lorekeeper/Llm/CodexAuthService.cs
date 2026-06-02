using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lorekeeper.Models;
using Lorekeeper.Persistence.Repositories;

namespace Lorekeeper.Llm;

/// <summary>
/// OAuth2 PKCE flow for OpenAI's Codex Responses API. Reuses the official Codex CLI
/// client_id, so the configured redirect URI must stay accepted by that client.
/// The default remains <c>http://localhost:1455/auth/callback</c> to match local
/// dev and the desktop shell's default port.
/// </summary>
public class CodexAuthService(
    IOAuthTokenRepository tokens,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration) : ICodexAuthService
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

    private sealed record PkceState(int ProviderId, string CodeVerifier, DateTime CreatedAt);

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

        var client = httpClientFactory.CreateClient();

        var tokenRequest = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["code"] = code,
            ["redirect_uri"] = _redirectUri,
            ["code_verifier"] = pkce.CodeVerifier
        };

        var response = await client.PostAsync(TokenEndpoint, new FormUrlEncodedContent(tokenRequest), cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        var accessToken = json.GetProperty("access_token").GetString()!;
        var expiresIn = json.GetProperty("expires_in").GetInt32();
        var refreshToken = json.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        var scope = json.TryGetProperty("scope", out var sc) ? sc.GetString() : null;

        await tokens.ReplaceForProviderAsync(pkce.ProviderId, new OAuthToken
        {
            ProviderId = pkce.ProviderId,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn),
            Scope = scope
        }, cancellationToken);
        await tokens.SaveChangesAsync(cancellationToken);

        return pkce.ProviderId;
    }

    public async Task<string?> GetValidTokenAsync(int providerId, CancellationToken cancellationToken = default)
    {
        var token = await tokens.GetLatestForProviderAsync(providerId, cancellationToken);
        if (token is null) return null;

        if (token.ExpiresAt <= DateTime.UtcNow && token.RefreshToken is not null)
            token = await RefreshTokenAsync(token, cancellationToken);

        return token.ExpiresAt > DateTime.UtcNow ? token.AccessToken : null;
    }

    public async Task RevokeTokenAsync(int providerId, CancellationToken cancellationToken = default)
    {
        await tokens.DeleteForProviderAsync(providerId, cancellationToken);
        await tokens.SaveChangesAsync(cancellationToken);
    }

    private async Task<OAuthToken> RefreshTokenAsync(OAuthToken token, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();

        var refreshRequest = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = ClientId,
            ["refresh_token"] = token.RefreshToken!
        };

        var response = await client.PostAsync(TokenEndpoint, new FormUrlEncodedContent(refreshRequest), cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        token.AccessToken = json.GetProperty("access_token").GetString()!;
        token.ExpiresAt = DateTime.UtcNow.AddSeconds(json.GetProperty("expires_in").GetInt32());
        if (json.TryGetProperty("refresh_token", out var rt))
            token.RefreshToken = rt.GetString();
        token.CreatedAt = DateTime.UtcNow;

        await tokens.SaveChangesAsync(cancellationToken);
        return token;
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
