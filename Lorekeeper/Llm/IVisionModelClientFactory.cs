using Lorekeeper.Models;

namespace Lorekeeper.Llm;

public interface IVisionModelClientFactory
{
    Task<string> ReadImageAsync(
        int providerId,
        byte[] imageBytes,
        string mediaType,
        string prompt,
        int maxOutputTokens = 2048,
        CancellationToken cancellationToken = default);

    Task<string> ReadImageAsync(
        LlmProvider provider,
        byte[] imageBytes,
        string mediaType,
        string prompt,
        int maxOutputTokens = 2048,
        CancellationToken cancellationToken = default);

    Task TestVisionModelAsync(int providerId, CancellationToken cancellationToken = default);

    Task TestVisionModelAsync(LlmProvider provider, CancellationToken cancellationToken = default);
}
