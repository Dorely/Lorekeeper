using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Ingest;

public sealed partial class IngestService
{
    public async Task<IngestJob> QueueLegacyConversionAsync(
        Guid projectId, Guid sourceId, CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenWriteAsync(projectId, cancellationToken);
        var source = await operation.Db.IngestSources.SingleOrDefaultAsync(
            item => item.Id == sourceId && item.ProjectId == projectId, cancellationToken)
            ?? throw new InvalidOperationException("Source not found in this project.");
        var activeJob = await operation.Db.IngestJobs.FirstOrDefaultAsync(item => item.SourceId == sourceId
            && (item.Status == IngestJobStatus.Queued || item.Status == IngestJobStatus.Running
                || item.Status == IngestJobStatus.StopRequested), cancellationToken);
        if (activeJob is { Mode: IngestJobMode.ConvertLegacySource })
            return activeJob;
        if (activeJob is not null)
            throw new InvalidOperationException("Stop the active source job before converting this source.");
        var extraction = await operation.Db.SourceExtractionVersions.AsNoTracking().SingleOrDefaultAsync(
            item => item.SourceId == sourceId && item.Id == source.ActiveExtractionVersionId, cancellationToken);
        if (extraction?.Status != SourceExtractionStatus.LegacyImmutable)
            throw new InvalidOperationException("Only a source with an active legacy extraction needs conversion.");
        if (string.IsNullOrWhiteSpace(extraction.NormalizedText))
            throw new InvalidOperationException("This legacy source has no saved text to convert.");

        var job = new IngestJob
        {
            ProjectId = projectId,
            SourceId = sourceId,
            SourceExtractionVersionId = extraction.Id,
            Mode = IngestJobMode.ConvertLegacySource,
            Instructions = string.Empty,
            Status = IngestJobStatus.Queued,
            CurrentMessage = "Queued for legacy source conversion and indexing.",
        };
        await operation.Repositories.Ingest.AddJobAsync(job, cancellationToken);
        await operation.SaveChangesAsync(cancellationToken);
        queue.Enqueue(job.Id);
        Notify(projectId, job.Id, IngestJobUpdateKind.Created);
        return job;
    }

    public async Task<IngestSource> ConvertLegacySourceAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        IngestJob job;
        SourceExtractionVersion legacy;
        await using (var read = await database.OpenReadAsync(cancellationToken))
        {
            job = await read.Db.IngestJobs.Include(item => item.Source).SingleAsync(item => item.Id == jobId, cancellationToken);
            if (job.Mode != IngestJobMode.ConvertLegacySource || job.SourceExtractionVersionId is null)
                throw new InvalidOperationException("This is not a legacy source conversion job.");
            // The job identity is also the new extraction identity. A retry after
            // publication or an indexing failure cannot duplicate the conversion.
            if (job.Source.ActiveExtractionVersionId == job.Id)
                return job.Source;
            if (job.Source.ActiveExtractionVersionId != job.SourceExtractionVersionId)
                throw new InvalidOperationException("The active source extraction changed; reload the source before converting it.");
            legacy = await read.Db.SourceExtractionVersions.SingleAsync(item => item.Id == job.SourceExtractionVersionId
                && item.SourceId == job.SourceId, cancellationToken);
            if (legacy.Status != SourceExtractionStatus.LegacyImmutable || string.IsNullOrWhiteSpace(legacy.NormalizedText))
                throw new InvalidOperationException("The legacy extraction has no readable saved text.");
        }

        var chunks = structureBuilder.Build(new IngestSourceStructureRequest(legacy.NormalizedText, null, null, null))
            .Select(draft => new IngestSourceChunk
            {
                SourceId = job.SourceId, SourceExtractionVersionId = job.Id,
                Index = draft.Index, Title = draft.Title, HeadingPath = draft.HeadingPath,
                StartChar = draft.StartChar, EndChar = draft.EndChar,
                EstimatedTokenCount = draft.TokenCount.TokenCount, TokenCountMethod = draft.TokenCount.Method,
                TokenEncodingName = draft.TokenCount.EncodingName, TokenCountIsExact = draft.TokenCount.IsExact,
            }).ToList();
        if (chunks.Count == 0)
            throw new InvalidOperationException("The saved legacy text could not be structured.");
        var blocks = BuildTextBlocks(job.SourceId, job.Id, legacy.NormalizedText, chunks, cancellationToken);

        await using var operation = await database.OpenWriteAsync(job.ProjectId, cancellationToken);
        await using var transaction = await operation.Db.Database.BeginTransactionAsync(cancellationToken);
        var source = await operation.Db.IngestSources.SingleAsync(item => item.Id == job.SourceId
            && item.ProjectId == job.ProjectId, cancellationToken);
        if (source.ActiveExtractionVersionId == job.Id)
            return source;
        if (source.ActiveExtractionVersionId != legacy.Id)
            throw new InvalidOperationException("The active source extraction changed during conversion.");
        var persistedJob = await operation.Db.IngestJobs.SingleAsync(item => item.Id == job.Id, cancellationToken);
        if (persistedJob.Status != IngestJobStatus.Running)
            throw new OperationCanceledException("Legacy conversion was stopped before publication.", cancellationToken);
        var nextOrdinal = await operation.Db.SourceExtractionVersions.Where(item => item.SourceId == source.Id)
            .MaxAsync(item => item.Ordinal, cancellationToken) + 1;
        operation.Db.SourceExtractionVersions.Add(new SourceExtractionVersion
        {
            Id = job.Id, SourceId = source.Id, Ordinal = nextOrdinal,
            Extractor = "legacy-text-conversion", ExtractorVersion = "1", OptionsJson = "{}",
            Status = SourceExtractionStatus.Ready, NormalizedText = legacy.NormalizedText,
            ContentHash = SourceRetentionValidator.Sha256(legacy.NormalizedText),
            Diagnostics = "Converted from saved legacy text. The original file remains unavailable; earlier evidence stays attached to its legacy extraction.",
        });
        operation.Db.IngestSourceChunks.AddRange(chunks);
        operation.Db.IngestSourceBlocks.AddRange(blocks);
        source.ActiveExtractionVersionId = job.Id;
        source.VectorIndexState = VectorIndexState.Stale;
        source.UpdatedAt = DateTime.UtcNow;
        persistedJob.TotalSourceChunks = chunks.Count;
        var project = await operation.Db.Projects.SingleAsync(item => item.Id == job.ProjectId, cancellationToken);
        project.UpdatedAt = DateTime.UtcNow;
        await operation.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return source;
    }

    private static List<IngestSourceBlock> BuildTextBlocks(Guid sourceId, Guid extractionId,
        string sourceText, IReadOnlyList<IngestSourceChunk> chunks, CancellationToken cancellationToken)
    {
        var blocks = new List<IngestSourceBlock>();
        foreach (var chunk in chunks)
        {
            for (var start = chunk.StartChar; start < chunk.EndChar;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var end = Math.Min(chunk.EndChar, start + IngestSourceBlock.MaximumNormalizedTextLength);
                var text = sourceText[start..end];
                blocks.Add(new IngestSourceBlock
                {
                    SourceId = sourceId, SourceExtractionVersionId = extractionId,
                    Index = blocks.Count, Kind = "Text", Title = chunk.Title, Locator = chunk.HeadingPath,
                    StartChar = start, EndChar = end, NormalizedText = text,
                    ContentHash = SourceRetentionValidator.Sha256(text), MetadataJson = "{}",
                });
                start = end;
            }
        }
        return blocks;
    }
}
