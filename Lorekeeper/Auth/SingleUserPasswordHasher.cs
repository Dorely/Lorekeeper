using System.Security.Cryptography;

namespace Lorekeeper.Auth;

/// <summary>
/// PBKDF2-SHA256 password hashing for the single-user login. The encoded form is
/// <c>pbkdf2-sha256.{iterations}.{saltBase64}.{hashBase64}</c> so the stored value is
/// self-describing and iteration counts can be raised without a format change.
/// </summary>
public static class SingleUserPasswordHasher
{
    public const int DefaultIterations = 210_000;

    private const string FormatPrefix = "pbkdf2-sha256";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int MinIterations = 100_000;
    private const int MaxIterations = 10_000_000;

    public static string Hash(string password, int iterations = DefaultIterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, MinIterations);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(iterations, MaxIterations);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{FormatPrefix}.{iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string encodedHash)
    {
        if (string.IsNullOrEmpty(password) || !TryParse(encodedHash, out var iterations, out var salt, out var expected))
            return false;

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static bool IsValidEncodedHash(string encodedHash) => TryParse(encodedHash, out _, out _, out _);

    private static bool TryParse(string encodedHash, out int iterations, out byte[] salt, out byte[] hash)
    {
        iterations = 0;
        salt = [];
        hash = [];
        if (string.IsNullOrWhiteSpace(encodedHash))
            return false;

        var parts = encodedHash.Split('.');
        if (parts is not [FormatPrefix, var iterationText, var saltText, var hashText])
            return false;
        if (!int.TryParse(iterationText, out iterations) || iterations is < MinIterations or > MaxIterations)
            return false;

        try
        {
            salt = Convert.FromBase64String(saltText);
            hash = Convert.FromBase64String(hashText);
        }
        catch (FormatException)
        {
            return false;
        }

        return salt.Length >= 8 && hash.Length >= 16;
    }
}
