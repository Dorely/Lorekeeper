using System.Security.Cryptography;
using System.Text;
using Lorekeeper.Models;

namespace Lorekeeper.Ingest;

/// <summary>
/// Keeps immutable retained-source invariants in one production boundary so
/// callers cannot create a second, unvalidated binary store.
/// </summary>
public static class SourceRetentionValidator
{
    public static SourceOriginal BuildAvailableOriginal(
        Guid sourceId,
        string fileName,
        string mediaType,
        byte[] bytes,
        IDictionary<string, SourceOriginalBlob> blobs)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0)
            throw new ArgumentException("A retained source original cannot be empty.", nameof(bytes));

        var original = new SourceOriginal
        {
            SourceId = sourceId,
            State = SourceOriginalState.Available,
            FileName = RequiredText(fileName, "source"),
            MediaType = RequiredText(mediaType, "application/octet-stream"),
            Length = bytes.LongLength,
            Sha256 = Sha256(bytes),
        };

        for (var offset = 0; offset < bytes.Length; offset += SourceOriginal.MaximumChunkBytes)
        {
            var count = Math.Min(SourceOriginal.MaximumChunkBytes, bytes.Length - offset);
            var data = bytes.AsSpan(offset, count).ToArray();
            var hash = Sha256(data);
            if (!blobs.TryGetValue(hash, out var blob))
            {
                blob = new SourceOriginalBlob { Sha256 = hash, Length = count, Data = data };
                blobs.Add(hash, blob);
            }
            else if (blob.Length != count || !blob.Data.AsSpan().SequenceEqual(data))
            {
                throw new InvalidOperationException("A content-addressed source blob hash collision was detected.");
            }

            original.Chunks.Add(new SourceOriginalChunk
            {
                SourceId = sourceId,
                Index = original.Chunks.Count,
                BlobSha256 = hash,
                Blob = blob,
                ByteLength = count,
            });
        }

        ValidateOriginal(original);
        return original;
    }

    public static void ValidateOriginal(SourceOriginal original)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (string.IsNullOrWhiteSpace(original.FileName)
            || string.IsNullOrWhiteSpace(original.MediaType)
            || original.Length < 0)
        {
            throw new InvalidOperationException("Source original metadata is invalid.");
        }

        if (original.State == SourceOriginalState.OriginalUnavailable)
        {
            if (original.Chunks.Count != 0 || original.Length != 0 || !string.IsNullOrEmpty(original.Sha256))
                throw new InvalidOperationException("An unavailable original must not carry reconstructed bytes.");
            return;
        }

        if (!IsSha256(original.Sha256))
            throw new InvalidOperationException("An available original requires a SHA-256 hash.");

        var chunks = original.Chunks.OrderBy(chunk => chunk.Index).ToList();
        if (chunks.Count == 0 || chunks.Select(chunk => chunk.Index).SequenceEqual(Enumerable.Range(0, chunks.Count)) is false)
            throw new InvalidOperationException("Retained source chunks must have contiguous ordinals.");
        if (chunks.Any(chunk => chunk.ByteLength is <= 0 or > SourceOriginal.MaximumChunkBytes
            || !IsSha256(chunk.BlobSha256)
            || chunk.Blob is null
            || chunk.Blob.Length != chunk.ByteLength
            || !string.Equals(chunk.BlobSha256, chunk.Blob.Sha256, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Retained source chunk metadata is invalid.");
        }
        if (chunks.Sum(chunk => (long)chunk.ByteLength) != original.Length)
            throw new InvalidOperationException("Retained source chunk lengths do not match the original length.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var chunk in chunks)
            hash.AppendData(chunk.Blob.Data);
        var fullHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (!string.Equals(fullHash, original.Sha256, StringComparison.Ordinal))
            throw new InvalidOperationException("Retained source original hash does not match its ordered chunks.");
    }

    internal static void MarkExternallyVerifiedOriginal(SourceOriginal original)
    {
        ValidateOriginalMetadata(original, requireTrackedBlobContent: false);
        original.HasExternallyVerifiedBlobContent = true;
    }

    internal static void ValidateOriginalForPersistence(SourceOriginal original)
    {
        if (original.HasExternallyVerifiedBlobContent)
            ValidateOriginalMetadata(original, requireTrackedBlobContent: false);
        else
            ValidateOriginal(original);
    }

    private static void ValidateOriginalMetadata(
        SourceOriginal original,
        bool requireTrackedBlobContent)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (string.IsNullOrWhiteSpace(original.FileName)
            || string.IsNullOrWhiteSpace(original.MediaType)
            || original.Length < 0)
        {
            throw new InvalidOperationException("Source original metadata is invalid.");
        }

        if (original.State == SourceOriginalState.OriginalUnavailable)
        {
            if (original.Chunks.Count != 0 || original.Length != 0 || !string.IsNullOrEmpty(original.Sha256))
                throw new InvalidOperationException("An unavailable original must not carry reconstructed bytes.");
            return;
        }

        if (!IsSha256(original.Sha256))
            throw new InvalidOperationException("An available original requires a SHA-256 hash.");

        var chunks = original.Chunks.OrderBy(chunk => chunk.Index).ToList();
        if (chunks.Count == 0 || chunks.Select(chunk => chunk.Index).SequenceEqual(Enumerable.Range(0, chunks.Count)) is false)
            throw new InvalidOperationException("Retained source chunks must have contiguous ordinals.");
        if (chunks.Any(chunk => chunk.ByteLength is <= 0 or > SourceOriginal.MaximumChunkBytes
            || !IsSha256(chunk.BlobSha256)
            || requireTrackedBlobContent && (chunk.Blob is null
                || chunk.Blob.Length != chunk.ByteLength
                || !string.Equals(chunk.BlobSha256, chunk.Blob.Sha256, StringComparison.Ordinal))))
        {
            throw new InvalidOperationException("Retained source chunk metadata is invalid.");
        }
        if (chunks.Sum(chunk => (long)chunk.ByteLength) != original.Length)
            throw new InvalidOperationException("Retained source chunk lengths do not match the original length.");
    }

    public static void ValidateLocation(SourceLocation location, SourceExtractionVersion extraction, IngestSourceBlock? block)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(extraction);
        if (location.SourceId != extraction.SourceId
            || location.ExtractionVersionId != extraction.Id
            || location.ProjectId == Guid.Empty
            || location.NormalizedStart < 0
            || location.NormalizedLength < 0
            || !IsSha256(location.VerificationHash))
        {
            throw new InvalidOperationException("Source location metadata is invalid.");
        }
        if (location.SourceBlockId is not null && block is null)
            throw new InvalidOperationException("Source location block is missing.");
        if (block is not null
            && (location.SourceBlockId != block.Id
                || block.SourceId != location.SourceId
                || block.SourceExtractionVersionId != extraction.Id))
        {
            throw new InvalidOperationException("Source location block does not belong to its extraction.");
        }

        var rangeEnd = (long)location.NormalizedStart + location.NormalizedLength;
        if (rangeEnd > extraction.NormalizedText.Length)
            throw new InvalidOperationException("Source location range is outside its extraction.");

        var quote = extraction.NormalizedText.Substring(location.NormalizedStart, location.NormalizedLength);
        if (!string.Equals(quote, location.Quote, StringComparison.Ordinal)
            || !string.Equals(Sha256(quote), location.VerificationHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Source location quote or verification hash does not match its extraction.");
        }
    }

    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string Sha256(string text) => Sha256(Encoding.UTF8.GetBytes(text ?? string.Empty));

    private static bool IsSha256(string? value) => value is { Length: 64 }
        && value.All(Uri.IsHexDigit);

    private static string RequiredText(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
