using SkiaSharp;

namespace Lorekeeper.Fonts;

public static class ProjectFontBinary
{
    public const int MaxBytes = 10 * 1024 * 1024;

    public static NormalizedProjectFont Normalize(byte[] data, string? fileName)
    {
        if (data.Length == 0)
            throw new InvalidOperationException("Font file is empty.");
        if (data.Length > MaxBytes)
            throw new InvalidOperationException("Font file is larger than the 10 MB limit.");

        var cleanName = Path.GetFileName(fileName?.Trim()) ?? string.Empty;
        var extension = Path.GetExtension(cleanName).ToLowerInvariant();
        if (extension is not ".ttf" and not ".otf")
            throw new InvalidOperationException("Only static TTF and OTF font faces can be imported.");
        if (!HasSupportedSignature(data, extension))
            throw new InvalidOperationException("The file signature does not match a supported TTF or OTF font.");
        if (!TryReadTableDirectory(data, out var tables))
            throw new InvalidOperationException("The font has an invalid or truncated OpenType table directory.");
        if (tables.Contains("fvar", StringComparer.Ordinal))
            throw new InvalidOperationException("Variable fonts are not supported yet. Import static TTF or OTF faces instead.");

        using var skData = SKData.CreateCopy(data);
        using var typeface = SKTypeface.FromData(skData)
            ?? throw new InvalidOperationException("The font could not be decoded by the page renderer.");
        var familyName = typeface.FamilyName?.Trim();
        if (string.IsNullOrWhiteSpace(familyName))
            throw new InvalidOperationException("The font does not contain a usable family name.");

        var weight = Math.Clamp(typeface.FontStyle.Weight, 100, 900);
        var italic = typeface.FontStyle.Slant != SKFontStyleSlant.Upright;
        var subfamily = weight >= 700
            ? italic ? "Bold Italic" : "Bold"
            : italic ? "Italic" : "Regular";
        return new NormalizedProjectFont(
            familyName,
            subfamily,
            string.IsNullOrWhiteSpace(cleanName) ? $"font{extension}" : cleanName,
            extension == ".otf" ? "font/otf" : "font/ttf",
            weight,
            italic,
            data);
    }

    private static bool HasSupportedSignature(byte[] data, string extension)
    {
        if (data.Length < 12)
            return false;
        var trueType = data[0] == 0 && data[1] == 1 && data[2] == 0 && data[3] == 0;
        var openType = data[0] == (byte)'O' && data[1] == (byte)'T' && data[2] == (byte)'T' && data[3] == (byte)'O';
        return extension == ".ttf" ? trueType : openType;
    }

    private static bool TryReadTableDirectory(byte[] data, out IReadOnlyList<string> tables)
    {
        var found = new List<string>();
        var tableCount = ReadUInt16BigEndian(data, 4);
        if (tableCount == 0 || 12L + tableCount * 16L > data.Length)
        {
            tables = [];
            return false;
        }

        for (var index = 0; index < tableCount; index++)
        {
            var offset = 12 + (index * 16);
            var tableOffset = ReadUInt32BigEndian(data, offset + 8);
            var tableLength = ReadUInt32BigEndian(data, offset + 12);
            if ((ulong)tableOffset + tableLength > (ulong)data.Length)
            {
                tables = [];
                return false;
            }

            found.Add(System.Text.Encoding.ASCII.GetString(data, offset, 4));
        }

        tables = found;
        return true;
    }

    private static ushort ReadUInt16BigEndian(byte[] data, int offset) =>
        offset + 2 <= data.Length
            ? (ushort)((data[offset] << 8) | data[offset + 1])
            : (ushort)0;

    private static uint ReadUInt32BigEndian(byte[] data, int offset) =>
        offset + 4 <= data.Length
            ? ((uint)data[offset] << 24)
                | ((uint)data[offset + 1] << 16)
                | ((uint)data[offset + 2] << 8)
                | data[offset + 3]
            : uint.MaxValue;
}

public sealed record NormalizedProjectFont(
    string FamilyName,
    string SubfamilyName,
    string FileName,
    string ContentType,
    int Weight,
    bool Italic,
    byte[] Data);
