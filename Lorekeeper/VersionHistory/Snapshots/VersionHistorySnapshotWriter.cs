using Lorekeeper.ImportExport;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.ProjectArchive;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.VersionHistory.Snapshots;

public interface IVersionHistorySnapshotWriter
{
    Task<VersionHistorySnapshotArtifact> WriteAsync(
        Guid repositoryId,
        Guid projectId,
        string rootDirectory,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Builds a deterministic, Git-ready snapshot tree from the current creative project.
/// This class deliberately has no Git, EF write, restore, or UI responsibilities.
/// </summary>
public sealed class VersionHistorySnapshotWriter(
    IProjectImportExportService projectExport,
    IAppDatabaseOperationFactory database) : IVersionHistorySnapshotWriter
{
    public async Task<VersionHistorySnapshotArtifact> WriteAsync(
        Guid repositoryId,
        Guid projectId,
        string rootDirectory,
        CancellationToken cancellationToken = default)
    {
        if (repositoryId == Guid.Empty)
            throw new ArgumentException("A repository ID is required.", nameof(repositoryId));
        if (projectId == Guid.Empty)
            throw new ArgumentException("A project ID is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new ArgumentException("A snapshot directory is required.", nameof(rootDirectory));

        var fullRoot = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(fullRoot);
        if (File.Exists(Path.Combine(fullRoot, VersionHistorySnapshotContract.ManifestFileName)))
            throw new IOException($"Snapshot directory already contains {VersionHistorySnapshotContract.ManifestFileName}.");

        var exported = await projectExport.CaptureArchiveDocumentAsync(projectId, ProjectExportKind.Full, cancellationToken);
        var document = exported.Document;
        // Review Edits is a local workflow setting, not canonical creative
        // content. Keep the legacy import field readable, but omit it from all
        // newly-created Git snapshots so toggling the workflow cannot dirty
        // version history.
        document = document with
        {
            Project = document.Project with { LegacyAiChangeApprovalEnabled = null },
        };
        var supplemental = await ReadSupplementalStateAsync(projectId, cancellationToken);
        AddJson(fullRoot, "project/project.json", new VersionHistorySnapshotProjectArea(
            document.Project,
            document.PageSetup,
            supplemental.Project.ContestModeEnabled,
            supplemental.References));

        var narrative = new VersionHistorySnapshotNarrativeArea(
            document.BookBrief,
            document.BookBriefCanonSourceIds.OrderBy(id => id).ToList(),
            document.EntityTypes.OrderBy(item => item.Type, StringComparer.Ordinal).ToList(),
            document.Acts.OrderBy(item => item.Id).ToList(),
            document.Chapters.OrderBy(item => item.Id).ToList(),
            supplemental.WritingSamples,
            supplemental.ContextPreferences,
            document.ManuscriptAnnotations.OrderBy(item => item.Id).ToList());
        AddJson(fullRoot, "narrative/narrative.json", VersionHistorySnapshotNarrativeFile.FromArea(narrative));
        foreach (var chapter in narrative.Chapters)
        {
            var chapterDirectory = $"narrative/chapters/{chapter.Id:N}";
            AddJson(
                fullRoot,
                $"{chapterDirectory}/chapter.json",
                VersionHistorySnapshotChapter.FromProjectExportChapter(chapter));
            WriteFile(fullRoot, $"{chapterDirectory}/manuscript.json",
                VersionHistoryCanonicalJson.SerializeDirectManuscript(chapter.ManuscriptJson));
        }

        AddJson(fullRoot, "graph/graph.json", new VersionHistorySnapshotGraphArea(
            document.Nodes
                .Where(IsCanonicalGraphNode)
                .OrderBy(item => item.NodeType, StringComparer.Ordinal)
                .ThenBy(item => item.Key, StringComparer.Ordinal)
                .ToList(),
            document.Edges
                .Where(edge => IsCanonicalGraphEdge(edge)
                    || (string.Equals(edge.EdgeType, "HasChild", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(edge.To.NodeType, "Event", StringComparison.OrdinalIgnoreCase)))
                .OrderBy(item => item.From.StableKey, StringComparer.Ordinal)
                .ThenBy(item => item.EdgeType, StringComparer.Ordinal)
                .ThenBy(item => item.To.StableKey, StringComparer.Ordinal)
                .ToList()));

        await WriteSourcesAsync(fullRoot, projectId, cancellationToken);

        var assetRecords = new List<VersionHistoryImageAsset>();
        await using var assetRead = await database.OpenReadAsync(cancellationToken);
        foreach (var image in document.Images.OrderBy(item => item.Id))
        {
            var blobPath = $"assets/images/{image.Id:N}/content{SafeExtension(image.ContentType, image.FileName)}";
            var data = await assetRead.Db.PublishAssets.AsNoTracking()
                .Where(item => item.ProjectId == projectId && item.Id == image.Id)
                .Select(item => item.Data)
                .SingleAsync(cancellationToken);
            WriteFile(fullRoot, blobPath, data);
            assetRecords.Add(new VersionHistoryImageAsset(
                image.Id,
                image.FileName,
                image.ContentType,
                blobPath,
                VersionHistoryCanonicalJson.Sha256Hex(data),
                data.LongLength,
                image.AltText,
                image.Source,
                image.Prompt,
                image.GenerationModel,
                image.SourceMetadataJson,
                image.DerivedFromImageId,
                image.CropXPercent,
                image.CropYPercent,
                image.CropWidthPercent,
                image.CropHeightPercent));
        }

        var fontFamilies = new List<VersionHistoryFontFamily>();
        foreach (var family in document.FontFamilies.OrderBy(item => item.Id))
        {
            var faces = new List<VersionHistoryFontFace>();
            foreach (var face in family.Faces.OrderBy(item => item.Id))
            {
                var blobPath = $"assets/fonts/{family.Id:N}/faces/{face.Id:N}/content{SafeExtension(face.ContentType, face.FileName)}";
                var data = await assetRead.Db.ProjectFontFaces.AsNoTracking()
                    .Where(item => item.Family.ProjectId == projectId && item.Id == face.Id)
                    .Select(item => item.Data)
                    .SingleAsync(cancellationToken);
                WriteFile(fullRoot, blobPath, data);
                faces.Add(new VersionHistoryFontFace(
                    face.Id,
                    face.SubfamilyName,
                    face.FileName,
                    face.ContentType,
                    face.Weight,
                    face.Italic,
                    blobPath,
                    VersionHistoryCanonicalJson.Sha256Hex(data),
                    data.LongLength));
            }

            fontFamilies.Add(new VersionHistoryFontFamily(
                family.Id,
                family.Name,
                family.EmbeddingRightsConfirmed,
                family.RightsDeclaration,
                faces));
        }

        AddJson(fullRoot, "assets/assets.json", new VersionHistorySnapshotAssetsArea(
            assetRecords,
            document.EntityVisualExamples
                .OrderBy(item => item.Entity.StableKey, StringComparer.Ordinal)
                .ThenBy(item => item.SortOrder)
                .ThenBy(item => item.ImageId)
                .ToList(),
            fontFamilies));

        AddJson(fullRoot, "manuscript/styles.json", new VersionHistorySnapshotManuscriptArea(
            document.ManuscriptStyles.OrderBy(item => item.Id).ToList()));

        AddJson(fullRoot, "composition/composition.json", new VersionHistorySnapshotCompositionArea(
            document.DesignedPages.OrderBy(item => item.Id).ToList()));

        AddJson(fullRoot, "publication/publication.json", new VersionHistorySnapshotPublicationArea(
            document.PublicationBook,
            document.PublicationEditions.OrderBy(item => item.Id).ToList(),
            document.PublicationSections.OrderBy(item => item.Id).ToList()));

        var descriptors = await DescribeFilesAsync(fullRoot, cancellationToken);
        var contentHash = await ComputeContentHashAsync(descriptors, cancellationToken);
        var fileEntries = descriptors
            .Select(item => new VersionHistorySnapshotFile(item.ArchivePath, item.Length, item.Sha256))
            .ToList();
        var manifestWithoutHash = new VersionHistorySnapshotManifest(
            VersionHistorySnapshotContract.FormatId,
            VersionHistorySnapshotContract.SchemaVersion,
            repositoryId,
            projectId,
            VersionHistorySnapshotContract.IncludedAreas,
            contentHash,
            string.Empty,
            fileEntries);
        var manifestHash = VersionHistoryCanonicalJson.Sha256Hex(VersionHistoryCanonicalJson.Serialize(manifestWithoutHash));
        var manifest = manifestWithoutHash with { ManifestHash = manifestHash };
        await File.WriteAllBytesAsync(
            ResolveSafePath(fullRoot, VersionHistorySnapshotContract.ManifestFileName),
            VersionHistoryCanonicalJson.Serialize(manifest),
            cancellationToken);

        // Read the emitted tree back through the strict schema boundary. This
        // validates complete preservation while returning a bounded review payload.
        // Restore owns a separate read lease over this same immutable tree.
        return new VersionHistorySnapshotReader().Read(fullRoot, repositoryId, projectId,
            new() { IncludeAssetData = false, IncludeSourceDetails = false, CancellationToken = cancellationToken });
    }

    private async Task<SupplementalState> ReadSupplementalStateAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        var db = operation.Db;
        var project = await db.Projects.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} was not found.");

        // The shared export DTO still carries operational timestamps and
        // provider diagnostics. Populate those slots with neutral values at
        // this boundary in addition to the canonical JSON filter below.
        var writingSamples = await db.WritingSamples.AsNoTracking()
            .Where(item => item.ProjectId == projectId)
            .OrderBy(item => item.Id)
            .Select(item => new VersionHistoryWritingSample(item.Id, item.Title, item.Body))
            .ToListAsync(cancellationToken);
        var contextPreferences = await db.EditorContextPreferences.AsNoTracking()
            .Where(item => item.ProjectId == projectId)
            .OrderBy(item => item.Id)
            .Select(item => new VersionHistoryContextPreference(
                item.Id,
                item.ChapterId,
                item.Kind,
                item.Key,
                item.IsIncluded,
                item.SortOrder))
            .ToListAsync(cancellationToken);
        var references = await db.ProjectReferences.AsNoTracking()
            .Where(item => item.ReferencingProjectId == projectId)
            .Select(item => new
            {
                item.Id,
                item.ReferencingProjectId,
                item.ReferencedProjectId,
                item.ReferencedRepositoryId,
                item.ReferencedProjectName,
                item.ReferencedProjectSlug,
            })
            .ToListAsync(cancellationToken);
        var referenceRecords = references
            .Select(item => new VersionHistoryProjectReference(
                item.Id,
                item.ReferencingProjectId,
                item.ReferencedProjectId,
                item.ReferencedRepositoryId,
                item.ReferencedProjectName,
                item.ReferencedProjectSlug))
            .OrderBy(item => item.ReferencedProjectId)
            .ThenBy(item => item.ReferenceId)
            .ToList();

        return new SupplementalState(
            new SupplementalProject(project.ContestModeEnabled),
            writingSamples,
            contextPreferences,
            referenceRecords);
    }

    private async Task WriteSourcesAsync(string root, Guid projectId, CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        var db = operation.Db;
        var sourceIds = await db.IngestSources.AsNoTracking()
            .Where(source => source.ProjectId == projectId)
            .OrderBy(source => source.Id)
            .Select(source => source.Id)
            .ToListAsync(cancellationToken);
        var unlinkedBibliography = (await db.BibliographicRecords.AsNoTracking()
            .Where(record => record.ProjectId == projectId && record.SourceId == null)
            .OrderBy(record => record.Id)
            .ToListAsync(cancellationToken))
            .Select(ToBibliographicRecord).ToList();
        var writtenBlobs = new HashSet<string>(StringComparer.Ordinal);

        foreach (var sourceId in sourceIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = await db.IngestSources.AsNoTracking()
                .Include(item => item.Original)
                .ThenInclude(original => original!.Chunks)
                .Include(item => item.ExtractionVersions)
                .SingleAsync(item => item.Id == sourceId, cancellationToken);
            var chunks = await db.IngestSourceChunks.AsNoTracking()
                .Where(item => item.SourceId == sourceId)
                .OrderBy(item => item.SourceExtractionVersionId).ThenBy(item => item.Index)
                .ToListAsync(cancellationToken);
            var pages = await db.IngestSourcePages.AsNoTracking()
                .Where(item => item.SourceId == sourceId)
                .OrderBy(item => item.SourceExtractionVersionId).ThenBy(item => item.PageNumber)
                .ToListAsync(cancellationToken);
            var blocks = await db.IngestSourceBlocks.AsNoTracking()
                .Where(item => item.SourceId == sourceId)
                .OrderBy(item => item.SourceExtractionVersionId).ThenBy(item => item.Index)
                .ToListAsync(cancellationToken);
            var bibliography = (await db.BibliographicRecords.AsNoTracking()
                .Where(item => item.SourceId == sourceId)
                .OrderBy(item => item.Id)
                .ToListAsync(cancellationToken))
                .Select(ToBibliographicRecord).ToList();
            var locations = await db.SourceLocations.AsNoTracking()
                .Where(item => item.SourceId == sourceId)
                .OrderBy(item => item.Id)
                .Select(item => new VersionHistorySourceLocation(
                    item.Id, item.SourceId, item.ExtractionVersionId, item.SourceBlockId, item.PageNumber,
                    item.NormalizedStart, item.NormalizedLength, item.Locator, item.Quote,
                    item.VerificationHash, item.ResolutionState))
                .ToListAsync(cancellationToken);

            var original = source.Original is { } available
                ? new VersionHistorySourceOriginal(
                    available.State, available.FileName, available.MediaType, available.Length, available.Sha256,
                    available.Chunks.OrderBy(chunk => chunk.Index)
                        .Select(chunk => new VersionHistorySourceOriginalChunk(
                            chunk.Id, chunk.Index, chunk.BlobSha256, chunk.ByteLength)).ToList())
                : new VersionHistorySourceOriginal(SourceOriginalState.OriginalUnavailable, source.Title, source.ContentType, 0, null, []);

            using var originalHash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            long originalLength = 0;
            foreach (var reference in original.Chunks)
            {
                var blob = await db.SourceOriginalBlobs.AsNoTracking()
                    .SingleAsync(item => item.Sha256 == reference.BlobSha256, cancellationToken);
                if (blob.Length != reference.ByteLength
                    || !string.Equals(VersionHistoryCanonicalJson.Sha256Hex(blob.Data), blob.Sha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"Source blob '{reference.BlobSha256}' failed retention validation.");
                }
                originalHash.AppendData(blob.Data);
                originalLength += blob.Data.Length;
                if (writtenBlobs.Add(reference.BlobSha256))
                    WriteFile(root, SourceBlobPath(blob.Sha256), blob.Data);
            }
            if (original.State == SourceOriginalState.Available
                && (originalLength != original.Length
                    || !string.Equals(Convert.ToHexStringLower(originalHash.GetHashAndReset()), original.Sha256, StringComparison.Ordinal)))
            {
                throw new InvalidDataException($"Source original '{source.Id:N}' failed retention validation.");
            }

            var manifest = new VersionHistoryRetainedSource(
                source.Id, source.Title, source.SourceKind, source.Description, source.Synopsis,
                source.UserInstructions, source.SourceUrl,
                source.FinalUrl, source.CanonicalUrl, source.ContentType, source.SourceMetadataJson,
                source.ActiveExtractionVersionId, original,
                source.ExtractionVersions.OrderBy(extraction => extraction.Ordinal).Select(extraction =>
                    new VersionHistorySourceExtraction(
                        extraction.Id, extraction.Ordinal, extraction.Extractor, extraction.ExtractorVersion,
                        extraction.OptionsJson, extraction.ContentHash, extraction.Status, extraction.Diagnostics,
                        extraction.NormalizedText,
                        chunks.Where(chunk => chunk.SourceExtractionVersionId == extraction.Id).Select(chunk =>
                            new VersionHistorySourceChunk(chunk.Id, chunk.Index, chunk.Title, chunk.HeadingPath,
                                chunk.StartChar, chunk.EndChar, chunk.EstimatedTokenCount, chunk.TokenCountMethod,
                                chunk.TokenEncodingName, chunk.TokenCountIsExact, chunk.Summary, chunk.AgentNotes,
                                chunk.StructureStatus)).ToList(),
                        pages.Where(page => page.SourceExtractionVersionId == extraction.Id).Select(page =>
                            new VersionHistorySourcePage(page.Id, page.PageNumber, page.Text, page.StartChar,
                                page.EndChar, page.ExtractionMethod, page.Width, page.Height, page.ImageHash,
                                page.RenderSettingsJson, page.VisionModelName, page.Diagnostics)).ToList(),
                        blocks.Where(block => block.SourceExtractionVersionId == extraction.Id).Select(block =>
                            new VersionHistorySourceBlock(block.Id, block.SourcePageId, block.Index, block.Kind,
                                block.Title, block.Locator, block.PageNumber, block.StartChar, block.EndChar,
                                block.NormalizedText, block.ContentHash, block.MetadataJson)).ToList()))
                    .ToList(),
                bibliography,
                locations);
            AddJson(root, SourceManifestPath(source.Id), manifest);
        }

        AddJson(root, "sources/index.json", new VersionHistorySourceIndex(sourceIds, unlinkedBibliography));
    }

    private static VersionHistoryBibliographicRecord ToBibliographicRecord(BibliographicRecord record) => new(
        record.Id, record.SourceId, record.Kind, record.Title, record.ContainerTitle, record.AuthorsJson,
        record.EditorsJson, record.IssuedYear, record.Publisher, record.PublisherPlace, record.Volume,
        record.Issue, record.Pages, record.Doi, record.Url, null, record.Isbn, record.Notes,
        record.TranslatorsJson, record.IssuedMonth, record.IssuedDay, record.Edition, record.Institution,
        record.ThesisType, record.AccessedYear, record.AccessedMonth, record.AccessedDay);

    internal static string SourceManifestPath(Guid sourceId) => $"sources/{sourceId:N}/source.json";

    internal static string SourceBlobPath(string sha256) => $"sources/blobs/{sha256}.bin";

    private static bool IsCanonicalGraphNode(ProjectExportNode node) =>
        !string.Equals(node.NodeType, "Project", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(node.NodeType, "Act", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(node.NodeType, "Chapter", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(node.NodeType, "Source", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(node.NodeType, "SourceChunk", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(node.NodeType, "SourceBlock", StringComparison.OrdinalIgnoreCase);

    private static bool IsCanonicalGraphEdge(ProjectExportEdge edge) =>
        !string.Equals(edge.EdgeType, "HasChild", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(edge.EdgeType, "AutoMention", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(edge.EdgeType, "ExtractedFrom", StringComparison.OrdinalIgnoreCase);

    private static void AddJson(string root, string path, object value) =>
        WriteFile(root, path, VersionHistoryCanonicalJson.Serialize(value));

    private static void WriteFile(string root, string path, byte[] value)
    {
        var destination = ResolveSafePath(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(destination, value);
    }

    private static async Task<IReadOnlyList<ProjectArchiveFileDescriptor>> DescribeFilesAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var files = new List<ProjectArchiveFileDescriptor>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = ProjectArchivePath.Normalize(Path.GetRelativePath(root, file).Replace('\\', '/'));
            files.Add(await ProjectArchiveFileDescriptor.CreateAsync(
                relative,
                "version-history-snapshot",
                relative.EndsWith(".json", StringComparison.Ordinal) ? "application/json" : "application/octet-stream",
                file,
                cancellationToken));
        }

        return files;
    }

    private static async Task<string> ComputeContentHashAsync(
        IReadOnlyList<ProjectArchiveFileDescriptor> files,
        CancellationToken cancellationToken)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var buffer = new byte[81_920];
        foreach (var file in files.OrderBy(item => item.ArchivePath, StringComparer.Ordinal))
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(file.ArchivePath));
            hash.AppendData([0]);
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            hash.AppendData([0]);
            await using var source = file.OpenRead();
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0)
                    break;
                hash.AppendData(buffer, 0, read);
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
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

    private static string SafeExtension(string contentType, string fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        if (extension is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp" or ".tif" or ".tiff"
            or ".svg" or ".ttf" or ".otf")
            return extension;
        return contentType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "font/ttf" or "font/sfnt" => ".ttf",
            "font/otf" => ".otf",
            _ => ".bin",
        };
    }

    private sealed record SupplementalState(
        SupplementalProject Project,
        IReadOnlyList<VersionHistoryWritingSample> WritingSamples,
        IReadOnlyList<VersionHistoryContextPreference> ContextPreferences,
        IReadOnlyList<VersionHistoryProjectReference> References);

    private sealed record SupplementalProject(bool ContestModeEnabled);
}

internal static class VersionHistoryProjectExportExtensions
{
    private static readonly System.Text.Json.JsonSerializerOptions ProjectExportCompatibilityOptions =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    public static ProjectExportDocument Document(this ProjectExportFile file)
    {
        var document = System.Text.Json.JsonSerializer.Deserialize<ProjectExportDocument>(
            file.Content,
            ProjectExportCompatibilityOptions);
        return document ?? throw new InvalidDataException("Project export returned no document.");
    }
}
