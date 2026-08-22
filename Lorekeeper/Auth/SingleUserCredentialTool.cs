using QRCoder;

namespace Lorekeeper.Auth;

/// <summary>
/// The <c>auth-setup</c> command-line entry point. It generates the password hash and
/// authenticator secret for the single-user login and prints them as environment
/// variables, so plaintext credentials never need to reach the server's configuration.
/// </summary>
public static class SingleUserCredentialTool
{
    public const string CommandName = "auth-setup";
    private const int MinPasswordLength = 12;

    public static int Run(string[] args)
    {
        string? username = null;
        string? password = null;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--username" when index + 1 < args.Length:
                    username = args[++index];
                    break;
                case "--password" when index + 1 < args.Length:
                    password = args[++index];
                    break;
                default:
                    Console.Error.WriteLine($"Unknown or incomplete argument: {args[index]}");
                    PrintUsage();
                    return 1;
            }
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            Console.Error.WriteLine("A username is required.");
            PrintUsage();
            return 1;
        }

        password ??= PromptForPassword();
        if (password is null)
            return 1;
        if (password.Length < MinPasswordLength)
        {
            Console.Error.WriteLine($"The password must be at least {MinPasswordLength} characters long.");
            return 1;
        }

        var passwordHash = SingleUserPasswordHasher.Hash(password);
        var totpSecret = TotpAuthenticator.GenerateSecret();

        Console.WriteLine("# Single-user login configuration. Store these as secrets; the password itself is not saved.");
        Console.WriteLine($"Auth__SingleUser__Username={username}");
        Console.WriteLine($"Auth__SingleUser__PasswordHash={passwordHash}");
        Console.WriteLine($"Auth__SingleUser__TotpSecret={totpSecret}");
        var otpauthUri = TotpAuthenticator.BuildOtpauthUri(username, totpSecret);
        Console.WriteLine();
        Console.WriteLine("# Add the authenticator entry with this Base32 secret, or the otpauth URI below:");
        Console.WriteLine(otpauthUri);
        PrintEnrollmentQr(otpauthUri);
        return 0;
    }

    /// <summary>
    /// Renders the enrollment URI as a scannable QR code on stderr when it is a
    /// terminal, using explicit black/white ANSI colors so the code stays valid on
    /// dark terminal themes. Redirected output receives only the machine-readable
    /// values on stdout.
    /// </summary>
    private static void PrintEnrollmentQr(string otpauthUri)
    {
        if (Console.IsErrorRedirected)
            return;

        using var generator = new QRCodeGenerator();
        using var qrData = generator.CreateQrCode(otpauthUri, QRCodeGenerator.ECCLevel.M);
        var modules = qrData.ModuleMatrix;
        var size = modules.Count;
        var qr = new System.Text.StringBuilder();
        for (var y = 0; y < size; y += 2)
        {
            for (var x = 0; x < size; x++)
            {
                var topDark = modules[y][x];
                var bottomDark = y + 1 < size && modules[y + 1][x];
                qr.Append($"\x1b[{(topDark ? 30 : 97)};{(bottomDark ? 40 : 107)}m▀");
            }

            qr.AppendLine("\x1b[0m");
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine("Or scan this with your authenticator app:");
        Console.Error.Write(qr);
    }

    private static string? PromptForPassword()
    {
        if (Console.IsInputRedirected)
        {
            Console.Error.Write("Password: ");
            return Console.ReadLine();
        }

        Console.Error.Write("Password (input is hidden): ");
        var buffer = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.Error.WriteLine();
                return buffer.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                    buffer.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
            }
        }
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: dotnet Lorekeeper.dll auth-setup --username <name> [--password <password>]");
        Console.Error.WriteLine("Omit --password to enter it interactively without shell history.");
    }
}
