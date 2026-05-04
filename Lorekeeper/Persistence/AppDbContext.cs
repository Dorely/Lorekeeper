using System.Text.Json;
using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Lorekeeper.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<LlmProvider> LlmProviders => Set<LlmProvider>();
    public DbSet<OAuthToken> OAuthTokens => Set<OAuthToken>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Act> Acts => Set<Act>();
    public DbSet<Chapter> Chapters => Set<Chapter>();
    public DbSet<GraphNode> GraphNodes => Set<GraphNode>();
    public DbSet<GraphEdge> GraphEdges => Set<GraphEdge>();
    public DbSet<GraphEntityType> GraphEntityTypes => Set<GraphEntityType>();
    public DbSet<AiConsoleEntry> AiConsoleEntries => Set<AiConsoleEntry>();
    public DbSet<OutlineConversation> OutlineConversations => Set<OutlineConversation>();
    public DbSet<OutlineMessage> OutlineMessages => Set<OutlineMessage>();

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

            entity.Property(e => e.Metadata)
                .HasColumnType("TEXT")
                .HasConversion(jsonDictConverter, jsonDictComparer);
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

        modelBuilder.Entity<AiConsoleEntry>(entity =>
        {
            entity.HasIndex(e => new { e.ProjectId, e.StartedAt });
            entity.Property(e => e.Status).HasConversion<string>();

            entity.HasOne(e => e.Project)
                .WithMany(p => p.AiConsoleEntries)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
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

        modelBuilder.Entity<LlmProvider>(entity =>
        {
            entity.HasIndex(e => e.Name).IsUnique();
            entity.Property(e => e.AuthType).HasConversion<string>();

            entity.HasOne(e => e.CredentialSource)
                .WithMany(e => e.ChildModels)
                .HasForeignKey(e => e.CredentialSourceId)
                .OnDelete(DeleteBehavior.SetNull);
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
    }
}
