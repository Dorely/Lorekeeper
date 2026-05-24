namespace Lorekeeper.Models;

public class EmbeddingConfiguration
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public int ProviderId { get; set; }
    public LlmProvider Provider { get; set; } = null!;

    public EmbeddingApiKind ApiKind { get; set; }
    public required string ModelId { get; set; }
    public int Dimensions { get; set; }

    public int LastTestedProviderId { get; set; }
    public EmbeddingApiKind LastTestedApiKind { get; set; }
    public required string LastTestedModelId { get; set; }
    public int LastTestedDimensions { get; set; }
    public DateTime LastTestedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum EmbeddingApiKind
{
    OllamaNative,
    OpenAICompatible,
}

