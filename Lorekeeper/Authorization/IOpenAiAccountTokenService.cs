using Lorekeeper.Models;

namespace Lorekeeper.Authorization;

public sealed record OpenAiAccountAccess(
    int AccountId,
    OpenAiAccountKind Kind,
    string ExternalAccountId,
    string AccessToken);

public interface IOpenAiAccountTokenService
{
    Task<OpenAiAccountAccess?> GetValidAccessAsync(
        int accountId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Refreshes the account after a backend rejected <paramref name="rejectedAccessToken"/>
    /// before its recorded expiry. Returns the saved access unchanged when another
    /// operation already replaced that token, or null when reconnect is required.
    /// </summary>
    Task<OpenAiAccountAccess?> RefreshRejectedAccessAsync(
        int accountId,
        string rejectedAccessToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Signs the account out and deletes its local tokens. Returns a user-facing
    /// warning when remote revocation could not be confirmed.
    /// </summary>
    Task<string?> DisconnectAsync(
        int accountId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Signs out and also discards a ChatGPT plan registration (issued client and
    /// identity) so the next sign-in registers again. The host identifier is kept.
    /// </summary>
    Task<string?> ForgetRegistrationAsync(
        int accountId,
        CancellationToken cancellationToken = default);
}
