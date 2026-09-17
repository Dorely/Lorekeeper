using System.Security.Cryptography;
using System.Text;
using Lorekeeper.Persistence;

namespace Lorekeeper.Authorization;

public sealed class OpenAiAccountAuthorizationService(
    IAppDatabaseOperationFactory database,
    IHttpClientFactory httpClientFactory,
    IOpenAiCallbackOriginValidator callbackOriginValidator,
    IExternalAuthorizationLauncher externalLauncher,
    OpenAiAuthorizationFlowRegistry flowRegistry,
    OpenAiAccountOperationCoordinator coordinator,
    OpenAiAccountTokenService tokenService,
    TimeProvider timeProvider,
    ILogger<OpenAiAccountAuthorizationService> logger) : IOpenAiAccountAuthorizationService
{
    private const string AuthEndpoint = "https://auth.openai.com/oauth/authorize";
    private const string TokenEndpoint = "https://auth.openai.com/oauth/token";
    private const string Scope = "openid profile email offline_access";

    public async Task<OpenAiAuthorizationStartResult> StartAsync(
        int accountId,
        CancellationToken cancellationToken = default)
    {
        var callback = callbackOriginValidator.Validate();
        if (!callback.IsValid || callback.RedirectUri is null)
            throw new InvalidOperationException(callback.Message);

        await using (var operation = await database.OpenReadAsync(cancellationToken))
        {
            if (await operation.Repositories.OpenAiAccounts.GetByIdAsync(accountId, cancellationToken) is null)
                throw new InvalidOperationException("The OpenAI account was not found.");
        }

        var flow = await coordinator.RunAsync(
            accountId,
            _ => Task.FromResult(flowRegistry.Start(
                accountId,
                (state, verifier) => BuildAuthorizationTarget(callback.RedirectUri, state, verifier))),
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
        string? code,
        string? state,
        string? error,
        string? errorDescription,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(state))
            return InvalidCallback();

        if (!string.IsNullOrWhiteSpace(error))
        {
            var denied = flowRegistry.Claim(state, DenialMessage(error));
            return denied is null
                ? StaleCallback()
                : new OpenAiAuthorizationCallbackResult(
                    OpenAiAuthorizationFlowStatus.Denied,
                    "OpenAI authorization was denied. Return to Lorekeeper to try again.");
        }

        var claim = flowRegistry.Claim(state);
        if (claim is null)
            return StaleCallback();
        if (string.IsNullOrWhiteSpace(code))
        {
            flowRegistry.Complete(
                claim,
                OpenAiAuthorizationFlowStatus.Failed,
                "OpenAI did not return an authorization code.");
            return InvalidCallback();
        }

        try
        {
            var callback = callbackOriginValidator.Validate();
            if (!callback.IsValid || callback.RedirectUri is null)
                throw new InvalidOperationException(callback.Message);

            var exchanged = await ExchangeCodeAsync(code, claim.CodeVerifier, callback.RedirectUri, cancellationToken);
            var externalAccountId = OpenAiTokenClaims.ReadExternalAccountId(exchanged.AccessToken);
            var committed = await coordinator.RunAsync(
                claim.AccountId,
                async token =>
                {
                    if (!flowRegistry.TryBeginCredentialCommit(claim))
                        return false;

                    await using var operation = await database.OpenWriteAsync(token);
                    var account = await operation.Repositories.OpenAiAccounts.GetByIdAsync(claim.AccountId, token);
                    if (account is null)
                        return false;

                    await operation.Repositories.OAuthTokens.ReplaceForAccountAsync(
                        claim.AccountId,
                        tokenService.ToOAuthToken(claim.AccountId, exchanged),
                        token);
                    account.ExternalAccountId = externalAccountId;
                    account.RequiresReauthenticationAt = null;
                    account.LastAuthenticationError = null;
                    account.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
                    operation.Repositories.OpenAiAccounts.Update(account);
                    await operation.SaveChangesAsync(token);
                    return flowRegistry.Complete(
                        claim,
                        OpenAiAuthorizationFlowStatus.Succeeded,
                        "OpenAI account connected.") is not null;
                },
                cancellationToken);

            return committed
                ? new OpenAiAuthorizationCallbackResult(
                    OpenAiAuthorizationFlowStatus.Succeeded,
                    "OpenAI account connected. Return to Lorekeeper.")
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
            logger.LogWarning(exception, "OpenAI authorization callback failed for account {AccountId}", claim.AccountId);
            flowRegistry.Complete(
                claim,
                OpenAiAuthorizationFlowStatus.Failed,
                "OpenAI authorization could not be completed. Try again from Lorekeeper.");
            return new OpenAiAuthorizationCallbackResult(
                OpenAiAuthorizationFlowStatus.Failed,
                "OpenAI authorization could not be completed. Return to Lorekeeper and try again.");
        }
    }

    private async Task<OpenAiTokenResponse> ExchangeCodeAsync(
        string code,
        string verifier,
        Uri redirectUri,
        CancellationToken cancellationToken)
    {
        var request = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = OpenAiAccountTokenService.ClientId,
            ["code"] = code,
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["code_verifier"] = verifier,
        };
        using var response = await httpClientFactory.CreateClient().PostAsync(
            TokenEndpoint,
            new FormUrlEncodedContent(request),
            cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("OpenAI rejected the authorization exchange.", null, response.StatusCode);
        return await OpenAiAccountTokenService.ReadTokenResponseAsync(response, null, cancellationToken);
    }

    private static Uri BuildAuthorizationTarget(Uri redirectUri, string state, string verifier)
    {
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = OpenAiAccountTokenService.ClientId,
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["scope"] = Scope,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["id_token_add_organizations"] = "true",
            ["codex_cli_simplified_flow"] = "true",
            ["originator"] = "pi",
        };
        return new Uri(Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(AuthEndpoint, query));
    }

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
