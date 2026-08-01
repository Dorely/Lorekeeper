using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Lorekeeper.Publish;

public sealed class PublicationPressOptions
{
    public const string SectionName = "Publishing:Press";

    public string RuntimeDirectory { get; set; } = "press-runtime";
    public string CmykProfileFile { get; set; } = "profiles/printing2009.icc";
    public int RenderTimeoutSeconds { get; set; } = 300;
}

public sealed record PublicationPressRuntimeReadiness(bool IsReady, string Message);

public interface IPublicationPressRuntime
{
    PublicationPressRuntimeReadiness GetReadiness(bool requireCmykProfile = false);
    ProcessStartInfo CreateStartInfo(Guid jobId, string outputRoot, bool requireCmykProfile);
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
    private static readonly string[] RequiredRelativeFiles =
    [
        "fonts/fonts.conf",
        "fonts/LiberationSerif-Regular.ttf",
        "fonts/LiberationSerif-Bold.ttf",
        "licenses/Liberation-Fonts-LICENSE.txt",
    ];

    public PublicationPressRuntimeReadiness GetReadiness(bool requireCmykProfile = false)
    {
        try
        {
            _ = Resolve(requireCmykProfile);
            return new(true, "Lorekeeper's app-owned Preview PDF runtime is ready.");
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

    public ProcessStartInfo CreateStartInfo(Guid jobId, string outputRoot, bool requireCmykProfile)
    {
        var runtime = Resolve(requireCmykProfile);
        var cacheRoot = Path.Combine(outputRoot, ".font-cache");
        Directory.CreateDirectory(cacheRoot);
        var start = new ProcessStartInfo
        {
            FileName = runtime.ExecutablePath,
            WorkingDirectory = runtime.RootPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("--output-root");
        start.ArgumentList.Add(outputRoot);
        if (runtime.CmykProfilePath is not null)
        {
            start.ArgumentList.Add("--cmyk-profile");
            start.ArgumentList.Add(runtime.CmykProfilePath);
        }

        start.Environment.Clear();
        start.Environment["FONTCONFIG_FILE"] = Path.Combine(runtime.RootPath, "fonts", "fonts.conf");
        start.Environment["FONTCONFIG_PATH"] = Path.Combine(runtime.RootPath, "fonts");
        start.Environment["XDG_CACHE_HOME"] = cacheRoot;
        start.Environment["FONTCONFIG_USE_MMAP"] = "0";
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["WEASYPRINT_DLL_DIRECTORIES"] = runtime.RootPath;
        start.Environment["LOREKEEPER_PRESS_JOB_ID"] = jobId.ToString("N");

        if (OperatingSystem.IsWindows())
        {
            var windowsRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var systemDirectory = Environment.SystemDirectory;
            if (string.IsNullOrWhiteSpace(windowsRoot)
                || string.IsNullOrWhiteSpace(systemDirectory)
                || !Directory.Exists(windowsRoot)
                || !Directory.Exists(systemDirectory))
            {
                throw new InvalidOperationException(
                    "The Windows operating-system runtime directories could not be resolved safely.");
            }
            start.Environment["SystemRoot"] = windowsRoot;
            start.Environment["PATH"] = string.Join(Path.PathSeparator, runtime.RootPath, systemDirectory, windowsRoot);
        }
        else
        {
            start.Environment["PATH"] = runtime.RootPath;
        }
        return start;
    }

    private ResolvedRuntime Resolve(bool requireCmykProfile)
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

        var executableName = OperatingSystem.IsWindows()
            ? "lorekeeper-press-weasy.exe"
            : "lorekeeper-press-weasy";
        var executablePath = Path.Combine(runtimeRoot, executableName);
        RequireOwnedFile(runtimeRoot, executablePath, executableName);
        if (!OperatingSystem.IsWindows()
            && !File.GetUnixFileMode(executablePath).HasFlag(UnixFileMode.UserExecute))
        {
            throw Unavailable("The app-owned press executable is not marked executable for its owner.");
        }
        foreach (var relativePath in RequiredRelativeFiles)
            RequireOwnedFile(runtimeRoot, Path.Combine(runtimeRoot, relativePath), relativePath);

        string? cmykProfilePath = null;
        if (requireCmykProfile)
        {
            var configuredProfile = options.Value.CmykProfileFile.Trim();
            if (string.IsNullOrWhiteSpace(configuredProfile) || Path.IsPathRooted(configuredProfile))
                throw Unavailable("The Ingram Preview profile must name an app-owned CMYK profile relative to the runtime folder.");
            cmykProfilePath = Path.GetFullPath(Path.Combine(runtimeRoot, configuredProfile));
            RequireOwnedFile(runtimeRoot, cmykProfilePath, configuredProfile);
        }
        VerifyBuildEvidence(runtimeRoot, executablePath, cmykProfilePath);
        return new(runtimeRoot, executablePath, cmykProfilePath);
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
                throw Unavailable($"The app-owned Preview PDF runtime is incomplete: '{displayName}' traverses a missing or unsafe directory.");
            }
            directory = directory.Parent;
        }
        if (!IsContainedBy(rootPath, filePath)
            || !File.Exists(filePath)
            || File.GetAttributes(filePath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw Unavailable($"The app-owned Preview PDF runtime is incomplete: '{displayName}' is missing or unsafe.");
        }
    }

    private static void VerifyBuildEvidence(
        string runtimeRoot,
        string executablePath,
        string? cmykProfilePath)
    {
        const string evidenceFileName = "lorekeeper-press-weasy-build.json";
        const string inventoryFileName = "lorekeeper-press-weasy-binaries.json";
        var evidencePath = Path.Combine(runtimeRoot, evidenceFileName);
        RequireOwnedFile(runtimeRoot, evidencePath, evidenceFileName);
        RequireOwnedFile(runtimeRoot, Path.Combine(runtimeRoot, inventoryFileName), inventoryFileName);
        try
        {
            var file = new FileInfo(evidencePath);
            if (file.Length is < 2 or > 1024 * 1024)
                throw new InvalidDataException("The build evidence has an invalid size.");
            using var document = JsonDocument.Parse(File.ReadAllBytes(evidencePath));
            var root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 2)
                throw new InvalidDataException("The build-evidence schema is unsupported.");
            if (!string.Equals(root.GetProperty("platform").GetString(), CurrentPlatform(), StringComparison.Ordinal)
                || !string.Equals(
                    root.GetProperty("architecture").GetString(),
                    RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("The press bundle targets a different platform or architecture.");
            }
            var licenseGatePassed = root.GetProperty("binaryInventory")
                .GetProperty("releaseLicenseGatePassed")
                .GetBoolean();
            if (!licenseGatePassed)
                throw new InvalidDataException("The release provisioning gate is not recorded.");
            var inventoryEvidence = root.GetProperty("binaryInventory");
            if (inventoryEvidence.GetProperty("uncontrolledBinaryCount").GetInt32() != 0)
                throw new InvalidDataException("The bundle inventory contains uncontrolled binaries.");
            VerifyHash(
                Path.Combine(runtimeRoot, inventoryFileName),
                inventoryEvidence.GetProperty("sha256").GetString(),
                "binary inventory");
            var expectedHash = root.GetProperty("executable").GetProperty("sha256").GetString();
            VerifyHash(executablePath, expectedHash, "executable");
            VerifyBundleFiles(runtimeRoot, evidenceFileName, root.GetProperty("bundleFiles"));
            if (cmykProfilePath is not null)
            {
                var profileEvidence = root.GetProperty("cmykProfile");
                var expectedProfilePath = profileEvidence.GetProperty("relativePath").GetString();
                var expectedProfileHash = profileEvidence.GetProperty("sha256").GetString();
                var actualRelativePath = Path.GetRelativePath(runtimeRoot, cmykProfilePath)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if (!string.Equals(expectedProfilePath, actualRelativePath, StringComparison.Ordinal)
                    || expectedProfileHash is null
                    || expectedProfileHash.Length != 64)
                {
                    throw new InvalidDataException("The approved CMYK profile evidence is missing or mismatched.");
                }
                VerifyHash(cmykProfilePath, expectedProfileHash, "CMYK profile");
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or JsonException
                or InvalidDataException
                or InvalidOperationException
                or KeyNotFoundException)
        {
            throw Unavailable($"The app-owned runtime build evidence is invalid: {exception.Message}");
        }
    }

    private static void VerifyBundleFiles(string runtimeRoot, string evidenceFileName, JsonElement evidence)
    {
        if (evidence.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The bundle file inventory is missing.");
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in evidence.EnumerateArray())
        {
            var relativePath = item.GetProperty("relativePath").GetString();
            var expectedHash = item.GetProperty("sha256").GetString();
            if (string.IsNullOrWhiteSpace(relativePath)
                || Path.IsPathRooted(relativePath)
                || relativePath.Contains('\\', StringComparison.Ordinal)
                || !expected.TryAdd(relativePath, expectedHash ?? string.Empty))
            {
                throw new InvalidDataException("The bundle file inventory contains an invalid or duplicate path.");
            }
            var fullPath = Path.GetFullPath(Path.Combine(runtimeRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            RequireOwnedFile(runtimeRoot, fullPath, relativePath);
            VerifyHash(fullPath, expectedHash, relativePath);
        }

        var actual = Directory.EnumerateFiles(runtimeRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(runtimeRoot, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(relativePath => !string.Equals(relativePath, evidenceFileName, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(expected.Keys))
            throw new InvalidDataException("The runtime folder does not exactly match its bundle file inventory.");
    }

    private static void VerifyHash(string path, string? expectedHash, string label)
    {
        if (expectedHash is null || expectedHash.Length != 64)
            throw new InvalidDataException($"The {label} fingerprint is missing.");
        using var stream = File.OpenRead(path);
        var actualHash = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The {label} fingerprint does not match the build evidence.");
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
        $"Preview PDF generation is unavailable because Lorekeeper's controlled press runtime is not ready. {detail} Lorekeeper will not fall back to machine-installed Python, uv, or native libraries.");

    private sealed record ResolvedRuntime(string RootPath, string ExecutablePath, string? CmykProfilePath);
}
