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
        Console.WriteLine();
        Console.WriteLine("# Add the authenticator entry with this Base32 secret, or the otpauth URI below:");
        Console.WriteLine(TotpAuthenticator.BuildOtpauthUri(username, totpSecret));
        return 0;
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
