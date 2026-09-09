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
