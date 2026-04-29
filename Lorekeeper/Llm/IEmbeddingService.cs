namespace Lorekeeper.Llm;

public interface IEmbeddingService
{
    Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default);
    Task<IList<float[]>> GenerateEmbeddingsAsync(IList<string> texts, CancellationToken cancellationToken = default);
}
