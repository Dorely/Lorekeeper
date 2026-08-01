using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Lorekeeper.Publish;
using Microsoft.Extensions.Options;

namespace Lorekeeper.Tests;

public sealed class LorekeeperPressProcessIntegrationTests
{
    private static readonly byte[] PixelPng =
    [
        137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 0, 1, 0, 0, 0, 1,
        8, 2, 0, 0, 0, 144, 119, 83, 222, 0, 0, 0, 12, 73, 68, 65, 84, 8, 215, 99, 248, 207,
        192, 0, 0, 3, 1, 1, 0, 24, 221, 141, 176, 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130,
    ];

    [Fact]
    public async Task RuntimeSbomFingerprintsEveryLockedExternalPackage()
    {
        var repositoryRoot = FindRepositoryRoot();
        var runtimeRoot = FindBuiltRuntime(repositoryRoot);
        using var sbom = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(runtimeRoot, "sbom.json")));
        var lockedChecksums = Regex.Matches(
                await File.ReadAllTextAsync(Path.Combine(repositoryRoot, "Lorekeeper.Press", "Cargo.lock")),
                @"(?ms)^\[\[package\]\]\s*(?<body>.*?)(?=^\[\[package\]\]|\z)")
            .Select(match => match.Groups["body"].Value)
            .Select(body => new
            {
                Name = Field(body, "name"),
                Version = Field(body, "version"),
                Source = Field(body, "source"),
                Checksum = Field(body, "checksum"),
            })
            .Where(package => package.Source.Length > 0)
            .ToDictionary(
                package => (package.Name, package.Version, package.Source),
                package => package.Checksum);

        foreach (var package in sbom.RootElement.GetProperty("packages").EnumerateArray())
        {
            var source = package.GetProperty("source").GetString() ?? string.Empty;
            if (source.Length == 0)
                continue;
            var key = (
                package.GetProperty("name").GetString() ?? string.Empty,
                package.GetProperty("version").GetString() ?? string.Empty,
                source);
            Assert.True(lockedChecksums.TryGetValue(key, out var locked), $"Missing locked package {key}.");
            Assert.Equal(locked, package.GetProperty("checksum").GetString());
            Assert.Matches("^[0-9a-f]{64}$", locked);
        }
    }

    [Fact]
    public async Task PackagedRuntimeRendersRealValidatedInteriorAndCoverWithoutMachinePath()
    {
        var repositoryRoot = FindRepositoryRoot();
        var runtimeRoot = FindBuiltRuntime(repositoryRoot);
        var installationRoot = Directory.GetParent(runtimeRoot)!.FullName;
        var runtime = new PublicationPressRuntime(
            Options.Create(new PublicationPressOptions()),
            new FixtureInstallationRoot(installationRoot));
        Assert.True(runtime.GetReadiness().IsReady, runtime.GetReadiness().Message);

        var jobId = Guid.NewGuid();
        var jobRoot = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", jobId.ToString("N"));
        Directory.CreateDirectory(Path.Combine(jobRoot, "input", "assets"));
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(jobRoot, "input", "assets", "pixel.png"), PixelPng);
            var request = JsonNode.Parse(await File.ReadAllTextAsync(
                Path.Combine(repositoryRoot, "Lorekeeper.Press", "fixtures", "full-model-v3.json")))!;
            request["jobId"] = jobId.ToString("N");
            request["profile"] = "kdp-paperback-v1";
            request["assets"]![0]!["byteLength"] = PixelPng.LongLength;
            request["assets"]![0]!["sha256"] = Convert.ToHexStringLower(SHA256.HashData(PixelPng));
            await File.WriteAllTextAsync(
                Path.Combine(jobRoot, "input", "request.json"),
                request.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            var startInfo = runtime.CreateStartInfo(jobId, jobRoot);
            Assert.Empty(startInfo.Environment);
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Press did not start.");
            var standardOutput = await process.StandardOutput.ReadToEndAsync();
            var standardError = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.Equal(0, process.ExitCode);
            Assert.True(string.IsNullOrWhiteSpace(standardError), standardError);
            using var response = JsonDocument.Parse(standardOutput);
            Assert.Equal("completed", response.RootElement.GetProperty("status").GetString());
            Assert.Equal("validated", response.RootElement.GetProperty("evidence")
                .GetProperty("validationStatus").GetString());
            foreach (var name in new[] { "interior.pdf", "cover.pdf" })
            {
                var bytes = await File.ReadAllBytesAsync(Path.Combine(jobRoot, "output", name));
                Assert.True(bytes.AsSpan().StartsWith("%PDF-"u8));
                Assert.True(bytes.Length > 1_000);
            }
        }
        finally
        {
            if (Directory.Exists(jobRoot))
                Directory.Delete(jobRoot, recursive: true);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Lorekeeper.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Lorekeeper repository root not found.");
    }

    private static string FindBuiltRuntime(string repositoryRoot)
    {
        var expectedExecutable = OperatingSystem.IsWindows() ? "lorekeeper-press.exe" : "lorekeeper-press";
        return Directory.EnumerateDirectories(
                Path.Combine(repositoryRoot, "Lorekeeper", "bin", "Debug"),
                "press-runtime",
                SearchOption.AllDirectories)
            .First(path => File.Exists(Path.Combine(path, expectedExecutable)));
    }

    private static string Field(string packageBlock, string name) =>
        Regex.Match(packageBlock, $"(?m)^{Regex.Escape(name)} = \"(?<value>[^\"]*)\"$")
            .Groups["value"]
            .Value;

    private sealed class FixtureInstallationRoot(string rootPath) : IPublicationPressInstallationRoot
    {
        public string RootPath { get; } = rootPath;
    }
}
