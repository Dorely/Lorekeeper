using System.Text.Json;

namespace Lorekeeper.VersionHistory.Snapshots;

public interface IVersionHistorySnapshotReader
{
    VersionHistorySnapshotArtifact Read(
        string rootDirectory,
        Guid? expectedRepositoryId = null,
        Guid? expectedProjectId = null);
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
        Guid? expectedProjectId = null)
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
            throw new InvalidDataException("Snapshot manifest.json is not in canonical schema-v1 form.");
        ValidateManifest(manifest, expectedRepositoryId, expectedProjectId);

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in manifest.Files)
        {
            var path = NormalizeRelativePath(entry.Path);
            var fullPath = ResolveSafePath(fullRoot, path);
            if (!File.Exists(fullPath))
                throw new InvalidDataException($"Snapshot file '{path}' is missing.");

            var bytes = File.ReadAllBytes(fullPath);
            if (bytes.LongLength != entry.Length)
                throw new InvalidDataException($"Snapshot file '{path}' has an unexpected length.");
            if (!string.Equals(VersionHistoryCanonicalJson.Sha256Hex(bytes), entry.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException($"Snapshot file '{path}' failed its SHA-256 check.");
            files.Add(path, bytes);
        }

        var actualPayloadHash = VersionHistoryCanonicalJson.Sha256Hex(
            files.Select(item => (item.Key, item.Value)));
        if (!string.Equals(actualPayloadHash, manifest.ContentHash, StringComparison.Ordinal))
            throw new InvalidDataException("Snapshot payload content hash does not match its manifest.");

        var expectedManifestHash = VersionHistoryCanonicalJson.Sha256Hex(
            VersionHistoryCanonicalJson.Serialize(manifest with { ManifestHash = string.Empty }));
        if (!string.Equals(expectedManifestHash, manifest.ManifestHash, StringComparison.Ordinal))
            throw new InvalidDataException("Snapshot manifest hash does not match its contents.");

        ValidateFileSet(fullRoot, files.Keys);
        var project = ReadRequired<VersionHistorySnapshotProjectArea>(files, "project/project.json");
        var narrative = ReadRequired<VersionHistorySnapshotNarrativeArea>(files, "narrative/narrative.json");
        var graph = ReadRequired<VersionHistorySnapshotGraphArea>(files, "graph/graph.json");
        var sources = ReadRequired<VersionHistorySnapshotSourcesArea>(files, "sources/sources.json");
        var assets = ReadRequired<VersionHistorySnapshotAssetsArea>(files, "assets/assets.json");
        var manuscript = ReadRequired<VersionHistorySnapshotManuscriptArea>(files, "manuscript/manuscript.json");
        var composition = ReadRequired<VersionHistorySnapshotCompositionArea>(files, "composition/composition.json");
        var publication = ReadRequired<VersionHistorySnapshotPublicationArea>(files, "publication/publication.json");
        ValidateSchemaFileSet(files.Keys, assets);
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
            ImageData = ReadImageData(fullRoot, assets),
            FontFaceData = ReadFontData(fullRoot, assets),
        };

        ValidateReferences(payload);
        return new VersionHistorySnapshotArtifact(fullRoot, manifest, payload);
    }

    private static void ValidateManifest(
        VersionHistorySnapshotManifest manifest,
        Guid? expectedRepositoryId,
        Guid? expectedProjectId)
    {
        if (!string.Equals(manifest.FormatId, VersionHistorySnapshotContract.FormatId, StringComparison.Ordinal))
            throw new InvalidDataException($"Unsupported snapshot format '{manifest.FormatId}'.");
        if (manifest.SchemaVersion != VersionHistorySnapshotContract.SchemaVersion)
            throw new InvalidDataException($"Unsupported snapshot schema version {manifest.SchemaVersion}.");
        if (manifest.RepositoryId == Guid.Empty || manifest.ProjectId == Guid.Empty)
            throw new InvalidDataException("Snapshot repository and project IDs are required.");
        if (expectedRepositoryId is Guid repositoryId && repositoryId != manifest.RepositoryId)
            throw new InvalidDataException("Snapshot repository ID does not match the expected repository.");
        if (expectedProjectId is Guid projectId && projectId != manifest.ProjectId)
            throw new InvalidDataException("Snapshot project ID does not match the expected project.");
        if (!manifest.IncludedAreas.SequenceEqual(VersionHistorySnapshotContract.IncludedAreas, StringComparer.Ordinal))
            throw new InvalidDataException("Snapshot included areas do not match schema version 1.");
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

    private static T ReadRequired<T>(IReadOnlyDictionary<string, byte[]> files, string path)
    {
        if (!files.TryGetValue(path, out var bytes))
            throw new InvalidDataException($"Snapshot payload file '{path}' is missing.");
        var value = VersionHistoryCanonicalJson.Deserialize<T>(bytes);
        if (!bytes.AsSpan().SequenceEqual(VersionHistoryCanonicalJson.Serialize(value!)))
            throw new InvalidDataException($"Snapshot payload file '{path}' is not in canonical schema-v1 form.");
        return value;
    }

    private static Dictionary<Guid, byte[]> ReadImageData(
        string root,
        VersionHistorySnapshotAssetsArea assets)
    {
        var result = new Dictionary<Guid, byte[]>();
        foreach (var image in assets.Images)
        {
            var data = ReadBlob(root, image.BlobPath, image.Sha256, image.ByteLength);
            result.Add(image.Id, data);
        }

        return result;
    }

    private static Dictionary<Guid, byte[]> ReadFontData(
        string root,
        VersionHistorySnapshotAssetsArea assets)
    {
        var result = new Dictionary<Guid, byte[]>();
        foreach (var family in assets.FontFamilies)
        foreach (var face in family.Faces)
        {
            var data = ReadBlob(root, face.BlobPath, face.Sha256, face.ByteLength);
            result.Add(face.Id, data);
        }

        return result;
    }

    private static void ValidateSchemaFileSet(
        IEnumerable<string> actualFiles,
        VersionHistorySnapshotAssetsArea assets)
    {
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "project/project.json",
            "narrative/narrative.json",
            "graph/graph.json",
            "sources/sources.json",
            "assets/assets.json",
            "manuscript/manuscript.json",
            "composition/composition.json",
            "publication/publication.json",
        };

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
            throw new InvalidDataException("Snapshot payload files do not exactly match the schema-v1 area and asset paths.");
    }

    private static byte[] ReadBlob(string root, string relativePath, string expectedHash, long expectedLength)
    {
        var path = ResolveSafePath(root, NormalizeRelativePath(relativePath));
        if (!File.Exists(path))
            throw new InvalidDataException($"Snapshot blob '{relativePath}' is missing.");
        var bytes = File.ReadAllBytes(path);
        if (bytes.LongLength != expectedLength)
            throw new InvalidDataException($"Snapshot blob '{relativePath}' has an unexpected length.");
        if (!string.Equals(VersionHistoryCanonicalJson.Sha256Hex(bytes), expectedHash, StringComparison.Ordinal))
            throw new InvalidDataException($"Snapshot blob '{relativePath}' failed its SHA-256 check.");
        return bytes;
    }

    private static void ValidateReferences(VersionHistorySnapshotPayload payload)
    {
        if (payload.Project.Project.Id != payload.ProjectId)
            throw new InvalidDataException("Snapshot project payload ID does not match its manifest.");
        if (payload.Project.Project.Id == Guid.Empty)
            throw new InvalidDataException("Snapshot project ID is empty.");
        var chapterIds = payload.Narrative.Chapters.Select(chapter => chapter.Id).ToHashSet();
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
            .ToHashSet(StringComparer.Ordinal);
        foreach (var edge in payload.Graph.Edges)
        {
            if (!nodeKeys.Contains(edge.From.StableKey) || !nodeKeys.Contains(edge.To.StableKey))
                throw new InvalidDataException($"Graph edge '{edge.From.StableKey} -> {edge.To.StableKey}' references a missing node.");
        }

        var sourceIds = payload.Sources.Sources.Select(source => source.Id).ToHashSet();
        if (payload.Narrative.BookBriefCanonSourceIds.Any(sourceId => !sourceIds.Contains(sourceId)))
            throw new InvalidDataException("Book Brief canonical-source selection references a missing source.");
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
}
