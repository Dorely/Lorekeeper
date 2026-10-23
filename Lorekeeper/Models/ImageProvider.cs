namespace Lorekeeper.Models;

/// <summary>
/// One configured image-generation connection. API secrets stay on this row; a
/// ChatGPT account connection borrows the Codex account's OAuth tokens through
/// <see cref="OpenAiAccountId"/> and never copies them here.
/// </summary>
public class ImageProvider
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public string? DisplayName { get; set; }

    public ImageProviderKind Kind { get; set; }

    public string? EndpointUrl { get; set; }

    public string? ApiKey { get; set; }

    /// <summary>The Codex account whose sign-in serves image requests; only for <see cref="ImageProviderKind.ChatGptAccount"/>.</summary>
    public int? OpenAiAccountId { get; set; }

    public OpenAiAccount? OpenAiAccount { get; set; }

    public bool IsDefault { get; set; }

    public string ConfigurationJson { get; set; } = "{}";

    public DateTime? LastTestedAt { get; set; }

    public bool? LastTestSucceeded { get; set; }

    public string? LastTestMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<ImageProviderModel> Models { get; set; } = [];
}

public enum ImageProviderKind
{
    ChatGptAccount,
    OpenRouter,
    OpenAiImagesCompatible,
}

/// <summary>An image model offered by one <see cref="ImageProvider"/>, with its request capabilities and raster limits.</summary>
public class ImageProviderModel
{
    public int Id { get; set; }

    public int ImageProviderId { get; set; }

    public ImageProvider? ImageProvider { get; set; }

    public required string ModelId { get; set; }

    public string? DisplayName { get; set; }

    public ImageProviderModelOrigin Origin { get; set; } = ImageProviderModelOrigin.Manual;

    /// <summary>JSON array of accepted quality values; the first is the default.</summary>
    public string QualitiesJson { get; set; } = "[\"auto\"]";

    /// <summary>JSON array of accepted output formats (png, jpeg, webp).</summary>
    public string OutputFormatsJson { get; set; } = "[\"png\"]";

    public bool SupportsGenerate { get; set; } = true;

    public bool SupportsEdit { get; set; }

    /// <summary>Accepts an alpha mask that guides where an edit lands.</summary>
    public bool SupportsRegionalGuide { get; set; }

    public int MaxReferenceImages { get; set; }

    public bool SupportsTransparentBackground { get; set; }

    public bool SupportsStreaming { get; set; }

    /// <summary>Serialized <c>ImageRasterConstraints</c> for the sizes this model accepts.</summary>
    public string RasterConstraintsJson { get; set; } = "{}";

    public bool IsDefault { get; set; }

    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum ImageProviderModelOrigin
{
    Bundled,
    Discovered,
    Manual,
}
