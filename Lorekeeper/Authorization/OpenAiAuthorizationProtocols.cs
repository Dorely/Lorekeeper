using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lorekeeper.Models;
using Microsoft.AspNetCore.WebUtilities;

namespace Lorekeeper.Authorization;

/// <summary>An authorization failure whose message is safe and meaningful to show the user.</summary>
public sealed class OpenAiAuthorizationException(string message) : Exception(message);

/// <summary>Per-attempt secrets and the exact redirect URI kept until the callback completes.</summary>
public sealed record OpenAiAuthorizationAttempt(
    string State,
    string CodeVerifier,
    string Nonce,
    Uri RedirectUri);

/// <summary>Query values OpenAI returns to the loopback callback.</summary>
public sealed record OpenAiAuthorizationCallback(
    string? Code,
    string? State,
    string? Error,
    string? ErrorDescription,
    string? ClientId,
    string? Scope);

/// <summary>A verified exchange result ready to be committed to the account.</summary>
public sealed record OpenAiAuthorizationGrant(
    OpenAiTokenResponse Tokens,
    string ExternalAccountId,
    string? Email,
    bool PlanUsageGranted);

/// <summary>A refresh outcome: refreshed tokens, a terminal rejection message, or a transient failure (both null).</summary>
public sealed record OpenAiRefreshOutcome(
    OpenAiTokenResponse? Tokens,
    string? TerminalMessage,
    HttpStatusCode? TransientStatus);

/// <summary>
/// The protocol-specific half of OpenAI account authorization. The services own
/// flow state, serialization, and persistence; a protocol owns endpoints, wire
/// fields, identity verification, and refresh/revocation semantics.
/// </summary>
public interface IOpenAiAuthorizationProtocol
{
    OpenAiAccountKind Kind { get; }

    Uri BuildAuthorizationTarget(OpenAiAccount account, string? idTokenHint, OpenAiAuthorizationAttempt attempt);

    /// <summary>
    /// Returns the client ID this callback must be exchanged with, or throws when the
    /// callback cannot belong to the account's registration. Null means the protocol
    /// uses a fixed public client.
    /// </summary>
    string? ResolveIssuedClientId(OpenAiAccount account, OpenAiAuthorizationCallback callback);

    Task<OpenAiAuthorizationGrant> ExchangeAsync(
        OpenAiAccount account,
        string? issuedClientId,
        OpenAiAuthorizationCallback callback,
        OpenAiAuthorizationAttempt attempt,
        CancellationToken cancellationToken);

    Task<OpenAiRefreshOutcome> RefreshAsync(
        OpenAiAccount account,
        string refreshToken,
        CancellationToken cancellationToken);

    /// <summary>True when refreshed tokens still belong to the persisted identity.</summary>
    bool IsSameIdentity(OpenAiAccount account, OpenAiTokenResponse refreshed);

    /// <summary>Revokes the session remotely; returns a user-facing warning when revocation is not confirmed.</summary>
    Task<string?> RevokeAsync(OpenAiAccount account, OAuthToken token, CancellationToken cancellationToken);
}

internal static class OpenAiAuthorizationProtocol
{
    public const string ReauthenticationMessage =
        "Reauthentication is required. Reconnect the OpenAI account in Settings > Providers.";

    public static IOpenAiAuthorizationProtocol For(
        IEnumerable<IOpenAiAuthorizationProtocol> protocols,
        OpenAiAccountKind kind) =>
        protocols.SingleOrDefault(protocol => protocol.Kind == kind)
        ?? throw new InvalidOperationException($"No authorization protocol is registered for {kind}.");

    public static string CodeChallenge(string verifier) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    public static async Task<string?> ReadOAuthErrorCodeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                    ? error.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static async Task<OpenAiTokenResponse> ReadTokenResponseAsync(
        HttpResponseMessage response,
        string? fallbackRefreshToken,
        CancellationToken cancellationToken)
    {
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        if (json.ValueKind != JsonValueKind.Object
            || !json.TryGetProperty("access_token", out var accessTokenElement)
            || accessTokenElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(accessTokenElement.GetString())
            || !json.TryGetProperty("expires_in", out var expiresInElement)
            || !expiresInElement.TryGetInt32(out var expiresIn)
            || expiresIn <= 0)
        {
            throw new OpenAiAuthorizationException("OpenAI returned an incomplete authorization response.");
        }

        return new OpenAiTokenResponse(
            accessTokenElement.GetString()!,
            ReadNonEmptyString(json, "refresh_token") ?? fallbackRefreshToken,
            expiresIn,
            ReadNonEmptyString(json, "scope"),
            ReadNonEmptyString(json, "id_token"));
    }

    private static string? ReadNonEmptyString(JsonElement json, string name) =>
        json.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;
}

/// <summary>The Codex CLI public-client login used for the ChatGPT Codex backend.</summary>
internal sealed class CodexAuthorizationProtocol(IHttpClientFactory httpClientFactory) : IOpenAiAuthorizationProtocol
{
    public const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string AuthEndpoint = "https://auth.openai.com/oauth/authorize";
    private const string TokenEndpoint = "https://auth.openai.com/oauth/token";
    private const string Scope = "openid profile email offline_access";

    public OpenAiAccountKind Kind => OpenAiAccountKind.Codex;

    public Uri BuildAuthorizationTarget(OpenAiAccount account, string? idTokenHint, OpenAiAuthorizationAttempt attempt)
    {
        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = attempt.RedirectUri.AbsoluteUri,
            ["scope"] = Scope,
            ["state"] = attempt.State,
            ["code_challenge"] = OpenAiAuthorizationProtocol.CodeChallenge(attempt.CodeVerifier),
            ["code_challenge_method"] = "S256",
            ["id_token_add_organizations"] = "true",
            ["codex_cli_simplified_flow"] = "true",
            ["originator"] = "pi",
        };
        return new Uri(QueryHelpers.AddQueryString(AuthEndpoint, query));
    }

    public string? ResolveIssuedClientId(OpenAiAccount account, OpenAiAuthorizationCallback callback) => null;

    public async Task<OpenAiAuthorizationGrant> ExchangeAsync(
        OpenAiAccount account,
        string? issuedClientId,
        OpenAiAuthorizationCallback callback,
        OpenAiAuthorizationAttempt attempt,
        CancellationToken cancellationToken)
    {
        var request = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["code"] = callback.Code!,
            ["redirect_uri"] = attempt.RedirectUri.AbsoluteUri,
            ["code_verifier"] = attempt.CodeVerifier,
        };
        using var response = await httpClientFactory.CreateClient().PostAsync(
            TokenEndpoint,
            new FormUrlEncodedContent(request),
            cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("OpenAI rejected the authorization exchange.", null, response.StatusCode);

        var tokens = await OpenAiAuthorizationProtocol.ReadTokenResponseAsync(response, null, cancellationToken);
        return new OpenAiAuthorizationGrant(
            tokens with { IdToken = null },
            OpenAiTokenClaims.ReadExternalAccountId(tokens.AccessToken),
            Email: null,
            PlanUsageGranted: false);
    }

    public async Task<OpenAiRefreshOutcome> RefreshAsync(
        OpenAiAccount account,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        var request = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = ClientId,
            ["refresh_token"] = refreshToken,
        };
        using var response = await httpClientFactory.CreateClient().PostAsync(
            TokenEndpoint,
            new FormUrlEncodedContent(request),
            cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            var tokens = await OpenAiAuthorizationProtocol.ReadTokenResponseAsync(response, refreshToken, cancellationToken);
            return new OpenAiRefreshOutcome(tokens with { IdToken = null }, null, null);
        }

        var errorCode = await OpenAiAuthorizationProtocol.ReadOAuthErrorCodeAsync(response, cancellationToken);
        var terminal = response.StatusCode == HttpStatusCode.Unauthorized
            || (response.StatusCode == HttpStatusCode.BadRequest && errorCode is "invalid_grant" or "invalid_token");
        return terminal
            ? new OpenAiRefreshOutcome(null, OpenAiAuthorizationProtocol.ReauthenticationMessage, null)
            : new OpenAiRefreshOutcome(null, null, response.StatusCode);
    }

    public bool IsSameIdentity(OpenAiAccount account, OpenAiTokenResponse refreshed) =>
        string.Equals(
            account.ExternalAccountId,
            OpenAiTokenClaims.ReadExternalAccountId(refreshed.AccessToken),
            StringComparison.Ordinal);

    public Task<string?> RevokeAsync(OpenAiAccount account, OAuthToken token, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);
}

/// <summary>
/// Sign in with ChatGPT plan usage: dynamic client registration, ID-token
/// identity, the plan-usage scope, rotating refresh tokens, and revocation.
/// </summary>
internal sealed class ChatGptPlanAuthorizationProtocol(
    IHttpClientFactory httpClientFactory,
    OpenAiIdTokenValidator idTokenValidator,
    ILogger<ChatGptPlanAuthorizationProtocol> logger) : IOpenAiAuthorizationProtocol
{
    public const string AuthorizeEndpoint = "https://auth.openai.com/api/accounts/authorize";
    public const string TokenEndpoint = "https://auth.openai.com/api/accounts/oauth/token";
    public const string RevocationEndpoint = "https://auth.openai.com/api/accounts/oauth/revoke";
    public const string Resource = "https://api.openai.com/v1";
    public const string PlanUsageScope = "chatgpt.tokens.use.direct";
    public const string RegistrationClientId = "dynamic_agent_client";
    public const string AgentName = "Lorekeeper";
    private const string Scope = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
    private const string RegistrationChangedMessage =
        "The ChatGPT sign-in registration is no longer valid. Forget the ChatGPT plan registration in Settings > Providers and sign in again.";
    private const string RevocationUnconfirmedMessage =
        "Signed out locally, but ChatGPT did not confirm the sign-out. You can also disconnect Lorekeeper in ChatGPT settings.";

    private static readonly string[] TerminalRefreshErrors =
    [
        "invalid_grant",
        "invalid_refresh_token",
        "token_expired",
        "refresh_token_expired",
        "refresh_token_invalidated",
        "refresh_token_reused",
    ];

    public OpenAiAccountKind Kind => OpenAiAccountKind.ChatGptPlan;

    public static bool GrantsPlanUsage(string? scope) =>
        scope?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(PlanUsageScope, StringComparer.Ordinal) == true;

    public Uri BuildAuthorizationTarget(OpenAiAccount account, string? idTokenHint, OpenAiAuthorizationAttempt attempt)
    {
        if (string.IsNullOrWhiteSpace(account.HostId))
            throw new OpenAiAuthorizationException("The ChatGPT plan connection has no host identifier.");

        var returning = !string.IsNullOrWhiteSpace(account.IssuedClientId);
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = returning ? account.IssuedClientId : RegistrationClientId,
            ["ext_agent_host_id"] = account.HostId,
            ["response_type"] = "code",
            ["redirect_uri"] = attempt.RedirectUri.AbsoluteUri,
            ["scope"] = Scope,
            ["resource"] = Resource,
            ["state"] = attempt.State,
            ["nonce"] = attempt.Nonce,
            ["code_challenge"] = OpenAiAuthorizationProtocol.CodeChallenge(attempt.CodeVerifier),
            ["code_challenge_method"] = "S256",
        };
        if (!returning)
            query["agent_name_hint"] = AgentName;
        if (returning && !string.IsNullOrWhiteSpace(idTokenHint))
            query["id_token_hint"] = idTokenHint;
        if (returning && !string.IsNullOrWhiteSpace(account.Email))
            query["login_hint"] = account.Email;

        return new Uri(QueryHelpers.AddQueryString(AuthorizeEndpoint, query));
    }

    public string? ResolveIssuedClientId(OpenAiAccount account, OpenAiAuthorizationCallback callback)
    {
        var returned = string.IsNullOrWhiteSpace(callback.ClientId) ? null : callback.ClientId.Trim();
        if (string.IsNullOrWhiteSpace(account.IssuedClientId))
        {
            if (returned is null || returned == RegistrationClientId)
                throw new OpenAiAuthorizationException("ChatGPT did not complete the app registration. Start the sign-in again.");
            return returned;
        }

        if (returned is not null && !string.Equals(returned, account.IssuedClientId, StringComparison.Ordinal))
            throw new OpenAiAuthorizationException("ChatGPT returned a different app registration than this connection uses.");

        return account.IssuedClientId;
    }

    public async Task<OpenAiAuthorizationGrant> ExchangeAsync(
        OpenAiAccount account,
        string? issuedClientId,
        OpenAiAuthorizationCallback callback,
        OpenAiAuthorizationAttempt attempt,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(issuedClientId))
            throw new OpenAiAuthorizationException("ChatGPT did not complete the app registration. Start the sign-in again.");

        var request = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = issuedClientId,
            ["code"] = callback.Code!,
            ["code_verifier"] = attempt.CodeVerifier,
            ["redirect_uri"] = attempt.RedirectUri.AbsoluteUri,
            ["resource"] = Resource,
        };
        using var response = await httpClientFactory.CreateClient().PostAsync(
            TokenEndpoint,
            new FormUrlEncodedContent(request),
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorCode = await OpenAiAuthorizationProtocol.ReadOAuthErrorCodeAsync(response, cancellationToken);
            logger.LogWarning(
                "ChatGPT plan code exchange was rejected. StatusCode={StatusCode}, OAuthError={OAuthError}",
                (int)response.StatusCode,
                errorCode ?? "unavailable");
            throw new OpenAiAuthorizationException(
                errorCode == "invalid_grant"
                    ? "The ChatGPT sign-in code was no longer valid. Start the sign-in again."
                    : "ChatGPT rejected the sign-in exchange. Start the sign-in again.");
        }

        var tokens = await OpenAiAuthorizationProtocol.ReadTokenResponseAsync(response, null, cancellationToken);
        if (tokens.IdToken is null)
            throw new OpenAiAuthorizationException("ChatGPT did not return an identity token.");

        var identity = await idTokenValidator.ValidateAsync(tokens.IdToken, issuedClientId, attempt.Nonce, cancellationToken);
        if (!string.IsNullOrWhiteSpace(account.ExternalAccountId)
            && !string.Equals(account.ExternalAccountId, identity.Subject, StringComparison.Ordinal))
        {
            throw new OpenAiAuthorizationException(
                "You signed in with a different ChatGPT account than this connection uses. Sign in with the original account, or forget the ChatGPT plan registration first.");
        }

        var grantedScope = tokens.Scope ?? callback.Scope;
        return new OpenAiAuthorizationGrant(
            tokens with { Scope = grantedScope },
            identity.Subject,
            identity.Email,
            GrantsPlanUsage(grantedScope));
    }

    public async Task<OpenAiRefreshOutcome> RefreshAsync(
        OpenAiAccount account,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(account.IssuedClientId))
            return new OpenAiRefreshOutcome(null, OpenAiAuthorizationProtocol.ReauthenticationMessage, null);

        var request = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = account.IssuedClientId,
            ["refresh_token"] = refreshToken,
            ["resource"] = Resource,
        };
        using var response = await httpClientFactory.CreateClient().PostAsync(
            TokenEndpoint,
            new FormUrlEncodedContent(request),
            cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            var tokens = await OpenAiAuthorizationProtocol.ReadTokenResponseAsync(response, refreshToken, cancellationToken);
            return new OpenAiRefreshOutcome(tokens, null, null);
        }

        var errorCode = await OpenAiAuthorizationProtocol.ReadOAuthErrorCodeAsync(response, cancellationToken);
        if (errorCode == "invalid_client")
            return new OpenAiRefreshOutcome(null, RegistrationChangedMessage, null);
        if (response.StatusCode == HttpStatusCode.Unauthorized
            || (errorCode is not null && TerminalRefreshErrors.Contains(errorCode, StringComparer.Ordinal)))
        {
            return new OpenAiRefreshOutcome(null, OpenAiAuthorizationProtocol.ReauthenticationMessage, null);
        }

        return new OpenAiRefreshOutcome(null, null, response.StatusCode);
    }

    // The access token's claims are opaque for this route; the registration's
    // client ID and the retained identity bind refreshed tokens to the account.
    public bool IsSameIdentity(OpenAiAccount account, OpenAiTokenResponse refreshed) => true;

    public async Task<string?> RevokeAsync(OpenAiAccount account, OAuthToken token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token.RefreshToken) || string.IsNullOrWhiteSpace(account.IssuedClientId))
            return RevocationUnconfirmedMessage;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                using var response = await httpClientFactory.CreateClient().PostAsync(
                    RevocationEndpoint,
                    new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["token"] = token.RefreshToken,
                        ["token_type_hint"] = "refresh_token",
                        ["client_id"] = account.IssuedClientId,
                    }),
                    timeout.Token);
                if (response.IsSuccessStatusCode)
                    return null;
                if ((int)response.StatusCode < 500)
                {
                    logger.LogWarning("ChatGPT plan revocation was rejected. StatusCode={StatusCode}", (int)response.StatusCode);
                    return RevocationUnconfirmedMessage;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            catch (HttpRequestException)
            {
            }
        }

        logger.LogWarning("ChatGPT plan revocation could not be confirmed for account {AccountId}", account.Id);
        return RevocationUnconfirmedMessage;
    }
}
