namespace Lorekeeper.Authorization;

public sealed record OpenAiAccountAccess(
    int AccountId,
    string ExternalAccountId,
    string AccessToken);

public interface IOpenAiAccountTokenService
{
    Task<OpenAiAccountAccess?> GetValidAccessAsync(
        int accountId,
        CancellationToken cancellationToken = default);

    Task DisconnectAsync(
        int accountId,
        CancellationToken cancellationToken = default);
}
