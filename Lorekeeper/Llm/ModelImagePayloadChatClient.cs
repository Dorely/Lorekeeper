using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.AI;
using SkiaSharp;

namespace Lorekeeper.Llm;

/// <summary>
/// Compacts oversized image payloads on every provider request. Assistant loops
/// resend their whole history each tool round, so a few large PNG references
/// otherwise multiply into tens of megabytes of base64 per turn.
/// </summary>
internal sealed class ModelImagePayloadChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(ModelImagePayload.Compact(messages), options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(ModelImagePayload.Compact(messages), options, cancellationToken);
}

public static class ModelImagePayload
{
    /// <summary>
    /// Marks an image the model explicitly asked to see at full resolution; the
    /// compactor passes it through byte for byte.
    /// </summary>
    public const string FullResolutionProperty = "lorekeeper.image.fullResolution";
    public const int MaxEdge = 2048;
    public const int CompactAboveBytes = 768 * 1024;
    private const int JpegQuality = 90;
    private const int CacheCapacity = 128;

    private static readonly ConcurrentDictionary<string, CompactedImage?> Cache = new(StringComparer.Ordinal);

    public static DataContent MarkFullResolution(DataContent image)
    {
        image.AdditionalProperties ??= [];
        image.AdditionalProperties[FullResolutionProperty] = true;
        return image;
    }

    public static bool IsFullResolution(AIContent content) =>
        content.AdditionalProperties?.TryGetValue(FullResolutionProperty, out var value) == true
        && value is true;

    public static IEnumerable<ChatMessage> Compact(IEnumerable<ChatMessage> messages)
    {
        var source = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        List<ChatMessage>? result = null;
        for (var index = 0; index < source.Count; index++)
        {
            var message = source[index];
            var compacted = CompactMessage(message);
            if (result is null && !ReferenceEquals(compacted, message))
                result = source.Take(index).ToList();
            result?.Add(compacted);
        }
        return result ?? source;
    }

    private static ChatMessage CompactMessage(ChatMessage message)
    {
        List<AIContent>? contents = null;
        for (var index = 0; index < message.Contents.Count; index++)
        {
            var content = message.Contents[index];
            var compacted = content is DataContent image ? CompactImage(image) : content;
            if (contents is null && !ReferenceEquals(compacted, content))
                contents = message.Contents.Take(index).ToList();
            contents?.Add(compacted);
        }
        if (contents is null)
            return message;

        // RawRepresentation is dropped on purpose: provider adapters may prefer it
        // over Contents, which would resend the original bytes.
        return new ChatMessage(message.Role, contents)
        {
            AuthorName = message.AuthorName,
            MessageId = message.MessageId,
            CreatedAt = message.CreatedAt,
            AdditionalProperties = message.AdditionalProperties,
        };
    }

    public static DataContent CompactImage(DataContent image)
    {
        if (!image.HasTopLevelMediaType("image")
            || IsFullResolution(image)
            || image.Data.Length <= CompactAboveBytes)
            return image;

        var bytes = image.Data.ToArray();
        var key = Convert.ToHexString(SHA256.HashData(bytes));
        if (!Cache.TryGetValue(key, out var compacted))
        {
            compacted = Encode(bytes);
            if (Cache.Count >= CacheCapacity)
                Cache.Clear();
            Cache[key] = compacted;
        }
        if (compacted is null)
            return image;

        return new DataContent(compacted.Data, compacted.MediaType)
        {
            Name = image.Name is { Length: > 0 } name
                ? Path.ChangeExtension(name, compacted.MediaType == "image/jpeg" ? ".jpg" : ".png")
                : image.Name,
            AdditionalProperties = image.AdditionalProperties,
        };
    }

    private static CompactedImage? Encode(byte[] original)
    {
        try
        {
            using var decoded = SKBitmap.Decode(original);
            if (decoded is null || decoded.Width <= 0 || decoded.Height <= 0)
                return null;

            using var resized = Math.Max(decoded.Width, decoded.Height) > MaxEdge
                ? Downscale(decoded)
                : null;
            var bitmap = resized ?? decoded;
            var transparent = HasTransparency(bitmap);
            using var encoded = transparent
                ? bitmap.Encode(SKEncodedImageFormat.Png, 100)
                : bitmap.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
            if (encoded is null || encoded.Size >= original.Length)
                return null;
            return new CompactedImage(encoded.ToArray(), transparent ? "image/png" : "image/jpeg");
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static SKBitmap? Downscale(SKBitmap bitmap)
    {
        var scale = (double)MaxEdge / Math.Max(bitmap.Width, bitmap.Height);
        var info = new SKImageInfo(
            Math.Max(1, (int)Math.Round(bitmap.Width * scale)),
            Math.Max(1, (int)Math.Round(bitmap.Height * scale)),
            SKColorType.Rgba8888,
            bitmap.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Premul);
        return bitmap.Resize(info, new SKSamplingOptions(SKCubicResampler.Mitchell));
    }

    private static bool HasTransparency(SKBitmap bitmap)
    {
        if (bitmap.AlphaType == SKAlphaType.Opaque)
            return false;

        using var converted = bitmap.ColorType is SKColorType.Rgba8888 or SKColorType.Bgra8888
            ? null
            : bitmap.Copy(SKColorType.Rgba8888);
        var pixels = converted ?? bitmap;
        if (pixels.BytesPerPixel != 4)
            return true;

        var span = pixels.GetPixelSpan();
        var rowBytes = pixels.RowBytes;
        for (var row = 0; row < pixels.Height; row++)
        {
            var line = span.Slice(row * rowBytes, pixels.Width * 4);
            for (var alpha = 3; alpha < line.Length; alpha += 4)
            {
                if (line[alpha] != 255)
                    return true;
            }
        }
        return false;
    }

    private sealed record CompactedImage(byte[] Data, string MediaType);
}
