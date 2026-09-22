using System.Text.Json;
using Lorekeeper.Citations;
using Lorekeeper.Ingest;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence.Legacy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Lorekeeper.Persistence;

public class AppDbContext(
    DbContextOptions<AppDbContext> options,
    ILogger<AppDbContext> logger) : DbContext(options)
{
    private const int _maxLockedSaveAttempts = 6;

    public DbSet<LlmProvider> LlmProviders => Set<LlmProvider>();
    public DbSet<OpenAiAccount> OpenAiAccounts => Set<OpenAiAccount>();
    public DbSet<EmbeddingConfiguration> EmbeddingConfigurations => Set<EmbeddingConfiguration>();
    public DbSet<SearchProvider> SearchProviders => Set<SearchProvider>();
    public DbSet<OAuthToken> OAuthTokens => Set<OAuthToken>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectReference> ProjectReferences => Set<ProjectReference>();
    public DbSet<ProjectVersionRepository> ProjectVersionRepositories => Set<ProjectVersionRepository>();
    public DbSet<ProjectVersionCheckpoint> ProjectVersionCheckpoints => Set<ProjectVersionCheckpoint>();
    public DbSet<ProjectVersionOperation> ProjectVersionOperations => Set<ProjectVersionOperation>();
    public DbSet<AuthoringSession> AuthoringSessions => Set<AuthoringSession>();
    public DbSet<AuthoringBatchReceipt> AuthoringBatchReceipts => Set<AuthoringBatchReceipt>();
    public DbSet<AuthoringTargetGeneration> AuthoringTargetGenerations => Set<AuthoringTargetGeneration>();
    public DbSet<GitHubConnection> GitHubConnections => Set<GitHubConnection>();
    public DbSet<ProjectGitRemote> ProjectGitRemotes => Set<ProjectGitRemote>();
    public DbSet<BookBrief> BookBriefs => Set<BookBrief>();
    public DbSet<BookBriefCanonSource> BookBriefCanonSources => Set<BookBriefCanonSource>();
    public DbSet<Act> Acts => Set<Act>();
    public DbSet<Chapter> Chapters => Set<Chapter>();
    public DbSet<ManuscriptAnnotation> ManuscriptAnnotations => Set<ManuscriptAnnotation>();
    public DbSet<ManuscriptMigrationJournal> ManuscriptMigrationJournals => Set<ManuscriptMigrationJournal>();
    public DbSet<GraphNode> GraphNodes => Set<GraphNode>();
    public DbSet<GraphEdge> GraphEdges => Set<GraphEdge>();
    public DbSet<GraphEntityType> GraphEntityTypes => Set<GraphEntityType>();
    public DbSet<OutlineConversation> OutlineConversations => Set<OutlineConversation>();
    public DbSet<OutlineMessage> OutlineMessages => Set<OutlineMessage>();
    public DbSet<EditorConversation> EditorConversations => Set<EditorConversation>();
    public DbSet<EditorMessage> EditorMessages => Set<EditorMessage>();
    public DbSet<EditorMessageVisual> EditorMessageVisuals => Set<EditorMessageVisual>();
    public DbSet<WritingSample> WritingSamples => Set<WritingSample>();
    public DbSet<VoiceConversation> VoiceConversations => Set<VoiceConversation>();
    public DbSet<VoiceMessage> VoiceMessages => Set<VoiceMessage>();
    public DbSet<ResearchConversation> ResearchConversations => Set<ResearchConversation>();
    public DbSet<ResearchMessage> ResearchMessages => Set<ResearchMessage>();
    public DbSet<PublishConversation> PublishConversations => Set<PublishConversation>();
    public DbSet<PublishMessage> PublishMessages => Set<PublishMessage>();
    public DbSet<PublishMessageVisual> PublishMessageVisuals => Set<PublishMessageVisual>();
    public DbSet<ProjectImageConversation> ProjectImageConversations => Set<ProjectImageConversation>();
    public DbSet<ProjectImageMessage> ProjectImageMessages => Set<ProjectImageMessage>();
    public DbSet<ProjectImageMessageVisual> ProjectImageMessageVisuals => Set<ProjectImageMessageVisual>();
    public DbSet<ChatMessageImageAttachment> ChatMessageImageAttachments => Set<ChatMessageImageAttachment>();
    public DbSet<ProjectImageChatAttachment> ProjectImageChatAttachments => Set<ProjectImageChatAttachment>();
    public DbSet<ContestBatch> ContestBatches => Set<ContestBatch>();
    public DbSet<ContestCandidate> ContestCandidates => Set<ContestCandidate>();
    public DbSet<EditorRevisionJob> EditorRevisionJobs => Set<EditorRevisionJob>();
    public DbSet<EditorRevisionSession> EditorRevisionSessions => Set<EditorRevisionSession>();
    public DbSet<EditorRevisionMessage> EditorRevisionMessages => Set<EditorRevisionMessage>();
    public DbSet<EditorContextPreference> EditorContextPreferences => Set<EditorContextPreference>();
    public DbSet<IngestSource> IngestSources => Set<IngestSource>();
    public DbSet<IngestSourceChunk> IngestSourceChunks => Set<IngestSourceChunk>();
    public DbSet<IngestSourcePage> IngestSourcePages => Set<IngestSourcePage>();
    public DbSet<IngestSourceBlock> IngestSourceBlocks => Set<IngestSourceBlock>();
    public DbSet<IngestVectorFragment> IngestVectorFragments => Set<IngestVectorFragment>();
    public DbSet<IngestJob> IngestJobs => Set<IngestJob>();
    public DbSet<IngestJobChunk> IngestJobChunks => Set<IngestJobChunk>();
    public DbSet<IngestReportItem> IngestReportItems => Set<IngestReportItem>();
    public DbSet<IngestStagingRecord> IngestStagingRecords => Set<IngestStagingRecord>();
    public DbSet<IngestJobEvent> IngestJobEvents => Set<IngestJobEvent>();
    public DbSet<SourceOriginal> SourceOriginals => Set<SourceOriginal>();
    public DbSet<SourceOriginalBlob> SourceOriginalBlobs => Set<SourceOriginalBlob>();
    public DbSet<SourceOriginalChunk> SourceOriginalChunks => Set<SourceOriginalChunk>();
    public DbSet<SourceExtractionVersion> SourceExtractionVersions => Set<SourceExtractionVersion>();
    public DbSet<SourceLocation> SourceLocations => Set<SourceLocation>();
    public DbSet<BibliographicRecord> BibliographicRecords => Set<BibliographicRecord>();
    public DbSet<WebIngestCandidate> WebIngestCandidates => Set<WebIngestCandidate>();
    public DbSet<ProjectImportJob> ProjectImportJobs => Set<ProjectImportJob>();
    public DbSet<ProjectImportReportItem> ProjectImportReportItems => Set<ProjectImportReportItem>();
    public DbSet<PublicationEdition> PublicationEditions => Set<PublicationEdition>();
    public DbSet<PublicationEditionChapterOverride> PublicationEditionChapterOverrides => Set<PublicationEditionChapterOverride>();
    public DbSet<PublicationBook> PublicationBooks => Set<PublicationBook>();
    public DbSet<PublicationBookPdfPresentation> PublicationBookPdfPresentations => Set<PublicationBookPdfPresentation>();
    public DbSet<PublicationBookOutlineItem> PublicationBookOutlineItems => Set<PublicationBookOutlineItem>();
    public DbSet<PublicationBookMatter> PublicationBookMatter => Set<PublicationBookMatter>();
    public DbSet<PublicationBookImagePlacement> PublicationBookImagePlacements => Set<PublicationBookImagePlacement>();
    public DbSet<PublicationSection> PublicationSections => Set<PublicationSection>();
    public DbSet<PublicationBookCoverDesign> PublicationBookCoverDesigns => Set<PublicationBookCoverDesign>();
    public DbSet<PublicationEditionOutlineItem> PublicationEditionOutlineItems => Set<PublicationEditionOutlineItem>();
    public DbSet<PublicationMatter> PublicationMatter => Set<PublicationMatter>();
    public DbSet<PublicationImagePlacement> PublicationImagePlacements => Set<PublicationImagePlacement>();
    public DbSet<PublicationEditionAuditEntry> PublicationEditionAuditEntries => Set<PublicationEditionAuditEntry>();
    public DbSet<PublicationEditionMigrationJournal> PublicationEditionMigrationJournals => Set<PublicationEditionMigrationJournal>();
    public DbSet<PublicationRenderJob> PublicationRenderJobs => Set<PublicationRenderJob>();
    public DbSet<PublicationInteriorPagination> PublicationInteriorPaginations => Set<PublicationInteriorPagination>();
    public DbSet<PublicationArtifact> PublicationArtifacts => Set<PublicationArtifact>();
    public DbSet<PublicationPageMapEntry> PublicationPageMapEntries => Set<PublicationPageMapEntry>();
    public DbSet<PublicationCoverDesign> PublicationCoverDesigns => Set<PublicationCoverDesign>();
    public DbSet<PublicationPreparationJob> PublicationPreparationJobs => Set<PublicationPreparationJob>();
    public DbSet<PublishAsset> PublishAssets => Set<PublishAsset>();
    public DbSet<ProjectImageGenerationJob> ProjectImageGenerationJobs => Set<ProjectImageGenerationJob>();
    public DbSet<ProjectImagePartial> ProjectImagePartials => Set<ProjectImagePartial>();
    public DbSet<ProjectImageMask> ProjectImageMasks => Set<ProjectImageMask>();
    public DbSet<EntityVisualExample> EntityVisualExamples => Set<EntityVisualExample>();
    public DbSet<SourceVisualCandidate> SourceVisualCandidates => Set<SourceVisualCandidate>();
    public DbSet<ProjectFontFamily> ProjectFontFamilies => Set<ProjectFontFamily>();
    public DbSet<ProjectFontFace> ProjectFontFaces => Set<ProjectFontFace>();
    public DbSet<ManuscriptStyleDefinition> ManuscriptStyleDefinitions => Set<ManuscriptStyleDefinition>();
    public DbSet<ProjectPageSetup> ProjectPageSetups => Set<ProjectPageSetup>();
    public DbSet<DesignedPage> DesignedPages => Set<DesignedPage>();
    public DbSet<DesignedPageContent> DesignedPageContents => Set<DesignedPageContent>();
    public DbSet<DesignedPageVariant> DesignedPageVariants => Set<DesignedPageVariant>();
    public DbSet<DesignedPagePlacementReference> DesignedPagePlacementReferences => Set<DesignedPagePlacementReference>();
    internal DbSet<LegacyPageComposition> LegacyPageCompositions => Set<LegacyPageComposition>();
    internal DbSet<LegacyPageCompositionVariant> LegacyPageCompositionVariants => Set<LegacyPageCompositionVariant>();
    public DbSet<CompositionMutationStage> CompositionMutationStages => Set<CompositionMutationStage>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);

    internal void MarkModified<TEntity>(TEntity entity)
        where TEntity : class
    {
        var entry = Entry(entity);
        if (entry.State == EntityState.Detached)
            entry.State = EntityState.Modified;
    }

    internal void MarkDeleted<TEntity>(TEntity entity)
        where TEntity : class
    {
        Entry(entity).State = EntityState.Deleted;
    }

    public override int SaveChanges() => SaveChanges(acceptAllChangesOnSuccess: true);

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        NormalizePublicationTargetOwnership();
        ValidateSourceRetentionMutations();
        try
        {
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            LogConcurrencyConflict(exception);
            throw new DbUpdateConcurrencyException(
                "The data changed while this operation was being saved. Nothing was overwritten; reload the current state and try again.",
                exception);
        }
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        SaveChangesWithLockRetryAsync(acceptAllChangesOnSuccess: true, cancellationToken);

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default) =>
        SaveChangesWithLockRetryAsync(acceptAllChangesOnSuccess, cancellationToken);

    private async Task<int> SaveChangesWithLockRetryAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken)
    {
        NormalizePublicationTargetOwnership();
        ValidateSourceRetentionMutations();
        var delay = TimeSpan.FromMilliseconds(100);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
                }
                catch (DbUpdateException ex) when (IsSqliteLocked(ex) && attempt < _maxLockedSaveAttempts && !cancellationToken.IsCancellationRequested)
                {
                    var retryDelay = delay + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 75));
                    logger.LogWarning(
                        ex,
                        "SQLite database was locked during SaveChanges; retrying attempt {Attempt}/{MaxAttempts} after {DelayMs} ms.",
                        attempt,
                        _maxLockedSaveAttempts,
                        retryDelay.TotalMilliseconds);
                    await Task.Delay(retryDelay, cancellationToken);
                    delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 2_000));
                }
            }
        }
        catch (DbUpdateConcurrencyException exception)
        {
            LogConcurrencyConflict(exception);
            throw new DbUpdateConcurrencyException(
                "The data changed while this operation was being saved. Nothing was overwritten; reload the current state and try again.",
                exception);
        }
    }

    private void ValidateSourceRetentionMutations()
    {
        ValidateNewSourceRetentionRows();
        ValidateTrackedActiveExtractions();
        foreach (var entry in ChangeTracker.Entries<SourceOriginal>())
        {
            if (entry.State == EntityState.Modified)
                throw new InvalidOperationException("Retained source originals are immutable.");
        }
        foreach (var entry in ChangeTracker.Entries<SourceOriginalBlob>())
        {
            if (entry.State == EntityState.Modified)
                throw new InvalidOperationException("Content-addressed source blobs are immutable.");
        }
        foreach (var entry in ChangeTracker.Entries<SourceOriginalChunk>())
        {
            if (entry.State == EntityState.Modified)
                throw new InvalidOperationException("Retained source chunk references are immutable.");
        }
        foreach (var entry in ChangeTracker.Entries<SourceExtractionVersion>())
        {
            if (entry.State != EntityState.Modified) continue;
            if (entry.OriginalValues.GetValue<SourceExtractionStatus>(nameof(SourceExtractionVersion.Status)) != SourceExtractionStatus.Extracting
                || entry.Property(nameof(SourceExtractionVersion.SourceId)).IsModified
                || entry.Property(nameof(SourceExtractionVersion.Ordinal)).IsModified
                || entry.Property(nameof(SourceExtractionVersion.Extractor)).IsModified
                || entry.Property(nameof(SourceExtractionVersion.ExtractorVersion)).IsModified
                || entry.Property(nameof(SourceExtractionVersion.OptionsJson)).IsModified
                || entry.Property(nameof(SourceExtractionVersion.ContentHash)).IsModified
                || entry.Property(nameof(SourceExtractionVersion.NormalizedText)).IsModified)
            {
                throw new InvalidOperationException("Completed source extraction versions are immutable; create a new extraction instead.");
            }
        }

        RejectExtractionContentMutation<IngestSourceChunk>("SourceExtractionVersionId", "SourceId", "StartChar", "EndChar");
        RejectExtractionContentMutation<IngestSourcePage>("SourceExtractionVersionId", "SourceId", "PageNumber", "Text", "StartChar", "EndChar", "ExtractionMethod");
        RejectExtractionContentMutation<IngestSourceBlock>("SourceExtractionVersionId", "SourceId", "SourcePageId", "Index", "StartChar", "EndChar", "NormalizedText", "ContentHash");
        RejectExtractionContentMutation<SourceLocation>("ProjectId", "SourceId", "ExtractionVersionId", "SourceBlockId", "PageNumber", "NormalizedStart", "NormalizedLength", "Locator", "Quote", "VerificationHash");
    }

    private void ValidateNewSourceRetentionRows()
    {
        foreach (var entry in ChangeTracker.Entries<SourceOriginal>().Where(entry => entry.State == EntityState.Added))
            SourceRetentionValidator.ValidateOriginalForPersistence(entry.Entity);

        foreach (var entry in ChangeTracker.Entries<SourceOriginalBlob>().Where(entry => entry.State == EntityState.Added))
        {
            var blob = entry.Entity;
            if (blob.Length != blob.Data.Length
                || !string.Equals(blob.Sha256, SourceRetentionValidator.Sha256(blob.Data), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A retained source blob does not match its content address.");
            }
        }

        foreach (var entry in ChangeTracker.Entries<SourceExtractionVersion>().Where(entry => entry.State == EntityState.Added))
        {
            var extraction = entry.Entity;
            if (extraction.Ordinal < 0
                || string.IsNullOrWhiteSpace(extraction.Extractor)
                || string.IsNullOrWhiteSpace(extraction.ExtractorVersion)
                || string.IsNullOrWhiteSpace(extraction.ContentHash))
            {
                throw new InvalidOperationException("A source extraction version is incomplete.");
            }
        }

        foreach (var entry in ChangeTracker.Entries<IngestSourceBlock>().Where(entry => entry.State == EntityState.Added))
        {
            var block = entry.Entity;
            if (block.NormalizedText.Length > IngestSourceBlock.MaximumNormalizedTextLength
                || block.StartChar < 0
                || block.EndChar < block.StartChar)
            {
                throw new InvalidOperationException("A retained source block is outside the supported bounds.");
            }
        }
    }

    private void ValidateTrackedActiveExtractions()
    {
        var trackedExtractions = ChangeTracker.Entries<SourceExtractionVersion>()
            .Where(entry => entry.State is not EntityState.Detached and not EntityState.Deleted)
            .Select(entry => entry.Entity)
            .ToDictionary(extraction => extraction.Id);
        foreach (var source in ChangeTracker.Entries<IngestSource>()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified)
            .Select(entry => entry.Entity))
        {
            if (source.ActiveExtractionVersionId is not Guid activeExtractionId
                || !trackedExtractions.TryGetValue(activeExtractionId, out var extraction))
            {
                continue;
            }
            if (extraction.SourceId != source.Id
                || extraction.Status is not (SourceExtractionStatus.Ready or SourceExtractionStatus.LegacyImmutable))
            {
                throw new InvalidOperationException("An active source extraction must be a ready extraction owned by that source.");
            }
        }
    }

    private void RejectExtractionContentMutation<TEntity>(params string[] propertyNames)
        where TEntity : class
    {
        foreach (var entry in ChangeTracker.Entries<TEntity>().Where(entry => entry.State == EntityState.Modified))
        {
            if (propertyNames.Any(propertyName => entry.Property(propertyName).IsModified))
                throw new InvalidOperationException("Completed source extraction content is immutable; create a new extraction instead.");
        }
    }

    private void LogConcurrencyConflict(DbUpdateConcurrencyException exception)
    {
        var entries = exception.Entries
            .Select(entry => $"{entry.Metadata.ClrType.Name} ({string.Join(", ", entry.Properties
                .Where(property => property.Metadata.IsPrimaryKey() || property.Metadata.IsConcurrencyToken)
                .Select(property => $"{property.Metadata.Name}={property.OriginalValue}"))})")
            .ToArray();
        logger.LogWarning(
            exception,
            "Optimistic database concurrency conflict while saving {Entries}.",
            entries.Length == 0 ? "an unidentified tracked entity" : string.Join("; ", entries));
    }

    private static bool IsSqliteLocked(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException { SqliteErrorCode: 5 or 6 })
                return true;
        }

        return false;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var jsonDictConverter = new ValueConverter<Dictionary<string, object?>, string>(
            v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
            v => JsonSerializer.Deserialize<Dictionary<string, object?>>(v, JsonSerializerOptions.Default) ?? new Dictionary<string, object?>());

        var jsonDictComparer = new ValueComparer<Dictionary<string, object?>>(
            (a, b) => JsonSerializer.Serialize(a, JsonSerializerOptions.Default) == JsonSerializer.Serialize(b, JsonSerializerOptions.Default),
            v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default).GetHashCode(),
            v => JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(v, JsonSerializerOptions.Default), JsonSerializerOptions.Default) ?? new Dictionary<string, object?>());

        modelBuilder.Entity<ManuscriptMigrationJournal>(entity =>
        {
            entity.HasIndex(journal => new { journal.MigrationName, journal.StartedAt });
            entity.Property(journal => journal.Phase).HasConversion<string>();
            entity.Property(journal => journal.Status).HasConversion<string>();
        });

        modelBuilder.Entity<Project>(entity =>
        {
            entity.HasIndex(e => e.Slug).IsUnique();
        });

        modelBuilder.Entity<ProjectVersionRepository>(entity =>
        {
            entity.HasIndex(e => e.ProjectId).IsUnique();
            entity.HasIndex(e => new { e.HeadCommitSha, e.HeadContentHash });
        });

        modelBuilder.Entity<Project>()
            .HasOne(e => e.VersionHistoryRepository)
            .WithOne(e => e.Project)
            .HasForeignKey<ProjectVersionRepository>(e => e.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ProjectVersionCheckpoint>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectVersionRepositoryId, e.CreatedAt });
            entity.HasIndex(e => new { e.ProjectVersionRepositoryId, e.ContentHash });
            entity.Property(e => e.Kind).HasConversion<string>();
            entity.Property(e => e.Source).HasConversion<string>();
            entity.HasOne(e => e.Repository)
                .WithMany(e => e.Checkpoints)
                .HasForeignKey(e => e.ProjectVersionRepositoryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProjectVersionOperation>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectVersionRepositoryId, e.CreatedAt });
            entity.HasIndex(e => new { e.ProjectVersionRepositoryId, e.Status, e.UpdatedAt });
            entity.HasIndex(e => new { e.ProjectVersionRepositoryId, e.RequestKey }).IsUnique();
            entity.HasIndex(e => new { e.ProjectGitRemoteId, e.TargetCommitSha }).IsUnique();
            entity.Property(e => e.Kind).HasConversion<string>();
            entity.Property(e => e.Status).HasConversion<string>();
            entity.HasOne(e => e.Repository)
                .WithMany(e => e.Operations)
                .HasForeignKey(e => e.ProjectVersionRepositoryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GitHubConnection>(entity =>
        {
            entity.HasIndex(e => e.GitHubUserId).IsUnique();
        });

        modelBuilder.Entity<ProjectGitRemote>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectVersionRepositoryId, e.RemoteName }).IsUnique();
            entity.HasIndex(e => new { e.ProjectVersionRepositoryId, e.Owner, e.RepositoryName }).IsUnique();
            entity.HasOne(e => e.Repository)
                .WithMany(e => e.GitRemotes)
                .HasForeignKey(e => e.ProjectVersionRepositoryId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.GitHubConnection)
                .WithMany(e => e.GitRemotes)
                .HasForeignKey(e => e.GitHubConnectionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ProjectReference>(entity =>
        {
            entity.HasKey(reference => reference.Id);
            entity.HasIndex(reference => new
            {
                reference.ReferencingProjectId,
                reference.ReferencedRepositoryId,
                reference.ReferencedProjectId,
            }).IsUnique();
            entity.HasIndex(reference => reference.ResolvedProjectId);
            entity.Property(reference => reference.ReferencedProjectName).IsRequired();
            entity.Property(reference => reference.ReferencedProjectSlug).IsRequired();
            entity.ToTable(table => table.HasCheckConstraint(
                "CK_ProjectReferences_ResolvedNotSelf",
                "\"ResolvedProjectId\" IS NULL OR \"ReferencingProjectId\" <> \"ResolvedProjectId\""));

            entity.HasOne(reference => reference.ReferencingProject)
                .WithMany(project => project.OutgoingReferences)
                .HasForeignKey(reference => reference.ReferencingProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(reference => reference.ResolvedProject)
                .WithMany(project => project.ResolvedIncomingReferences)
                .HasForeignKey(reference => reference.ResolvedProjectId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<BookBrief>(entity =>
        {
            entity.HasIndex(e => e.ProjectId).IsUnique();
            entity.Property(e => e.BookKind).HasConversion<string>();

            entity.HasOne(e => e.Project)
                .WithOne(p => p.BookBrief)
                .HasForeignKey<BookBrief>(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BookBriefCanonSource>(entity =>
        {
            entity.HasKey(e => new { e.BookBriefId, e.IngestSourceId });
            entity.HasIndex(e => e.IngestSourceId);

            entity.HasOne(e => e.BookBrief)
                .WithMany(e => e.CanonSources)
                .HasForeignKey(e => e.BookBriefId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.IngestSource)
                .WithMany(e => e.BookBriefCanonSelections)
                .HasForeignKey(e => e.IngestSourceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProjectPageSetup>(entity =>
        {
            entity.HasKey(e => e.ProjectId);
            entity.Property(e => e.Revision).IsConcurrencyToken();
            entity.HasOne(e => e.Project)
                .WithOne(e => e.PageSetup)
                .HasForeignKey<ProjectPageSetup>(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Act>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectId, e.Order });

            entity.HasOne(e => e.Project)
                .WithMany(p => p.Acts)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Chapter>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectId, e.Order });
            entity.HasIndex(e => new { e.ActId, e.Order });
            entity.Property(e => e.VectorIndexState).HasConversion<string>();
            entity.Property(e => e.ManuscriptRevision).IsConcurrencyToken();

            entity.HasOne(e => e.Project)
                .WithMany(p => p.Chapters)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Act)
                .WithMany(a => a.Chapters)
                .HasForeignKey(e => e.ActId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ManuscriptAnnotation>(entity =>
        {
            entity.HasIndex(item => new { item.ProjectId, item.EditionId, item.ChapterId });
            entity.HasIndex(item => new { item.ChapterId, item.AnchorState, item.CreatedAt });
            entity.Property(item => item.Kind).HasConversion<string>();
            entity.Property(item => item.AnchorState).HasConversion<string>();
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.Property(item => item.NoteText).HasMaxLength(ManuscriptAnnotationService.MaxNoteLength);
            entity.Property(item => item.OriginalQuote).HasMaxLength(ManuscriptAnnotationService.MaxSelectionLength);
            entity.Property(item => item.ContextBefore).HasMaxLength(96);
            entity.Property(item => item.ContextAfter).HasMaxLength(96);

            entity.HasOne(item => item.Project)
                .WithMany(project => project.ManuscriptAnnotations)
                .HasForeignKey(item => item.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(item => item.Chapter)
                .WithMany(chapter => chapter.ManuscriptAnnotations)
                .HasForeignKey(item => item.ChapterId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(item => item.Edition)
                .WithMany(edition => edition.ManuscriptAnnotations)
                .HasForeignKey(item => item.EditionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OutlineConversation>(entity =>
        {
            entity.HasIndex(e => e.ProjectId).IsUnique();
            entity.Property(e => e.SelectedProviderId);

            entity.HasOne(e => e.Project)
                .WithMany(p => p.OutlineConversations)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OutlineMessage>(entity =>
        {
            entity.HasIndex(e => new { e.ConversationId, e.Order });
            entity.Property(e => e.Role).HasConversion<string>();
            entity.Property(e => e.Status).HasConversion<string>();

            entity.HasOne(e => e.Conversation)
                .WithMany(c => c.Messages)
                .HasForeignKey(e => e.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EditorConversation>(entity =>
        {
            entity.HasIndex(e => e.ProjectId).IsUnique();
            entity.Property(e => e.SelectedProviderId);

            entity.HasOne(e => e.Project)
                .WithMany(p => p.EditorConversations)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EditorMessage>(entity =>
        {
            entity.HasIndex(e => new { e.ConversationId, e.Order });
            entity.Property(e => e.Role).HasConversion<string>();
            entity.Property(e => e.Status).HasConversion<string>();

            entity.HasOne(e => e.Conversation)
                .WithMany(c => c.Messages)
                .HasForeignKey(e => e.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DesignedPage>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectId, e.ScopeEditionId, e.UpdatedAt });
            entity.HasOne(e => e.Project)
                .WithMany(e => e.DesignedPages)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.ScopeEdition)
                .WithMany(e => e.ScopedDesignedPages)
                .HasForeignKey(e => e.ScopeEditionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuthoringSession>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.ProjectId, item.UpdatedAt });
            entity.HasOne<Project>().WithMany().HasForeignKey(item => item.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuthoringBatchReceipt>(entity =>
        {
            entity.HasIndex(item => item.BatchId).IsUnique();
            entity.HasIndex(item => new { item.SessionId, item.Sequence }).IsUnique();
            entity.HasIndex(item => new { item.ProjectId, item.AcknowledgedAt, item.CreatedAt });
            entity.HasOne<Project>().WithMany().HasForeignKey(item => item.ProjectId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<AuthoringSession>().WithMany().HasForeignKey(item => item.SessionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuthoringTargetGeneration>(entity =>
        {
            entity.HasIndex(item => new { item.ProjectId, item.TargetId }).IsUnique();
            entity.HasOne<Project>().WithMany().HasForeignKey(item => item.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DesignedPageContent>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectId, e.EditionId, e.UpdatedAt });
            entity.HasIndex(e => e.ActiveVariantId);
            entity.HasIndex(e => e.DesignedPageId)
                .IsUnique()
                .HasFilter("\"EditionId\" IS NULL");
            entity.HasIndex(e => new { e.DesignedPageId, e.EditionId })
                .IsUnique()
                .HasFilter("\"EditionId\" IS NOT NULL");
            entity.Property(e => e.Revision).IsConcurrencyToken();
            entity.HasOne(e => e.Project)
                .WithMany(e => e.DesignedPageContents)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Page)
                .WithMany(e => e.Contents)
                .HasForeignKey(e => e.DesignedPageId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Edition)
                .WithMany(e => e.DesignedPageContents)
                .HasForeignKey(e => e.EditionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DesignedPageVariant>(entity =>
        {
            entity.HasIndex(e => new { e.ContentId, e.GeometryKey }).IsUnique();
            entity.Property(e => e.Revision).IsConcurrencyToken();
            entity.HasOne(e => e.Content)
                .WithMany(e => e.Variants)
                .HasForeignKey(e => e.ContentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DesignedPagePlacementReference>(entity =>
        {
            entity.HasKey(e => e.ReferenceId);
            entity.Property(e => e.Id).HasMaxLength(64);
            entity.Property(e => e.ContainerKind).HasConversion<string>();
            entity.HasIndex(e => new { e.ProjectId, e.DesignedPageId });
            entity.HasIndex(e => new { e.ProjectId, e.ContainerKind, e.ContainerId, e.EditionId });
            entity.HasIndex(e => new { e.ProjectId, e.ContainerKind, e.ContainerId, e.Id })
                .IsUnique()
                .HasFilter("\"EditionId\" IS NULL");
            entity.HasIndex(e => new { e.ProjectId, e.ContainerKind, e.ContainerId, e.EditionId, e.Id })
                .IsUnique()
                .HasFilter("\"EditionId\" IS NOT NULL");
            entity.HasOne(e => e.Project)
                .WithMany(e => e.DesignedPagePlacementReferences)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Page)
                .WithMany(e => e.PlacementReferences)
                .HasForeignKey(e => e.DesignedPageId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Edition)
                .WithMany(e => e.DesignedPagePlacementReferences)
                .HasForeignKey(e => e.EditionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LegacyPageComposition>(entity =>
        {
            entity.ToTable("PageCompositions", table => table.ExcludeFromMigrations());
            entity.HasKey(item => item.Id);
        });

        modelBuilder.Entity<LegacyPageCompositionVariant>(entity =>
        {
            entity.ToTable("PageCompositionVariants", table => table.ExcludeFromMigrations());
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.CompositionId, item.GeometryKey }).IsUnique();
            entity.HasOne(item => item.Composition)
                .WithMany(item => item.Variants)
                .HasForeignKey(item => item.CompositionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CompositionMutationStage>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectId, e.ConversationId, e.ExpiresAt });
            entity.HasIndex(e => e.PayloadSha256);
            entity.HasOne(e => e.Project)
                .WithMany(e => e.CompositionMutationStages)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EditorMessageVisual>(entity =>
        {
            entity.HasIndex(e => new { e.MessageId, e.SortOrder });
            entity.HasIndex(e => new { e.ToolCallId, e.CreatedAt });

            entity.HasOne(e => e.Message)
                .WithMany(m => m.Visuals)
                .HasForeignKey(e => e.MessageId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WritingSample>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectId, e.UpdatedAt });
            entity.HasIndex(e => new { e.ProjectId, e.Title });

            entity.HasOne(e => e.Project)
                .WithMany(p => p.WritingSamples)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<VoiceConversation>(entity =>
        {
            entity.HasIndex(e => e.ProjectId).IsUnique();
            entity.Property(e => e.SelectedProviderId);

            entity.HasOne(e => e.Project)
                .WithMany(p => p.VoiceConversations)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<VoiceMessage>(entity =>
        {
            entity.HasIndex(e => new { e.ConversationId, e.Order });
            entity.Property(e => e.Role).HasConversion<string>();
            entity.Property(e => e.Status).HasConversion<string>();

            entity.HasOne(e => e.Conversation)
                .WithMany(c => c.Messages)
                .HasForeignKey(e => e.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ResearchConversation>(entity =>
        {
            entity.HasIndex(e => e.ProjectId).IsUnique();
            entity.Property(e => e.SelectedProviderId);

            entity.HasOne(e => e.Project)
                .WithMany(p => p.ResearchConversations)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ResearchMessage>(entity =>
        {
            entity.HasIndex(e => new { e.ConversationId, e.Order });
            entity.Property(e => e.Role).HasConversion<string>();
            entity.Property(e => e.Status).HasConversion<string>();

            entity.HasOne(e => e.Conversation)
                .WithMany(c => c.Messages)
                .HasForeignKey(e => e.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PublishConversation>(entity =>
        {
            entity.HasIndex(e => e.ProjectId).IsUnique();
            entity.Property(e => e.SelectedProviderId);

            entity.HasOne(e => e.Project)
                .WithMany(p => p.PublishConversations)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PublishMessage>(entity =>
        {
            entity.HasIndex(e => new { e.ConversationId, e.Order });
            entity.Property(e => e.Role).HasConversion<string>();
            entity.Property(e => e.Status).HasConversion<string>();

            entity.HasOne(e => e.Conversation)
                .WithMany(c => c.Messages)
                .HasForeignKey(e => e.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PublishMessageVisual>(entity =>
        {
            entity.HasIndex(e => new { e.MessageId, e.SortOrder });
            entity.HasIndex(e => new { e.ToolCallId, e.CreatedAt });

            entity.HasOne(e => e.Message)
                .WithMany(m => m.Visuals)
                .HasForeignKey(e => e.MessageId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProjectImageConversation>(entity =>
        {
            entity.HasIndex(e => e.ProjectId).IsUnique();
            entity.Property(e => e.SelectedProviderId);

            entity.HasOne(e => e.Project)
                .WithMany(p => p.ProjectImageConversations)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProjectImageMessage>(entity =>
        {
            entity.HasIndex(e => new { e.ConversationId, e.Order });
            entity.Property(e => e.Role).HasConversion<string>();
            entity.Property(e => e.Status).HasConversion<string>();

            entity.HasOne(e => e.Conversation)
                .WithMany(c => c.Messages)
                .HasForeignKey(e => e.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProjectImageMessageVisual>(entity =>
        {
            entity.HasIndex(e => new { e.MessageId, e.SortOrder });
            entity.HasIndex(e => new { e.ToolCallId, e.CreatedAt });

            entity.HasOne(e => e.Message)
                .WithMany(m => m.Visuals)
                .HasForeignKey(e => e.MessageId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ChatMessageImageAttachment>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectId, e.Surface, e.MessageId, e.SortOrder });
            entity.HasIndex(e => new { e.Surface, e.MessageId, e.ImageId }).IsUnique();
            entity.Property(e => e.Surface).HasConversion<string>();

            entity.HasOne(e => e.Image)
                .WithMany()
                .HasForeignKey(e => e.ImageId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProjectImageChatAttachment>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectId, e.SortOrder });
            entity.HasIndex(e => new { e.ProjectId, e.ImageId }).IsUnique();

            entity.HasOne(e => e.Project)
                .WithMany(p => p.ProjectImageChatAttachments)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Image)
                .WithMany(a => a.ImageChatAttachments)
                .HasForeignKey(e => e.ImageId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ContestBatch>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectId, e.Status, e.CreatedAt });
            entity.HasIndex(e => e.ProjectId)
                .HasDatabaseName("IX_ContestBatches_ProjectId_Unresolved")
                .IsUnique()
                .HasFilter("\"Status\" IN ('Running', 'Completed', 'Failed')");
            entity.HasIndex(e => new { e.ConversationId, e.CreatedAt });
            entity.Property(e => e.Status).HasConversion<string>();

            entity.HasOne(e => e.Project)
                .WithMany(p => p.ContestBatches)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ContestCandidate>(entity =>
        {
            entity.HasIndex(e => new { e.BatchId, e.Order });
            entity.Property(e => e.Status).HasConversion<string>();

            entity.HasOne(e => e.Batch)
                .WithMany(b => b.Candidates)
                .HasForeignKey(e => e.BatchId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EditorRevisionJob>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectId, e.Status, e.CreatedAt });
            entity.HasIndex(e => new { e.ConversationId, e.CreatedAt });
            entity.Property(e => e.Status).HasConversion<string>();

            entity.HasOne(e => e.Project)
                .WithMany(p => p.EditorRevisionJobs)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EditorRevisionSession>(entity =>
        {
            entity.HasIndex(e => new { e.JobId, e.Order });
            entity.HasIndex(e => new { e.ChapterId, e.CreatedAt });
            entity.Property(e => e.Status).HasConversion<string>();

            entity.HasOne(e => e.Job)
                .WithMany(j => j.Sessions)
                .HasForeignKey(e => e.JobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EditorRevisionMessage>(entity =>
        {
            entity.HasIndex(e => new { e.SessionId, e.Order });
            entity.Property(e => e.Role).HasConversion<string>();
            entity.Property(e => e.Status).HasConversion<string>();

            entity.HasOne(e => e.Session)
                .WithMany(s => s.Messages)
                .HasForeignKey(e => e.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EditorContextPreference>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectId, e.ChapterId, e.Kind, e.Key }).IsUnique();
            entity.HasIndex(e => new { e.ProjectId, e.ChapterId, e.SortOrder });

            entity.HasOne(e => e.Project)
                .WithMany(p => p.EditorContextPreferences)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Chapter)
                .WithMany(c => c.EditorContextPreferences)
                .HasForeignKey(e => e.ChapterId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LlmProvider>(entity =>
        {
            entity.HasIndex(e => e.Name).IsUnique();
            entity.Property(e => e.AuthType).HasConversion<string>();
            entity.Property(e => e.ReasoningEffort).HasConversion<string>();
            entity.Property(e => e.MaxTokensField).HasConversion<string>();
            entity.Property(e => e.ModelOrigin)
                .HasConversion<string>()
                .HasDefaultValue(LlmModelOrigin.Manual);
            entity.Property(e => e.LastChatTestAuthType).HasConversion<string>();
            entity.Property(e => e.LastChatTestReasoningEffort).HasConversion<string>();
            entity.Property(e => e.LastVisionTestAuthType).HasConversion<string>();
            entity.Property(e => e.LastVisionTestReasoningEffort).HasConversion<string>();

            entity.HasOne(e => e.CredentialSource)
                .WithMany(e => e.ChildModels)
                .HasForeignKey(e => e.CredentialSourceId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.OpenAiAccount)
                .WithMany(e => e.Models)
                .HasForeignKey(e => e.OpenAiAccountId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OpenAiAccount>(entity =>
        {
            entity.HasIndex(e => e.DisplayName);
        });

        modelBuilder.Entity<EmbeddingConfiguration>(entity =>
        {
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.ApiKind).HasConversion<string>();
            entity.Property(e => e.LastTestedApiKind).HasConversion<string>();

            entity.HasOne(e => e.Provider)
                .WithMany()
                .HasForeignKey(e => e.ProviderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SearchProvider>(entity =>
        {
            entity.HasIndex(e => e.Name).IsUnique();
            entity.HasIndex(e => e.IsActive);
            entity.Property(e => e.ProviderKind).HasConversion<string>();
        });

        modelBuilder.Entity<OAuthToken>(entity =>
        {
            entity.HasOne(e => e.OpenAiAccount)
                .WithMany(p => p.OAuthTokens)
                .HasForeignKey(e => e.OpenAiAccountId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.OpenAiAccountId);
        });

        modelBuilder.Entity<GraphNode>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectId, e.NodeType, e.Key }).IsUnique();
            entity.HasIndex(e => e.NodeType);

            entity.HasOne(e => e.Project)
                .WithMany(p => p.Nodes)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(e => e.Properties)
                .HasColumnType("TEXT")
                .HasConversion(jsonDictConverter, jsonDictComparer);
        });

        modelBuilder.Entity<GraphEdge>(entity =>
        {
            entity.HasOne(e => e.FromNode)
                .WithMany(n => n.OutgoingEdges)
                .HasForeignKey(e => e.FromNodeId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.ToNode)
                .WithMany(n => n.IncomingEdges)
                .HasForeignKey(e => e.ToNodeId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.FromNodeId, e.EdgeType });
            entity.HasIndex(e => new { e.ToNodeId, e.EdgeType });

            entity.Property(e => e.Properties)
                .HasColumnType("TEXT")
                .HasConversion(jsonDictConverter, jsonDictComparer);
        });

        modelBuilder.Entity<GraphEntityType>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectId, e.Type }).IsUnique();
            entity.HasIndex(e => new { e.ProjectId, e.SortOrder });

            entity.HasOne(e => e.Project)
                .WithMany(p => p.EntityTypes)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(e => e.DefaultProperties)
                .HasColumnType("TEXT")
                .HasConversion(jsonDictConverter, jsonDictComparer);
        });

        modelBuilder.Entity<IngestSource>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectId, e.CreatedAt });
            entity.HasIndex(e => e.ActiveExtractionVersionId);
            entity.Property(e => e.VectorIndexState).HasConversion<string>();

            entity.HasOne(e => e.Project)
                .WithMany(p => p.IngestSources)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SourceOriginal>(entity =>
        {
            entity.HasKey(e => e.SourceId);
            entity.Property(e => e.State).HasConversion<string>();
            entity.HasIndex(e => e.Sha256);
            entity.HasOne(e => e.Source)
                .WithOne(source => source.Original)
                .HasForeignKey<SourceOriginal>(e => e.SourceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SourceOriginalBlob>(entity =>
        {
            entity.HasKey(e => e.Sha256);
            entity.Property(e => e.Data).HasColumnType("BLOB");
            entity.ToTable(table => table.HasCheckConstraint("CK_SourceOriginalBlobs_Length", "Length >= 0"));
        });

        modelBuilder.Entity<SourceOriginalChunk>(entity =>
        {
            entity.HasIndex(e => new { e.SourceId, e.Index }).IsUnique();
            entity.ToTable(table => table.HasCheckConstraint("CK_SourceOriginalChunks_ByteLength", $"ByteLength > 0 AND ByteLength <= {SourceOriginal.MaximumChunkBytes}"));
            entity.HasOne(e => e.SourceOriginal)
                .WithMany(original => original.Chunks)
                .HasForeignKey(e => e.SourceId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Blob)
                .WithMany(blob => blob.Chunks)
                .HasForeignKey(e => e.BlobSha256)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SourceExtractionVersion>(entity =>
        {
            entity.HasIndex(e => new { e.SourceId, e.Ordinal }).IsUnique();
            entity.Property(e => e.Status).HasConversion<string>();
            entity.HasOne(e => e.Source)
                .WithMany(source => source.ExtractionVersions)
                .HasForeignKey(e => e.SourceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BibliographicRecord>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectId, e.Title });
            entity.HasIndex(e => e.SourceId);
            entity.Property(e => e.Kind).HasConversion<string>();
            entity.HasOne(e => e.Project)
                .WithMany(project => project.BibliographicRecords)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Source)
                .WithMany(source => source.BibliographicRecords)
                .HasForeignKey(e => e.SourceId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<SourceLocation>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectId, e.SourceId, e.ExtractionVersionId });
            entity.HasIndex(e => e.SourceBlockId);
            entity.Property(e => e.ResolutionState).HasConversion<string>();
            entity.ToTable(table => table.HasCheckConstraint("CK_SourceLocations_NormalizedRange", "NormalizedStart >= 0 AND NormalizedLength >= 0"));
            entity.HasOne(e => e.Project)
                .WithMany(project => project.SourceLocations)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Source)
                .WithMany(source => source.Locations)
                .HasForeignKey(e => e.SourceId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.ExtractionVersion)
                .WithMany(extraction => extraction.Locations)
                .HasForeignKey(e => e.ExtractionVersionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.SourceBlock)
                .WithMany(block => block.Locations)
                .HasForeignKey(e => e.SourceBlockId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<IngestSourceChunk>(entity =>
        {
            entity.HasIndex(e => e.SourceId);
            entity.HasIndex(e => new { e.SourceExtractionVersionId, e.Index }).IsUnique();
            entity.Property(e => e.StructureStatus).HasConversion<string>();

            entity.HasOne(e => e.Source)
                .WithMany(s => s.SourceChunks)
                .HasForeignKey(e => e.SourceId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.SourceExtractionVersion)
                .WithMany(extraction => extraction.SourceChunks)
                .HasForeignKey(e => e.SourceExtractionVersionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<IngestSourcePage>(entity =>
        {
            entity.HasIndex(e => e.SourceId);
            entity.HasIndex(e => new { e.SourceExtractionVersionId, e.PageNumber }).IsUnique();
            entity.HasIndex(e => new { e.SourceExtractionVersionId, e.StartChar });

            entity.HasOne(e => e.Source)
                .WithMany(s => s.SourcePages)
                .HasForeignKey(e => e.SourceId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.SourceExtractionVersion)
                .WithMany(extraction => extraction.SourcePages)
                .HasForeignKey(e => e.SourceExtractionVersionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<IngestSourceBlock>(entity =>
        {
            entity.HasIndex(e => e.SourceId);
            entity.HasIndex(e => new { e.SourceExtractionVersionId, e.StartChar });
            entity.HasIndex(e => e.SourcePageId);
            entity.HasIndex(e => new { e.SourceExtractionVersionId, e.Index }).IsUnique();
            entity.ToTable(table => table.HasCheckConstraint("CK_IngestSourceBlocks_NormalizedText", $"length(NormalizedText) <= {IngestSourceBlock.MaximumNormalizedTextLength}"));

            entity.HasOne(e => e.Source)
                .WithMany(s => s.SourceBlocks)
                .HasForeignKey(e => e.SourceId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.SourceExtractionVersion)
                .WithMany(extraction => extraction.SourceBlocks)
                .HasForeignKey(e => e.SourceExtractionVersionId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.SourcePage)
                .WithMany(p => p.Blocks)
                .HasForeignKey(e => e.SourcePageId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<IngestVectorFragment>(entity =>
        {
            entity.HasIndex(e => new { e.SourceId, e.Index }).IsUnique();
            entity.HasIndex(e => e.VectorRowId);

            entity.HasOne(e => e.Source)
                .WithMany(s => s.VectorFragments)
                .HasForeignKey(e => e.SourceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<IngestJob>(entity =>
        {
            entity.Property(e => e.Mode).HasConversion<string>().HasDefaultValue(IngestJobMode.ExtractEntities);
            entity.HasIndex(e => new { e.ProjectId, e.Status, e.CreatedAt });
            entity.HasIndex(e => new { e.SourceId, e.CreatedAt });
            entity.HasIndex(e => e.ProviderId);
            entity.Property(e => e.Status).HasConversion<string>();

            entity.HasOne(e => e.Project)
                .WithMany(p => p.IngestJobs)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Source)
                .WithMany(s => s.Jobs)
                .HasForeignKey(e => e.SourceId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Provider)
                .WithMany()
                .HasForeignKey(e => e.ProviderId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<IngestJobChunk>(entity =>
        {
            entity.HasIndex(e => new { e.JobId, e.SourceChunkId }).IsUnique();
            entity.HasIndex(e => new { e.JobId, e.SourceChunkIndex });
            entity.Property(e => e.Status).HasConversion<string>();

            entity.HasOne(e => e.Job)
                .WithMany(j => j.Chunks)
                .HasForeignKey(e => e.JobId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.SourceChunk)
                .WithMany(c => c.JobChunks)
                .HasForeignKey(e => e.SourceChunkId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<IngestReportItem>(entity =>
        {
            entity.HasIndex(e => new { e.JobId, e.Kind, e.Status, e.CreatedAt });
            entity.HasIndex(e => e.GraphNodeId);
            entity.HasIndex(e => e.GraphEdgeId);
            entity.Property(e => e.Kind).HasConversion<string>();
            entity.Property(e => e.Status).HasConversion<string>();

            entity.HasOne(e => e.Job)
                .WithMany(j => j.ReportItems)
                .HasForeignKey(e => e.JobId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.SourceChunk)
                .WithMany(c => c.ReportItems)
                .HasForeignKey(e => e.SourceChunkId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<IngestStagingRecord>(entity =>
        {
            entity.HasIndex(e => new { e.JobId, e.Kind, e.Status, e.CreatedAt });
            entity.HasIndex(e => new { e.JobId, e.SourceChunkId, e.Kind, e.Status });
            entity.HasIndex(e => new { e.SourceId, e.Status });
            entity.HasIndex(e => e.EntityId);
            entity.HasIndex(e => e.GraphNodeId);
            entity.HasIndex(e => e.GraphEdgeId);
            entity.Property(e => e.Kind).HasConversion<string>();
            entity.Property(e => e.Status).HasConversion<string>();

            entity.HasOne(e => e.Job)
                .WithMany(j => j.StagingRecords)
                .HasForeignKey(e => e.JobId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Source)
                .WithMany(s => s.StagingRecords)
                .HasForeignKey(e => e.SourceId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.SourceChunk)
                .WithMany(c => c.StagingRecords)
                .HasForeignKey(e => e.SourceChunkId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<IngestJobEvent>(entity =>
        {
            entity.HasIndex(e => new { e.JobId, e.CreatedAt });
            entity.Property(e => e.Level).HasConversion<string>();

            entity.HasOne(e => e.Job)
                .WithMany(j => j.Events)
                .HasForeignKey(e => e.JobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WebIngestCandidate>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectId, e.Status, e.CreatedAt });
            entity.HasIndex(e => new { e.ResearchConversationId, e.CreatedAt });
            entity.HasIndex(e => e.IngestJobId);
            entity.Property(e => e.DiscoveryKind).HasConversion<string>();
            entity.Property(e => e.Status).HasConversion<string>();

            entity.HasOne(e => e.Project)
                .WithMany(p => p.WebIngestCandidates)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProjectImportJob>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectId, e.Status, e.CreatedAt });
            entity.Property(e => e.Status).HasConversion<string>();
            entity.Property(e => e.InputKind).HasConversion<string>();
            entity.Property(e => e.StagedFileKey).HasMaxLength(80);
            entity.Property(e => e.StagedSha256).HasMaxLength(64);

            entity.HasOne(e => e.Project)
                .WithMany(p => p.ProjectImportJobs)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProjectImportReportItem>(entity =>
        {
            entity.HasIndex(e => new { e.JobId, e.Kind, e.Status, e.CreatedAt });
            entity.HasIndex(e => e.GraphNodeId);
            entity.HasIndex(e => e.GraphEdgeId);
            entity.Property(e => e.Kind).HasConversion<string>();
            entity.Property(e => e.Status).HasConversion<string>();

            entity.HasOne(e => e.Job)
                .WithMany(j => j.ReportItems)
                .HasForeignKey(e => e.JobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PublicationEdition>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectId, e.Name }).IsUnique();
            entity.HasIndex(e => e.SelectedCoverImageId);
            entity.Property(e => e.Format).HasConversion<string>();
            entity.Property(e => e.Vendor).HasConversion<string>();
            entity.Property(e => e.Status).HasConversion<string>();
            entity.Property(e => e.PrintCoverMode).HasConversion<string>();
            entity.Property(e => e.TitlePageMode).HasConversion<string>();
            entity.Property(e => e.CitationStyle)
                .HasConversion<string>()
                .HasDefaultValue(CitationStyle.Chicago18NotesBibliography);
            entity.Property(e => e.Revision).IsConcurrencyToken();
            entity.Property(e => e.PublicationSectionOrderJson).HasDefaultValue("{}");

            entity.HasOne(e => e.Project)
                .WithMany(p => p.PublicationEditions)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.SelectedCoverImage)
                .WithMany(image => image.CoverEditions)
                .HasForeignKey(e => e.SelectedCoverImageId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<PublicationEditionChapterOverride>(entity =>
        {
            entity.HasIndex(e => new { e.EditionId, e.ChapterId }).IsUnique();
            entity.HasIndex(e => new { e.ChapterId, e.UpdatedAt });
            entity.Property(e => e.Revision).IsConcurrencyToken();
            entity.HasOne(e => e.Edition)
                .WithMany(e => e.ChapterOverrides)
                .HasForeignKey(e => e.EditionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Chapter)
                .WithMany(e => e.PublicationEditionChapterOverrides)
                .HasForeignKey(e => e.ChapterId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PublicationInteriorPagination>(entity =>
        {
            entity.HasKey(e => e.EditionId);
            entity.HasOne(e => e.Edition)
                .WithOne(e => e.InteriorPagination)
                .HasForeignKey<PublicationInteriorPagination>(e => e.EditionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PublicationBook>(entity =>
        {
            entity.HasKey(e => e.ProjectId);
            entity.Property(e => e.TitlePageMode).HasConversion<string>();
            entity.Property(e => e.CitationStyle)
                .HasConversion<string>()
                .HasDefaultValue(CitationStyle.Chicago18NotesBibliography);
            entity.Property(e => e.Revision).IsConcurrencyToken();
            entity.HasOne(e => e.Project)
                .WithOne(e => e.PublicationBook)
                .HasForeignKey<PublicationBook>(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PublicationBookPdfPresentation>(entity =>
        {
            entity.HasKey(e => e.ProjectId);
            entity.HasOne(e => e.Book).WithOne(e => e.PdfPresentation)
                .HasForeignKey<PublicationBookPdfPresentation>(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PublicationBookOutlineItem>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectId, e.TargetKind, e.TargetId }).IsUnique();
            entity.HasIndex(e => new { e.ProjectId, e.SortOrder });
            entity.Property(e => e.TargetKind).HasConversion<string>();
            entity.HasOne(e => e.Book).WithMany(e => e.OutlineItems)
                .HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Act).WithMany().HasForeignKey(e => e.ActId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Chapter).WithMany().HasForeignKey(e => e.ChapterId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PublicationBookMatter>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectId, e.Location, e.SortOrder });
            entity.Property(e => e.Location).HasConversion<string>();
            entity.Property(e => e.Kind).HasConversion<string>();
            entity.Property(e => e.Revision).IsConcurrencyToken();
            entity.HasOne(e => e.Book).WithMany(e => e.Matter)
                .HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PublicationBookImagePlacement>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectId, e.TargetKind, e.TargetId, e.PlacementKind, e.SortOrder });
            entity.HasIndex(e => e.AssetId);
            entity.Property(e => e.TargetKind).HasConversion<string>();
            entity.Property(e => e.PlacementKind).HasConversion<string>();
            entity.Property(e => e.AccessibilityRole).HasConversion<string>();
            entity.HasOne(e => e.Book).WithMany(e => e.ImagePlacements)
                .HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Asset).WithMany().HasForeignKey(e => e.AssetId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Act).WithMany().HasForeignKey(e => e.ActId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Chapter).WithMany().HasForeignKey(e => e.ChapterId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PublicationSection>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectId, e.EditionId, e.Anchor, e.TargetId, e.LocalOrder });
            entity.HasIndex(e => new { e.EditionId, e.CoreSectionId }).IsUnique();
            entity.HasIndex(e => new { e.ProjectId, e.SystemRole, e.EditionId }).IsUnique()
                .HasFilter("\"SystemRole\" <> 'None' AND \"IsExcluded\" = 0");
            entity.Property(e => e.Kind).HasConversion<string>();
            entity.Property(e => e.SystemRole).HasConversion<string>();
            entity.Property(e => e.Anchor).HasConversion<string>();
            entity.Property(e => e.TargetKind).HasConversion<string>();
            entity.Property(e => e.InclusionMode).HasConversion<string>();
            entity.Property(e => e.StartSide).HasConversion<string>();
            entity.Property(e => e.Revision).IsConcurrencyToken();
            entity.HasOne(e => e.Project).WithMany()
                .HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Edition).WithMany(e => e.PublicationSections)
                .HasForeignKey(e => e.EditionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.CoreSection).WithMany()
                .HasForeignKey(e => e.CoreSectionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Act).WithMany()
                .HasForeignKey(e => e.ActId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Chapter).WithMany()
                .HasForeignKey(e => e.ChapterId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PublicationBookCoverDesign>(entity =>
        {
            entity.HasIndex(e => e.ProjectId).IsUnique();
            entity.Property(e => e.Revision).IsConcurrencyToken();
            entity.HasOne(e => e.Book).WithOne(e => e.CoverDesign)
                .HasForeignKey<PublicationBookCoverDesign>(e => e.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PublishAsset>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectId, e.CreatedAt });
            entity.HasIndex(e => e.DerivedFromImageId);
            entity.HasIndex(e => new
            {
                e.DerivedFromImageId,
                e.CropXPercent,
                e.CropYPercent,
                e.CropWidthPercent,
                e.CropHeightPercent,
            }).IsUnique();
            entity.Property(e => e.Source).HasConversion<string>();

            entity.HasOne(e => e.Project)
                .WithMany(p => p.PublishAssets)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.DerivedFromImage)
                .WithMany(e => e.DerivedImages)
                .HasForeignKey(e => e.DerivedFromImageId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ProjectFontFamily>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectId, e.Name }).IsUnique();
            entity.HasOne(e => e.Project)
                .WithMany(p => p.FontFamilies)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProjectFontFace>(entity =>
        {
            entity.HasIndex(e => new { e.FamilyId, e.Weight, e.Italic }).IsUnique();
            entity.HasOne(e => e.Family)
                .WithMany(f => f.Faces)
                .HasForeignKey(e => e.FamilyId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ManuscriptStyleDefinition>(entity =>
        {
            entity.HasIndex(style => new { style.ProjectId, style.Kind, style.NameKey }).IsUnique();
            entity.HasIndex(style => new { style.ProjectId, style.Kind, style.SemanticRoleKey }).IsUnique();
            entity.Property(style => style.Kind).HasConversion<string>();
            entity.Property(style => style.Revision).IsConcurrencyToken();
            entity.HasOne(style => style.Project)
                .WithMany(project => project.ManuscriptStyles)
                .HasForeignKey(style => style.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SourceVisualCandidate>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectId, e.Kind, e.Status, e.CreatedAt });
            entity.HasIndex(e => e.IngestSourceId);
            entity.HasIndex(e => e.WebIngestCandidateId);
            entity.HasIndex(e => e.PromotedImageId);
            entity.HasIndex(e => new { e.ProjectId, e.ContentHash });
            entity.Property(e => e.Kind).HasConversion<string>();
            entity.Property(e => e.Status).HasConversion<string>();

            entity.HasOne(e => e.Project)
                .WithMany(p => p.SourceVisualCandidates)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.IngestSource)
                .WithMany(s => s.VisualCandidates)
                .HasForeignKey(e => e.IngestSourceId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.WebIngestCandidate)
                .WithMany(s => s.VisualCandidates)
                .HasForeignKey(e => e.WebIngestCandidateId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.PromotedImage)
                .WithMany(a => a.SourceVisualCandidates)
                .HasForeignKey(e => e.PromotedImageId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<EntityVisualExample>(entity =>
        {
            entity.HasIndex(e => new { e.GraphNodeId, e.ImageId }).IsUnique();
            entity.HasIndex(e => new { e.ProjectId, e.GraphNodeId, e.SortOrder });
            entity.HasIndex(e => e.ImageId);
            entity.HasIndex(e => e.SourceVisualCandidateId);
            entity.Property(e => e.Origin).HasConversion<string>();

            entity.HasOne(e => e.Project)
                .WithMany(p => p.EntityVisualExamples)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.GraphNode)
                .WithMany(n => n.VisualExamples)
                .HasForeignKey(e => e.GraphNodeId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Image)
                .WithMany(a => a.EntityVisualExamples)
                .HasForeignKey(e => e.ImageId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.SourceVisualCandidate)
                .WithMany(c => c.EntityVisualExamples)
                .HasForeignKey(e => e.SourceVisualCandidateId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ProjectImageGenerationJob>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectId, e.Status, e.CreatedAt });
            entity.HasIndex(e => new { e.ProjectId, e.Kind, e.CreatedAt });
            entity.HasIndex(e => e.SourceImageId);
            entity.HasIndex(e => e.MaskId);
            entity.Property(e => e.Kind).HasConversion<string>();
            entity.Property(e => e.Status).HasConversion<string>();
            entity.Property(e => e.Background).HasDefaultValue("auto");

            entity.HasOne(e => e.Project)
                .WithMany(p => p.ProjectImageGenerationJobs)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProjectImagePartial>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectId, e.CreatedAt });
            entity.HasIndex(e => new { e.JobId, e.OutputIndex, e.Attempt, e.PartialImageIndex }).IsUnique();
            entity.HasIndex(e => new { e.ProjectId, e.JobId, e.OutputIndex, e.Attempt });
            entity.HasIndex(e => e.FinalOutputImageId);

            entity.HasOne(e => e.Project)
                .WithMany(p => p.ProjectImagePartials)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Job)
                .WithMany(job => job.Partials)
                .HasForeignKey(e => e.JobId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.FinalOutputImage)
                .WithMany()
                .HasForeignKey(e => e.FinalOutputImageId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProjectImageMask>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectId, e.ImageId, e.CreatedAt });
            entity.HasIndex(e => new { e.ProjectId, e.OwnerKind, e.OwnerId });

            entity.HasOne(e => e.Project)
                .WithMany(p => p.ProjectImageMasks)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Image)
                .WithMany(a => a.ImageMasks)
                .HasForeignKey(e => e.ImageId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PublicationEditionOutlineItem>(entity =>
        {
            entity.HasIndex(e => new { e.EditionId, e.TargetKind, e.TargetId }).IsUnique();
            entity.HasIndex(e => new { e.EditionId, e.SortOrder });
            entity.Property(e => e.TargetKind).HasConversion<string>();

            entity.HasOne(e => e.Edition)
                .WithMany(e => e.OutlineItems)
                .HasForeignKey(e => e.EditionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Act)
                .WithMany()
                .HasForeignKey(e => e.ActId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Chapter)
                .WithMany()
                .HasForeignKey(e => e.ChapterId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PublicationImagePlacement>(entity =>
        {
            entity.HasIndex(e => new { e.EditionId, e.TargetKind, e.TargetId, e.PlacementKind, e.SortOrder });
            entity.HasIndex(e => e.AssetId);
            entity.HasIndex(e => new { e.EditionId, e.CorePlacementId }).IsUnique();
            entity.Property(e => e.TargetKind).HasConversion<string>();
            entity.Property(e => e.PlacementKind).HasConversion<string>();
            entity.Property(e => e.AccessibilityRole).HasConversion<string>();

            entity.HasOne(e => e.Edition)
                .WithMany(e => e.ImagePlacements)
                .HasForeignKey(e => e.EditionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.CorePlacement)
                .WithMany()
                .HasForeignKey(e => e.CorePlacementId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Asset)
                .WithMany(a => a.PublicationPlacements)
                .HasForeignKey(e => e.AssetId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Act)
                .WithMany()
                .HasForeignKey(e => e.ActId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Chapter)
                .WithMany()
                .HasForeignKey(e => e.ChapterId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PublicationMatter>(entity =>
        {
            entity.HasIndex(e => new { e.EditionId, e.Location, e.SortOrder });
            entity.Property(e => e.Location).HasConversion<string>();
            entity.Property(e => e.Kind).HasConversion<string>();
            entity.Property(e => e.Revision).IsConcurrencyToken();
            entity.HasIndex(e => new { e.EditionId, e.CoreMatterId }).IsUnique();
            entity.HasOne(e => e.Edition)
                .WithMany(e => e.Matter)
                .HasForeignKey(e => e.EditionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.CoreMatter)
                .WithMany()
                .HasForeignKey(e => e.CoreMatterId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PublicationEditionAuditEntry>(entity =>
        {
            entity.HasIndex(e => new { e.EditionId, e.CreatedAt });
            entity.HasOne(e => e.Edition)
                .WithMany(e => e.AuditEntries)
                .HasForeignKey(e => e.EditionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PublicationRenderJob>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectId, e.TargetKind, e.EditionId, e.CreatedAt });
            entity.HasIndex(e => new { e.Status, e.CreatedAt });
            entity.Property(e => e.Status).HasConversion<string>();
            entity.Property(e => e.Scope).HasConversion<string>();
            entity.Property(e => e.TargetKind).HasConversion<string>();
            entity.HasOne(e => e.Project)
                .WithMany(e => e.PublicationRenderJobs)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Edition)
                .WithMany(e => e.RenderJobs)
                .HasForeignKey(e => e.EditionId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<PublicationCoverDesign>(entity =>
        {
            entity.HasIndex(e => e.EditionId).IsUnique();
            entity.Property(e => e.BarcodeMode).HasConversion<string>();
            entity.Property(e => e.Revision).IsConcurrencyToken();
            entity.HasOne(e => e.Edition)
                .WithOne(e => e.CoverDesign)
                .HasForeignKey<PublicationCoverDesign>(e => e.EditionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PublicationArtifact>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectId, e.TargetKind, e.EditionId, e.Kind, e.CreatedAt });
            entity.HasIndex(e => new { e.RenderJobId, e.Kind }).IsUnique();
            entity.Property(e => e.Kind).HasConversion<string>();
            entity.Property(e => e.TargetKind).HasConversion<string>();
            entity.HasOne(e => e.Project)
                .WithMany(e => e.PublicationArtifacts)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Edition)
                .WithMany(e => e.Artifacts)
                .HasForeignKey(e => e.EditionId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.RenderJob)
                .WithMany(e => e.Artifacts)
                .HasForeignKey(e => e.RenderJobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PublicationPreparationJob>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectId, e.TargetKind, e.EditionId, e.CreatedAt });
            entity.HasIndex(e => new { e.Status, e.CreatedAt });
            entity.Property(e => e.TargetKind).HasConversion<string>();
            entity.Property(e => e.Status).HasConversion<string>();
            entity.HasOne(e => e.Project).WithMany(e => e.PublicationPreparationJobs)
                .HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Edition).WithMany()
                .HasForeignKey(e => e.EditionId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.BookRenderJob).WithMany()
                .HasForeignKey(e => e.BookRenderJobId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.InteriorRenderJob).WithMany()
                .HasForeignKey(e => e.InteriorRenderJobId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.CoverRenderJob).WithMany()
                .HasForeignKey(e => e.CoverRenderJobId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<PublicationPageMapEntry>(entity =>
        {
            entity.HasIndex(e => new { e.RenderJobId, e.ChapterId, e.BlockId }).IsUnique();
            entity.HasIndex(e => new { e.RenderJobId, e.PageNumber });
            entity.HasOne(e => e.RenderJob)
                .WithMany(e => e.PageMapEntries)
                .HasForeignKey(e => e.RenderJobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PublicationEditionMigrationJournal>(entity =>
        {
            entity.HasIndex(e => new { e.MigrationName, e.StartedAt });
        });
    }

    private void NormalizePublicationTargetOwnership()
    {
        var releases = PublicationEditions.Local.ToDictionary(item => item.Id, item => item.ProjectId);
        var renderJobs = PublicationRenderJobs.Local.ToDictionary(item => item.Id);
        foreach (var entry in ChangeTracker.Entries<PublicationRenderJob>().Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            if (entry.Entity.ProjectId != Guid.Empty || entry.Entity.EditionId is not Guid releaseId) continue;
            if (entry.Entity.Edition?.ProjectId is Guid projectId && projectId != Guid.Empty) entry.Entity.ProjectId = projectId;
            else if (releases.TryGetValue(releaseId, out projectId)) entry.Entity.ProjectId = projectId;
            entry.Entity.TargetKind = PublicationTargetKind.Release;
        }
        foreach (var entry in ChangeTracker.Entries<PublicationArtifact>().Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            if (entry.Entity.ProjectId != Guid.Empty) continue;
            if (entry.Entity.Edition?.ProjectId is Guid projectId && projectId != Guid.Empty) entry.Entity.ProjectId = projectId;
            else if (entry.Entity.EditionId is Guid releaseId && releases.TryGetValue(releaseId, out projectId)) entry.Entity.ProjectId = projectId;
            else if (entry.Entity.RenderJobId is Guid renderId && renderJobs.TryGetValue(renderId, out var render)) entry.Entity.ProjectId = render.ProjectId;
            if (entry.Entity.EditionId is not null) entry.Entity.TargetKind = PublicationTargetKind.Release;
        }
    }
}
