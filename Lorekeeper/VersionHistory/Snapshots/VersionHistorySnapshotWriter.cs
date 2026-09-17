using Lorekeeper.ImportExport;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
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

        var exported = await projectExport.ExportProjectAsync(projectId, ProjectExportKind.Full, cancellationToken);
        var document = exported.Document();
        // Review Edits is a local workflow setting, not canonical creative
        // content. Keep the legacy import field readable, but omit it from all
        // newly-created Git snapshots so toggling the workflow cannot dirty
        // version history.
        document = document with
        {
            Project = document.Project with { LegacyAiChangeApprovalEnabled = null },
        };
        var supplemental = await ReadSupplementalStateAsync(projectId, cancellationToken);
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);

        AddJson(files, "project/project.json", new VersionHistorySnapshotProjectArea(
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
        AddJson(files, "narrative/narrative.json", VersionHistorySnapshotNarrativeFile.FromArea(narrative));
        foreach (var chapter in narrative.Chapters)
        {
            var chapterDirectory = $"narrative/chapters/{chapter.Id:N}";
            AddJson(
                files,
                $"{chapterDirectory}/chapter.json",
                VersionHistorySnapshotChapter.FromProjectExportChapter(chapter));
            files[$"{chapterDirectory}/manuscript.json"] =
                VersionHistoryCanonicalJson.SerializeDirectManuscript(chapter.ManuscriptJson);
        }

        AddJson(files, "graph/graph.json", new VersionHistorySnapshotGraphArea(
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

        AddJson(files, "sources/sources.json", new VersionHistorySnapshotSourcesArea(
            supplemental.Sources.OrderBy(item => item.Id).ToList()));

        var assetRecords = new List<VersionHistoryImageAsset>();
        foreach (var image in document.Images.OrderBy(item => item.Id))
        {
            var blobPath = $"assets/images/{image.Id:N}/content{SafeExtension(image.ContentType, image.FileName)}";
            var data = image.Data ?? [];
            AddBinary(files, blobPath, data);
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
                var data = face.Data ?? [];
                AddBinary(files, blobPath, data);
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

        AddJson(files, "assets/assets.json", new VersionHistorySnapshotAssetsArea(
            assetRecords,
            document.EntityVisualExamples
                .OrderBy(item => item.Entity.StableKey, StringComparer.Ordinal)
                .ThenBy(item => item.SortOrder)
                .ThenBy(item => item.ImageId)
                .ToList(),
            fontFamilies));

        AddJson(files, "manuscript/styles.json", new VersionHistorySnapshotManuscriptArea(
            document.ManuscriptStyles.OrderBy(item => item.Id).ToList()));

        AddJson(files, "composition/composition.json", new VersionHistorySnapshotCompositionArea(
            document.DesignedPages.OrderBy(item => item.Id).ToList()));

        AddJson(files, "publication/publication.json", new VersionHistorySnapshotPublicationArea(
            document.PublicationBook,
            document.PublicationEditions.OrderBy(item => item.Id).ToList(),
            document.PublicationSections.OrderBy(item => item.Id).ToList()));

        var contentHash = VersionHistoryCanonicalJson.Sha256Hex(
            files.Select(item => (item.Key, item.Value)));
        var fileEntries = files
            .Select(item => new VersionHistorySnapshotFile(
                item.Key,
                item.Value.LongLength,
                VersionHistoryCanonicalJson.Sha256Hex(item.Value)))
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
        files[VersionHistorySnapshotContract.ManifestFileName] = VersionHistoryCanonicalJson.Serialize(manifest);

        foreach (var item in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ResolveSafePath(fullRoot, item.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, item.Value, cancellationToken);
        }

        // Read the emitted tree back through the strict schema boundary. This
        // makes the writer's returned payload exactly match what a later
        // compare/restore operation will receive from Git.
        return new VersionHistorySnapshotReader().Read(fullRoot, repositoryId, projectId);
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
        var sources = await db.IngestSources.AsNoTracking()
            .Include(item => item.SourceChunks)
            .Include(item => item.SourcePages)
            .Include(item => item.SourceBlocks)
            .Where(item => item.ProjectId == projectId)
            .Select(item => new ProjectExportIngestSource(
                item.Id,
                item.Title,
                item.SourceKind,
                item.Description,
                item.Synopsis,
                item.UserInstructions,
                item.SourceText,
                item.SourceHash,
                item.SourceUrl,
                item.FinalUrl,
                item.CanonicalUrl,
                null,
                item.ContentType,
                item.SourceMetadataJson,
                default,
                default,
                item.SourceChunks.OrderBy(chunk => chunk.Index).Select(chunk => new ProjectExportIngestSourceChunk(
                    chunk.Id,
                    chunk.Index,
                    chunk.Title,
                    chunk.HeadingPath,
                    chunk.StartChar,
                    chunk.EndChar,
                    chunk.EstimatedTokenCount,
                    chunk.TokenCountMethod,
                    chunk.TokenEncodingName,
                    chunk.TokenCountIsExact,
                    chunk.Summary,
                    chunk.AgentNotes,
                    chunk.StructureStatus,
                    default,
                    default)).ToList(),
                item.SourcePages.OrderBy(page => page.PageNumber).Select(page => new ProjectExportIngestSourcePage(
                    page.Id,
                    page.PageNumber,
                    page.Text,
                    page.StartChar,
                    page.EndChar,
                    page.ExtractionMethod,
                    page.Width,
                    page.Height,
                    page.ImageHash,
                    page.RenderSettingsJson,
                    null,
                    page.VisionModelName,
                    string.Empty,
                    default)).ToList(),
                item.SourceBlocks.OrderBy(block => block.Index).Select(block => new ProjectExportIngestSourceBlock(
                    block.Id,
                    block.SourcePageId,
                    block.Index,
                    block.Kind,
                    block.Title,
                    block.Locator,
                    block.PageNumber,
                    block.StartChar,
                    block.EndChar,
                    block.MetadataJson,
                    default)).ToList()))
            .ToListAsync(cancellationToken);

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
            sources,
            writingSamples,
            contextPreferences,
            referenceRecords);
    }

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

    private static void AddJson(IDictionary<string, byte[]> files, string path, object value) =>
        files[path] = VersionHistoryCanonicalJson.Serialize(value);

    private static void AddBinary(IDictionary<string, byte[]> files, string path, byte[] value)
    {
        if (!IsSafeRelativePath(path))
            throw new InvalidDataException($"Unsafe snapshot path '{path}'.");
        files[path] = value;
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
        IReadOnlyList<ProjectExportIngestSource> Sources,
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
