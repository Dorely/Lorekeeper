using SkiaSharp;

namespace Lorekeeper.Images;

public static class ProjectImageBinary
{
    public const int DefaultMaxBytes = 20 * 1024 * 1024;
    public const int ProviderInputMaxBytes = 50 * 1024 * 1024;

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

    public static NormalizedProjectImage EncodePng(
        byte[] data,
        string? fileName,
        int maxBytes = ProviderInputMaxBytes)
    {
        if (data.Length == 0)
            throw new InvalidOperationException("Image file is empty.");
        if (data.Length >= maxBytes)
            throw new InvalidOperationException($"Image must be smaller than the provider's {maxBytes / 1024 / 1024:N0} MB input limit.");

        using var stream = new SKMemoryStream(data);
        using var codec = SKCodec.Create(stream) ?? throw new InvalidOperationException("Image data is not a supported raster image.");
        if (codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Webp))
            throw new InvalidOperationException("Only PNG, JPEG, and WebP raster images can be used.");
        using var bitmap = SKBitmap.Decode(data) ?? throw new InvalidOperationException("Image data is not a supported raster image.");
        if (bitmap.Width <= 0 || bitmap.Height <= 0)
            throw new InvalidOperationException("Image dimensions are invalid.");

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("Image could not be converted to PNG.");
        var png = encoded.ToArray();
        if (png.Length >= maxBytes)
            throw new InvalidOperationException($"PNG conversion must be smaller than the provider's {maxBytes / 1024 / 1024:N0} MB input limit.");

        return new NormalizedProjectImage(
            png,
            "image/png",
            SafeFileName(Path.ChangeExtension(fileName, ".png"), "image/png"),
            bitmap.Width,
            bitmap.Height);
    }

    public static NormalizedProjectImage ValidateBinaryPngMask(
        byte[] data,
        string? contentType,
        string? fileName,
        int maxBytes = ProviderInputMaxBytes)
    {
        if (data.Length == 0)
            throw new InvalidOperationException("Mask PNG is empty.");
        if (data.Length >= maxBytes)
            throw new InvalidOperationException($"Mask PNG must be smaller than the provider's {maxBytes / 1024 / 1024:N0} MB input limit.");
        if (!string.Equals(contentType?.Trim(), "image/png", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Mask must be a PNG image.");

        using var stream = new SKMemoryStream(data);
        using var codec = SKCodec.Create(stream) ?? throw new InvalidOperationException("Mask PNG could not be decoded.");
        if (codec.EncodedFormat != SKEncodedImageFormat.Png)
            throw new InvalidOperationException("Mask data must be encoded as PNG.");
        using var bitmap = SKBitmap.Decode(data) ?? throw new InvalidOperationException("Mask PNG could not be decoded.");
        if (bitmap.Width <= 0 || bitmap.Height <= 0)
            throw new InvalidOperationException("Mask PNG dimensions are invalid.");
        if (bitmap.AlphaType == SKAlphaType.Opaque)
            throw new InvalidOperationException("Mask PNG must contain an alpha channel.");

        var hasEditablePixel = false;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var alpha = bitmap.GetPixel(x, y).Alpha;
                if (alpha is not (byte.MinValue or byte.MaxValue))
                    throw new InvalidOperationException("Mask PNG alpha must be binary: editable pixels are fully transparent and protected pixels are fully opaque.");
                hasEditablePixel |= alpha == byte.MinValue;
            }
        }

        if (!hasEditablePixel)
            throw new InvalidOperationException("Paint the editable mask area first.");

        return new NormalizedProjectImage(
            data,
            "image/png",
            SafeFileName(Path.ChangeExtension(fileName, ".png"), "image/png"),
            bitmap.Width,
            bitmap.Height);
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
