using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Lorekeeper.ExternalAccess;

public sealed record ExternalClientSetupResult(string Message, bool SkillInstalled, bool ConfigurationInstalled);

public sealed partial class ExternalClientSetupService(McpClientGrantService grants, IServer server, IDataProtectionProvider protection)
{
    private static readonly SemaphoreSlim SetupGate = new(1, 1);
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    public string Endpoint => server.Features.Get<IServerAddressesFeature>()?.Addresses
        .Select(value => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null)
        .Where(value => value is { IsLoopback: true } && value.Scheme == "http")
        .Select(value => $"http://127.0.0.1:{value!.Port}/mcp").FirstOrDefault() ?? "";

    public async Task<ExternalClientSetupResult> ConfigureAsync(Guid clientId, string clientKind, bool installSkill, CancellationToken ct = default)
    {
        if (Endpoint.Length == 0) return new("Use a loopback HTTP launch address, then reconnect this client. Existing configuration is retained.", false, false);
        await SetupGate.WaitAsync(ct);
        try
        {
            var credential = await grants.CredentialForSetupAsync(clientId, cancellationToken: ct);
            var connectionPath = Path.Combine(ExternalTransferCommand.SecurityRoot, "clients", clientId.ToString("N") + ".json");
            EnsureSafeDirectory(Path.GetDirectoryName(connectionPath)!);
            await WritePrivateAsync(connectionPath, JsonSerializer.Serialize(new ExternalLocalConnection(Endpoint,
                protection.CreateProtector("Lorekeeper.ExternalAccess.Credentials.v1").Protect(credential)), ExternalToolCatalog.Json), ct);
            var name = $"lorekeeper_{clientId:N}";
            bool configured;
            string? compatibleCodex = null;
            if (clientKind == "Codex")
            {
                compatibleCodex = await FindCompatibleCodexAsync(ct);
                if (compatibleCodex is null)
                    return new("A compatible Codex runtime was not found. Update Codex or use manual Streamable HTTP configuration; the existing CLI and configuration were preserved.", false, false);
                // The documented CLI can add a URL/env-var token, but cannot persist a static header.
                // Use its documented TOML header configuration without changing any unrelated table.
                var codexDirectory = Environment.GetEnvironmentVariable("CODEX_HOME");
                if (string.IsNullOrWhiteSpace(codexDirectory)) codexDirectory = Path.Combine(UserHome(), ".codex");
                var path = Path.Combine(codexDirectory, "config.toml");
                EnsureSafeDirectory(codexDirectory);
                configured = await UpdateCodexAsync(path, name, credential, ct);
            }
            else if (clientKind == "Claude Code")
            {
                configured = await TryClaudeAsync(name, credential, ct);
            }
            else return new("This client is ready for manual configuration. Use its Streamable HTTP settings with the local endpoint and the connection credential.", false, false);

            var skillInstalled = installSkill && await InstallSkillAsync(clientKind, connectionPath, ct);
            return new(configured
                ? $"{clientKind} connection saved. Open a new client session to load the server and skill."
                    + (compatibleCodex is null ? "" : $" Compatible runtime: {compatibleCodex}.")
                    + (installSkill && !skillInstalled ? " An existing custom skill was preserved; review it before installing the bundled update." : "")
                : "Automatic client setup is unavailable. Your Lorekeeper connection is retained; use the supported manual configuration below.", skillInstalled, configured);
        }
        finally { SetupGate.Release(); }
    }

    public async Task<string> ManualConfigurationAsync(Guid clientId, CancellationToken ct = default) => JsonSerializer.Serialize(new
    {
        mcpServers = new Dictionary<string, object>
        {
            [$"lorekeeper_{clientId:N}"] = new { type = "http", url = Endpoint,
                headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + await grants.CredentialForSetupAsync(clientId, cancellationToken: ct) } },
        },
    }, new JsonSerializerOptions { WriteIndented = true });

    private async Task<bool> UpdateCodexAsync(string path, string name, string credential, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            RejectLinkedFile(path);
            var previous = File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : "";
            var lines = previous.Split('\n');
            var retained = new List<string>();
            bool ownedSection = false;
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith('[')) ownedSection = trimmed == $"[mcp_servers.{name}]"
                    || trimmed.StartsWith($"[mcp_servers.{name}.", StringComparison.Ordinal);
                if (!ownedSection) retained.Add(line);
            }
            var section = $"[mcp_servers.{name}]\nurl = {JsonSerializer.Serialize(Endpoint)}\nhttp_headers = {{ Authorization = {JsonSerializer.Serialize("Bearer " + credential)} }}\n";
            var updated = string.Join('\n', retained).TrimEnd() + "\n\n" + section;
            if ((File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : "") != previous) continue;
            await WritePrivateAsync(path, updated, ct);
            return true;
        }
        return false;
    }

    private async Task<bool> TryClaudeAsync(string name, string credential, CancellationToken ct)
    {
        var executable = FindExecutable("claude");
        if (executable is null) return false;
        var probe = await RunCliAsync(executable, ["mcp", "add", "--help"], ct);
        if (probe.ExitCode != 0 || !probe.Output.Contains("--transport", StringComparison.Ordinal)
            || !probe.Output.Contains("--header", StringComparison.Ordinal)) return false;
        // The generated name belongs exclusively to this grant. The CLI preserves other entries.
        _ = await RunCliAsync(executable, ["mcp", "remove", "--scope", "user", name], ct);
        var result = await RunCliAsync(executable,
            ["mcp", "add", "--transport", "http", "--scope", "user", name, Endpoint, "--header", "Authorization: Bearer " + credential], ct);
        return result.ExitCode == 0;
    }

    private static async Task<string?> FindCompatibleCodexAsync(CancellationToken ct)
    {
        var candidates = new List<string>();
        var executable = FindExecutable("codex");
        if (executable is not null) candidates.Add(executable);
        if (OperatingSystem.IsWindows())
        {
            var bundled = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
            if (Directory.Exists(bundled)) candidates.AddRange(Directory.EnumerateDirectories(bundled)
                .OrderByDescending(Directory.GetLastWriteTimeUtc).Select(directory => Path.Combine(directory, "codex.exe")).Where(File.Exists));
        }
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var result = await RunCliAsync(candidate, ["mcp", "add", "--help"], ct);
            if (result.ExitCode == 0 && result.Output.Contains("--url", StringComparison.Ordinal)
                && result.Output.Contains("--bearer-token-env-var", StringComparison.Ordinal)) return candidate;
        }
        return null;
    }

    private static async Task<(int ExitCode, string Output)> RunCliAsync(string executable, string[] arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
        if (process is null) return (-1, "");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); await Task.WhenAll(stdout, stderr); return (process.ExitCode, await stdout); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); return (-1, ""); }
        // CLI output is deliberately neither logged nor returned: it can contain the header.
    }

    private static async Task<bool> InstallSkillAsync(string clientKind, string connectionPath, CancellationToken ct)
    {
        var directory = Path.Combine(UserHome(), clientKind == "Claude Code" ? ".claude" : ".agents", "skills", "lorekeeper-external-access");
        EnsureSafeDirectory(directory);
        var skill = Path.Combine(directory, "SKILL.md");
        var owner = Path.Combine(directory, ".lorekeeper-owned-sha256");
        RejectLinkedFile(skill);
        RejectLinkedFile(owner);
        if (File.Exists(skill) && (!File.Exists(owner)
            || McpClientGrantService.Hash(await File.ReadAllTextAsync(skill, ct)) != (await File.ReadAllTextAsync(owner, ct)).Trim())) return false;
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Lorekeeper.ExternalAccess.AgentSkill.SKILL.md")
            ?? throw new InvalidOperationException("The bundled Lorekeeper skill is unavailable.");
        using var reader = new StreamReader(stream);
        var source = await reader.ReadToEndAsync(ct);
        source = source.Replace("{{TRANSFER_COMMAND}}", TransferInstructions(connectionPath), StringComparison.Ordinal);
        await WritePrivateAsync(skill, source, ct);
        await WritePrivateAsync(owner, McpClientGrantService.Hash(source), ct);
        return true;
    }

    internal static string TransferInstructions(Guid clientId) => TransferInstructions(
        Path.Combine(ExternalTransferCommand.SecurityRoot, "clients", clientId.ToString("N") + ".json"));

    private static string TransferInstructions(string connectionPath)
    {
        var host = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Lorekeeper.exe" : "Lorekeeper");
        static string Quote(string value) => "'" + value.Replace("'", OperatingSystem.IsWindows() ? "''" : "'\"'\"'", StringComparison.Ordinal) + "'";
        var command = $"{(OperatingSystem.IsWindows() ? "& " : "")}{Quote(host)} --external-transfer";
        return $"```text\n{command} upload {Quote(connectionPath)} '<uploadResource>' '<local-image-file>' 33554432\n{command} download {Quote(connectionPath)} '<resource>' '<new-local-destination>' 268435456\n```\nUse the resource returned by the MCP tool. Image originals can be downloaded from `/external-access/transfers/images/<imageId>?projectId=<projectId>&workspaceId=<workspaceId>`.";
    }

    private static string UserHome() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    internal static void EnsureSafeDirectory(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("This setup location uses a linked directory. Choose the manual configuration path to preserve it.");
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void RejectLinkedFile(string path)
    {
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("This setup file is a link. Its contents were preserved.");
    }

    internal static async Task WritePrivateAsync(string path, string content, CancellationToken ct)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, Utf8, ct);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            RejectLinkedFile(path);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string? FindExecutable(string name)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        foreach (var extension in OperatingSystem.IsWindows() ? new[] { ".exe" } : new[] { "" })
        {
            var path = Path.Combine(directory, name + extension);
            if (File.Exists(path)) return path;
        }
        if (OperatingSystem.IsWindows())
        {
            var local = Path.Combine(UserHome(), ".local", "bin", name + ".exe");
            if (File.Exists(local)) return local;
        }
        return null;
    }
}
