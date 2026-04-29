using Microsoft.Extensions.AI;

namespace Lorekeeper.Llm;

public interface IChatClientFactory
{
    Task<IChatClient> CreateChatClientAsync(int providerId, CancellationToken cancellationToken = default);

    /// <summary>Sends a 1-token request to verify the provider/model is reachable and credentialed.</summary>
    Task TestModelAsync(int providerId, CancellationToken cancellationToken = default);
}
