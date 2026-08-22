namespace Lorekeeper.Auth;

/// <summary>
/// Configuration for the optional single-user server login. When any value in the
/// <c>Auth:SingleUser</c> section is present the section must be complete and valid;
/// a partially configured login fails startup instead of hosting an unlocked server.
/// </summary>
public sealed class SingleUserAuthOptions
{
    public const string SectionName = "Auth:SingleUser";

    public string? Username { get; set; }
    public string? PasswordHash { get; set; }
    public string? TotpSecret { get; set; }
    public int MaxFailedAttempts { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 5;
    public int SessionDays { get; set; } = 7;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Username)
        || !string.IsNullOrWhiteSpace(PasswordHash)
        || !string.IsNullOrWhiteSpace(TotpSecret);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Username))
            throw new InvalidOperationException($"{SectionName}:Username must be configured for the single-user login.");
        if (string.IsNullOrWhiteSpace(PasswordHash) || !SingleUserPasswordHasher.IsValidEncodedHash(PasswordHash))
            throw new InvalidOperationException(
                $"{SectionName}:PasswordHash must contain a hash produced by the auth-setup command.");
        if (string.IsNullOrWhiteSpace(TotpSecret) || !TotpAuthenticator.IsValidSecret(TotpSecret))
            throw new InvalidOperationException(
                $"{SectionName}:TotpSecret must contain a Base32 secret produced by the auth-setup command.");
        if (MaxFailedAttempts is < 1 or > 100)
            throw new InvalidOperationException($"{SectionName}:MaxFailedAttempts must be between 1 and 100.");
        if (LockoutMinutes is < 1 or > 1440)
            throw new InvalidOperationException($"{SectionName}:LockoutMinutes must be between 1 and 1440.");
        if (SessionDays is < 1 or > 90)
            throw new InvalidOperationException($"{SectionName}:SessionDays must be between 1 and 90.");
    }
}

/// <summary>Whether the single-user login is active for this host, for UI surfaces.</summary>
public sealed record SingleUserAuthState(bool Enabled);
