using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.ExternalAccess;

public sealed class ExternalAccessSettings
{
    public int Id { get; set; } = 1;
    public bool Enabled { get; set; }
}

// Connection secrets belong only to this installation-level security record.
// None of the external-access records participate in creative capture/export.
public sealed class McpClientGrant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string DisplayName { get; set; }
    public string ClientKind { get; set; } = "Other MCP client";
    public bool Enabled { get; set; } = true;
    public required string CredentialHash { get; set; }
    public required string ProtectedCredential { get; set; }
    public required string PermissionsJson { get; set; }
    public string? ProjectIdsJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
}

public sealed class ExternalWork
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientGrantId { get; set; }
    public Guid ProjectId { get; set; }
    public required string Task { get; set; }
    public string Status { get; set; } = "Active";
    public bool HasMutations { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class ExternalWorkspace
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkId { get; set; }
    public required string Surface { get; set; }
    public Guid? ChapterId { get; set; }
    public Guid? EditionId { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class ExternalMutationReceipt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientGrantId { get; set; }
    public Guid? WorkId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid MutationId { get; set; }
    public required string Operation { get; set; }
    public required string RequestHash { get; set; }
    public required string ResultJson { get; set; }
    public bool RepairPending { get; set; } = true;
    public string RepairTargetsJson { get; set; } = "[]";
    public DateTime? RepairAttemptedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class ExternalProjectLink
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public required string Name { get; set; }
    public required string ExternalKey { get; set; }
    public Guid? BaselineCaptureId { get; set; }
    public long Revision { get; set; }
}

public sealed class ExternalSourceIdentity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LinkId { get; set; }
    public required string ExternalKey { get; set; }
    public required string TargetKind { get; set; }
    public required string TargetId { get; set; }
}

public sealed class ExternalTransferCapture
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LinkId { get; set; }
    public Guid? PreviousBaselineId { get; set; }
    public long ExpectedLinkRevision { get; set; }
    public required string ManifestJson { get; set; }
    public string IdentitiesJson { get; set; } = "[]";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? AcknowledgedAt { get; set; }
}

public sealed class ExternalAssetTransfer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientGrantId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid WorkspaceId { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public string? Sha256 { get; set; }
    public long Length { get; set; }
    public Guid? ImportedImageId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

internal static class ExternalAccessModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<ExternalAccessSettings>();
        model.Entity<McpClientGrant>().HasIndex(value => value.CredentialHash).IsUnique();
        model.Entity<McpClientGrant>().Property(value => value.ClientKind).HasDefaultValue("Other MCP client");
        model.Entity<ExternalWork>().HasOne<McpClientGrant>().WithMany()
            .HasForeignKey(value => value.ClientGrantId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ExternalWork>().HasOne<Project>().WithMany()
            .HasForeignKey(value => value.ProjectId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<ExternalWorkspace>().HasOne<ExternalWork>().WithMany()
            .HasForeignKey(value => value.WorkId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<ExternalMutationReceipt>().HasIndex(value => new { value.ClientGrantId, value.MutationId }).IsUnique();
        model.Entity<ExternalProjectLink>().HasIndex(value => new { value.ProjectId, value.ExternalKey }).IsUnique();
        model.Entity<ExternalProjectLink>().HasOne<Project>().WithMany()
            .HasForeignKey(value => value.ProjectId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<ExternalSourceIdentity>().HasIndex(value => new { value.LinkId, value.ExternalKey }).IsUnique();
        model.Entity<ExternalSourceIdentity>().HasOne<ExternalProjectLink>().WithMany()
            .HasForeignKey(value => value.LinkId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<ExternalTransferCapture>().HasOne<ExternalProjectLink>().WithMany()
            .HasForeignKey(value => value.LinkId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<ExternalTransferCapture>().Property(value => value.IdentitiesJson).HasDefaultValue("[]");
        model.Entity<ExternalAssetTransfer>();
    }
}
