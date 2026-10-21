using Lorekeeper.Models;
using Lorekeeper.Persistence;

namespace Lorekeeper.Authorization;

public sealed class OpenAiAccountTokenService(
    IAppDatabaseOperationFactory database,
    IEnumerable<IOpenAiAuthorizationProtocol> protocols,
    OpenAiAccountOperationCoordinator coordinator,
    TimeProvider timeProvider,
    ILogger<OpenAiAccountTokenService> logger) : IOpenAiAccountTokenService
{
    public Task<OpenAiAccountAccess?> GetValidAccessAsync(
        int accountId,
        CancellationToken cancellationToken = default) =>
        coordinator.RunAsync(accountId, token => GetValidAccessCoreAsync(accountId, token), cancellationToken);

    public Task<string?> DisconnectAsync(
        int accountId,
        CancellationToken cancellationToken = default) =>
        coordinator.RunAsync(accountId, token => SignOutAsync(accountId, forgetRegistration: false, token), cancellationToken);

    public Task<string?> ForgetRegistrationAsync(
        int accountId,
        CancellationToken cancellationToken = default) =>
        coordinator.RunAsync(accountId, token => SignOutAsync(accountId, forgetRegistration: true, token), cancellationToken);

    private async Task<string?> SignOutAsync(int accountId, bool forgetRegistration, CancellationToken cancellationToken)
    {
        OpenAiAccount? account;
        OAuthToken? savedToken;
        await using (var read = await database.OpenReadAsync(cancellationToken))
        {
            account = await read.Repositories.OpenAiAccounts.GetByIdAsync(accountId, cancellationToken);
            savedToken = await read.Repositories.OAuthTokens.GetLatestForAccountAsync(accountId, cancellationToken);
        }

        if (account is null)
            return null;

        var warning = savedToken is null
            ? null
            : await OpenAiAuthorizationProtocol.For(protocols, account.Kind).RevokeAsync(account, savedToken, cancellationToken);

        await using var operation = await database.OpenWriteAsync(cancellationToken);
        var persisted = await operation.Repositories.OpenAiAccounts.GetByIdAsync(accountId, cancellationToken);
        if (persisted is null)
            return warning;

        await operation.Repositories.OAuthTokens.DeleteForAccountAsync(accountId, cancellationToken);
        // A ChatGPT plan registration stays bound to its verified identity across
        // sign-out so the next sign-in reuses it; Codex identity comes from each login.
        if (persisted.Kind == OpenAiAccountKind.Codex || forgetRegistration)
            persisted.ExternalAccountId = null;
        if (forgetRegistration)
        {
            persisted.IssuedClientId = null;
            persisted.Email = null;
            persisted.PlanIneligibleAt = null;
            persisted.WelcomeAcknowledgedAt = null;
        }

        persisted.PlanUsageGranted = false;
        persisted.RequiresReauthenticationAt = null;
        persisted.LastAuthenticationError = null;
        persisted.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        operation.Repositories.OpenAiAccounts.Update(persisted);
        await operation.SaveChangesAsync(cancellationToken);
        return warning;
    }

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

        // Plan inference is only authorized by an explicit plan-usage grant.
        if (account.Kind == OpenAiAccountKind.ChatGptPlan && !account.PlanUsageGranted)
            return null;

        if (string.IsNullOrWhiteSpace(account.ExternalAccountId))
        {
            await MarkReauthenticationRequiredAsync(accountId, OpenAiAuthorizationProtocol.ReauthenticationMessage, cancellationToken);
            return null;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (savedToken.ExpiresAt > now)
            return new OpenAiAccountAccess(accountId, account.Kind, account.ExternalAccountId, savedToken.AccessToken);

        if (string.IsNullOrWhiteSpace(savedToken.RefreshToken))
        {
            await MarkReauthenticationRequiredAsync(accountId, OpenAiAuthorizationProtocol.ReauthenticationMessage, cancellationToken);
            return null;
        }

        var protocol = OpenAiAuthorizationProtocol.For(protocols, account.Kind);
        var outcome = await protocol.RefreshAsync(account, savedToken.RefreshToken, cancellationToken);
        if (outcome.TerminalMessage is { } terminalMessage)
        {
            logger.LogWarning("OpenAI refresh was rejected for account {AccountId}; reconnect is required.", accountId);
            await MarkReauthenticationRequiredAsync(accountId, terminalMessage, cancellationToken);
            return null;
        }

        if (outcome.Tokens is not { } refreshed)
        {
            throw new HttpRequestException(
                "OpenAI authorization could not be refreshed. The saved credentials were retained; try again.",
                null,
                outcome.TransientStatus);
        }

        if (!protocol.IsSameIdentity(account, refreshed))
        {
            await MarkReauthenticationRequiredAsync(accountId, OpenAiAuthorizationProtocol.ReauthenticationMessage, cancellationToken);
            return null;
        }

        var planUsageGranted = account.PlanUsageGranted;
        if (account.Kind == OpenAiAccountKind.ChatGptPlan && refreshed.Scope is not null)
            planUsageGranted = ChatGptPlanAuthorizationProtocol.GrantsPlanUsage(refreshed.Scope);

        await using (var operation = await database.OpenWriteAsync(cancellationToken))
        {
            var persistedAccount = await operation.Repositories.OpenAiAccounts.GetByIdAsync(accountId, cancellationToken)
                ?? throw new InvalidOperationException("The OpenAI account no longer exists.");
            // Access token, expiry, scope, and the rotating refresh token are replaced together.
            await operation.Repositories.OAuthTokens.ReplaceForAccountAsync(
                accountId,
                ToOAuthToken(
                    accountId,
                    refreshed with
                    {
                        Scope = refreshed.Scope ?? savedToken.Scope,
                        IdToken = refreshed.IdToken ?? savedToken.IdToken,
                    }),
                cancellationToken);
            persistedAccount.PlanUsageGranted = planUsageGranted;
            persistedAccount.RequiresReauthenticationAt = null;
            persistedAccount.LastAuthenticationError = null;
            persistedAccount.UpdatedAt = now;
            operation.Repositories.OpenAiAccounts.Update(persistedAccount);
            await operation.SaveChangesAsync(cancellationToken);
        }

        if (account.Kind == OpenAiAccountKind.ChatGptPlan && !planUsageGranted)
            return null;

        return new OpenAiAccountAccess(accountId, account.Kind, account.ExternalAccountId, refreshed.AccessToken);
    }

    internal OAuthToken ToOAuthToken(int accountId, OpenAiTokenResponse response)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return new OAuthToken
        {
            OpenAiAccountId = accountId,
            AccessToken = response.AccessToken,
            RefreshToken = response.RefreshToken,
            IdToken = response.IdToken,
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
}

public sealed record OpenAiTokenResponse(
    string AccessToken,
    string? RefreshToken,
    int ExpiresInSeconds,
    string? Scope,
    string? IdToken);
