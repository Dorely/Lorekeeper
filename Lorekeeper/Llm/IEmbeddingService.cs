namespace Lorekeeper.Llm;

public interface IEmbeddingService
{
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
    Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default);
    Task<IList<float[]>> GenerateEmbeddingsAsync(IList<string> texts, CancellationToken cancellationToken = default);
}

public sealed record EmbeddingConfigurationDraft(
    int ProviderId,
    Models.EmbeddingApiKind ApiKind,
    string ModelId,
    int TestedDimensions,
    int TestedProviderId,
    Models.EmbeddingApiKind TestedApiKind,
    string TestedModelId);

public sealed record EmbeddingTestRequest(
    int ProviderId,
    Models.EmbeddingApiKind ApiKind,
    string ModelId);

public sealed record EmbeddingTestResult(
    int ProviderId,
    Models.EmbeddingApiKind ApiKind,
    string ModelId,
    int Dimensions,
    DateTime TestedAt);

public sealed record EmbeddingSaveResult(
    Models.EmbeddingConfiguration Configuration,
    bool RebuildQueued);

public sealed record EmbeddingUnsetResult(bool Removed);
