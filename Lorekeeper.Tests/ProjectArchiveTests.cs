using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lorekeeper.ProjectArchive;

namespace Lorekeeper.Tests;

public sealed class ProjectArchiveTests
{
    private const string ProjectId = "10000000-0000-0000-0000-000000000001";
    private static readonly ProjectArchiveSchemaVersions SchemaVersions = new(5, ProjectArchiveContract.RecordSchemaVersion, 8);

    [Fact]
    public void NormativeM4FixtureAgreesWithArchiveConstantsAndPolicies()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FindFixture("m4-archive-source-history-v1.json")));
        var archive = document.RootElement.GetProperty("archive");

        Assert.Equal(ProjectArchiveContract.FormatId, archive.GetProperty("formatId").GetString());
        Assert.Equal(ProjectArchiveContract.EnvelopeVersion, archive.GetProperty("envelopeVersion").GetInt32());
        var schemaVersions = archive.GetProperty("manifest").GetProperty("schemaVersions");
        Assert.Equal(ProjectId, archive.GetProperty("manifest").GetProperty("projectId").GetString());
        Assert.Equal(SchemaVersions.Manuscript, schemaVersions.GetProperty("manuscript").GetInt32());
        Assert.Equal(ProjectArchiveContract.RecordSchemaVersion, schemaVersions.GetProperty("archiveRecord").GetInt32());
        Assert.Equal(SchemaVersions.HistorySnapshot, schemaVersions.GetProperty("historySnapshot").GetInt32());
        Assert.Equal(
            Enum.GetNames<ProjectDependencyTraversalPolicy>(),
            archive.GetProperty("policies").EnumerateArray().Select(item => item.GetString()).ToArray());

        var closures = document.RootElement.GetProperty("closurePolicies").EnumerateArray()
            .ToDictionary(item => item.GetProperty("policy").GetString()!, StringComparer.Ordinal);
        Assert.Contains("sources/source-0001/original/chunk-0000", closures[nameof(ProjectDependencyTraversalPolicy.FullArchive)]
            .GetProperty("includedPaths").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(
            [ProjectArchiveWarningCodes.SourceEvidenceOmittedNonStructuralExport],
            closures[nameof(ProjectDependencyTraversalPolicy.NonStructuralArchive)]
                .GetProperty("warnings").EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.DoesNotContain("ingest jobs", closures[nameof(ProjectDependencyTraversalPolicy.HistorySnapshot)]
            .GetProperty("includedPaths").EnumerateArray().Select(item => item.GetString()));
        Assert.Empty(closures[nameof(ProjectDependencyTraversalPolicy.HistorySnapshot)]
            .GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public async Task WritesStagesAndReadsADeterministicStreamedArchive()
    {
        await using var capture = ProjectArchiveTemporaryCapture.Create();
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes("tide"));
        var descriptor = await capture.CaptureAsync(input, "sources/source-0001/original/chunk-0000", "source-original-chunk", "text/plain", 1024);
        await using var archive = new MemoryStream();

        var written = await ProjectArchiveZip.WriteAsync(archive, ProjectId, SchemaVersions, ProjectDependencyTraversalPolicy.FullArchive, [descriptor], []);
        archive.Position = 0;
        var read = await ProjectArchiveZip.ReadAsync(archive);
        archive.Position = 0;
        await using var staged = await ProjectArchiveZip.StageAsync(archive);

        Assert.Equal(written.ContentHash, read.Manifest.ContentHash);
        Assert.Equal(written.ManifestHash, read.Manifest.ManifestHash);
        Assert.Equal(written.Entries, read.Manifest.Entries);
        Assert.Equal(written.Warnings, staged.Manifest.Warnings);
        Assert.Equal("sources/source-0001/original/chunk-0000", staged.Manifest.Entries.Single().Path);
        Assert.True(File.Exists(staged.ArchiveFile.LocalPath));
    }

    [Fact]
    public async Task CaptureOwnsAStableDescriptorAfterItsInputChanges()
    {
        await using var capture = ProjectArchiveTemporaryCapture.Create();
        var inputBytes = Encoding.UTF8.GetBytes("tide");
        await using var input = new MemoryStream(inputBytes, writable: true);
        var descriptor = await capture.CaptureAsync(input, "sources/source-0001/original/chunk-0000", "source-original-chunk", "text/plain", 1024);

        inputBytes.AsSpan().Fill((byte)'x');
        await using var stored = descriptor.OpenRead();
        using var reader = new StreamReader(stored, Encoding.UTF8, leaveOpen: true);
        Assert.Equal("tide", await reader.ReadToEndAsync());
    }

    [Theory]
    [InlineData("../outside.json")]
    [InlineData("C:/outside.json")]
    [InlineData("assets\\backslash.json")]
    public async Task RejectsUnsafeZipPaths(string unsafePath)
    {
        await using var archive = CreateRawZip(zip => zip.CreateEntry(unsafePath));

        await Assert.ThrowsAsync<ProjectArchiveException>(() => ProjectArchiveZip.ReadAsync(archive));
    }

    [Fact]
    public async Task RejectsDuplicateNormalizedPathsLinksMalformedManifestHashMismatchAndFileSetMismatch()
    {
        await using (var duplicate = CreateRawZip(zip =>
        {
            zip.CreateEntry("assets/e\u0301.txt");
            zip.CreateEntry("assets/é.txt");
        }))
        {
            await Assert.ThrowsAsync<ProjectArchiveException>(() => ProjectArchiveZip.ReadAsync(duplicate));
        }

        await using (var link = CreateRawZip(zip =>
        {
            var entry = zip.CreateEntry("linked.txt");
            entry.ExternalAttributes = unchecked((int)0xA1FF0000);
        }))
        {
            await Assert.ThrowsAsync<ProjectArchiveException>(() => ProjectArchiveZip.ReadAsync(link));
        }

        await using (var manifestCollision = CreateRawZip(zip => zip.CreateEntry("MANIFEST.JSON")))
        {
            await Assert.ThrowsAsync<ProjectArchiveException>(() => ProjectArchiveZip.ReadAsync(manifestCollision));
        }

        await using (var malformed = CreateRawZip(zip =>
        {
            var manifest = zip.CreateEntry(ProjectArchiveContract.ManifestPath);
            WriteText(manifest, "{");
        }))
        {
            await Assert.ThrowsAsync<ProjectArchiveException>(() => ProjectArchiveZip.ReadAsync(malformed));
        }

        var declared = new ProjectArchiveManifestEntry("content.txt", "record", "text/plain", 4, Convert.ToHexStringLower(SHA256.HashData("tide"u8)));
        var validManifest = ProjectArchiveManifest.Create(ProjectId, SchemaVersions, ProjectDependencyTraversalPolicy.FullArchive, [declared], []);
        await using (var hashMismatch = CreateRawZip(zip =>
        {
            var content = zip.CreateEntry("content.txt");
            WriteText(content, "wave");
            WriteManifest(zip, validManifest);
        }))
        {
            await Assert.ThrowsAsync<ProjectArchiveException>(() => ProjectArchiveZip.ReadAsync(hashMismatch));
        }

        await using (var fileSetMismatch = CreateRawZip(zip =>
        {
            var content = zip.CreateEntry("unexpected.txt");
            WriteText(content, "tide");
            WriteManifest(zip, validManifest);
        }))
        {
            await Assert.ThrowsAsync<ProjectArchiveException>(() => ProjectArchiveZip.ReadAsync(fileSetMismatch));
        }
    }

    [Fact]
    public async Task RejectsDeclaredExpandedAndStagedCompressedLimitBreaches()
    {
        await using var capture = ProjectArchiveTemporaryCapture.Create();
        await using var input = new MemoryStream(new byte[4_096]);
        var descriptor = await capture.CaptureAsync(input, "content.txt", "record", "application/octet-stream", 8_192);
        await using var archive = new MemoryStream();
        await ProjectArchiveZip.WriteAsync(archive, ProjectId, SchemaVersions, ProjectDependencyTraversalPolicy.FullArchive, [descriptor], []);

        archive.Position = 0;
        await Assert.ThrowsAsync<ProjectArchiveException>(() => ProjectArchiveZip.ReadAsync(archive, new ProjectArchiveLimits { MaximumEntryBytes = 2_048, MaximumManifestBytes = 2_048 }));
        archive.Position = 0;
        await Assert.ThrowsAsync<ProjectArchiveException>(() => ProjectArchiveZip.StageAsync(archive, new ProjectArchiveLimits { MaximumCompressedBytes = 1 }));
    }

    [Fact]
    public void CanonicalManifestBindsProjectSchemaAndOrderedWarnings()
    {
        var entry = new ProjectArchiveManifestEntry("content.txt", "record", "text/plain", 4, Convert.ToHexStringLower(SHA256.HashData("tide"u8)));
        var manifest = ProjectArchiveManifest.Create(ProjectId, SchemaVersions, ProjectDependencyTraversalPolicy.NonStructuralArchive, [entry], ["SOURCE_EVIDENCE_OMITTED_NON_STRUCTURAL_EXPORT"]);

        Assert.Equal(ProjectId, manifest.ProjectId);
        Assert.Equal(SchemaVersions, manifest.SchemaVersions);
        Assert.Equal(["SOURCE_EVIDENCE_OMITTED_NON_STRUCTURAL_EXPORT"], manifest.Warnings);
        Assert.Throws<ProjectArchiveException>(() => ProjectArchiveManifest.Create(ProjectId, SchemaVersions, ProjectDependencyTraversalPolicy.NonStructuralArchive, [entry], ["Z_WARNING", "A_WARNING", "A_WARNING"]));
    }

    private static MemoryStream CreateRawZip(Action<ZipArchive> write)
    {
        var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            write(zip);
        }
        output.Position = 0;
        return output;
    }

    private static void WriteManifest(ZipArchive zip, ProjectArchiveManifest manifest)
    {
        var entry = zip.CreateEntry(ProjectArchiveContract.ManifestPath);
        using var stream = entry.Open();
        ProjectArchiveCanonicalJson.WriteManifest(stream, manifest);
    }

    private static void WriteText(ZipArchiveEntry entry, string value)
    {
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        writer.Write(value);
    }

    private static string FindFixture(string name)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "Lorekeeper.Tests", "Fixtures", "RemainingV1", name);
            if (File.Exists(candidate))
                return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException("The normative fixture was not found.", name);
    }
}
