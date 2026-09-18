using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Lorekeeper.Authoring;
using Lorekeeper.Citations;
using Lorekeeper.ImportExport;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.ProjectArchive;

public static class ProjectArchiveWarningCodes
{
    public const string OutgoingProjectLinksOmitted = "OUTGOING_PROJECT_LINKS_OMITTED";
    public const string SourceEvidenceOmittedNonStructuralExport = "SOURCE_EVIDENCE_OMITTED_NON_STRUCTURAL_EXPORT";
}

public sealed class ProjectArchiveTraversalCapture : IDisposable, IAsyncDisposable
{
    private bool _disposed;

    internal ProjectArchiveTraversalCapture(
        ProjectArchiveTemporaryCapture capture,
        IReadOnlyList<ProjectArchiveFileDescriptor> files,
        ProjectArchiveSchemaVersions schemaVersions,
        IReadOnlyList<string> warnings)
    {
        Capture = capture;
        Files = files;
        SchemaVersions = schemaVersions;
        Warnings = warnings;
    }

    public ProjectArchiveTemporaryCapture Capture { get; }
    public IReadOnlyList<ProjectArchiveFileDescriptor> Files { get; }
    public ProjectArchiveSchemaVersions SchemaVersions { get; }
    public IReadOnlyList<string> Warnings { get; }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Capture.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

public interface IProjectDependencyTraversalService
{
    Task<ProjectArchiveTraversalCapture> CaptureAsync(
        Guid projectId,
        ProjectDependencyTraversalPolicy policy,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Captures immutable archive inputs while authoring is frozen. The project
/// mutation lease ends before a caller starts ZIP compression or transport.
/// </summary>
public sealed class ProjectDependencyTraversalService(
    IAuthoringMutationFence authoringFence,
    IAppDatabaseOperationFactory database,
    IProjectImportExportService legacyExport,
    ProjectArchiveLimits? archiveLimits = null) : IProjectDependencyTraversalService
{
    private readonly ProjectArchiveLimits _limits = archiveLimits ?? ProjectArchiveLimits.Default;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    public Task<ProjectArchiveTraversalCapture> CaptureAsync(
        Guid projectId,
        ProjectDependencyTraversalPolicy policy,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(policy))
            throw new ArgumentOutOfRangeException(nameof(policy));

        return authoringFence.ExecuteAsync(
            new AuthoringFenceRequest(projectId, [], "portable archive capture"),
            (_, token) => CaptureFrozenAsync(projectId, policy, token),
            cancellationToken);
    }

    private async Task<ProjectArchiveTraversalCapture> CaptureFrozenAsync(
        Guid projectId,
        ProjectDependencyTraversalPolicy policy,
        CancellationToken cancellationToken)
    {
        var capture = ProjectArchiveTemporaryCapture.Create();
        try
        {
            var files = new List<ProjectArchiveFileDescriptor>();
            var exportKind = policy == ProjectDependencyTraversalPolicy.NonStructuralArchive
                ? ProjectExportKind.NonStructural
                : ProjectExportKind.Full;
            var creative = await legacyExport.CaptureArchiveDocumentAsync(projectId, exportKind, cancellationToken);
            await using var operation = await database.OpenReadAsync(cancellationToken);
            var db = operation.Db;
            var manuscripts = ProjectCitationRemapping.Manuscripts(creative.Document).ToList();
            foreach (var manuscript in manuscripts)
                await CitationReferenceValidator.ValidateAsync(db, projectId, manuscript, cancellationToken);
            var citedIds = manuscripts.SelectMany(ManuscriptTraversal.EnumerateCitations)
                .SelectMany(occurrence => occurrence.Cluster.Items).Select(item => item.BibliographicRecordId).Distinct().ToArray();
            var exportedDocument = policy == ProjectDependencyTraversalPolicy.NonStructuralArchive
                ? ProjectCitationRemapping.OmitSourceEvidence(creative.Document) : creative.Document;
            var cleanCreative = RemoveBinaryPayloads(exportedDocument);
            await CaptureJsonAsync(files, capture, cleanCreative["project"]!, "project/project.json", "project-record", cancellationToken);
            await CaptureJsonAsync(files, capture, cleanCreative, "project/creative-state.json", "creative-state", cancellationToken);

            var warnings = new List<string>();
            if (await db.ProjectReferences.AsNoTracking().AnyAsync(reference => reference.ReferencingProjectId == projectId, cancellationToken))
                warnings.Add(ProjectArchiveWarningCodes.OutgoingProjectLinksOmitted);

            if (policy == ProjectDependencyTraversalPolicy.NonStructuralArchive)
            {
                warnings.Add(ProjectArchiveWarningCodes.SourceEvidenceOmittedNonStructuralExport);
                // Keep cited metadata even when its retained source is omitted.
                await CaptureBibliographyAsync(files, capture, db, projectId, null, cancellationToken, citedIds);
            }
            else
            {
                await CaptureSourceClosureAsync(files, capture, db, projectId, cancellationToken);
            }

            await CaptureCreativeBinariesAsync(files, capture, db, projectId, cleanCreative, cancellationToken);
            return new ProjectArchiveTraversalCapture(
                capture,
                files.OrderBy(file => file.ArchivePath, StringComparer.Ordinal).ToArray(),
                new ProjectArchiveSchemaVersions(
                    ProjectArchiveContract.ManuscriptSchemaVersion,
                    ProjectArchiveContract.RecordSchemaVersion,
                    ProjectArchiveContract.HistorySnapshotSchemaVersion),
                warnings.OrderBy(warning => warning, StringComparer.Ordinal).ToArray());
        }
        catch
        {
            capture.Dispose();
            throw;
        }
    }

    private async Task CaptureSourceClosureAsync(
        ICollection<ProjectArchiveFileDescriptor> files,
        ProjectArchiveTemporaryCapture capture,
        AppDbContext db,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var sourceIds = await db.IngestSources.AsNoTracking()
            .Where(source => source.ProjectId == projectId)
            .OrderBy(source => source.Id)
            .Select(source => source.Id)
            .ToListAsync(cancellationToken);
        await CaptureJsonAsync(files, capture, sourceIds, "sources/index.json", "source-index", cancellationToken);
        foreach (var sourceId in sourceIds)
        {
            var source = await db.IngestSources.AsNoTracking()
                .Where(item => item.Id == sourceId)
                .Select(item => new SourceRecord(item.Id, item.Title, item.SourceKind, item.Description,
                    item.Synopsis, item.UserInstructions, item.SourceUrl,
                    item.FinalUrl, item.CanonicalUrl, item.FetchedAt, item.ContentType, item.SourceMetadataJson,
                    item.ActiveExtractionVersionId, item.CreatedAt, item.UpdatedAt))
                .SingleAsync(cancellationToken);
            await CaptureJsonAsync(files, capture, source, $"sources/{sourceId:N}/source.json", "source-record", cancellationToken);

            var original = await db.SourceOriginals.AsNoTracking()
                .SingleOrDefaultAsync(item => item.SourceId == sourceId, cancellationToken);
            if (original is not null)
            {
                var chunks = await db.SourceOriginalChunks.AsNoTracking()
                    .Where(chunk => chunk.SourceId == sourceId)
                    .OrderBy(chunk => chunk.Index)
                    .Select(chunk => new OriginalChunkPointer(chunk.Index, chunk.BlobSha256, chunk.ByteLength))
                    .ToListAsync(cancellationToken);
            var chunkRecords = new List<SourceOriginalChunkRecord>(chunks.Count);
            foreach (var chunk in chunks)
            {
                var blob = await db.SourceOriginalBlobs.AsNoTracking()
                    .Where(item => item.Sha256 == chunk.BlobSha256)
                    .Select(item => item.Data)
                    .SingleAsync(cancellationToken);
                var path = $"sources/{sourceId:N}/original/chunks/{chunk.Index:D8}";
                var descriptor = await CaptureBinaryAsync(
                    capture,
                    blob,
                    path,
                    "source-original-chunk",
                    "application/octet-stream",
                    cancellationToken);
                if (descriptor.Length != chunk.ByteLength
                    || !string.Equals(descriptor.Sha256, chunk.BlobSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ProjectArchiveException("A retained source-original chunk does not match its persisted declaration.");
                }
                files.Add(descriptor);
                chunkRecords.Add(new SourceOriginalChunkRecord(chunk.Index, path, descriptor.Length, descriptor.Sha256));
            }

                await CaptureJsonAsync(files, capture, new SourceOriginalRecord(
                    original.SourceId, original.State, original.FileName, original.MediaType, original.Length,
                    original.Sha256, original.CreatedAt, chunkRecords),
                    $"sources/{original.SourceId:N}/original/manifest.json", "source-original-manifest", cancellationToken);
            }

            await CaptureSourceExtractionsAsync(files, capture, db, sourceId, cancellationToken);
            await CaptureSourceLocationsAsync(files, capture, db, projectId, sourceId, cancellationToken);
            await CaptureBibliographyAsync(files, capture, db, projectId, sourceId, cancellationToken);
        }

        await CaptureBibliographyAsync(files, capture, db, projectId, null, cancellationToken);
    }

    private async Task CaptureSourceExtractionsAsync(
        ICollection<ProjectArchiveFileDescriptor> files,
        ProjectArchiveTemporaryCapture capture,
        AppDbContext db,
        Guid sourceId,
        CancellationToken cancellationToken)
    {
        var versionIds = await db.SourceExtractionVersions.AsNoTracking()
            .Where(version => version.SourceId == sourceId)
            .OrderBy(version => version.Ordinal)
            .Select(version => version.Id)
            .ToListAsync(cancellationToken);
        foreach (var versionId in versionIds)
        {
            var version = await db.SourceExtractionVersions.AsNoTracking()
                .Where(item => item.Id == versionId)
                .Select(item => new SourceExtractionMetadataRecord(item.Id, item.SourceId, item.Ordinal,
                    item.Extractor, item.ExtractorVersion, item.OptionsJson, item.ContentHash, item.Status,
                    item.Diagnostics, item.NormalizedText, item.CreatedAt))
                .SingleAsync(cancellationToken);
            var root = $"sources/{sourceId:N}/extractions/{versionId:N}";
            await CaptureJsonAsync(files, capture, version, $"{root}/extraction.json", "source-extraction", cancellationToken);

            var chunkIds = await db.IngestSourceChunks.AsNoTracking()
                .Where(item => item.SourceExtractionVersionId == versionId)
                .OrderBy(item => item.Index)
                .Select(item => item.Id)
                .ToListAsync(cancellationToken);
            foreach (var chunkId in chunkIds)
            {
                var item = await db.IngestSourceChunks.AsNoTracking()
                    .Where(chunk => chunk.Id == chunkId)
                    .Select(chunk => new SourceChunkRecord(chunk.Id, chunk.Index, chunk.Title, chunk.HeadingPath,
                        chunk.StartChar, chunk.EndChar, chunk.EstimatedTokenCount, chunk.TokenCountMethod,
                        chunk.TokenEncodingName, chunk.TokenCountIsExact, chunk.Summary, chunk.AgentNotes,
                        chunk.StructureStatus, chunk.CreatedAt, chunk.UpdatedAt))
                    .SingleAsync(cancellationToken);
                await CaptureJsonAsync(files, capture, item, $"{root}/chunks/{chunkId:N}.json", "source-analysis-chunk", cancellationToken);
            }

            var pageIds = await db.IngestSourcePages.AsNoTracking()
                .Where(item => item.SourceExtractionVersionId == versionId)
                .OrderBy(item => item.PageNumber)
                .Select(item => item.Id)
                .ToListAsync(cancellationToken);
            foreach (var pageId in pageIds)
            {
                var item = await db.IngestSourcePages.AsNoTracking()
                    .Where(page => page.Id == pageId)
                    .Select(page => new SourcePageRecord(page.Id, page.PageNumber, page.Text, page.StartChar,
                        page.EndChar, page.ExtractionMethod, page.Width, page.Height, page.ImageHash,
                        page.RenderSettingsJson, page.VisionProviderId, page.VisionModelName, page.Diagnostics,
                        page.CreatedAt))
                    .SingleAsync(cancellationToken);
                await CaptureJsonAsync(files, capture, item, $"{root}/pages/{pageId:N}.json", "source-page", cancellationToken);
            }

            var blockIds = await db.IngestSourceBlocks.AsNoTracking()
                .Where(item => item.SourceExtractionVersionId == versionId)
                .OrderBy(item => item.Index)
                .Select(item => item.Id)
                .ToListAsync(cancellationToken);
            foreach (var blockId in blockIds)
            {
                var item = await db.IngestSourceBlocks.AsNoTracking()
                    .Where(block => block.Id == blockId)
                    .Select(block => new SourceBlockRecord(block.Id, block.SourcePageId, block.Index, block.Kind,
                        block.Title, block.Locator, block.PageNumber, block.StartChar, block.EndChar,
                        block.NormalizedText, block.ContentHash, block.MetadataJson, block.CreatedAt))
                    .SingleAsync(cancellationToken);
                await CaptureJsonAsync(files, capture, item, $"{root}/blocks/{blockId:N}.json", "source-extraction-block", cancellationToken);
            }
        }
    }

    private async Task CaptureSourceLocationsAsync(
        ICollection<ProjectArchiveFileDescriptor> files,
        ProjectArchiveTemporaryCapture capture,
        AppDbContext db,
        Guid projectId,
        Guid sourceId,
        CancellationToken cancellationToken)
    {
        var ids = await db.SourceLocations.AsNoTracking()
            .Where(location => location.ProjectId == projectId && location.SourceId == sourceId)
            .OrderBy(location => location.Id)
            .Select(location => location.Id)
            .ToListAsync(cancellationToken);
        foreach (var id in ids)
        {
            var item = await db.SourceLocations.AsNoTracking()
                .Where(location => location.Id == id)
                .Select(location => new SourceLocationRecord(location.Id, location.SourceId,
                    location.ExtractionVersionId, location.SourceBlockId, location.PageNumber,
                    location.NormalizedStart, location.NormalizedLength, location.Locator, location.Quote,
                    location.VerificationHash, location.ResolutionState, location.CreatedAt))
                .SingleAsync(cancellationToken);
            await CaptureJsonAsync(files, capture, item, $"sources/{sourceId:N}/locations/{id:N}.json", "source-location", cancellationToken);
        }
    }

    private async Task CaptureBibliographyAsync(
        ICollection<ProjectArchiveFileDescriptor> files,
        ProjectArchiveTemporaryCapture capture,
        AppDbContext db,
        Guid projectId,
        Guid? sourceId,
        CancellationToken cancellationToken,
        Guid[]? includedLinkedIds = null)
    {
        var additionalIds = includedLinkedIds ?? [];
        var ids = await db.BibliographicRecords.AsNoTracking()
            .Where(record => record.ProjectId == projectId && (record.SourceId == sourceId || additionalIds.Contains(record.Id)))
            .OrderBy(record => record.Id)
            .Select(record => record.Id)
            .ToListAsync(cancellationToken);
        foreach (var id in ids)
        {
            var item = await db.BibliographicRecords.AsNoTracking()
                .Where(record => record.Id == id)
                .Select(record => new BibliographicRecordRecord(record.Id, record.SourceId, record.Kind,
                    record.Title, record.ContainerTitle, record.AuthorsJson, record.EditorsJson, record.IssuedYear,
                    record.Publisher, record.PublisherPlace, record.Volume, record.Issue, record.Pages,
                    record.Doi, record.Url, null, record.Isbn, record.Notes, record.CreatedAt,
                    record.UpdatedAt, record.TranslatorsJson, record.IssuedMonth, record.IssuedDay,
                    record.Edition, record.Institution, record.ThesisType, record.AccessedYear,
                    record.AccessedMonth, record.AccessedDay))
                .SingleAsync(cancellationToken);
            if (includedLinkedIds is not null)
                item = item with { SourceId = null };
            var root = sourceId is Guid idForPath ? $"sources/{idForPath:N}/bibliography" : "bibliography";
            await CaptureJsonAsync(files, capture, item, $"{root}/{id:N}.json", "bibliographic-record", cancellationToken);
        }
    }

    private async Task CaptureCreativeBinariesAsync(
        ICollection<ProjectArchiveFileDescriptor> files,
        ProjectArchiveTemporaryCapture capture,
        AppDbContext db,
        Guid projectId,
        JsonObject cleanCreative,
        CancellationToken cancellationToken)
    {
        var imageIds = cleanCreative["images"]?.AsArray()
            .Select(image => image?["id"]?.GetValue<Guid>() ?? Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToArray() ?? [];
        foreach (var imageId in imageIds.Order())
        {
            var image = await db.PublishAssets.AsNoTracking()
                .Where(item => item.ProjectId == projectId && item.Id == imageId)
                .Select(item => new BinaryRecord(item.Id, item.ContentType, item.Data))
                .SingleAsync(cancellationToken);
            files.Add(await CaptureBinaryAsync(capture, image.Data, $"assets/images/{image.Id:N}", "image-blob", image.ContentType, cancellationToken));
        }

        var faceIds = cleanCreative["fontFamilies"]?.AsArray()
            .SelectMany(family => family?["faces"] is JsonArray faces ? faces : Enumerable.Empty<JsonNode?>())
            .Select(face => face?["id"]?.GetValue<Guid>() ?? Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToArray() ?? [];
        foreach (var faceId in faceIds.Order())
        {
            var face = await db.ProjectFontFaces.AsNoTracking()
                .Where(item => item.Family.ProjectId == projectId && item.Id == faceId)
                .Select(item => new BinaryRecord(item.Id, item.ContentType, item.Data))
                .SingleAsync(cancellationToken);
            files.Add(await CaptureBinaryAsync(capture, face.Data, $"assets/fonts/{face.Id:N}", "font-blob", face.ContentType, cancellationToken));
        }
    }

    private static JsonObject RemoveBinaryPayloads(ProjectExportDocument document)
    {
        // CaptureArchiveDocumentAsync already excludes binary payloads. Keep a
        // single structural DOM only to omit their empty legacy `data` members
        // before the bounded file writer serializes creative-state.json.
        var result = JsonSerializer.SerializeToNode(document, JsonOptions)?.AsObject()
            ?? throw new ProjectArchiveException("Creative archive state could not be serialized.");
        // Retained sources have one authority under sources/. The legacy source
        // projection exists only for JSON v1-v31 import/export adapters.
        result["ingestSources"] = new JsonArray();
        RemoveDataProperties(result);
        return result;
    }

    private static void RemoveDataProperties(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                obj.Remove("data");
                foreach (var property in obj.ToArray())
                    RemoveDataProperties(property.Value);
                break;
            case JsonArray array:
                foreach (var child in array)
                    RemoveDataProperties(child);
                break;
        }
    }

    private Task CaptureJsonAsync<T>(
        ICollection<ProjectArchiveFileDescriptor> files,
        ProjectArchiveTemporaryCapture capture,
        T value,
        string path,
        string kind,
        CancellationToken cancellationToken) =>
        CaptureJsonCoreAsync(files, capture, value, path, kind, cancellationToken);

    private async Task CaptureJsonCoreAsync<T>(
        ICollection<ProjectArchiveFileDescriptor> files,
        ProjectArchiveTemporaryCapture capture,
        T value,
        string path,
        string kind,
        CancellationToken cancellationToken)
    {
        files.Add(await capture.CaptureJsonAsync(value, path, kind, _limits.MaximumEntryBytes, JsonOptions, cancellationToken));
    }

    private async Task<ProjectArchiveFileDescriptor> CaptureBinaryAsync(
        ProjectArchiveTemporaryCapture capture,
        byte[] data,
        string path,
        string kind,
        string mediaType,
        CancellationToken cancellationToken)
    {
        if (data.LongLength > _limits.MaximumEntryBytes)
            throw new ProjectArchiveException("A binary archive record exceeds its configured byte limit.");
        await using var stream = new MemoryStream(data, writable: false);
        return await capture.CaptureAsync(stream, path, kind, mediaType, _limits.MaximumEntryBytes, cancellationToken);
    }

    private sealed record SourceRecord(Guid Id, string Title, string SourceKind, string Description, string Synopsis,
        string UserInstructions, string SourceUrl, string FinalUrl,
        string CanonicalUrl, DateTime? FetchedAt, string ContentType, string SourceMetadataJson,
        Guid? ActiveExtractionVersionId, DateTime CreatedAt, DateTime UpdatedAt);
    private sealed record OriginalChunkPointer(int Index, string BlobSha256, int ByteLength);
    private sealed record SourceOriginalChunkRecord(int Index, string Path, long ByteLength, string Sha256);
    private sealed record SourceOriginalRecord(Guid SourceId, SourceOriginalState State, string FileName,
        string MediaType, long Length, string? Sha256, DateTime CreatedAt, IReadOnlyList<SourceOriginalChunkRecord> Chunks);
    private sealed record SourceExtractionMetadataRecord(Guid Id, Guid SourceId, int Ordinal, string Extractor,
        string ExtractorVersion, string OptionsJson, string ContentHash, SourceExtractionStatus Status,
        string Diagnostics, string NormalizedText, DateTime CreatedAt);
    private sealed record SourceChunkRecord(Guid Id, int Index, string Title, string HeadingPath, int StartChar,
        int EndChar, int EstimatedTokenCount, string TokenCountMethod, string? TokenEncodingName,
        bool TokenCountIsExact, string Summary, string AgentNotes, IngestSourceChunkStructureStatus StructureStatus,
        DateTime CreatedAt, DateTime UpdatedAt);
    private sealed record SourcePageRecord(Guid Id, int PageNumber, string Text, int StartChar, int EndChar,
        string ExtractionMethod, int Width, int Height, string ImageHash, string RenderSettingsJson,
        int? VisionProviderId, string VisionModelName, string Diagnostics, DateTime CreatedAt);
    private sealed record SourceBlockRecord(Guid Id, Guid? SourcePageId, int Index, string Kind, string Title,
        string Locator, int? PageNumber, int StartChar, int EndChar, string NormalizedText, string ContentHash,
        string MetadataJson, DateTime CreatedAt);
    private sealed record SourceLocationRecord(Guid Id, Guid SourceId, Guid ExtractionVersionId, Guid? SourceBlockId,
        int? PageNumber, int NormalizedStart, int NormalizedLength, string Locator, string Quote,
        string VerificationHash, SourceLocationResolutionState ResolutionState, DateTime CreatedAt);
    private sealed record BibliographicRecordRecord(Guid Id, Guid? SourceId, BibliographicRecordKind Kind,
        string Title, string ContainerTitle, string AuthorsJson, string EditorsJson, int? IssuedYear,
        string Publisher, string PublisherPlace, string Volume, string Issue, string Pages, string Doi,
        string Url, DateTime? AccessedAt, string Isbn, string Notes, DateTime CreatedAt, DateTime UpdatedAt,
        string TranslatorsJson, int? IssuedMonth, int? IssuedDay, string Edition, string Institution,
        string ThesisType, int? AccessedYear, int? AccessedMonth, int? AccessedDay);
    private sealed record BinaryRecord(Guid Id, string ContentType, byte[] Data);
}
