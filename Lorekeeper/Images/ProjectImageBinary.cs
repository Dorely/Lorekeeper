using SkiaSharp;

namespace Lorekeeper.Images;

public static class ProjectImageBinary
{
    public const int DefaultMaxBytes = 20 * 1024 * 1024;

    public static NormalizedProjectImage Normalize(byte[] data, string? contentType, string? fileName, int maxBytes = DefaultMaxBytes)
    {
        if (data.Length == 0)
            throw new InvalidOperationException("Image file is empty.");
        if (data.Length > maxBytes)
            throw new InvalidOperationException($"Image is larger than the configured {maxBytes / 1024 / 1024:N0} MB limit.");

        using var stream = new SKMemoryStream(data);
        using var codec = SKCodec.Create(stream) ?? throw new InvalidOperationException("Image data is not a supported raster image.");
        var normalizedType = codec.EncodedFormat switch
        {
            SKEncodedImageFormat.Png => "image/png",
            SKEncodedImageFormat.Jpeg => "image/jpeg",
            SKEncodedImageFormat.Webp => "image/webp",
            _ => throw new InvalidOperationException("Only PNG, JPEG, and WebP raster images can be used."),
        };
        using var bitmap = SKBitmap.Decode(data) ?? throw new InvalidOperationException("Image data is not a supported raster image.");
        if (bitmap.Width <= 0 || bitmap.Height <= 0)
            throw new InvalidOperationException("Image dimensions are invalid.");

        if (normalizedType is "image/png" or "image/jpeg")
        {
            return new NormalizedProjectImage(
                data,
                normalizedType,
                SafeFileName(fileName, normalizedType),
                bitmap.Width,
                bitmap.Height);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 95);
        var png = encoded?.ToArray() ?? throw new InvalidOperationException("Image could not be converted to PNG.");
        return new NormalizedProjectImage(png, "image/png", SafeFileName(Path.ChangeExtension(fileName, ".png"), "image/png"), bitmap.Width, bitmap.Height);
    }

    private static string SafeFileName(string? fileName, string contentType)
    {
        var clean = Path.GetFileName(fileName?.Trim());
        var extension = contentType == "image/jpeg" ? ".jpg" : ".png";
        if (!string.IsNullOrWhiteSpace(clean)) return Path.ChangeExtension(clean, extension);
        return $"image-{DateTime.UtcNow:yyyyMMddHHmmss}{extension}";
    }
}

public sealed record NormalizedProjectImage(byte[] Data, string ContentType, string FileName, int Width, int Height);
