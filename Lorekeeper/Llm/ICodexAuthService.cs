namespace Lorekeeper.Llm;

public interface ICodexAuthService
{
    /// <summary>Generates an OAuth authorization URL and stores the PKCE state for the callback.</summary>
    (string AuthorizationUrl, string State) StartPkceFlow(int accountId);

    Task<int> HandleCallbackAsync(string code, string state, CancellationToken cancellationToken = default);

    Task<string?> GetValidTokenAsync(int accountId, CancellationToken cancellationToken = default);

    Task RevokeTokenAsync(int accountId, CancellationToken cancellationToken = default);
}
