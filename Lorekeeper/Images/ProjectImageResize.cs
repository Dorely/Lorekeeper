using SkiaSharp;

namespace Lorekeeper.Images;

public static class ProjectImageResize
{
    public const string DeterministicInterpolation = "SkiaSharp default sampling";

    public static byte[] Resize(byte[] data, string contentType, int maxEdge)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(data);
            if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0 || Math.Max(bitmap.Width, bitmap.Height) <= maxEdge)
                return data;
            var scale = (double)maxEdge / Math.Max(bitmap.Width, bitmap.Height);
            using var resized = bitmap.Resize(new SKImageInfo(Math.Max(1, (int)Math.Round(bitmap.Width * scale)), Math.Max(1, (int)Math.Round(bitmap.Height * scale))), SKSamplingOptions.Default);
            if (resized is null) return data;
            using var image = SKImage.FromBitmap(resized);
            var format = contentType.ToLowerInvariant() switch
            {
                "image/jpeg" or "image/jpg" => SKEncodedImageFormat.Jpeg,
                "image/webp" => SKEncodedImageFormat.Webp,
                _ => SKEncodedImageFormat.Png,
            };
            using var encoded = image.Encode(format, 84);
            return encoded?.ToArray() ?? data;
        }
        catch
        {
            return data;
        }
    }

    public static byte[] ResizeExact(byte[] data, int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Resize dimensions must be positive.");

        using var bitmap = SKBitmap.Decode(data)
            ?? throw new InvalidOperationException("Image data could not be decoded.");
        if (bitmap.Width <= 0 || bitmap.Height <= 0)
            throw new InvalidOperationException("Image dimensions are invalid.");

        using var resized = bitmap.Width == width && bitmap.Height == height
            ? bitmap.Copy()
            : bitmap.Resize(new SKImageInfo(width, height), SKSamplingOptions.Default);
        if (resized is null)
            throw new InvalidOperationException("Image could not be resized.");

        using var image = SKImage.FromBitmap(resized);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("Resized image could not be encoded.");
        return encoded.ToArray();
    }
}
