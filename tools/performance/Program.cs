using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lorekeeper.ImportExport;
using Lorekeeper.Manuscripts;

return PerformanceFixtureProgram.Run(args);

internal static class PerformanceFixtureProgram
{
    private const string Seed = "Lorekeeper-M0.3-v1";
    private const int ProjectWordCount = 250_000;
    private const int ProjectChapterCount = 25;
    private const int EditorBlockCount = 10_000;
    private const int EditorTextBytes = 1_048_576;
    private const int LargeSourceCount = 50;
    private const long LargeSourceBytes = 64L * 1024 * 1024;
    private static readonly DateTime ExportedAtUtc = new(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc);
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static int Run(string[] args)
    {
        var request = ParseArguments(args);
        if (request.VerifyOnly)
        {
            ValidateManifest(request.OutputDirectory);
            Console.WriteLine($"Validated {Path.Combine(request.OutputDirectory, "m0.3-fixture-manifest.json")}");
            return 0;
        }

        Directory.CreateDirectory(request.OutputDirectory);
        var files = new List<FixtureFile>
        {
            WriteJsonFixture(
                request.OutputDirectory,
                "v30-250000-word-project.json",
                CreateWordProject(),
                new Dictionary<string, long>
                {
                    ["chapters"] = ProjectChapterCount,
                    ["words"] = ProjectWordCount,
                }),
            WriteJsonFixture(
                request.OutputDirectory,
                "v30-10000-block-editor.json",
                CreateEditorStressProject(),
                new Dictionary<string, long>
                {
                    ["blocks"] = EditorBlockCount,
                    ["plainTextUtf8Bytes"] = EditorTextBytes,
                }),
        };

        if (request.IncludeLargeSources)
            files.AddRange(WriteLargeSourceLibrary(request.OutputDirectory));

        var manifest = new FixtureManifest(
            1,
            Seed,
            ProjectExportDocument.CurrentFormatVersion,
            files,
            new LargeSourceLibrary(
                LargeSourceCount,
                LargeSourceBytes,
                checked(LargeSourceCount * LargeSourceBytes),
                request.IncludeLargeSources));
        WriteUtf8(
            Path.Combine(request.OutputDirectory, "m0.3-fixture-manifest.json"),
            JsonSerializer.Serialize(manifest, ManifestJsonOptions));
        ValidateManifest(request.OutputDirectory);
        Console.WriteLine($"Generated and validated {files.Count} fixture file(s) in {request.OutputDirectory}");
        return 0;
    }

    private static FixtureRequest ParseArguments(string[] args)
    {
        string? outputDirectory = null;
        var includeLargeSources = false;
        var verifyOnly = false;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--output" when index + 1 < args.Length:
                    outputDirectory = args[++index];
                    break;
                case "--include-large-sources":
                    includeLargeSources = true;
                    break;
                case "--verify":
                    verifyOnly = true;
                    break;
                default:
                    throw new ArgumentException(
                        "Use --output <.artifacts/performance directory> [--include-large-sources] or --verify.");
            }
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("An output directory is required.");
        if (verifyOnly && includeLargeSources)
            throw new ArgumentException("--verify does not generate the optional large-source library.");

        return new FixtureRequest(RequirePerformanceArtifactDirectory(outputDirectory), includeLargeSources, verifyOnly);
    }

    private static string RequirePerformanceArtifactDirectory(string candidate)
    {
        var fullPath = Path.GetFullPath(candidate);
        for (var directory = new DirectoryInfo(fullPath); directory is not null; directory = directory.Parent)
        {
            if (!string.Equals(directory.Name, ".artifacts", StringComparison.OrdinalIgnoreCase))
                continue;

            var relative = Path.GetRelativePath(directory.FullName, fullPath);
            if (relative.Equals("performance", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith($"performance{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                return fullPath;
            }

            break;
        }

        throw new ArgumentException("Fixture output is allowed only under .artifacts/performance.", nameof(candidate));
    }

    private static ProjectExportDocument CreateWordProject()
    {
        var projectId = DeterministicGuid("word-project", 0);
        var chapters = new List<ProjectExportChapter>(ProjectChapterCount);
        var wordsPerChapter = ProjectWordCount / ProjectChapterCount;
        for (var index = 0; index < ProjectChapterCount; index++)
        {
            var chapterId = DeterministicGuid("word-chapter", index);
            var document = ManuscriptCodec.FromPlainText(
                chapterId,
                CreateWords(wordsPerChapter, index * wordsPerChapter),
                revision: 0,
                deterministicIds: true);
            chapters.Add(new ProjectExportChapter
            {
                Id = chapterId,
                Title = $"Fixture Chapter {index + 1:D2}",
                Order = index,
                ManuscriptRevision = 0,
                ManuscriptJson = ManuscriptCodec.Serialize(document),
            });
        }

        return CreateDocument(projectId, "M0.3 250000-word fixture", chapters);
    }

    private static ProjectExportDocument CreateEditorStressProject()
    {
        var projectId = DeterministicGuid("editor-project", 0);
        var chapterId = DeterministicGuid("editor-chapter", 0);
        var baseLength = EditorTextBytes / EditorBlockCount;
        var additionalByteBlocks = EditorTextBytes % EditorBlockCount;
        var blocks = new List<ManuscriptBlock>(EditorBlockCount);
        for (var index = 0; index < EditorBlockCount; index++)
        {
            var textLength = baseLength + (index < additionalByteBlocks ? 1 : 0);
            blocks.Add(new ManuscriptBlock
            {
                Id = $"fixture-block-{index + 1:D5}",
                Type = ManuscriptBlockType.Paragraph,
                StyleRole = ManuscriptStyleRoles.Body,
                Content = [new ManuscriptInline { Text = CreateAsciiText(textLength, index) }],
            });
        }

        var document = new ManuscriptDocument
        {
            ManuscriptId = chapterId,
            Revision = 0,
            Content = blocks,
        };
        return CreateDocument(projectId, "M0.3 10000-block fixture",
        [
            new ProjectExportChapter
            {
                Id = chapterId,
                Title = "Editor stress chapter",
                Order = 0,
                ManuscriptRevision = 0,
                ManuscriptJson = ManuscriptCodec.Serialize(document),
            },
        ]);
    }

    private static ProjectExportDocument CreateDocument(
        Guid projectId,
        string name,
        List<ProjectExportChapter> chapters) =>
        new()
        {
            ExportKind = ProjectExportKind.Full,
            ExportedAtUtc = ExportedAtUtc,
            Project = new ProjectExportProject(
                projectId,
                name,
                "m0-3-performance-fixture",
                string.Empty,
                true,
                true),
            Chapters = chapters,
        };

    private static FixtureFile WriteJsonFixture(
        string outputDirectory,
        string fileName,
        ProjectExportDocument document,
        IReadOnlyDictionary<string, long> counts)
    {
        var path = Path.Combine(outputDirectory, fileName);
        WriteUtf8(path, JsonSerializer.Serialize(document, ManuscriptCodec.JsonOptions));
        return DescribeFile(outputDirectory, path, counts);
    }

    private static IReadOnlyList<FixtureFile> WriteLargeSourceLibrary(string outputDirectory)
    {
        const int bufferLength = 1024 * 1024;
        var sourceDirectory = Path.Combine(outputDirectory, "v30-50-source-library");
        Directory.CreateDirectory(sourceDirectory);
        var files = new List<FixtureFile>(LargeSourceCount);
        for (var index = 0; index < LargeSourceCount; index++)
        {
            var path = Path.Combine(sourceDirectory, $"source-{index + 1:D2}.txt");
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferLength))
            {
                var buffer = CreateSourceBuffer(bufferLength, index);
                for (long remaining = LargeSourceBytes; remaining > 0; remaining -= buffer.Length)
                    stream.Write(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            }
            files.Add(DescribeFile(outputDirectory, path, new Dictionary<string, long>
            {
                ["sourceOrdinal"] = index + 1,
                ["bytes"] = LargeSourceBytes,
            }));
        }

        return files;
    }

    private static FixtureFile DescribeFile(
        string outputDirectory,
        string path,
        IReadOnlyDictionary<string, long> counts) =>
        new(
            Path.GetRelativePath(outputDirectory, path).Replace(Path.DirectorySeparatorChar, '/'),
            new FileInfo(path).Length,
            HashFile(path),
            counts);

    private static void ValidateManifest(string outputDirectory)
    {
        var manifestPath = Path.Combine(outputDirectory, "m0.3-fixture-manifest.json");
        var manifest = JsonSerializer.Deserialize<FixtureManifest>(File.ReadAllText(manifestPath), ManifestJsonOptions)
            ?? throw new InvalidDataException("The fixture manifest is empty.");
        if (manifest.FormatVersion != 1
            || !string.Equals(manifest.Seed, Seed, StringComparison.Ordinal)
            || manifest.ProjectExportFormatVersion != ProjectExportDocument.CurrentFormatVersion)
        {
            throw new InvalidDataException("The fixture manifest does not describe the M0.3 fixture contract.");
        }

        foreach (var file in manifest.Files)
        {
            var path = Path.GetFullPath(Path.Combine(outputDirectory, file.FileName));
            if (!path.StartsWith(outputDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)
                || !File.Exists(path))
            {
                throw new InvalidDataException($"Fixture manifest file is missing or escapes its output directory: {file.FileName}");
            }

            var actualLength = new FileInfo(path).Length;
            var actualHash = HashFile(path);
            if (actualLength != file.ByteLength
                || !string.Equals(actualHash, file.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Fixture manifest validation failed for {file.FileName}.");
            }
        }
    }

    private static string CreateWords(int wordCount, int startOrdinal)
    {
        var builder = new StringBuilder(wordCount * 12);
        for (var index = 0; index < wordCount; index++)
        {
            if (index > 0)
                builder.Append(' ');
            builder.Append("fixtureword");
            builder.Append((startOrdinal + index + 1).ToString("D6", System.Globalization.CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    private static string CreateAsciiText(int length, int offset)
    {
        var characters = new char[length];
        for (var index = 0; index < characters.Length; index++)
            characters[index] = (char)('a' + ((index + offset) % 26));
        return new string(characters);
    }

    private static byte[] CreateSourceBuffer(int length, int sourceOrdinal)
    {
        var buffer = new byte[length];
        for (var index = 0; index < buffer.Length; index++)
            buffer[index] = (byte)('a' + ((index + sourceOrdinal) % 26));
        return buffer;
    }

    private static Guid DeterministicGuid(string kind, int ordinal)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{Seed}:{kind}:{ordinal}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void WriteUtf8(string path, string content) =>
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    private sealed record FixtureRequest(string OutputDirectory, bool IncludeLargeSources, bool VerifyOnly);

    private sealed record FixtureManifest(
        int FormatVersion,
        string Seed,
        int ProjectExportFormatVersion,
        IReadOnlyList<FixtureFile> Files,
        LargeSourceLibrary LargeSourceLibrary);

    private sealed record FixtureFile(
        string FileName,
        long ByteLength,
        string Sha256,
        IReadOnlyDictionary<string, long> Counts);

    private sealed record LargeSourceLibrary(
        int SourceCount,
        long BytesPerSource,
        long TotalBytes,
        bool Generated);
}
