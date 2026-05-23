namespace Lorekeeper.Publish;

public interface ICodexImageGenerationService
{
    Task<CodexGeneratedImage> GenerateAsync(CodexImageGenerationOptions options, CancellationToken cancellationToken = default);
}

public sealed record CodexImageGenerationOptions(
    string Prompt,
    string Size,
    string Quality,
    string OutputFormat,
    int? OutputCompression,
    IReadOnlyList<CodexImageReference> ReferenceImages);

public sealed record CodexImageReference(
    string FileName,
    string ContentType,
    byte[] Data);

public sealed record CodexGeneratedImage(
    byte[] Data,
    string ContentType,
    string OutputFormat,
    string MainlineModel,
    string ImageModel,
    string? RevisedPrompt,
    string? ResponseId,
    string? CallId);
