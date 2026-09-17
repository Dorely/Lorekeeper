using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lorekeeper.Composition;
using Lorekeeper.ImportExport;
using Lorekeeper.Ingest;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.ProjectArchive;

namespace Lorekeeper.VersionHistory.Snapshots;

public interface IVersionHistorySnapshotReader
{
    VersionHistorySnapshotArtifact Read(
        string rootDirectory,
        Guid? expectedRepositoryId = null,
        Guid? expectedProjectId = null,
        VersionHistorySnapshotReadOptions? options = null);
}

public sealed record VersionHistorySnapshotReadOptions
{
    public static VersionHistorySnapshotReadOptions Default { get; } = new();

    public bool IncludeAssetData { get; init; } = true;

    /// <summary>Only restore/import callers request original source bytes.</summary>
    public bool IncludeSourceOriginalBlobs { get; init; }
}

/// <summary>
/// Strictly validates and reads a snapshot tree. It does not mutate the database,
/// rebuild projections, or execute Git operations.
/// </summary>
public sealed class VersionHistorySnapshotReader : IVersionHistorySnapshotReader
{
    public VersionHistorySnapshotArtifact Read(
        string rootDirectory,
        Guid? expectedRepositoryId = null,
        Guid? expectedProjectId = null,
        VersionHistorySnapshotReadOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new ArgumentException("A snapshot directory is required.", nameof(rootDirectory));

        var fullRoot = Path.GetFullPath(rootDirectory);
        var manifestPath = ResolveSafePath(fullRoot, VersionHistorySnapshotContract.ManifestFileName);
        if (!File.Exists(manifestPath))
            throw new InvalidDataException("Snapshot manifest.json is missing.");

        var manifestBytes = File.ReadAllBytes(manifestPath);
        var manifest = VersionHistoryCanonicalJson.Deserialize<VersionHistorySnapshotManifest>(manifestBytes);
        if (!manifestBytes.AsSpan().SequenceEqual(VersionHistoryCanonicalJson.Serialize(manifest)))
            throw new InvalidDataException("Snapshot manifest.json is not in canonical form.");
        ValidateManifest(manifest, expectedRepositoryId, expectedProjectId);

        var readOptions = options ?? VersionHistorySnapshotReadOptions.Default;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var assetFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var sourceBlobFiles = new Dictionary<string, VersionHistorySourceBlobDescriptor>(StringComparer.Ordinal);
        var validatedFiles = new Dictionary<string, ValidatedSnapshotFile>(StringComparer.Ordinal);
        using var payloadHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in manifest.Files.OrderBy(item => item.Path, StringComparer.Ordinal))
        {
            var path = NormalizeRelativePath(entry.Path);
            var fullPath = ResolveSafePath(fullRoot, path);
            if (!File.Exists(fullPath))
                throw new InvalidDataException($"Snapshot file '{path}' is missing.");

            var isAssetBlob = IsAssetBlobPath(path);
            var isSourceBlob = IsSourceBlobPath(path);
            var bytes = ValidateAndReadFile(
                fullPath,
                path,
                entry,
                payloadHash,
                retainBytes: !(isAssetBlob || isSourceBlob)
                    || isAssetBlob && readOptions.IncludeAssetData,
                out var actualHash);
            validatedFiles.Add(path, new(entry.Length, actualHash));
            if (isSourceBlob && readOptions.IncludeSourceOriginalBlobs)
                sourceBlobFiles.Add(path, new VersionHistorySourceBlobDescriptor(actualHash, entry.Length, fullPath));
            if (bytes is not null)
            {
                if (isAssetBlob)
                    assetFiles.Add(path, bytes);
                else
                    files.Add(path, bytes);
            }
        }

        var actualPayloadHash = Convert.ToHexStringLower(payloadHash.GetHashAndReset());
        if (!string.Equals(actualPayloadHash, manifest.ContentHash, StringComparison.Ordinal))
            throw new InvalidDataException("Snapshot payload content hash does not match its manifest.");

        var expectedManifestHash = VersionHistoryCanonicalJson.Sha256Hex(
            VersionHistoryCanonicalJson.Serialize(manifest with { ManifestHash = string.Empty }));
        if (!string.Equals(expectedManifestHash, manifest.ManifestHash, StringComparison.Ordinal))
            throw new InvalidDataException("Snapshot manifest hash does not match its contents.");

        var listedPaths = manifest.Files.Select(item => NormalizeRelativePath(item.Path)).ToList();
        ValidateFileSet(fullRoot, listedPaths);
        var project = ReadRequired<VersionHistorySnapshotProjectArea>(files, "project/project.json");
        var narrativeFile = ReadRequired<VersionHistorySnapshotNarrativeFile>(files, "narrative/narrative.json");
        var chapterPaths = ValidateChapterFileSet(listedPaths);
        var chapters = ReadChapters(files, chapterPaths);
        var narrative = narrativeFile.ToArea(chapters);
        var graph = ReadRequired<VersionHistorySnapshotGraphArea>(files, "graph/graph.json");
        var sources = ReadSources(files, manifest.SchemaVersion, listedPaths);
        var assets = ReadRequired<VersionHistorySnapshotAssetsArea>(files, "assets/assets.json");
        assets = AdaptAssetsForSchema(assets, manifest.SchemaVersion);
        var manuscript = ReadRequired<VersionHistorySnapshotManuscriptArea>(files, "manuscript/styles.json");
        var composition = ReadRequired<VersionHistorySnapshotCompositionArea>(files, "composition/composition.json");
        var publication = ReadRequired<VersionHistorySnapshotPublicationArea>(
            files,
            "publication/publication.json",
            requireCanonicalRoundTrip: manifest.SchemaVersion == VersionHistorySnapshotContract.SchemaVersion);
        publication = AdaptPublicationForSchema(publication, manifest.SchemaVersion);
        ValidateSchemaFileSet(listedPaths, assets, chapterPaths, sources, manifest.SchemaVersion);
        ValidateAssetBlobs(assets, validatedFiles);
        ValidateSourceBlobs(sources, validatedFiles);
        var payload = new VersionHistorySnapshotPayload(
            manifest.RepositoryId,
            manifest.ProjectId,
            project,
            narrative,
            graph,
            sources,
            assets,
            manuscript,
            composition,
            publication)
        {
            ImageData = readOptions.IncludeAssetData ? ReadImageData(assetFiles, assets) : new Dictionary<Guid, byte[]>(),
            FontFaceData = readOptions.IncludeAssetData ? ReadFontData(assetFiles, assets) : new Dictionary<Guid, byte[]>(),
            SourceOriginalBlobs = readOptions.IncludeSourceOriginalBlobs
                ? ReadSourceOriginalBlobDescriptors(sourceBlobFiles, sources)
                : new Dictionary<string, VersionHistorySourceBlobDescriptor>(StringComparer.Ordinal),
        };

        ValidateReferences(payload, manifest.SchemaVersion);
        return new VersionHistorySnapshotArtifact(fullRoot, manifest, payload);
    }

    private static byte[]? ValidateAndReadFile(
        string fullPath,
        string relativePath,
        VersionHistorySnapshotFile entry,
        IncrementalHash payloadHash,
        bool retainBytes,
        out string actualHash)
    {
        using var source = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (source.Length != entry.Length)
            throw new InvalidDataException($"Snapshot file '{relativePath}' has an unexpected length.");

        var pathBytes = Encoding.UTF8.GetBytes(relativePath);
        var lengthBytes = Encoding.UTF8.GetBytes(entry.Length.ToString(CultureInfo.InvariantCulture));
        payloadHash.AppendData(pathBytes);
        payloadHash.AppendData([0]);
        payloadHash.AppendData(lengthBytes);
        payloadHash.AppendData([0]);

        using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[]? retained = retainBytes ? new byte[checked((int)entry.Length)] : null;
        var buffer = new byte[81_920];
        var totalRead = 0L;
        while (true)
        {
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;

            var span = buffer.AsSpan(0, read);
            fileHash.AppendData(span);
            payloadHash.AppendData(span);
            if (retained is not null)
                span.CopyTo(retained.AsSpan(checked((int)totalRead)));
            totalRead += read;
        }

        if (totalRead != entry.Length)
            throw new InvalidDataException($"Snapshot file '{relativePath}' changed while it was being read.");
        actualHash = Convert.ToHexStringLower(fileHash.GetHashAndReset());
        if (!string.Equals(actualHash, entry.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException($"Snapshot file '{relativePath}' failed its SHA-256 check.");
        return retained;
    }

    private static bool IsAssetBlobPath(string path) =>
        path.StartsWith("assets/images/", StringComparison.Ordinal)
        || path.StartsWith("assets/fonts/", StringComparison.Ordinal);

    private static bool IsSourceBlobPath(string path) =>
        path.StartsWith("sources/blobs/", StringComparison.Ordinal);

    private static void ValidateManifest(
        VersionHistorySnapshotManifest manifest,
        Guid? expectedRepositoryId,
        Guid? expectedProjectId)
    {
        if (!string.Equals(manifest.FormatId, VersionHistorySnapshotContract.FormatId, StringComparison.Ordinal))
            throw new InvalidDataException($"Unsupported snapshot format '{manifest.FormatId}'.");
        if (!VersionHistorySnapshotContract.CanReadSchema(manifest.SchemaVersion))
            throw new InvalidDataException($"Unsupported snapshot schema version {manifest.SchemaVersion}.");
        if (manifest.RepositoryId == Guid.Empty || manifest.ProjectId == Guid.Empty)
            throw new InvalidDataException("Snapshot repository and project IDs are required.");
        if (expectedRepositoryId is Guid repositoryId && repositoryId != manifest.RepositoryId)
            throw new InvalidDataException("Snapshot repository ID does not match the expected repository.");
        if (expectedProjectId is Guid projectId && projectId != manifest.ProjectId)
            throw new InvalidDataException("Snapshot project ID does not match the expected project.");
        if (!manifest.IncludedAreas.SequenceEqual(VersionHistorySnapshotContract.IncludedAreas, StringComparer.Ordinal))
            throw new InvalidDataException($"Snapshot included areas do not match schema version {manifest.SchemaVersion}.");
        if (manifest.Files.Count == 0)
            throw new InvalidDataException("Snapshot contains no payload files.");
        if (manifest.Files.Any(file => string.IsNullOrWhiteSpace(file.Path) || file.Path.Equals(VersionHistorySnapshotContract.ManifestFileName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Snapshot file list contains an invalid manifest entry.");
        if (manifest.Files.Any(file => file.Path.Contains('\\')))
            throw new InvalidDataException("Snapshot paths must use canonical forward slashes.");
        if (manifest.Files.GroupBy(file => NormalizeRelativePath(file.Path), StringComparer.Ordinal).Any(group => group.Count() != 1))
            throw new InvalidDataException("Snapshot file list contains duplicate paths.");
    }

    private static void ValidateFileSet(string root, IEnumerable<string> listedFiles)
    {
        var listed = listedFiles.ToHashSet(StringComparer.Ordinal);
        var actual = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => NormalizeRelativePath(Path.GetRelativePath(root, path)))
            .Where(path => !path.Equals(VersionHistorySnapshotContract.ManifestFileName, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(listed))
        {
            var unexpected = actual.Except(listed, StringComparer.Ordinal).FirstOrDefault();
            var missing = listed.Except(actual, StringComparer.Ordinal).FirstOrDefault();
            throw new InvalidDataException($"Snapshot file set is not exact (unexpected='{unexpected}', missing='{missing}').");
        }
    }

    private static T ReadRequired<T>(
        IReadOnlyDictionary<string, byte[]> files,
        string path,
        bool requireCanonicalRoundTrip = true)
    {
        if (!files.TryGetValue(path, out var bytes))
            throw new InvalidDataException($"Snapshot payload file '{path}' is missing.");
        var value = VersionHistoryCanonicalJson.Deserialize<T>(bytes);
        if (requireCanonicalRoundTrip
            && !bytes.AsSpan().SequenceEqual(VersionHistoryCanonicalJson.Serialize(value!)))
            throw new InvalidDataException($"Snapshot payload file '{path}' is not in canonical form.");
        return value;
    }

    private static Dictionary<Guid, byte[]> ReadImageData(
        IReadOnlyDictionary<string, byte[]> files,
        VersionHistorySnapshotAssetsArea assets)
    {
        var result = new Dictionary<Guid, byte[]>();
        foreach (var image in assets.Images)
        {
            var data = files.GetValueOrDefault(image.BlobPath)
                ?? throw new InvalidDataException($"Snapshot image blob '{image.BlobPath}' was not retained.");
            result.Add(image.Id, data);
        }

        return result;
    }

    private static Dictionary<Guid, byte[]> ReadFontData(
        IReadOnlyDictionary<string, byte[]> files,
        VersionHistorySnapshotAssetsArea assets)
    {
        var result = new Dictionary<Guid, byte[]>();
        foreach (var family in assets.FontFamilies)
        foreach (var face in family.Faces)
        {
            var data = files.GetValueOrDefault(face.BlobPath)
                ?? throw new InvalidDataException($"Snapshot font blob '{face.BlobPath}' was not retained.");
            result.Add(face.Id, data);
        }

        return result;
    }

    private static VersionHistorySnapshotSourcesArea ReadSources(
        IReadOnlyDictionary<string, byte[]> files,
        int schemaVersion,
        IReadOnlyList<string> listedPaths)
    {
        if (schemaVersion <= 7)
        {
            var legacy = ReadRequired<VersionHistorySnapshotSourcesArea>(files, "sources/sources.json");
            return AdaptLegacySources(legacy, schemaVersion);
        }

        var index = ReadRequired<VersionHistorySourceIndex>(files, "sources/index.json");
        if (index.SourceIds.Distinct().Count() != index.SourceIds.Count || index.SourceIds.Any(id => id == Guid.Empty))
            throw new InvalidDataException("Snapshot source index contains duplicate or empty source IDs.");
        var retained = new List<VersionHistoryRetainedSource>(index.SourceIds.Count);
        foreach (var sourceId in index.SourceIds.OrderBy(id => id))
        {
            var manifest = ReadRequired<VersionHistoryRetainedSource>(files, SourceManifestPath(sourceId));
            if (manifest.Id != sourceId)
                throw new InvalidDataException($"Source manifest {sourceId:N} has a mismatched source ID.");
            retained.Add(manifest);
        }

        var expectedPaths = index.SourceIds.Select(SourceManifestPath).ToHashSet(StringComparer.Ordinal);
        var actualPaths = listedPaths.Where(path => path.StartsWith("sources/", StringComparison.Ordinal)
            && path.EndsWith("/source.json", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        if (!actualPaths.SetEquals(expectedPaths))
            throw new InvalidDataException("Snapshot source manifests do not exactly match the source index.");
        return new VersionHistorySnapshotSourcesArea(retained.Select(ToLegacyProjection).ToList())
        {
            RetainedSources = retained,
            UnlinkedBibliographicRecords = index.UnlinkedBibliographicRecords,
        };
    }

    // Each predecessor is deliberately routed explicitly. Manifest/file hashes
    // have already been verified before this adapter runs; this isolates old
    // aggregate source shape from the schema-v8 retained-source contract.
    private static VersionHistorySnapshotSourcesArea AdaptLegacySources(
        VersionHistorySnapshotSourcesArea sources,
        int schemaVersion) => schemaVersion switch
        {
            1 => AdaptLegacySourcesCore(sources),
            2 => AdaptLegacySourcesCore(sources),
            3 => AdaptLegacySourcesCore(sources),
            4 => AdaptLegacySourcesCore(sources),
            5 => AdaptLegacySourcesCore(sources),
            6 => AdaptLegacySourcesCore(sources),
            7 => AdaptLegacySourcesCore(sources),
            _ => throw new InvalidDataException($"Unsupported legacy source schema {schemaVersion}."),
        };

    private static VersionHistorySnapshotSourcesArea AdaptLegacySourcesCore(VersionHistorySnapshotSourcesArea sources)
    {
        if (sources.Sources.GroupBy(source => source.Id).Any(group => group.Count() != 1))
            throw new InvalidDataException("Legacy snapshot has duplicate source IDs.");
        return sources with
        {
            RetainedSources = sources.Sources.Select(FromLegacyProjection).ToList(),
        };
    }

    private static ProjectExportIngestSource ToLegacyProjection(VersionHistoryRetainedSource source)
    {
        var active = source.Extractions.SingleOrDefault(extraction => extraction.Id == source.ActiveExtractionVersionId)
            ?? source.Extractions.OrderBy(extraction => extraction.Ordinal).LastOrDefault();
        return new ProjectExportIngestSource(
            source.Id, source.Title, source.SourceKind, source.Description, source.Synopsis,
            source.UserInstructions, active?.NormalizedText ?? string.Empty, active?.ContentHash ?? string.Empty, source.SourceUrl,
            source.FinalUrl, source.CanonicalUrl, null, source.ContentType, source.SourceMetadataJson,
            default, default,
            active?.Chunks.Select(chunk => new ProjectExportIngestSourceChunk(chunk.Id, chunk.Index, chunk.Title,
                chunk.HeadingPath, chunk.StartChar, chunk.EndChar, chunk.EstimatedTokenCount,
                chunk.TokenCountMethod, chunk.TokenEncodingName, chunk.TokenCountIsExact, chunk.Summary,
                chunk.AgentNotes, chunk.StructureStatus, default, default)).ToList() ?? [],
            active?.Pages.Select(page => new ProjectExportIngestSourcePage(page.Id, page.PageNumber, page.Text,
                page.StartChar, page.EndChar, page.ExtractionMethod, page.Width, page.Height, page.ImageHash,
                page.RenderSettingsJson, null, page.VisionModelName, page.Diagnostics, default)).ToList() ?? [],
            active?.Blocks.Select(block => new ProjectExportIngestSourceBlock(block.Id, block.SourcePageId,
                block.Index, block.Kind, block.Title, block.Locator, block.PageNumber, block.StartChar,
                block.EndChar, block.MetadataJson, default)).ToList() ?? []);
    }

    private static VersionHistoryRetainedSource FromLegacyProjection(ProjectExportIngestSource source)
    {
        var extraction = new VersionHistorySourceExtraction(source.Id, 0, "legacy-history", "pre-v8", "{}",
            source.SourceHash, SourceExtractionStatus.LegacyImmutable, "Migrated from schema-v1-v7 source aggregate.",
            source.SourceText,
            source.Chunks.Select(chunk => new VersionHistorySourceChunk(chunk.Id, chunk.Index, chunk.Title,
                chunk.HeadingPath, chunk.StartChar, chunk.EndChar, chunk.EstimatedTokenCount,
                chunk.TokenCountMethod, chunk.TokenEncodingName, chunk.TokenCountIsExact, chunk.Summary,
                chunk.AgentNotes, chunk.StructureStatus)).ToList(),
            source.Pages.Select(page => new VersionHistorySourcePage(page.Id, page.PageNumber, page.Text,
                page.StartChar, page.EndChar, page.ExtractionMethod, page.Width, page.Height, page.ImageHash,
                page.RenderSettingsJson, page.VisionModelName, page.Diagnostics)).ToList(),
            source.Blocks.Select(block => new VersionHistorySourceBlock(block.Id, block.SourcePageId, block.Index,
                block.Kind, block.Title, block.Locator, block.PageNumber, block.StartChar, block.EndChar,
                string.Empty, string.Empty, block.MetadataJson)).ToList());
        return new VersionHistoryRetainedSource(source.Id, source.Title, source.SourceKind, source.Description,
            source.Synopsis, source.UserInstructions, source.SourceUrl,
            source.FinalUrl, source.CanonicalUrl, source.ContentType, source.SourceMetadataJson, source.Id,
            new VersionHistorySourceOriginal(SourceOriginalState.OriginalUnavailable, source.Title,
                source.ContentType, 0, null, []), [extraction], [], []);
    }

    private static void ValidateSourceBlobs(
        VersionHistorySnapshotSourcesArea sources,
        IReadOnlyDictionary<string, ValidatedSnapshotFile> files)
    {
        var references = sources.RetainedSources.SelectMany(source => source.Original.Chunks).ToList();
        if (references.GroupBy(reference => reference.Id).Any(group => group.Count() != 1)
            || references.Any(reference => reference.ByteLength <= 0 || reference.ByteLength > SourceOriginal.MaximumChunkBytes))
        {
            throw new InvalidDataException("Snapshot contains invalid original source chunk references.");
        }
        foreach (var source in sources.RetainedSources)
        {
            if (source.Original.State == SourceOriginalState.OriginalUnavailable)
            {
                if (source.Original.Length != 0 || !string.IsNullOrEmpty(source.Original.Sha256) || source.Original.Chunks.Count != 0)
                    throw new InvalidDataException($"Unavailable original {source.Id:N} contains byte claims.");
                continue;
            }
            if (source.Original.Chunks.Sum(chunk => (long)chunk.ByteLength) != source.Original.Length
                || string.IsNullOrWhiteSpace(source.Original.Sha256))
            {
                throw new InvalidDataException($"Source original {source.Id:N} has invalid length or hash metadata.");
            }
            foreach (var chunk in source.Original.Chunks)
                ValidateAssetBlob(files, SourceBlobPath(chunk.BlobSha256), chunk.BlobSha256, chunk.ByteLength, "source");
        }
    }

    private static Dictionary<string, VersionHistorySourceBlobDescriptor> ReadSourceOriginalBlobDescriptors(
        IReadOnlyDictionary<string, VersionHistorySourceBlobDescriptor> files,
        VersionHistorySnapshotSourcesArea sources)
    {
        var result = new Dictionary<string, VersionHistorySourceBlobDescriptor>(StringComparer.Ordinal);
        foreach (var hash in sources.RetainedSources.SelectMany(source => source.Original.Chunks)
                     .Select(chunk => chunk.BlobSha256).Distinct(StringComparer.Ordinal))
        {
            var path = SourceBlobPath(hash);
            result.Add(hash, files.GetValueOrDefault(path)
                ?? throw new InvalidDataException($"Snapshot source blob '{path}' was not retained."));
        }
        return result;
    }

    private static string SourceManifestPath(Guid sourceId) => $"sources/{sourceId:N}/source.json";

    private static string SourceBlobPath(string sha256) => $"sources/blobs/{sha256}.bin";

    private static void ValidateAssetBlobs(
        VersionHistorySnapshotAssetsArea assets,
        IReadOnlyDictionary<string, ValidatedSnapshotFile> files)
    {
        foreach (var image in assets.Images)
            ValidateAssetBlob(files, image.BlobPath, image.Sha256, image.ByteLength, "image");
        foreach (var family in assets.FontFamilies)
        foreach (var face in family.Faces)
            ValidateAssetBlob(files, face.BlobPath, face.Sha256, face.ByteLength, "font");
    }

    private static void ValidateAssetBlob(
        IReadOnlyDictionary<string, ValidatedSnapshotFile> files,
        string path,
        string expectedHash,
        long expectedLength,
        string kind)
    {
        if (!files.TryGetValue(path, out var file))
            throw new InvalidDataException($"Snapshot {kind} blob '{path}' is missing.");
        if (file.Length != expectedLength)
            throw new InvalidDataException($"Snapshot {kind} blob '{path}' has an unexpected length.");
        if (!string.Equals(file.Sha256, expectedHash, StringComparison.Ordinal))
            throw new InvalidDataException($"Snapshot {kind} blob '{path}' failed its SHA-256 check.");
    }

    private static VersionHistorySnapshotAssetsArea AdaptAssetsForSchema(
        VersionHistorySnapshotAssetsArea assets,
        int schemaVersion)
    {
        if (schemaVersion >= VersionHistorySnapshotContract.ImageUpscaleSchemaVersion)
            return assets;

        return assets with
        {
            Images = assets.Images
                .Select(image => image with
                {
                    Source = ProjectExportImageCompatibility.AdaptLegacySource(
                        image.Source,
                        image.SourceMetadataJson),
                })
                .ToList(),
        };
    }

    private static VersionHistorySnapshotPublicationArea AdaptPublicationForSchema(
        VersionHistorySnapshotPublicationArea publication,
        int schemaVersion)
    {
        if (schemaVersion >= VersionHistorySnapshotContract.CoverDescriptionSchemaVersion)
            return publication;

        static ProjectExportCoverDesign? Adapt(ProjectExportCoverDesign? cover) => cover is null
            ? null
            : cover with
            {
                CompositionSceneJson = LegacyCoverTextBindingMigration.AdaptSceneJson(cover.CompositionSceneJson),
                SurfaceScenesJson = LegacyCoverTextBindingMigration.AdaptSurfaceScenesJson(cover.SurfaceScenesJson),
                LegacyBackCopy = null,
            };

        return publication with
        {
            PublicationBook = publication.PublicationBook is null
                ? null
                : publication.PublicationBook with
                {
                    CoverDesign = Adapt(publication.PublicationBook.CoverDesign),
                },
            PublicationEditions = publication.PublicationEditions
                .Select(edition => edition with { CoverDesign = Adapt(edition.CoverDesign) })
                .ToList(),
        };
    }

    private static IReadOnlyDictionary<Guid, ChapterFilePaths> ValidateChapterFileSet(
        IEnumerable<string> actualFiles)
    {
        var entries = new Dictionary<Guid, (string? MetadataPath, string? ManuscriptPath)>();
        foreach (var path in actualFiles.Where(path =>
                     path.StartsWith("narrative/chapters", StringComparison.OrdinalIgnoreCase)))
        {
            var segments = path.Split('/');
            if (segments.Length != 4
                || !string.Equals(segments[0], "narrative", StringComparison.Ordinal)
                || !string.Equals(segments[1], "chapters", StringComparison.Ordinal)
                || !Guid.TryParseExact(segments[2], "N", out var chapterId)
                || chapterId == Guid.Empty
                || !string.Equals(segments[2], chapterId.ToString("N"), StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Snapshot chapter path '{path}' is not in the canonical chapter layout.");
            }

            var current = entries.GetValueOrDefault(chapterId);
            if (string.Equals(segments[3], "chapter.json", StringComparison.Ordinal))
            {
                if (current.MetadataPath is not null)
                    throw new InvalidDataException($"Snapshot contains duplicate chapter metadata for {chapterId:N}.");
                current.MetadataPath = path;
            }
            else if (string.Equals(segments[3], "manuscript.json", StringComparison.Ordinal))
            {
                if (current.ManuscriptPath is not null)
                    throw new InvalidDataException($"Snapshot contains duplicate chapter manuscript for {chapterId:N}.");
                current.ManuscriptPath = path;
            }
            else
            {
                throw new InvalidDataException($"Snapshot chapter path '{path}' is not a declared chapter file.");
            }

            entries[chapterId] = current;
        }

        var result = new Dictionary<Guid, ChapterFilePaths>();
        foreach (var (chapterId, paths) in entries)
        {
            if (paths.MetadataPath is null || paths.ManuscriptPath is null)
            {
                throw new InvalidDataException(
                    $"Snapshot chapter {chapterId:N} must contain exactly chapter.json and manuscript.json.");
            }

            result.Add(chapterId, new ChapterFilePaths(chapterId, paths.MetadataPath, paths.ManuscriptPath));
        }

        return result;
    }

    private static IReadOnlyList<ProjectExportChapter> ReadChapters(
        IReadOnlyDictionary<string, byte[]> files,
        IReadOnlyDictionary<Guid, ChapterFilePaths> chapterPaths)
    {
        var chapters = new List<ProjectExportChapter>(chapterPaths.Count);
        foreach (var (chapterId, paths) in chapterPaths.OrderBy(item => item.Key))
        {
            var metadata = ReadRequired<VersionHistorySnapshotChapter>(files, paths.MetadataPath);
            if (metadata.Id != chapterId)
            {
                throw new InvalidDataException(
                    $"Snapshot chapter path ID {chapterId:N} does not match chapter metadata ID {metadata.Id:N}.");
            }

            var manuscriptJson = VersionHistoryCanonicalJson.DeserializeDirectManuscript(
                files[paths.ManuscriptPath]);
            try
            {
                _ = ManuscriptCodec.Deserialize(
                    manuscriptJson,
                    metadata.Id,
                    metadata.ManuscriptRevision);
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidDataException(
                    $"Snapshot chapter {chapterId:N} manuscript does not match its chapter metadata.",
                    exception);
            }
            chapters.Add(metadata.ToProjectExportChapter(manuscriptJson));
        }

        return chapters;
    }

    private static void ValidateSchemaFileSet(
        IEnumerable<string> actualFiles,
        VersionHistorySnapshotAssetsArea assets,
        IReadOnlyDictionary<Guid, ChapterFilePaths> chapterPaths,
        VersionHistorySnapshotSourcesArea sources,
        int schemaVersion)
    {
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "project/project.json",
            "narrative/narrative.json",
            "graph/graph.json",
            "assets/assets.json",
            "manuscript/styles.json",
            "composition/composition.json",
            "publication/publication.json",
        };

        if (schemaVersion <= 7)
            expected.Add("sources/sources.json");
        else
        {
            expected.Add("sources/index.json");
            foreach (var source in sources.RetainedSources)
            {
                expected.Add(SourceManifestPath(source.Id));
                foreach (var originalChunk in source.Original.Chunks)
                    expected.Add(SourceBlobPath(originalChunk.BlobSha256));
            }
        }

        foreach (var paths in chapterPaths.Values)
        {
            if (!expected.Add(paths.MetadataPath) || !expected.Add(paths.ManuscriptPath))
                throw new InvalidDataException($"Snapshot chapter {paths.ChapterId:N} contains duplicate paths.");
        }

        foreach (var image in assets.Images)
        {
            var prefix = $"assets/images/{image.Id:N}/content.";
            if (image.Id == Guid.Empty
                || image.BlobPath.Contains('\\')
                || !image.BlobPath.StartsWith(prefix, StringComparison.Ordinal)
                || image.BlobPath.Length == prefix.Length
                || image.BlobPath[prefix.Length..].Contains('/'))
            {
                throw new InvalidDataException($"Image {image.Id:N} has a noncanonical blob path.");
            }
            if (!expected.Add(image.BlobPath))
                throw new InvalidDataException($"Snapshot blob path '{image.BlobPath}' is duplicated.");
        }

        foreach (var family in assets.FontFamilies)
        foreach (var face in family.Faces)
        {
            var prefix = $"assets/fonts/{family.Id:N}/faces/{face.Id:N}/content.";
            if (family.Id == Guid.Empty
                || face.Id == Guid.Empty
                || face.BlobPath.Contains('\\')
                || !face.BlobPath.StartsWith(prefix, StringComparison.Ordinal)
                || face.BlobPath.Length == prefix.Length
                || face.BlobPath[prefix.Length..].Contains('/'))
            {
                throw new InvalidDataException($"Font face {face.Id:N} has a noncanonical blob path.");
            }
            if (!expected.Add(face.BlobPath))
                throw new InvalidDataException($"Snapshot blob path '{face.BlobPath}' is duplicated.");
        }

        if (!expected.SetEquals(actualFiles))
            throw new InvalidDataException(
                "Snapshot payload files do not exactly match the canonical area, chapter, style, and asset paths.");
    }

    private static void ValidateReferences(VersionHistorySnapshotPayload payload, int schemaVersion)
    {
        if (payload.Project.Project.Id != payload.ProjectId)
            throw new InvalidDataException("Snapshot project payload ID does not match its manifest.");
        if (payload.Project.Project.Id == Guid.Empty)
            throw new InvalidDataException("Snapshot project ID is empty.");
        var chapterIdList = payload.Narrative.Chapters.Select(chapter => chapter.Id).ToList();
        if (chapterIdList.Distinct().Count() != chapterIdList.Count)
            throw new InvalidDataException("Snapshot contains duplicate chapter IDs.");
        var chapterIds = chapterIdList.ToHashSet();
        var actIds = payload.Narrative.Acts.Select(act => act.Id).ToHashSet();
        foreach (var chapter in payload.Narrative.Chapters)
        {
            if (chapter.ActId is Guid actId && !actIds.Contains(actId))
                throw new InvalidDataException($"Chapter {chapter.Id:N} references missing act {actId:N}.");
        }

        var imageIds = payload.Assets.Images.Select(image => image.Id).ToHashSet();
        if (imageIds.Count != payload.Assets.Images.Count)
            throw new InvalidDataException("Snapshot contains duplicate image IDs.");
        if (payload.Assets.Images.Select(image => image.BlobPath).Distinct(StringComparer.Ordinal).Count() != payload.Assets.Images.Count)
            throw new InvalidDataException("Snapshot contains duplicate image blob paths.");
        foreach (var image in payload.Assets.Images)
        {
            if (image.DerivedFromImageId is Guid sourceImageId && !imageIds.Contains(sourceImageId))
                throw new InvalidDataException(
                    $"Image {image.Id:N} references missing source image {sourceImageId:N}.");
        }
        ValidateImageLineageAcyclic(payload.Assets.Images);
        var fontFaceIds = payload.Assets.FontFamilies
            .SelectMany(family => family.Faces)
            .Select(face => face.Id)
            .ToList();
        if (fontFaceIds.Distinct().Count() != fontFaceIds.Count)
            throw new InvalidDataException("Snapshot contains duplicate font-face IDs.");

        foreach (var example in payload.Assets.EntityVisualExamples)
        {
            if (!imageIds.Contains(example.ImageId))
                throw new InvalidDataException($"Entity visual references missing image {example.ImageId:N}.");
        }

        foreach (var preference in payload.Narrative.ContextPreferences)
        {
            if (!chapterIds.Contains(preference.ChapterId))
                throw new InvalidDataException($"Context preference references missing chapter {preference.ChapterId:N}.");
        }

        var nodeKeys = payload.Graph.Nodes
            .Select(node => $"{node.NodeType}/{node.Key}")
            .Concat(["Project/" + payload.ProjectId.ToString("N")])
            .Concat(payload.Narrative.Acts.Select(act => "Act/" + act.Id.ToString("N")))
            .Concat(payload.Narrative.Chapters.Select(chapter => "Chapter/" + chapter.Id.ToString("N")))
            .Concat(payload.Sources.Sources.Select(source => "Source/" + source.Id.ToString("N")))
            .Concat(payload.Sources.Sources.SelectMany(source =>
                source.Chunks.Select(chunk => "SourceChunk/" + chunk.Id.ToString("N"))))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var edge in payload.Graph.Edges)
        {
            if (!nodeKeys.Contains(edge.From.StableKey) || !nodeKeys.Contains(edge.To.StableKey))
                throw new InvalidDataException($"Graph edge '{edge.From.StableKey} -> {edge.To.StableKey}' references a missing node.");
        }
        foreach (var example in payload.Assets.EntityVisualExamples)
        {
            if (!nodeKeys.Contains(example.Entity.StableKey))
                throw new InvalidDataException($"Entity visual references missing graph entity '{example.Entity.StableKey}'.");
        }

        var sourceIds = payload.Sources.Sources.Select(source => source.Id).ToHashSet();
        if (sourceIds.Count != payload.Sources.Sources.Count)
            throw new InvalidDataException("Snapshot contains duplicate source IDs.");
        if (payload.Narrative.BookBriefCanonSourceIds.Any(sourceId => !sourceIds.Contains(sourceId)))
            throw new InvalidDataException("Book Brief canonical-source selection references a missing source.");

        var retainedExtractionIds = new HashSet<Guid>();
        var retainedChunkIds = new HashSet<Guid>();
        var retainedPageIds = new HashSet<Guid>();
        var retainedBlockIds = new HashSet<Guid>();
        var retainedLocationIds = new HashSet<Guid>();
        foreach (var source in payload.Sources.RetainedSources)
        {
            var extractionIds = source.Extractions.Select(extraction => extraction.Id).ToHashSet();
            if (source.ActiveExtractionVersionId is Guid activeId && !extractionIds.Contains(activeId))
                throw new InvalidDataException($"Source {source.Id:N} references a missing active extraction.");
            if (source.Extractions.GroupBy(extraction => extraction.Id).Any(group => group.Count() != 1)
                || source.Extractions.Any(extraction => extraction.Chunks.GroupBy(chunk => chunk.Id).Any(group => group.Count() != 1)
                    || extraction.Pages.GroupBy(page => page.Id).Any(group => group.Count() != 1)
                    || extraction.Blocks.GroupBy(block => block.Id).Any(group => group.Count() != 1)
                    || extraction.Blocks.Any(block => block.SourcePageId is Guid pageId
                        && !extraction.Pages.Any(page => page.Id == pageId))))
            {
                throw new InvalidDataException($"Source {source.Id:N} has duplicate or dangling extraction content.");
            }
            foreach (var location in source.Locations)
            {
                if (location.SourceId != source.Id || !extractionIds.Contains(location.ExtractionVersionId))
                    throw new InvalidDataException($"Source location {location.Id:N} has a dangling source or extraction.");
                var extraction = source.Extractions.Single(item => item.Id == location.ExtractionVersionId);
                if (location.SourceBlockId is Guid blockId && !extraction.Blocks.Any(block => block.Id == blockId))
                    throw new InvalidDataException($"Source location {location.Id:N} has a dangling source block.");
            }

            if (schemaVersion >= 8)
            {
                ValidateRetainedSource(
                    source,
                    retainedExtractionIds,
                    retainedChunkIds,
                    retainedPageIds,
                    retainedBlockIds,
                    retainedLocationIds);
            }
        }
        var bibliography = payload.Sources.RetainedSources.SelectMany(source => source.BibliographicRecords)
            .Concat(payload.Sources.UnlinkedBibliographicRecords)
            .ToList();
        if (bibliography.GroupBy(record => record.Id).Any(group => group.Count() != 1)
            || bibliography.Any(record => record.Id == Guid.Empty
                || string.IsNullOrWhiteSpace(record.Title)
                || record.SourceId is Guid sourceId && !sourceIds.Contains(sourceId)))
        {
            throw new InvalidDataException("Snapshot contains invalid, duplicate, or dangling bibliographic records.");
        }

        if (schemaVersion >= VersionHistorySnapshotContract.DesignedPagesSchemaVersion)
            ValidateDesignedPages(payload);
    }

    private static void ValidateRetainedSource(
        VersionHistoryRetainedSource source,
        ISet<Guid> extractionIds,
        ISet<Guid> chunkIds,
        ISet<Guid> pageIds,
        ISet<Guid> blockIds,
        ISet<Guid> locationIds)
    {
        if (source.Id == Guid.Empty
            || string.IsNullOrWhiteSpace(source.Title)
            || source.Extractions.Count == 0
            || source.ActiveExtractionVersionId is not Guid activeId)
        {
            throw new InvalidDataException("Snapshot retained source metadata is incomplete.");
        }

        var active = source.Extractions.Single(extraction => extraction.Id == activeId);
        if (active.Status is not (SourceExtractionStatus.Ready or SourceExtractionStatus.LegacyImmutable))
            throw new InvalidDataException($"Source {source.Id:N} has a non-readable active extraction.");

        if (source.Original.State == SourceOriginalState.OriginalUnavailable)
        {
            if (source.Original.Length != 0
                || source.Original.Sha256 is not null
                || source.Original.Chunks.Count != 0)
            {
                throw new InvalidDataException($"Unavailable original {source.Id:N} contains byte claims.");
            }
        }
        else if (source.Original.Length <= 0
            || !ProjectArchiveManifest.IsSha256(source.Original.Sha256 ?? string.Empty)
            || source.Original.Chunks.Count == 0
            || !source.Original.Chunks.OrderBy(chunk => chunk.Index)
                .Select(chunk => chunk.Index)
                .SequenceEqual(Enumerable.Range(0, source.Original.Chunks.Count)))
        {
            throw new InvalidDataException($"Available original {source.Id:N} has invalid metadata or chunk ordering.");
        }

        if (source.Extractions.GroupBy(extraction => extraction.Ordinal).Any(group => group.Count() != 1))
            throw new InvalidDataException($"Source {source.Id:N} contains duplicate extraction ordinals.");

        foreach (var extraction in source.Extractions)
        {
            if (extraction.Id == Guid.Empty
                || !extractionIds.Add(extraction.Id)
                || extraction.Ordinal < 0
                || string.IsNullOrWhiteSpace(extraction.Extractor)
                || string.IsNullOrWhiteSpace(extraction.ExtractorVersion)
                || string.IsNullOrWhiteSpace(extraction.ContentHash)
                || extraction.Status == SourceExtractionStatus.Ready
                    && (!ProjectArchiveManifest.IsSha256(extraction.ContentHash)
                        || !string.Equals(SourceRetentionValidator.Sha256(extraction.NormalizedText), extraction.ContentHash, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException($"Source {source.Id:N} contains invalid extraction metadata.");
            }

            var length = extraction.NormalizedText.Length;
            if (!extraction.Chunks.OrderBy(chunk => chunk.Index).Select(chunk => chunk.Index)
                    .SequenceEqual(Enumerable.Range(0, extraction.Chunks.Count))
                || !extraction.Blocks.OrderBy(block => block.Index).Select(block => block.Index)
                    .SequenceEqual(Enumerable.Range(0, extraction.Blocks.Count))
                || extraction.Pages.GroupBy(page => page.PageNumber).Any(group => group.Count() != 1))
            {
                throw new InvalidDataException($"Source extraction {extraction.Id:N} has invalid child ordering.");
            }

            foreach (var chunk in extraction.Chunks)
            {
                if (chunk.Id == Guid.Empty || !chunkIds.Add(chunk.Id) || !ValidRange(chunk.StartChar, chunk.EndChar, length))
                    throw new InvalidDataException($"Source extraction {extraction.Id:N} contains an invalid chunk.");
            }
            foreach (var page in extraction.Pages)
            {
                if (page.Id == Guid.Empty || !pageIds.Add(page.Id) || page.PageNumber <= 0 || !ValidRange(page.StartChar, page.EndChar, length))
                    throw new InvalidDataException($"Source extraction {extraction.Id:N} contains an invalid page.");
            }
            foreach (var block in extraction.Blocks)
            {
                var rangeValid = ValidRange(block.StartChar, block.EndChar, length);
                var blockTextMatches = rangeValid && (extraction.Status == SourceExtractionStatus.LegacyImmutable
                    ? block.NormalizedText.Length <= block.EndChar - block.StartChar
                        && string.Equals(
                            extraction.NormalizedText.Substring(block.StartChar, block.NormalizedText.Length),
                            block.NormalizedText,
                            StringComparison.Ordinal)
                        && (string.IsNullOrEmpty(block.ContentHash)
                            || string.Equals(SourceRetentionValidator.Sha256(block.NormalizedText), block.ContentHash, StringComparison.OrdinalIgnoreCase))
                    : string.Equals(extraction.NormalizedText[block.StartChar..block.EndChar], block.NormalizedText, StringComparison.Ordinal)
                        && string.Equals(SourceRetentionValidator.Sha256(block.NormalizedText), block.ContentHash, StringComparison.OrdinalIgnoreCase));
                if (block.Id == Guid.Empty
                    || !blockIds.Add(block.Id)
                    || !rangeValid
                    || block.SourcePageId is Guid sourcePageId && extraction.Pages.All(page => page.Id != sourcePageId)
                    || block.PageNumber is int pageNumber && extraction.Pages.All(page => page.PageNumber != pageNumber)
                    || !blockTextMatches)
                {
                    throw new InvalidDataException($"Source extraction {extraction.Id:N} contains an invalid block.");
                }
            }
        }

        foreach (var location in source.Locations)
        {
            if (location.Id == Guid.Empty || !locationIds.Add(location.Id))
                throw new InvalidDataException($"Source {source.Id:N} contains a duplicate or empty source location ID.");
            var extraction = source.Extractions.Single(item => item.Id == location.ExtractionVersionId);
            var block = location.SourceBlockId is Guid blockId
                ? extraction.Blocks.Single(item => item.Id == blockId)
                : null;
            var end = (long)location.NormalizedStart + location.NormalizedLength;
            if (location.NormalizedStart < 0
                || location.NormalizedLength < 0
                || end > extraction.NormalizedText.Length
                || location.PageNumber is int pageNumber && extraction.Pages.All(page => page.PageNumber != pageNumber))
            {
                throw new InvalidDataException($"Source location {location.Id:N} has an invalid range or page.");
            }
            var quote = extraction.NormalizedText.Substring(location.NormalizedStart, location.NormalizedLength);
            if (!string.Equals(quote, location.Quote, StringComparison.Ordinal)
                || !string.Equals(SourceRetentionValidator.Sha256(quote), location.VerificationHash, StringComparison.OrdinalIgnoreCase)
                || block is not null && (location.NormalizedStart < block.StartChar || end > block.EndChar))
            {
                throw new InvalidDataException($"Source location {location.Id:N} failed quote, hash, or block containment validation.");
            }
        }

        if (source.BibliographicRecords.Any(record => record.SourceId != source.Id))
            throw new InvalidDataException($"Source {source.Id:N} contains a bibliographic record owned by another source.");
    }

    private static bool ValidRange(int start, int end, int maximum) =>
        start >= 0 && end >= start && end <= maximum;

    private static void ValidateDesignedPages(VersionHistorySnapshotPayload payload)
    {
        var pages = payload.Composition.DesignedPages;
        if (pages.GroupBy(page => page.Id).Any(group => group.Count() != 1)
            || pages.Any(page => page.Id == Guid.Empty || string.IsNullOrWhiteSpace(page.Name)))
        {
            throw new InvalidDataException("Snapshot contains invalid or duplicate Designed Pages.");
        }

        var pageIds = pages.Select(page => page.Id).ToHashSet();
        var contentIds = new HashSet<Guid>();
        foreach (var page in pages)
        foreach (var content in page.Contents)
        {
            if (content.Id == Guid.Empty || content.DesignedPageId != page.Id || !contentIds.Add(content.Id))
                throw new InvalidDataException($"Designed Page {page.Id:N} contains invalid content identity.");
            if (content.Variants.GroupBy(variant => variant.GeometryKey, StringComparer.Ordinal)
                .Any(group => group.Count() != 1))
            {
                throw new InvalidDataException($"Designed Page {page.Id:N} contains duplicate geometry variants.");
            }
            if (content.ActiveVariantId is Guid activeVariantId
                && !content.Variants.Any(variant => variant.Id == activeVariantId))
            {
                throw new InvalidDataException($"Designed Page {page.Id:N} references a missing active variant.");
            }
        }

        var manuscriptPayloads = payload.Narrative.Chapters
            .Select(chapter => (chapter.ManuscriptJson, chapter.Id, chapter.ManuscriptRevision))
            .Concat(payload.Publication.PublicationSections.Select(section => (section.ManuscriptJson, section.Id, section.Revision)));
        foreach (var (manuscript, manuscriptId, revision) in manuscriptPayloads)
        {
            var document = ManuscriptCodec.Deserialize(manuscript, manuscriptId, revision);
            foreach (var block in document.Content.Where(block => block.Type == ManuscriptBlockType.DesignedPage))
            {
                if (block.DesignedPageId is not Guid pageId || !pageIds.Contains(pageId))
                    throw new InvalidDataException($"Designed Page block {block.Id} references a missing page.");
            }
        }
    }

    private static void ValidateImageLineageAcyclic(IReadOnlyList<VersionHistoryImageAsset> images)
    {
        var parentByImageId = images
            .Where(image => image.DerivedFromImageId is not null)
            .ToDictionary(image => image.Id, image => image.DerivedFromImageId!.Value);
        foreach (var image in images)
        {
            var visited = new HashSet<Guid>();
            var current = image.Id;
            while (parentByImageId.TryGetValue(current, out var parentId))
            {
                if (!visited.Add(current))
                    throw new InvalidDataException($"Image lineage contains a cycle involving image '{current:N}'.");
                current = parentId;
            }
        }
    }

    private static string ResolveSafePath(string root, string relativePath)
    {
        if (!IsSafeRelativePath(relativePath))
            throw new InvalidDataException($"Unsafe snapshot path '{relativePath}'.");
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Snapshot path escapes its root: '{relativePath}'.");
        return fullPath;
    }

    private static bool IsSafeRelativePath(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && !Path.IsPathRooted(path)
        && path.Replace('\\', '/').Split('/').All(segment => segment is not ("" or "." or ".."))
        && !path.Contains('\0');

    private static string NormalizeRelativePath(string path) =>
        path.Replace('\\', '/');

    private sealed record ValidatedSnapshotFile(long Length, string Sha256);

    private sealed record ChapterFilePaths(
        Guid ChapterId,
        string MetadataPath,
        string ManuscriptPath);
}
