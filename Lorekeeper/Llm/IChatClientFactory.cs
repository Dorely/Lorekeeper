using Microsoft.Extensions.AI;
using Lorekeeper.Models;

namespace Lorekeeper.Llm;

public interface IChatClientFactory
{
    Task<IChatClient> CreateChatClientAsync(int providerId, CancellationToken cancellationToken = default);

    /// <summary>Sends a short request to verify the provider/model and configured reasoning effort.</summary>
    Task TestModelAsync(int providerId, CancellationToken cancellationToken = default);

    /// <summary>Sends a short request to verify an unsaved or edited provider/model and reasoning effort.</summary>
    Task TestModelAsync(LlmProvider provider, CancellationToken cancellationToken = default);
}
