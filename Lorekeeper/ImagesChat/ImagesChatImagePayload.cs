using Lorekeeper.Images;

namespace Lorekeeper.ImagesChat;

internal static class ImagesChatImagePayload
{
    public static object From(ProjectImageView image) => new
    {
        image.Id,
        image.FileName,
        image.ContentType,
        image.PreviewUrl,
        FullUrl = image.PreviewUrl.Replace("?maxEdge=640", string.Empty, StringComparison.Ordinal),
        image.AltText,
        image.Source,
        image.Prompt,
        image.GenerationModel,
        image.SourceMetadataJson,
        image.CreatedAt,
        image.UpdatedAt,
        image.SizeBytes,
    };
}
