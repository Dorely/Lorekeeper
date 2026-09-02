using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lorekeeper.Composition;
using Lorekeeper.ImportExport;
using Lorekeeper.Manuscripts;

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
        var validatedFiles = new Dictionary<string, ValidatedSnapshotFile>(StringComparer.Ordinal);
        using var payloadHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in manifest.Files.OrderBy(item => item.Path, StringComparer.Ordinal))
        {
            var path = NormalizeRelativePath(entry.Path);
            var fullPath = ResolveSafePath(fullRoot, path);
            if (!File.Exists(fullPath))
                throw new InvalidDataException($"Snapshot file '{path}' is missing.");

            var isAssetBlob = IsAssetBlobPath(path);
            var bytes = ValidateAndReadFile(
                fullPath,
                path,
                entry,
                payloadHash,
                retainBytes: !isAssetBlob || readOptions.IncludeAssetData,
                out var actualHash);
            validatedFiles.Add(path, new(entry.Length, actualHash));
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
        var sources = ReadRequired<VersionHistorySnapshotSourcesArea>(files, "sources/sources.json");
        var assets = ReadRequired<VersionHistorySnapshotAssetsArea>(files, "assets/assets.json");
        assets = AdaptAssetsForSchema(assets, manifest.SchemaVersion);
        var manuscript = ReadRequired<VersionHistorySnapshotManuscriptArea>(files, "manuscript/styles.json");
        var composition = ReadRequired<VersionHistorySnapshotCompositionArea>(files, "composition/composition.json");
        var publication = ReadRequired<VersionHistorySnapshotPublicationArea>(
            files,
            "publication/publication.json",
            requireCanonicalRoundTrip: manifest.SchemaVersion == VersionHistorySnapshotContract.SchemaVersion);
        publication = AdaptPublicationForSchema(publication, manifest.SchemaVersion);
        ValidateSchemaFileSet(listedPaths, assets, chapterPaths);
        ValidateAssetBlobs(assets, validatedFiles);
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
        };

        ValidateReferences(payload);
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
        IReadOnlyDictionary<Guid, ChapterFilePaths> chapterPaths)
    {
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "project/project.json",
            "narrative/narrative.json",
            "graph/graph.json",
            "sources/sources.json",
            "assets/assets.json",
            "manuscript/styles.json",
            "composition/composition.json",
            "publication/publication.json",
        };

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

    private static void ValidateReferences(VersionHistorySnapshotPayload payload)
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
        if (payload.Narrative.BookBriefCanonSourceIds.Any(sourceId => !sourceIds.Contains(sourceId)))
            throw new InvalidDataException("Book Brief canonical-source selection references a missing source.");
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
