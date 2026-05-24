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
    public DbSet<SearchProvider> SearchProviders => Set<SearchProvider>();
    public DbSet<OAuthToken> OAuthTokens => Set<OAuthToken>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Act> Acts => Set<Act>();
    public DbSet<Chapter> Chapters => Set<Chapter>();
    public DbSet<GraphNode> GraphNodes => Set<GraphNode>();
    public DbSet<GraphEdge> GraphEdges => Set<GraphEdge>();
    public DbSet<GraphEntityType> GraphEntityTypes => Set<GraphEntityType>();
    public DbSet<OutlineConversation> OutlineConversations => Set<OutlineConversation>();
    public DbSet<OutlineMessage> OutlineMessages => Set<OutlineMessage>();
    public DbSet<EditorConversation> EditorConversations => Set<EditorConversation>();
    public DbSet<EditorMessage> EditorMessages => Set<EditorMessage>();
    public DbSet<WritingSample> WritingSamples => Set<WritingSample>();
    public DbSet<WritingCoachConversation> WritingCoachConversations => Set<WritingCoachConversation>();
    public DbSet<WritingCoachMessage> WritingCoachMessages => Set<WritingCoachMessage>();
    public DbSet<ResearchConversation> ResearchConversations => Set<ResearchConversation>();
    public DbSet<ResearchMessage> ResearchMessages => Set<ResearchMessage>();
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
    public DbSet<IngestVectorFragment> IngestVectorFragments => Set<IngestVectorFragment>();
    public DbSet<IngestJob> IngestJobs => Set<IngestJob>();
    public DbSet<IngestJobChunk> IngestJobChunks => Set<IngestJobChunk>();
    public DbSet<IngestReportItem> IngestReportItems => Set<IngestReportItem>();
    public DbSet<IngestJobEvent> IngestJobEvents => Set<IngestJobEvent>();
    public DbSet<WebIngestCandidate> WebIngestCandidates => Set<WebIngestCandidate>();
    public DbSet<ProjectImportJob> ProjectImportJobs => Set<ProjectImportJob>();
    public DbSet<ProjectImportReportItem> ProjectImportReportItems => Set<ProjectImportReportItem>();
    public DbSet<PublishProfile> PublishProfiles => Set<PublishProfile>();
    public DbSet<PublishAsset> PublishAssets => Set<PublishAsset>();
    public DbSet<PublishOutlineSelection> PublishOutlineSelections => Set<PublishOutlineSelection>();
    public DbSet<PublishImagePlacement> PublishImagePlacements => Set<PublishImagePlacement>();

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

        modelBuilder.Entity<Project>(entity =>
        {
            entity.HasIndex(e => e.Slug).IsUnique();
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

            entity.HasOne(e => e.CredentialSource)
                .WithMany(e => e.ChildModels)
                .HasForeignKey(e => e.CredentialSourceId)
                .OnDelete(DeleteBehavior.SetNull);
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

        modelBuilder.Entity<PublishProfile>(entity =>
        {
            entity.HasIndex(e => e.ProjectId).IsUnique();
            entity.HasIndex(e => e.SelectedCoverAssetId);

            entity.HasOne(e => e.Project)
                .WithMany(p => p.PublishProfiles)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.SelectedCoverAsset)
                .WithMany(a => a.CoverProfiles)
                .HasForeignKey(e => e.SelectedCoverAssetId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<PublishAsset>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectId, e.CreatedAt });
            entity.Property(e => e.Source).HasConversion<string>();

            entity.HasOne(e => e.Project)
                .WithMany(p => p.PublishAssets)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PublishOutlineSelection>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectId, e.TargetKind, e.TargetId }).IsUnique();
            entity.Property(e => e.TargetKind).HasConversion<string>();

            entity.HasOne(e => e.Project)
                .WithMany(p => p.PublishOutlineSelections)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PublishImagePlacement>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectId, e.TargetKind, e.TargetId, e.PlacementKind, e.SortOrder });
            entity.HasIndex(e => e.AssetId);
            entity.Property(e => e.TargetKind).HasConversion<string>();
            entity.Property(e => e.PlacementKind).HasConversion<string>();

            entity.HasOne(e => e.Project)
                .WithMany(p => p.PublishImagePlacements)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Asset)
                .WithMany(a => a.ImagePlacements)
                .HasForeignKey(e => e.AssetId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
