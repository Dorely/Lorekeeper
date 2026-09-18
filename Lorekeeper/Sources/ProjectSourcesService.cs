using System.Security.Cryptography;
using System.Text.Json;
using Docnet.Core;
using Docnet.Core.Models;
using Lorekeeper.Authoring;
using Lorekeeper.Citations;
using Lorekeeper.Ingest;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.ProjectArchive;
using Lorekeeper.VersionHistory.Services;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace Lorekeeper.Sources;

/// <summary>
/// Read and delivery boundary for retained project sources. Extraction, ingest
/// jobs, graph cleanup, and source deletion remain owned by <see cref="IIngestService"/>.
/// </summary>
public sealed class ProjectSourcesService(
    IAppDatabaseOperationFactory database,
    IIngestService ingest,
    IAuthoringMutationFence authoringFence,
    IAuthoringTargetMutationService targetMutations,
    IAuthoringMutationContextAccessor mutationContext,
    IAuthoringGenerationService generations,
    ProjectVersionHistoryUiEvents historyEvents,
    ILogger<ProjectSourcesService>? logger = null) : IProjectSourcesService
{
    private const int MaximumContentsItems = 1_000;
    private const int MaximumReadingCharacters = 64 * 1024;
    private const int MaximumEvidenceItems = 100;
    private const int MaximumSearchHits = 24;
    private const int SearchContextCharacters = 240;
    private const int SearchExcerptCharacters = 1_200;
    private const int MaximumPdfRenderEdge = 2_048;
    private const int MaximumPdfRenderPixels = 4_000_000;
    private const int MaximumPdfRenderBytes = 16 * 1024 * 1024;

    public async Task<ProjectSourcesWorkspace> GetWorkspaceAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        var db = operation.Db;
        var sources = await db.IngestSources
            .AsNoTracking()
            .Where(source => source.ProjectId == projectId)
            .Select(source => new SourceWorkspaceSeed(
                source.Id,
                source.Title,
                source.SourceKind,
                source.ContentType,
                source.UpdatedAt,
                source.ActiveExtractionVersionId,
                source.Original == null ? null : source.Original.State,
                source.Original == null ? 0 : source.Original.Length,
                source.Original == null ? null : source.Original.Sha256))
            .ToListAsync(cancellationToken);

        var sourceIds = sources.Select(source => source.Id).ToArray();
        var activeVersionIds = sources
            .Where(source => source.ActiveExtractionVersionId is not null)
            .Select(source => source.ActiveExtractionVersionId!.Value)
            .Distinct()
            .ToArray();
        var versions = await db.SourceExtractionVersions
            .AsNoTracking()
            .Where(version => activeVersionIds.Contains(version.Id))
            .Select(version => new { version.Id, version.Status })
            .ToDictionaryAsync(version => version.Id, cancellationToken);
        var blockCounts = await db.IngestSourceBlocks
            .AsNoTracking()
            .Where(block => activeVersionIds.Contains(block.SourceExtractionVersionId))
            .GroupBy(block => block.SourceExtractionVersionId)
            .Select(group => new { ExtractionVersionId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.ExtractionVersionId, item => item.Count, cancellationToken);
        var pageCounts = await db.IngestSourcePages
            .AsNoTracking()
            .Where(page => activeVersionIds.Contains(page.SourceExtractionVersionId))
            .GroupBy(page => page.SourceExtractionVersionId)
            .Select(group => new { ExtractionVersionId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.ExtractionVersionId, item => item.Count, cancellationToken);

        var jobs = await db.IngestJobs
            .AsNoTracking()
            .Where(job => job.ProjectId == projectId)
            .Select(job => new ProjectSourceJobItem(
                job.Id,
                job.SourceId,
                job.Source.Title,
                job.Status,
                job.TotalSourceChunks,
                job.CompletedSourceChunks,
                job.CurrentMessage,
                job.ErrorMessage,
                job.UpdatedAt))
            .ToListAsync(cancellationToken);
        var bibliography = await db.BibliographicRecords
            .AsNoTracking()
            .Where(record => record.ProjectId == projectId)
            .Select(record => new ProjectBibliographicRecordItem(
                record.Id,
                record.SourceId,
                record.Kind,
                record.Title,
                record.ContainerTitle,
                record.AuthorsJson,
                record.EditorsJson,
                record.TranslatorsJson,
                record.IssuedYear,
                record.IssuedMonth,
                record.IssuedDay,
                record.Edition,
                record.Publisher,
                record.PublisherPlace,
                record.Institution,
                record.ThesisType,
                record.Volume,
                record.Issue,
                record.Pages,
                record.Doi,
                record.Url,
                record.AccessedYear,
                record.AccessedMonth,
                record.AccessedDay,
                record.Isbn,
                record.Notes,
                record.UpdatedAt))
            .ToListAsync(cancellationToken);

        var items = sources
            .Select(source =>
            {
                var version = source.ActiveExtractionVersionId is Guid versionId && versions.TryGetValue(versionId, out var active)
                    ? active
                    : null;
                return new ProjectSourceListItem(
                    source.Id,
                    source.Title,
                    source.SourceKind,
                    source.ContentType,
                    source.OriginalState ?? SourceOriginalState.OriginalUnavailable,
                    source.OriginalLength,
                    source.OriginalHash,
                    source.ActiveExtractionVersionId,
                    version?.Status,
                    source.ActiveExtractionVersionId is Guid activeVersionId && blockCounts.TryGetValue(activeVersionId, out var blocks) ? blocks : 0,
                    source.ActiveExtractionVersionId is Guid pageVersionId && pageCounts.TryGetValue(pageVersionId, out var pages) ? pages : 0,
                    source.UpdatedAt);
            })
            .OrderBy(source => source.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(source => source.Id)
            .ToList();

        return new ProjectSourcesWorkspace(
            items,
            jobs.OrderByDescending(job => job.UpdatedAt).ThenBy(job => job.Id).ToList(),
            bibliography.OrderBy(record => record.Title, StringComparer.OrdinalIgnoreCase).ThenBy(record => record.Id).ToList());
    }

    public Task<IReadOnlyList<ProjectBibliographicRecordItem>> ListBibliographyAsync(
        Guid projectId,
        CancellationToken cancellationToken = default) => ReadBibliographyCoreAsync(projectId, 0, null, null, cancellationToken);

    public Task<IReadOnlyList<ProjectBibliographicRecordItem>> ReadBibliographyPageAsync(
        Guid projectId, int offset, int limit, Guid? recordId = null, CancellationToken cancellationToken = default)
    {
        if (offset < 0 || limit is < 1 or > 51) throw new ArgumentOutOfRangeException(nameof(limit));
        return ReadBibliographyCoreAsync(projectId, offset, limit, recordId, cancellationToken);
    }

    private async Task<IReadOnlyList<ProjectBibliographicRecordItem>> ReadBibliographyCoreAsync(
        Guid projectId, int offset, int? limit, Guid? recordId, CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        var query = operation.Db.BibliographicRecords
            .AsNoTracking()
            .Where(record => record.ProjectId == projectId && (recordId == null || record.Id == recordId))
            .OrderBy(record => record.Title)
            .ThenBy(record => record.Id)
            .Skip(offset);
        if (limit is int count) query = query.Take(count);
        return await query
            .Select(record => new ProjectBibliographicRecordItem(
                record.Id,
                record.SourceId,
                record.Kind,
                record.Title,
                record.ContainerTitle,
                record.AuthorsJson,
                record.EditorsJson,
                record.TranslatorsJson,
                record.IssuedYear,
                record.IssuedMonth,
                record.IssuedDay,
                record.Edition,
                record.Publisher,
                record.PublisherPlace,
                record.Institution,
                record.ThesisType,
                record.Volume,
                record.Issue,
                record.Pages,
                record.Doi,
                record.Url,
                record.AccessedYear,
                record.AccessedMonth,
                record.AccessedDay,
                record.Isbn,
                record.Notes,
                record.UpdatedAt))
            .ToListAsync(cancellationToken);
    }

    public Task<ProjectBibliographicRecordItem> SaveBibliographicRecordAsync(
        Guid projectId,
        BibliographicRecordInput input,
        CancellationToken cancellationToken = default) => authoringFence.ExecuteAsync(
            new(projectId, [], "edit bibliography"),
            (_, token) => SaveBibliographicRecordCoreAsync(projectId, input, token), cancellationToken);

    private async Task<ProjectBibliographicRecordItem> SaveBibliographicRecordCoreAsync(
        Guid projectId,
        BibliographicRecordInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateBibliographicRecord(input);

        await using var operation = await database.OpenWriteAsync(projectId, cancellationToken);
        operation.ShareWithNestedOperations();
        var db = operation.Db;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await db.Projects.AnyAsync(project => project.Id == projectId, cancellationToken))
            throw new InvalidOperationException("Project not found.");
        if (input.SourceId is Guid sourceId
            && !await db.IngestSources.AnyAsync(source => source.Id == sourceId && source.ProjectId == projectId, cancellationToken))
        {
            throw new InvalidOperationException("Bibliographic source not found.");
        }

        var record = input.Id is Guid id
            ? await db.BibliographicRecords.SingleOrDefaultAsync(
                item => item.Id == id && item.ProjectId == projectId,
                cancellationToken)
                ?? throw new InvalidOperationException("Bibliographic record not found.")
            : new BibliographicRecord
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                Title = input.Title.Trim(),
            };
        if (input.Id is null)
            db.BibliographicRecords.Add(record);
        else if (input.ExpectedUpdatedAt is null || record.UpdatedAt != input.ExpectedUpdatedAt.Value)
            throw new DbUpdateConcurrencyException("This bibliography record changed after it was opened. Reload it before saving your edits.");

        var clearEvidence = input.Id is not null && record.SourceId != input.SourceId;
        var affected = input.Id is null ? new List<string>()
            : await PrepareBibliographyMutationAsync(db, projectId, record.Id, clearEvidence, cancellationToken);
        record.SourceId = input.SourceId;
        record.Kind = input.Kind;
        record.Title = input.Title.Trim();
        record.ContainerTitle = Clean(input.ContainerTitle);
        record.AuthorsJson = JsonSerializer.Serialize(input.Authors, ManuscriptCodec.JsonOptions);
        record.EditorsJson = JsonSerializer.Serialize(input.Editors, ManuscriptCodec.JsonOptions);
        record.TranslatorsJson = JsonSerializer.Serialize(input.Translators, ManuscriptCodec.JsonOptions);
        record.IssuedYear = input.Issued.Year;
        record.IssuedMonth = input.Issued.Month;
        record.IssuedDay = input.Issued.Day;
        record.AccessedYear = input.Accessed.Year;
        record.AccessedMonth = input.Accessed.Month;
        record.AccessedDay = input.Accessed.Day;
        record.Edition = Clean(input.Edition);
        record.Publisher = Clean(input.Publisher);
        record.PublisherPlace = Clean(input.PublisherPlace);
        record.Institution = Clean(input.Institution);
        record.ThesisType = Clean(input.ThesisType);
        record.Volume = Clean(input.Volume);
        record.Issue = Clean(input.Issue);
        record.Pages = Clean(input.Pages);
        record.Doi = Clean(input.Doi);
        record.Url = Clean(input.Url);
        record.Isbn = Clean(input.Isbn);
        record.Notes = Clean(input.Notes);
        record.UpdatedAt = DateTime.UtcNow;
        await operation.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var result = BibliographyItem(record);
        await operation.DisposeAsync();
        await CompleteBibliographyMutationAsync(projectId, affected, clearEvidence);
        return result;
    }

    public async Task<ProjectSourceReading?> GetReadingAsync(
        Guid projectId,
        Guid sourceId,
        Guid? blockId = null,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        var db = operation.Db;
        var source = await db.IngestSources
            .AsNoTracking()
            .Where(item => item.ProjectId == projectId && item.Id == sourceId)
            .Select(item => new { item.Id, item.Title, item.SourceKind, item.ActiveExtractionVersionId })
            .SingleOrDefaultAsync(cancellationToken);
        if (source?.ActiveExtractionVersionId is not Guid extractionVersionId)
            return null;

        var extraction = await db.SourceExtractionVersions
            .AsNoTracking()
            .Where(version => version.Id == extractionVersionId && version.SourceId == sourceId)
            .Select(version => new { version.Status, version.Extractor, version.ExtractorVersion, version.Diagnostics })
            .SingleOrDefaultAsync(cancellationToken);
        if (extraction is null)
            return null;

        var contents = await db.IngestSourceBlocks
            .AsNoTracking()
            .Where(block => block.SourceId == sourceId && block.SourceExtractionVersionId == extractionVersionId)
            .OrderBy(block => block.Index)
            .ThenBy(block => block.Id)
            .Take(MaximumContentsItems)
            .Select(block => new ProjectSourceContentsItem(
                block.Id,
                block.Index,
                block.Title,
                block.Kind,
                block.Locator,
                block.PageNumber,
                block.StartChar,
                block.EndChar))
            .ToListAsync(cancellationToken);
        var contentsTruncated = contents.Count == MaximumContentsItems
            && await db.IngestSourceBlocks
                .AsNoTracking()
                .Where(block => block.SourceId == sourceId && block.SourceExtractionVersionId == extractionVersionId)
                .CountAsync(cancellationToken) > MaximumContentsItems;
        var selectedBlockId = blockId is Guid requested && contents.Any(item => item.BlockId == requested)
            ? requested
            : contents.FirstOrDefault()?.BlockId;
        ReadingBlockSeed? selected = null;
        if (selectedBlockId is Guid id)
        {
            var block = await db.IngestSourceBlocks
                .AsNoTracking()
                .Where(item => item.Id == id && item.SourceId == sourceId && item.SourceExtractionVersionId == extractionVersionId)
                .Select(item => new ReadingBlockSeed(
                    item.Id,
                    item.SourceExtractionVersionId,
                    item.Title,
                    item.Kind,
                    item.Locator,
                    item.PageNumber,
                    item.StartChar,
                    item.EndChar,
                    item.NormalizedText.Length,
                    item.NormalizedText.Length > MaximumReadingCharacters
                        ? item.NormalizedText.Substring(0, MaximumReadingCharacters)
                        : item.NormalizedText))
                .SingleOrDefaultAsync(cancellationToken);
            if (block is not null)
            {
                selected = block;
            }
        }

        var evidenceSeeds = await db.SourceLocations
            .AsNoTracking()
            .Where(location => location.ProjectId == projectId
                && location.SourceId == sourceId)
            .OrderByDescending(location => location.ExtractionVersionId == extractionVersionId)
            .ThenBy(location => location.NormalizedStart)
            .ThenBy(location => location.Id)
            .Take(MaximumEvidenceItems)
            .Select(location => new EvidenceSeed(
                location.Id,
                location.ResolutionState,
                location.ExtractionVersionId,
                location.SourceBlockId,
                location.Locator,
                location.Quote,
                location.PageNumber,
                location.NormalizedStart,
                location.NormalizedLength,
                location.VerificationHash))
            .ToListAsync(cancellationToken);
        var validatedEvidence = ValidateEvidence(extractionVersionId, selected, evidenceSeeds);
        var selectedBlock = selected is null
            ? null
            : CreateReadingBlock(selected, validatedEvidence.Highlights);

        return new ProjectSourceReading(
            sourceId,
            source.Title,
            source.SourceKind,
            extractionVersionId,
            extraction.Status,
            extraction.Extractor,
            extraction.ExtractorVersion,
            extraction.Diagnostics,
            contents,
            contentsTruncated,
            selectedBlock,
            validatedEvidence.Evidence);
    }

    public async Task<IReadOnlyList<ProjectSourceSearchHit>> SearchAsync(Guid projectId, string query, CancellationToken cancellationToken = default)
    {
        var term = query?.Trim() ?? string.Empty;
        if (term.Length == 0)
            return [];
        if (term.Length > 256)
            throw new ArgumentException("Source search is limited to 256 characters.", nameof(query));

        await using var operation = await database.OpenReadAsync(cancellationToken);
        var rows = await operation.Db.Database.SqlQuery<SourceSearchRow>($"""
            SELECT b."Id" AS "BlockId",
                   b."SourceId" AS "SourceId",
                   s."Title" AS "SourceTitle",
                   b."Title" AS "BlockTitle",
                   b."Locator" AS "Locator",
                   b."PageNumber" AS "PageNumber",
                   substr(
                       b."NormalizedText",
                       MAX(1, instr(lower(b."NormalizedText"), lower({term})) - {SearchContextCharacters}),
                       {SearchExcerptCharacters}) AS "Excerpt"
            FROM "IngestSourceBlocks" AS b
            INNER JOIN "IngestSources" AS s ON s."Id" = b."SourceId"
            WHERE s."ProjectId" = {projectId}
              AND s."ActiveExtractionVersionId" = b."SourceExtractionVersionId"
              AND instr(lower(b."NormalizedText"), lower({term})) > 0
            ORDER BY s."Title", b."Index", b."Id"
            LIMIT {MaximumSearchHits}
            """).ToListAsync(cancellationToken);

        return rows.Select(row => new ProjectSourceSearchHit(
                row.SourceId,
                row.BlockId,
                row.SourceTitle,
                row.BlockTitle,
                row.Locator,
                row.PageNumber,
                HighlightExactMatches(row.Excerpt, term)))
            .ToList();
    }

    public async Task<ProjectSourceOriginalDownload?> GetOriginalDownloadAsync(Guid projectId, Guid sourceId, CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        var original = await operation.Db.SourceOriginals
            .AsNoTracking()
            .Where(item => item.SourceId == sourceId && item.Source.ProjectId == projectId)
            .Select(item => new { item.State, item.FileName, item.MediaType, item.Length, item.Sha256 })
            .SingleOrDefaultAsync(cancellationToken);
        if (original is null || original.State != SourceOriginalState.Available || !IsSha256(original.Sha256))
            return null;

        var chunks = await operation.Db.SourceOriginalChunks
            .AsNoTracking()
            .Where(chunk => chunk.SourceId == sourceId)
            .OrderBy(chunk => chunk.Index)
            .Select(chunk => new ProjectSourceOriginalChunk(chunk.Index, chunk.BlobSha256, chunk.ByteLength))
            .ToListAsync(cancellationToken);
        var download = new ProjectSourceOriginalDownload(
            projectId,
            sourceId,
            FileNameForDownload(original.FileName),
            string.IsNullOrWhiteSpace(original.MediaType) ? "application/octet-stream" : original.MediaType,
            original.Length,
            original.Sha256!,
            chunks);
        ValidateDownload(download);
        return download;
    }

    public async Task CopyOriginalAsync(ProjectSourceOriginalDownload download, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(download);
        ArgumentNullException.ThrowIfNull(destination);
        ValidateDownload(download);

        using var aggregateHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long copied = 0;
        foreach (var expected in download.Chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var actual = await ReadOriginalChunkAsync(download, expected, cancellationToken);
            if (actual.Length != expected.Length
                || actual.Data.Length != expected.Length
                || !string.Equals(actual.Sha256, expected.Sha256, StringComparison.Ordinal)
                || !string.Equals(SourceRetentionValidator.Sha256(actual.Data), expected.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Retained source chunk validation failed.");
            }

            await destination.WriteAsync(actual.Data, cancellationToken);
            aggregateHash.AppendData(actual.Data);
            copied = checked(copied + actual.Data.LongLength);
        }

        var fullHash = Convert.ToHexString(aggregateHash.GetHashAndReset()).ToLowerInvariant();
        if (copied != download.Length || !string.Equals(fullHash, download.Sha256, StringComparison.Ordinal))
            throw new InvalidOperationException("Retained source original validation failed.");
    }

    public async Task<ProjectSourcePdfPage?> RenderPdfPageAsync(
        Guid projectId,
        Guid sourceId,
        int pageNumber,
        int maxEdge,
        CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1)
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        var download = await GetOriginalDownloadAsync(projectId, sourceId, cancellationToken);
        if (download is null || !IsPdf(download))
            return null;

        await using var operation = await database.OpenReadAsync(cancellationToken);
        var page = await operation.Db.IngestSourcePages
            .AsNoTracking()
            .Where(item => item.SourceId == sourceId
                && item.PageNumber == pageNumber
                && item.SourceExtractionVersionId == item.Source.ActiveExtractionVersionId)
            .Select(item => new { item.Width, item.Height })
            .FirstOrDefaultAsync(cancellationToken);
        if (page is null || page.Width <= 0 || page.Height <= 0)
            return null;

        var boundedEdge = Math.Clamp(maxEdge, 256, MaximumPdfRenderEdge);
        var (width, height) = RenderDimensions(page.Width, page.Height, boundedEdge);
        await using var capture = ProjectArchiveTemporaryCapture.Create();
        var staged = await capture.CaptureWrittenAsync(
            "original.pdf",
            "source-original",
            "application/pdf",
            download.Length,
            (stream, token) => CopyOriginalAsync(download, stream, token),
            cancellationToken);
        using var document = DocLib.Instance.GetDocReader(staged.LocalPath, new PageDimensions(width, height));
        using var pageReader = document.GetPageReader(pageNumber - 1);
        var raw = pageReader.GetImage();
        var renderedWidth = pageReader.GetPageWidth();
        var renderedHeight = pageReader.GetPageHeight();
        if (renderedWidth <= 0 || renderedHeight <= 0 || (long)renderedWidth * renderedHeight > MaximumPdfRenderPixels)
            throw new InvalidOperationException("Requested PDF page render exceeds the bounded raster limit.");
        var expectedRasterBytes = checked((long)renderedWidth * renderedHeight * 4);
        if (raw.Length != expectedRasterBytes)
            throw new InvalidOperationException("PDF renderer returned an invalid BGRA raster length.");

        using var bitmap = new SKBitmap(renderedWidth, renderedHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        System.Runtime.InteropServices.Marshal.Copy(raw, 0, bitmap.GetPixels(), raw.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 95);
        if (encoded is null || encoded.Size > MaximumPdfRenderBytes)
            throw new InvalidOperationException("Rendered PDF page exceeds the bounded image limit.");
        return new ProjectSourcePdfPage(encoded.ToArray(), renderedWidth, renderedHeight);
    }

    public async Task<SourceDeletionUsageReport?> GetDeletionUsageAsync(Guid projectId, Guid sourceId, CancellationToken cancellationToken = default)
    {
        if (!await IsOwnedSourceAsync(projectId, sourceId, cancellationToken))
            return null;
        return await ingest.GetSourceDeletionUsageAsync(sourceId, cancellationToken);
    }

    public Task<IngestJob> QueueLegacyConversionAsync(Guid projectId, Guid sourceId, CancellationToken cancellationToken = default) =>
        ingest.QueueLegacyConversionAsync(projectId, sourceId, cancellationToken);

    public async Task ReextractSourceAsync(Guid projectId, Guid sourceId, CancellationToken cancellationToken = default)
    {
        if (!await IsOwnedSourceAsync(projectId, sourceId, cancellationToken))
            throw new InvalidOperationException("Source not found.");
        await ingest.ReextractSourceAsync(sourceId, cancellationToken);
    }

    public Task DetachBibliographicRecordAsync(Guid projectId, Guid bibliographicRecordId, DateTime expectedUpdatedAt, CancellationToken cancellationToken = default) =>
        authoringFence.ExecuteAsync(new(projectId, [], "detach bibliography evidence"), async (_, token) =>
        {
            await DetachBibliographicRecordCoreAsync(projectId, bibliographicRecordId, expectedUpdatedAt, token);
            return true;
        }, cancellationToken);

    private async Task DetachBibliographicRecordCoreAsync(Guid projectId, Guid bibliographicRecordId, DateTime expectedUpdatedAt, CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenWriteAsync(projectId, cancellationToken);
        operation.ShareWithNestedOperations();
        await using var transaction = await operation.Db.Database.BeginTransactionAsync(cancellationToken);
        var record = await operation.Db.BibliographicRecords
            .SingleOrDefaultAsync(item => item.ProjectId == projectId && item.Id == bibliographicRecordId, cancellationToken)
            ?? throw new InvalidOperationException("Bibliographic record not found.");
        if (record.UpdatedAt != expectedUpdatedAt)
            throw new DbUpdateConcurrencyException("This bibliography record changed. Reload it before detaching its source evidence.");
        var affected = await PrepareBibliographyMutationAsync(operation.Db, projectId, record.Id, clearEvidence: true, cancellationToken);
        record.SourceId = null;
        record.UpdatedAt = DateTime.UtcNow;
        await operation.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await operation.DisposeAsync();
        await CompleteBibliographyMutationAsync(projectId, affected, clearHistory: true);
    }

    private async Task<List<string>> PrepareBibliographyMutationAsync(
        AppDbContext db, Guid projectId, Guid recordId, bool clearEvidence, CancellationToken cancellationToken)
    {
        var targets = (await db.Chapters.Where(item => item.ProjectId == projectId).OrderBy(item => item.Order).ThenBy(item => item.Id).Select(item => item.Id).ToListAsync(cancellationToken))
            .Select(id => $"chapter:{id:D}").ToList();
        targets.AddRange((await db.PublicationEditionChapterOverrides.Where(item => item.Edition.ProjectId == projectId)
            .Select(item => new { item.EditionId, item.ChapterId }).ToListAsync(cancellationToken))
            .Select(item => $"release:{item.EditionId:D}:chapter:{item.ChapterId:D}"));
        targets.AddRange((await db.PublicationSections.Where(item => item.ProjectId == projectId)
            .Select(item => new { item.Id, item.EditionId }).ToListAsync(cancellationToken))
            .Select(item => item.EditionId is Guid editionId ? $"release:{editionId:D}:section:{item.Id:D}" : $"publication-section:{item.Id:D}"));
        targets.AddRange((await db.DesignedPageContents.Where(item => item.ProjectId == projectId)
            .Select(item => new { item.Id, item.EditionId }).ToListAsync(cancellationToken))
            .Select(item => item.EditionId is Guid editionId ? $"release:{editionId:D}:designed-page-content:{item.Id:D}" : $"designed-page-content:{item.Id:D}"));
        var affected = new List<string>();
        using var suppression = mutationContext.SuppressHistory();
        foreach (var targetId in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = await AuthoringPersistence.ReadTargetAsync(db, projectId, targetId, "", cancellationToken);
            var document = ManuscriptCodec.Deserialize(state.ManuscriptJson);
            if (!ManuscriptTraversal.EnumerateCitations(document).Any(occurrence => occurrence.Cluster.Items.Any(item => item.BibliographicRecordId == recordId)))
                continue;
            await CitationReferenceValidator.ValidateAsync(db, projectId, document, cancellationToken);
            affected.Add(targetId);
            if (!clearEvidence) continue;
            var edits = EvidenceDetachOperations(document, recordId);
            if (edits.Count > 0)
                await targetMutations.ApplyAsync(projectId, targetId, state.Revision, edits, cancellationToken);
        }
        if (clearEvidence)
        {
            // Historical matter remains in portable snapshots. Keep those retained
            // documents closed even though current authoring uses sections.
            var coreMatterIds = await db.PublicationBookMatter.Where(item => item.ProjectId == projectId).Select(item => item.Id).ToListAsync(cancellationToken);
            foreach (var id in coreMatterIds)
            {
                var matter = await db.PublicationBookMatter.SingleAsync(item => item.Id == id, cancellationToken);
                var document = ManuscriptCodec.Deserialize(matter.ManuscriptJson, matter.Id, matter.Revision);
                var edits = EvidenceDetachOperations(document, recordId);
                if (edits.Count == 0) continue;
                await CitationReferenceValidator.ValidateAsync(db, projectId, document, cancellationToken);
                var changed = ManuscriptOperations.Apply(document, edits).Document with { Revision = checked(matter.Revision + 1) };
                matter.ManuscriptJson = ManuscriptCodec.Serialize(changed);
                matter.Revision = changed.Revision;
                matter.UpdatedAt = DateTime.UtcNow;
            }
            var releaseMatterIds = await db.PublicationMatter.Where(item => item.Edition.ProjectId == projectId).Select(item => item.Id).ToListAsync(cancellationToken);
            foreach (var id in releaseMatterIds)
            {
                var matter = await db.PublicationMatter.SingleAsync(item => item.Id == id, cancellationToken);
                var document = ManuscriptCodec.Deserialize(matter.ManuscriptJson, matter.Id, matter.Revision);
                var edits = EvidenceDetachOperations(document, recordId);
                if (edits.Count == 0) continue;
                await CitationReferenceValidator.ValidateAsync(db, projectId, document, cancellationToken);
                var changed = ManuscriptOperations.Apply(document, edits).Document with { Revision = checked(matter.Revision + 1) };
                matter.ManuscriptJson = ManuscriptCodec.Serialize(changed);
                matter.Revision = changed.Revision;
                matter.UpdatedAt = DateTime.UtcNow;
            }
        }
        var affectedCoreChapters = affected.Where(target => target.StartsWith("chapter:", StringComparison.Ordinal)).ToArray();
        if (affectedCoreChapters.Length > 0)
        {
            var editionIds = await db.PublicationEditions.Where(edition => edition.ProjectId == projectId)
                .Select(edition => edition.Id).ToListAsync(cancellationToken);
            var persistedTargets = targets.ToHashSet(StringComparer.Ordinal);
            foreach (var editionId in editionIds)
            foreach (var chapterTarget in affectedCoreChapters)
            {
                var inheritedTarget = $"release:{editionId:D}:{chapterTarget}";
                // A release without an override reads Core, so its outstanding
                // authoring generation must also become stale without creating an override.
                if (!persistedTargets.Contains(inheritedTarget)) affected.Add(inheritedTarget);
            }
        }
        if (clearEvidence)
            await generations.StageInvalidationAsync(db, projectId, affected, cancellationToken);
        return affected;
    }

    private static List<ManuscriptOperation> EvidenceDetachOperations(ManuscriptDocument document, Guid recordId) =>
        ManuscriptTraversal.EnumerateText(document)
            .Where(segment => segment.Block.Content.Any(inline => inline.Citation?.Items.Any(item => item.BibliographicRecordId == recordId && item.SourceLocationId is not null) == true))
            .Select(segment => (ManuscriptOperation)new ReplaceManuscriptInlineContent(segment.Start,
                segment.Block.Content.Select(inline => inline.Citation is not { } cluster ? inline : inline with
                {
                    Citation = cluster with { Items = cluster.Items.Select(item => item.BibliographicRecordId == recordId ? item with { SourceLocationId = null } : item).ToList() },
                }).ToList())).ToList();

    private async Task CompleteBibliographyMutationAsync(Guid projectId, IReadOnlyCollection<string> affected, bool clearHistory)
    {
        if (clearHistory) generations.CompleteInvalidation(affected);
        historyEvents.PublishReviewStateChanged(projectId);
        foreach (var targetId in affected)
        {
            try { await targetMutations.RefreshAsync(projectId, targetId, CancellationToken.None); }
            catch (Exception exception)
            {
                logger?.LogWarning(exception, "Bibliography was saved but derived state refresh failed for {TargetId}.", targetId);
            }
        }
    }

    public async Task DeleteSourceAsync(Guid projectId, Guid sourceId, CancellationToken cancellationToken = default)
    {
        if (!await IsOwnedSourceAsync(projectId, sourceId, cancellationToken))
            throw new InvalidOperationException("Source not found.");
        await ingest.DeleteSourceAsync(sourceId, cancellationToken);
    }

    private async Task<bool> IsOwnedSourceAsync(Guid projectId, Guid sourceId, CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        return await operation.Db.IngestSources
            .AsNoTracking()
            .AnyAsync(source => source.ProjectId == projectId && source.Id == sourceId, cancellationToken);
    }

    internal static void ValidateBibliographicRecord(BibliographicRecordInput input)
    {
        if (!Enum.IsDefined(input.Kind))
            throw new ArgumentOutOfRangeException(nameof(input), "Choose a supported bibliographic record type.");
        if (string.IsNullOrWhiteSpace(input.Title) || input.Title.Trim().Length > 1_000)
            throw new ArgumentException("A bibliographic title of 1 to 1,000 characters is required.", nameof(input));
        ValidatePeople(input.Authors, "author");
        ValidatePeople(input.Editors, "editor");
        ValidatePeople(input.Translators, "translator");
        ValidateDate(input.Issued, "issued");
        ValidateDate(input.Accessed, "accessed");
        foreach (var value in new[]
        {
            input.ContainerTitle, input.Edition, input.Publisher, input.PublisherPlace,
            input.Institution, input.ThesisType, input.Volume, input.Issue, input.Pages,
            input.Doi, input.Url, input.Isbn,
        })
        {
            if ((value?.Length ?? 0) > 2_000)
                throw new ArgumentException("A bibliographic field exceeds the 2,000-character limit.", nameof(input));
        }
        if ((input.Notes?.Length ?? 0) > 20_000)
            throw new ArgumentException("Bibliographic notes exceed the 20,000-character limit.", nameof(input));
    }

    private static void ValidatePeople(IReadOnlyList<CitationPerson> people, string label)
    {
        ArgumentNullException.ThrowIfNull(people);
        if (people.Count > 100)
            throw new ArgumentException($"A bibliographic record supports at most 100 {label} entries.");
        foreach (var person in people)
        {
            ArgumentNullException.ThrowIfNull(person);
            if (string.IsNullOrWhiteSpace(person.Literal) && string.IsNullOrWhiteSpace(person.Family))
                throw new ArgumentException($"Each {label} requires a family or corporate name.");
            if (new[] { person.Family, person.Given, person.Literal }.Any(value => value is null || value.Length > 500))
                throw new ArgumentException($"A {label} name exceeds the 500-character limit.");
        }
    }

    private static void ValidateDate(CitationDate value, string label)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Year is < 1 or > 9999)
            throw new ArgumentException($"The {label} year is outside the supported range.");
        if (value.Month is < 1 or > 12 || value.Month is not null && value.Year is null)
            throw new ArgumentException($"The {label} month requires a valid year.");
        if (value.Day is < 1 or > 31 || value.Day is not null && value.Month is null)
            throw new ArgumentException($"The {label} day requires a valid month.");
        if (value is { Year: int year, Month: int month, Day: int day } && day > DateTime.DaysInMonth(year, month))
            throw new ArgumentException($"The {label} day does not exist in that month.");
    }

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;

    private static ProjectBibliographicRecordItem BibliographyItem(BibliographicRecord record) => new(
        record.Id,
        record.SourceId,
        record.Kind,
        record.Title,
        record.ContainerTitle,
        record.AuthorsJson,
        record.EditorsJson,
        record.TranslatorsJson,
        record.IssuedYear,
        record.IssuedMonth,
        record.IssuedDay,
        record.Edition,
        record.Publisher,
        record.PublisherPlace,
        record.Institution,
        record.ThesisType,
        record.Volume,
        record.Issue,
        record.Pages,
        record.Doi,
        record.Url,
        record.AccessedYear,
        record.AccessedMonth,
        record.AccessedDay,
        record.Isbn,
        record.Notes,
        record.UpdatedAt);

    private async Task<OriginalChunkData> ReadOriginalChunkAsync(
        ProjectSourceOriginalDownload download,
        ProjectSourceOriginalChunk expected,
        CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenReadAsync(cancellationToken);
        var chunk = await operation.Db.SourceOriginalChunks
            .AsNoTracking()
            .Where(item => item.SourceId == download.SourceId
                && item.SourceOriginal.Source.ProjectId == download.ProjectId
                && item.Index == expected.Index
                && item.BlobSha256 == expected.Sha256)
            .Select(item => new OriginalChunkData(item.ByteLength, item.BlobSha256, item.Blob.Data))
            .SingleOrDefaultAsync(cancellationToken);
        return chunk ?? throw new InvalidOperationException("Retained source chunk is no longer available.");
    }

    private static IReadOnlyList<ProjectSourceTextSegment> HighlightExactMatches(string? text, string query)
    {
        var source = text ?? string.Empty;
        var segments = new List<ProjectSourceTextSegment>();
        var offset = 0;
        while (offset < source.Length)
        {
            var match = source.IndexOf(query, offset, StringComparison.OrdinalIgnoreCase);
            if (match < 0)
                break;
            if (match > offset)
                segments.Add(new ProjectSourceTextSegment(source[offset..match], false));
            segments.Add(new ProjectSourceTextSegment(source.Substring(match, query.Length), true));
            offset = match + query.Length;
        }
        if (offset < source.Length)
            segments.Add(new ProjectSourceTextSegment(source[offset..], false));
        return segments.Count == 0 ? [new ProjectSourceTextSegment(source, false)] : segments;
    }

    private static ValidatedEvidence ValidateEvidence(
        Guid activeExtractionVersionId,
        ReadingBlockSeed? block,
        IReadOnlyList<EvidenceSeed> evidence)
    {
        var states = evidence.ToDictionary(item => item.Id, item => item.State);
        foreach (var item in evidence.Where(item => item.ExtractionVersionId != activeExtractionVersionId
            && item.State == SourceLocationResolutionState.Resolved))
        {
            states[item.Id] = SourceLocationResolutionState.Outdated;
        }
        var candidates = new List<EvidenceHighlight>();
        if (block is not null)
        {
            var normalized = block.Text ?? string.Empty;
            foreach (var item in evidence)
            {
                if (item.ExtractionVersionId != block.ExtractionVersionId)
                    continue;
                var rangeEnds = (long)item.NormalizedStart + item.NormalizedLength;
                var intersectsBlock = item.NormalizedStart < block.EndChar && rangeEnds > block.StartChar;
                if (!intersectsBlock)
                    continue;

                var relativeStart = item.NormalizedStart - block.StartChar;
                var relativeEnd = relativeStart + item.NormalizedLength;
                // A reader block carries only a bounded prefix. A valid anchor
                // later in that block remains resolved, but is deliberately not
                // revalidated or highlighted without all of its text in hand.
                if (relativeStart >= 0
                    && relativeEnd > normalized.Length
                    && relativeEnd <= block.NormalizedTextLength)
                {
                    continue;
                }
                var valid = item.State == SourceLocationResolutionState.Resolved
                    && (item.SourceBlockId is null || item.SourceBlockId == block.Id)
                    && item.NormalizedLength > 0
                    && relativeStart >= 0
                    && relativeEnd <= normalized.Length
                    && string.Equals(normalized.Substring(relativeStart, item.NormalizedLength), item.Quote, StringComparison.Ordinal)
                    && string.Equals(
                        SourceRetentionValidator.Sha256(normalized.Substring(relativeStart, item.NormalizedLength)),
                        item.VerificationHash,
                        StringComparison.Ordinal);
                if (!valid)
                {
                    states[item.Id] = item.State == SourceLocationResolutionState.Unavailable
                        ? SourceLocationResolutionState.Unavailable
                        : item.State == SourceLocationResolutionState.Resolved
                            ? SourceLocationResolutionState.Outdated
                            : SourceLocationResolutionState.Ambiguous;
                    continue;
                }

                candidates.Add(new EvidenceHighlight(item.Id, relativeStart, relativeEnd, item.Locator));
            }

            foreach (var candidate in candidates)
            {
                if (candidates.Any(other => other.Id != candidate.Id
                    && other.Start < candidate.End
                    && candidate.Start < other.End))
                {
                    states[candidate.Id] = SourceLocationResolutionState.Ambiguous;
                }
            }
        }

        var highlights = candidates
            .Where(candidate => states[candidate.Id] == SourceLocationResolutionState.Resolved)
            .OrderBy(candidate => candidate.Start)
            .ThenBy(candidate => candidate.End)
            .ToList();
        return new ValidatedEvidence(
            evidence.Select(item => new ProjectSourceEvidenceItem(
                item.Id,
                states[item.Id],
                item.ExtractionVersionId,
                item.SourceBlockId,
                item.Locator,
                item.Quote,
                item.PageNumber,
                item.NormalizedStart,
                item.NormalizedLength)).ToList(),
            highlights);
    }

    private static ProjectSourceReadingBlock CreateReadingBlock(ReadingBlockSeed block, IReadOnlyList<EvidenceHighlight> highlights)
    {
        var text = block.Text ?? string.Empty;
        var segments = new List<ProjectSourceReadingSegment>();
        var cursor = 0;
        foreach (var highlight in highlights)
        {
            if (highlight.Start >= text.Length)
                break;
            var end = Math.Min(highlight.End, text.Length);
            if (highlight.Start > cursor)
                segments.Add(new ProjectSourceReadingSegment(text[cursor..highlight.Start], null, null));
            segments.Add(new ProjectSourceReadingSegment(
                text[highlight.Start..end],
                SourceLocationResolutionState.Resolved,
                highlight.Locator));
            cursor = end;
        }
        if (cursor < text.Length)
            segments.Add(new ProjectSourceReadingSegment(text[cursor..], null, null));
        if (segments.Count == 0)
            segments.Add(new ProjectSourceReadingSegment(text, null, null));
        return new ProjectSourceReadingBlock(
            block.Id,
            block.Title,
            block.Kind,
            block.Locator,
            block.PageNumber,
            block.StartChar,
            block.EndChar,
            text,
            block.NormalizedTextLength > MaximumReadingCharacters,
            segments);
    }

    private static (int Width, int Height) RenderDimensions(int pageWidth, int pageHeight, int maxEdge)
    {
        var scale = maxEdge / (double)Math.Max(pageWidth, pageHeight);
        var width = Math.Max(1, (int)Math.Ceiling(pageWidth * scale));
        var height = Math.Max(1, (int)Math.Ceiling(pageHeight * scale));
        var pixels = (long)width * height;
        if (pixels <= MaximumPdfRenderPixels)
            return (width, height);
        var reduction = Math.Sqrt(MaximumPdfRenderPixels / (double)pixels);
        return (
            Math.Max(1, (int)Math.Floor(width * reduction)),
            Math.Max(1, (int)Math.Floor(height * reduction)));
    }

    private static bool IsPdf(ProjectSourceOriginalDownload download) =>
        string.Equals(download.MediaType, "application/pdf", StringComparison.OrdinalIgnoreCase)
        || download.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    private static string FileNameForDownload(string fileName)
    {
        var candidate = Path.GetFileName(fileName?.Trim());
        return string.IsNullOrWhiteSpace(candidate) ? "source" : candidate;
    }

    private static void ValidateDownload(ProjectSourceOriginalDownload download)
    {
        if (download.ProjectId == Guid.Empty || download.SourceId == Guid.Empty || download.Length <= 0 || !IsSha256(download.Sha256))
            throw new InvalidOperationException("Retained source original metadata is invalid.");
        if (download.Chunks.Count == 0
            || !download.Chunks.Select(chunk => chunk.Index).SequenceEqual(Enumerable.Range(0, download.Chunks.Count))
            || download.Chunks.Any(chunk => chunk.Length is <= 0 or > SourceOriginal.MaximumChunkBytes || !IsSha256(chunk.Sha256))
            || download.Chunks.Sum(chunk => (long)chunk.Length) != download.Length)
        {
            throw new InvalidOperationException("Retained source chunk metadata is invalid.");
        }
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private sealed record SourceWorkspaceSeed(
        Guid Id,
        string Title,
        string SourceKind,
        string ContentType,
        DateTime UpdatedAt,
        Guid? ActiveExtractionVersionId,
        SourceOriginalState? OriginalState,
        long OriginalLength,
        string? OriginalHash);

    private sealed record OriginalChunkData(int Length, string Sha256, byte[] Data);

    private sealed record ReadingBlockSeed(
        Guid Id,
        Guid ExtractionVersionId,
        string Title,
        string Kind,
        string Locator,
        int? PageNumber,
        int StartChar,
        int EndChar,
        int NormalizedTextLength,
        string Text);

    private sealed record EvidenceSeed(
        Guid Id,
        SourceLocationResolutionState State,
        Guid ExtractionVersionId,
        Guid? SourceBlockId,
        string Locator,
        string Quote,
        int? PageNumber,
        int NormalizedStart,
        int NormalizedLength,
        string VerificationHash);

    private sealed record EvidenceHighlight(Guid Id, int Start, int End, string Locator);

    private sealed record ValidatedEvidence(
        IReadOnlyList<ProjectSourceEvidenceItem> Evidence,
        IReadOnlyList<EvidenceHighlight> Highlights);

    private sealed class SourceSearchRow
    {
        public Guid BlockId { get; init; }
        public Guid SourceId { get; init; }
        public string SourceTitle { get; init; } = string.Empty;
        public string BlockTitle { get; init; } = string.Empty;
        public string Locator { get; init; } = string.Empty;
        public int? PageNumber { get; init; }
        public string Excerpt { get; init; } = string.Empty;
    }
}
