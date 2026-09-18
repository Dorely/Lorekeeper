using System.Security.Cryptography;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Lorekeeper.Chapters;
using Lorekeeper.Composition;
using Lorekeeper.Context;
using Lorekeeper.EntityVisuals;
using Lorekeeper.Fonts;
using Lorekeeper.Ingest;
using Lorekeeper.Knowledge;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence;
using Lorekeeper.Persistence.Repositories;
using Lorekeeper.ProjectArchive;
using Lorekeeper.Projects;
using Lorekeeper.Publish;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.ImportExport;

public sealed class ProjectImportJobProcessor(
    IAppDatabaseOperationFactory database, IGraphStore graph, IActService acts, IChapterService chapters, IProjectFactService projectFacts, IEntityTypeService entityTypeService, IOutlineGraphSync outlineGraphSync, IContextIndexingService contextIndexing, IEntityVisualExampleService entityVisualExamples, IBookBriefService bookBriefs, IManuscriptStyleService manuscriptStyles, IIngestVectorIndexingService ingestVectorIndexing, IVectorIndexWorkCoordinator indexWork, IProjectImportJobNotifier notifier, ILogger<ProjectImportJobProcessor> logger, IProjectImportFileStore? fileStore = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly IProjectImportFileStore _fileStore = fileStore ?? new ProjectImportFileStore();

    private static readonly HashSet<string> AppendStructuralNodeTypes =
    [
        EntityTypeService.ActNodeType,
        EntityTypeService.ChapterNodeType,
        EntityTypeService.EventNodeType,
    ];

    private static readonly HashSet<string> NonStructuralMergeExcludedTypes =
    [
        EntityTypeService.ProjectNodeType,
        EntityTypeService.ActNodeType,
        EntityTypeService.ChapterNodeType,
        EntityTypeService.EventNodeType,
        EntityTypeService.SourceNodeType,
        EntityTypeService.SourceChunkNodeType,
        EntityTypeService.SourceBlockNodeType,
    ];

    private sealed class ImportState
    {
        public Dictionary<string, GraphNode> NodeMap { get; } = new(StringComparer.Ordinal);
        public Dictionary<Guid, Guid> ImageMap { get; } = [];
        public Dictionary<Guid, Guid> ActMap { get; } = [];
        public Dictionary<Guid, Guid> ChapterMap { get; } = [];
        public Dictionary<Guid, Guid> DesignedPageMap { get; } = [];
        public Dictionary<Guid, Guid> PublicationSectionMap { get; } = [];
        public Dictionary<Guid, Guid> FontFamilyMap { get; } = [];
        public Dictionary<Guid, Guid> EditionMap { get; } = [];
        public Dictionary<Guid, Guid> CoreMatterMap { get; } = [];
        public Dictionary<Guid, Guid> CorePlacementMap { get; } = [];
        public Dictionary<Guid, Guid> IngestSourceMap { get; } = [];
        public Dictionary<Guid, Guid> IngestSourceChunkMap { get; } = [];
        public Dictionary<Guid, Guid> IngestSourcePageMap { get; } = [];
        public Dictionary<Guid, Guid> IngestSourceBlockMap { get; } = [];
        public List<Guid> CreatedActIds { get; } = [];
        public List<Guid> CreatedChapterIds { get; } = [];
        public List<Guid> ContextEntityIdsToReindex { get; } = [];
    }

    public async Task RunAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        ProjectImportJob? job;
        await using (var readOperation = await database.OpenReadAsync(cancellationToken))
            job = await readOperation.Repositories.ProjectImports.GetJobAsync(jobId, cancellationToken);
        if (job is null) return;
        if (job.Status is ProjectImportJobStatus.Committed or ProjectImportJobStatus.Indexing)
        {
            await ResumeCommittedIndexingAsync(job, cancellationToken);
            return;
        }
        if (job.Status is not (ProjectImportJobStatus.Staged or ProjectImportJobStatus.Validated)) return;

        var committed = false;

        try
        {
            var document = await ReadAndValidateAsync(job, cancellationToken);
            await ValidateManuscriptStyleCompatibilityAsync(
                job.ProjectId,
                document,
                cancellationToken);
            await MarkApplyingAsync(job, cancellationToken);

            var state = new ImportState();
            await using var databaseOperation = await database.OpenWriteAsync(job.ProjectId, cancellationToken);
            databaseOperation.ShareWithNestedOperations();
            var db = databaseOperation.Db;
            var projects = databaseOperation.Repositories.Projects;
            var imports = databaseOperation.Repositories.ProjectImports;
            job = await imports.GetJobAsync(jobId, cancellationToken)
                ?? throw new InvalidOperationException($"Import job {jobId:N} disappeared before execution.");
            var project = await projects.GetByIdAsync(job.ProjectId, cancellationToken)
                ?? throw new InvalidOperationException($"Project {job.ProjectId} not found.");
            await using (var indexDeferral = indexWork.BeginDeferral())
            await using (var importTransaction = await db.Database.BeginTransactionAsync(cancellationToken))
            {
                await outlineGraphSync.RepairProjectAsync(project.Id, cancellationToken);
                await entityTypeService.EnsureDefaultsAsync(project.Id, cancellationToken);
                await ImportProjectDirectionAsync(project, document, cancellationToken);
                await ImportPageSetupAsync(project.Id, document, cancellationToken);
                await StepAsync(job, "Prepared current project graph.", cancellationToken);

                await SeedProjectNodeMapAsync(project, document, state, cancellationToken);
                await ImportEntityTypesAsync(job, document, cancellationToken);
                await StepAsync(job, "Imported entity type definitions.", cancellationToken);

                if (document.ExportKind == ProjectExportKind.Full)
                {
                    if (job.InputKind == ProjectImportInputKind.LorekeeperArchive && document.ExportKind == ProjectExportKind.Full)
                        await ImportArchiveSourceClosureAsync(job, document, state, cancellationToken);
                    else
                        await ImportCanonicalIngestSourcesAsync(job, document, state, cancellationToken);
                    await ImportProjectImagesAsync(job, document, state, cancellationToken);
                    await StepAsync(job, "Imported project images.", cancellationToken);
                    await ImportProjectFontsAsync(job, document, state, cancellationToken);
                    await StepAsync(job, "Imported project fonts.", cancellationToken);
                    await ImportManuscriptStylesAsync(job, document.ManuscriptStyles, cancellationToken);
                    foreach (var (exportedId, pageId) in EffectiveDesignedPageIdentityMap(document))
                    {
                        if (!state.DesignedPageMap.TryGetValue(pageId, out var localPageId))
                        {
                            localPageId = Guid.NewGuid();
                            state.DesignedPageMap[pageId] = localPageId;
                        }
                        state.DesignedPageMap[exportedId] = localPageId;
                    }
                    foreach (var section in document.PublicationSections)
                        state.PublicationSectionMap[section.Id] = Guid.NewGuid();
                    foreach (var edition in document.PublicationEditions)
                        state.EditionMap[edition.Id] = Guid.NewGuid();
                    await AppendStructuralItemsAsync(job, document, state, cancellationToken);
                    if (document.FormatVersion >= 16 && document.PublicationBook is not null)
                        await ImportPublicationBookAsync(job.ProjectId, document.PublicationBook, state, cancellationToken);
                    foreach (var importedEdition in document.PublicationEditions
                        .OrderByDescending(edition => edition.IsDefault)
                        .ThenBy(edition => edition.Name))
                    {
                        await ImportPublicationEditionSettingsAsync(
                            job.ProjectId,
                            importedEdition,
                            state.ActMap,
                            state.ChapterMap,
                            state.ImageMap,
                            state.FontFamilyMap,
                            state.EditionMap,
                            state.DesignedPageMap,
                            state.PublicationSectionMap,
                            state.CoreMatterMap,
                            state.CorePlacementMap,
                            document.FormatVersion,
                            document.Chapters,
                            cancellationToken);
                    }
                    if (document.FormatVersion >= 19)
                        await ImportPublicationSectionsAsync(job.ProjectId, document, state, cancellationToken);
                    await ImportDesignedPagesAsync(job, document, state, cancellationToken);
                    if (document.FormatVersion < 18)
                        await MaterializeImportedLegacyEditionContentAsync(job.ProjectId, document, state, cancellationToken);
                    if (document.FormatVersion < 16)
                        await SeedImportedCoreBookAsync(job.ProjectId, document, state, cancellationToken);
                    if (document.FormatVersion < 19)
                        await PublicationSectionMigrationService.ConvertImportedLegacyProjectAsync(
                            db, job.ProjectId, cancellationToken);
                    await MaterializeImportedDesignedPageVariantsAsync(job.ProjectId, cancellationToken);
                    // Rebuild only after legacy release materialization and
                    // section conversion so every Core/release manuscript has
                    // an authoritative reverse placement row.
                    await RebuildImportedDesignedPagePlacementsAsync(job.ProjectId, cancellationToken);
                    await StepAsync(job, "Appended exported outline structure and imported publish page settings.", cancellationToken);
                }
                else
                {
                    await ImportProjectImagesAsync(job, document, state, cancellationToken);
                    await StepAsync(job, "Imported project images.", cancellationToken);
                    await StepAsync(job, "Skipped structural outline data for non-structural import.", cancellationToken);
                }

                await ImportGraphNodesAsync(job, document, state, cancellationToken);
                await StepAsync(job, "Merged graph entities and provenance nodes.", cancellationToken);

                await ImportGraphEdgesAsync(job, document, state, cancellationToken);
                await StepAsync(job, "Merged graph relationships.", cancellationToken);

                await ImportEntityVisualExamplesAsync(job, document, state, cancellationToken);

                await ImportManuscriptAnnotationsAsync(job.ProjectId, document, state, cancellationToken);
                if (document.FormatVersion >= 24)
                    await StepAsync(job, "Imported manuscript review annotations.", cancellationToken);

                await outlineGraphSync.RepairProjectAsync(project.Id, cancellationToken);
                await StepAsync(job, "Repaired graph outline links.", cancellationToken);
                job.Status = ProjectImportJobStatus.Committed;
                job.CurrentMessage = "Import committed; rebuilding indexes.";
                job.UpdatedAt = DateTime.UtcNow;
                imports.UpdateJob(job);
                await databaseOperation.SaveChangesAsync(cancellationToken);
                await importTransaction.CommitAsync(cancellationToken);
                committed = true;
            }

            await MarkIndexingAsync(job, CancellationToken.None);
            var indexedWithoutWarnings = await ReindexBestEffortAsync(job, state, CancellationToken.None);
            await MarkCompletedAsync(job, indexedWithoutWarnings, CancellationToken.None);
        }
        catch (OperationCanceledException) when (!committed)
        {
            await MarkCancelledAsync(job, CancellationToken.None);
        }
        catch (OperationCanceledException) when (committed)
        {
            await AddWarningAsync(job, "Indexing interrupted", "The imported project was committed; indexing can resume after restart.", CancellationToken.None);
            await MarkCompletedAsync(job, false, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await using (var readOperation = await database.OpenReadAsync(cancellationToken))
            {
                job = await readOperation.Repositories.ProjectImports.GetJobAsync(jobId, cancellationToken)
                    ?? throw new InvalidOperationException($"Import job {jobId:N} disappeared during rollback.", ex);
            }
            var failure = ex is DbUpdateConcurrencyException concurrency
                ? new InvalidOperationException(
                    $"Import concurrency failure ({string.Join(", ", concurrency.Entries.Select(entry => $"{entry.Metadata.ClrType.Name}:{entry.State}"))}).",
                    concurrency)
                : ex;
            if (committed)
            {
                await AddWarningAsync(job, "Post-commit import work failed", failure.Message, CancellationToken.None);
                await MarkCompletedAsync(job, false, CancellationToken.None);
            }
            else
            {
                await MarkFailedAsync(job, failure, CancellationToken.None);
            }
        }
    }

    private async Task ImportProjectDirectionAsync(
        Project project,
        ProjectExportDocument document,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var projects = databaseOperation.Repositories.Projects;
        if (string.IsNullOrWhiteSpace(project.ProjectGuidance)
            && !string.IsNullOrWhiteSpace(document.Project.EffectiveProjectGuidance))
        {
            project.ProjectGuidance = document.Project.EffectiveProjectGuidance.Trim();
            project.UpdatedAt = DateTime.UtcNow;
            projects.Update(project);
            await databaseOperation.SaveChangesAsync(cancellationToken);
        }

        if (document.BookBrief is not { } imported)
            return;

        var current = await bookBriefs.GetOrCreateAsync(project.Id, cancellationToken);
        await bookBriefs.UpdateAsync(project.Id, new BookBriefPatch
        {
            BookKind = current.BookKind == BookKind.Unspecified && imported.BookKind != BookKind.Unspecified
                ? imported.BookKind
                : null,
            Premise = Missing(current.Premise, imported.Premise),
            Genre = Missing(current.Genre, imported.Genre),
            PrimaryThemes = Missing(current.PrimaryThemes, imported.PrimaryThemes),
            Purpose = Missing(current.Purpose, imported.Purpose),
            CreativeConstraints = Missing(current.CreativeConstraints, imported.CreativeConstraints),
            TargetAudience = Missing(current.TargetAudience, imported.TargetAudience),
            MinimumReaderAge = current.MinimumReaderAge is null ? imported.MinimumReaderAge : null,
            MaximumReaderAge = current.MaximumReaderAge is null ? imported.MaximumReaderAge : null,
            ReadingLevelGuidance = Missing(current.ReadingLevelGuidance, imported.ReadingLevelGuidance),
            TargetWordCount = current.TargetWordCount is null ? imported.TargetWordCount : null,
            PointOfView = Missing(current.PointOfView, imported.PointOfView),
            Tense = Missing(current.Tense, imported.Tense),
            VoiceAndTone = Missing(current.VoiceAndTone, imported.VoiceAndTone),
            LanguageLocale = Missing(current.LanguageLocale, imported.LanguageLocale),
            HouseStyle = Missing(current.HouseStyle, imported.HouseStyle),
            ReadAloudPriority = current.ReadAloudPriority is null ? imported.ReadAloudPriority : null,
            AccessibilityGoals = Missing(current.AccessibilityGoals, imported.AccessibilityGoals),
            VisualDirection = Missing(current.VisualDirection, imported.VisualDirection),
        }, cancellationToken);
    }

    private static string? Missing(string current, string imported) =>
        string.IsNullOrWhiteSpace(current) && !string.IsNullOrWhiteSpace(imported)
            ? imported
            : null;

    private async Task ImportPageSetupAsync(
        Guid projectId,
        ProjectExportDocument document,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var setup = await db.ProjectPageSetups.SingleOrDefaultAsync(item => item.ProjectId == projectId, cancellationToken);
        if (setup is null)
        {
            setup = new ProjectPageSetup { ProjectId = projectId };
            db.ProjectPageSetups.Add(setup);
        }
        if (document.FormatVersion < 15 || document.PageSetup is not { } imported)
        {
            await db.SaveChangesAsync(cancellationToken);
            return;
        }
        setup.PageWidthInches = imported.PageWidthInches;
        setup.PageHeightInches = imported.PageHeightInches;
        setup.PageMarginInches = imported.PageMarginInches;
        setup.BodyFontSizePoints = imported.BodyFontSizePoints;
        setup.BodyLineHeight = imported.BodyLineHeight;
        setup.Revision = checked(setup.Revision + 1);
        setup.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task ImportCanonicalIngestSourcesAsync(
        ProjectImportJob job,
        ProjectExportDocument document,
        ImportState state,
        CancellationToken cancellationToken)
    {
        if (document.FormatVersion < 23 || document.IngestSources.Count == 0)
            return;

        await using var operation = await database.OpenWriteAsync(cancellationToken);
        operation.ShareWithNestedOperations();
        var db = operation.Db;
        foreach (var imported in document.IngestSources)
        {
            var sourceId = Guid.NewGuid();
            var extractionId = sourceId;
            state.IngestSourceMap[imported.Id] = sourceId;
            var source = new IngestSource
            {
                Id = sourceId,
                ProjectId = job.ProjectId,
                Title = imported.Title,
                SourceKind = imported.SourceKind,
                Description = imported.Description,
                Synopsis = imported.Synopsis,
                UserInstructions = imported.UserInstructions,
                SourceUrl = imported.SourceUrl,
                FinalUrl = imported.FinalUrl,
                CanonicalUrl = imported.CanonicalUrl,
                FetchedAt = imported.FetchedAt,
                ContentType = imported.ContentType,
                SourceMetadataJson = imported.SourceMetadataJson,
                VectorIndexState = VectorIndexState.Stale,
                VectorIndexedAt = null,
                VectorIndexError = null,
                ActiveExtractionVersionId = extractionId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Original = new SourceOriginal
                {
                    SourceId = sourceId,
                    State = SourceOriginalState.OriginalUnavailable,
                    FileName = imported.Title,
                    MediaType = imported.ContentType,
                    Length = 0,
                    Sha256 = null,
                    CreatedAt = imported.CreatedAt,
                },
            };
            source.ExtractionVersions.Add(new SourceExtractionVersion
            {
                Id = extractionId,
                SourceId = sourceId,
                Ordinal = 0,
                Extractor = "legacy-project-import",
                ExtractorVersion = $"json-v{document.FormatVersion}",
                OptionsJson = "{}",
                ContentHash = string.IsNullOrWhiteSpace(imported.SourceHash)
                    ? SourceRetentionValidator.Sha256(imported.SourceText)
                    : imported.SourceHash,
                Status = SourceExtractionStatus.LegacyImmutable,
                Diagnostics = "Imported from a legacy JSON project export; original bytes were not available.",
                NormalizedText = imported.SourceText,
                CreatedAt = imported.CreatedAt,
            });
            foreach (var importedPage in imported.Pages)
            {
                var pageId = Guid.NewGuid();
                state.IngestSourcePageMap[importedPage.Id] = pageId;
                source.SourcePages.Add(new IngestSourcePage
                {
                    Id = pageId,
                    SourceId = sourceId,
                    SourceExtractionVersionId = extractionId,
                    PageNumber = importedPage.PageNumber,
                    Text = importedPage.Text,
                    StartChar = importedPage.StartChar,
                    EndChar = importedPage.EndChar,
                    ExtractionMethod = importedPage.ExtractionMethod,
                    Width = importedPage.Width,
                    Height = importedPage.Height,
                    ImageHash = importedPage.ImageHash,
                    RenderSettingsJson = importedPage.RenderSettingsJson,
                    VisionProviderId = null,
                    VisionModelName = importedPage.VisionModelName,
                    Diagnostics = importedPage.Diagnostics,
                    CreatedAt = importedPage.CreatedAt,
                });
            }
            foreach (var importedChunk in imported.Chunks)
            {
                var chunkId = Guid.NewGuid();
                state.IngestSourceChunkMap[importedChunk.Id] = chunkId;
                source.SourceChunks.Add(new IngestSourceChunk
                {
                    Id = chunkId,
                    SourceId = sourceId,
                    SourceExtractionVersionId = extractionId,
                    Index = importedChunk.Index,
                    Title = importedChunk.Title,
                    HeadingPath = importedChunk.HeadingPath,
                    StartChar = importedChunk.StartChar,
                    EndChar = importedChunk.EndChar,
                    EstimatedTokenCount = importedChunk.EstimatedTokenCount,
                    TokenCountMethod = importedChunk.TokenCountMethod,
                    TokenEncodingName = importedChunk.TokenEncodingName,
                    TokenCountIsExact = importedChunk.TokenCountIsExact,
                    Summary = importedChunk.Summary,
                    AgentNotes = importedChunk.AgentNotes,
                    StructureStatus = importedChunk.StructureStatus,
                    CreatedAt = importedChunk.CreatedAt,
                    UpdatedAt = importedChunk.UpdatedAt,
                });
            }
            foreach (var importedBlock in imported.Blocks)
            {
                var blockId = Guid.NewGuid();
                var normalizedText = ReadLegacyBlockText(imported.SourceText, importedBlock.StartChar, importedBlock.EndChar);
                state.IngestSourceBlockMap[importedBlock.Id] = blockId;
                source.SourceBlocks.Add(new IngestSourceBlock
                {
                    Id = blockId,
                    SourceId = sourceId,
                    SourceExtractionVersionId = extractionId,
                    SourcePageId = importedBlock.SourcePageId is { } oldPageId
                        ? state.IngestSourcePageMap.GetValueOrDefault(oldPageId)
                        : null,
                    Index = importedBlock.Index,
                    Kind = importedBlock.Kind,
                    Title = importedBlock.Title,
                    Locator = importedBlock.Locator,
                    PageNumber = importedBlock.PageNumber,
                    StartChar = importedBlock.StartChar,
                    EndChar = importedBlock.EndChar,
                    NormalizedText = normalizedText,
                    ContentHash = SourceRetentionValidator.Sha256(normalizedText),
                    MetadataJson = importedBlock.MetadataJson,
                    CreatedAt = importedBlock.CreatedAt,
                });
            }
            db.IngestSources.Add(source);
        }
        await db.SaveChangesAsync(cancellationToken);

        var currentSelections = await bookBriefs.ListCanonSourcesAsync(job.ProjectId, cancellationToken);
        var importedSelections = document.BookBriefCanonSourceIds
            .Where(state.IngestSourceMap.ContainsKey)
            .Select(id => state.IngestSourceMap[id]);
        await bookBriefs.ReplaceCanonSourcesAsync(
            job.ProjectId,
            currentSelections.Select(source => source.SourceId).Concat(importedSelections).Distinct().ToArray(),
            cancellationToken);
    }

    private static string ReadLegacyBlockText(string sourceText, int start, int end)
    {
        var safeStart = Math.Clamp(start, 0, sourceText.Length);
        var safeEnd = Math.Clamp(end, safeStart, sourceText.Length);
        var length = Math.Min(safeEnd - safeStart, IngestSourceBlock.MaximumNormalizedTextLength);
        return sourceText.Substring(safeStart, length);
    }

    private async Task ImportArchiveSourceClosureAsync(
        ProjectImportJob job,
        ProjectExportDocument document,
        ImportState state,
        CancellationToken cancellationToken)
    {
        var key = new ProjectImportJobFileKey(job.StagedFileKey);
        await using var input = _fileStore.OpenRead(key);
        _ = await ProjectArchiveZip.ReadAsync(input, cancellationToken: cancellationToken);
        input.Position = 0;
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var sourceIds = await ReadArchiveJsonAsync<List<Guid>>(archive, "sources/index.json", cancellationToken);
        if (sourceIds.Distinct().Count() != sourceIds.Count || sourceIds.Any(id => id == Guid.Empty))
            throw new InvalidDataException("Archive source index is not a unique set of source identities.");

        await using var operation = await database.OpenWriteAsync(cancellationToken);
        operation.ShareWithNestedOperations();
        var db = operation.Db;
        var extractionMaps = new Dictionary<Guid, Guid>();
        var blockMaps = new Dictionary<Guid, Guid>();
        var extractionRecordsById = new Dictionary<Guid, ArchiveExtractionRecord>();
        var blockExtractionIds = new Dictionary<Guid, Guid>();
        var extractionPageNumbers = new Dictionary<Guid, HashSet<int>>();
        var sourceMaps = new Dictionary<Guid, Guid>();

        foreach (var exportedSourceId in sourceIds.Order())
        {
            var root = $"sources/{exportedSourceId:N}";
            var record = await ReadArchiveJsonAsync<ArchiveSourceRecord>(archive, $"{root}/source.json", cancellationToken);
            if (record.Id != exportedSourceId)
                throw new InvalidDataException("Archive source record identity does not match its path.");
            var localSourceId = Guid.NewGuid();
            sourceMaps.Add(exportedSourceId, localSourceId);
            state.IngestSourceMap[exportedSourceId] = localSourceId;

            var extractionEntries = archive.Entries
                .Where(entry => entry.FullName.StartsWith($"{root}/extractions/", StringComparison.Ordinal)
                    && entry.FullName.EndsWith("/extraction.json", StringComparison.Ordinal))
                .OrderBy(entry => entry.FullName, StringComparer.Ordinal)
                .ToArray();
            if (extractionEntries.Length == 0)
                throw new InvalidDataException("Archive source has no immutable extraction versions.");

            var extractionRecords = new List<(ArchiveExtractionRecord Record, string Root)>();
            foreach (var entry in extractionEntries)
            {
                var extraction = await ReadArchiveJsonAsync<ArchiveExtractionRecord>(archive, entry.FullName, cancellationToken);
                var extractionRoot = entry.FullName[..^"/extraction.json".Length];
                if (extraction.SourceId != exportedSourceId || extraction.Id == Guid.Empty || extraction.Ordinal < 0)
                    throw new InvalidDataException("Archive extraction ownership or identity is invalid.");
                if (extractionRecords.Any(item => item.Record.Id == extraction.Id || item.Record.Ordinal == extraction.Ordinal))
                    throw new InvalidDataException("Archive source has duplicate extraction identities or ordinals.");
                extractionRecords.Add((extraction, extractionRoot));
                extractionMaps.Add(extraction.Id, Guid.NewGuid());
                extractionRecordsById.Add(extraction.Id, extraction);
            }
            if (record.ActiveExtractionVersionId is not Guid activeId || !extractionMaps.ContainsKey(activeId))
                throw new InvalidDataException("Archive source active extraction is missing.");

            var active = extractionRecords.Single(item => item.Record.Id == activeId).Record;
            if (active.Status is not (SourceExtractionStatus.Ready or SourceExtractionStatus.LegacyImmutable))
                throw new InvalidDataException("Archive source active extraction is not readable.");
            var source = new IngestSource
            {
                Id = localSourceId,
                ProjectId = job.ProjectId,
                Title = record.Title,
                SourceKind = record.SourceKind,
                Description = record.Description,
                Synopsis = record.Synopsis,
                UserInstructions = record.UserInstructions,
                SourceUrl = record.SourceUrl,
                FinalUrl = record.FinalUrl,
                CanonicalUrl = record.CanonicalUrl,
                FetchedAt = record.FetchedAt,
                ContentType = record.ContentType,
                SourceMetadataJson = record.SourceMetadataJson,
                ActiveExtractionVersionId = extractionMaps[activeId],
                VectorIndexState = VectorIndexState.Stale,
                CreatedAt = record.CreatedAt,
                UpdatedAt = record.UpdatedAt,
            };
            db.IngestSources.Add(source);

            await ImportArchiveOriginalAsync(db, archive, root, exportedSourceId, localSourceId, cancellationToken);
            foreach (var (extraction, extractionRoot) in extractionRecords)
            {
                var localExtractionId = extractionMaps[extraction.Id];
                db.SourceExtractionVersions.Add(new SourceExtractionVersion
                {
                    Id = localExtractionId,
                    SourceId = localSourceId,
                    Ordinal = extraction.Ordinal,
                    Extractor = extraction.Extractor,
                    ExtractorVersion = extraction.ExtractorVersion,
                    OptionsJson = extraction.OptionsJson,
                    ContentHash = extraction.ContentHash,
                    Status = extraction.Status,
                    Diagnostics = extraction.Diagnostics,
                    NormalizedText = extraction.NormalizedText,
                    CreatedAt = extraction.CreatedAt,
                });
                await ImportArchiveExtractionChildrenAsync(db, archive, extractionRoot, exportedSourceId, localSourceId,
                    extraction.Id, localExtractionId, state, blockMaps, blockExtractionIds, extractionPageNumbers, cancellationToken);
            }
            await ImportArchiveLocationsAsync(db, archive, root, job.ProjectId, exportedSourceId, localSourceId,
                extractionMaps, extractionRecordsById, blockMaps, blockExtractionIds, extractionPageNumbers, cancellationToken);
        }

        await ImportArchiveBibliographyAsync(db, archive, job.ProjectId, sourceMaps, cancellationToken);
        var selections = document.BookBriefCanonSourceIds.Select(id => sourceMaps.GetValueOrDefault(id)).ToArray();
        if (selections.Any(id => id == Guid.Empty))
            throw new InvalidDataException("Archive canonical-source selection is not closed over its source index.");
        var existing = await bookBriefs.ListCanonSourcesAsync(job.ProjectId, cancellationToken);
        await bookBriefs.ReplaceCanonSourcesAsync(job.ProjectId,
            existing.Select(item => item.SourceId).Concat(selections).Distinct().ToArray(), cancellationToken);
        await operation.SaveChangesAsync(cancellationToken);
    }

    private static async Task ImportArchiveOriginalAsync(AppDbContext db, ZipArchive archive, string sourceRoot,
        Guid exportedSourceId, Guid localSourceId, CancellationToken cancellationToken)
    {
        var path = $"{sourceRoot}/original/manifest.json";
        if (archive.GetEntry(path) is null)
            throw new InvalidDataException("Archive source original manifest is missing.");
        var original = await ReadArchiveJsonAsync<ArchiveOriginalRecord>(archive, path, cancellationToken);
        if (original.SourceId != exportedSourceId)
            throw new InvalidDataException("Archive source original ownership is invalid.");
        var local = new SourceOriginal { SourceId = localSourceId, State = original.State, FileName = original.FileName,
            MediaType = original.MediaType, Length = original.Length, Sha256 = original.Sha256, CreatedAt = original.CreatedAt };
        if (original.State == SourceOriginalState.OriginalUnavailable)
        {
            if (original.Length != 0 || original.Sha256 is not null || original.Chunks.Count != 0)
                throw new InvalidDataException("Unavailable source originals cannot carry reconstructed bytes.");
            db.SourceOriginals.Add(local);
            return;
        }
        using var originalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long originalLength = 0;
        foreach (var chunk in original.Chunks.OrderBy(item => item.Index))
        {
            if (chunk.Index != local.Chunks.Count || chunk.ByteLength is <= 0 or > SourceOriginal.MaximumChunkBytes)
                throw new InvalidDataException("Archive source original chunks are not contiguous or bounded.");
            var bytes = await ReadArchiveBytesAsync(archive, chunk.Path, SourceOriginal.MaximumChunkBytes, cancellationToken);
            if (bytes.LongLength != chunk.ByteLength || !string.Equals(SourceRetentionValidator.Sha256(bytes), chunk.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Archive source original chunk hash does not match its manifest.");
            originalHash.AppendData(bytes);
            originalLength = checked(originalLength + bytes.LongLength);
            var existingBlob = await db.SourceOriginalBlobs.AsNoTracking()
                .Where(blob => blob.Sha256 == chunk.Sha256)
                .Select(blob => new { blob.Length, blob.Data })
                .SingleOrDefaultAsync(cancellationToken);
            if (existingBlob is not null
                && (existingBlob.Length != bytes.Length || !existingBlob.Data.AsSpan().SequenceEqual(bytes)))
            {
                throw new InvalidDataException("Archive source blob collides with different retained bytes.");
            }
            if (existingBlob is null)
            {
                var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT OR IGNORE INTO \"SourceOriginalBlobs\" (\"Sha256\", \"Length\", \"Data\") VALUES ({chunk.Sha256}, {bytes.Length}, {bytes})",
                    cancellationToken);
                if (inserted == 0)
                {
                    existingBlob = await db.SourceOriginalBlobs.AsNoTracking()
                        .Where(blob => blob.Sha256 == chunk.Sha256)
                        .Select(blob => new { blob.Length, blob.Data })
                        .SingleAsync(cancellationToken);
                    if (existingBlob.Length != bytes.Length || !existingBlob.Data.AsSpan().SequenceEqual(bytes))
                        throw new InvalidDataException("Archive source blob collides with different retained bytes.");
                }
            }
            local.Chunks.Add(new SourceOriginalChunk
            {
                SourceId = localSourceId,
                Index = chunk.Index,
                BlobSha256 = chunk.Sha256,
                ByteLength = bytes.Length,
            });
        }
        if (originalLength != original.Length
            || !string.Equals(Convert.ToHexStringLower(originalHash.GetHashAndReset()), original.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Archive source original length or full hash does not match its manifest.");
        }
        SourceRetentionValidator.MarkExternallyVerifiedOriginal(local);
        db.SourceOriginals.Add(local);
    }

    private static async Task ImportArchiveExtractionChildrenAsync(AppDbContext db, ZipArchive archive, string root,
        Guid exportedSourceId, Guid localSourceId, Guid exportedExtractionId, Guid localExtractionId, ImportState state,
        IDictionary<Guid, Guid> blockMaps, IDictionary<Guid, Guid> blockExtractionIds,
        IDictionary<Guid, HashSet<int>> extractionPageNumbers, CancellationToken cancellationToken)
    {
        var pages = new Dictionary<Guid, Guid>();
        foreach (var entry in ArchiveJsonEntries(archive, $"{root}/pages/"))
        {
            var page = await ReadArchiveJsonAsync<ArchivePageRecord>(archive, entry.FullName, cancellationToken);
            if (page.Id == Guid.Empty || !pages.TryAdd(page.Id, Guid.NewGuid())) throw new InvalidDataException("Archive pages are not unique.");
            db.IngestSourcePages.Add(new IngestSourcePage { Id = pages[page.Id], SourceId = localSourceId, SourceExtractionVersionId = localExtractionId,
                PageNumber = page.PageNumber, Text = page.Text, StartChar = page.StartChar, EndChar = page.EndChar, ExtractionMethod = page.ExtractionMethod,
                Width = page.Width, Height = page.Height, ImageHash = page.ImageHash, RenderSettingsJson = page.RenderSettingsJson, VisionProviderId = page.VisionProviderId,
                VisionModelName = page.VisionModelName, Diagnostics = page.Diagnostics, CreatedAt = page.CreatedAt });
            state.IngestSourcePageMap[page.Id] = pages[page.Id];
            if (!extractionPageNumbers.TryGetValue(exportedExtractionId, out var pageNumbers))
                extractionPageNumbers.Add(exportedExtractionId, pageNumbers = []);
            if (!pageNumbers.Add(page.PageNumber))
                throw new InvalidDataException("Archive extraction has duplicate page numbers.");
        }
        foreach (var entry in ArchiveJsonEntries(archive, $"{root}/chunks/"))
        {
            var chunk = await ReadArchiveJsonAsync<ArchiveChunkRecord>(archive, entry.FullName, cancellationToken);
            if (chunk.Id == Guid.Empty || state.IngestSourceChunkMap.ContainsKey(chunk.Id)) throw new InvalidDataException("Archive chunks are not unique.");
            var id = Guid.NewGuid(); state.IngestSourceChunkMap[chunk.Id] = id;
            db.IngestSourceChunks.Add(new IngestSourceChunk { Id = id, SourceId = localSourceId, SourceExtractionVersionId = localExtractionId,
                Index = chunk.Index, Title = chunk.Title, HeadingPath = chunk.HeadingPath, StartChar = chunk.StartChar, EndChar = chunk.EndChar,
                EstimatedTokenCount = chunk.EstimatedTokenCount, TokenCountMethod = chunk.TokenCountMethod, TokenEncodingName = chunk.TokenEncodingName,
                TokenCountIsExact = chunk.TokenCountIsExact, Summary = chunk.Summary, AgentNotes = chunk.AgentNotes, StructureStatus = chunk.StructureStatus,
                CreatedAt = chunk.CreatedAt, UpdatedAt = chunk.UpdatedAt });
        }
        foreach (var entry in ArchiveJsonEntries(archive, $"{root}/blocks/"))
        {
            var block = await ReadArchiveJsonAsync<ArchiveBlockRecord>(archive, entry.FullName, cancellationToken);
            if (block.Id == Guid.Empty || !blockMaps.TryAdd(block.Id, Guid.NewGuid()) || block.SourcePageId is Guid pageId && !pages.ContainsKey(pageId))
                throw new InvalidDataException("Archive blocks are not unique or reference a missing page.");
            state.IngestSourceBlockMap[block.Id] = blockMaps[block.Id];
            blockExtractionIds[block.Id] = exportedExtractionId;
            db.IngestSourceBlocks.Add(new IngestSourceBlock { Id = blockMaps[block.Id], SourceId = localSourceId, SourceExtractionVersionId = localExtractionId,
                SourcePageId = block.SourcePageId is Guid sourcePageId ? pages[sourcePageId] : null, Index = block.Index, Kind = block.Kind, Title = block.Title,
                Locator = block.Locator, PageNumber = block.PageNumber, StartChar = block.StartChar, EndChar = block.EndChar, NormalizedText = block.NormalizedText,
                ContentHash = block.ContentHash, MetadataJson = block.MetadataJson, CreatedAt = block.CreatedAt });
        }
    }

    private async Task ResumeCommittedIndexingAsync(ProjectImportJob job, CancellationToken cancellationToken)
    {
        // A committed import must never be replayed. The import transaction is
        // already durable, so recovery is strictly a best-effort refresh over
        // the current project state.
        await MarkIndexingAsync(job, cancellationToken);
        var indexedWithoutWarnings = await ReindexCommittedProjectAsync(job, cancellationToken);
        await MarkCompletedAsync(job, indexedWithoutWarnings, CancellationToken.None);
    }

    private static async Task ImportArchiveLocationsAsync(AppDbContext db, ZipArchive archive, string root, Guid projectId,
        Guid exportedSourceId, Guid localSourceId, IReadOnlyDictionary<Guid, Guid> extractionMaps,
        IReadOnlyDictionary<Guid, ArchiveExtractionRecord> extractionRecords, IReadOnlyDictionary<Guid, Guid> blockMaps,
        IReadOnlyDictionary<Guid, Guid> blockExtractionIds, IReadOnlyDictionary<Guid, HashSet<int>> extractionPageNumbers,
        CancellationToken cancellationToken)
    {
        foreach (var entry in ArchiveJsonEntries(archive, $"{root}/locations/"))
        {
            var location = await ReadArchiveJsonAsync<ArchiveLocationRecord>(archive, entry.FullName, cancellationToken);
            if (location.Id == Guid.Empty
                || location.SourceId != exportedSourceId
                || !extractionMaps.TryGetValue(location.ExtractionVersionId, out var extractionId)
                || !extractionRecords.TryGetValue(location.ExtractionVersionId, out var extraction)
                || location.SourceBlockId is Guid blockId && (!blockMaps.TryGetValue(blockId, out _) || blockExtractionIds.GetValueOrDefault(blockId) != location.ExtractionVersionId)
                || location.PageNumber is int pageNumber && (!extractionPageNumbers.TryGetValue(location.ExtractionVersionId, out var pages) || !pages.Contains(pageNumber))
                || location.NormalizedStart < 0
                || location.NormalizedLength < 0
                || location.NormalizedStart > extraction.NormalizedText.Length
                || location.NormalizedLength > extraction.NormalizedText.Length - location.NormalizedStart
                || !ProjectArchiveManifest.IsSha256(location.VerificationHash)
                || !Enum.IsDefined(location.ResolutionState))
                throw new InvalidDataException("Archive source location is not closed over its extraction or block.");
            var exactQuote = location.NormalizedLength == 0 ? string.Empty
                : extraction.NormalizedText.Substring(location.NormalizedStart, location.NormalizedLength);
            var quoteMatches = string.Equals(SourceRetentionValidator.Sha256(exactQuote), location.VerificationHash, StringComparison.OrdinalIgnoreCase);
            if (location.ResolutionState == SourceLocationResolutionState.Resolved
                && (location.NormalizedLength == 0 || !quoteMatches || !string.Equals(location.Quote, exactQuote, StringComparison.Ordinal)))
            {
                throw new InvalidDataException("A resolved archive location does not match its immutable extraction text.");
            }
            db.SourceLocations.Add(new SourceLocation { Id = Guid.NewGuid(), ProjectId = projectId, SourceId = localSourceId,
                ExtractionVersionId = extractionId, SourceBlockId = location.SourceBlockId is Guid value ? blockMaps[value] : null,
                PageNumber = location.PageNumber, NormalizedStart = location.NormalizedStart, NormalizedLength = location.NormalizedLength,
                Locator = location.Locator, Quote = location.Quote, VerificationHash = location.VerificationHash,
                ResolutionState = location.ResolutionState, CreatedAt = location.CreatedAt });
        }
    }

    private static async Task ImportArchiveBibliographyAsync(AppDbContext db, ZipArchive archive, Guid projectId,
        IReadOnlyDictionary<Guid, Guid> sourceMaps, CancellationToken cancellationToken)
    {
        var entries = ArchiveJsonEntries(archive, "bibliography/")
            .Concat(archive.Entries.Where(entry => entry.FullName.StartsWith("sources/", StringComparison.Ordinal)
                && entry.FullName.Contains("/bibliography/", StringComparison.Ordinal) && entry.FullName.EndsWith(".json", StringComparison.Ordinal)))
            .OrderBy(entry => entry.FullName, StringComparer.Ordinal);
        var seen = new HashSet<Guid>();
        foreach (var entry in entries)
        {
            var record = await ReadArchiveJsonAsync<ArchiveBibliographyRecord>(archive, entry.FullName, cancellationToken);
            if (record.Id == Guid.Empty || !seen.Add(record.Id)
                || record.SourceId is Guid sourceId && !sourceMaps.TryGetValue(sourceId, out _))
                throw new InvalidDataException("Archive bibliography is not closed over its source records.");
            db.BibliographicRecords.Add(new BibliographicRecord { Id = Guid.NewGuid(), ProjectId = projectId,
                SourceId = record.SourceId is Guid mapped ? sourceMaps[mapped] : null, Kind = record.Kind, Title = record.Title,
                ContainerTitle = record.ContainerTitle, AuthorsJson = record.AuthorsJson, EditorsJson = record.EditorsJson,
                IssuedYear = record.IssuedYear, Publisher = record.Publisher, PublisherPlace = record.PublisherPlace,
                Volume = record.Volume, Issue = record.Issue, Pages = record.Pages, Doi = record.Doi, Url = record.Url,
                AccessedAt = record.AccessedAt, Isbn = record.Isbn, Notes = record.Notes, CreatedAt = record.CreatedAt, UpdatedAt = record.UpdatedAt });
        }
    }

    private static IEnumerable<ZipArchiveEntry> ArchiveJsonEntries(ZipArchive archive, string prefix) => archive.Entries
        .Where(entry => entry.FullName.StartsWith(prefix, StringComparison.Ordinal) && entry.FullName.EndsWith(".json", StringComparison.Ordinal))
        .OrderBy(entry => entry.FullName, StringComparer.Ordinal);

    private static async Task<T> ReadArchiveJsonAsync<T>(ZipArchive archive, string path, CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry(path) ?? throw new InvalidDataException($"Archive is missing '{path}'.");
        if (entry.Length > ProjectArchiveLimits.Default.MaximumEntryBytes)
            throw new InvalidDataException($"Archive record '{path}' exceeds its configured limit.");
        await using var stream = entry.Open();
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException($"Archive record '{path}' is malformed.");
    }

    private static async Task<byte[]> ReadArchiveBytesAsync(ZipArchive archive, string path, long maximumBytes, CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry(path) ?? throw new InvalidDataException($"Archive is missing '{path}'.");
        if (entry.Length > maximumBytes || entry.Length > int.MaxValue)
            throw new InvalidDataException($"Archive record '{path}' exceeds its configured limit.");
        await using var stream = entry.Open();
        using var output = new MemoryStream(checked((int)entry.Length));
        await stream.CopyToAsync(output, cancellationToken);
        if (output.Length != entry.Length) throw new InvalidDataException($"Archive record '{path}' was truncated.");
        return output.ToArray();
    }

    private sealed record ArchiveSourceRecord(Guid Id, string Title, string SourceKind, string Description, string Synopsis, string UserInstructions, string SourceUrl, string FinalUrl, string CanonicalUrl, DateTime? FetchedAt, string ContentType, string SourceMetadataJson, Guid? ActiveExtractionVersionId, DateTime CreatedAt, DateTime UpdatedAt);
    private sealed record ArchiveOriginalChunk(int Index, string Path, long ByteLength, string Sha256);
    private sealed record ArchiveOriginalRecord(Guid SourceId, SourceOriginalState State, string FileName, string MediaType, long Length, string? Sha256, DateTime CreatedAt, List<ArchiveOriginalChunk> Chunks);
    private sealed record ArchiveExtractionRecord(Guid Id, Guid SourceId, int Ordinal, string Extractor, string ExtractorVersion, string OptionsJson, string ContentHash, SourceExtractionStatus Status, string Diagnostics, string NormalizedText, DateTime CreatedAt);
    private sealed record ArchiveChunkRecord(Guid Id, int Index, string Title, string HeadingPath, int StartChar, int EndChar, int EstimatedTokenCount, string TokenCountMethod, string? TokenEncodingName, bool TokenCountIsExact, string Summary, string AgentNotes, IngestSourceChunkStructureStatus StructureStatus, DateTime CreatedAt, DateTime UpdatedAt);
    private sealed record ArchivePageRecord(Guid Id, int PageNumber, string Text, int StartChar, int EndChar, string ExtractionMethod, int Width, int Height, string ImageHash, string RenderSettingsJson, int? VisionProviderId, string VisionModelName, string Diagnostics, DateTime CreatedAt);
    private sealed record ArchiveBlockRecord(Guid Id, Guid? SourcePageId, int Index, string Kind, string Title, string Locator, int? PageNumber, int StartChar, int EndChar, string NormalizedText, string ContentHash, string MetadataJson, DateTime CreatedAt);
    private sealed record ArchiveLocationRecord(Guid Id, Guid SourceId, Guid ExtractionVersionId, Guid? SourceBlockId, int? PageNumber, int NormalizedStart, int NormalizedLength, string Locator, string Quote, string VerificationHash, SourceLocationResolutionState ResolutionState, DateTime CreatedAt);
    private sealed record ArchiveBibliographyRecord(Guid Id, Guid? SourceId, BibliographicRecordKind Kind, string Title, string ContainerTitle, string AuthorsJson, string EditorsJson, int? IssuedYear, string Publisher, string PublisherPlace, string Volume, string Issue, string Pages, string Doi, string Url, DateTime? AccessedAt, string Isbn, string Notes, DateTime CreatedAt, DateTime UpdatedAt);

    private async Task<ProjectExportDocument> ReadAndValidateAsync(ProjectImportJob job, CancellationToken cancellationToken)
    {
        ProjectExportDocument document;
        try
        {
            document = await ReadStagedDocumentAsync(job, cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Import file is not valid Lorekeeper JSON: {ex.Message}", ex);
        }

        if (!string.Equals(document.FormatId, ProjectExportDocument.CurrentFormatId, StringComparison.Ordinal))
            throw new InvalidOperationException($"Unsupported import format '{document.FormatId}'.");
        if (document.FormatVersion < 1 || document.FormatVersion > ProjectExportDocument.CurrentFormatVersion)
            throw new InvalidOperationException($"Unsupported import format version {document.FormatVersion}.");
        document = AdaptLegacyCoverDescription(
            AdaptLegacySourceEvidence(
                AdaptLegacyImageSources(
                    AdaptLegacyPublicationEditions(AdaptLegacyManuscriptStyles(document)))));

        var duplicateNode = document.Nodes
            .GroupBy(node => StableKey(node.NodeType, node.Key), StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateNode is not null)
            throw new InvalidOperationException($"Import file contains duplicate graph node '{duplicateNode.Key}'.");

        var nodeKeys = document.Nodes
            .Select(node => StableKey(node.NodeType, node.Key))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var edge in document.Edges)
        {
            if (!nodeKeys.Contains(edge.From.StableKey))
                throw new InvalidOperationException($"Import edge references missing source node '{edge.From.StableKey}'.");
            if (!nodeKeys.Contains(edge.To.StableKey))
                throw new InvalidOperationException($"Import edge references missing target node '{edge.To.StableKey}'.");
        }

        ValidateChapterPayloads(document);
        ValidatePublicationPayloads(document);
        ValidateIngestSourcePayloads(document);
        ValidateManuscriptAnnotationPayloads(document);

        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var imports = databaseOperation.Repositories.ProjectImports;
        job.FormatId = job.InputKind == ProjectImportInputKind.LorekeeperArchive
            ? ProjectArchiveContract.FormatId
            : document.FormatId;
        job.FormatVersion = document.FormatVersion;
        job.ExportKind = document.ExportKind.ToString();
        job.Status = ProjectImportJobStatus.Validated;
        job.CurrentMessage = "Import validated; waiting to apply.";
        job.UpdatedAt = DateTime.UtcNow;
        imports.UpdateJob(job);
        await databaseOperation.SaveChangesAsync(cancellationToken);

        await AddReportAsync(
            job,
            ProjectImportReportItemKind.Validation,
            "Validated import file",
            $"{document.ExportKind} export from {document.Project.Name}",
            resourceType: "ProjectExport",
            resourceKey: document.Project.Id.ToString("N"),
            cancellationToken: cancellationToken);
        await StepAsync(job, "Validated import file.", cancellationToken);
        return document;
    }

    private async Task<ProjectExportDocument> ReadStagedDocumentAsync(ProjectImportJob job, CancellationToken cancellationToken)
    {
        if (job.StagedLength <= 0 || !ProjectArchiveManifest.IsSha256(job.StagedSha256))
            throw new InvalidDataException("Import staging declaration is invalid.");
        var key = new ProjectImportJobFileKey(job.StagedFileKey);
        await using var input = _fileStore.OpenRead(key);
        var (length, sha256) = await ComputeHashAsync(input, ProjectImportExportService.MaximumImportBytes, cancellationToken);
        if (length != job.StagedLength || !string.Equals(sha256, job.StagedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The staged import file changed after upload.");
        input.Position = 0;

        if (job.InputKind == ProjectImportInputKind.LegacyJson)
        {
            return await JsonSerializer.DeserializeAsync<ProjectExportDocument>(input, JsonOptions, cancellationToken)
                ?? throw new InvalidOperationException("Import file did not contain a project export document.");
        }

        var archive = await ProjectArchiveZip.ReadAsync(input, cancellationToken: cancellationToken);
        if (archive.Manifest.Policy is not (ProjectDependencyTraversalPolicy.FullArchive or ProjectDependencyTraversalPolicy.NonStructuralArchive))
            throw new InvalidDataException("History snapshot archives cannot be imported as projects.");
        input.Position = 0;
        using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var creative = zip.GetEntry("project/creative-state.json")
            ?? throw new InvalidDataException("Archive is missing its creative-state record.");
        if (creative.Length > ProjectArchiveLimits.Default.MaximumEntryBytes)
            throw new InvalidDataException("Archive creative-state record exceeds its configured limit.");
        await using var creativeStream = creative.Open();
        var document = await JsonSerializer.DeserializeAsync<ProjectExportDocument>(creativeStream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Archive creative-state record is invalid.");
        // `sources/` is the v1 archive authority. Do not use the legacy
        // SourceText projection embedded in creative-state.json when restoring
        // an archive: ImportArchiveSourceClosureAsync reads every immutable
        // extraction/original record and supplies graph remapping.
        if (!Guid.TryParse(archive.Manifest.ProjectId, out var manifestProjectId)
            || manifestProjectId != document.Project.Id)
        {
            throw new InvalidDataException("Archive manifest project identity does not match project/creative-state.json.");
        }

        return (await PopulateArchiveBinaryPayloadsAsync(zip, document, cancellationToken)) with { IngestSources = [] };
    }

    private static async Task<(long Length, string Sha256)> ComputeHashAsync(Stream input, long maximumBytes, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81_920];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            total = checked(total + read);
            if (total > maximumBytes)
                throw new InvalidDataException("Staged import exceeds the configured byte limit.");
            hash.AppendData(buffer, 0, read);
        }
        return (total, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static async Task<ProjectExportDocument> PopulateArchiveBinaryPayloadsAsync(
        ZipArchive archive,
        ProjectExportDocument document,
        CancellationToken cancellationToken)
    {
        var images = new List<ProjectExportImage>(document.Images.Count);
        foreach (var image in document.Images)
            images.Add(image with { Data = await ReadArchiveEntryAsync(archive, $"assets/images/{image.Id:N}", cancellationToken) });

        var families = new List<ProjectExportFontFamily>(document.FontFamilies.Count);
        foreach (var family in document.FontFamilies)
        {
            var faces = new List<ProjectExportFontFace>(family.Faces.Count);
            foreach (var face in family.Faces)
                faces.Add(face with { Data = await ReadArchiveEntryAsync(archive, $"assets/fonts/{face.Id:N}", cancellationToken) });
            families.Add(family with { Faces = faces });
        }
        return document with { Images = images, FontFamilies = families };
    }

    private static async Task<byte[]> ReadArchiveEntryAsync(ZipArchive archive, string path, CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry(path) ?? throw new InvalidDataException($"Archive is missing required binary '{path}'.");
        if (entry.Length > ProjectArchiveLimits.Default.MaximumEntryBytes || entry.Length > int.MaxValue)
            throw new InvalidDataException($"Archive binary '{path}' exceeds an importable size.");
        await using var input = entry.Open();
        using var output = new MemoryStream(checked((int)entry.Length));
        await input.CopyToAsync(output, cancellationToken);
        if (output.Length != entry.Length)
            throw new InvalidDataException($"Archive binary '{path}' was truncated.");
        return output.ToArray();
    }

    internal static void ValidateChapterPayloads(ProjectExportDocument document)
    {
        document = AdaptLegacyManuscriptStyles(document);
        if (document.FormatVersion >= 8)
        {
            foreach (var style in document.ManuscriptStyles)
            {
                ManuscriptStyleService.ValidateInput(new ManuscriptStyleInput(
                    style.Id,
                    style.Name,
                    style.Kind,
                    style.SemanticRole,
                    style.Definition));
            }
            if (document.ManuscriptStyles
                .GroupBy(
                    style => $"{style.Kind}:{style.Name.Trim().ToLowerInvariant()}",
                    StringComparer.Ordinal)
                .Any(group => group.Count() > 1))
            {
                throw new InvalidOperationException("Import file contains duplicate Book Text Styles.");
            }
            if (document.ManuscriptStyles
                .GroupBy(
                    style => $"{style.Kind}:{style.SemanticRole.Trim().ToLowerInvariant()}",
                    StringComparer.Ordinal)
                .Any(group => group.Count() > 1))
            {
                throw new InvalidOperationException("Import file maps more than one Book Text Style to the same semantic role.");
            }
        }
        var duplicateImage = document.Images
            .GroupBy(image => image.Id)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateImage is not null)
            throw new InvalidOperationException($"Import file contains duplicate image '{duplicateImage.Key:N}'.");
        var exportedImageIds = document.Images.Select(image => image.Id).ToHashSet();
        if (document.FormatVersion >= 29)
        {
            foreach (var image in document.Images.Where(image => image.DerivedFromImageId is not null))
            {
                var parentId = image.DerivedFromImageId!.Value;
                if (!exportedImageIds.Contains(parentId))
                {
                    throw new InvalidOperationException(
                        $"Imported upscale '{image.Id:N}' references missing source image '{parentId:N}'.");
                }

                var parent = document.Images.First(parentImage => parentImage.Id == parentId);
                if (parent.Data is null
                    || parent.Data.Length == 0
                    || string.IsNullOrWhiteSpace(parent.ContentType)
                    || !parent.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Imported upscale '{image.Id:N}' references source image '{parentId:N}', which cannot be imported.");
                }
            }

            ValidateImageLineageAcyclic(document.Images);
        }
        if (document.FormatVersion >= 12)
        {
            if (document.FontFamilies.GroupBy(family => family.Id).Any(group => group.Count() > 1)
                || document.FontFamilies.GroupBy(
                    family => family.Name.Trim(),
                    StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1)
                || document.FontFamilies.SelectMany(family => family.Faces)
                    .GroupBy(face => face.Id).Any(group => group.Count() > 1))
            {
                throw new InvalidOperationException("Import file contains duplicate project font records.");
            }
            foreach (var family in document.FontFamilies)
            {
                if (string.IsNullOrWhiteSpace(family.Name) || family.Faces.Count == 0)
                    throw new InvalidOperationException("Imported project font families require a name and at least one face.");
                if (family.Faces.GroupBy(face => (face.Weight, face.Italic)).Any(group => group.Count() > 1))
                    throw new InvalidOperationException($"Imported font family '{family.Name}' contains duplicate face variants.");
                foreach (var face in family.Faces)
                {
                    var normalized = ProjectFontBinary.Normalize(face.Data, face.FileName);
                    if (!string.Equals(
                            Convert.ToHexStringLower(SHA256.HashData(face.Data)),
                            face.Sha256,
                            StringComparison.OrdinalIgnoreCase)
                        || normalized.Weight != face.Weight
                        || normalized.Italic != face.Italic
                        || !string.Equals(normalized.ContentType, face.ContentType, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(normalized.FamilyName, family.Name, StringComparison.Ordinal)
                        || !string.Equals(normalized.SubfamilyName, face.SubfamilyName, StringComparison.Ordinal)
                        || !string.Equals(normalized.FileName, face.FileName, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"Imported font face {face.Id:N} failed binary metadata validation.");
                    }
                }
            }
        }
        var exportedStyles = document.FormatVersion >= 8
            ? document.ManuscriptStyles.Select(style => new ManuscriptStyleView(
                style.Id,
                style.Name,
                style.Kind,
                style.SemanticRole,
                style.Definition,
                style.Revision)).ToList()
            : [];
        var duplicateChapter = document.Chapters
            .GroupBy(chapter => chapter.Id)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateChapter is not null)
            throw new InvalidOperationException($"Import file contains duplicate chapter '{duplicateChapter.Key:N}'.");

        foreach (var chapter in document.Chapters)
        {
            try
            {
                var manuscript = document.FormatVersion >= 8
                    ? ReadCurrentManuscript(chapter, document.FormatVersion)
                    : ManuscriptCodec.FromPlainText(
                        chapter.Id,
                        chapter.Body,
                        revision: 1,
                        deterministicIds: true);
                if (document.FormatVersion >= 8)
                {
                    ManuscriptStyleService.ValidateDocumentReferences(manuscript, exportedStyles);
                    var missingFigure = manuscript.Content.FirstOrDefault(block =>
                        block.Type == ManuscriptBlockType.Figure
                        && block.ImageId is Guid imageId
                        && !exportedImageIds.Contains(imageId));
                    if (missingFigure is not null)
                    {
                        throw new InvalidOperationException(
                            $"Figure block {missingFigure.Id} references an image that is not included in the export.");
                    }
                    ValidateCurrentPageLayout(
                        chapter,
                        manuscript,
                        exportedImageIds,
                        document.FormatVersion >= 12
                            ? document.FontFamilies.Select(family => family.Id).ToHashSet()
                            : null);
                    ValidateCurrentIllustrationLayout(chapter, manuscript, exportedImageIds);
                }
                else
                {
                    _ = ManuscriptMigrationService.MigrateLegacyPicturePage(
                        chapter.Id,
                        chapter.Body ?? string.Empty,
                        chapter.PageLayoutJson,
                        manuscript);
                    _ = ManuscriptMigrationService.MigrateLegacyIllustrations(
                        chapter.Id,
                        chapter.Body ?? string.Empty,
                        chapter.IllustrationLayoutJson,
                        manuscript,
                        out _);
                }
            }
            catch (Exception exception) when (exception is JsonException
                or InvalidDataException
                or InvalidOperationException)
            {
                throw new InvalidOperationException(
                    $"Chapter {chapter.Id:N} contains invalid manuscript or visual-layout data: {exception.Message}",
                    exception);
            }
        }
        if (document.FormatVersion >= 14)
        {
            var chapterIds = document.Chapters.Select(chapter => chapter.Id).ToHashSet();
            var designedPages = EffectiveDesignedPages(document);
            if (designedPages.GroupBy(item => item.Id).Any(group => group.Count() > 1))
                throw new InvalidOperationException("Import file contains duplicate Designed Pages.");
            var compositionIds = designedPages.Select(item => item.Id).ToHashSet();
            foreach (var chapter in document.Chapters)
            {
                var manuscript = ReadCurrentManuscript(chapter, document.FormatVersion);
                var missing = manuscript.Content.FirstOrDefault(block =>
                    block.Type == ManuscriptBlockType.DesignedPage
                    && block.DesignedPageId is Guid compositionId
                    && !compositionIds.Contains(compositionId));
                if (missing is not null)
                    throw new InvalidOperationException($"Designed Page block {missing.Id} references a missing composition.");
            }
            var sectionIds = document.PublicationSections.Select(section => section.Id).ToHashSet();
            if (document.FormatVersion >= 19)
            {
                ValidatePublicationSections(document, chapterIds, compositionIds);
                foreach (var section in document.PublicationSections)
                {
                    var manuscript = ManuscriptCodec.Deserialize(section.ManuscriptJson, section.Id, section.Revision);
                    var missing = manuscript.Content.FirstOrDefault(block =>
                        block.Type == ManuscriptBlockType.DesignedPage
                        && block.DesignedPageId is Guid compositionId
                        && !compositionIds.Contains(compositionId));
                    if (missing is not null)
                        throw new InvalidOperationException($"Publication section {section.Id:N} references a missing Designed Page composition.");
                }
            }
            foreach (var composition in document.PageCompositions)
            {
                var hasChapter = composition.ChapterId is Guid compositionChapterId && chapterIds.Contains(compositionChapterId);
                var hasSection = document.FormatVersion >= 19
                    && composition.PublicationSectionId is Guid sectionId && sectionIds.Contains(sectionId);
                if (hasChapter == hasSection
                    || string.IsNullOrWhiteSpace(composition.Name)
                    || composition.Variants.GroupBy(variant => variant.GeometryKey, StringComparer.Ordinal).Any(group => group.Count() > 1))
                    throw new InvalidOperationException($"Page composition {composition.Id:N} has invalid ownership or variants.");
                var semantic = ManuscriptCodec.Deserialize(
                    composition.SemanticManuscriptJson,
                    composition.Id,
                    composition.Revision);
                foreach (var variant in composition.Variants)
                {
                    var scene = JsonSerializer.Deserialize<CompositionScene>(variant.SceneJson, ManuscriptCodec.JsonOptions)
                        ?? throw new InvalidOperationException($"Page composition variant {variant.Id:N} has no scene.");
                    DesignedPageService.Validate(scene, semantic);
                    ValidateExportSceneReferences(
                        scene,
                        exportedImageIds,
                        document.FontFamilies.Select(family => family.Id).ToHashSet(),
                        allowCoverBindings: false,
                        $"Page composition variant {variant.Id:N}");
                }
            }
            if (document.FormatVersion >= 31)
            {
                foreach (var page in designedPages)
                foreach (var content in page.Contents)
                {
                    if (content.DesignedPageId != page.Id
                        || content.Variants.GroupBy(variant => variant.GeometryKey, StringComparer.Ordinal).Any(group => group.Count() > 1)
                        || (content.ActiveVariantId is Guid activeVariantId && !content.Variants.Any(variant => variant.Id == activeVariantId)))
                    {
                        throw new InvalidOperationException($"Designed Page {page.Id:N} has invalid content or variants.");
                    }
                }
            }
        }
    }

    private static void ValidatePublicationSections(
        ProjectExportDocument document,
        IReadOnlySet<Guid> chapterIds,
        IReadOnlySet<Guid> compositionIds)
    {
        if (document.PublicationSections.GroupBy(item => item.Id).Any(group => group.Count() > 1))
            throw new InvalidOperationException("Import file contains duplicate publication sections.");
        var editionIds = document.PublicationEditions.Select(item => item.Id).ToHashSet();
        var actIds = document.Acts.Select(item => item.Id).ToHashSet();
        var core = document.PublicationSections.Where(item => item.EditionId == null).ToDictionary(item => item.Id);
        if (document.PublicationSections
            .GroupBy(item => (item.EditionId, item.SystemRole))
            .Any(group => group.Key.SystemRole != PublicationSectionSystemRole.None && group.Count() > 1))
            throw new InvalidOperationException("Import file contains duplicate generated publication sections.");

        foreach (var section in document.PublicationSections)
        {
            if (section.EditionId is Guid editionId && !editionIds.Contains(editionId))
                throw new InvalidOperationException($"Publication section {section.Id:N} references a missing release.");
            if (section.CoreSectionId is Guid coreId
                && (section.EditionId is null || !core.ContainsKey(coreId)))
                throw new InvalidOperationException($"Publication section {section.Id:N} has an invalid Core Book source.");
            if (string.IsNullOrWhiteSpace(section.Title)
                || section.Title.Trim().Length > 500
                || section.Title.Contains('\r')
                || section.Title.Contains('\n')
                || !Enum.IsDefined(section.Kind)
                || !Enum.IsDefined(section.SystemRole)
                || !Enum.IsDefined(section.Anchor)
                || !Enum.IsDefined(section.InclusionMode)
                || (document.FormatVersion >= 21 && !Enum.IsDefined(section.StartSide))
                || section.LocalOrder < 0
                || section.Revision < 0)
                throw new InvalidOperationException($"Publication section {section.Id:N} has invalid metadata.");

            var requiresTarget = section.Anchor is PublicationSectionAnchor.BeforeAct
                or PublicationSectionAnchor.AfterAct
                or PublicationSectionAnchor.BeforeChapter
                or PublicationSectionAnchor.AfterChapter;
            var targetExists = section.TargetKind switch
            {
                PublishOutlineTargetKind.Act => section.TargetId is Guid targetId && actIds.Contains(targetId),
                PublishOutlineTargetKind.Chapter => section.TargetId is Guid targetId && chapterIds.Contains(targetId),
                _ => false,
            };
            if (requiresTarget != targetExists
                || (!requiresTarget && (section.TargetKind is not null || section.TargetId is not null)))
                throw new InvalidOperationException($"Publication section {section.Id:N} has an invalid outline anchor.");

            var manuscript = ManuscriptCodec.Deserialize(section.ManuscriptJson, section.Id, section.Revision);
            foreach (var block in manuscript.Content.Where(block => block.DesignedPageId.HasValue))
            {
                if (!compositionIds.Contains(block.DesignedPageId!.Value))
                    throw new InvalidOperationException($"Publication section {section.Id:N} references a missing composition.");
            }
        }
    }

    internal static void ValidatePublicationPayloads(ProjectExportDocument document)
    {
        if (document.FormatVersion < 25
            && ((document.PublicationBook?.RectoChapterStarts ?? false)
                || document.PublicationEditions.Any(edition => edition.RectoChapterStarts
                    || edition.OverrideFields.Contains(PublicationEditionOverrideField.RectoChapterStarts))))
        {
            throw new InvalidOperationException(
                $"Import file contains recto chapter-start data introduced after export format v{document.FormatVersion}.");
        }
        if (document.FormatVersion < 10) return;
        if (document.PublicationEditions.GroupBy(edition => edition.Id).Any(group => group.Count() > 1)
            || document.PublicationEditions.GroupBy(
                edition => edition.Name.Trim(),
                StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new InvalidOperationException("Import file contains duplicate publication releases.");
        if (document.FormatVersion >= 16 && document.PublicationBook is null)
            throw new InvalidOperationException("Import file does not contain its Core Book.");
        if (document.FormatVersion >= 16 && document.PublicationEditions.Any(edition => edition.IsDefault))
            throw new InvalidOperationException("Import file contains obsolete default-release state.");
        if (document.FormatVersion < 16 && document.PublicationBook is not null)
            throw new InvalidOperationException(
                $"Import file contains Core Book data introduced after export format v{document.FormatVersion}.");
        if (document.PublicationEditions.Count(edition => edition.IsDefault) > 1)
            throw new InvalidOperationException("Legacy import file contains more than one default publication release.");
        var sharedIsbnGroups = document.PublicationEditions
            .Select(edition => new
            {
                Edition = edition,
                Isbn = PublicationIsbn.NormalizeValidOrEmpty(edition.Isbn),
            })
            .Where(item => item.Isbn.Length > 0)
            .GroupBy(item => item.Isbn, StringComparer.Ordinal)
            .Where(group => group.Count() > 1);
        foreach (var group in sharedIsbnGroups)
        {
            if (group.Select(item => item.Edition.Format).Distinct().Count() != 1
                || group.Select(item => ExportBibliographicSignature(item.Edition)).Distinct(StringComparer.Ordinal).Count() != 1)
            {
                throw new InvalidOperationException(
                    "Import file shares an ISBN-13 across incompatible product forms or bibliographic content.");
            }
        }
        var actIds = document.Acts.Select(act => act.Id).ToHashSet();
        var chapterIds = document.Chapters.Select(chapter => chapter.Id).ToHashSet();
        var chapterParents = document.Chapters.ToDictionary(chapter => chapter.Id, chapter => chapter.ActId);
        var imageIds = document.Images.Select(image => image.Id).ToHashSet();
        var stylesById = document.ManuscriptStyles.ToDictionary(style => style.Id);
        var importedStyleViews = document.ManuscriptStyles
            .Select(style => new ManuscriptStyleView(
                style.Id,
                style.Name,
                style.Kind,
                style.SemanticRole,
                style.Definition,
                style.Revision))
            .ToList();
        var coreMatterIds = new HashSet<Guid>();
        var corePlacementIds = new HashSet<Guid>();
        if (document.PublicationBook is { } book)
        {
            ValidatePublicationBookPayload(
                book,
                actIds,
                chapterIds,
                chapterParents,
                imageIds,
                importedStyleViews,
                document.FontFamilies.Select(family => family.Id).ToHashSet());
            coreMatterIds = book.LegacyMatter.Select(item => item.Id).ToHashSet();
            corePlacementIds = book.LegacyImagePlacements.Select(item => item.Id).ToHashSet();
        }
        foreach (var edition in document.PublicationEditions)
        {
            if (string.IsNullOrWhiteSpace(edition.Name)
                || !Enum.IsDefined(edition.Format)
                || !Enum.IsDefined(edition.Vendor)
                || !Enum.IsDefined(edition.Status)
                || !Enum.IsDefined(edition.Binding)
                || !Enum.IsDefined(edition.Paper)
                || !Enum.IsDefined(edition.Ink)
                || !Enum.IsDefined(edition.TitlePageMode)
                || (document.FormatVersion < 14
                    && (!Enum.IsDefined(edition.PrintPicturePageSpreadMode)
                        || !Enum.IsDefined(edition.EpubPicturePageSpreadMode)))
                || !double.IsFinite(edition.PageWidthInches)
                || !double.IsFinite(edition.PageHeightInches)
                || !double.IsFinite(edition.PageMarginInches)
                || !double.IsFinite(edition.ImportedBodyFontSizePoints)
                || !double.IsFinite(edition.ImportedBodyLineHeight)
                || edition.PageWidthInches is < 3 or > 24
                || edition.PageHeightInches is < 3 or > 24
                || edition.PageMarginInches < 0.125
                || edition.PageMarginInches > Math.Min(edition.PageWidthInches, edition.PageHeightInches) / 3
                || edition.ImportedBodyFontSizePoints is < 7 or > 72
                || edition.ImportedBodyLineHeight is < 1 or > 2.4
                || edition.Name.Trim().Length > 120
                || edition.TitleOverride.Trim().Length > 500
                || edition.Subtitle.Trim().Length > 500
                || edition.Author.Trim().Length > 500
                || edition.Language.Trim().Length > 40
                || edition.Publisher.Trim().Length > 500
                || edition.Copyright.Trim().Length > 100_000
                || edition.Description.Trim().Length > 100_000)
                throw new InvalidOperationException($"Publication edition {edition.Id:N} has invalid product settings.");
            _ = PublicationIsbn.NormalizeValidOrEmpty(edition.Isbn);
            if (document.FormatVersion >= 13)
            {
                if (edition.SelectedCoverChapterId is not null)
                    throw new InvalidOperationException($"Publication edition {edition.Id:N} contains an obsolete cover chapter reference.");
                if (edition.SelectedCoverImageId is Guid coverImageId && !imageIds.Contains(coverImageId))
                    throw new InvalidOperationException($"Publication edition {edition.Id:N} references a missing cover image.");
            }
            else if (edition.SelectedCoverImageId is not null)
            {
                throw new InvalidOperationException(
                    $"Publication edition {edition.Id:N} contains a cover image field introduced after export format v{document.FormatVersion}.");
            }
            else if (edition.SelectedCoverChapterId is Guid coverId
                && document.Chapters.FirstOrDefault(chapter => chapter.Id == coverId)
                    is not { VisualMode: ChapterVisualMode.PicturePage })
            {
                throw new InvalidOperationException($"Publication edition {edition.Id:N} references a missing cover chapter.");
            }
            if (edition.CoverDesign is { } cover
                && (!Enum.IsDefined(cover.BarcodeMode)
                    || (document.FormatVersion >= 30 && cover.LegacyBackCopy is not null)
                    || cover.Title.Trim().Length is < 1 or > 160
                    || cover.Subtitle.Trim().Length > 240
                    || cover.Author.Trim().Length > 160
                    || cover.SpineText.Trim().Length > 120
                    || !System.Text.RegularExpressions.Regex.IsMatch(cover.BackgroundColor, "^#[0-9a-fA-F]{6}$")
                    || !double.IsFinite(cover.ImageCropXPercent)
                    || !double.IsFinite(cover.ImageCropYPercent)
                    || cover.ImageCropXPercent is < 0 or > 100
                    || cover.ImageCropYPercent is < 0 or > 100
                    || cover.Revision < 0))
                throw new InvalidOperationException($"Publication edition {edition.Id:N} has an invalid cover design.");
            if (edition.CoverDesign is { CompositionSceneJson.Length: > 0 } coverWithScene)
            {
                var scene = JsonSerializer.Deserialize<CompositionScene>(
                    coverWithScene.CompositionSceneJson,
                    ManuscriptCodec.JsonOptions)
                    ?? throw new InvalidOperationException($"Publication edition {edition.Id:N} has an empty cover scene.");
                DesignedPageService.Validate(
                    scene,
                    ManuscriptCodec.CreateEmpty(edition.Id),
                    allowCanonicalTextBindings: true);
                ValidateExportSceneReferences(
                    scene,
                    imageIds,
                    document.FontFamilies.Select(family => family.Id).ToHashSet(),
                    allowCoverBindings: true,
                    $"Publication edition {edition.Id:N} cover");
                if (edition.Format == PublicationEditionFormat.Paperback
                    ? scene.Surface.Kind != CompositionSurfaceKind.FacingSpread
                    : scene.Surface.Kind != CompositionSurfaceKind.SinglePage
                        || scene.Objects.Any(item => item.RegionConstraint is CompositionRegionConstraint.Back
                            or CompositionRegionConstraint.Spine
                            or CompositionRegionConstraint.BarcodeReserve))
                {
                    throw new InvalidOperationException($"Publication edition {edition.Id:N} has a cover scene incompatible with its format.");
                }
            }
            if (edition.OutlineItems.GroupBy(item => (item.TargetKind, item.TargetId)).Any(group => group.Count() > 1)
                || edition.LegacyMatter.GroupBy(item => item.Id).Any(group => group.Count() > 1)
                || edition.LegacyStyleMappings.GroupBy(item => item.ManuscriptStyleDefinitionId).Any(group => group.Count() > 1)
                || edition.LegacyImagePlacements.GroupBy(item => item.Id).Any(group => group.Count() > 1))
                throw new InvalidOperationException($"Publication edition {edition.Id:N} contains duplicate child records.");
            if (edition.OverrideFields.Distinct().Count() != edition.OverrideFields.Count
                || (document.FormatVersion >= 18 && edition.OverrideFields.Any(field => !Enum.IsDefined(field)))
                || (document.FormatVersion < 18 && edition.OverrideFields.Any(field => (int)field is < 0 or > 21)))
                throw new InvalidOperationException($"Publication release {edition.Id:N} contains invalid override markers.");
            if (document.FormatVersion >= 26
                && (!Enum.IsDefined(edition.PrintProjectUse)
                    || !Enum.IsDefined(edition.PrintIdentifierMode)
                    || !Enum.IsDefined(edition.PrintCoverSubmissionMode)
                    || edition.CoverDesign is { } coverDesign && !Enum.IsDefined(coverDesign.SpineReadingDirection)))
                throw new InvalidOperationException($"Publication edition {edition.Id:N} contains invalid print-use or cover-orientation values.");
            if (edition.OutlineItems.Any(item => item.SortOrder < 0)
                || edition.OutlineItems.GroupBy(item => item.SortOrder).Any(group => group.Count() > 1))
            {
                throw new InvalidOperationException($"Publication edition {edition.Id:N} contains invalid content order values.");
            }
            if (document.FormatVersion >= 22)
            {
                var validSectionOrderIds = document.PublicationSections
                    .Where(section => section.EditionId == null
                        || section.EditionId == edition.Id && section.CoreSectionId == null)
                    .Select(section => section.Id)
                    .ToHashSet();
                if (edition.PublicationSectionOrder.Any(item => item.Value < 0 || !validSectionOrderIds.Contains(item.Key)))
                    throw new InvalidOperationException($"Publication edition {edition.Id:N} contains an invalid publication section order overlay.");
            }
            if (document.FormatVersion < 16)
            {
                ValidateImportedLegacyOutlineOrder(
                        edition.OutlineItems
                            .OrderBy(item => item.SortOrder)
                            .Select(item => (item.TargetKind, item.TargetId))
                            .ToList(),
                        actIds,
                        chapterParents);
            }
            foreach (var item in edition.OutlineItems)
            {
                var exists = item.TargetKind == PublishOutlineTargetKind.Act
                    ? actIds.Contains(item.TargetId)
                    : item.TargetKind == PublishOutlineTargetKind.Chapter && chapterIds.Contains(item.TargetId);
                if (!exists)
                    throw new InvalidOperationException($"Publication edition {edition.Id:N} references missing outline content.");
            }
            foreach (var matter in edition.LegacyMatter)
            {
                if (matter.CoreMatterId is Guid coreMatterId && !coreMatterIds.Contains(coreMatterId))
                    throw new InvalidOperationException($"Publication release {edition.Id:N} references missing Core matter.");
                if (matter.IsExcluded && matter.CoreMatterId is null)
                    throw new InvalidOperationException($"Publication release {edition.Id:N} excludes matter that is not from Core Book.");
                if (!Enum.IsDefined(matter.Kind)
                    || !Enum.IsDefined(matter.Location)
                    || IsLegacyGeneratedMatterKind(matter.Kind)
                    || matter.Revision < 0
                    || matter.SortOrder < 0
                    || matter.Title.Trim().Length is < 1 or > 500
                    || matter.Title.Contains('\r')
                    || matter.Title.Contains('\n'))
                    throw new InvalidOperationException($"Publication edition {edition.Id:N} contains invalid matter metadata.");
                var manuscript = ManuscriptCodec.Deserialize(matter.ManuscriptJson, matter.Id, matter.Revision);
                if (manuscript.Content.Any(block =>
                    block.Type == ManuscriptBlockType.Figure
                    && block.ImageId is Guid imageId
                    && !imageIds.Contains(imageId)))
                {
                    throw new InvalidOperationException(
                        $"Publication matter {matter.Id:N} references an image missing from the import.");
                }
                ManuscriptStyleService.ValidateDocumentReferences(manuscript, importedStyleViews);
            }
            if (edition.LegacyStyleMappings.Any(mapping => !stylesById.ContainsKey(mapping.ManuscriptStyleDefinitionId)))
                throw new InvalidOperationException($"Publication edition {edition.Id:N} references a missing Book Text Style.");
            foreach (var mapping in edition.LegacyStyleMappings)
                _ = ManuscriptStyleService.NormalizeOverride(
                    stylesById[mapping.ManuscriptStyleDefinitionId].Kind,
                    mapping.Override);
            if (edition.LegacyImagePlacements.Any(placement => !imageIds.Contains(placement.AssetId)))
                throw new InvalidOperationException($"Publication edition {edition.Id:N} references a missing image.");
            foreach (var placement in edition.LegacyImagePlacements)
            {
                if (placement.CorePlacementId is Guid corePlacementId && !corePlacementIds.Contains(corePlacementId))
                    throw new InvalidOperationException($"Publication release {edition.Id:N} references a missing Core image placement.");
                if (placement.IsExcluded && placement.CorePlacementId is null)
                    throw new InvalidOperationException($"Publication release {edition.Id:N} excludes an image placement that is not from Core Book.");
                var targetExists = placement.TargetKind == PublishOutlineTargetKind.Act
                    ? actIds.Contains(placement.TargetId)
                    : placement.TargetKind == PublishOutlineTargetKind.Chapter
                        && chapterIds.Contains(placement.TargetId);
                var kindValid = placement.TargetKind == PublishOutlineTargetKind.Act
                    ? placement.PlacementKind is PublicationImagePlacementKind.BeforeAct
                        or PublicationImagePlacementKind.AfterAct
                    : placement.PlacementKind is PublicationImagePlacementKind.BeforeChapter
                        or PublicationImagePlacementKind.ChapterOpening
                        or PublicationImagePlacementKind.ChapterEnding
                        or PublicationImagePlacementKind.AfterChapter;
                if (!targetExists || !kindValid || placement.SortOrder < 0)
                    throw new InvalidOperationException($"Publication edition {edition.Id:N} contains an invalid image placement.");
            }
        }
    }

    private async Task ImportProjectFontsAsync(
        ProjectImportJob job,
        ProjectExportDocument document,
        ImportState state,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        if (document.FormatVersion < 12 || document.FontFamilies.Count == 0)
            return;

        var existingNames = await db.ProjectFontFamilies
            .Where(family => family.ProjectId == job.ProjectId)
            .Select(family => family.Name)
            .ToListAsync(cancellationToken);
        foreach (var importedFamily in document.FontFamilies)
        {
            var baseName = importedFamily.Name.Trim();
            var name = baseName;
            for (var suffix = 2; existingNames.Contains(name, StringComparer.OrdinalIgnoreCase); suffix++)
                name = $"{baseName} (Imported {suffix})";
            existingNames.Add(name);
            var family = new ProjectFontFamily
            {
                ProjectId = job.ProjectId,
                Name = name,
                EmbeddingRightsConfirmed = importedFamily.EmbeddingRightsConfirmed,
                RightsDeclaration = importedFamily.RightsDeclaration,
            };
            db.ProjectFontFamilies.Add(family);
            foreach (var importedFace in importedFamily.Faces)
            {
                var normalized = ProjectFontBinary.Normalize(importedFace.Data, importedFace.FileName);
                family.Faces.Add(new ProjectFontFace
                {
                    Family = family,
                    FamilyId = family.Id,
                    SubfamilyName = normalized.SubfamilyName,
                    FileName = normalized.FileName,
                    ContentType = normalized.ContentType,
                    Weight = normalized.Weight,
                    Italic = normalized.Italic,
                    Data = normalized.Data,
                });
            }
            state.FontFamilyMap[importedFamily.Id] = family.Id;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task ImportManuscriptStylesAsync(
        ProjectImportJob job,
        IReadOnlyList<ProjectExportManuscriptStyle> importedStyles,
        CancellationToken cancellationToken)
    {
        var existing = (await manuscriptStyles.ListAsync(job.ProjectId, cancellationToken)).ToList();
        var usedNames = existing
            .GroupBy(style => style.Kind)
            .ToDictionary(
                group => group.Key,
                group => group.Select(style => style.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
        foreach (var kind in Enum.GetValues<ManuscriptStyleKind>())
            usedNames.TryAdd(kind, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        foreach (var imported in importedStyles)
        {
            var roleMatch = existing.FirstOrDefault(style =>
                style.Kind == imported.Kind
                && string.Equals(
                    style.SemanticRole,
                    imported.SemanticRole,
                    StringComparison.OrdinalIgnoreCase));
            if (roleMatch is not null)
            {
                if (roleMatch.Definition != imported.Definition)
                    throw new InvalidOperationException(
                        $"Book Text Style '{imported.Name}' conflicts with the target project.");
                continue;
            }
            var name = imported.Name.Trim();
            var collision = existing.FirstOrDefault(style =>
                style.Kind == imported.Kind
                && string.Equals(style.Name, name, StringComparison.OrdinalIgnoreCase));
            if (collision is not null
                && collision.SemanticRole == imported.SemanticRole
                && collision.Definition == imported.Definition)
            {
                continue;
            }
            if (collision is not null)
                name = AllocateImportedStyleName(name, usedNames[imported.Kind], forceSuffix: true);
            else
                usedNames[imported.Kind].Add(name);
            var created = await manuscriptStyles.UpsertAsync(
                job.ProjectId,
                new ManuscriptStyleInput(
                    null,
                    name,
                    imported.Kind,
                    imported.SemanticRole,
                    imported.Definition),
                cancellationToken);
            existing.Add(created);
            await AddReportAsync(
                job,
                ProjectImportReportItemKind.Structural,
                $"Imported Book Text Style {created.Name}",
                $"{created.Kind} style for semantic role {created.SemanticRole}.",
                "ManuscriptStyle",
                created.Id.ToString("N"),
                cancellationToken: cancellationToken);
        }
    }

    private async Task ValidateManuscriptStyleCompatibilityAsync(
        Guid projectId,
        ProjectExportDocument document,
        CancellationToken cancellationToken)
    {
        var existing = await manuscriptStyles.ListAsync(projectId, cancellationToken);
        foreach (var imported in document.ManuscriptStyles)
        {
            var roleMatch = existing.FirstOrDefault(style =>
                style.Kind == imported.Kind
                && string.Equals(
                    style.SemanticRole,
                    imported.SemanticRole,
                    StringComparison.OrdinalIgnoreCase));
            if (roleMatch is not null && roleMatch.Definition != imported.Definition)
            {
                throw new InvalidOperationException(
                    $"Book Text Style '{imported.Name}' conflicts with the target project's "
                    + $"{imported.Kind.ToString().ToLowerInvariant()} style '{roleMatch.Name}'.");
            }
        }

        var overlay = existing.ToList();
        overlay.AddRange(document.ManuscriptStyles
            .Where(imported => !overlay.Any(style =>
                style.Kind == imported.Kind
                && string.Equals(
                    style.SemanticRole,
                    imported.SemanticRole,
                    StringComparison.OrdinalIgnoreCase)))
            .Select(imported => new ManuscriptStyleView(
                imported.Id,
                imported.Name,
                imported.Kind,
                imported.SemanticRole,
                imported.Definition,
                imported.Revision)));
        foreach (var chapter in document.Chapters)
        {
            var manuscript = document.FormatVersion >= 8
                ? ReadCurrentManuscript(chapter, document.FormatVersion)
                : ManuscriptCodec.FromPlainText(
                    chapter.Id,
                    chapter.Body,
                    revision: 1,
                    deterministicIds: true);
            ManuscriptStyleService.ValidateDocumentReferences(manuscript, overlay);
        }
    }

    internal static ProjectExportDocument AdaptLegacyManuscriptStyles(ProjectExportDocument document)
    {
        if (document.FormatVersion != 8 || document.ManuscriptStyles.Count > 0)
            return document;

        var roles = document.Chapters
            .Select(chapter => ReadCurrentManuscript(chapter, document.FormatVersion))
            .SelectMany(manuscript => manuscript.Content)
            .Select(block => block.StyleRole)
            .Where(role => !ManuscriptStyleService.BuiltInParagraphRoles.Contains(role))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(role => role, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var styles = roles.Select(role => new ProjectExportManuscriptStyle(
            DeterministicImportedStyleId(document.Project.Id, role),
            AllocateImportedStyleName($"Imported {role}", names),
            ManuscriptStyleKind.Paragraph,
            role,
            new ManuscriptStyleProperties(),
            1)).ToList();
        return document with { ManuscriptStyles = styles };
    }

    internal static ProjectExportDocument AdaptLegacyPublicationEditions(ProjectExportDocument document)
    {
        if (document.FormatVersion >= 10
            || document.PublicationEditions.Count > 0
            || document.LegacyPublishProfiles is not { Count: > 0 })
        {
            return document;
        }

        var editions = document.LegacyPublishProfiles.Select((legacy, index) =>
        {
            var matter = new List<ProjectExportPublicationMatter>();
            AddLegacyMatter(matter, document.Project.Id, legacy.Dedication, PublicationMatterKind.Dedication, PublicationMatterLocation.Front, 0);
            AddLegacyMatter(matter, document.Project.Id, legacy.Acknowledgments, PublicationMatterKind.Acknowledgments, PublicationMatterLocation.Back, 0);
            AddLegacyMatter(matter, document.Project.Id, legacy.References, PublicationMatterKind.References, PublicationMatterLocation.Back, 1);
            return new ProjectExportPublicationEdition(
                legacy.Id,
                index == 0 ? "Imported paperback" : $"Imported paperback {index + 1}",
                PublicationEditionFormat.Paperback,
                PublicationVendor.Generic,
                "legacy-v9",
                PublicationEditionStatus.Draft,
                index == 0,
                0,
                legacy.TitleOverride,
                legacy.Subtitle,
                legacy.Author,
                legacy.Language,
                legacy.Publisher,
                legacy.Copyright,
                legacy.Isbn,
                legacy.Description,
                legacy.IncludeTableOfContents,
                legacy.IncludeVisibleTableOfContents,
                legacy.IncludeActSynopses,
                legacy.IncludeChapterSynopses,
                legacy.IncludeActHeadings,
                legacy.IncludeChapterHeadings,
                legacy.NumberActs,
                legacy.NumberChapters,
                legacy.TitlePageMode,
                legacy.PageWidthInches,
                legacy.PageHeightInches,
                legacy.PageMarginInches,
                null,
                LegacyPublicationBinding.PerfectBound,
                LegacyPublicationPaper.White,
                LegacyPublicationInk.BlackAndWhite,
                false,
                false,
                [],
                null)
            {
                BodyFontSizePoints = legacy.BodyFontSizePoints,
                BodyLineHeight = legacy.BodyLineHeight,
                Matter = matter,
                StyleMappings = [],
                ImagePlacements = [],
                PrintPicturePageSpreadMode = legacy.PrintPicturePageSpreadMode,
                EpubPicturePageSpreadMode = legacy.EpubPicturePageSpreadMode,
                SelectedCoverChapterId = legacy.SelectedCoverChapterId,
            };
        }).ToList();
        return document with { PublicationEditions = editions, LegacyPublishProfiles = null };
    }

    private static void AddLegacyMatter(
        List<ProjectExportPublicationMatter> target,
        Guid projectId,
        string text,
        PublicationMatterKind kind,
        PublicationMatterLocation location,
        int sortOrder)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var id = DeterministicImportedStyleId(projectId, $"publication-matter:{kind}");
        var manuscript = ManuscriptCodec.FromPlainText(id, text, revision: 1, deterministicIds: true);
        target.Add(new ProjectExportPublicationMatter(
            id,
            location,
            kind,
            kind.ToString(),
            ManuscriptCodec.Serialize(manuscript),
            1,
            true,
            sortOrder));
    }

    private static Guid DeterministicImportedStyleId(Guid projectId, string semanticRole)
    {
        var bytes = SHA256.HashData(
            Encoding.UTF8.GetBytes($"lorekeeper-imported-style-v8:{projectId:N}:{semanticRole}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    internal static string AllocateImportedStyleName(
        string requestedName,
        ISet<string> usedNames,
        bool forceSuffix = false)
    {
        var normalized = requestedName.Trim();
        if (!forceSuffix)
        {
            var direct = normalized[..Math.Min(normalized.Length, 80)];
            if (usedNames.Add(direct))
                return direct;
        }

        var suffix = " (imported)";
        var stem = normalized[..Math.Min(normalized.Length, 80 - suffix.Length)];
        var candidate = $"{stem}{suffix}";
        var number = 2;
        while (!usedNames.Add(candidate))
        {
            var numberedSuffix = $"{suffix} {number++}";
            stem = normalized[..Math.Min(normalized.Length, 80 - numberedSuffix.Length)];
            candidate = $"{stem}{numberedSuffix}";
        }
        return candidate;
    }

    private static ManuscriptDocument ReadCurrentManuscript(
        ProjectExportChapter chapter,
        int formatVersion)
    {
        if (string.IsNullOrWhiteSpace(chapter.ManuscriptJson))
            throw new InvalidDataException($"The v{formatVersion} manuscript document is missing.");
        var manuscriptJson = UpgradeImportedManuscript(
            chapter.ManuscriptJson,
            chapter.Id,
            chapter.ManuscriptRevision);
        return ManuscriptCodec.Deserialize(
            manuscriptJson,
            chapter.Id,
            chapter.ManuscriptRevision);
    }

    private static void ValidateCurrentPageLayout(
        ProjectExportChapter chapter,
        ManuscriptDocument manuscript,
        IReadOnlySet<Guid> exportedImageIds,
        IReadOnlySet<Guid>? exportedFontFamilyIds)
    {
        if (string.IsNullOrWhiteSpace(chapter.PageLayoutJson))
            return;
        var layout = JsonSerializer.Deserialize<PicturePageLayout>(chapter.PageLayoutJson, ManuscriptCodec.JsonOptions)
            ?? throw new InvalidDataException("The designed page layout is null.");
        var missingImage = layout.Images.FirstOrDefault(
            image => !exportedImageIds.Contains(image.ImageId));
        if (missingImage is not null)
        {
            throw new InvalidDataException(
                $"Picture Page element {missingImage.Id:N} references an image that is not included in the export.");
        }
        var projectFontText = layout.TextElements.FirstOrDefault(text =>
            TryReadProjectFontKey(text.FontFamilyKey, out _));
        if (exportedFontFamilyIds is null && projectFontText is not null)
        {
            throw new InvalidDataException(
                $"Picture Page text element {projectFontText.Id:N} references a project font, but this pre-v12 backup does not contain the required font binary. Import a v12 backup or replace the custom font in the source project before exporting.");
        }
        if (exportedFontFamilyIds is not null)
        {
            var missingFont = layout.TextElements.FirstOrDefault(text =>
                TryReadProjectFontKey(text.FontFamilyKey, out var familyId)
                && !exportedFontFamilyIds.Contains(familyId));
            if (missingFont is not null)
            {
                throw new InvalidDataException(
                    $"Picture Page text element {missingFont.Id:N} references a project font that is not included in the export.");
            }
        }
        var blockIds = manuscript.Content.Select(block => block.Id).ToHashSet(StringComparer.Ordinal);
        if (layout.TextElements.SelectMany(text => text.ContentReferences ?? []).Any(reference => !blockIds.Contains(reference.BlockId)))
            throw new InvalidDataException("Designed page text references a missing manuscript block.");
    }

    private static void ValidateCurrentIllustrationLayout(
        ProjectExportChapter chapter,
        ManuscriptDocument manuscript,
        IReadOnlySet<Guid> exportedImageIds)
    {
        if (string.IsNullOrWhiteSpace(chapter.IllustrationLayoutJson))
            return;
        var layout = JsonSerializer.Deserialize<IllustratedProseLayout>(
            chapter.IllustrationLayoutJson,
            ManuscriptCodec.JsonOptions)
            ?? throw new InvalidDataException("The illustrated-prose layout is null.");
        var missingImage = layout.Images.FirstOrDefault(
            image => !exportedImageIds.Contains(image.ImageId));
        if (missingImage is not null)
        {
            throw new InvalidDataException(
                $"Illustrated Prose element {missingImage.Id:N} references an image that is not included in the export.");
        }
        var blockIds = manuscript.Content.Select(block => block.Id).ToHashSet(StringComparer.Ordinal);
        if (layout.Images.Any(image => !string.IsNullOrWhiteSpace(image.BlockId) && !blockIds.Contains(image.BlockId)))
            throw new InvalidDataException("Illustration layout references a missing manuscript block.");
    }

    private async Task SeedProjectNodeMapAsync(
        Project project,
        ProjectExportDocument document,
        ImportState state,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var nodes = databaseOperation.Repositories.GraphNodes;
        var projectNode = await nodes.FindAsync(
            project.Id,
            EntityTypeService.ProjectNodeType,
            project.Id.ToString("N"),
            cancellationToken) ?? throw new InvalidOperationException("Current project graph node was not found.");

        foreach (var exportedProjectNode in document.Nodes.Where(node => string.Equals(node.NodeType, EntityTypeService.ProjectNodeType, StringComparison.Ordinal)))
            state.NodeMap[StableKey(exportedProjectNode.NodeType, exportedProjectNode.Key)] = projectNode;
    }

    private async Task ImportEntityTypesAsync(
        ProjectImportJob job,
        ProjectExportDocument document,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var entityTypes = databaseOperation.Repositories.GraphEntityTypes;
        foreach (var importedType in document.EntityTypes)
        {
            if (string.Equals(importedType.Type, EntityTypeService.ProjectNodeType, StringComparison.Ordinal)
                || string.Equals(importedType.Type, EntityTypeService.ActNodeType, StringComparison.Ordinal)
                || string.Equals(importedType.Type, EntityTypeService.ChapterNodeType, StringComparison.Ordinal)
                || string.Equals(importedType.Type, EntityTypeService.EventNodeType, StringComparison.Ordinal))
            {
                continue;
            }

            var existing = await entityTypes.FindAsync(job.ProjectId, importedType.Type, cancellationToken);
            if (existing is null)
            {
                await entityTypes.AddAsync(new GraphEntityType
                {
                    ProjectId = job.ProjectId,
                    Type = importedType.Type,
                    SingularLabel = importedType.SingularLabel,
                    PluralLabel = importedType.PluralLabel,
                    Color = importedType.Color,
                    Icon = importedType.Icon,
                    IsStructural = importedType.IsStructural,
                    IsChapterScoped = importedType.IsChapterScoped,
                    SortOrder = importedType.SortOrder,
                    DefaultProperties = NormalizeProperties(importedType.DefaultProperties),
                }, cancellationToken);
                await databaseOperation.SaveChangesAsync(cancellationToken);
                await AddReportAsync(job, ProjectImportReportItemKind.EntityType, $"Created type {importedType.Type}", importedType.PluralLabel, "GraphEntityType", importedType.Type, cancellationToken: cancellationToken);
                continue;
            }

            var changed = false;
            if (string.IsNullOrWhiteSpace(existing.Color) && !string.IsNullOrWhiteSpace(importedType.Color))
            {
                existing.Color = importedType.Color;
                changed = true;
            }
            if (string.IsNullOrWhiteSpace(existing.Icon) && !string.IsNullOrWhiteSpace(importedType.Icon))
            {
                existing.Icon = importedType.Icon;
                changed = true;
            }
            changed |= MergeProperties(existing.DefaultProperties, NormalizeProperties(importedType.DefaultProperties));
            if (changed)
            {
                existing.UpdatedAt = DateTime.UtcNow;
                entityTypes.Update(existing);
                await databaseOperation.SaveChangesAsync(cancellationToken);
            }
            await AddReportAsync(job, ProjectImportReportItemKind.EntityType, $"Merged type {importedType.Type}", importedType.PluralLabel, "GraphEntityType", importedType.Type, cancellationToken: cancellationToken);
        }
    }

    private async Task AppendStructuralItemsAsync(
        ProjectImportJob job,
        ProjectExportDocument document,
        ImportState state,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var nodes = databaseOperation.Repositories.GraphNodes;
        var chapterRepo = databaseOperation.Repositories.Chapters;
        var actMap = state.ActMap;
        var assetAltById = await db.PublishAssets.AsNoTracking()
            .Where(asset => asset.ProjectId == job.ProjectId)
            .ToDictionaryAsync(asset => asset.Id, asset => asset.AltText, cancellationToken);
        foreach (var importedAct in document.Acts.OrderBy(act => act.Order))
        {
            var created = await acts.CreateAsync(job.ProjectId, importedAct.Title, importedAct.Synopsis, cancellationToken: cancellationToken);
            actMap[importedAct.Id] = created.Id;
            job.CreatedActCount++;
            state.CreatedActIds.Add(created.Id);
            var node = await nodes.FindAsync(job.ProjectId, EntityTypeService.ActNodeType, created.Id.ToString("N"), cancellationToken);
            if (node is not null)
                state.NodeMap[StableKey(EntityTypeService.ActNodeType, importedAct.Id.ToString("N"))] = node;
            await AddReportAsync(job, ProjectImportReportItemKind.Structural, $"Appended act {created.Title}", "Imported as a new act at the end of the current outline.", "Act", created.Id.ToString("N"), cancellationToken: cancellationToken);
        }

        foreach (var importedChapter in OrderedImportedChapters(document))
        {
            var targetActId = importedChapter.ActId is Guid exportedActId && actMap.TryGetValue(exportedActId, out var localActId)
                ? localActId
                : (Guid?)null;
            var created = await chapters.CreateAsync(job.ProjectId, targetActId, importedChapter.Title, importedChapter.Synopsis, cancellationToken: cancellationToken);
            var tracked = await chapterRepo.GetByIdAsync(created.Id, cancellationToken)
                ?? throw new InvalidOperationException($"Created chapter {created.Id} could not be reloaded.");
            var importedManuscript = document.FormatVersion >= 8
                ? ImportCurrentManuscript(
                    importedChapter,
                    tracked.Id,
                    document.FormatVersion,
                    state.ImageMap,
                    state.DesignedPageMap,
                    state.EditionMap)
                : ManuscriptCodec.FromPlainText(
                    tracked.Id,
                    importedChapter.Body,
                    checked(tracked.ManuscriptRevision + 1),
                    deterministicIds: true);
            var pageLayoutJson = document.FormatVersion >= 8
                ? importedChapter.PageLayoutJson
                : ManuscriptMigrationService.MigrateLegacyPicturePage(
                    tracked.Id,
                    importedChapter.Body ?? string.Empty,
                    importedChapter.PageLayoutJson,
                    importedManuscript);
            var staleIllustrationAnchorHashCount = 0;
            var illustrationLayoutJson = document.FormatVersion >= 8
                ? importedChapter.IllustrationLayoutJson
                : ManuscriptMigrationService.MigrateLegacyIllustrations(
                    tracked.Id,
                    importedChapter.Body ?? string.Empty,
                    importedChapter.IllustrationLayoutJson,
                    importedManuscript,
                    out staleIllustrationAnchorHashCount);
            pageLayoutJson = RewritePageLayoutJson(pageLayoutJson, state.ImageMap, state.FontFamilyMap);
            illustrationLayoutJson = RewriteIllustrationLayoutJson(illustrationLayoutJson, state.ImageMap);
            var manuscriptToStore = importedManuscript;
            if (document.FormatVersion < 14 && importedChapter.VisualMode == ChapterVisualMode.IllustratedProse)
                manuscriptToStore = ConvertImportedIllustrations(importedManuscript, illustrationLayoutJson, assetAltById);
            if (document.FormatVersion < 14 && importedChapter.VisualMode == ChapterVisualMode.PicturePage)
            {
                var designedPage = CreateImportedDesignedPage(
                    job.ProjectId,
                    tracked.Id,
                    importedChapter,
                    importedManuscript,
                    pageLayoutJson,
                    assetAltById);
                var content = designedPage.Contents.Single();
                var seed = content.Variants.Single();
                content.Variants.Clear();
                db.DesignedPages.Add(designedPage);
                db.CompositionMutationStages.Add(CreateCompositionSeed(designedPage, content, seed.SceneJson));
                manuscriptToStore = new ManuscriptDocument
                {
                    ManuscriptId = tracked.Id,
                    Revision = importedManuscript.Revision,
                    Content =
                    [
                        new ManuscriptBlock
                        {
                            Id = designedPage.Id.ToString("N"),
                            Type = ManuscriptBlockType.DesignedPage,
                            StyleRole = ManuscriptStyleRoles.DesignedPage,
                            DesignedPageId = designedPage.Id,
                        },
                    ],
                };
            }
            tracked.ManuscriptJson = ManuscriptCodec.Serialize(manuscriptToStore);
            tracked.ManuscriptRevision = manuscriptToStore.Revision;
            tracked.VectorIndexState = string.IsNullOrWhiteSpace(ManuscriptCodec.ProjectPlainText(manuscriptToStore))
                ? VectorIndexState.UpToDate
                : VectorIndexState.Stale;
            tracked.VectorIndexedAt = null;
            tracked.VectorIndexError = null;
            tracked.UpdatedAt = DateTime.UtcNow;
            chapterRepo.Update(tracked);
            await databaseOperation.SaveChangesAsync(cancellationToken);
            await AddImageContextPreferencesAsync(
                job.ProjectId,
                tracked.Id,
                importedChapter.ExplicitImageContextImageIds,
                state.ImageMap,
                cancellationToken);
            await outlineGraphSync.EnsureChapterAsync(tracked, cancellationToken);

            job.CreatedChapterCount++;
            if (staleIllustrationAnchorHashCount > 0)
            {
                await AddWarningAsync(
                    job,
                    $"Preserved {staleIllustrationAnchorHashCount} stale illustration anchor hash(es)",
                    $"Chapter {tracked.Title} retained the paragraph positions used by the legacy runtime.",
                    cancellationToken);
            }
            state.ChapterMap[importedChapter.Id] = tracked.Id;
            state.CreatedChapterIds.Add(tracked.Id);
            var node = await nodes.FindAsync(job.ProjectId, EntityTypeService.ChapterNodeType, tracked.Id.ToString("N"), cancellationToken);
            if (node is not null)
                state.NodeMap[StableKey(EntityTypeService.ChapterNodeType, importedChapter.Id.ToString("N"))] = node;
            await AddReportAsync(job, ProjectImportReportItemKind.Structural, $"Appended chapter {tracked.Title}", "Imported as a new chapter; duplicate titles are left for outline cleanup.", "Chapter", tracked.Id.ToString("N"), cancellationToken: cancellationToken);
        }

        var importedEvents = document.Nodes
            .Where(node => string.Equals(node.NodeType, EntityTypeService.EventNodeType, StringComparison.Ordinal))
            .ToList();
        foreach (var importedEvent in importedEvents)
        {
            var parentEdge = document.Edges.FirstOrDefault(edge =>
                string.Equals(edge.EdgeType, EntityService.HasChildEdgeType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(edge.To.NodeType, importedEvent.NodeType, StringComparison.Ordinal)
                && string.Equals(edge.To.Key, importedEvent.Key, StringComparison.Ordinal)
                && string.Equals(edge.From.NodeType, EntityTypeService.ChapterNodeType, StringComparison.Ordinal));
            if (parentEdge is null || !state.NodeMap.TryGetValue(parentEdge.From.StableKey, out var localChapterNode))
            {
                await AddWarningAsync(job, $"Skipped beat {importedEvent.Label ?? importedEvent.Key}", "The exported beat did not have an imported chapter parent.", cancellationToken);
                continue;
            }

            var localKey = Guid.NewGuid().ToString("N");
            var created = await graph.UpsertNodeAsync(
                job.ProjectId,
                EntityTypeService.EventNodeType,
                localKey,
                importedEvent.Label,
                NormalizeProperties(importedEvent.Properties),
                cancellationToken);
            await graph.UpsertEdgeAsync(
                localChapterNode.Id,
                created.Id,
                EntityService.HasChildEdgeType,
                sortOrder: parentEdge.SortOrder,
                cancellationToken: cancellationToken);

            state.NodeMap[StableKey(importedEvent.NodeType, importedEvent.Key)] = created;
            if (Guid.TryParseExact(localKey, "N", out var eventId))
                state.ContextEntityIdsToReindex.Add(eventId);
            job.CreatedBeatCount++;
            await AddReportAsync(job, ProjectImportReportItemKind.Structural, $"Appended beat {created.Label ?? created.Key}", "Imported as a new beat under an imported chapter.", "Event", created.Key, entityId: Guid.ParseExact(localKey, "N"), graphNodeId: created.Id, cancellationToken: cancellationToken);
        }
    }

    private async Task ImportProjectImagesAsync(
        ProjectImportJob job,
        ProjectExportDocument document,
        ImportState state,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var importedAssets = new Dictionary<Guid, PublishAsset>();
        foreach (var importedImage in document.Images)
        {
            if (importedImage.Data.Length == 0 || !importedImage.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                await AddWarningAsync(job, $"Skipped image {importedImage.FileName}", "The exported image was empty or did not have an image content type.", cancellationToken);
                continue;
            }

            var localId = Guid.NewGuid();
            state.ImageMap[importedImage.Id] = localId;
            var asset = new PublishAsset
            {
                Id = localId,
                ProjectId = job.ProjectId,
                // ReadAndValidateAsync has already normalized legacy image source
                // values before this phase. Applying the legacy adapter again
                // would reinterpret the newly canonical Upscaled value as the
                // pre-v29 numeric Imported value.
                Source = importedImage.Source,
                FileName = string.IsNullOrWhiteSpace(importedImage.FileName) ? $"imported-image-{localId:N}.png" : importedImage.FileName.Trim(),
                ContentType = importedImage.ContentType.Trim(),
                Data = importedImage.Data,
                AltText = importedImage.AltText,
                Prompt = importedImage.Prompt,
                GenerationModel = importedImage.GenerationModel,
                SourceMetadataJson = importedImage.SourceMetadataJson,
                CropXPercent = importedImage.CropXPercent,
                CropYPercent = importedImage.CropYPercent,
                CropWidthPercent = importedImage.CropWidthPercent,
                CropHeightPercent = importedImage.CropHeightPercent,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            importedAssets[importedImage.Id] = asset;
            await db.PublishAssets.AddAsync(asset, cancellationToken);
        }

        foreach (var importedImage in document.Images)
        {
            if (importedImage.DerivedFromImageId is not Guid exportedSourceId
                || !importedAssets.TryGetValue(importedImage.Id, out var localAsset)
                || !state.ImageMap.TryGetValue(exportedSourceId, out var localSourceId))
            {
                continue;
            }

            localAsset.DerivedFromImageId = localSourceId;
        }

        if (state.ImageMap.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            await AddReportAsync(job, ProjectImportReportItemKind.Structural, $"Imported {state.ImageMap.Count} project image(s)", "Images were added to the project image library.", "ProjectImage", job.ProjectId.ToString("N"), cancellationToken: cancellationToken);
        }

    }

    private static ProjectExportDocument AdaptLegacyImageSources(ProjectExportDocument document)
    {
        if (document.FormatVersion >= 29)
            return document;

        return document with
        {
            Images = document.Images
                .Select(image => image with
                {
                    Source = ProjectExportImageCompatibility.AdaptLegacySource(
                        image.Source,
                        image.SourceMetadataJson),
                })
                .ToList(),
        };
    }

    internal static ProjectExportDocument AdaptLegacyCoverDescription(ProjectExportDocument document)
    {
        if (document.FormatVersion >= 30)
            return document;

        static ProjectExportCoverDesign? Adapt(ProjectExportCoverDesign? cover) => cover is null
            ? null
            : cover with
            {
                CompositionSceneJson = LegacyCoverTextBindingMigration.AdaptSceneJson(cover.CompositionSceneJson),
                SurfaceScenesJson = LegacyCoverTextBindingMigration.AdaptSurfaceScenesJson(cover.SurfaceScenesJson),
                LegacyBackCopy = null,
            };

        return document with
        {
            PublicationBook = document.PublicationBook is null
                ? null
                : document.PublicationBook with { CoverDesign = Adapt(document.PublicationBook.CoverDesign) },
            PublicationEditions = document.PublicationEditions
                .Select(edition => edition with { CoverDesign = Adapt(edition.CoverDesign) })
                .ToList(),
        };
    }

    private static void ValidateImageLineageAcyclic(IReadOnlyList<ProjectExportImage> images)
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
                    throw new InvalidOperationException($"Image lineage contains a cycle involving image '{current:N}'.");
                current = parentId;
            }
        }
    }

    private static string UpgradeImportedManuscript(string json, Guid id, long revision)
    {
        using var parsed = JsonDocument.Parse(json);
        return parsed.RootElement.GetProperty("schemaVersion").GetInt32() switch
        {
            ManuscriptDocument.CurrentSchemaVersion => json,
            5 => ManuscriptSchemaUpgrade.UpgradeV5DocumentJson(json, id, revision),
            4 => ManuscriptSchemaUpgrade.UpgradeV4DocumentJson(json, id, revision),
            3 => ManuscriptSchemaUpgrade.UpgradeV3DocumentJson(json, id, revision),
            2 => ManuscriptSchemaUpgrade.UpgradeV2DocumentJson(json, id, revision),
            1 => ManuscriptSchemaUpgrade.UpgradeV1DocumentJson(json, id, revision),
            var version => throw new InvalidDataException($"The imported manuscript schema version {version} is unsupported."),
        };
    }

    private async Task ImportPublicationSectionsAsync(
        Guid projectId,
        ProjectExportDocument document,
        ImportState state,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        foreach (var imported in document.PublicationSections
            .OrderBy(section => section.EditionId.HasValue)
            .ThenBy(section => section.LocalOrder))
        {
            if (!state.PublicationSectionMap.TryGetValue(imported.Id, out var localId))
                throw new InvalidDataException($"Publication section {imported.Id:N} was not mapped.");
            var manuscript = ManuscriptCodec.Deserialize(
                UpgradeImportedManuscript(imported.ManuscriptJson, imported.Id, imported.Revision),
                imported.Id,
                imported.Revision);
            var remapped = RemapManuscriptFigures(
                manuscript,
                localId,
                state.ImageMap,
            state.DesignedPageMap,
                state.EditionMap);
            var editionId = imported.EditionId is Guid exportedEditionId
                ? state.EditionMap.GetValueOrDefault(exportedEditionId)
                : Guid.Empty;
            if (imported.EditionId.HasValue && editionId == Guid.Empty)
                throw new InvalidDataException($"Publication section {imported.Id:N} references a release that was not imported.");
            Guid? targetId = imported.TargetId switch
            {
                null => null,
                { } target when imported.TargetKind == PublishOutlineTargetKind.Act
                    => state.ActMap.GetValueOrDefault(target) is var mapped && mapped != Guid.Empty ? mapped : null,
                { } target when imported.TargetKind == PublishOutlineTargetKind.Chapter
                    => state.ChapterMap.GetValueOrDefault(target) is var mapped && mapped != Guid.Empty ? mapped : null,
                _ => null,
            };
            if (imported.TargetId.HasValue && targetId is null)
                throw new InvalidDataException($"Publication section {imported.Id:N} references an outline target that was not imported.");
            var coreSectionId = imported.CoreSectionId is Guid exportedCoreId
                ? state.PublicationSectionMap.GetValueOrDefault(exportedCoreId)
                : Guid.Empty;
            if (imported.CoreSectionId.HasValue && coreSectionId == Guid.Empty)
                throw new InvalidDataException($"Publication section {imported.Id:N} references a Core section that was not imported.");

            db.PublicationSections.Add(new PublicationSection
            {
                Id = localId,
                ProjectId = projectId,
                EditionId = editionId == Guid.Empty ? null : editionId,
                CoreSectionId = coreSectionId == Guid.Empty ? null : coreSectionId,
                Title = imported.Title,
                Kind = imported.Kind,
                SystemRole = imported.SystemRole,
                Anchor = imported.Anchor,
                TargetKind = imported.TargetKind,
                TargetId = targetId,
                ActId = imported.TargetKind == PublishOutlineTargetKind.Act ? targetId : null,
                ChapterId = imported.TargetKind == PublishOutlineTargetKind.Chapter ? targetId : null,
                InclusionMode = imported.InclusionMode,
                StartSide = document.FormatVersion >= 21
                    ? imported.StartSide
                    : RecommendedStartSide(imported.SystemRole, imported.Kind),
                IsExcluded = imported.IsExcluded,
                LocalOrder = imported.LocalOrder,
                ManuscriptJson = ManuscriptCodec.Serialize(remapped),
                Revision = imported.Revision,
                CreatedAt = imported.CreatedAt,
                UpdatedAt = imported.UpdatedAt,
            });
        }
        if (document.PublicationSections.Count > 0)
            await db.SaveChangesAsync(cancellationToken);
    }

    private static PublicationSectionStartSide RecommendedStartSide(
        PublicationSectionSystemRole role,
        PublicationSectionKind kind) => role switch
        {
            PublicationSectionSystemRole.Title or PublicationSectionSystemRole.Contents => PublicationSectionStartSide.Recto,
            PublicationSectionSystemRole.Copyright => PublicationSectionStartSide.Verso,
            _ when kind is PublicationSectionKind.Dedication or PublicationSectionKind.AboutAuthor or PublicationSectionKind.References
                => PublicationSectionStartSide.Recto,
            _ => PublicationSectionStartSide.Next,
        };

    private async Task ImportDesignedPagesAsync(
        ProjectImportJob job,
        ProjectExportDocument document,
        ImportState state,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var importedPages = EffectiveDesignedPages(document);
        var activeVariants = new List<(DesignedPageContent Content, Guid? ActiveVariantId)>();
        foreach (var imported in importedPages)
        {
            if (!state.DesignedPageMap.TryGetValue(imported.Id, out var localId))
                throw new InvalidOperationException($"Designed Page {imported.Id:N} was not mapped.");

            var page = new DesignedPage
            {
                Id = localId,
                ProjectId = job.ProjectId,
                ScopeEditionId = imported.ScopeEditionId is Guid exportedEditionId
                    ? state.EditionMap.GetValueOrDefault(exportedEditionId) is var localEditionId && localEditionId != Guid.Empty
                        ? localEditionId
                        : throw new InvalidDataException($"Designed Page {imported.Id:N} references an edition that was not imported.")
                    : null,
                Name = string.IsNullOrWhiteSpace(imported.Name) ? "Designed page" : imported.Name.Trim(),
            };
            foreach (var importedContent in imported.Contents)
            {
                var localContentId = Guid.NewGuid();
                var semantic = ManuscriptCodec.Deserialize(
                    UpgradeImportedManuscript(importedContent.SemanticManuscriptJson, importedContent.Id, importedContent.Revision),
                    importedContent.Id,
                    importedContent.Revision);
                var content = new DesignedPageContent
                {
                    Id = localContentId,
                    ProjectId = job.ProjectId,
                    DesignedPageId = localId,
                    EditionId = importedContent.EditionId is Guid exportedContentEditionId
                        ? state.EditionMap.GetValueOrDefault(exportedContentEditionId) is var localContentEditionId && localContentEditionId != Guid.Empty
                            ? localContentEditionId
                            : throw new InvalidDataException($"Designed Page content {importedContent.Id:N} references an edition that was not imported.")
                        : null,
                    SemanticManuscriptJson = ManuscriptCodec.Serialize(RemapManuscriptFigures(
                        semantic, localContentId, state.ImageMap, state.DesignedPageMap, state.EditionMap)),
                    AccessibilityDescription = importedContent.AccessibilityDescription,
                    Revision = importedContent.Revision,
                };
                foreach (var importedVariant in importedContent.Variants)
                {
                    var scene = JsonSerializer.Deserialize<CompositionScene>(
                        AuthoringPageMigrationService.UpgradeJson(importedVariant.SceneJson, removeGuides: true),
                        ManuscriptCodec.JsonOptions)
                        ?? throw new InvalidDataException($"Designed Page variant {importedVariant.Id:N} has an empty scene.");
                    var remappedScene = scene with
                    {
                        Styles = scene.Styles.Select(style => style with { FontFamilyKey = RemapFontKey(style.FontFamilyKey, state.FontFamilyMap) }).ToList(),
                        Objects = scene.Objects.Select(item => item with
                        {
                            ImageId = item.ImageId is Guid exportedImageId
                                ? state.ImageMap.TryGetValue(exportedImageId, out var localImageId)
                                    ? localImageId
                                    : throw new InvalidDataException($"Composition object {item.Id:N} references an image that was not imported.")
                                : null,
                            FontFamilyKey = RemapFontKey(item.FontFamilyKey, state.FontFamilyMap),
                        }).ToList(),
                    };
                    var variant = new DesignedPageVariant
                    {
                        Id = Guid.NewGuid(), ContentId = localContentId, GeometryKey = importedVariant.GeometryKey,
                        SceneJson = JsonSerializer.Serialize(remappedScene, ManuscriptCodec.JsonOptions), Revision = importedVariant.Revision,
                    };
                    content.Variants.Add(variant);
                    if (importedContent.ActiveVariantId == importedVariant.Id)
                        content.ActiveVariantId = variant.Id;
                }
                content.ActiveVariantId ??= content.Variants.OrderByDescending(item => item.UpdatedAt).Select(item => (Guid?)item.Id).FirstOrDefault();
                activeVariants.Add((content, content.ActiveVariantId));
                content.ActiveVariantId = null;
                page.Contents.Add(content);
            }
            db.DesignedPages.Add(page);
        }
        if (importedPages.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            foreach (var (content, activeVariantId) in activeVariants)
                content.ActiveVariantId = activeVariantId;
            await db.SaveChangesAsync(cancellationToken);
            await AddReportAsync(
                job,
                ProjectImportReportItemKind.Structural,
                $"Imported {importedPages.Count} Designed Page(s)",
                "Semantic content, geometry variants, image references, and font references were remapped into the project.",
                "DesignedPage",
                job.ProjectId.ToString("N"),
                cancellationToken: cancellationToken);
        }
    }

    /// <summary>
    /// The v31 writer emits page-owned content. Older JSON is deliberately
    /// adapted only at this import boundary: an owner-bound composition becomes
    /// a project page with one Core or release content record. Its manuscript
    /// placement has already been preserved in the legacy block ID.
    /// </summary>
    private static IReadOnlyList<ProjectExportDesignedPage> EffectiveDesignedPages(ProjectExportDocument document)
    {
        if (document.FormatVersion >= 31)
            return document.DesignedPages;

        var legacy = document.PageCompositions;
        var byId = legacy.ToDictionary(item => item.Id);
        // Only a single valid source clone for the exact release/source
        // lineage is an override. Multiple candidates are divergent data and
        // remain release-only pages rather than silently coalescing content.
        var cloneOwner = legacy
            .Where(item => item.SourceCompositionId is Guid sourceId
                && byId.TryGetValue(sourceId, out var source)
                && source.EditionId is null)
            .GroupBy(item => new { item.EditionId, item.SourceCompositionId })
            .Where(group => group.Count() == 1)
            .SelectMany(group => group)
            .ToDictionary(item => item.Id, item => item.SourceCompositionId!.Value);

        return legacy
            .Where(item => !cloneOwner.ContainsKey(item.Id))
            .Select(page =>
            {
                var contents = legacy
                    .Where(item => item.Id == page.Id || cloneOwner.GetValueOrDefault(item.Id) == page.Id)
                    .Select(content => LegacyDesignedPageContent(page.Id, content))
                    .ToList();
                return new ProjectExportDesignedPage(page.Id, page.Name, page.EditionId, contents);
            })
            .ToList();
    }

    private static IReadOnlyDictionary<Guid, Guid> EffectiveDesignedPageIdentityMap(ProjectExportDocument document)
    {
        if (document.FormatVersion >= 31)
            return document.DesignedPages.ToDictionary(page => page.Id, page => page.Id);

        var legacy = document.PageCompositions;
        var byId = legacy.ToDictionary(item => item.Id);
        var cloneOwner = legacy
            .Where(item => item.SourceCompositionId is Guid sourceId
                && byId.TryGetValue(sourceId, out var source)
                && source.EditionId is null)
            .GroupBy(item => new { item.EditionId, item.SourceCompositionId })
            .Where(group => group.Count() == 1)
            .SelectMany(group => group)
            .ToDictionary(item => item.Id, item => item.SourceCompositionId!.Value);
        return legacy.ToDictionary(
            item => item.Id,
            item => cloneOwner.GetValueOrDefault(item.Id, item.Id));
    }

    private static ProjectExportDesignedPageContent LegacyDesignedPageContent(
        Guid pageId,
        ProjectExportPageComposition composition) => new(
            composition.Id,
            pageId,
            composition.EditionId,
            composition.SemanticManuscriptJson,
            string.Empty,
            composition.Revision,
            composition.Variants.Select(variant => new ProjectExportDesignedPageVariant(
                variant.Id,
                composition.Id,
                variant.GeometryKey,
                variant.SceneJson,
                variant.Revision)).ToList(),
            composition.ActiveAuthoringVariantId);

    private async Task RebuildImportedDesignedPagePlacementsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var pages = await db.DesignedPages.AsNoTracking()
            .Where(page => page.ProjectId == projectId)
            .Select(page => page.Id)
            .ToHashSetAsync(cancellationToken);
        var placements = new List<DesignedPagePlacementReference>();
        var placementKeys = new HashSet<(DesignedPageContainerKind Kind, Guid ContainerId, Guid? EditionId, string BlockId)>();
        foreach (var chapter in await db.Chapters.Where(chapter => chapter.ProjectId == projectId).ToListAsync(cancellationToken))
            AddPlacementReferences(projectId, chapter.Manuscript, DesignedPageContainerKind.Chapter, chapter.Id, null, pages, placementKeys, placements);
        foreach (var chapterOverride in await db.PublicationEditionChapterOverrides
            .Where(item => item.Edition.ProjectId == projectId)
            .ToListAsync(cancellationToken))
        {
            var manuscript = ManuscriptCodec.Deserialize(chapterOverride.ManuscriptJson, chapterOverride.ChapterId, chapterOverride.Revision);
            AddPlacementReferences(projectId, manuscript, DesignedPageContainerKind.Chapter, chapterOverride.ChapterId, chapterOverride.EditionId, pages, placementKeys, placements);
        }
        foreach (var section in await db.PublicationSections.Where(section => section.ProjectId == projectId).ToListAsync(cancellationToken))
        {
            var manuscript = ManuscriptCodec.Deserialize(section.ManuscriptJson, section.Id, section.Revision);
            AddPlacementReferences(projectId, manuscript, DesignedPageContainerKind.PublicationSection, section.Id, section.EditionId, pages, placementKeys, placements);
        }
        db.DesignedPagePlacementReferences.AddRange(placements);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void AddPlacementReferences(
        Guid projectId,
        ManuscriptDocument manuscript,
        DesignedPageContainerKind containerKind,
        Guid containerId,
        Guid? editionId,
        IReadOnlySet<Guid> pageIds,
        ISet<(DesignedPageContainerKind Kind, Guid ContainerId, Guid? EditionId, string BlockId)> placementKeys,
        ICollection<DesignedPagePlacementReference> placements)
    {
        foreach (var block in manuscript.Content.Where(block => block.Type == ManuscriptBlockType.DesignedPage))
        {
            if (block.DesignedPageId is not Guid pageId || !pageIds.Contains(pageId))
                throw new InvalidDataException($"Designed Page block {block.Id} references a page outside the imported project.");
            if (!placementKeys.Add((containerKind, containerId, editionId, block.Id)))
                throw new InvalidDataException($"Designed Page placement block ID {block.Id} is not unique within its manuscript container.");
            placements.Add(new DesignedPagePlacementReference
            {
                Id = block.Id,
                ProjectId = projectId,
                DesignedPageId = pageId,
                ContainerKind = containerKind,
                ContainerId = containerId,
                EditionId = editionId,
                ManuscriptRevision = manuscript.Revision,
            });
        }
    }

    private static void ValidatePublicationBookPayload(
        ProjectExportPublicationBook book,
        IReadOnlySet<Guid> actIds,
        IReadOnlySet<Guid> chapterIds,
        IReadOnlyDictionary<Guid, Guid?> chapterParents,
        IReadOnlySet<Guid> imageIds,
        IReadOnlyList<ManuscriptStyleView> styles,
        IReadOnlySet<Guid> fontFamilyIds)
    {
        if (book.Revision < 0
            || book.Title.Trim().Length > 500
            || book.Subtitle.Trim().Length > 500
            || book.Author.Trim().Length > 500
            || book.Language.Trim().Length > 40
            || book.Publisher.Trim().Length > 500
            || book.Copyright.Trim().Length > 100_000
            || book.Description.Trim().Length > 100_000
            || !Enum.IsDefined(book.TitlePageMode))
            throw new InvalidOperationException("Core Book contains invalid book details.");
        if (book.OutlineItems.GroupBy(item => (item.TargetKind, item.TargetId)).Any(group => group.Count() > 1)
            || book.OutlineItems.GroupBy(item => item.SortOrder).Any(group => group.Count() > 1)
            || book.OutlineItems.Any(item => item.SortOrder < 0)
            || book.LegacyMatter.GroupBy(item => item.Id).Any(group => group.Count() > 1)
            || book.LegacyImagePlacements.GroupBy(item => item.Id).Any(group => group.Count() > 1))
            throw new InvalidOperationException("Core Book contains duplicate or invalid child records.");
        ValidateImportedLegacyOutlineOrder(
            book.OutlineItems.OrderBy(item => item.SortOrder)
                .Select(item => (item.TargetKind, item.TargetId))
                .ToList(),
            actIds,
            chapterParents);
        foreach (var item in book.OutlineItems)
        {
            var exists = item.TargetKind == PublishOutlineTargetKind.Act
                ? actIds.Contains(item.TargetId)
                : item.TargetKind == PublishOutlineTargetKind.Chapter && chapterIds.Contains(item.TargetId);
            if (!exists)
                throw new InvalidOperationException("Core Book references missing outline content.");
        }
        foreach (var matter in book.LegacyMatter)
        {
            if (!Enum.IsDefined(matter.Kind)
                || !Enum.IsDefined(matter.Location)
                || IsLegacyGeneratedMatterKind(matter.Kind)
                || matter.Revision < 0
                || matter.SortOrder < 0
                || matter.Title.Trim().Length is < 1 or > 500
                || matter.Title.Contains('\r')
                || matter.Title.Contains('\n'))
                throw new InvalidOperationException("Core Book contains invalid matter metadata.");
            var manuscript = ManuscriptCodec.Deserialize(matter.ManuscriptJson, matter.Id, matter.Revision);
            if (ManuscriptTraversal.EnumerateBlocks(manuscript).Any(block => block.Type == ManuscriptBlockType.Figure
                && block.ImageId is Guid imageId && !imageIds.Contains(imageId)))
                throw new InvalidOperationException($"Core matter {matter.Id:N} references a missing image.");
            ManuscriptStyleService.ValidateDocumentReferences(manuscript, styles);
        }
        foreach (var placement in book.LegacyImagePlacements)
        {
            if (!imageIds.Contains(placement.AssetId))
                throw new InvalidOperationException("Core Book references a missing image.");
            var targetExists = placement.TargetKind == PublishOutlineTargetKind.Act
                ? actIds.Contains(placement.TargetId)
                : placement.TargetKind == PublishOutlineTargetKind.Chapter && chapterIds.Contains(placement.TargetId);
            var kindValid = placement.TargetKind == PublishOutlineTargetKind.Act
                ? placement.PlacementKind is PublicationImagePlacementKind.BeforeAct or PublicationImagePlacementKind.AfterAct
                : placement.PlacementKind is PublicationImagePlacementKind.BeforeChapter
                    or PublicationImagePlacementKind.ChapterOpening
                    or PublicationImagePlacementKind.ChapterEnding
                    or PublicationImagePlacementKind.AfterChapter;
            if (!targetExists || !kindValid || placement.SortOrder < 0)
                throw new InvalidOperationException("Core Book contains an invalid image placement.");
        }
        if (book.CoverDesign is not { } cover)
            return;
        if (!System.Text.RegularExpressions.Regex.IsMatch(cover.BackgroundColor, "^#[0-9a-fA-F]{6}$")
            || cover.Revision < 0)
            throw new InvalidOperationException("Core Book contains invalid cover settings.");
        if (cover.CompositionSceneJson.Length == 0)
            return;
        var scene = JsonSerializer.Deserialize<CompositionScene>(cover.CompositionSceneJson, ManuscriptCodec.JsonOptions)
            ?? throw new InvalidOperationException("Core Book contains an empty cover scene.");
        DesignedPageService.Validate(scene, ManuscriptCodec.CreateEmpty(Guid.Empty), allowCanonicalTextBindings: true);
        ValidateExportSceneReferences(scene, imageIds, fontFamilyIds, allowCoverBindings: true, "Core Book cover");
        if (scene.Surface.Kind != CompositionSurfaceKind.SinglePage
            || scene.Objects.Any(item => item.RegionConstraint is CompositionRegionConstraint.Back
                or CompositionRegionConstraint.Spine
                or CompositionRegionConstraint.BarcodeReserve))
            throw new InvalidOperationException("Core Book cover contains print-only regions.");
    }

    private static string RemapFontKey(string value, IReadOnlyDictionary<Guid, Guid> fontFamilyMap)
    {
        if (!TryReadProjectFontKey(value, out var exportedFamilyId)) return value;
        return fontFamilyMap.TryGetValue(exportedFamilyId, out var localFamilyId)
            ? ProjectFontService.CustomKey(localFamilyId)
            : throw new InvalidDataException(
                $"Composition references project font {exportedFamilyId:N}, which was not imported.");
    }

    private static string RemapSceneJson(
        string json,
        IReadOnlyDictionary<Guid, Guid> imageMap,
        IReadOnlyDictionary<Guid, Guid> fontFamilyMap)
    {
        if (string.IsNullOrWhiteSpace(json)) return string.Empty;
        var scene = JsonSerializer.Deserialize<CompositionScene>(json, ManuscriptCodec.JsonOptions)
            ?? throw new InvalidDataException("Composition scene is empty.");
        return JsonSerializer.Serialize(scene with
        {
            Styles = scene.Styles.Select(style => style with
            {
                FontFamilyKey = RemapFontKey(style.FontFamilyKey, fontFamilyMap),
            }).ToList(),
            Objects = scene.Objects.Select(item => item with
            {
                ImageId = item.ImageId is Guid exportedImageId
                    ? imageMap.TryGetValue(exportedImageId, out var localImageId)
                        ? localImageId
                        : throw new InvalidDataException(
                            $"Composition object {item.Id:N} references an image that was not imported.")
                    : null,
                FontFamilyKey = RemapFontKey(item.FontFamilyKey, fontFamilyMap),
            }).ToList(),
        }, ManuscriptCodec.JsonOptions);
    }

    private static string RemapSurfaceScenesJson(
        string json,
        IReadOnlyDictionary<Guid, Guid> imageMap,
        IReadOnlyDictionary<Guid, Guid> fontFamilyMap)
    {
        if (string.IsNullOrWhiteSpace(json)) return "{}";
        var scenes = JsonSerializer.Deserialize<Dictionary<string, string>>(json, ManuscriptCodec.JsonOptions)
            ?? new Dictionary<string, string>();
        return JsonSerializer.Serialize(
            scenes.ToDictionary(
                item => item.Key,
                item => RemapSceneJson(item.Value, imageMap, fontFamilyMap),
                StringComparer.Ordinal),
            ManuscriptCodec.JsonOptions);
    }

    private async Task ImportEntityVisualExamplesAsync(
        ProjectImportJob job,
        ProjectExportDocument document,
        ImportState state,
        CancellationToken cancellationToken)
    {
        foreach (var group in document.EntityVisualExamples
            .GroupBy(example => example.Entity.StableKey, StringComparer.Ordinal))
        {
            if (!state.NodeMap.TryGetValue(group.Key, out var localNode)
                || !Guid.TryParseExact(localNode.Key, "N", out var entityId))
            {
                await AddWarningAsync(job, $"Skipped {group.Count()} visual association(s)", $"Entity '{group.Key}' was not imported.", cancellationToken);
                continue;
            }

            var attachedIds = new List<Guid>();
            foreach (var imported in group.OrderBy(example => example.SortOrder))
            {
                if (!state.ImageMap.TryGetValue(imported.ImageId, out var imageId))
                {
                    await AddWarningAsync(job, "Skipped entity visual", $"Image {imported.ImageId:N} was not imported.", cancellationToken);
                    continue;
                }
                var attached = await entityVisualExamples.AttachAsync(
                    job.ProjectId, entityId, imageId, imported.Label, EntityVisualExampleOrigin.Import,
                    cancellationToken: cancellationToken);
                attachedIds.Add(attached.Id);
            }
            if (attachedIds.Count > 1)
            {
                var all = await entityVisualExamples.ListForEntityAsync(job.ProjectId, entityId, cancellationToken);
                var importedSet = attachedIds.ToHashSet();
                var order = all.Where(example => importedSet.Contains(example.Id)).OrderBy(example => attachedIds.IndexOf(example.Id))
                    .Concat(all.Where(example => !importedSet.Contains(example.Id)))
                    .Select(example => example.Id)
                    .ToList();
                await entityVisualExamples.ReorderAsync(job.ProjectId, entityId, order, markManual: false, cancellationToken: cancellationToken);
            }
        }
    }

    private async Task ImportPublicationEditionSettingsAsync(
        Guid projectId,
        ProjectExportPublicationEdition importedEdition,
        IReadOnlyDictionary<Guid, Guid> actMap,
        IReadOnlyDictionary<Guid, Guid> chapterMap,
        IReadOnlyDictionary<Guid, Guid> imageMap,
        IReadOnlyDictionary<Guid, Guid> fontFamilyMap,
        IReadOnlyDictionary<Guid, Guid> editionMap,
        IReadOnlyDictionary<Guid, Guid> compositionMap,
        IReadOnlyDictionary<Guid, Guid> publicationSectionMap,
        IReadOnlyDictionary<Guid, Guid> coreMatterMap,
        IReadOnlyDictionary<Guid, Guid> corePlacementMap,
        int formatVersion,
        IReadOnlyList<ProjectExportChapter> importedChapters,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        if (!Enum.IsDefined(importedEdition.TitlePageMode)
            || !Enum.IsDefined(importedEdition.PrintPicturePageSpreadMode)
            || !Enum.IsDefined(importedEdition.EpubPicturePageSpreadMode))
        {
            throw new InvalidOperationException("The imported publication release contains an unsupported page presentation mode.");
        }

        var normalizedIsbn = PublicationIsbn.NormalizeValidOrEmpty(importedEdition.Isbn);
        if (normalizedIsbn.Length > 0)
        {
            var existingWithIsbn = await db.PublicationEditions.AsNoTracking()
                .Where(candidate => candidate.ProjectId == projectId && candidate.Isbn == normalizedIsbn)
                .ToListAsync(cancellationToken);
            if (existingWithIsbn.Any(candidate =>
                candidate.Format != importedEdition.Format
                || !ImportedBibliographicProductSettingsMatch(candidate, importedEdition)))
            {
                throw new InvalidOperationException(
                    "The imported ISBN-13 conflicts with an existing publication product in this project.");
            }
            foreach (var candidate in existingWithIsbn)
            {
                if (!await ImportedBibliographicContentMatchesAsync(
                    candidate.Id,
                    importedEdition,
                    actMap,
                    chapterMap,
                    imageMap,
                    editionMap,
                    cancellationToken))
                {
                    throw new InvalidOperationException(
                        "Vendor editions sharing an ISBN-13 must contain the same ordered manuscript and publication matter.");
                }
            }
        }

        var existingNames = await db.PublicationEditions
            .Where(candidate => candidate.ProjectId == projectId)
            .Select(candidate => candidate.Name)
            .ToListAsync(cancellationToken);
        var baseName = string.IsNullOrWhiteSpace(importedEdition.Name) ? "Imported edition" : importedEdition.Name.Trim();
        var name = baseName;
        for (var suffix = 2; existingNames.Contains(name, StringComparer.OrdinalIgnoreCase); suffix++)
            name = $"{baseName} {suffix}";
        var edition = new PublicationEdition
        {
            Id = editionMap.GetValueOrDefault(importedEdition.Id) is var mappedEditionId && mappedEditionId != Guid.Empty
                ? mappedEditionId
                : throw new InvalidDataException($"Publication edition {importedEdition.Id:N} has no allocated import identity."),
            ProjectId = projectId,
            Name = name,
            Format = importedEdition.Format,
            Vendor = importedEdition.Vendor,
            VendorProfileVersion = importedEdition.VendorProfileVersion,
            Status = importedEdition.Status,
            Revision = importedEdition.Revision,
            TitleOverride = importedEdition.TitleOverride,
            Subtitle = importedEdition.Subtitle,
            Author = importedEdition.Author,
            Language = importedEdition.Language,
            Publisher = importedEdition.Publisher,
            Copyright = importedEdition.Copyright,
            Isbn = normalizedIsbn,
            Description = importedEdition.Description,
            IncludeTableOfContents = importedEdition.IncludeTableOfContents,
            IncludeVisibleTableOfContents = importedEdition.IncludeVisibleTableOfContents,
            IncludeActSynopses = importedEdition.IncludeActSynopses,
            IncludeChapterSynopses = importedEdition.IncludeChapterSynopses,
            IncludeActHeadings = importedEdition.IncludeActHeadings,
            IncludeChapterHeadings = importedEdition.IncludeChapterHeadings,
            NumberActs = importedEdition.NumberActs,
            NumberChapters = importedEdition.NumberChapters,
            TitlePageMode = importedEdition.TitlePageMode,
            PageWidthInches = importedEdition.PageWidthInches,
            PageHeightInches = importedEdition.PageHeightInches,
            PageMarginInches = importedEdition.PageMarginInches,
            BodyFontSizePoints = importedEdition.ImportedBodyFontSizePoints,
            BodyLineHeight = importedEdition.ImportedBodyLineHeight,
            PrintArtifactRegistryVersion = importedEdition.Format is PublicationEditionFormat.Paperback or PublicationEditionFormat.Hardcover
                ? PrintArtifactProfileRegistry.CurrentVersion
                : string.Empty,
            PrintArtifactProfileKey = formatVersion >= 20 && !string.IsNullOrWhiteSpace(importedEdition.ImportedPrintArtifactProfileKey)
                ? NormalizePrintArtifactProfileKey(importedEdition.ImportedPrintArtifactProfileKey)
                : LegacyPrintArtifactProfile(importedEdition.Format, importedEdition.Vendor, importedEdition.Paper, importedEdition.Ink),
            PrintCoverMode = formatVersion >= 20 ? importedEdition.PrintCoverMode : PrintCoverMode.Simplex,
            PrintProjectUse = formatVersion >= 26 ? importedEdition.PrintProjectUse : PrintProjectUse.ForSale,
            PrintIdentifierMode = formatVersion >= 26 ? importedEdition.PrintIdentifierMode : PrintIdentifierMode.UserSuppliedIsbn,
            PrintCoverSubmissionMode = formatVersion >= 26 ? importedEdition.PrintCoverSubmissionMode : PrintCoverSubmissionMode.FullWrapMeasured,
            Bleed = importedEdition.Bleed,
            AllowDesignedPageOverrides = importedEdition.AllowDesignedPageOverrides,
            RectoChapterStarts = importedEdition.RectoChapterStarts,
            OverrideFieldsJson = formatVersion >= 16
                ? JsonSerializer.Serialize(importedEdition.OverrideFields.Where(Enum.IsDefined))
                : "[]",
            InheritsCoreCover = formatVersion >= 16 && importedEdition.InheritsCoreCover,
            EditionSpecificContentEnabled = formatVersion >= 18 && importedEdition.EditionSpecificContentEnabled,
            PublicationSectionOrderJson = formatVersion >= 22
                ? PublicationSectionOrderCodec.Serialize(importedEdition.PublicationSectionOrder.ToDictionary(
                    item => publicationSectionMap.TryGetValue(item.Key, out var mapped)
                        ? mapped
                        : throw new InvalidDataException($"Publication section order references missing section {item.Key:N}."),
                    item => item.Value))
                : "{}",
        };
        var exportedCoverImageId = importedEdition.SelectedCoverImageId;
        Guid? exportedLegacyCoverChapterId = null;
        if (exportedCoverImageId is null
            && formatVersion < 13
            && importedEdition.SelectedCoverChapterId is Guid exportedCoverChapterId)
        {
            exportedLegacyCoverChapterId = exportedCoverChapterId;
            var legacyCoverChapter = importedChapters.FirstOrDefault(chapter => chapter.Id == exportedCoverChapterId);
            if (legacyCoverChapter is not null)
            {
                var layout = JsonSerializer.Deserialize<PicturePageLayout>(
                    legacyCoverChapter.PageLayoutJson,
                    ManuscriptCodec.JsonOptions) ?? new PicturePageLayout([], []);
                var coverImageIds = layout.Images.Select(image => image.ImageId).Distinct().ToList();
                if (coverImageIds.Count == 1)
                {
                    exportedCoverImageId = coverImageIds[0];
                }
            }
        }
        if (exportedCoverImageId is Guid exportedImageId
            && imageMap.TryGetValue(exportedImageId, out var localCoverImageId))
            edition.SelectedCoverImageId = localCoverImageId;
        if (importedEdition.CoverDesign is { } cover)
            edition.CoverDesign = new PublicationCoverDesign
            {
                EditionId = edition.Id,
                Title = cover.Title,
                Subtitle = cover.Subtitle,
                Author = cover.Author,
                SpineText = cover.SpineText,
                BackgroundColor = cover.BackgroundColor,
                BarcodeMode = cover.BarcodeMode,
                ImageCropXPercent = cover.ImageCropXPercent,
                ImageCropYPercent = cover.ImageCropYPercent,
                CompositionSceneJson = RemapSceneJson(
                    cover.CompositionSceneJson,
                    imageMap,
                    fontFamilyMap),
                SurfaceScenesJson = RemapSurfaceScenesJson(
                    cover.SurfaceScenesJson,
                    imageMap,
                    fontFamilyMap),
                SpineReadingDirection = formatVersion >= 26
                    ? cover.SpineReadingDirection
                    : SpineReadingDirection.TopToBottom,
                Revision = cover.Revision,
            };
        foreach (var imported in importedEdition.OutlineItems)
        {
            var mappedTarget = imported.TargetKind == PublishOutlineTargetKind.Act
                ? actMap.GetValueOrDefault(imported.TargetId)
                : chapterMap.GetValueOrDefault(imported.TargetId);
            if (mappedTarget == Guid.Empty) continue;
            edition.OutlineItems.Add(new PublicationEditionOutlineItem
            {
                TargetKind = imported.TargetKind,
                TargetId = mappedTarget,
                ActId = imported.TargetKind == PublishOutlineTargetKind.Act ? mappedTarget : null,
                ChapterId = imported.TargetKind == PublishOutlineTargetKind.Chapter ? mappedTarget : null,
                IsIncluded = imported.IsIncluded
                    && !(exportedLegacyCoverChapterId == imported.TargetId
                        && imported.TargetKind == PublishOutlineTargetKind.Chapter),
                SortOrder = imported.SortOrder,
            });
        }
        if (edition.OutlineItems.Count == 0)
        {
            var order = 0;
            foreach (var mapped in actMap.Values)
                edition.OutlineItems.Add(new PublicationEditionOutlineItem { TargetKind = PublishOutlineTargetKind.Act, TargetId = mapped, ActId = mapped, SortOrder = order++ });
            foreach (var mapped in chapterMap)
                edition.OutlineItems.Add(new PublicationEditionOutlineItem
                {
                    TargetKind = PublishOutlineTargetKind.Chapter,
                    TargetId = mapped.Value,
                    ChapterId = mapped.Value,
                    IsIncluded = mapped.Key != exportedLegacyCoverChapterId,
                    SortOrder = order++,
                });
        }
        foreach (var imported in importedEdition.LegacyMatter)
        {
            var matter = new PublicationMatter
            {
                CoreMatterId = formatVersion >= 16 && imported.CoreMatterId is Guid importedCoreMatterId
                    ? coreMatterMap.GetValueOrDefault(importedCoreMatterId) is var coreMatterId && coreMatterId != Guid.Empty ? coreMatterId : null
                    : null,
                Location = imported.Location,
                Kind = imported.Kind,
                Title = imported.Title,
                Revision = imported.Revision,
                IsIncluded = imported.IsIncluded,
                IsExcluded = formatVersion >= 16 && imported.IsExcluded,
                SortOrder = imported.SortOrder,
            };
            var document = ManuscriptCodec.Deserialize(imported.ManuscriptJson, imported.Id, imported.Revision);
            var remapped = RemapManuscriptFigures(document, matter.Id, imageMap, editionMap: editionMap);
            ManuscriptCodec.Validate(remapped, matter.Id, remapped.Revision);
            matter.ManuscriptJson = ManuscriptCodec.Serialize(remapped);
            edition.Matter.Add(matter);
        }
        var placementAssetIds = importedEdition.LegacyImagePlacements
            .Select(imported => imageMap.GetValueOrDefault(imported.AssetId))
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        var placementAssetAlt = await db.PublishAssets.AsNoTracking()
            .Where(asset => placementAssetIds.Contains(asset.Id))
            .ToDictionaryAsync(asset => asset.Id, asset => asset.AltText, cancellationToken);
        foreach (var imported in importedEdition.LegacyImagePlacements)
        {
            var targetId = imported.TargetKind == PublishOutlineTargetKind.Act
                ? actMap.GetValueOrDefault(imported.TargetId)
                : chapterMap.GetValueOrDefault(imported.TargetId);
            var assetId = imageMap.GetValueOrDefault(imported.AssetId);
            if (targetId == Guid.Empty || assetId == Guid.Empty) continue;
            edition.ImagePlacements.Add(new PublicationImagePlacement
            {
                CorePlacementId = formatVersion >= 16 && imported.CorePlacementId is Guid importedCorePlacementId
                    ? corePlacementMap.GetValueOrDefault(importedCorePlacementId) is var corePlacementId && corePlacementId != Guid.Empty ? corePlacementId : null
                    : null,
                AssetId = assetId,
                TargetKind = imported.TargetKind,
                TargetId = targetId,
                ActId = imported.TargetKind == PublishOutlineTargetKind.Act ? targetId : null,
                ChapterId = imported.TargetKind == PublishOutlineTargetKind.Chapter ? targetId : null,
                PlacementKind = imported.PlacementKind,
                Caption = imported.Caption,
                PresentationJson = JsonSerializer.Serialize(
                    RemapFigurePresentation(
                        imported.Presentation ?? new FigurePresentation { Placement = FigurePlacementIntent.DedicatedPage, StartOnNewPage = true }),
                    ManuscriptCodec.JsonOptions),
                AltText = imported.Decorative
                    ? string.Empty
                    : FirstNonEmpty(
                        imported.AltText,
                        placementAssetAlt.GetValueOrDefault(assetId)),
                Decorative = imported.Decorative,
                Language = string.IsNullOrWhiteSpace(imported.Language) ? "en" : imported.Language.Trim(),
                AccessibilityRole = imported.AccessibilityRole,
                IsExcluded = formatVersion >= 16 && imported.IsExcluded,
                SortOrder = imported.SortOrder,
            });
        }
        if (formatVersion >= 18)
        {
            foreach (var imported in importedEdition.ChapterOverrides)
            {
                if (!chapterMap.TryGetValue(imported.ChapterId, out var localChapterId))
                    throw new InvalidDataException($"Edition chapter override {imported.Id:N} references a chapter that was not imported.");
                var document = ManuscriptCodec.Deserialize(imported.ManuscriptJson, imported.ChapterId, imported.Revision);
                var remapped = RemapManuscriptFigures(
                    document with { ManuscriptId = localChapterId },
                    localChapterId,
                    imageMap,
                    compositionMap,
                    editionMap);
                edition.ChapterOverrides.Add(new PublicationEditionChapterOverride
                {
                    Id = Guid.NewGuid(),
                    EditionId = edition.Id,
                    ChapterId = localChapterId,
                    ManuscriptJson = ManuscriptCodec.Serialize(remapped),
                    Revision = imported.Revision,
                    BaseCoreRevision = imported.BaseCoreRevision,
                    BaseCoreHash = imported.BaseCoreHash,
                    CreatedAt = imported.CreatedAt,
                    UpdatedAt = imported.UpdatedAt,
                });
            }
        }
        await db.PublicationEditions.AddAsync(edition, cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            var entries = string.Join(", ", exception.Entries.Select(entry =>
                $"{entry.Metadata.ClrType.Name}:{entry.State}"));
            throw new InvalidOperationException($"Publication edition import encountered an unexpected state transition ({entries}).", exception);
        }
    }

    private async Task MaterializeImportedLegacyEditionContentAsync(
        Guid projectId,
        ProjectExportDocument document,
        ImportState state,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var setup = await db.ProjectPageSetups.AsNoTracking().SingleAsync(
            item => item.ProjectId == projectId, cancellationToken);
        foreach (var importedEdition in document.PublicationEditions)
        {
            var typographyDiffers = Math.Abs(importedEdition.ImportedBodyFontSizePoints - setup.BodyFontSizePoints) > 0.0001
                || Math.Abs(importedEdition.ImportedBodyLineHeight - setup.BodyLineHeight) > 0.0001
                || importedEdition.OverrideFields.Any(field => (int)field is 19 or 20);
            if (importedEdition.LegacyStyleMappings.Count == 0 && !typographyDiffers)
                continue;
            if (!state.EditionMap.TryGetValue(importedEdition.Id, out var editionId))
                throw new InvalidDataException($"Legacy publication release {importedEdition.Id:N} has no imported identity.");
            var edition = await db.PublicationEditions.SingleAsync(item => item.Id == editionId, cancellationToken);
            var projectStyles = await db.ManuscriptStyleDefinitions
                .Where(item => item.ProjectId == projectId)
                .ToListAsync(cancellationToken);
            var roleMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mapping in importedEdition.LegacyStyleMappings)
            {
                var source = projectStyles.FirstOrDefault(item =>
                    string.Equals(item.SemanticRole, mapping.SemanticRole, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"Legacy release style '{mapping.SemanticRole}' was not imported.");
                var definition = JsonSerializer.Deserialize<ManuscriptStyleProperties>(
                    source.DefinitionJson, ManuscriptCodec.JsonOptions) ?? new ManuscriptStyleProperties();
                var merged = MergeImportedStyle(definition, mapping.Override);
                if (string.Equals(mapping.SemanticRole, ManuscriptStyleRoles.Body, StringComparison.OrdinalIgnoreCase)
                    && typographyDiffers)
                    merged = merged with { FontSizePoints = importedEdition.ImportedBodyFontSizePoints, LineHeight = importedEdition.ImportedBodyLineHeight };
                roleMap[mapping.SemanticRole] = AddImportedEditionStyle(projectId, edition, source, merged, projectStyles);
            }
            if (typographyDiffers && !roleMap.ContainsKey(ManuscriptStyleRoles.Body))
            {
                var merged = new ManuscriptStyleProperties(
                    FontSizePoints: importedEdition.ImportedBodyFontSizePoints,
                    LineHeight: importedEdition.ImportedBodyLineHeight);
                var source = projectStyles.FirstOrDefault(item =>
                    item.Kind == ManuscriptStyleKind.Paragraph
                    && string.Equals(item.SemanticRole, ManuscriptStyleRoles.Body, StringComparison.OrdinalIgnoreCase));
                roleMap[ManuscriptStyleRoles.Body] = AddImportedEditionStyle(
                    projectId,
                    edition,
                    source,
                    source is null
                        ? merged
                        : MergeImportedStyle(
                            JsonSerializer.Deserialize<ManuscriptStyleProperties>(source.DefinitionJson, ManuscriptCodec.JsonOptions)
                                ?? new ManuscriptStyleProperties(),
                            merged),
                    projectStyles);
            }
            await db.SaveChangesAsync(cancellationToken);

            var chapters = await db.Chapters.Where(item => item.ProjectId == projectId).ToListAsync(cancellationToken);
            foreach (var chapter in chapters)
            {
                var transformed = ReplaceImportedStyleRoles(chapter.Manuscript, roleMap);
                if (ManuscriptCodec.ContentEquals(chapter.Manuscript, transformed))
                    continue;
                var remap = await CloneImportedEditionCompositionsAsync(projectId, chapter.Id, edition.Id, transformed, roleMap, cancellationToken);
                transformed = transformed with
                {
                    Content = transformed.Content.Select(block =>
                        block.DesignedPageId is Guid sourceId && remap.TryGetValue(sourceId, out var cloneId)
                            ? block with { DesignedPageId = cloneId }
                            : block).ToList(),
                };
                edition.ChapterOverrides.Add(new PublicationEditionChapterOverride
                {
                    EditionId = edition.Id,
                    ChapterId = chapter.Id,
                    ManuscriptJson = ManuscriptCodec.Serialize(transformed),
                    Revision = transformed.Revision,
                    BaseCoreRevision = chapter.ManuscriptRevision,
                    BaseCoreHash = ManuscriptCodec.HashPlainText(ManuscriptCodec.ProjectPlainText(chapter.Manuscript)),
                });
            }
            foreach (var matter in await db.PublicationMatter.Where(item => item.EditionId == edition.Id).ToListAsync(cancellationToken))
            {
                var semantic = ManuscriptCodec.Deserialize(matter.ManuscriptJson, matter.Id, matter.Revision);
                var transformed = ReplaceImportedStyleRoles(semantic, roleMap);
                if (!ManuscriptCodec.ContentEquals(semantic, transformed))
                    matter.ManuscriptJson = ManuscriptCodec.Serialize(transformed);
            }
            edition.EditionSpecificContentEnabled = true;
            edition.Revision = checked(edition.Revision + 1);
            edition.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private string AddImportedEditionStyle(
        Guid projectId,
        PublicationEdition edition,
        ManuscriptStyleDefinition? source,
        ManuscriptStyleProperties definition,
        ICollection<ManuscriptStyleDefinition> styles)
    {
        using var databaseOperation = database.OpenWrite();
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var sourceRole = source?.SemanticRole ?? ManuscriptStyleRoles.Body;
        var roleStem = $"{sourceRole}-edition-{edition.Id:N}";
        var role = roleStem[..Math.Min(72, roleStem.Length)];
        for (var suffix = 2; styles.Any(item => string.Equals(item.SemanticRole, role, StringComparison.OrdinalIgnoreCase)); suffix++)
            role = $"{roleStem[..Math.Min(65, roleStem.Length)]}-{suffix}";
        var nameStem = $"{source?.Name ?? "Body text"} — {edition.Name}";
        var name = nameStem[..Math.Min(72, nameStem.Length)];
        for (var suffix = 2; styles.Any(item => item.Kind == (source?.Kind ?? ManuscriptStyleKind.Paragraph)
            && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)); suffix++)
            name = $"{nameStem[..Math.Min(66, nameStem.Length)]} {suffix}";
        var created = new ManuscriptStyleDefinition
        {
            ProjectId = projectId,
            Name = name,
            NameKey = name.ToUpperInvariant(),
            Kind = source?.Kind ?? ManuscriptStyleKind.Paragraph,
            SemanticRole = role,
            SemanticRoleKey = role.ToUpperInvariant(),
            DefinitionJson = JsonSerializer.Serialize(definition, ManuscriptCodec.JsonOptions),
            Revision = 1,
        };
        db.ManuscriptStyleDefinitions.Add(created);
        styles.Add(created);
        return role;
    }

    private async Task<Dictionary<Guid, Guid>> CloneImportedEditionCompositionsAsync(
        Guid projectId,
        Guid chapterId,
        Guid editionId,
        ManuscriptDocument document,
        IReadOnlyDictionary<string, string> roles,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var sourceIds = document.Content.Where(item => item.Type == ManuscriptBlockType.DesignedPage && item.DesignedPageId.HasValue)
            .Select(item => item.DesignedPageId!.Value).Distinct().ToList();
        if (sourceIds.Count == 0)
            return [];
        var sources = await db.DesignedPages.AsNoTracking()
            .Include(item => item.Contents)
            .ThenInclude(content => content.Variants)
            .Where(item => item.ProjectId == projectId && item.ScopeEditionId == null && sourceIds.Contains(item.Id))
            .ToListAsync(cancellationToken);
        var map = new Dictionary<Guid, Guid>();
        var activeVariants = new List<(DesignedPageContent Content, Guid? ActiveVariantId)>();
        foreach (var source in sources)
        {
            var core = source.Contents.SingleOrDefault(content => content.EditionId is null)
                ?? throw new InvalidDataException($"Designed Page {source.Id:N} has no Core content.");
            if (source.Contents.Any(content => content.EditionId == editionId))
            {
                map[source.Id] = source.Id;
                continue;
            }
            var clone = new DesignedPageContent
            {
                ProjectId = projectId,
                DesignedPageId = source.Id,
                EditionId = editionId,
                AccessibilityDescription = core.AccessibilityDescription,
                Revision = core.Revision,
            };
            var semantic = RehomeImportedEditionContent(
                ManuscriptCodec.Deserialize(core.SemanticManuscriptJson, core.Id, core.Revision),
                clone.Id,
                roles);
            clone.SemanticManuscriptJson = ManuscriptCodec.Serialize(semantic);
            var variantMap = new Dictionary<Guid, Guid>();
            foreach (var sourceVariant in core.Variants)
            {
                var cloneVariant = new DesignedPageVariant
                {
                    Content = clone,
                    GeometryKey = sourceVariant.GeometryKey,
                    SceneJson = sourceVariant.SceneJson,
                    Revision = sourceVariant.Revision,
                };
                variantMap[sourceVariant.Id] = cloneVariant.Id;
                clone.Variants.Add(cloneVariant);
            }
            if (core.ActiveVariantId is Guid activeId && variantMap.TryGetValue(activeId, out var cloneActiveId))
                clone.ActiveVariantId = cloneActiveId;
            activeVariants.Add((clone, clone.ActiveVariantId));
            clone.ActiveVariantId = null;
            db.DesignedPageContents.Add(clone);
            map[source.Id] = source.Id;
        }
        if (activeVariants.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            foreach (var (content, activeVariantId) in activeVariants)
                content.ActiveVariantId = activeVariantId;
            await db.SaveChangesAsync(cancellationToken);
        }
        return map;
    }

    internal static ManuscriptDocument RehomeImportedEditionContent(
        ManuscriptDocument document,
        Guid contentId,
        IReadOnlyDictionary<string, string> roles) =>
        ReplaceImportedStyleRoles(document, roles) with { ManuscriptId = contentId };

    private static ManuscriptDocument ReplaceImportedStyleRoles(
        ManuscriptDocument document,
        IReadOnlyDictionary<string, string> roles) => document with
        {
            Content = document.Content.Select(block => block with
            {
                StyleRole = ReplacementImportedRole(block, roles),
                Content = block.Content.Select(inline => inline with
                {
                    Marks = inline.Marks.Select(mark => mark.Type == ManuscriptMarkType.CharacterStyle
                        && mark.Value is { } value && roles.TryGetValue(value, out var replacement)
                            ? mark with { Value = replacement }
                            : mark).ToList(),
                }).ToList(),
            }).ToList(),
        };

    private static string ReplacementImportedRole(
        ManuscriptBlock block,
        IReadOnlyDictionary<string, string> roles)
    {
        if (!string.IsNullOrWhiteSpace(block.StyleRole))
            return roles.GetValueOrDefault(block.StyleRole, block.StyleRole);
        return block.Type == ManuscriptBlockType.Paragraph
            ? roles.GetValueOrDefault(ManuscriptStyleRoles.Body, block.StyleRole ?? string.Empty)
            : block.StyleRole ?? string.Empty;
    }

    private static ManuscriptStyleProperties MergeImportedStyle(
        ManuscriptStyleProperties inherited,
        ManuscriptStyleProperties value) => inherited with
        {
            FontFamilyKey = value.FontFamilyKey ?? inherited.FontFamilyKey,
            FontSizePoints = value.FontSizePoints ?? inherited.FontSizePoints,
            FontWeight = value.FontWeight ?? inherited.FontWeight,
            Italic = value.Italic ?? inherited.Italic,
            SmallCaps = value.SmallCaps ?? inherited.SmallCaps,
            LineHeight = value.LineHeight ?? inherited.LineHeight,
            SpaceBeforePoints = value.SpaceBeforePoints ?? inherited.SpaceBeforePoints,
            SpaceAfterPoints = value.SpaceAfterPoints ?? inherited.SpaceAfterPoints,
            KeepWithNext = value.KeepWithNext ?? inherited.KeepWithNext,
            TextAlign = value.TextAlign ?? inherited.TextAlign,
            LeftIndentEm = value.LeftIndentEm ?? inherited.LeftIndentEm,
            RightIndentEm = value.RightIndentEm ?? inherited.RightIndentEm,
            FirstLineIndentEm = value.FirstLineIndentEm ?? inherited.FirstLineIndentEm,
            StartOnNewPage = value.StartOnNewPage ?? inherited.StartOnNewPage,
        };

    private async Task ImportPublicationBookAsync(
        Guid projectId,
        ProjectExportPublicationBook imported,
        ImportState state,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        if (await db.PublicationBooks.AnyAsync(item => item.ProjectId == projectId, cancellationToken)) return;
        var book = new PublicationBook
        {
            ProjectId = projectId,
            Revision = imported.Revision,
            Title = imported.Title,
            Subtitle = imported.Subtitle,
            Author = imported.Author,
            Language = string.IsNullOrWhiteSpace(imported.Language) ? "en" : imported.Language,
            Publisher = imported.Publisher,
            Copyright = imported.Copyright,
            Description = imported.Description,
            IncludeTableOfContents = imported.IncludeTableOfContents,
            IncludeVisibleTableOfContents = imported.IncludeVisibleTableOfContents,
            IncludeActSynopses = imported.IncludeActSynopses,
            IncludeChapterSynopses = imported.IncludeChapterSynopses,
            IncludeActHeadings = imported.IncludeActHeadings,
            IncludeChapterHeadings = imported.IncludeChapterHeadings,
            NumberActs = imported.NumberActs,
            NumberChapters = imported.NumberChapters,
            TitlePageMode = imported.TitlePageMode,
            RectoChapterStarts = imported.RectoChapterStarts,
            PdfPresentation = new PublicationBookPdfPresentation
            {
                ProjectId = projectId,
                AllowDesignedPageOverrides = imported.AllowDesignedPageOverrides,
            },
        };
        foreach (var row in imported.OutlineItems)
        {
            var targetId = row.TargetKind == PublishOutlineTargetKind.Act
                ? state.ActMap.GetValueOrDefault(row.TargetId)
                : state.ChapterMap.GetValueOrDefault(row.TargetId);
            if (targetId == Guid.Empty) continue;
            book.OutlineItems.Add(new PublicationBookOutlineItem
            {
                ProjectId = projectId,
                TargetKind = row.TargetKind,
                TargetId = targetId,
                ActId = row.TargetKind == PublishOutlineTargetKind.Act ? targetId : null,
                ChapterId = row.TargetKind == PublishOutlineTargetKind.Chapter ? targetId : null,
                IsIncluded = row.IsIncluded,
                SortOrder = row.SortOrder,
            });
        }
        foreach (var row in imported.LegacyMatter)
        {
            var document = ManuscriptCodec.Deserialize(row.ManuscriptJson, row.Id, row.Revision);
            var matter = new PublicationBookMatter
            {
                ProjectId = projectId,
                Location = row.Location,
                Kind = row.Kind,
                Title = row.Title,
                Revision = row.Revision,
                IsIncluded = row.IsIncluded,
                SortOrder = row.SortOrder,
            };
            matter.ManuscriptJson = ManuscriptCodec.Serialize(RemapManuscriptFigures(document, matter.Id, state.ImageMap, state.EditionMap));
            book.Matter.Add(matter);
            state.CoreMatterMap[row.Id] = matter.Id;
        }
        foreach (var row in imported.LegacyImagePlacements)
        {
            var targetId = row.TargetKind == PublishOutlineTargetKind.Act
                ? state.ActMap.GetValueOrDefault(row.TargetId)
                : state.ChapterMap.GetValueOrDefault(row.TargetId);
            var assetId = state.ImageMap.GetValueOrDefault(row.AssetId);
            if (targetId == Guid.Empty || assetId == Guid.Empty) continue;
            var placement = new PublicationBookImagePlacement
            {
                ProjectId = projectId,
                AssetId = assetId,
                TargetKind = row.TargetKind,
                TargetId = targetId,
                ActId = row.TargetKind == PublishOutlineTargetKind.Act ? targetId : null,
                ChapterId = row.TargetKind == PublishOutlineTargetKind.Chapter ? targetId : null,
                PlacementKind = row.PlacementKind,
                Caption = row.Caption,
                SortOrder = row.SortOrder,
                PresentationJson = JsonSerializer.Serialize(row.Presentation ?? new FigurePresentation(), ManuscriptCodec.JsonOptions),
                AltText = row.AltText,
                Decorative = row.Decorative,
                Language = string.IsNullOrWhiteSpace(row.Language) ? "en" : row.Language,
                AccessibilityRole = row.AccessibilityRole,
            };
            book.ImagePlacements.Add(placement);
            state.CorePlacementMap[row.Id] = placement.Id;
        }
        book.CoverDesign = new PublicationBookCoverDesign
        {
            ProjectId = projectId,
            BackgroundColor = imported.CoverDesign?.BackgroundColor ?? "#5c7ca5",
            CompositionSceneJson = await ImportedCoreCoverSceneAsync(
                projectId,
                sourceEdition: null,
                imported.CoverDesign is null ? string.Empty : RemapSceneJson(
                    imported.CoverDesign.CompositionSceneJson, state.ImageMap, state.FontFamilyMap),
                cancellationToken),
            Revision = imported.CoverDesign?.Revision ?? 0,
        };
        db.PublicationBooks.Add(book);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task SeedImportedCoreBookAsync(
        Guid projectId,
        ProjectExportDocument document,
        ImportState state,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        if (await db.PublicationBooks.AnyAsync(item => item.ProjectId == projectId, cancellationToken)) return;
        var sourceExport = document.PublicationEditions.OrderByDescending(item => item.IsDefault).ThenBy(item => item.Name).FirstOrDefault();
        var sourceId = sourceExport is null ? Guid.Empty : state.EditionMap.GetValueOrDefault(sourceExport.Id);
        var source = sourceId == Guid.Empty ? null : await db.PublicationEditions
            .Include(item => item.OutlineItems).Include(item => item.Matter).Include(item => item.ImagePlacements).Include(item => item.CoverDesign)
            .SingleAsync(item => item.Id == sourceId, cancellationToken);
        var project = await db.Projects.AsNoTracking().SingleAsync(item => item.Id == projectId, cancellationToken);
        var book = new PublicationBook
        {
            ProjectId = projectId,
            Revision = 1,
            Title = string.IsNullOrWhiteSpace(source?.TitleOverride) ? project.Name : source.TitleOverride,
            Subtitle = source?.Subtitle ?? string.Empty,
            Author = source?.Author ?? string.Empty,
            Language = string.IsNullOrWhiteSpace(source?.Language) ? "en" : source.Language,
            Publisher = source?.Publisher ?? string.Empty,
            Copyright = source?.Copyright ?? string.Empty,
            Description = source?.Description ?? string.Empty,
            IncludeTableOfContents = source?.IncludeTableOfContents ?? true,
            IncludeVisibleTableOfContents = source?.IncludeVisibleTableOfContents ?? false,
            IncludeActSynopses = source?.IncludeActSynopses ?? false,
            IncludeChapterSynopses = source?.IncludeChapterSynopses ?? false,
            IncludeActHeadings = source?.IncludeActHeadings ?? false,
            IncludeChapterHeadings = source?.IncludeChapterHeadings ?? true,
            NumberActs = source?.NumberActs ?? false,
            NumberChapters = source?.NumberChapters ?? false,
            TitlePageMode = source?.TitlePageMode ?? PublishTitlePageMode.Automatic,
            RectoChapterStarts = source?.RectoChapterStarts ?? false,
            PdfPresentation = new PublicationBookPdfPresentation
            {
                ProjectId = projectId,
                AllowDesignedPageOverrides = source?.Format == PublicationEditionFormat.DigitalPdf
                    && source.AllowDesignedPageOverrides,
            },
            OutlineItems = source?.OutlineItems.Select(row => new PublicationBookOutlineItem
            { ProjectId = projectId, TargetKind = row.TargetKind, TargetId = row.TargetId, ActId = row.ActId, ChapterId = row.ChapterId, IsIncluded = row.IsIncluded, SortOrder = row.SortOrder }).ToList() ?? [],
            Matter = source?.Matter.Select(row => new PublicationBookMatter
            { ProjectId = projectId, Location = row.Location, Kind = row.Kind, Title = row.Title, ManuscriptJson = row.ManuscriptJson, Revision = row.Revision, IsIncluded = row.IsIncluded, SortOrder = row.SortOrder }).ToList() ?? [],
            ImagePlacements = source?.ImagePlacements.Select(row => new PublicationBookImagePlacement
            { ProjectId = projectId, AssetId = row.AssetId, TargetKind = row.TargetKind, TargetId = row.TargetId, ActId = row.ActId, ChapterId = row.ChapterId, PlacementKind = row.PlacementKind, SortOrder = row.SortOrder, Caption = row.Caption, PresentationJson = row.PresentationJson, AltText = row.AltText, Decorative = row.Decorative, Language = row.Language, AccessibilityRole = row.AccessibilityRole }).ToList() ?? [],
            CoverDesign = new PublicationBookCoverDesign { ProjectId = projectId, BackgroundColor = source?.CoverDesign?.BackgroundColor ?? "#5c7ca5", CompositionSceneJson = await ImportedCoreCoverSceneAsync(projectId, source, source?.CoverDesign?.CompositionSceneJson ?? string.Empty, cancellationToken), Revision = source?.CoverDesign?.Revision ?? 0 },
        };
        db.PublicationBooks.Add(book);
        await db.SaveChangesAsync(cancellationToken);
        var setup = await db.ProjectPageSetups.AsNoTracking().SingleOrDefaultAsync(item => item.ProjectId == projectId, cancellationToken);
        var releases = await db.PublicationEditions
            .Where(item => item.ProjectId == projectId)
            .Include(item => item.OutlineItems)
            .Include(item => item.Matter)
            .Include(item => item.ImagePlacements)
            .ToListAsync(cancellationToken);
        foreach (var release in releases)
        {
            release.OverrideFieldsJson = JsonSerializer.Serialize(
                PublicationCoreMigrationService.ChangedFields(book, setup, release));
            release.InheritsCoreCover = release.CoverDesign is null;
            foreach (var row in release.OutlineItems.ToList())
            {
                var inherited = book.OutlineItems.SingleOrDefault(item => item.TargetKind == row.TargetKind && item.TargetId == row.TargetId);
                if (inherited is not null && inherited.IsIncluded == row.IsIncluded && inherited.SortOrder == row.SortOrder)
                    db.PublicationEditionOutlineItems.Remove(row);
            }
            foreach (var row in release.Matter.ToList())
            {
                var inherited = book.Matter.FirstOrDefault(item => item.Location == row.Location && item.Kind == row.Kind && item.SortOrder == row.SortOrder);
                if (inherited is null) continue;
                if (row.Title == inherited.Title && row.ManuscriptJson == inherited.ManuscriptJson
                    && row.Revision == inherited.Revision && row.IsIncluded == inherited.IsIncluded)
                    db.PublicationMatter.Remove(row);
                else
                    row.CoreMatterId = inherited.Id;
            }
            foreach (var row in release.ImagePlacements.ToList())
            {
                var inherited = book.ImagePlacements.FirstOrDefault(item => item.TargetKind == row.TargetKind
                    && item.TargetId == row.TargetId && item.PlacementKind == row.PlacementKind && item.SortOrder == row.SortOrder);
                if (inherited is null) continue;
                if (row.AssetId == inherited.AssetId && row.Caption == inherited.Caption
                    && row.PresentationJson == inherited.PresentationJson && row.AltText == inherited.AltText
                    && row.Decorative == inherited.Decorative && row.Language == inherited.Language
                    && row.AccessibilityRole == inherited.AccessibilityRole)
                    db.PublicationImagePlacements.Remove(row);
                else
                    row.CorePlacementId = inherited.Id;
            }
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<string> ImportedCoreCoverSceneAsync(
        Guid projectId,
        PublicationEdition? sourceEdition,
        string sceneJson,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var setup = await db.ProjectPageSetups.AsNoTracking().SingleAsync(
            item => item.ProjectId == projectId,
            cancellationToken);
        var coreEdition = new PublicationEdition
        {
            ProjectId = projectId,
            Name = "Core Book",
            Format = PublicationEditionFormat.DigitalPdf,
            PageWidthInches = setup.PageWidthInches,
            PageHeightInches = setup.PageHeightInches,
            PageMarginInches = setup.PageMarginInches,
            BodyFontSizePoints = setup.BodyFontSizePoints,
            BodyLineHeight = setup.BodyLineHeight,
        };
        if (string.IsNullOrWhiteSpace(sceneJson))
            return JsonSerializer.Serialize(
                CoverCompositionFactory.Create(coreEdition, new PublicationCoverDesign { EditionId = Guid.Empty }),
                ManuscriptCodec.JsonOptions);
        var scene = JsonSerializer.Deserialize<CompositionScene>(sceneJson, ManuscriptCodec.JsonOptions)
            ?? throw new InvalidDataException("Imported Core cover scene is empty.");
        if (sourceEdition?.Format == PublicationEditionFormat.Paperback)
            scene = CoverCompositionFactory.CreateCoreFrontFromRelease(sourceEdition, scene, coreEdition);
        return JsonSerializer.Serialize(scene, ManuscriptCodec.JsonOptions);
    }

    private async Task ImportGraphNodesAsync(
        ProjectImportJob job,
        ProjectExportDocument document,
        ImportState state,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var nodes = databaseOperation.Repositories.GraphNodes;
        foreach (var importedNode in document.Nodes)
        {
            var importedStableKey = StableKey(importedNode.NodeType, importedNode.Key);
            if (state.NodeMap.ContainsKey(importedStableKey)) continue;
            if (string.Equals(importedNode.NodeType, EntityTypeService.ProjectNodeType, StringComparison.Ordinal)) continue;
            if (document.ExportKind == ProjectExportKind.Full && AppendStructuralNodeTypes.Contains(importedNode.NodeType)) continue;

            var remappedNode = RemapImportedNode(importedNode, state);
            if (string.Equals(remappedNode.NodeType, EntityTypeService.ProjectFactNodeType, StringComparison.Ordinal))
            {
                await ImportProjectFactAsync(job, remappedNode, state, cancellationToken, importedStableKey);
                continue;
            }

            var importedProperties = NormalizeProperties(remappedNode.Properties);
            var existing = await FindExistingNodeAsync(job.ProjectId, remappedNode, cancellationToken);
            if (existing is null)
            {
                var created = await graph.UpsertNodeAsync(job.ProjectId, remappedNode.NodeType, remappedNode.Key, remappedNode.Label, importedProperties, cancellationToken);
                state.NodeMap[importedStableKey] = created;
                job.CreatedNodeCount++;
                AddContextEntityId(created, state.ContextEntityIdsToReindex);
                await AddReportAsync(job, ProjectImportReportItemKind.Entity, $"Created {created.NodeType} {created.Label ?? created.Key}", string.Empty, created.NodeType, created.Key, graphNodeId: created.Id, cancellationToken: cancellationToken);
                continue;
            }

            var changed = false;
            if (string.IsNullOrWhiteSpace(existing.Label) && !string.IsNullOrWhiteSpace(importedNode.Label))
            {
                existing.Label = importedNode.Label;
                changed = true;
            }
            changed |= MergeProperties(existing.Properties, importedProperties);
            if (changed)
            {
                existing.UpdatedAt = DateTime.UtcNow;
                nodes.Update(existing);
                await databaseOperation.SaveChangesAsync(cancellationToken);
            }

            state.NodeMap[importedStableKey] = existing;
            job.MergedNodeCount++;
            AddContextEntityId(existing, state.ContextEntityIdsToReindex);
            await AddReportAsync(job, ProjectImportReportItemKind.Entity, $"Merged {existing.NodeType} {existing.Label ?? existing.Key}", "Existing canonical values were preserved; missing fields and provenance were added.", existing.NodeType, existing.Key, graphNodeId: existing.Id, cancellationToken: cancellationToken);
        }
    }

    private async Task ImportProjectFactAsync(
        ProjectImportJob job,
        ProjectExportNode importedNode,
        ImportState state,
        CancellationToken cancellationToken,
        string? originalStableKey = null)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var nodes = databaseOperation.Repositories.GraphNodes;
        var importedProperties = NormalizeProperties(importedNode.Properties);
        var key = ReadString(importedProperties, "key") ?? importedNode.Label ?? importedNode.Key;
        var value = ReadString(importedProperties, "value") ?? string.Empty;
        var existing = await projectFacts.GetByKeyAsync(job.ProjectId, key, cancellationToken);
        var requestedId = existing is null && Guid.TryParseExact(importedNode.Key, "N", out var importedId)
            ? importedId
            : (Guid?)null;
        var fact = await projectFacts.UpsertAsync(job.ProjectId, key, value, requestedId, cancellationToken);
        var node = await nodes.FindByKeyAsync(job.ProjectId, fact.Id.ToString("N"), cancellationToken)
            ?? throw new InvalidOperationException($"Imported project fact {fact.Id} could not be resolved.");
        var changed = MergeProperties(node.Properties, importedProperties);
        if (changed)
        {
            node.UpdatedAt = DateTime.UtcNow;
            nodes.Update(node);
            await databaseOperation.SaveChangesAsync(cancellationToken);
        }

        state.NodeMap[originalStableKey ?? StableKey(importedNode.NodeType, importedNode.Key)] = node;
        if (existing is null) job.CreatedNodeCount++; else job.MergedNodeCount++;
        await AddReportAsync(job, ProjectImportReportItemKind.Entity, $"{(existing is null ? "Created" : "Merged")} project fact {fact.Key}", fact.Value, EntityTypeService.ProjectFactNodeType, fact.Id.ToString("N"), entityId: fact.Id, graphNodeId: node.Id, cancellationToken: cancellationToken);
    }

    private async Task ImportGraphEdgesAsync(
        ProjectImportJob job,
        ProjectExportDocument document,
        ImportState state,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var edges = databaseOperation.Repositories.GraphEdges;
        foreach (var importedEdge in document.Edges)
        {
            if (!state.NodeMap.TryGetValue(importedEdge.From.StableKey, out var fromNode)
                || !state.NodeMap.TryGetValue(importedEdge.To.StableKey, out var toNode))
            {
                await AddWarningAsync(job, $"Skipped {importedEdge.EdgeType} relationship", "One or both relationship endpoints were not imported.", cancellationToken);
                continue;
            }

            if (IsManagedStructuralEdge(importedEdge)) continue;

            var importedProperties = NormalizeProperties(RemapSourceReferences(importedEdge.Properties, state));
            var existing = await edges.FindAsync(fromNode.Id, toNode.Id, importedEdge.EdgeType, cancellationToken);
            if (existing is null)
            {
                var created = await graph.UpsertEdgeAsync(fromNode.Id, toNode.Id, importedEdge.EdgeType, importedProperties, importedEdge.SortOrder, cancellationToken);
                job.CreatedEdgeCount++;
                AddContextEndpointIds(fromNode, toNode, state.ContextEntityIdsToReindex);
                await AddReportAsync(job, ProjectImportReportItemKind.Relationship, $"Created {created.EdgeType} relationship", $"{fromNode.Label ?? fromNode.Key} -> {toNode.Label ?? toNode.Key}", created.EdgeType, created.Id.ToString(), graphEdgeId: created.Id, cancellationToken: cancellationToken);
                continue;
            }

            var changed = MergeProperties(existing.Properties, importedProperties);
            if (existing.SortOrder is null && importedEdge.SortOrder is not null)
            {
                existing.SortOrder = importedEdge.SortOrder;
                changed = true;
            }
            if (changed)
            {
                existing.UpdatedAt = DateTime.UtcNow;
                edges.Update(existing);
                await databaseOperation.SaveChangesAsync(cancellationToken);
            }

            job.MergedEdgeCount++;
            AddContextEndpointIds(fromNode, toNode, state.ContextEntityIdsToReindex);
            await AddReportAsync(job, ProjectImportReportItemKind.Relationship, $"Merged {existing.EdgeType} relationship", $"{fromNode.Label ?? fromNode.Key} -> {toNode.Label ?? toNode.Key}", existing.EdgeType, existing.Id.ToString(), graphEdgeId: existing.Id, cancellationToken: cancellationToken);
        }
    }

    private async Task<bool> ReindexBestEffortAsync(ProjectImportJob job, ImportState state, CancellationToken cancellationToken)
    {
        var succeeded = true;
        foreach (var actId in state.CreatedActIds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            succeeded &= await TryReindexAsync(job, "Act context index refresh failed", () => contextIndexing.ReindexActAsync(actId, cancellationToken), cancellationToken);
        }

        foreach (var chapterId in state.CreatedChapterIds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            succeeded &= await TryReindexAsync(job, "Chapter context index refresh failed", () => contextIndexing.ReindexChapterAsync(chapterId, cancellationToken), cancellationToken);
            succeeded &= await TryReindexAsync(job, "Chapter body index refresh failed", () => chapters.ReindexAsync(chapterId, cancellationToken), cancellationToken);
        }

        foreach (var entityId in state.ContextEntityIdsToReindex.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            succeeded &= await TryReindexAsync(job, "Entity context index refresh failed", () => contextIndexing.ReindexEntityAsync(job.ProjectId, entityId, cancellationToken), cancellationToken);
        }

        foreach (var sourceId in state.IngestSourceMap.Values.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            succeeded &= await TryReindexAsync(job, "Imported source context index refresh failed", () => contextIndexing.ReindexIngestSourceAsync(sourceId, cancellationToken), cancellationToken);
            succeeded &= await TryReindexAsync(job, "Imported source search index refresh failed", async () =>
            {
                await using var operation = await database.OpenReadAsync(cancellationToken);
                var source = await operation.Db.IngestSources.Include(item => item.SourceChunks)
                    .SingleAsync(item => item.Id == sourceId, cancellationToken);
                await ingestVectorIndexing.EnsureVectorFragmentsAsync(source, cancellationToken: cancellationToken);
            }, cancellationToken);
        }

        return succeeded;
    }

    private async Task<bool> ReindexCommittedProjectAsync(ProjectImportJob job, CancellationToken cancellationToken)
    {
        List<Guid> actIds;
        List<Guid> chapterIds;
        List<Guid> entityIds;
        List<Guid> sourceIds;
        await using (var operation = await database.OpenReadAsync(cancellationToken))
        {
            actIds = await operation.Db.Acts.AsNoTracking()
                .Where(item => item.ProjectId == job.ProjectId)
                .Select(item => item.Id)
                .ToListAsync(cancellationToken);
            chapterIds = await operation.Db.Chapters.AsNoTracking()
                .Where(item => item.ProjectId == job.ProjectId)
                .Select(item => item.Id)
                .ToListAsync(cancellationToken);
            var entityKeys = await operation.Db.GraphNodes.AsNoTracking()
                .Where(item => item.ProjectId == job.ProjectId
                    && item.NodeType != EntityTypeService.ProjectNodeType
                    && item.NodeType != EntityTypeService.ActNodeType
                    && item.NodeType != EntityTypeService.ChapterNodeType
                    && item.NodeType != EntityTypeService.ProjectFactNodeType
                    && item.NodeType != EntityTypeService.SourceNodeType
                    && item.NodeType != EntityTypeService.SourceChunkNodeType
                    && item.NodeType != EntityTypeService.SourceBlockNodeType)
                .Select(item => item.Key)
                .ToListAsync(cancellationToken);
            entityIds = entityKeys
                .Where(key => Guid.TryParseExact(key, "N", out _))
                .Select(key => Guid.ParseExact(key, "N"))
                .ToList();
            sourceIds = await operation.Db.IngestSources.AsNoTracking()
                .Where(item => item.ProjectId == job.ProjectId)
                .Select(item => item.Id)
                .ToListAsync(cancellationToken);
        }

        var succeeded = await TryReindexAsync(job, "Project context index refresh failed", () =>
            contextIndexing.ReindexProjectProfileAsync(job.ProjectId, cancellationToken), cancellationToken);
        foreach (var actId in actIds)
            succeeded &= await TryReindexAsync(job, "Act context index refresh failed", () => contextIndexing.ReindexActAsync(actId, cancellationToken), cancellationToken);
        foreach (var chapterId in chapterIds)
        {
            succeeded &= await TryReindexAsync(job, "Chapter context index refresh failed", () => contextIndexing.ReindexChapterAsync(chapterId, cancellationToken), cancellationToken);
            succeeded &= await TryReindexAsync(job, "Chapter body index refresh failed", () => chapters.ReindexAsync(chapterId, cancellationToken), cancellationToken);
        }
        foreach (var entityId in entityIds)
            succeeded &= await TryReindexAsync(job, "Entity context index refresh failed", () => contextIndexing.ReindexEntityAsync(job.ProjectId, entityId, cancellationToken), cancellationToken);
        foreach (var sourceId in sourceIds)
        {
            succeeded &= await TryReindexAsync(job, "Imported source context index refresh failed", () => contextIndexing.ReindexIngestSourceAsync(sourceId, cancellationToken), cancellationToken);
            succeeded &= await TryReindexAsync(job, "Imported source search index refresh failed", async () =>
            {
                await using var operation = await database.OpenReadAsync(cancellationToken);
                var source = await operation.Db.IngestSources.Include(item => item.SourceChunks)
                    .SingleAsync(item => item.Id == sourceId, cancellationToken);
                await ingestVectorIndexing.EnsureVectorFragmentsAsync(source, cancellationToken: cancellationToken);
            }, cancellationToken);
        }
        return succeeded;
    }

    private async Task<bool> TryReindexAsync(
        ProjectImportJob job,
        string warningTitle,
        Func<Task> action,
        CancellationToken cancellationToken)
    {
        try
        {
            await action();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "{WarningTitle} after committed import {JobId}", warningTitle, job.Id);
            try
            {
                await AddWarningAsync(job, warningTitle, ex.Message, cancellationToken);
            }
            catch (Exception warningException) when (warningException is not OperationCanceledException)
            {
                logger.LogWarning(warningException, "Could not persist post-import indexing warning for committed import {JobId}", job.Id);
            }
            return false;
        }
    }

    private async Task<GraphNode?> FindExistingNodeAsync(
        Guid projectId,
        ProjectExportNode importedNode,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var nodes = databaseOperation.Repositories.GraphNodes;
        var byStableKey = await nodes.FindAsync(projectId, importedNode.NodeType, importedNode.Key, cancellationToken);
        if (byStableKey is not null) return byStableKey;

        if (NonStructuralMergeExcludedTypes.Contains(importedNode.NodeType)) return null;
        var importedName = NormalizeComparable(importedNode.Label ?? importedNode.Key);
        if (importedName.Length == 0) return null;

        var candidates = await nodes.ListByTypeAsync(projectId, importedNode.NodeType, cancellationToken);
        return candidates.FirstOrDefault(candidate => NormalizeComparable(candidate.Label ?? candidate.Key) == importedName);
    }

    private static IReadOnlyList<ProjectExportChapter> OrderedImportedChapters(ProjectExportDocument document)
    {
        var actOrder = document.Acts.ToDictionary(act => act.Id, act => act.Order);
        return document.Chapters
            .OrderBy(chapter => chapter.ActId is Guid actId && actOrder.TryGetValue(actId, out var order) ? order : int.MaxValue)
            .ThenBy(chapter => chapter.ActId is null ? 1 : 0)
            .ThenBy(chapter => chapter.Order)
            .ToList();
    }

    private async Task AddImageContextPreferencesAsync(
        Guid projectId,
        Guid chapterId,
        IReadOnlyList<Guid> exportedImageIds,
        IReadOnlyDictionary<Guid, Guid> imageMap,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        foreach (var exportedImageId in exportedImageIds.Distinct())
        {
            if (!imageMap.TryGetValue(exportedImageId, out var localImageId))
                continue;

            await db.EditorContextPreferences.AddAsync(new EditorContextPreference
            {
                ProjectId = projectId,
                ChapterId = chapterId,
                Kind = ContextItemKind.ProjectImage.ToString(),
                Key = EditorContextKeys.ProjectImage(localImageId),
                IsIncluded = true,
            }, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static string RewriteIllustrationLayoutJson(
        string layoutJson,
        IReadOnlyDictionary<Guid, Guid> imageMap)
    {
        if (string.IsNullOrWhiteSpace(layoutJson))
            return layoutJson;

        try
        {
            var layout = JsonSerializer.Deserialize<IllustratedProseLayout>(layoutJson, JsonOptions)
                ?? new IllustratedProseLayout([]);
            var images = layout.Images
                .Select(image => imageMap.TryGetValue(image.ImageId, out var localImageId)
                    ? image with { ImageId = localImageId }
                    : throw new InvalidDataException(
                        $"Illustrated Prose element {image.Id:N} references an image that was not imported."))
                .ToList();
            return JsonSerializer.Serialize(layout with { Images = images }, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The illustrated-prose layout is malformed.", exception);
        }
    }

    internal static ManuscriptDocument ImportCurrentManuscript(
        ProjectExportChapter chapter,
        Guid localChapterId,
        int formatVersion,
        IReadOnlyDictionary<Guid, Guid> imageMap,
        IReadOnlyDictionary<Guid, Guid>? compositionMap = null,
        IReadOnlyDictionary<Guid, Guid>? editionMap = null)
    {
        var imported = ReadCurrentManuscript(chapter, formatVersion);
        var remapped = RemapManuscriptFigures(imported, localChapterId, imageMap, compositionMap, editionMap);
        ManuscriptCodec.Validate(remapped, localChapterId, remapped.Revision);
        return remapped;
    }

    private static ManuscriptDocument RemapManuscriptFigures(
        ManuscriptDocument imported,
        Guid localManuscriptId,
        IReadOnlyDictionary<Guid, Guid> imageMap,
        IReadOnlyDictionary<Guid, Guid>? compositionMap = null,
        IReadOnlyDictionary<Guid, Guid>? editionMap = null) =>
        imported with
        {
            ManuscriptId = localManuscriptId,
            Content = imported.Content.Select(block =>
                block.Type == ManuscriptBlockType.Figure
                    ? block with
                    {
                        ImageId = block.ImageId is Guid exportedImageId
                            && imageMap.TryGetValue(exportedImageId, out var localImageId)
                                ? localImageId
                                : throw new InvalidOperationException(
                                    $"Figure block {block.Id} references an image that was not imported."),
                        FigurePresentation = RemapFigurePresentation(
                            block.FigurePresentation ?? new FigurePresentation()),
                    }
                    : block.Type == ManuscriptBlockType.DesignedPage
                        ? block with
                        {
                            DesignedPageId = block.DesignedPageId is Guid exportedCompositionId
                                && compositionMap?.TryGetValue(exportedCompositionId, out var localCompositionId) == true
                                    ? localCompositionId
                                    : throw new InvalidOperationException(
                                        $"Designed Page block {block.Id} references a composition that was not imported."),
                        }
                        : block).ToList(),
        };

    private static FigurePresentation RemapFigurePresentation(FigurePresentation presentation) => presentation;

    private static string RewritePageLayoutJson(
        string layoutJson,
        IReadOnlyDictionary<Guid, Guid> imageMap,
        IReadOnlyDictionary<Guid, Guid>? fontFamilyMap = null)
    {
        if (string.IsNullOrWhiteSpace(layoutJson))
            return layoutJson;

        try
        {
            var layout = JsonSerializer.Deserialize<PicturePageLayout>(layoutJson, ManuscriptCodec.JsonOptions)
                ?? new PicturePageLayout([], []);
            var images = layout.Images
                .Select(image => imageMap.TryGetValue(image.ImageId, out var localImageId)
                    ? image with { ImageId = localImageId }
                    : throw new InvalidDataException(
                        $"Picture Page element {image.Id:N} references an image that was not imported."))
                .ToList();
            var textElements = layout.TextElements
                .Select(text =>
                {
                    if (!TryReadProjectFontKey(text.FontFamilyKey, out var exportedFamilyId))
                        return text;
                    if (fontFamilyMap is null
                        || !fontFamilyMap.TryGetValue(exportedFamilyId, out var localFamilyId))
                    {
                        throw new InvalidDataException(
                            $"Picture Page text element {text.Id:N} references a project font that was not imported.");
                    }
                    return text with { FontFamilyKey = ProjectFontService.CustomKey(localFamilyId) };
                })
                .ToList();
            return JsonSerializer.Serialize(
                layout with { Images = images, TextElements = textElements },
                JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Picture Page layout is malformed.", exception);
        }
    }

    private static ManuscriptDocument ConvertImportedIllustrations(
        ManuscriptDocument source,
        string layoutJson,
        IReadOnlyDictionary<Guid, string> assetAltById)
    {
        var layout = string.IsNullOrWhiteSpace(layoutJson)
            ? new IllustratedProseLayout([])
            : JsonSerializer.Deserialize<IllustratedProseLayout>(layoutJson, ManuscriptCodec.JsonOptions)
                ?? new IllustratedProseLayout([]);
        var blocks = source.Content.ToList();
        foreach (var image in layout.Images.OrderBy(item => item.SortOrder).ThenBy(item => item.Id))
        {
            var index = blocks.FindIndex(block => string.Equals(block.Id, image.BlockId, StringComparison.OrdinalIgnoreCase));
            index = index < 0 ? blocks.Count : index + (image.AnchorPosition == ChapterImageAnchorPosition.AfterParagraph ? 1 : 0);
            blocks.Insert(index, new ManuscriptBlock
            {
                Id = image.Id.ToString("N"),
                Type = ManuscriptBlockType.Figure,
                StyleRole = ManuscriptStyleRoles.FigureCaption,
                ImageId = image.ImageId,
                AltText = FirstNonEmpty(image.AltTextOverride, assetAltById.GetValueOrDefault(image.ImageId)),
                FigurePresentation = new FigurePresentation
                {
                    Placement = image.Alignment == ChapterImageAlignment.Center
                        ? FigurePlacementIntent.Centered
                        : FigurePlacementIntent.Float,
                    WidthPercent = Math.Clamp(image.WidthPercent, 1, 100),
                    Alignment = image.Alignment switch
                    {
                        ChapterImageAlignment.Left => FigureAlignment.Start,
                        ChapterImageAlignment.Right => FigureAlignment.End,
                        _ => FigureAlignment.Center,
                    },
                    TextWrap = image.Alignment switch
                    {
                        ChapterImageAlignment.Left => FigureTextWrap.End,
                        ChapterImageAlignment.Right => FigureTextWrap.Start,
                        _ => FigureTextWrap.None,
                    },
                    StartOnNewPage = image.StartOnNewPage,
                },
                Content = string.IsNullOrWhiteSpace(image.Caption)
                    ? []
                    : [new ManuscriptInline { Text = image.Caption.Trim() }],
            });
        }
        return source with { Content = blocks };
    }

    private static DesignedPage CreateImportedDesignedPage(
        Guid projectId,
        Guid chapterId,
        ProjectExportChapter chapter,
        ManuscriptDocument semantic,
        string layoutJson,
        IReadOnlyDictionary<Guid, string> assetAltById)
    {
        var layout = string.IsNullOrWhiteSpace(layoutJson)
            ? new PicturePageLayout([], [])
            : JsonSerializer.Deserialize<PicturePageLayout>(layoutJson, ManuscriptCodec.JsonOptions)
                ?? new PicturePageLayout([], []);
        var id = Guid.NewGuid();
        var layerId = Guid.NewGuid();
        var objects = new List<CompositionObject>();
        var nextImageReadingOrder = layout.TextElements.Select(item => item.ReadingOrder).DefaultIfEmpty(0).Max() + 1;
        foreach (var image in layout.Images)
        {
            var altText = FirstNonEmpty(image.AltTextOverride, assetAltById.GetValueOrDefault(image.ImageId));
            var accessibilityPending = string.IsNullOrWhiteSpace(altText);
            objects.Add(new CompositionObject
            {
                Id = image.Id,
                LayerId = layerId,
                Kind = CompositionObjectKind.Image,
                Bounds = new CompositionBounds
                {
                    XPercent = image.XPercent,
                    YPercent = image.YPercent,
                    WidthPercent = image.WidthPercent,
                    HeightPercent = image.HeightPercent,
                },
                ImageId = image.ImageId,
                ImageFit = image.Fit switch
                {
                    ChapterImageFit.Contain => FigureImageFit.Contain,
                    ChapterImageFit.Fill => FigureImageFit.Contain,
                    _ => FigureImageFit.Cover,
                },
                Opacity = Math.Clamp(image.Opacity, 0, 1),
                ZIndex = image.ZIndex,
                AltText = altText,
                Decorative = false,
                AccessibilityDecisionPending = accessibilityPending,
                SemanticRole = CompositionSemanticRole.Figure,
                ReadingOrder = nextImageReadingOrder++,
            });
        }
        objects.AddRange(layout.TextElements.Select(text => new CompositionObject
        {
            Id = text.Id,
            LayerId = layerId,
            Kind = CompositionObjectKind.Text,
            Bounds = new CompositionBounds
            {
                XPercent = text.XPercent,
                YPercent = text.YPercent,
                WidthPercent = text.WidthPercent,
                HeightPercent = text.HeightPercent,
            },
            ContentReferences = text.ContentReferences ?? [],
            FontFamilyKey = text.FontFamilyKey,
            FontWeight = text.FontWeight,
            Italic = text.Italic,
            FontSizePoints = text.FontSizePoints,
            LineHeight = text.LineHeight,
            LetterSpacingEm = text.LetterSpacingEm,
            FillColor = text.Color,
            BackgroundColor = text.BackgroundColor,
            BackgroundOpacity = text.BackgroundOpacity,
            TextAlignment = text.TextAlign switch
            {
                PicturePageTextAlign.Center => CompositionTextAlignment.Center,
                PicturePageTextAlign.Right => CompositionTextAlignment.End,
                _ => CompositionTextAlignment.Start,
            },
            VerticalAlignment = text.VerticalAlign switch
            {
                ChapterTextVerticalAlign.Middle => CompositionVerticalAlignment.Center,
                ChapterTextVerticalAlign.Bottom => CompositionVerticalAlignment.Bottom,
                _ => CompositionVerticalAlignment.Top,
            },
            TextShadow = text.Shadow switch
            {
                PicturePageTextShadow.Soft => CompositionTextShadow.Soft,
                PicturePageTextShadow.Strong => CompositionTextShadow.Strong,
                PicturePageTextShadow.Glow => CompositionTextShadow.Glow,
                _ => CompositionTextShadow.None,
            },
            ZIndex = text.ZIndex,
            ReadingOrder = text.ReadingOrder,
            SemanticRole = text.Role switch
            {
                PicturePageTextRole.Title => CompositionSemanticRole.Heading1,
                PicturePageTextRole.Heading => CompositionSemanticRole.Heading2,
                PicturePageTextRole.Caption => CompositionSemanticRole.Caption,
                PicturePageTextRole.Credit => CompositionSemanticRole.Credit,
                _ => CompositionSemanticRole.Paragraph,
            },
        }));
        var kind = chapter.PageLayoutKind switch
        {
            ChapterPageLayoutKind.SingleLandscape => CompositionSurfaceKind.IndependentPage,
            ChapterPageLayoutKind.DoublePortrait or ChapterPageLayoutKind.DoubleLandscape =>
                CompositionSurfaceKind.FacingSpread,
            _ => CompositionSurfaceKind.SinglePage,
        };
        var (width, height) = LegacyPicturePageGeometry.SurfacePoints(chapter.PageLayoutKind);
        var scene = new CompositionScene
        {
            Surface = new CompositionSurface { Kind = kind, WidthPoints = width, HeightPoints = height },
            Layers = [new CompositionLayer(layerId, "Content", 0)],
            Objects = objects,
        };
        var contentId = Guid.NewGuid();
        return new DesignedPage
        {
            Id = id,
            ProjectId = projectId,
            Name = chapter.Title,
            Contents =
            [
                new DesignedPageContent
                {
                    Id = contentId,
                    ProjectId = projectId,
                    DesignedPageId = id,
                    SemanticManuscriptJson = ManuscriptCodec.Serialize(semantic with { ManuscriptId = contentId }),
                    Revision = semantic.Revision,
                    Variants =
                    [
                        new DesignedPageVariant
                        {
                            ContentId = contentId,
                            GeometryKey = "import-seed",
                            SceneJson = JsonSerializer.Serialize(scene, ManuscriptCodec.JsonOptions),
                        },
                    ],
                },
            ],
        };
    }

    private static CompositionMutationStage CreateCompositionSeed(DesignedPage page, DesignedPageContent content, string sceneJson) => new()
    {
        ProjectId = page.ProjectId,
        ConversationId = Guid.Empty,
        TargetKind = "designed-page-seed",
        TargetId = content.Id,
        ExpectedRevision = content.Revision,
        OperationsJson = sceneJson,
        PayloadSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sceneJson))),
        ExpiresAt = DateTime.MaxValue,
    };

    private async Task MaterializeImportedDesignedPageVariantsAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var editions = await db.PublicationEditions.AsNoTracking()
            .Where(item => item.ProjectId == projectId)
            .ToListAsync(cancellationToken);
        if (editions.Count == 0) return;
        var seeds = await db.CompositionMutationStages
            .Where(item => item.ProjectId == projectId
                && item.ConversationId == Guid.Empty
                && item.TargetKind == "designed-page-seed")
            .ToListAsync(cancellationToken);
        if (seeds.Count == 0) return;
        var contentIds = seeds.Select(item => item.TargetId).ToHashSet();
        var contents = await db.DesignedPageContents
            .Include(item => item.Variants)
            .Where(item => item.ProjectId == projectId && contentIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        foreach (var seed in seeds)
        {
            if (!contents.TryGetValue(seed.TargetId, out var content))
                throw new InvalidDataException($"Imported Designed Page seed {seed.Id:N} has no content.");
            var actualHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seed.OperationsJson)));
            if (!string.Equals(actualHash, seed.PayloadSha256, StringComparison.Ordinal))
                throw new InvalidDataException($"Imported Designed Page seed {seed.Id:N} failed its integrity check.");
            var semantic = ManuscriptCodec.Deserialize(content.SemanticManuscriptJson, content.Id, content.Revision);
            foreach (var edition in editions)
            {
                var scene = DesignedPageService.AdaptSeedScene(seed.OperationsJson, edition);
                var geometryKey = DesignedPageService.GeometryKey(edition, scene);
                if (content.Variants.Any(item => item.GeometryKey == geometryKey))
                    continue;
                DesignedPageService.ValidateVariantGeometry(edition, scene);
                DesignedPageService.Validate(scene, semantic);
                db.DesignedPageVariants.Add(new DesignedPageVariant
                {
                    ContentId = content.Id,
                    GeometryKey = geometryKey,
                    SceneJson = JsonSerializer.Serialize(scene, ManuscriptCodec.JsonOptions),
                });
            }
            db.CompositionMutationStages.Remove(seed);
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static void ValidateExportSceneReferences(
        CompositionScene scene,
        IReadOnlySet<Guid> imageIds,
        IReadOnlySet<Guid> fontFamilyIds,
        bool allowCoverBindings,
        string label)
    {
        if (scene.Objects.Any(item => item.ImageId is Guid imageId && !imageIds.Contains(imageId)))
            throw new InvalidOperationException($"{label} references an image that is not included in the export.");
        var fontKeys = scene.Objects.Select(item => item.FontFamilyKey)
            .Concat(scene.Styles.Select(item => item.FontFamilyKey));
        foreach (var key in fontKeys)
        {
            if (TryReadProjectFontKey(key, out var familyId) && !fontFamilyIds.Contains(familyId))
                throw new InvalidOperationException($"{label} references a project font that is not included in the export.");
        }
        foreach (var item in scene.Objects.Where(item => item.Kind == CompositionObjectKind.Text))
        {
            if (!allowCoverBindings && !string.IsNullOrWhiteSpace(item.TextBinding))
                throw new InvalidOperationException($"{label} stores duplicated text instead of a semantic content reference.");
            if (allowCoverBindings && string.IsNullOrWhiteSpace(item.TextBinding))
                throw new InvalidOperationException($"{label} contains an empty cover text frame.");
            if (allowCoverBindings
                && PublicationTextBindings.UnknownTokens(item.TextBinding) is { Count: > 0 } unknownTokens)
                throw new InvalidOperationException($"{label} contains unsupported cover-copy token(s): {string.Join(", ", unknownTokens)}.");
        }
    }

    private static bool TryReadProjectFontKey(string value, out Guid familyId)
    {
        const string prefix = "project:";
        familyId = Guid.Empty;
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(value[prefix.Length..], out familyId);
    }

    private static string ExportBibliographicSignature(ProjectExportPublicationEdition edition) =>
        JsonSerializer.Serialize(new
        {
            edition.Format,
            Title = edition.TitleOverride.Trim(),
            Subtitle = edition.Subtitle.Trim(),
            Author = edition.Author.Trim(),
            Language = edition.Language.Trim().ToLowerInvariant(),
            Publisher = edition.Publisher.Trim(),
            Copyright = edition.Copyright.Trim(),
            Description = edition.Description.Trim(),
            edition.IncludeTableOfContents,
            edition.IncludeVisibleTableOfContents,
            edition.IncludeActSynopses,
            edition.IncludeChapterSynopses,
            edition.IncludeActHeadings,
            edition.IncludeChapterHeadings,
            edition.NumberActs,
            edition.NumberChapters,
            edition.TitlePageMode,
            edition.PrintPicturePageSpreadMode,
            edition.EpubPicturePageSpreadMode,
            edition.PrintArtifactRegistryVersion,
            edition.PrintArtifactProfileKey,
            edition.PrintCoverMode,
            edition.Bleed,
            edition.RectoChapterStarts,
            edition.PageWidthInches,
            edition.PageHeightInches,
            edition.PageMarginInches,
            BodyFontSizePoints = edition.ImportedBodyFontSizePoints,
            BodyLineHeight = edition.ImportedBodyLineHeight,
            Outline = edition.OutlineItems
                .OrderBy(item => item.SortOrder)
                .Select(item => new { item.TargetKind, item.TargetId, item.IsIncluded, item.SortOrder }),
            Matter = edition.LegacyMatter
                .OrderBy(item => item.Location).ThenBy(item => item.SortOrder)
                .Select(item => new
                {
                    item.Location,
                    item.Kind,
                    item.Title,
                    Content = PublicationEditionService.CanonicalManuscriptContent(
                        ManuscriptCodec.Deserialize(item.ManuscriptJson, item.Id, item.Revision)),
                    item.IsIncluded,
                    item.SortOrder,
                }),
            Mappings = edition.LegacyStyleMappings
                .OrderBy(item => item.SemanticRole, StringComparer.Ordinal)
                .Select(item => new { item.SemanticRole, item.Override }),
            Placements = edition.LegacyImagePlacements
                .OrderBy(item => item.TargetKind)
                .ThenBy(item => item.TargetId)
                .ThenBy(item => item.PlacementKind)
                .ThenBy(item => item.SortOrder)
                .Select(item => new
                {
                    item.AssetId,
                    item.TargetKind,
                    item.TargetId,
                    item.PlacementKind,
                    item.Caption,
                    item.SortOrder,
                }),
        }, ManuscriptCodec.JsonOptions);

    private static bool ImportedBibliographicProductSettingsMatch(
        PublicationEdition existing,
        ProjectExportPublicationEdition imported) =>
        string.Equals(existing.TitleOverride, imported.TitleOverride.Trim(), StringComparison.Ordinal)
        && string.Equals(existing.Subtitle, imported.Subtitle.Trim(), StringComparison.Ordinal)
        && string.Equals(existing.Author, imported.Author.Trim(), StringComparison.Ordinal)
        && string.Equals(existing.Language, imported.Language.Trim(), StringComparison.OrdinalIgnoreCase)
        && string.Equals(existing.Publisher, imported.Publisher.Trim(), StringComparison.Ordinal)
        && string.Equals(existing.Copyright, imported.Copyright.Trim(), StringComparison.Ordinal)
        && string.Equals(existing.Description, imported.Description.Trim(), StringComparison.Ordinal)
        && existing.IncludeTableOfContents == imported.IncludeTableOfContents
        && existing.IncludeVisibleTableOfContents == imported.IncludeVisibleTableOfContents
        && existing.IncludeActSynopses == imported.IncludeActSynopses
        && existing.IncludeChapterSynopses == imported.IncludeChapterSynopses
        && existing.IncludeActHeadings == imported.IncludeActHeadings
        && existing.IncludeChapterHeadings == imported.IncludeChapterHeadings
        && existing.NumberActs == imported.NumberActs
        && existing.NumberChapters == imported.NumberChapters
        && existing.TitlePageMode == imported.TitlePageMode
        && string.Equals(existing.PrintArtifactProfileKey,
            string.IsNullOrWhiteSpace(imported.ImportedPrintArtifactProfileKey)
                ? LegacyPrintArtifactProfile(imported.Format, imported.Vendor, imported.Paper, imported.Ink)
                : NormalizePrintArtifactProfileKey(imported.ImportedPrintArtifactProfileKey),
            StringComparison.Ordinal)
        && existing.Bleed == imported.Bleed
        && existing.AllowDesignedPageOverrides == imported.AllowDesignedPageOverrides
        && existing.RectoChapterStarts == imported.RectoChapterStarts
        && existing.PageWidthInches.Equals(imported.PageWidthInches)
        && existing.PageHeightInches.Equals(imported.PageHeightInches)
        && existing.PageMarginInches.Equals(imported.PageMarginInches)
        && existing.BodyFontSizePoints.Equals(imported.ImportedBodyFontSizePoints)
        && existing.BodyLineHeight.Equals(imported.ImportedBodyLineHeight);

    private static string LegacyPrintArtifactProfile(
        PublicationEditionFormat format,
        PublicationVendor vendor,
        LegacyPublicationPaper paper,
        LegacyPublicationInk ink) => (format, vendor, paper, ink) switch
        {
            (PublicationEditionFormat.Paperback, PublicationVendor.AmazonKdp, LegacyPublicationPaper.Cream, _) => "kdp-pb-bw-50-2500",
            (PublicationEditionFormat.Paperback, PublicationVendor.AmazonKdp, _, LegacyPublicationInk.Color) => "kdp-pb-premium-color",
            (PublicationEditionFormat.Paperback, PublicationVendor.AmazonKdp, _, _) => "kdp-pb-bw-50-2252",
            (PublicationEditionFormat.Paperback, PublicationVendor.IngramSpark, LegacyPublicationPaper.Cream, _) => "ingram-pb-bw-50-2225",
            (PublicationEditionFormat.Paperback, PublicationVendor.IngramSpark, _, LegacyPublicationInk.Color) => "ingram-pb-premium70",
            (PublicationEditionFormat.Paperback, PublicationVendor.IngramSpark, _, _) => "ingram-pb-bw-50-2009",
            (PublicationEditionFormat.Paperback, _, LegacyPublicationPaper.Cream, _) => "generic-pb-bw-60-cream",
            (PublicationEditionFormat.Paperback, _, _, _) => "generic-pb-bw-50-white",
            (PublicationEditionFormat.Hardcover, PublicationVendor.AmazonKdp, LegacyPublicationPaper.Cream, _) => "kdp-hc-bw-50-2500",
            (PublicationEditionFormat.Hardcover, PublicationVendor.AmazonKdp, _, LegacyPublicationInk.Color) => "kdp-hc-premium-color",
            (PublicationEditionFormat.Hardcover, PublicationVendor.AmazonKdp, _, _) => "kdp-hc-bw-50-2252",
            (PublicationEditionFormat.Hardcover, PublicationVendor.IngramSpark, LegacyPublicationPaper.Cream, _) => "ingram-hc-case-bw-50-2224",
            (PublicationEditionFormat.Hardcover, PublicationVendor.IngramSpark, _, LegacyPublicationInk.Color) => "ingram-hc-case-premium70",
            (PublicationEditionFormat.Hardcover, PublicationVendor.IngramSpark, _, _) => "ingram-hc-case-bw-50-2009",
            (PublicationEditionFormat.Hardcover, _, LegacyPublicationPaper.Cream, _) => "generic-case-bw-60-cream",
            (PublicationEditionFormat.Hardcover, _, _, _) => "generic-case-bw-50-white",
            _ => string.Empty,
        };

    private static string NormalizePrintArtifactProfileKey(string key) => key switch
    {
        "generic-perfectbound-template" => "generic-pb-bw-50-white",
        "generic-perfectbound-v1" => "generic-pb-bw-50-white",
        "generic-casebound-template" => "generic-case-bw-50-white",
        "generic-casebound-v1" => "generic-case-bw-50-white",
        "kdp-pb-bw-white" => "kdp-pb-bw-50-2252",
        "kdp-pb-bw-cream" => "kdp-pb-bw-50-2500",
        "kdp-pb-bw-groundwood" => "kdp-pb-bw-45-2350",
        "kdp-hc-bw-white" => "kdp-hc-bw-50-2252",
        "kdp-hc-bw-cream" => "kdp-hc-bw-50-2500",
        "ingram-pb-bw-white50" => "ingram-pb-bw-50-2009",
        "ingram-pb-bw-cream50" => "ingram-pb-bw-50-2225",
        "ingram-pb-bw-groundwood38" => "ingram-pb-bw-38-2550",
        "ingram-hc-case-bw-white50" => "ingram-hc-case-bw-50-2009",
        "ingram-hc-case-bw-cream50" => "ingram-hc-case-bw-50-2224",
        "ingram-hc-cloth-blue" or "ingram-hc-cloth-gray" => "ingram-hc-cloth-bw-50-2009",
        "ingram-hc-cloth-blue-jacket" or "ingram-hc-cloth-gray-jacket" => "ingram-hc-cloth-jacket-bw-50-2009",
        "bn-pb-bw-cream50-6x9" => "bn-pb-bw-50-6x9",
        "bn-hc-case-bw-cream50-6x9" => "bn-hc-case-bw-50-6x9",
        "bn-hc-jacket-bw-cream50-6x9" => "bn-hc-jacket-bw-50-6x9",
        _ => key,
    };

    private async Task<bool> ImportedBibliographicContentMatchesAsync(
        Guid existingEditionId,
        ProjectExportPublicationEdition imported,
        IReadOnlyDictionary<Guid, Guid> actMap,
        IReadOnlyDictionary<Guid, Guid> chapterMap,
        IReadOnlyDictionary<Guid, Guid> imageMap,
        IReadOnlyDictionary<Guid, Guid> editionMap,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var existingOutline = await db.PublicationEditionOutlineItems.AsNoTracking()
            .Where(item => item.EditionId == existingEditionId)
            .OrderBy(item => item.SortOrder)
            .Select(item => new { item.TargetKind, item.TargetId, item.IsIncluded, item.SortOrder })
            .ToListAsync(cancellationToken);
        var importedOutline = imported.OutlineItems
            .OrderBy(item => item.SortOrder)
            .Select(item => new
            {
                item.TargetKind,
                TargetId = item.TargetKind == PublishOutlineTargetKind.Act
                    ? actMap.GetValueOrDefault(item.TargetId)
                    : chapterMap.GetValueOrDefault(item.TargetId),
                item.IsIncluded,
                item.SortOrder,
            })
            .ToList();
        if (!string.Equals(
            JsonSerializer.Serialize(existingOutline, JsonOptions),
            JsonSerializer.Serialize(importedOutline, JsonOptions),
            StringComparison.Ordinal))
        {
            return false;
        }

        var existingMatter = await db.PublicationMatter.AsNoTracking()
            .Where(item => item.EditionId == existingEditionId)
            .OrderBy(item => item.Location).ThenBy(item => item.SortOrder)
            .ToListAsync(cancellationToken);
        var existingSignature = existingMatter.Select(item => new
        {
            item.Location,
            item.Kind,
            item.Title,
            Content = PublicationEditionService.CanonicalManuscriptContent(
                ManuscriptCodec.Deserialize(item.ManuscriptJson, item.Id, item.Revision)),
            item.IsIncluded,
            item.SortOrder,
        });
        var importedSignature = imported.LegacyMatter
            .OrderBy(item => item.Location).ThenBy(item => item.SortOrder)
            .Select(item => new
            {
                item.Location,
                item.Kind,
                item.Title,
                Content = PublicationEditionService.CanonicalManuscriptContent(
                    RemapManuscriptFigures(
                        ManuscriptCodec.Deserialize(item.ManuscriptJson, item.Id, item.Revision),
                        Guid.Empty,
                        imageMap,
                        editionMap: editionMap)),
                item.IsIncluded,
                item.SortOrder,
            });
        if (!string.Equals(
            JsonSerializer.Serialize(existingSignature, JsonOptions),
            JsonSerializer.Serialize(importedSignature, JsonOptions),
            StringComparison.Ordinal))
        {
            return false;
        }

        var existingPlacements = await db.PublicationImagePlacements.AsNoTracking()
            .Where(placement => placement.EditionId == existingEditionId)
            .OrderBy(placement => placement.TargetKind)
            .ThenBy(placement => placement.TargetId)
            .ThenBy(placement => placement.PlacementKind)
            .ThenBy(placement => placement.SortOrder)
            .Select(placement => new
            {
                placement.AssetId,
                placement.TargetKind,
                placement.TargetId,
                placement.PlacementKind,
                placement.Caption,
                placement.SortOrder,
            })
            .ToListAsync(cancellationToken);
        var importedPlacements = imported.LegacyImagePlacements
            .Select(placement => new
            {
                AssetId = imageMap.GetValueOrDefault(placement.AssetId),
                placement.TargetKind,
                TargetId = placement.TargetKind == PublishOutlineTargetKind.Act
                    ? actMap.GetValueOrDefault(placement.TargetId)
                    : chapterMap.GetValueOrDefault(placement.TargetId),
                placement.PlacementKind,
                placement.Caption,
                placement.SortOrder,
            })
            .OrderBy(placement => placement.TargetKind)
            .ThenBy(placement => placement.TargetId)
            .ThenBy(placement => placement.PlacementKind)
            .ThenBy(placement => placement.SortOrder);
        return string.Equals(
            JsonSerializer.Serialize(existingPlacements, JsonOptions),
            JsonSerializer.Serialize(importedPlacements, JsonOptions),
            StringComparison.Ordinal);
    }

    private async Task MarkApplyingAsync(ProjectImportJob job, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var imports = databaseOperation.Repositories.ProjectImports;
        job.Status = ProjectImportJobStatus.Applying;
        job.StartedAt = DateTime.UtcNow;
        job.UpdatedAt = DateTime.UtcNow;
        job.CurrentMessage = "Applying validated import.";
        imports.UpdateJob(job);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        Notify(job.ProjectId, job.Id, ProjectImportJobUpdateKind.Progress);
    }

    private async Task MarkIndexingAsync(ProjectImportJob job, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var imports = databaseOperation.Repositories.ProjectImports;
        job.Status = ProjectImportJobStatus.Indexing;
        job.CurrentMessage = "Import committed; rebuilding indexes.";
        job.UpdatedAt = DateTime.UtcNow;
        imports.UpdateJob(job);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        Notify(job.ProjectId, job.Id, ProjectImportJobUpdateKind.Progress);
    }

    private async Task MarkCompletedAsync(ProjectImportJob job, bool withoutWarnings, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var imports = databaseOperation.Repositories.ProjectImports;
        job.Status = withoutWarnings && job.WarningCount == 0
            ? ProjectImportJobStatus.Completed
            : ProjectImportJobStatus.CompletedWithWarnings;
        job.CurrentMessage = job.Status == ProjectImportJobStatus.Completed
            ? "Import completed."
            : "Import committed with indexing warnings.";
        job.CompletedAt = DateTime.UtcNow;
        job.UpdatedAt = DateTime.UtcNow;
        imports.UpdateJob(job);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        Notify(job.ProjectId, job.Id, ProjectImportJobUpdateKind.Completed);
        DeleteTerminalStagingFile(job);
    }

    private async Task MarkCancelledAsync(ProjectImportJob job, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var imports = databaseOperation.Repositories.ProjectImports;
        job.Status = ProjectImportJobStatus.Cancelled;
        job.CurrentMessage = "Import cancelled before commit.";
        job.CompletedAt = DateTime.UtcNow;
        job.UpdatedAt = DateTime.UtcNow;
        imports.UpdateJob(job);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        Notify(job.ProjectId, job.Id, ProjectImportJobUpdateKind.Failed);
        DeleteTerminalStagingFile(job);
    }

    private async Task StepAsync(ProjectImportJob job, string message, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var imports = databaseOperation.Repositories.ProjectImports;
        job.CompletedSteps = Math.Min(job.TotalSteps, job.CompletedSteps + 1);
        job.CurrentMessage = message;
        job.UpdatedAt = DateTime.UtcNow;
        imports.UpdateJob(job);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        Notify(job.ProjectId, job.Id, ProjectImportJobUpdateKind.Progress);
    }

    private async Task MarkFailedAsync(ProjectImportJob job, Exception exception, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var imports = databaseOperation.Repositories.ProjectImports;
        job.Status = ProjectImportJobStatus.Failed;
        job.CurrentMessage = "Import failed.";
        job.ErrorMessage = exception.Message;
        job.CompletedAt = DateTime.UtcNow;
        job.UpdatedAt = DateTime.UtcNow;
        imports.UpdateJob(job);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        await AddReportAsync(job, ProjectImportReportItemKind.Validation, "Import failed", exception.Message, status: ProjectImportReportItemStatus.Failed, errorMessage: exception.Message, cancellationToken: cancellationToken);
        Notify(job.ProjectId, job.Id, ProjectImportJobUpdateKind.Failed);
        DeleteTerminalStagingFile(job);
    }

    private void DeleteTerminalStagingFile(ProjectImportJob job)
    {
        try
        {
            _fileStore.Delete(new ProjectImportJobFileKey(job.StagedFileKey));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not remove staged import file for terminal job {JobId}", job.Id);
        }
    }

    private async Task AddWarningAsync(ProjectImportJob job, string title, string summary, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var imports = databaseOperation.Repositories.ProjectImports;
        job.WarningCount++;
        imports.UpdateJob(job);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        await AddReportAsync(job, ProjectImportReportItemKind.Warning, title, summary, cancellationToken: cancellationToken);
    }

    private async Task AddReportAsync(
        ProjectImportJob job,
        ProjectImportReportItemKind kind,
        string title,
        string summary,
        string resourceType = "",
        string resourceKey = "",
        Guid? entityId = null,
        long? graphNodeId = null,
        long? graphEdgeId = null,
        string payloadJson = "{}",
        ProjectImportReportItemStatus status = ProjectImportReportItemStatus.Active,
        string errorMessage = "",
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var imports = databaseOperation.Repositories.ProjectImports;
        await imports.AddReportItemAsync(new ProjectImportReportItem
        {
            JobId = job.Id,
            Kind = kind,
            Status = status,
            Title = title,
            Summary = summary,
            ResourceType = resourceType,
            ResourceKey = resourceKey,
            EntityId = entityId,
            GraphNodeId = graphNodeId,
            GraphEdgeId = graphEdgeId,
            PayloadJson = payloadJson,
            ErrorMessage = errorMessage,
        }, cancellationToken);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        Notify(job.ProjectId, job.Id, ProjectImportJobUpdateKind.Report);
    }

    private static bool IsManagedStructuralEdge(ProjectExportEdge edge)
    {
        if (!string.Equals(edge.EdgeType, EntityService.HasChildEdgeType, StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.Equals(edge.From.NodeType, EntityTypeService.ProjectNodeType, StringComparison.Ordinal)
            && (string.Equals(edge.To.NodeType, EntityTypeService.ActNodeType, StringComparison.Ordinal)
                || string.Equals(edge.To.NodeType, EntityTypeService.ChapterNodeType, StringComparison.Ordinal)
                || string.Equals(edge.To.NodeType, EntityTypeService.ProjectFactNodeType, StringComparison.Ordinal)))
        {
            return true;
        }

        return (string.Equals(edge.From.NodeType, EntityTypeService.ActNodeType, StringComparison.Ordinal)
                && string.Equals(edge.To.NodeType, EntityTypeService.ChapterNodeType, StringComparison.Ordinal))
            || (string.Equals(edge.From.NodeType, EntityTypeService.ChapterNodeType, StringComparison.Ordinal)
                && string.Equals(edge.To.NodeType, EntityTypeService.EventNodeType, StringComparison.Ordinal));
    }

    private static Dictionary<string, object?> NormalizeProperties(IDictionary<string, object?> source)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in source)
        {
            if (string.IsNullOrWhiteSpace(kv.Key)) continue;
            result[kv.Key] = NormalizeJsonValue(kv.Value);
        }

        return result;
    }

    private static object? NormalizeJsonValue(object? value)
    {
        if (value is not JsonElement element) return value;
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt32(out var intValue) => intValue,
            JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when element.TryGetDouble(out var doubleValue) => doubleValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            _ => element.GetRawText(),
        };
    }

    private static bool MergeProperties(IDictionary<string, object?> target, IReadOnlyDictionary<string, object?> source)
    {
        var changed = false;
        foreach (var kv in source)
        {
            if (string.IsNullOrWhiteSpace(kv.Key)) continue;
            if (IsAssertionProperty(kv.Key) && target.TryGetValue(kv.Key, out var existingAssertion))
            {
                var merged = MergeJsonObjectStrings(existingAssertion?.ToString(), kv.Value?.ToString());
                if (merged is not null && !string.Equals(existingAssertion?.ToString(), merged, StringComparison.Ordinal))
                {
                    target[kv.Key] = merged;
                    changed = true;
                }
                continue;
            }

            if (!target.TryGetValue(kv.Key, out var existing) || IsEmptyValue(existing))
            {
                target[kv.Key] = kv.Value;
                changed = true;
            }
        }

        return changed;
    }

    private static string? MergeJsonObjectStrings(string? existingJson, string? importedJson)
    {
        if (string.IsNullOrWhiteSpace(importedJson)) return existingJson;
        if (string.IsNullOrWhiteSpace(existingJson)) return importedJson;

        try
        {
            var existing = JsonNode.Parse(existingJson) as JsonObject;
            var imported = JsonNode.Parse(importedJson) as JsonObject;
            if (existing is null || imported is null) return existingJson;
            DeepMerge(existing, imported);
            return existing.ToJsonString(JsonOptions);
        }
        catch (JsonException)
        {
            return existingJson;
        }
    }

    private static void DeepMerge(JsonObject target, JsonObject source)
    {
        foreach (var kv in source.ToList())
        {
            if (!target.TryGetPropertyValue(kv.Key, out var existing) || existing is null)
            {
                target[kv.Key] = kv.Value?.DeepClone();
                continue;
            }

            if (existing is JsonObject existingObject && kv.Value is JsonObject importedObject)
            {
                DeepMerge(existingObject, importedObject);
                continue;
            }

            if (existing is JsonArray existingArray && kv.Value is JsonArray importedArray)
            {
                var existingValues = existingArray.Select(item => item?.ToJsonString()).ToHashSet(StringComparer.Ordinal);
                foreach (var item in importedArray)
                {
                    var key = item?.ToJsonString();
                    if (key is not null && existingValues.Add(key))
                        existingArray.Add(item?.DeepClone());
                }
            }
        }
    }

    private static bool IsAssertionProperty(string key) =>
        string.Equals(key, IngestSourceAssertions.EntityAssertionsProperty, StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, IngestSourceAssertions.RelationshipAssertionsProperty, StringComparison.OrdinalIgnoreCase);

    private static bool IsLegacyGeneratedMatterKind(PublicationMatterKind kind) =>
        kind is PublicationMatterKind.TitlePage
            or PublicationMatterKind.Copyright
            or PublicationMatterKind.Contents;

    private static void ValidateImportedLegacyOutlineOrder(
        IReadOnlyList<(PublishOutlineTargetKind TargetKind, Guid TargetId)> orderedItems,
        IReadOnlyCollection<Guid> actIds,
        IReadOnlyDictionary<Guid, Guid?> chapterParents)
    {
        var expected = actIds
            .Select(id => (PublishOutlineTargetKind.Act, id))
            .Concat(chapterParents.Keys.Select(id => (PublishOutlineTargetKind.Chapter, id)))
            .ToHashSet();
        if (orderedItems.Count != expected.Count
            || orderedItems.Distinct().Count() != orderedItems.Count
            || !orderedItems.All(expected.Contains))
        {
            throw new InvalidOperationException(
                "Imported publication content order must contain every current act and chapter exactly once.");
        }

        Guid? currentActId = null;
        var reachedUnassigned = false;
        foreach (var ordered in orderedItems)
        {
            if (ordered.TargetKind == PublishOutlineTargetKind.Act)
            {
                if (reachedUnassigned)
                    throw new InvalidOperationException("Imported act groups cannot appear after unassigned chapters.");
                currentActId = ordered.TargetId;
                continue;
            }

            var parentActId = chapterParents[ordered.TargetId];
            if (parentActId is null)
            {
                reachedUnassigned = true;
                currentActId = null;
            }
            else if (reachedUnassigned || currentActId != parentActId)
            {
                throw new InvalidOperationException(
                    "Every imported chapter must remain contiguous beneath its owning act.");
            }
        }
    }

    private static bool IsEmptyValue(object? value) =>
        value is null || string.IsNullOrWhiteSpace(value.ToString());

    private static string? ReadString(IReadOnlyDictionary<string, object?> properties, string key) =>
        properties.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static string NormalizeComparable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        Span<char> buffer = stackalloc char[value.Length];
        var index = 0;
        foreach (var ch in value.Trim())
        {
            if (char.IsLetterOrDigit(ch))
                buffer[index++] = char.ToLowerInvariant(ch);
        }
        return new string(buffer[..index]);
    }

    private async Task ImportManuscriptAnnotationsAsync(
        Guid projectId,
        ProjectExportDocument document,
        ImportState state,
        CancellationToken cancellationToken)
    {
        if (document.FormatVersion < 24 || document.ManuscriptAnnotations.Count == 0)
            return;
        await using var operation = await database.OpenWriteAsync(cancellationToken);
        operation.ShareWithNestedOperations();
        var db = operation.Db;
        foreach (var imported in document.ManuscriptAnnotations)
        {
            var chapterId = state.ChapterMap.TryGetValue(imported.ChapterId, out var remappedChapterId)
                ? remappedChapterId
                : imported.ChapterId;
            var chapter = await db.Chapters.SingleOrDefaultAsync(
                item => item.Id == chapterId && item.ProjectId == projectId,
                cancellationToken);
            if (chapter is null && !string.IsNullOrWhiteSpace(imported.ChapterTitle))
            {
                var titleMatches = await db.Chapters
                    .Where(item => item.ProjectId == projectId && item.Title == imported.ChapterTitle)
                    .Take(2)
                    .ToListAsync(cancellationToken);
                chapter = titleMatches.Count == 1 ? titleMatches[0] : null;
                chapterId = chapter?.Id ?? chapterId;
            }
            if (chapter is null)
                continue;

            Guid? editionId = null;
            if (imported.EditionId is Guid importedEditionId)
            {
                editionId = state.EditionMap.TryGetValue(importedEditionId, out var remappedEditionId)
                    ? remappedEditionId
                    : importedEditionId;
                if (!await db.PublicationEditions.AnyAsync(item => item.Id == editionId && item.ProjectId == projectId, cancellationToken))
                {
                    var editionMatches = await db.PublicationEditions
                        .Where(item => item.ProjectId == projectId && item.Name == imported.EditionName)
                        .Select(item => item.Id)
                        .Take(2)
                        .ToListAsync(cancellationToken);
                    if (editionMatches.Count != 1)
                        continue;
                    editionId = editionMatches[0];
                }
            }

            var chapterOverride = editionId is Guid targetEditionId
                ? await db.PublicationEditionChapterOverrides.AsNoTracking().SingleOrDefaultAsync(
                    item => item.EditionId == targetEditionId && item.ChapterId == chapterId,
                    cancellationToken)
                : null;
            var revision = chapterOverride?.Revision ?? chapter.ManuscriptRevision;
            var manuscript = chapterOverride is null
                ? ManuscriptCodec.Deserialize(chapter.ManuscriptJson, chapter.Id, chapter.ManuscriptRevision)
                : ManuscriptCodec.Deserialize(chapterOverride.ManuscriptJson, chapter.Id, chapterOverride.Revision);
            var range = new ManuscriptAnnotationRange(
                imported.StartBlockId,
                imported.StartOffset,
                imported.EndBlockId,
                imported.EndOffset);
            ManuscriptAnnotationAnchors.Resolved? resolved = null;
            try { resolved = ManuscriptAnnotationAnchors.TryResolveSelection(manuscript, range); }
            catch (InvalidDataException) { }
            var current = imported.AnchorState == ManuscriptAnnotationAnchorState.Current
                && resolved?.Quote == imported.OriginalQuote;
            db.ManuscriptAnnotations.Add(new ManuscriptAnnotation
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                ChapterId = chapterId,
                EditionId = editionId,
                Kind = imported.Kind,
                NoteText = imported.NoteText,
                Revision = Math.Max(1, imported.Revision),
                AnchorManuscriptRevision = revision,
                AnchorState = current
                    ? ManuscriptAnnotationAnchorState.Current
                    : ManuscriptAnnotationAnchorState.Outdated,
                StartBlockId = imported.StartBlockId,
                StartOffset = imported.StartOffset,
                EndBlockId = imported.EndBlockId,
                EndOffset = imported.EndOffset,
                OriginalQuote = imported.OriginalQuote,
                ContextBefore = imported.ContextBefore,
                ContextAfter = imported.ContextAfter,
                CreatedAt = imported.CreatedAt,
                UpdatedAt = imported.UpdatedAt,
            });
        }
        await operation.SaveChangesAsync(cancellationToken);
    }

    internal static void ValidateManuscriptAnnotationPayloads(ProjectExportDocument document)
    {
        if (document.FormatVersion < 24)
            return;
        var duplicate = document.ManuscriptAnnotations.GroupBy(item => item.Id).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Import file contains duplicate manuscript annotation '{duplicate.Key}'.");
        var chapterIds = document.Chapters.Select(item => item.Id).ToHashSet();
        var editionIds = document.PublicationEditions.Select(item => item.Id).ToHashSet();
        foreach (var annotation in document.ManuscriptAnnotations)
        {
            if (annotation.Id == Guid.Empty || annotation.ChapterId == Guid.Empty || annotation.Revision < 1
                || string.IsNullOrWhiteSpace(annotation.ChapterTitle)
                || annotation.EditionId is not null && string.IsNullOrWhiteSpace(annotation.EditionName))
                throw new InvalidOperationException("Import file contains an annotation with invalid identity metadata.");
            if (!Enum.IsDefined(annotation.Kind) || !Enum.IsDefined(annotation.AnchorState))
                throw new InvalidOperationException($"Manuscript annotation '{annotation.Id}' has an unsupported kind or anchor state.");
            if (annotation.NoteText is null || annotation.OriginalQuote is null
                || annotation.StartBlockId is null || annotation.EndBlockId is null
                || annotation.ContextBefore is null || annotation.ContextAfter is null)
                throw new InvalidOperationException($"Manuscript annotation '{annotation.Id}' has missing text fields.");
            if (annotation.NoteText.Length > ManuscriptAnnotationService.MaxNoteLength
                || annotation.Kind == ManuscriptAnnotationKind.Note && string.IsNullOrWhiteSpace(annotation.NoteText)
                || annotation.Kind == ManuscriptAnnotationKind.Highlight && annotation.NoteText.Length > 0)
                throw new InvalidOperationException($"Manuscript annotation '{annotation.Id}' has an invalid note body.");
            if (annotation.OriginalQuote.Length is 0 or > ManuscriptAnnotationService.MaxSelectionLength
                || annotation.ContextBefore.Length > 96 || annotation.ContextAfter.Length > 96)
                throw new InvalidOperationException($"Manuscript annotation '{annotation.Id}' exceeds review text limits.");
            if (document.ExportKind == ProjectExportKind.Full
                && (!chapterIds.Contains(annotation.ChapterId)
                    || annotation.EditionId is Guid editionId && !editionIds.Contains(editionId)))
                throw new InvalidOperationException($"Manuscript annotation '{annotation.Id}' references a missing chapter or edition.");
        }
    }

    internal static void ValidateIngestSourcePayloads(ProjectExportDocument document)
    {
        if (document.FormatVersion < 23)
            return;
        if (document.ExportKind == ProjectExportKind.NonStructural
            && (document.IngestSources.Count > 0 || document.BookBriefCanonSourceIds.Count > 0))
        {
            throw new InvalidOperationException("Non-structural exports cannot contain ingested source bodies or canonical-source selections.");
        }

        if (document.IngestSources.GroupBy(source => source.Id).Any(group => group.Count() > 1))
            throw new InvalidOperationException("Import file contains duplicate ingested source records.");
        var sourceIds = document.IngestSources.Select(source => source.Id).ToHashSet();
        if (document.BookBriefCanonSourceIds.Distinct().Count() != document.BookBriefCanonSourceIds.Count
            || document.BookBriefCanonSourceIds.Any(sourceId => !sourceIds.Contains(sourceId)))
        {
            throw new InvalidOperationException("Canonical-source selections must be unique and reference exported ingested sources.");
        }

        var childIds = document.IngestSources.SelectMany(source =>
                source.Chunks.Select(chunk => chunk.Id)
                    .Concat(source.Pages.Select(page => page.Id))
                    .Concat(source.Blocks.Select(block => block.Id)))
            .ToList();
        if (childIds.Distinct().Count() != childIds.Count)
            throw new InvalidOperationException("Import file contains duplicate ingested source child records.");
        foreach (var source in document.IngestSources)
        {
            var pageIds = source.Pages.Select(page => page.Id).ToHashSet();
            if (source.Blocks.Any(block => block.SourcePageId is { } pageId && !pageIds.Contains(pageId)))
                throw new InvalidOperationException($"Ingested source '{source.Title}' contains a block whose page was not exported.");
        }
    }

    private static ProjectExportDocument AdaptLegacySourceEvidence(ProjectExportDocument document)
    {
        if (document.FormatVersion >= 23)
            return document;

        static Dictionary<string, object?> Adapt(Dictionary<string, object?> properties)
        {
            var adapted = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in properties)
            {
                var key = property.Key switch
                {
                    "canonSourceMetaJson" => IngestWikiSheet.SourceEvidenceMetaProperty,
                    _ when property.Key.StartsWith("canonSource.", StringComparison.OrdinalIgnoreCase) =>
                        IngestWikiSheet.SourceEvidencePrefix + property.Key["canonSource.".Length..],
                    _ => property.Key,
                };
                adapted[key] = property.Value;
            }
            return adapted;
        }

        return document with
        {
            Nodes = document.Nodes.Select(node => node with { Properties = Adapt(node.Properties) }).ToList(),
            Edges = document.Edges.Select(edge => edge with { Properties = Adapt(edge.Properties) }).ToList(),
        };
    }

    private static ProjectExportNode RemapImportedNode(ProjectExportNode node, ImportState state)
    {
        var key = node.Key;
        if (Guid.TryParse(key, out var id))
        {
            var mapped = node.NodeType switch
            {
                EntityTypeService.SourceNodeType => state.IngestSourceMap.GetValueOrDefault(id),
                EntityTypeService.SourceChunkNodeType => state.IngestSourceChunkMap.GetValueOrDefault(id),
                EntityTypeService.SourceBlockNodeType => state.IngestSourceBlockMap.GetValueOrDefault(id),
                _ => Guid.Empty,
            };
            if (mapped != Guid.Empty)
                key = mapped.ToString("N");
        }

        return node with
        {
            Key = key,
            Properties = RemapSourceReferences(node.Properties, state),
        };
    }

    private static Dictionary<string, object?> RemapSourceReferences(
        Dictionary<string, object?> properties,
        ImportState state)
    {
        if (properties.Count == 0)
            return [];
        var json = JsonSerializer.Serialize(properties, JsonOptions);
        foreach (var mapping in state.IngestSourceMap
            .Concat(state.IngestSourceChunkMap)
            .Concat(state.IngestSourcePageMap)
            .Concat(state.IngestSourceBlockMap))
        {
            json = json.Replace(mapping.Key.ToString("N"), mapping.Value.ToString("N"), StringComparison.OrdinalIgnoreCase)
                .Replace(mapping.Key.ToString(), mapping.Value.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        return JsonSerializer.Deserialize<Dictionary<string, object?>>(json, JsonOptions) ?? [];
    }

    private static string StableKey(string nodeType, string key) =>
        $"{nodeType}/{key}";

    private static void AddContextEndpointIds(GraphNode from, GraphNode to, ICollection<Guid> target)
    {
        AddContextEntityId(from, target);
        AddContextEntityId(to, target);
    }

    private static void AddContextEntityId(GraphNode node, ICollection<Guid> target)
    {
        if (!Guid.TryParseExact(node.Key, "N", out var entityId)) return;
        if (string.Equals(node.NodeType, EntityTypeService.ProjectNodeType, StringComparison.OrdinalIgnoreCase)
            || string.Equals(node.NodeType, EntityTypeService.ActNodeType, StringComparison.OrdinalIgnoreCase)
            || string.Equals(node.NodeType, EntityTypeService.ChapterNodeType, StringComparison.OrdinalIgnoreCase)
            || string.Equals(node.NodeType, EntityTypeService.ProjectFactNodeType, StringComparison.OrdinalIgnoreCase)
            || string.Equals(node.NodeType, EntityTypeService.SourceNodeType, StringComparison.OrdinalIgnoreCase)
            || string.Equals(node.NodeType, EntityTypeService.SourceChunkNodeType, StringComparison.OrdinalIgnoreCase)
            || string.Equals(node.NodeType, EntityTypeService.SourceBlockNodeType, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        target.Add(entityId);
    }

    private void Notify(Guid projectId, Guid jobId, ProjectImportJobUpdateKind kind) =>
        notifier.Notify(new ProjectImportJobUpdate(projectId, jobId, kind, DateTime.UtcNow));
}
