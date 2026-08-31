using SkiaSharp;

namespace Lorekeeper.Images;

public static class ProjectImageResampler
{
    public const string Lanczos3Interpolation = "Lanczos3 (separable)";

    private const int KernelSupport = 3;
    private const int BytesPerPixel = 4;

    public static byte[] ResampleExact(byte[] data, int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Resample dimensions must be positive.");

        using var source = SKBitmap.Decode(data)
            ?? throw new InvalidOperationException("Image data could not be decoded.");
        if (source.Width <= 0 || source.Height <= 0)
            throw new InvalidOperationException("Image dimensions are invalid.");
        if (source.Width == width && source.Height == height)
            return EncodePng(source);

        using var bitmap = source.ColorType == SKColorType.Bgra8888
            ? source
            : source.Copy(SKColorType.Bgra8888)
                ?? throw new InvalidOperationException("Image could not be converted for resampling.");

        var sourcePixels = bitmap.GetPixelSpan();
        var horizontal = ResampleDimension(
            sourcePixels,
            source.Width,
            source.Height,
            width,
            source.Height,
            horizontal: true);
        var resultPixels = ResampleDimension(
            horizontal,
            width,
            source.Height,
            width,
            height,
            horizontal: false);

        using var result = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul)
            ?? throw new InvalidOperationException("Resampled image could not be created.");
        resultPixels.CopyTo(result.GetPixelSpan());
        using var image = SKImage.FromBitmap(result);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("Resampled image could not be encoded.");
        return encoded.ToArray();
    }

    private static byte[] ResampleDimension(
        ReadOnlySpan<byte> source,
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        bool horizontal)
    {
        var sourceStride = horizontal ? sourceWidth : sourceHeight;
        var fixedStride = horizontal ? sourceHeight : sourceWidth;
        var targetStride = horizontal ? targetWidth : targetHeight;
        var fixedTargetStride = horizontal ? targetHeight : targetWidth;
        var result = new byte[targetWidth * targetHeight * BytesPerPixel];
        var weights = new (int Start, float[] Values)[targetStride];

        for (var targetIndex = 0; targetIndex < targetStride; targetIndex++)
        {
            var center = (targetIndex + 0.5d) * sourceStride / targetStride - 0.5d;
            var start = (int)Math.Floor(center) - KernelSupport + 1;
            var taps = new float[KernelSupport * 2];
            var total = 0d;
            for (var tap = 0; tap < taps.Length; tap++)
            {
                var weight = Lanczos3(start + tap - center);
                taps[tap] = (float)weight;
                total += weight;
            }
            var normalizer = Math.Abs(total) > 1e-9 ? 1d / total : 1d;
            for (var tap = 0; tap < taps.Length; tap++)
                taps[tap] = (float)(taps[tap] * normalizer);
            weights[targetIndex] = (start, taps);
        }

        Span<double> accumulator = stackalloc double[BytesPerPixel];
        for (var fixedIndex = 0; fixedIndex < fixedStride; fixedIndex++)
        {
            for (var targetIndex = 0; targetIndex < targetStride; targetIndex++)
            {
                var (start, taps) = weights[targetIndex];
                accumulator.Clear();
                for (var tap = 0; tap < taps.Length; tap++)
                {
                    var sourceIndex = Math.Clamp(start + tap, 0, sourceStride - 1);
                    var sourceOffset = horizontal
                        ? (fixedIndex * sourceStride + sourceIndex) * BytesPerPixel
                        : (sourceIndex * fixedStride + fixedIndex) * BytesPerPixel;
                    var weight = taps[tap];
                    accumulator[0] += weight * source[sourceOffset];
                    accumulator[1] += weight * source[sourceOffset + 1];
                    accumulator[2] += weight * source[sourceOffset + 2];
                    accumulator[3] += weight * source[sourceOffset + 3];
                }

                var targetOffset = horizontal
                    ? (fixedIndex * targetStride + targetIndex) * BytesPerPixel
                    : (targetIndex * fixedTargetStride + fixedIndex) * BytesPerPixel;
                for (var channel = 0; channel < BytesPerPixel; channel++)
                    result[targetOffset + channel] = ClampToByte(accumulator[channel]);
            }
        }

        return result;
    }

    private static double Lanczos3(double value)
    {
        var distance = Math.Abs(value);
        if (distance < 1e-9)
            return 1d;
        if (distance >= KernelSupport)
            return 0d;
        var piDistance = Math.PI * distance;
        return KernelSupport * Math.Sin(piDistance) * Math.Sin(piDistance / KernelSupport)
            / (piDistance * piDistance);
    }

    private static byte ClampToByte(double value) =>
        (byte)Math.Clamp((int)Math.Round(value), 0, 255);

    private static byte[] EncodePng(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("Image could not be encoded.");
        return encoded.ToArray();
    }
}
