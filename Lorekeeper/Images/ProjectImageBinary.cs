using SkiaSharp;

namespace Lorekeeper.Images;

public static class ProjectImageBinary
{
    public const int DefaultMaxBytes = 20 * 1024 * 1024;

    public static bool ContainsTransparentPixel(byte[] data)
    {
        using var bitmap = SKBitmap.Decode(data)
            ?? throw new InvalidDataException("Image transparency could not be inspected.");
        if (bitmap.AlphaType == SKAlphaType.Opaque)
            return false;

        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
            if (bitmap.GetPixel(x, y).Alpha < byte.MaxValue)
                return true;

        return false;
    }

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

    /// <summary>
    /// Fully decodes a provider's image, rejecting truncated or corrupt data
    /// that a header-only read would accept, and re-encodes it as JPEG when
    /// JPEG was requested from a provider that returned another format.
    /// </summary>
    public static NormalizedProjectImage DecodeProviderOutput(
        byte[] data,
        string requestedOutputFormat,
        int? outputCompression,
        int maxBytes)
    {
        if (data.Length == 0)
            throw new InvalidDataException("The image provider returned an empty image.");
        if (data.Length > maxBytes)
            throw new InvalidDataException($"The provider image is larger than the configured {maxBytes / 1024 / 1024:N0} MB limit.");

        using var stream = new SKMemoryStream(data);
        using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException("The provider image is not a supported raster image.");
        var contentType = codec.EncodedFormat switch
        {
            SKEncodedImageFormat.Png => "image/png",
            SKEncodedImageFormat.Jpeg => "image/jpeg",
            SKEncodedImageFormat.Webp => "image/webp",
            _ => throw new InvalidDataException("The provider image is not PNG, JPEG, or WebP."),
        };
        var info = new SKImageInfo(
            codec.Info.Width,
            codec.Info.Height,
            SKImageInfo.PlatformColorType,
            codec.Info.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Premul);
        if (info.Width <= 0 || info.Height <= 0)
            throw new InvalidDataException("The provider image dimensions are invalid.");
        using var bitmap = new SKBitmap(info);
        if (codec.GetPixels(info, bitmap.GetPixels()) != SKCodecResult.Success)
            throw new InvalidDataException("The provider image is truncated or corrupt.");

        if (requestedOutputFormat is not ("jpeg" or "jpg") || contentType == "image/jpeg")
            return new NormalizedProjectImage(data, contentType, SafeFileName(null, contentType), info.Width, info.Height);

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(outputCompression ?? 90, 0, 100))
            ?? throw new InvalidDataException("The provider image could not be converted to JPEG.");
        return new NormalizedProjectImage(encoded.ToArray(), "image/jpeg", SafeFileName(null, "image/jpeg"), info.Width, info.Height);
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
