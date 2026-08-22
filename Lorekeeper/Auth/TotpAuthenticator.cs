using System.Security.Cryptography;
using System.Text;

namespace Lorekeeper.Auth;

/// <summary>
/// RFC 6238 time-based one-time passwords (SHA-1, 30-second period, 6 digits) with the
/// standard one-step clock-drift window, compatible with common authenticator apps.
/// </summary>
public static class TotpAuthenticator
{
    public const int PeriodSeconds = 30;
    public const int Digits = 6;

    private const int DriftSteps = 1;
    private const int SecretBytes = 20;
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string GenerateSecret() => Base32Encode(RandomNumberGenerator.GetBytes(SecretBytes));

    public static bool IsValidSecret(string secret) => TryBase32Decode(secret, out var bytes) && bytes.Length >= 10;

    public static string BuildOtpauthUri(string username, string secret) =>
        $"otpauth://totp/Lorekeeper:{Uri.EscapeDataString(username)}?secret={secret}"
        + $"&issuer=Lorekeeper&algorithm=SHA1&digits={Digits}&period={PeriodSeconds}";

    /// <summary>
    /// Validates a submitted code against the current time step and its immediate
    /// neighbors. On success, <paramref name="matchedStep"/> identifies the accepted
    /// step so callers can reject replays of the same code.
    /// </summary>
    public static bool Validate(string secret, string code, DateTimeOffset now, out long matchedStep)
    {
        matchedStep = 0;
        if (string.IsNullOrWhiteSpace(code) || code.Length != Digits || !code.All(char.IsAsciiDigit))
            return false;
        if (!TryBase32Decode(secret, out var key))
            return false;

        var currentStep = now.ToUnixTimeSeconds() / PeriodSeconds;
        var submitted = Encoding.ASCII.GetBytes(code);
        for (var offset = -DriftSteps; offset <= DriftSteps; offset++)
        {
            var step = currentStep + offset;
            if (step < 0)
                continue;

            var expected = Encoding.ASCII.GetBytes(ComputeCode(key, step));
            if (CryptographicOperations.FixedTimeEquals(submitted, expected))
            {
                matchedStep = step;
                return true;
            }
        }

        return false;
    }

    private static string ComputeCode(byte[] key, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> mac = stackalloc byte[20];
        HMACSHA1.HashData(key, counter, mac);

        var truncationOffset = mac[^1] & 0x0F;
        var binaryCode = ((mac[truncationOffset] & 0x7F) << 24)
            | (mac[truncationOffset + 1] << 16)
            | (mac[truncationOffset + 2] << 8)
            | mac[truncationOffset + 3];
        return (binaryCode % 1_000_000).ToString("D6");
    }

    private static string Base32Encode(byte[] data)
    {
        var result = new StringBuilder((data.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;
        foreach (var value in data)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                result.Append(Base32Alphabet[(buffer >> bits) & 0x1F]);
            }
        }

        if (bits > 0)
            result.Append(Base32Alphabet[(buffer << (5 - bits)) & 0x1F]);
        return result.ToString();
    }

    private static bool TryBase32Decode(string secret, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(secret))
            return false;

        var normalized = secret.Trim().TrimEnd('=').Replace(" ", "", StringComparison.Ordinal);
        if (normalized.Length == 0)
            return false;

        var output = new List<byte>(normalized.Length * 5 / 8);
        var buffer = 0;
        var bits = 0;
        foreach (var character in normalized)
        {
            var index = Base32Alphabet.IndexOf(char.ToUpperInvariant(character));
            if (index < 0)
                return false;

            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)((buffer >> bits) & 0xFF));
            }
        }

        bytes = [.. output];
        return true;
    }
}
