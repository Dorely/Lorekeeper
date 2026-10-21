using Lorekeeper.Models;
using Lorekeeper.Persistence;

namespace Lorekeeper.Authorization;

public sealed class OpenAiAccountAuthorizationService(
    IAppDatabaseOperationFactory database,
    IOpenAiCallbackOriginValidator callbackOriginValidator,
    IExternalAuthorizationLauncher externalLauncher,
    IEnumerable<IOpenAiAuthorizationProtocol> protocols,
    OpenAiAuthorizationFlowRegistry flowRegistry,
    OpenAiAccountOperationCoordinator coordinator,
    OpenAiAccountTokenService tokenService,
    TimeProvider timeProvider,
    ILogger<OpenAiAccountAuthorizationService> logger) : IOpenAiAccountAuthorizationService
{
    public async Task<OpenAiAuthorizationStartResult> StartAsync(
        int accountId,
        CancellationToken cancellationToken = default)
    {
        var flow = await coordinator.RunAsync(
            accountId,
            async token =>
            {
                await using var operation = await database.OpenWriteAsync(token);
                var account = await operation.Repositories.OpenAiAccounts.GetByIdAsync(accountId, token)
                    ?? throw new InvalidOperationException("The OpenAI account was not found.");
                var callback = callbackOriginValidator.Validate(account.Kind);
                if (!callback.IsValid || callback.RedirectUri is null)
                    throw new InvalidOperationException(callback.Message);

                if (account.Kind == OpenAiAccountKind.ChatGptPlan && string.IsNullOrWhiteSpace(account.HostId))
                {
                    account.HostId = $"urn:uuid:{Guid.NewGuid()}";
                    account.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
                    operation.Repositories.OpenAiAccounts.Update(account);
                    await operation.SaveChangesAsync(token);
                }

                var idTokenHint = account.Kind == OpenAiAccountKind.ChatGptPlan
                    ? (await operation.Repositories.OAuthTokens.GetLatestForAccountAsync(accountId, token))?.IdToken
                    : null;
                var protocol = OpenAiAuthorizationProtocol.For(protocols, account.Kind);
                return flowRegistry.Start(
                    accountId,
                    callback.RedirectUri,
                    attempt => protocol.BuildAuthorizationTarget(account, idTokenHint, attempt));
            },
            cancellationToken);
        try
        {
            var launch = await externalLauncher.LaunchAsync(flow.AuthorizationTarget, cancellationToken);
            return new OpenAiAuthorizationStartResult(flow, launch);
        }
        catch
        {
            await coordinator.RunAsync(
                accountId,
                _ => Task.FromResult(flowRegistry.Cancel(
                    accountId,
                    "The external authorization page could not be opened.",
                    flow.FlowId)),
                CancellationToken.None);
            throw;
        }
    }

    public OpenAiAuthorizationFlowSnapshot? GetFlow(int accountId) => flowRegistry.Get(accountId);

    public Task<OpenAiAuthorizationFlowSnapshot?> CancelAsync(
        int accountId,
        CancellationToken cancellationToken = default) =>
        coordinator.RunAsync(
            accountId,
            _ => Task.FromResult(flowRegistry.Cancel(accountId)),
            cancellationToken);

    public async Task<OpenAiAuthorizationCallbackResult> HandleCallbackAsync(
        OpenAiAuthorizationCallback callback,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(callback.State))
            return InvalidCallback();

        // A denial is matched to its attempt by state and never exchanges a code.
        if (!string.IsNullOrWhiteSpace(callback.Error))
        {
            var denied = flowRegistry.Claim(callback.State, DenialMessage(callback.Error));
            return denied is null
                ? StaleCallback()
                : new OpenAiAuthorizationCallbackResult(
                    OpenAiAuthorizationFlowStatus.Denied,
                    "OpenAI authorization was denied. Return to Lorekeeper to try again.");
        }

        var claim = flowRegistry.Claim(callback.State);
        if (claim is null)
            return StaleCallback();
        if (string.IsNullOrWhiteSpace(callback.Code))
        {
            flowRegistry.Complete(
                claim,
                OpenAiAuthorizationFlowStatus.Failed,
                "OpenAI did not return an authorization code.");
            return InvalidCallback();
        }

        try
        {
            OpenAiAccount account;
            await using (var read = await database.OpenReadAsync(cancellationToken))
            {
                account = await read.Repositories.OpenAiAccounts.GetByIdAsync(claim.AccountId, cancellationToken)
                    ?? throw new OpenAiAuthorizationException("The OpenAI connection no longer exists.");
            }

            var protocol = OpenAiAuthorizationProtocol.For(protocols, account.Kind);
            var issuedClientId = protocol.ResolveIssuedClientId(account, callback);
            if (issuedClientId is not null && account.IssuedClientId is null)
            {
                // Retain a new registration before the exchange so a retry reuses it.
                await coordinator.RunAsync(
                    claim.AccountId,
                    async token => await RetainIssuedClientIdAsync(claim.AccountId, issuedClientId, token),
                    cancellationToken);
                account.IssuedClientId = issuedClientId;
            }

            var grant = await protocol.ExchangeAsync(account, issuedClientId, callback, claim.Attempt, cancellationToken);
            var committed = await coordinator.RunAsync(
                claim.AccountId,
                async token =>
                {
                    if (!flowRegistry.TryBeginCredentialCommit(claim))
                        return false;

                    await using var operation = await database.OpenWriteAsync(token);
                    var persisted = await operation.Repositories.OpenAiAccounts.GetByIdAsync(claim.AccountId, token);
                    if (persisted is null
                        || !string.Equals(persisted.IssuedClientId, account.IssuedClientId, StringComparison.Ordinal))
                    {
                        flowRegistry.Complete(
                            claim,
                            OpenAiAuthorizationFlowStatus.Failed,
                            "The OpenAI connection changed during sign-in. Start a new connection attempt.");
                        return false;
                    }

                    await operation.Repositories.OAuthTokens.ReplaceForAccountAsync(
                        claim.AccountId,
                        tokenService.ToOAuthToken(claim.AccountId, grant.Tokens),
                        token);
                    persisted.ExternalAccountId = grant.ExternalAccountId;
                    if (persisted.Kind == OpenAiAccountKind.ChatGptPlan)
                    {
                        persisted.Email = grant.Email ?? persisted.Email;
                        persisted.PlanUsageGranted = grant.PlanUsageGranted;
                        persisted.PlanIneligibleAt = null;
                    }

                    persisted.RequiresReauthenticationAt = null;
                    persisted.LastAuthenticationError = null;
                    persisted.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
                    operation.Repositories.OpenAiAccounts.Update(persisted);
                    await operation.SaveChangesAsync(token);
                    return flowRegistry.Complete(
                        claim,
                        OpenAiAuthorizationFlowStatus.Succeeded,
                        SuccessMessage(persisted)) is not null;
                },
                cancellationToken);

            return committed
                ? new OpenAiAuthorizationCallbackResult(
                    OpenAiAuthorizationFlowStatus.Succeeded,
                    account.Kind == OpenAiAccountKind.ChatGptPlan && !grant.PlanUsageGranted
                        ? "Signed in, but ChatGPT plan usage wasn't granted. Return to Lorekeeper to reconnect and allow it."
                        : "OpenAI account connected. Return to Lorekeeper.")
                : StaleCallback();
        }
        catch (OperationCanceledException)
        {
            flowRegistry.Complete(
                claim,
                OpenAiAuthorizationFlowStatus.Failed,
                "OpenAI authorization was interrupted. Start a new connection attempt.");
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "OpenAI authorization callback failed for account {AccountId}: {ErrorType}",
                claim.AccountId,
                exception.GetType().Name);
            var message = exception is OpenAiAuthorizationException
                ? exception.Message
                : "OpenAI authorization could not be completed. Try again from Lorekeeper.";
            flowRegistry.Complete(claim, OpenAiAuthorizationFlowStatus.Failed, message);
            return new OpenAiAuthorizationCallbackResult(
                OpenAiAuthorizationFlowStatus.Failed,
                $"{message} Return to Lorekeeper.");
        }
    }

    private async Task<bool> RetainIssuedClientIdAsync(int accountId, string issuedClientId, CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenWriteAsync(cancellationToken);
        var account = await operation.Repositories.OpenAiAccounts.GetByIdAsync(accountId, cancellationToken)
            ?? throw new OpenAiAuthorizationException("The OpenAI connection no longer exists.");
        if (account.IssuedClientId is not null)
        {
            if (!string.Equals(account.IssuedClientId, issuedClientId, StringComparison.Ordinal))
                throw new OpenAiAuthorizationException("ChatGPT returned a different app registration than this connection uses.");
            return false;
        }

        account.IssuedClientId = issuedClientId;
        account.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        operation.Repositories.OpenAiAccounts.Update(account);
        await operation.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static string SuccessMessage(OpenAiAccount account) =>
        account.Kind == OpenAiAccountKind.ChatGptPlan && !account.PlanUsageGranted
            ? "Signed in, but ChatGPT plan usage wasn't granted. Reconnect and allow ChatGPT plan usage."
            : "OpenAI account connected.";

    private static string DenialMessage(string error) =>
        error.Equals("access_denied", StringComparison.OrdinalIgnoreCase)
            ? "OpenAI authorization was denied."
            : "OpenAI authorization was not completed.";

    private static OpenAiAuthorizationCallbackResult InvalidCallback() =>
        new(
            OpenAiAuthorizationFlowStatus.Failed,
            "This authorization callback is incomplete. Return to Lorekeeper and start again.");

    private static OpenAiAuthorizationCallbackResult StaleCallback() =>
        new(
            OpenAiAuthorizationFlowStatus.Canceled,
            "This authorization attempt is no longer active. Return to Lorekeeper.");
}
