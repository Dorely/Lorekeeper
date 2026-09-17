using System.Net;
using System.Text.Json;
using Lorekeeper.Models;
using Lorekeeper.Persistence;

namespace Lorekeeper.Authorization;

public sealed class OpenAiAccountTokenService(
    IAppDatabaseOperationFactory database,
    IHttpClientFactory httpClientFactory,
    OpenAiAccountOperationCoordinator coordinator,
    TimeProvider timeProvider,
    ILogger<OpenAiAccountTokenService> logger) : IOpenAiAccountTokenService
{
    private const string TokenEndpoint = "https://auth.openai.com/oauth/token";
    internal const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string ReauthenticationMessage =
        "Reauthentication is required. Reconnect the OpenAI account in Settings > Providers.";

    public Task<OpenAiAccountAccess?> GetValidAccessAsync(
        int accountId,
        CancellationToken cancellationToken = default) =>
        coordinator.RunAsync(accountId, token => GetValidAccessCoreAsync(accountId, token), cancellationToken);

    public Task DisconnectAsync(
        int accountId,
        CancellationToken cancellationToken = default) =>
        coordinator.RunAsync(
            accountId,
            async token =>
            {
                await using var operation = await database.OpenWriteAsync(token);
                var account = await operation.Repositories.OpenAiAccounts.GetByIdAsync(accountId, token);
                if (account is null)
                    return;

                await operation.Repositories.OAuthTokens.DeleteForAccountAsync(accountId, token);
                account.ExternalAccountId = null;
                account.RequiresReauthenticationAt = null;
                account.LastAuthenticationError = null;
                account.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
                operation.Repositories.OpenAiAccounts.Update(account);
                await operation.SaveChangesAsync(token);
            },
            cancellationToken);

    public static bool IsTerminalRefreshRejection(HttpStatusCode statusCode, string? errorCode) =>
        statusCode == HttpStatusCode.Unauthorized
        || (statusCode == HttpStatusCode.BadRequest
            && errorCode is "invalid_grant" or "invalid_token");

    private async Task<OpenAiAccountAccess?> GetValidAccessCoreAsync(
        int accountId,
        CancellationToken cancellationToken)
    {
        OpenAiAccount? account;
        OAuthToken? savedToken;
        await using (var operation = await database.OpenReadAsync(cancellationToken))
        {
            account = await operation.Repositories.OpenAiAccounts.GetByIdAsync(accountId, cancellationToken);
            savedToken = await operation.Repositories.OAuthTokens.GetLatestForAccountAsync(accountId, cancellationToken);
        }

        if (account is null || savedToken is null || account.RequiresReauthenticationAt is not null)
            return null;

        if (string.IsNullOrWhiteSpace(account.ExternalAccountId))
        {
            await MarkReauthenticationRequiredAsync(accountId, ReauthenticationMessage, cancellationToken);
            return null;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (savedToken.ExpiresAt > now)
            return new OpenAiAccountAccess(accountId, account.ExternalAccountId, savedToken.AccessToken);

        if (string.IsNullOrWhiteSpace(savedToken.RefreshToken))
        {
            await MarkReauthenticationRequiredAsync(accountId, ReauthenticationMessage, cancellationToken);
            return null;
        }

        using var response = await RefreshAsync(savedToken.RefreshToken, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorCode = await ReadOAuthErrorCodeAsync(response, cancellationToken);
            if (IsTerminalRefreshRejection(response.StatusCode, errorCode))
            {
                logger.LogWarning(
                    "OpenAI refresh was rejected for account {AccountId}; reconnect is required. StatusCode={StatusCode}, OAuthError={OAuthError}",
                    accountId,
                    (int)response.StatusCode,
                    errorCode ?? "unavailable");
                await MarkReauthenticationRequiredAsync(accountId, ReauthenticationMessage, cancellationToken);
                return null;
            }

            throw new HttpRequestException(
                "OpenAI authorization could not be refreshed. The saved credentials were retained; try again.",
                null,
                response.StatusCode);
        }

        var refreshed = await ReadTokenResponseAsync(response, savedToken.RefreshToken, cancellationToken);
        var refreshedExternalAccountId = OpenAiTokenClaims.ReadExternalAccountId(refreshed.AccessToken);
        if (!string.Equals(account.ExternalAccountId, refreshedExternalAccountId, StringComparison.Ordinal))
        {
            await MarkReauthenticationRequiredAsync(accountId, ReauthenticationMessage, cancellationToken);
            return null;
        }

        await using (var operation = await database.OpenWriteAsync(cancellationToken))
        {
            var persistedAccount = await operation.Repositories.OpenAiAccounts.GetByIdAsync(accountId, cancellationToken)
                ?? throw new InvalidOperationException("The OpenAI account no longer exists.");
            await operation.Repositories.OAuthTokens.ReplaceForAccountAsync(
                accountId,
                ToOAuthToken(accountId, refreshed),
                cancellationToken);
            persistedAccount.RequiresReauthenticationAt = null;
            persistedAccount.LastAuthenticationError = null;
            persistedAccount.UpdatedAt = now;
            operation.Repositories.OpenAiAccounts.Update(persistedAccount);
            await operation.SaveChangesAsync(cancellationToken);
        }

        return new OpenAiAccountAccess(accountId, account.ExternalAccountId, refreshed.AccessToken);
    }

    private async Task<HttpResponseMessage> RefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken)
    {
        var request = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = ClientId,
            ["refresh_token"] = refreshToken,
        };
        return await httpClientFactory.CreateClient().PostAsync(
            TokenEndpoint,
            new FormUrlEncodedContent(request),
            cancellationToken);
    }

    internal static async Task<OpenAiTokenResponse> ReadTokenResponseAsync(
        HttpResponseMessage response,
        string? fallbackRefreshToken,
        CancellationToken cancellationToken)
    {
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        if (!json.TryGetProperty("access_token", out var accessTokenElement)
            || accessTokenElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(accessTokenElement.GetString())
            || !json.TryGetProperty("expires_in", out var expiresInElement)
            || !expiresInElement.TryGetInt32(out var expiresIn)
            || expiresIn <= 0)
        {
            throw new InvalidOperationException("OpenAI returned an incomplete authorization response.");
        }

        var refreshToken = json.TryGetProperty("refresh_token", out var refreshTokenElement)
            && refreshTokenElement.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(refreshTokenElement.GetString())
                ? refreshTokenElement.GetString()
                : fallbackRefreshToken;
        var scope = json.TryGetProperty("scope", out var scopeElement)
            && scopeElement.ValueKind == JsonValueKind.String
                ? scopeElement.GetString()
                : null;
        return new OpenAiTokenResponse(accessTokenElement.GetString()!, refreshToken, expiresIn, scope);
    }

    internal OAuthToken ToOAuthToken(int accountId, OpenAiTokenResponse response)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return new OAuthToken
        {
            OpenAiAccountId = accountId,
            AccessToken = response.AccessToken,
            RefreshToken = response.RefreshToken,
            ExpiresAt = now.AddSeconds(response.ExpiresInSeconds),
            Scope = response.Scope,
            CreatedAt = now,
        };
    }

    private async Task MarkReauthenticationRequiredAsync(
        int accountId,
        string message,
        CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenWriteAsync(cancellationToken);
        var account = await operation.Repositories.OpenAiAccounts.GetByIdAsync(accountId, cancellationToken);
        if (account is null)
            return;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        account.RequiresReauthenticationAt = now;
        account.LastAuthenticationError = message;
        account.UpdatedAt = now;
        operation.Repositories.OpenAiAccounts.Update(account);
        await operation.SaveChangesAsync(cancellationToken);
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
}

internal sealed record OpenAiTokenResponse(
    string AccessToken,
    string? RefreshToken,
    int ExpiresInSeconds,
    string? Scope);
