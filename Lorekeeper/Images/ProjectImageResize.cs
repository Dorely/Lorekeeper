using SkiaSharp;

namespace Lorekeeper.Images;

public static class ProjectImageResize
{
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
}
