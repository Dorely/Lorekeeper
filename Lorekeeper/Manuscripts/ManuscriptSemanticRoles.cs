using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Lorekeeper.Manuscripts;

public static partial class ManuscriptSemanticRoles
{
    private const int MaximumLength = 80;

    public static bool IsValid(string? value) =>
        value is not null
        && value.Length <= MaximumLength
        && SemanticRoleRegex().IsMatch(value);

    public static string NormalizeLegacy(string value)
    {
        var trimmed = value.Trim();
        if (IsValid(trimmed))
            return trimmed;

        var slug = string.Join(
            '-',
            NonAlphaNumericRegex()
                .Split(trimmed.ToLowerInvariant())
                .Where(part => part.Length > 0));
        if (slug.Length == 0 || !char.IsAsciiLetter(slug[0]))
            slug = $"legacy-{slug}";

        var hash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(trimmed)))[..10];
        var prefixLength = MaximumLength - hash.Length - 1;
        slug = slug[..Math.Min(slug.Length, prefixLength)].TrimEnd('-');
        if (slug.Length == 0)
            slug = "legacy";
        return $"{slug}-{hash}";
    }

    [GeneratedRegex("^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SemanticRoleRegex();

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonAlphaNumericRegex();
}
