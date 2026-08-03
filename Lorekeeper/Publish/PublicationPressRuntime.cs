using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Lorekeeper.Publish;

public sealed class PublicationPressOptions
{
    public const string SectionName = "Publishing:Press";

    public string RuntimeDirectory { get; set; } = "press-runtime";
    public int RenderTimeoutSeconds { get; set; } = 300;
}

public sealed record PublicationPressRuntimeReadiness(bool IsReady, string Message);

public sealed record PublicationPressDescription(
    int ProtocolVersion,
    string RendererVersion,
    IReadOnlyList<string> Profiles,
    JsonElement Limits,
    JsonElement Capabilities);

public interface IPublicationPressRuntime
{
    PublicationPressRuntimeReadiness GetReadiness();
    PublicationPressDescription GetDescription();
    ProcessStartInfo CreateStartInfo(Guid jobId, string jobRoot);
}

public interface IPublicationPressInstallationRoot
{
    string RootPath { get; }
}

public sealed class PublicationPressInstallationRoot : IPublicationPressInstallationRoot
{
    public string RootPath { get; } = Path.GetFullPath(AppContext.BaseDirectory);
}

public sealed class PublicationPressRuntime(
    IOptions<PublicationPressOptions> options,
    IPublicationPressInstallationRoot installationRoot) : IPublicationPressRuntime
{
    private const string ManifestFileName = "lorekeeper-press-runtime.json";

    public PublicationPressRuntimeReadiness GetReadiness()
    {
        try
        {
            var runtime = Resolve();
            return new(
                true,
                $"Lorekeeper Press {runtime.Description.RendererVersion} is ready for internally validated PDF generation.");
        }
        catch (InvalidOperationException exception)
        {
            return new(false, exception.Message);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {
            return new(false, Unavailable(exception.Message).Message);
        }
    }

    public PublicationPressDescription GetDescription() => Resolve().Description;

    public ProcessStartInfo CreateStartInfo(Guid jobId, string jobRoot)
    {
        var runtime = Resolve();
        var boundedJobRoot = Path.GetFullPath(jobRoot);
        var start = new ProcessStartInfo
        {
            FileName = runtime.ExecutablePath,
            WorkingDirectory = runtime.RootPath,
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("render");
        start.ArgumentList.Add("--job-root");
        start.ArgumentList.Add(boundedJobRoot);
        start.Environment.Clear();
        return start;
    }

    private ResolvedRuntime Resolve()
    {
        var configuredDirectory = options.Value.RuntimeDirectory.Trim();
        if (string.IsNullOrWhiteSpace(configuredDirectory))
            throw Unavailable("Publishing:Press:RuntimeDirectory is empty.");
        if (Path.IsPathRooted(configuredDirectory))
            throw Unavailable("The runtime directory must be relative to the Lorekeeper application root.");

        var applicationRoot = Path.GetFullPath(installationRoot.RootPath);
        var runtimeRoot = Path.GetFullPath(Path.Combine(applicationRoot, configuredDirectory));
        if (!IsContainedBy(applicationRoot, runtimeRoot))
            throw Unavailable("The configured runtime directory escapes the Lorekeeper application root.");
        if (!Directory.Exists(runtimeRoot)
            || File.GetAttributes(runtimeRoot).HasFlag(FileAttributes.ReparsePoint))
        {
            throw Unavailable($"The app-owned runtime folder '{configuredDirectory}' is not installed.");
        }

        var executableName = OperatingSystem.IsWindows() ? "lorekeeper-press.exe" : "lorekeeper-press";
        var executablePath = Path.Combine(runtimeRoot, executableName);
        RequireOwnedFile(runtimeRoot, executablePath, executableName);
        if (!OperatingSystem.IsWindows()
            && !File.GetUnixFileMode(executablePath).HasFlag(UnixFileMode.UserExecute))
        {
            throw Unavailable("The app-owned press executable is not marked executable for its owner.");
        }

        var manifestPath = Path.Combine(runtimeRoot, ManifestFileName);
        RequireOwnedFile(runtimeRoot, manifestPath, ManifestFileName);
        var description = VerifyManifest(runtimeRoot, manifestPath);
        return new(runtimeRoot, executablePath, description);
    }

    private static PublicationPressDescription VerifyManifest(string runtimeRoot, string manifestPath)
    {
        try
        {
            var file = new FileInfo(manifestPath);
            if (file.Length is < 2 or > 4 * 1024 * 1024)
                throw new InvalidDataException("The runtime manifest has an invalid size.");
            using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
            var root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 3)
                throw new InvalidDataException("The runtime-manifest schema is unsupported.");
            if (!string.Equals(root.GetProperty("platform").GetString(), CurrentPlatform(), StringComparison.Ordinal)
                || !string.Equals(
                    root.GetProperty("architecture").GetString(),
                    RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("The press bundle targets a different platform or architecture.");
            }

            var expected = new Dictionary<string, (string Hash, long Size)>(StringComparer.Ordinal);
            foreach (var item in root.GetProperty("files").EnumerateArray())
            {
                var relativePath = item.GetProperty("relativePath").GetString();
                var hash = item.GetProperty("sha256").GetString();
                var size = item.GetProperty("byteLength").GetInt64();
                if (string.IsNullOrWhiteSpace(relativePath)
                    || Path.IsPathRooted(relativePath)
                    || relativePath.Contains('\\', StringComparison.Ordinal)
                    || hash is null
                    || hash.Length != 64
                    || size < 1
                    || !expected.TryAdd(relativePath, (hash, size)))
                {
                    throw new InvalidDataException("The runtime manifest contains an invalid or duplicate file.");
                }
                var fullPath = Path.GetFullPath(Path.Combine(
                    runtimeRoot,
                    relativePath.Replace('/', Path.DirectorySeparatorChar)));
                RequireOwnedFile(runtimeRoot, fullPath, relativePath);
                var info = new FileInfo(fullPath);
                if (info.Length != size)
                    throw new InvalidDataException($"The runtime file '{relativePath}' has changed size.");
                VerifyHash(fullPath, hash, relativePath);
            }

            var actual = Directory.EnumerateFiles(runtimeRoot, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(runtimeRoot, path).Replace(Path.DirectorySeparatorChar, '/'))
                .Where(relativePath => !string.Equals(relativePath, ManifestFileName, StringComparison.Ordinal))
                .ToHashSet(StringComparer.Ordinal);
            if (!actual.SetEquals(expected.Keys))
                throw new InvalidDataException("The runtime folder does not exactly match its declared inventory.");

            var description = root.GetProperty("description");
            var protocol = description.GetProperty("protocolVersion").GetInt32();
            var renderer = description.GetProperty("rendererVersion").GetString() ?? string.Empty;
            var profiles = description.GetProperty("profiles").EnumerateArray()
                .Select(item => item.GetString() ?? string.Empty)
                .Where(item => item.Length > 0)
                .ToArray();
            if (protocol != 5 || renderer.Length == 0 || profiles.Length == 0)
                throw new InvalidDataException("The renderer capability contract is incomplete.");
            return new(
                protocol,
                renderer,
                profiles,
                description.GetProperty("limits").Clone(),
                description.GetProperty("capabilities").Clone());
        }
        catch (Exception exception) when (
            exception is IOException
                or JsonException
                or InvalidDataException
                or InvalidOperationException
                or KeyNotFoundException)
        {
            throw Unavailable($"The app-owned runtime manifest is invalid: {exception.Message}");
        }
    }

    private static void RequireOwnedFile(string rootPath, string filePath, string displayName)
    {
        var directory = Directory.GetParent(filePath);
        while (directory is not null && !string.Equals(directory.FullName, rootPath, StringComparison.Ordinal))
        {
            if (!IsContainedBy(rootPath, directory.FullName)
                || !directory.Exists
                || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw Unavailable($"The app-owned press runtime is incomplete: '{displayName}' traverses a missing or unsafe directory.");
            }
            directory = directory.Parent;
        }
        if (!IsContainedBy(rootPath, filePath)
            || !File.Exists(filePath)
            || File.GetAttributes(filePath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw Unavailable($"The app-owned press runtime is incomplete: '{displayName}' is missing or unsafe.");
        }
    }

    private static void VerifyHash(string path, string expectedHash, string label)
    {
        using var stream = File.OpenRead(path);
        var actualHash = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The runtime file '{label}' does not match its manifest fingerprint.");
    }

    private static string CurrentPlatform() =>
        OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsLinux() ? "linux" :
        OperatingSystem.IsMacOS() ? "macos" :
        "unsupported";

    private static bool IsContainedBy(string rootPath, string candidatePath)
    {
        var relative = Path.GetRelativePath(rootPath, candidatePath);
        return !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static InvalidOperationException Unavailable(string detail) => new(
        $"PDF generation is unavailable because Lorekeeper's owned press runtime is not ready. {detail} "
        + "Lorekeeper never falls back to machine-installed renderers or PDF software.");

    private sealed record ResolvedRuntime(
        string RootPath,
        string ExecutablePath,
        PublicationPressDescription Description);
}
