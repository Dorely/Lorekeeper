using System.Text.Json;
using Lorekeeper.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Lorekeeper.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options, ILogger<AppDbContext> logger) : DbContext(options)
{
    private const int _maxLockedSaveAttempts = 6;

    public DbSet<LlmProvider> LlmProviders => Set<LlmProvider>();
    public DbSet<EmbeddingConfiguration> EmbeddingConfigurations => Set<EmbeddingConfiguration>();
    public DbSet<SearchProvider> SearchProviders => Set<SearchProvider>();
    public DbSet<OAuthToken> OAuthTokens => Set<OAuthToken>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<BookBrief> BookBriefs => Set<BookBrief>();
    public DbSet<Act> Acts => Set<Act>();
    public DbSet<Chapter> Chapters => Set<Chapter>();
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
    public DbSet<WritingCoachConversation> WritingCoachConversations => Set<WritingCoachConversation>();
    public DbSet<WritingCoachMessage> WritingCoachMessages => Set<WritingCoachMessage>();
    public DbSet<ResearchConversation> ResearchConversations => Set<ResearchConversation>();
    public DbSet<ResearchMessage> ResearchMessages => Set<ResearchMessage>();
    public DbSet<ProjectImageConversation> ProjectImageConversations => Set<ProjectImageConversation>();
    public DbSet<ProjectImageMessage> ProjectImageMessages => Set<ProjectImageMessage>();
    public DbSet<ProjectImageMessageVisual> ProjectImageMessageVisuals => Set<ProjectImageMessageVisual>();
    public DbSet<ChatMessageImageAttachment> ChatMessageImageAttachments => Set<ChatMessageImageAttachment>();
    public DbSet<ProjectImageChatAttachment> ProjectImageChatAttachments => Set<ProjectImageChatAttachment>();
    public DbSet<AiChangeBatch> AiChangeBatches => Set<AiChangeBatch>();
    public DbSet<AiChange> AiChanges => Set<AiChange>();
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
    public DbSet<WebIngestCandidate> WebIngestCandidates => Set<WebIngestCandidate>();
    public DbSet<ProjectImportJob> ProjectImportJobs => Set<ProjectImportJob>();
    public DbSet<ProjectImportReportItem> ProjectImportReportItems => Set<ProjectImportReportItem>();
    public DbSet<PublicationEdition> PublicationEditions => Set<PublicationEdition>();
    public DbSet<PublicationEditionOutlineItem> PublicationEditionOutlineItems => Set<PublicationEditionOutlineItem>();
    public DbSet<PublicationMatter> PublicationMatter => Set<PublicationMatter>();
    public DbSet<PublicationEditionStyleMapping> PublicationEditionStyleMappings => Set<PublicationEditionStyleMapping>();
    public DbSet<PublicationImagePlacement> PublicationImagePlacements => Set<PublicationImagePlacement>();
    public DbSet<PublicationEditionAuditEntry> PublicationEditionAuditEntries => Set<PublicationEditionAuditEntry>();
    public DbSet<PublicationEditionMigrationJournal> PublicationEditionMigrationJournals => Set<PublicationEditionMigrationJournal>();
    public DbSet<PublicationRenderJob> PublicationRenderJobs => Set<PublicationRenderJob>();
    public DbSet<PublicationArtifact> PublicationArtifacts => Set<PublicationArtifact>();
    public DbSet<PublicationPageMapEntry> PublicationPageMapEntries => Set<PublicationPageMapEntry>();
    public DbSet<PublishAsset> PublishAssets => Set<PublishAsset>();
    public DbSet<ProjectImageGenerationJob> ProjectImageGenerationJobs => Set<ProjectImageGenerationJob>();
    public DbSet<ProjectImageMask> ProjectImageMasks => Set<ProjectImageMask>();
    public DbSet<EntityVisualExample> EntityVisualExamples => Set<EntityVisualExample>();
    public DbSet<SourceVisualCandidate> SourceVisualCandidates => Set<SourceVisualCandidate>();
    public DbSet<ProjectFontFamily> ProjectFontFamilies => Set<ProjectFontFamily>();
    public DbSet<ProjectFontFace> ProjectFontFaces => Set<ProjectFontFace>();
    public DbSet<ManuscriptStyleDefinition> ManuscriptStyleDefinitions => Set<ManuscriptStyleDefinition>();

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        SaveChangesWithLockRetryAsync(acceptAllChangesOnSuccess: true, cancellationToken);

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default) =>
        SaveChangesWithLockRetryAsync(acceptAllChangesOnSuccess, cancellationToken);

    private async Task<int> SaveChangesWithLockRetryAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMilliseconds(100);
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

        modelBuilder.Entity<BookBrief>(entity =>
        {
            entity.HasIndex(e => e.ProjectId).IsUnique();
            entity.Property(e => e.BookKind).HasConversion<string>();

            entity.HasOne(e => e.Project)
                .WithOne(p => p.BookBrief)
                .HasForeignKey<BookBrief>(e => e.ProjectId)
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
            entity.Property(e => e.VisualMode).HasConversion<string>();
            entity.Property(e => e.PageLayoutKind).HasConversion<string>();
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

        modelBuilder.Entity<OutlineConversation>(entity =>
        {
            entity.HasIndex(e => e.ProjectId).IsUnique();

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

        modelBuilder.Entity<WritingCoachConversation>(entity =>
        {
            entity.HasIndex(e => e.ProjectId).IsUnique();

            entity.HasOne(e => e.Project)
                .WithMany(p => p.WritingCoachConversations)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WritingCoachMessage>(entity =>
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

        modelBuilder.Entity<ProjectImageConversation>(entity =>
        {
            entity.HasIndex(e => e.ProjectId).IsUnique();

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

        modelBuilder.Entity<AiChangeBatch>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectId, e.Status, e.CreatedAt });
            entity.Property(e => e.ConversationKind).HasConversion<string>();
            entity.Property(e => e.Status).HasConversion<string>();

            entity.HasOne(e => e.Project)
                .WithMany(p => p.AiChangeBatches)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AiChange>(entity =>
        {
            entity.HasIndex(e => new { e.BatchId, e.Order });
            entity.HasIndex(e => e.Status);
            entity.Property(e => e.Status).HasConversion<string>();

            entity.HasOne(e => e.Batch)
                .WithMany(b => b.Changes)
                .HasForeignKey(e => e.BatchId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ContestBatch>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectId, e.Status, e.CreatedAt });
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
            entity.Property(e => e.LastChatTestAuthType).HasConversion<string>();
            entity.Property(e => e.LastChatTestReasoningEffort).HasConversion<string>();
            entity.Property(e => e.LastVisionTestAuthType).HasConversion<string>();
            entity.Property(e => e.LastVisionTestReasoningEffort).HasConversion<string>();

            entity.HasOne(e => e.CredentialSource)
                .WithMany(e => e.ChildModels)
                .HasForeignKey(e => e.CredentialSourceId)
                .OnDelete(DeleteBehavior.SetNull);
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
            entity.HasOne(e => e.Provider)
                .WithMany(p => p.OAuthTokens)
                .HasForeignKey(e => e.ProviderId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.ProviderId);
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
            entity.Property(e => e.VectorIndexState).HasConversion<string>();

            entity.HasOne(e => e.Project)
                .WithMany(p => p.IngestSources)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<IngestSourceChunk>(entity =>
        {
            entity.HasIndex(e => new { e.SourceId, e.Index }).IsUnique();
            entity.Property(e => e.StructureStatus).HasConversion<string>();

            entity.HasOne(e => e.Source)
                .WithMany(s => s.SourceChunks)
                .HasForeignKey(e => e.SourceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<IngestSourcePage>(entity =>
        {
            entity.HasIndex(e => new { e.SourceId, e.PageNumber }).IsUnique();
            entity.HasIndex(e => new { e.SourceId, e.StartChar });

            entity.HasOne(e => e.Source)
                .WithMany(s => s.SourcePages)
                .HasForeignKey(e => e.SourceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<IngestSourceBlock>(entity =>
        {
            entity.HasIndex(e => new { e.SourceId, e.Index }).IsUnique();
            entity.HasIndex(e => new { e.SourceId, e.StartChar });
            entity.HasIndex(e => e.SourcePageId);

            entity.HasOne(e => e.Source)
                .WithMany(s => s.SourceBlocks)
                .HasForeignKey(e => e.SourceId)
                .OnDelete(DeleteBehavior.Cascade);

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
            entity.HasIndex(e => new { e.ProjectId, e.IsDefault })
                .IsUnique()
                .HasFilter("\"IsDefault\" = 1");
            entity.HasIndex(e => e.SelectedCoverChapterId);
            entity.Property(e => e.Format).HasConversion<string>();
            entity.Property(e => e.Vendor).HasConversion<string>();
            entity.Property(e => e.Status).HasConversion<string>();
            entity.Property(e => e.Binding).HasConversion<string>();
            entity.Property(e => e.Paper).HasConversion<string>();
            entity.Property(e => e.Ink).HasConversion<string>();
            entity.Property(e => e.TitlePageMode).HasConversion<string>();
            entity.Property(e => e.PrintPicturePageSpreadMode).HasConversion<string>();
            entity.Property(e => e.EpubPicturePageSpreadMode).HasConversion<string>();
            entity.Property(e => e.Revision).IsConcurrencyToken();

            entity.HasOne(e => e.Project)
                .WithMany(p => p.PublicationEditions)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.SelectedCoverChapter)
                .WithMany(chapter => chapter.CoverEditions)
                .HasForeignKey(e => e.SelectedCoverChapterId)
                .OnDelete(DeleteBehavior.SetNull);
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

            entity.HasOne(e => e.Project)
                .WithMany(p => p.ProjectImageGenerationJobs)
                .HasForeignKey(e => e.ProjectId)
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
            entity.Property(e => e.TargetKind).HasConversion<string>();
            entity.Property(e => e.PlacementKind).HasConversion<string>();

            entity.HasOne(e => e.Edition)
                .WithMany(e => e.ImagePlacements)
                .HasForeignKey(e => e.EditionId)
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
            entity.HasOne(e => e.Edition)
                .WithMany(e => e.Matter)
                .HasForeignKey(e => e.EditionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PublicationEditionStyleMapping>(entity =>
        {
            entity.HasIndex(e => new { e.EditionId, e.ManuscriptStyleDefinitionId }).IsUnique();
            entity.HasIndex(e => new { e.EditionId, e.SemanticRole }).IsUnique();
            entity.Property(e => e.Revision).IsConcurrencyToken();
            entity.HasOne(e => e.Edition)
                .WithMany(e => e.StyleMappings)
                .HasForeignKey(e => e.EditionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.ManuscriptStyleDefinition)
                .WithMany()
                .HasForeignKey(e => e.ManuscriptStyleDefinitionId)
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
            entity.HasIndex(e => new { e.EditionId, e.CreatedAt });
            entity.HasIndex(e => new { e.Status, e.CreatedAt });
            entity.Property(e => e.Status).HasConversion<string>();
            entity.HasOne(e => e.Edition)
                .WithMany(e => e.RenderJobs)
                .HasForeignKey(e => e.EditionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PublicationArtifact>(entity =>
        {
            entity.HasIndex(e => new { e.EditionId, e.Kind, e.CreatedAt });
            entity.HasIndex(e => new { e.RenderJobId, e.Kind }).IsUnique();
            entity.Property(e => e.Kind).HasConversion<string>();
            entity.HasOne(e => e.Edition)
                .WithMany(e => e.Artifacts)
                .HasForeignKey(e => e.EditionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.RenderJob)
                .WithMany(e => e.Artifacts)
                .HasForeignKey(e => e.RenderJobId)
                .OnDelete(DeleteBehavior.Cascade);
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
}
